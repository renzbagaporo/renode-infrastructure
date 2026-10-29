//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SPI
{
    public sealed class RP2040_SSI : SimpleContainer<ISPIPeripheral>, IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize, IGPIOReceiver
    {
        public RP2040_SSI(IMachine machine, ulong frequency = 125000000) : base(machine)
        {
            transmitFifo = new Queue<uint>();
            receiveFifo = new Queue<uint>();
            registers = new uint[64];
            transferTimer = new LimitTimer(machine.ClockSource, frequency, this, "serial clock",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            transferTimer.LimitReached += CompleteFrame;
            Reset();
        }

        public override void Register(ISPIPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            if(registrationPoint.Address != 0) throw new RegistrationException("RP2040 SSI has one chip select, at address 0");
            base.Register(peripheral, registrationPoint);
        }

        public override void Reset()
        {
            EndTransaction();
            transferTimer.Reset();
            transmitFifo.Clear();
            receiveFifo.Clear();
            Array.Clear(registers, 0, registers.Length);
            registers[0] = 0x001f0000;
            registers[0x2c / 4] = 0x3f;
            chipSelectOverride = 0;
            transactionActive = false;
            receiveRemaining = 0;
            headerFramesRemaining = 0;
            errors = 0;
            dummyWait = false;
            UpdateInterrupt();
            ConfigurationChanged?.Invoke();
        }

        public byte ReadByte(long offset) => (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));

        public ushort ReadWord(long offset) => (ushort)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 2) * 8));

        public void WriteByte(long offset, byte value) => WriteDoubleWord(offset & ~3L, (uint)value * 0x01010101u);

        public void WriteWord(long offset, ushort value) => WriteDoubleWord(offset & ~3L, (uint)value | ((uint)value << 16));

        public uint ReadDoubleWord(long offset)
        {
            SynchronizeTime();
            offset &= 0xfff;
            if(IsDataRegister(offset))
            {
                if(receiveFifo.Count == 0)
                {
                    errors |= 4;
                    UpdateInterrupt();
                    return 0;
                }
                var value = receiveFifo.Dequeue();
                TryStartFrame();
                UpdateInterrupt();
                return value;
            }
            switch(offset)
            {
            case 0x20: return (uint)transmitFifo.Count;
            case 0x24: return (uint)receiveFifo.Count;
            case 0x28:
                return (transferTimer.Enabled || receiveRemaining != 0 ? 1u : 0)
                    | (transmitFifo.Count < FifoDepth ? 2u : 0) | (transmitFifo.Count == 0 ? 4u : 0)
                    | (receiveFifo.Count != 0 ? 8u : 0) | (receiveFifo.Count == FifoDepth ? 16u : 0);
            case 0x30: return RawInterrupts & registers[0x2c / 4];
            case 0x34: return RawInterrupts;
            case 0x38: return ClearErrors(2);
            case 0x3c: return ClearErrors(8);
            case 0x40: return ClearErrors(4);
            case 0x44: return ClearErrors(0x20);
            case 0x48: return ClearErrors(0x2e);
            case 0x58: return 0x51535049;
            case 0x5c: return 0x3430312a;
            default:
                if(offset >= 0 && offset < 0x100 && offset % 4 == 0) return registers[offset / 4];
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            SynchronizeTime();
            var operation = (offset >> 12) & 3;
            offset &= 0xfff;
            if(operation != 0)
            {
                var previous = ReadDoubleWord(offset);
                value = operation == 1 ? previous ^ value : operation == 2 ? previous | value : previous & ~value;
            }
            if(IsDataRegister(offset))
            {
                if(!Enabled) return;
                if(transmitFifo.Count == FifoDepth) errors |= 2;
                else transmitFifo.Enqueue(value);
                TryStartFrame();
                UpdateInterrupt();
                return;
            }
            switch(offset)
            {
            case 0:
            case 4:
            case 0x14:
            case 0xf0:
            case 0xf4:
            case 0xf8:
                if(Enabled)
                {
                    this.Log(LogLevel.Warning, "Ignoring SSI configuration write while enabled: 0x{0:X}", offset);
                    return;
                }
                registers[offset / 4] = offset == 0x14 ? value & 0xfffe : value;
                break;
            case 8:
                registers[2] = value & 1;
                if(!Enabled)
                {
                    transferTimer.Enabled = false;
                    transmitFifo.Clear();
                    receiveFifo.Clear();
                    receiveRemaining = 0;
                    if(chipSelectOverride != 2) EndTransaction();
                }
                TryStartFrame();
                break;
            case 0x10:
                registers[4] = value & 1;
                if(value == 0 && chipSelectOverride != 2) EndTransaction();
                TryStartFrame();
                break;
            case 0x18: case 0x1c: case 0x2c: case 0x4c: case 0x50: case 0x54: case 0xc:
                registers[offset / 4] = value;
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
            UpdateInterrupt();
            ConfigurationChanged?.Invoke();
        }

        // IO_QSPI.OUTOVER: 0 normal, 1 invert, 2 force low, 3 force high.
        // Forced low preserves the slave transaction while SSI's FIFO is idle.
        public uint ChipSelectOverride
        {
            get => chipSelectOverride;
            set
            {
                SynchronizeTime();
                var next = value & 3;
                if(next == 3 || (next == 0 && chipSelectOverride == 2 && !transferTimer.Enabled)) EndTransaction();
                chipSelectOverride = next;
                ConfigurationChanged?.Invoke();
            }
        }

        public ulong Frequency
        {
            get => transferTimer.Frequency;
            set => transferTimer.Frequency = value;
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0) ChipSelectOverride = value ? 3u : 2u;
        }

        public event Action ConfigurationChanged;

        // This model exposes 32-bit XIP bus words in EEPROM-read mode.
        // The external flash must also accept the configured instruction/mode.
        public bool IsXIPReady => Enabled && registers[4] == 1 && chipSelectOverride == 0
            && registers[0x14 / 4] != 0 && Mode == 3 && FrameBits == 32
            && (LaneWidth == 1 || LaneWidth == 2 || LaneWidth == 4)
            && ((registers[0xf4 / 4] >> 2) & 15) >= 6
            && ((registers[0xf4 / 4] >> 2) & 15) <= 8
            && (registers[0xf4 / 4] & 3) <= 2;

        public GPIO IRQ { get; } = new GPIO();
        public long Size => 0x4000;

        private void TryStartFrame()
        {
            if(!Enabled || registers[4] == 0 || registers[0x14 / 4] == 0 || transferTimer.Enabled) return;
            if(receiveRemaining != 0)
            {
                if(receiveFifo.Count == FifoDepth) return;
                shiftValue = 0;
                shiftBits = FrameBits;
                shiftReceive = true;
                receiveRemaining--;
                ScheduleFrame(shiftBits / LaneWidth);
                return;
            }
            if(transmitFifo.Count == 0) return;
            if(!transactionActive)
            {
                if(Mode == 3 && Enhanced && transmitFifo.Count < HeaderFrames) return;
                transactionActive = true;
                headerFramesRemaining = Mode == 3 && Enhanced ? HeaderFrames : 0;
                enhancedInstruction = true;
            }
            shiftValue = transmitFifo.Dequeue();
            shiftReceive = Mode == 0;
            shiftBits = FrameBits;
            var clocks = shiftBits;
            if(Mode == 2)
            {
                shiftValue = 0;
                shiftReceive = true;
                receiveRemaining = (int)(registers[1] & 0xffff);
                clocks /= LaneWidth;
            }
            else if(Mode == 3 && Enhanced)
            {
                if(enhancedInstruction && InstructionBits != 0)
                {
                    shiftBits = InstructionBits;
                    clocks = shiftBits / ((registers[0xf4 / 4] & 3) == 2 ? LaneWidth : 1);
                    enhancedInstruction = false;
                }
                else
                {
                    shiftBits = (int)((registers[0xf4 / 4] >> 2) & 0xf) * 4;
                    clocks = shiftBits / ((registers[0xf4 / 4] & 3) == 0 ? 1 : LaneWidth);
                }
                headerFramesRemaining--;
            }
            ScheduleFrame(clocks);
        }

        private void ScheduleFrame(int clocks)
        {
            transferTimer.Limit = (ulong)Math.Max(1, clocks) * registers[0x14 / 4];
            transferTimer.ResetValue();
            transferTimer.Enabled = true;
            UpdateInterrupt();
        }

        private void CompleteFrame()
        {
            if(dummyWait)
            {
                dummyWait = false;
                FinishFrame();
                return;
            }
            var selected = chipSelectOverride == 2 || chipSelectOverride == 0;
            TryGetByAddress(0, out var target);
            uint received = 0;
            if(shiftBits % 8 != 0)
            {
                this.Log(LogLevel.Warning, "Unsupported non-byte SSI frame size: {0}", shiftBits);
            }
            else
            {
                for(var bit = shiftBits - 8; bit >= 0; bit -= 8)
                {
                    received = (received << 8) | (selected && target != null ? target.Transmit((byte)(shiftValue >> bit)) : (byte)0xff);
                }
            }
            if(shiftReceive)
            {
                if(receiveFifo.Count == FifoDepth) errors |= 8;
                else receiveFifo.Enqueue(received);
            }
            if(Mode == 3 && transmitFifo.Count == 0 && headerFramesRemaining == 0 && !shiftReceive)
            {
                // Dummy cycles consume wire time but are not instruction/address data.
                // At the byte-level flash interface they are represented by dummy bytes.
                var waitCycles = Enhanced ? (int)((registers[0xf4 / 4] >> 11) & 0x1f) : 0;
                for(var bit = 0; bit < waitCycles * LaneWidth; bit += 8)
                {
                    if(selected) target?.Transmit(0);
                }
                receiveRemaining = (int)(registers[1] & 0xffff) + 1;
                if(waitCycles != 0)
                {
                    shiftBits = 0;
                    shiftReceive = false;
                    ScheduleFrame(waitCycles);
                    dummyWait = true;
                    return;
                }
            }
            FinishFrame();
        }

        private void FinishFrame()
        {
            if(transmitFifo.Count == 0 && receiveRemaining == 0)
            {
                if(chipSelectOverride != 2) EndTransaction();
                else transactionActive = false;
            }
            TryStartFrame();
            UpdateInterrupt();
        }

        private void EndTransaction()
        {
            if(transactionActive || chipSelectOverride == 2)
            {
                if(TryGetByAddress(0, out var target)) target.FinishTransmission();
            }
            transactionActive = false;
            ConfigurationChanged?.Invoke();
        }

        private uint ClearErrors(uint mask)
        {
            errors &= ~mask;
            UpdateInterrupt();
            return 1;
        }

        private void SynchronizeTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu)) cpu.SyncTime();
        }

        private void UpdateInterrupt() => IRQ.Set((RawInterrupts & registers[0x2c / 4]) != 0);
        private static bool IsDataRegister(long offset) => offset >= 0x60 && offset <= 0xec && offset % 4 == 0;
        private uint RawInterrupts => errors | (Enabled && transmitFifo.Count <= registers[0x18 / 4] ? 1u : 0)
            | (Enabled && receiveFifo.Count > registers[0x1c / 4] ? 16u : 0);
        private bool Enabled => (registers[2] & 1) != 0;
        private int Mode => (int)((registers[0] >> 8) & 3);
        private int FrameBits => (int)((registers[0] >> 16) & 31) + 1;
        private bool Enhanced => LaneWidth != 1;
        private int LaneWidth => 1 << (int)((registers[0] >> 21) & 3);
        private int InstructionBits => new[] { 0, 4, 8, 16 }[(registers[0xf4 / 4] >> 8) & 3];
        private int HeaderFrames => (InstructionBits != 0 ? 1 : 0) + (((registers[0xf4 / 4] >> 2) & 15) != 0 ? 1 : 0);

        private readonly Queue<uint> transmitFifo;
        private readonly Queue<uint> receiveFifo;
        private readonly uint[] registers;
        private readonly LimitTimer transferTimer;
        private uint chipSelectOverride;
        private uint errors;
        private uint shiftValue;
        private int shiftBits;
        private bool shiftReceive;
        private bool transactionActive;
        private bool enhancedInstruction;
        private bool dummyWait;
        private int headerFramesRemaining;
        private int receiveRemaining;
        private const int FifoDepth = 16;
    }
}
