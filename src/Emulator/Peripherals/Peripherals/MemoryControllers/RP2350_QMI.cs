//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    // RP2350 datasheet section 12.14. Direct FIFO records retain their wire
    // width and complete on the divided system clock, including RX backpressure.
    public sealed class RP2350_QMI : RP2APBPeripheral
    {
        public RP2350_QMI(IMachine machine, WinbondW25QFlash flash, RP2350_Clocks clocks = null,
            ulong frequency = 6000000) : base(machine)
        {
            this.flash = flash;
            this.clocks = clocks;
            transferTimer = new LimitTimer(machine.ClockSource, frequency, this, "serial frame",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            transferTimer.LimitReached += CompleteFrame;
            if(clocks != null) clocks.ClockChanged += UpdateClock;
            flash.StateChanged += NotifyConfiguration;
            Reset();
        }

        public override void Reset()
        {
            EndTransaction();
            transferTimer.Reset();
            transmit.Clear();
            receive.Clear();
            directControl = 6u << 22;
            for(var window = 0; window < 2; window++)
            {
                var index = window * 5;
                memoryConfiguration[index] = 0x40000004;
                memoryConfiguration[index + 1] = 0x1000;
                memoryConfiguration[index + 2] = 0xa003;
                memoryConfiguration[index + 3] = 0x1000;
                memoryConfiguration[index + 4] = 0xa002;
            }
            for(var i = 0; i < addressTranslation.Length; i++) addressTranslation[i] = 0x04000000u | (uint)(i % 4) * 0x400;
            UpdateClock();
            NotifyConfiguration();
        }

        public bool Available => !ResetHeld && (directControl & 1) == 0
            && (clocks == null || clocks.SystemFrequency != 0) && !Selected && flash.ReadAvailable;

        public event Action ConfigurationChanged;

        // This endpoint has CS0 flash. CS1 reads represent an undriven serial
        // bus. Translation SIZE is a count of sectors, BASE wraps at 16 MiB.
        public bool TryTranslate(long offset, bool untranslated, out long physical)
        {
            physical = 0;
            if(offset < 0 || offset >= 0x1000000 || !Available) return false;
            if(untranslated)
            {
                physical = offset;
                return true;
            }
            var entry = addressTranslation[offset >> 22];
            if(((offset >> 12) & 0x3ff) >= ((entry >> 16) & 0x7ff)) return false;
            physical = (((entry & 0xfff) << 12) + (offset & 0x3fffff)) & 0xffffff;
            return true;
        }

        public bool FlashReadConfigured
        {
            get
            {
                var format = memoryConfiguration[1];
                var command = memoryConfiguration[2] & 0xff;
                var prefix = (format & 0x1000) != 0;
                var dataWidth = (format >> 8) & 3;
                var addressWidth = (format >> 2) & 3;
                if((format & 0x10000000) != 0 || (format & 3) != 0) return false;
                if(!prefix) return flash.QuadEnabled && flash.ContinuousReadEnabled && dataWidth == 2 && addressWidth == 2;
                if(flash.ContinuousReadEnabled) return false;
                if(command == 3) return dataWidth == 0 && addressWidth == 0 && (format & 0x7c000) == 0;
                if(command == 0xb) return dataWidth == 0 && addressWidth == 0 && ((format >> 16) & 7) == 2;
                if(command == 0x6b) return flash.QuadEnabled && dataWidth == 2 && addressWidth == 0;
                return command == 0xeb && flash.QuadEnabled && dataWidth == 2 && addressWidth == 2;
            }
        }

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu)) cpu.SyncTime();
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0:
                return directControl | (transferTimer.Enabled || transmit.Count != 0 ? 2u : 0)
                    | (transmit.Count == FifoDepth ? 1u << 10 : 0)
                    | (transmit.Count == 0 ? 1u << 11 : 0) | (uint)transmit.Count << 12
                    | (receive.Count == 0 ? 1u << 16 : 0)
                    | (receive.Count == FifoDepth ? 1u << 17 : 0) | (uint)receive.Count << 18;
            case 8:
                if(receive.Count == 0) return 0;
                var value = receive.Dequeue();
                TryStartFrame();
                return value;
            default:
                if(offset >= 0xc && offset <= 0x30 && offset % 4 == 0) return memoryConfiguration[(offset - 0xc) / 4];
                if(offset >= 0x34 && offset <= 0x50 && offset % 4 == 0) return addressTranslation[(offset - 0x34) / 4];
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0:
                directControl = ApplyOperation(directControl, value, operation, 0xffc000cdu);
                UpdateSelect();
                TryStartFrame();
                break;
            case 4:
                if(transmit.Count < FifoDepth) transmit.Enqueue(value & 0x1fffff);
                TryStartFrame();
                return;
            default:
                if(offset >= 0xc && offset <= 0x30 && offset % 4 == 0)
                {
                    var index = (offset - 0xc) / 4;
                    var mask = index % 5 == 0 ? 0xf3fff7ffu : index % 5 == 2 || index % 5 == 4 ? 0xffffu : 0x1007d3ffu;
                    memoryConfiguration[index] = ApplyOperation(memoryConfiguration[index], value, operation, mask);
                }
                else if(offset >= 0x34 && offset <= 0x50 && offset % 4 == 0)
                {
                    var index = (offset - 0x34) / 4;
                    addressTranslation[index] = ApplyOperation(addressTranslation[index], value, operation, 0x07ff0fff);
                }
                else this.LogUnhandledWrite(offset, value);
                break;
            }
            NotifyConfiguration();
        }

        private void TryStartFrame()
        {
            if((directControl & 1) == 0 || transferTimer.Enabled || transmit.Count == 0 || receive.Count == FifoDepth
                || (clocks != null && clocks.SystemFrequency == 0)) return;
            frame = transmit.Dequeue();
            var width = (frame >> 16) & 3;
            if(width == 3)
            {
                this.Log(LogLevel.Warning, "Reserved QMI direct interface width");
                return;
            }
            var bits = (frame & (1u << 18)) == 0 ? 8u : 16u;
            var divisor = (directControl >> 22) & 0xff;
            transferTimer.Limit = bits / (1u << (int)width) * (divisor == 0 ? 256 : divisor);
            transferTimer.ResetValue();
            transferTimer.Enabled = true;
            UpdateSelect();
        }

        private void CompleteFrame()
        {
            var result = transactionActive ? flash.Transmit((byte)frame) : (byte)0xff;
            uint received = result;
            if((frame & (1u << 18)) != 0) received |= (uint)(transactionActive ? flash.Transmit((byte)(frame >> 8)) : (byte)0xff) << 8;
            if((frame & (1u << 20)) == 0) receive.Enqueue(received);
            TryStartFrame();
            UpdateSelect();
        }

        private void UpdateClock()
        {
            if(clocks != null && clocks.SystemFrequency != 0) transferTimer.Frequency = clocks.SystemFrequency;
            TryStartFrame();
            NotifyConfiguration();
        }

        private void UpdateSelect()
        {
            var selected = (directControl & 4) != 0 || ((directControl & 0x40) != 0 && transferTimer.Enabled);
            if(!selected) EndTransaction();
            else transactionActive = true;
        }

        private void EndTransaction()
        {
            if(transactionActive) flash.FinishTransmission();
            transactionActive = false;
        }

        private void NotifyConfiguration() => ConfigurationChanged?.Invoke();
        private bool Selected => (directControl & 0xc) != 0;
        private readonly WinbondW25QFlash flash;
        private readonly RP2350_Clocks clocks;
        private readonly LimitTimer transferTimer;
        private readonly Queue<uint> transmit = new Queue<uint>();
        private readonly Queue<uint> receive = new Queue<uint>();
        private readonly uint[] memoryConfiguration = new uint[10];
        private readonly uint[] addressTranslation = new uint[8];
        private uint directControl;
        private uint frame;
        private bool transactionActive;
        private const int FifoDepth = 4;
    }
}
