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
    // the XIP_AUX alias (RP2_XIPAux); its DREQ follows FIFO non-empty.
    // Each word is fetched as soon as the FIFO has room: bus arbitration
    // with XIP fetches is not modelled.
    // The 16 KiB cache is 2-way set-associative with 8-byte lines: set from
    // address bits 12:3, least recently used way replaced. Reads through the
    // cached aliases look it up while CTRL.EN is set, and only the
    // allocating alias fills a missed line; a write to it deallocates a
    // matching line, and FLUSH clears every line. CTR_ACC and CTR_HIT count
    // those reads from reset for the first BootReads of them, so a program
    // reads its boot's counts, and stop there unless the program has
    // accessed either counter, which restarts and keeps the counting; while
    // they do not count, the cached aliases read the NOR directly.
    // Instruction fetches are never counted.
    public sealed class RP2040_XIP : RP2APBPeripheral, IRP2XIPStream
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
                Array.Fill(tags, Invalid);
                Array.Clear(leastRecent, 0, leastRecent.Length);
                hits = accesses = 0;
                measuring = true;
                countersUsed = false;
                bootReads = BootReads;
                streamAddress = 0;
                streamCount = 0;
                streamFifo.Clear();
            }
            PublishRequest();
            UpdateAvailability();
        }

        public GPIO StreamDMA { get; } = new GPIO();

        // A pop through the XIP_AUX alias.
        public uint PopStream()
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

        // True while the counters count: the cached aliases then pass every
        // data read through RecordRead.
        public bool Measuring
        {
            get
            {
                lock(SyncRoot) return measuring;
            }
        }

        public void RecordRead(long offset, bool allocate)
        {
            lock(SyncRoot)
            {
                if((control & 1) == 0) return;
                var line = offset >> 3;
                var set = (int)(line & (Sets - 1));
                var counting = measuring;
                if(counting && accesses != uint.MaxValue) accesses++;
                if(!countersUsed && measuring && --bootReads <= 0)
                {
                    measuring = false;
                    measuringPending = true;
                }
                for(var way = 0; way < Ways; way++)
                {
                    if(tags[set * Ways + way] != line) continue;
                    if(counting && hits != uint.MaxValue) hits++;
                    leastRecent[set] = (byte)(1 - way);
                    return;
                }
                if(!allocate) return;
                var victim = leastRecent[set];
                tags[set * Ways + victim] = line;
                leastRecent[set] = (byte)(1 - victim);
            }
        }

        public void DeallocateLine(long offset)
        {
            lock(SyncRoot)
            {
                var line = offset >> 3;
                var set = (int)(line & (Sets - 1));
                for(var way = 0; way < Ways; way++)
                {
                    if(tags[set * Ways + way] == line) tags[set * Ways + way] = Invalid;
                }
            }
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0: return control;
            case 4: return flushReady ? 1u : 0;
            case 8: return (streamFifo.Count == 0 ? 2u : 0) | (streamFifo.Count == StreamDepth ? 4u : 0) | (flushReady ? 1u : 0);
            case 0xc: StartMeasuring(); return hits;
            case 0x10: StartMeasuring(); return accesses;
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
                Array.Fill(tags, Invalid);
                flushReady = true;
                break;
            case 0xc:
                hits = 0;
                StartMeasuring();
                break;
            case 0x10:
                accesses = 0;
                StartMeasuring();
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

        protected override void OnAccessCompleted()
        {
            PublishRequest();
            PublishMeasuring();
        }

        // Applies a change of whether the counters count to the cached
        // aliases, outside the locks of the access that made it.
        public void PublishMeasuring()
        {
            bool pending;
            lock(SyncRoot)
            {
                pending = measuringPending;
                measuringPending = false;
            }
            if(pending) UpdateAvailability();
        }

        private void StartMeasuring()
        {
            countersUsed = true;
            if(measuring) return;
            measuring = true;
            measuringPending = true;
        }

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
        private readonly long[] tags = new long[Sets * Ways];
        private readonly byte[] leastRecent = new byte[Sets];
        private uint hits;
        private uint accesses;
        private bool measuring;
        private bool measuringPending;
        private bool countersUsed;
        private int bootReads;

        private const int BootReads = 65536;
        private const int StreamDepth = 2;
        private const int Sets = 1024;
        private const int Ways = 2;
        private const long Invalid = -1;
    }

    // An XIP controller's streaming FIFO, as XIP_AUX pops it.
    public interface IRP2XIPStream
    {
        uint PopStream();
    }

    // XIP_AUX (RP2040 0x50400000, RP2350 0x50500000): reads of STREAM (offset
    // 0) pop the XIP streaming FIFO. The RP2350's QMI direct-mode FIFOs at 4
    // and 8 are not modelled.
    public sealed class RP2_XIPAux : IDoubleWordPeripheral, IKnownSize
    {
        public RP2_XIPAux(IRP2XIPStream xip)
        {
            this.xip = xip;
        }

        public uint ReadDoubleWord(long offset) => offset == 0 ? xip.PopStream() : 0;

        public void WriteDoubleWord(long offset, uint value)
        {
        }

        public void Reset()
        {
        }

        public long Size => 0x1000;

        private readonly IRP2XIPStream xip;
    }
}
