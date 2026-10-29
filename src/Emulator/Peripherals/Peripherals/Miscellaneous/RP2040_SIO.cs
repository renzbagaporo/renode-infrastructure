//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2040_SIO : BaseGPIOPort, IDoubleWordPeripheral, IKnownSize, IHasFrequency
    {
        public RP2040_SIO(IMachine machine) : base(machine, GPIOCount + QSPICount)
        {
            sysbus = machine.GetSystemBus(this);
            receiveFifos = new[] { new Queue<uint>(), new Queue<uint>() };
            fifoErrors = new uint[CoreCount];
            dividers = new Divider[CoreCount];
            for(var core = 0; core < CoreCount; core++)
            {
                dividers[core] = new Divider(this, machine, core);
            }
            Core0IRQ = new GPIO();
            Core1IRQ = new GPIO();
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            SynchronizeTime();
            lock(sync)
            {
                if(IsSpinlockOffset(offset))
                {
                    var mask = 1u << (int)((offset - (long)Registers.Spinlock0) / sizeof(uint));
                    if((spinlocks & mask) != 0)
                    {
                        return 0;
                    }
                    spinlocks |= mask;
                    return mask;
                }

                switch((Registers)offset)
                {
                case Registers.CpuId:
                    return (uint)CurrentCore;
                case Registers.GPIOInput:
                    return ReadInputs(0, GPIOCount);
                case Registers.QSPIInput:
                    return ReadInputs(GPIOCount, QSPICount);
                case Registers.GPIOOutput:
                    return gpioOutput;
                case Registers.GPIOOutputEnable:
                    return gpioOutputEnable;
                case Registers.QSPIOutput:
                    return qspiOutput;
                case Registers.QSPIOutputEnable:
                    return qspiOutputEnable;
                case Registers.FifoStatus:
                    var core = CurrentCore;
                    return fifoErrors[core]
                        | (receiveFifos[core].Count != 0 ? FifoValid : 0)
                        | (receiveFifos[core ^ 1].Count < FifoCapacity ? FifoReady : 0);
                case Registers.FifoRead:
                    return ReadFifo(CurrentCore);
                case Registers.SpinlockStatus:
                    return spinlocks;
                case Registers.UnsignedDividend:
                case Registers.SignedDividend:
                    return dividers[CurrentCore].Dividend;
                case Registers.UnsignedDivisor:
                case Registers.SignedDivisor:
                    return dividers[CurrentCore].Divisor;
                case Registers.Quotient:
                    var divider = dividers[CurrentCore];
                    divider.Dirty = false;
                    return divider.Quotient;
                case Registers.Remainder:
                    return dividers[CurrentCore].Remainder;
                case Registers.DividerStatus:
                    return (dividers[CurrentCore].Ready ? 1u : 0) | (dividers[CurrentCore].Dirty ? 2u : 0);
                default:
                    this.LogUnhandledRead(offset);
                    return 0;
                }
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            SynchronizeTime();
            lock(sync)
            {
                if(IsSpinlockOffset(offset))
                {
                    spinlocks &= ~(1u << (int)((offset - (long)Registers.Spinlock0) / sizeof(uint)));
                    return;
                }

                switch((Registers)offset)
                {
                case Registers.GPIOOutput:
                case Registers.GPIOOutputSet:
                case Registers.GPIOOutputClear:
                case Registers.GPIOOutputXor:
                    UpdateGPIO(ref gpioOutput, offset, value, GPIOMask);
                    return;
                case Registers.GPIOOutputEnable:
                case Registers.GPIOOutputEnableSet:
                case Registers.GPIOOutputEnableClear:
                case Registers.GPIOOutputEnableXor:
                    UpdateGPIO(ref gpioOutputEnable, offset, value, GPIOMask);
                    return;
                case Registers.QSPIOutput:
                case Registers.QSPIOutputSet:
                case Registers.QSPIOutputClear:
                case Registers.QSPIOutputXor:
                    UpdateGPIO(ref qspiOutput, offset, value, QSPIMask);
                    return;
                case Registers.QSPIOutputEnable:
                case Registers.QSPIOutputEnableSet:
                case Registers.QSPIOutputEnableClear:
                case Registers.QSPIOutputEnableXor:
                    UpdateGPIO(ref qspiOutputEnable, offset, value, QSPIMask);
                    return;
                case Registers.FifoStatus:
                    fifoErrors[CurrentCore] &= ~(value & (FifoOverflow | FifoUnderflow));
                    UpdateInterrupts();
                    return;
                case Registers.FifoWrite:
                    WriteFifo(CurrentCore, value);
                    return;
                case Registers.UnsignedDividend:
                case Registers.SignedDividend:
                    dividers[CurrentCore].Dividend = value;
                    dividers[CurrentCore].Calculate((Registers)offset == Registers.SignedDividend);
                    return;
                case Registers.UnsignedDivisor:
                case Registers.SignedDivisor:
                    dividers[CurrentCore].Divisor = value;
                    dividers[CurrentCore].Calculate((Registers)offset == Registers.SignedDivisor);
                    return;
                case Registers.Quotient:
                    dividers[CurrentCore].Quotient = value;
                    dividers[CurrentCore].RestoreResult();
                    return;
                case Registers.Remainder:
                    dividers[CurrentCore].Remainder = value;
                    dividers[CurrentCore].RestoreResult();
                    return;
                default:
                    this.LogUnhandledWrite(offset, value);
                    return;
                }
            }
        }

        public override void Reset()
        {
            lock(sync)
            {
                base.Reset();
                gpioOutput = 0;
                gpioOutputEnable = 0;
                qspiOutput = 0;
                qspiOutputEnable = 0;
                spinlocks = 0;
                for(var core = 0; core < CoreCount; core++)
                {
                    receiveFifos[core].Clear();
                    fifoErrors[core] = 0;
                    dividers[core].Reset();
                }
                UpdateInterrupts();
                GPIOChanged?.Invoke();
            }
        }

        public GPIO Core0IRQ { get; }

        public GPIO Core1IRQ { get; }

        // These are the SIO signals before the IO_BANK mux and pad output enable.
        public ulong Output => gpioOutput | ((ulong)qspiOutput << GPIOCount);

        public ulong OutputEnable => gpioOutputEnable | ((ulong)qspiOutputEnable << GPIOCount);

        public long Size => 0x1000;

        public ulong Frequency
        {
            get => dividers[0].Timer.Frequency;
            set
            {
                foreach(var divider in dividers)
                {
                    divider.Timer.Frequency = value;
                }
            }
        }

        public event Action GPIOChanged;

        private int CurrentCore
        {
            get
            {
                // Debug accesses without a CPU context observe core 0's bank.
                return sysbus.TryGetCurrentCPU(out var cpu) ? (int)(cpu.MultiprocessingId & 1) : 0;
            }
        }

        private uint ReadInputs(int first, int count)
        {
            var result = 0u;
            for(var pin = 0; pin < count; pin++)
            {
                if(State[first + pin])
                {
                    result |= 1u << pin;
                }
            }
            return result;
        }

        private void SynchronizeTime()
        {
            if(sysbus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
            }
        }

        private uint ReadFifo(int core)
        {
            var result = 0u;
            if(receiveFifos[core].Count == 0)
            {
                fifoErrors[core] |= FifoUnderflow;
            }
            else
            {
                result = receiveFifos[core].Dequeue();
            }
            UpdateInterrupts();
            return result;
        }

        private void WriteFifo(int core, uint value)
        {
            var destination = receiveFifos[core ^ 1];
            if(destination.Count == FifoCapacity)
            {
                fifoErrors[core] |= FifoOverflow;
            }
            else
            {
                destination.Enqueue(value);
            }
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            Core0IRQ.Set(receiveFifos[0].Count != 0 || fifoErrors[0] != 0);
            Core1IRQ.Set(receiveFifos[1].Count != 0 || fifoErrors[1] != 0);
        }

        private void UpdateGPIO(ref uint field, long offset, uint value, uint mask)
        {
            switch(offset & 0xc)
            {
            case 0:
                field = value & mask;
                break;
            case 4:
                field |= value & mask;
                break;
            case 8:
                field &= ~(value & mask);
                break;
            case 12:
                field ^= value & mask;
                break;
            }
            for(var pin = 0; pin < NumberOfConnections; pin++)
            {
                Connections[pin].Set((Output & (1ul << pin)) != 0);
            }
            GPIOChanged?.Invoke();
        }

        private static bool IsSpinlockOffset(long offset)
        {
            return offset >= (long)Registers.Spinlock0 && offset <= (long)Registers.Spinlock31 && (offset & 3) == 0;
        }

        private readonly IBusController sysbus;
        private readonly Queue<uint>[] receiveFifos;
        private readonly uint[] fifoErrors;
        private readonly Divider[] dividers;
        private readonly object sync = new object();
        private uint spinlocks;
        private uint gpioOutput;
        private uint gpioOutputEnable;
        private uint qspiOutput;
        private uint qspiOutputEnable;

        private const int CoreCount = 2;
        private const int FifoCapacity = 8;
        private const int GPIOCount = 30;
        private const int QSPICount = 6;
        private const uint GPIOMask = (1u << GPIOCount) - 1;
        private const uint QSPIMask = (1u << QSPICount) - 1;
        private const uint FifoValid = 1;
        private const uint FifoReady = 2;
        private const uint FifoOverflow = 4;
        private const uint FifoUnderflow = 8;

        private sealed class Divider
        {
            public Divider(RP2040_SIO parent, IMachine machine, int core)
            {
                Timer = new LimitTimer(machine.ClockSource, 125000000, parent, $"Divider{core}", limit: 8, workMode: WorkMode.OneShot);
            }

            public void Calculate(bool signed)
            {
                Timer.Enabled = false;
                Timer.ResetValue();
                Dirty = true;
                if(Divisor == 0)
                {
                    Quotient = signed && (int)Dividend < 0 ? 1u : uint.MaxValue;
                    Remainder = Dividend;
                }
                else if(signed)
                {
                    // Use 64-bit arithmetic so INT_MIN / -1 wraps to INT_MIN.
                    Quotient = unchecked((uint)((long)(int)Dividend / (int)Divisor));
                    Remainder = unchecked((uint)((long)(int)Dividend % (int)Divisor));
                }
                else
                {
                    Quotient = Dividend / Divisor;
                    Remainder = Dividend % Divisor;
                }
                // Result registers are undefined until the eight-cycle operation completes.
                Timer.Enabled = true;
            }

            public void RestoreResult()
            {
                Timer.Enabled = false;
                Dirty = true;
            }

            public void Reset()
            {
                Timer.Reset();
                Dividend = 0;
                Divisor = 0;
                Quotient = 0;
                Remainder = 0;
                Dirty = false;
            }

            public LimitTimer Timer { get; }

            public uint Dividend { get; set; }

            public uint Divisor { get; set; }

            public uint Quotient { get; set; }

            public uint Remainder { get; set; }

            public bool Dirty { get; set; }

            public bool Ready => !Timer.Enabled;
        }

        private enum Registers : long
        {
            CpuId = 0x0,
            GPIOInput = 0x4,
            QSPIInput = 0x8,
            GPIOOutput = 0x10,
            GPIOOutputSet = 0x14,
            GPIOOutputClear = 0x18,
            GPIOOutputXor = 0x1c,
            GPIOOutputEnable = 0x20,
            GPIOOutputEnableSet = 0x24,
            GPIOOutputEnableClear = 0x28,
            GPIOOutputEnableXor = 0x2c,
            QSPIOutput = 0x30,
            QSPIOutputSet = 0x34,
            QSPIOutputClear = 0x38,
            QSPIOutputXor = 0x3c,
            QSPIOutputEnable = 0x40,
            QSPIOutputEnableSet = 0x44,
            QSPIOutputEnableClear = 0x48,
            QSPIOutputEnableXor = 0x4c,
            FifoStatus = 0x50,
            FifoWrite = 0x54,
            FifoRead = 0x58,
            SpinlockStatus = 0x5c,
            UnsignedDividend = 0x60,
            UnsignedDivisor = 0x64,
            SignedDividend = 0x68,
            SignedDivisor = 0x6c,
            Quotient = 0x70,
            Remainder = 0x74,
            DividerStatus = 0x78,
            Spinlock0 = 0x100,
            Spinlock31 = 0x17c,
        }
    }
}
