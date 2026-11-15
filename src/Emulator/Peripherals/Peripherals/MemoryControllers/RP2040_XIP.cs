//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;


namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    // XIP aliases consult the serial controller and NOR state for each bus
    // transaction, including instruction fetches. The streaming engine
    // (STREAM_ADDR, STREAM_CTR) reads words through the uncached
    // non-allocating alias into a 2-word FIFO, popped through STREAM_FIFO or
    // the XIP_AUX alias (RP2040_XIPAux); its DREQ follows FIFO non-empty.
    // Each word is fetched as soon as the FIFO has room: bus arbitration
    // with XIP fetches is not modelled.
    public sealed class RP2040_XIP : RP2APBPeripheral
    {
        public RP2040_XIP(IMachine machine, RP2040_SSI ssi, WinbondW25QFlash flash) : base(machine)
        {
            this.ssi = ssi;
            this.flash = flash;
            ssi.ConfigurationChanged += UpdateAvailability;
            flash.StateChanged += UpdateAvailability;
            Reset();
        }

        public override void Reset()
        {
            lock(SyncRoot)
            {
                control = 3;
                flushReady = false;
                streamAddress = 0;
                streamCount = 0;
                streamFifo.Clear();
            }
            PublishRequest();
            UpdateAvailability();
        }

        public GPIO StreamDMA { get; } = new GPIO();

        // A pop through the XIP_AUX alias.
        internal uint PopStream()
        {
            uint value;
            lock(SyncRoot) value = PopStreamWord();
            PublishRequest();
            return value;
        }

        public bool Available
        {
            get
            {
                var available = false;
                machine.ClockSource.ExecuteInLock(() => available =
                    ssi.TryGetXIPConfiguration(out var serialControl, out var frameFormat)
                    && FlashAcceptsRead(serialControl, frameFormat));
                return available;
            }
        }

        public event Action AvailabilityChanged;

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0: return control;
            case 4: return flushReady ? 1u : 0;
            case 8: return (streamFifo.Count == 0 ? 2u : 0) | (streamFifo.Count == StreamDepth ? 4u : 0) | (flushReady ? 1u : 0);
            case 0xc:
            case 0x10: return 0;
            case 0x14: return streamAddress;
            case 0x18: return streamCount;
            case 0x1c: return PopStreamWord();
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0:
                // Disabling the cache leaves uncached flash accesses available.
                control = ApplyOperation(control, value, operation, 0xb);
                break;
            case 4:
                // NOR writes invalidate the shared mapped memory's translated
                // blocks; there are no retained data-cache lines to flush.
                flushReady = true;
                break;
            case 0xc:
            case 0x10:
                break;
            case 0x14:
                streamAddress = ApplyOperation(streamAddress, value, operation, 0xfffffffc);
                break;
            case 0x18:
                streamCount = ApplyOperation(streamCount, value, operation, 0x3fffff);
                FillStream();
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        protected override void OnAccessCompleted() => PublishRequest();

        private uint PopStreamWord()
        {
            if(streamFifo.Count == 0) return 0;
            var value = streamFifo.Dequeue();
            FillStream();
            return value;
        }

        private void FillStream()
        {
            while(streamCount != 0 && streamFifo.Count < StreamDepth)
            {
                streamFifo.Enqueue(machine.SystemBus.ReadDoubleWord(0x13000000 | (streamAddress & 0x00fffffc)));
                streamAddress += 4;
                streamCount--;
            }
            requestLevel = streamFifo.Count != 0;
        }

        // Outside the state monitor: a DMA channel answers synchronously.
        private void PublishRequest()
        {
            bool level;
            lock(SyncRoot) level = requestLevel;
            if(level == publishedRequest) return;
            publishedRequest = level;
            StreamDMA.Set(level);
        }

        private bool FlashAcceptsRead(uint serialControl, uint frameFormat)
        {
            if(!flash.ReadAvailable) return false;
            var instructionLength = (serialControl >> 8) & 3;
            var command = (serialControl >> 24) & 0xff;
            if(frameFormat == 0)
            {
                return !flash.ContinuousReadEnabled && instructionLength == 2 && (command == 3 || command == 0xb);
            }
            if(frameFormat != 2 || !flash.QuadEnabled) return false;
            if(instructionLength == 0) return flash.ContinuousReadEnabled;
            return !flash.ContinuousReadEnabled && instructionLength == 2 && (command == 0x6b || command == 0xeb);
        }

        private void UpdateAvailability()
        {
            AvailabilityChanged?.Invoke();
        }

        private readonly RP2040_SSI ssi;
        private readonly WinbondW25QFlash flash;
        private uint control;
        private bool flushReady;
        private uint streamAddress;
        private uint streamCount;
        private bool requestLevel;
        private bool publishedRequest;
        private readonly Queue<uint> streamFifo = new Queue<uint>();

        private const int StreamDepth = 2;
    }

    // RP2040 XIP_AUX (0x50400000): reads pop the XIP streaming FIFO.
    public sealed class RP2040_XIPAux : IDoubleWordPeripheral, IKnownSize
    {
        public RP2040_XIPAux(RP2040_XIP xip)
        {
            this.xip = xip;
        }

        public uint ReadDoubleWord(long offset) => xip.PopStream();

        public void WriteDoubleWord(long offset, uint value)
        {
        }

        public void Reset()
        {
        }

        public long Size => 0x1000;

        private readonly RP2040_XIP xip;
    }
}
