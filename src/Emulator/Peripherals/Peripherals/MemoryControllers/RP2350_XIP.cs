//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    // Cache maintenance operates on 2048 eight-byte lines, with set/way selected
    // by address bits13:3. Pinned lines retain their tag and data across core
    // resets and can back SRAM in the otherwise unused upper XIP address space.
    public sealed class RP2350_XIP : RP2APBPeripheral
    {
        public RP2350_XIP(IMachine machine) : base(machine) { Reset(); }
        public override void Reset()
        {
            lock(SyncRoot)
            {
                control = 0x83;
                accesses = hits = 0;
                configurationPending = true;
            }
            PublishNotifications();
        }
        public void SetPowerPresent(bool value)
        {
            lock(SyncRoot)
            {
                if(powerPresent == value) return;
                powerPresent = value;
                if(!value)
                {
                    Array.Clear(data, 0, data.Length);
                    Array.Clear(tags, 0, tags.Length);
                    Array.Clear(pinned, 0, pinned.Length);
                }
                configurationPending = true;
            }
            PublishNotifications();
        }

        public event Action ConfigurationChanged;
        public event Action<long, int> ContentsChanged;

        public bool TryReadPinned(long offset, bool useCache, bool untranslated, out byte value)
        {
            lock(SyncRoot) return ReadPinned(offset, useCache, untranslated, out value);
        }

        private bool ReadPinned(long offset, bool useCache, bool untranslated, out byte value)
        {
            CheckAccess(useCache, untranslated);
            var secure = Secure;
            var line = (int)((offset >> 3) & 0x7ff);
            if(useCache && (control & 8) == 0 && (control & (secure ? 1u : 2u)) != 0
                && pinned[line] && tags[line] == (offset & ~7L))
            {
                value = data[line * 8 + (offset & 7)];
                return true;
            }
            value = 0;
            return false;
        }

        public bool TryWritePinned(long offset, byte value, bool useCache, bool untranslated)
        {
            try
            {
                lock(SyncRoot)
                {
                    if(!ReadPinned(offset, useCache, untranslated, out var _)) return false;
                    // Read-only QMI windows downgrade stores to reads, including hits.
                    if(offset < 0x2000000 && (control & (offset < 0x1000000 ? 0x400u : 0x800u)) == 0) return true;
                    var line = (int)((offset >> 3) & 0x7ff);
                    data[line * 8 + (offset & 7)] = value;
                    pendingRanges.Enqueue(new KeyValuePair<long, int>(offset, 1));
                    return true;
                }
            }
            finally { PublishNotifications(); }
        }

        public void Maintain(long offset)
        {
            try
            {
                lock(SyncRoot)
                {
                    if(!powerPresent) throw new BusAccessException(BusAccessError.GenericError);
                    if(!Secure && (control & 0x100) == 0) throw new BusAccessException(BusAccessError.CommandError);
                    var operation = offset & 7;
                    var line = (int)((offset >> 3) & 0x7ff);
                    switch(operation)
                    {
                    case 0:
                        if(pinned[line])
                        {
                            pinned[line] = false;
                            pendingRanges.Enqueue(new KeyValuePair<long, int>(tags[line], 8));
                        }
                        break;
                    case 1:
                        // Pinned cache-as-SRAM lines have no downstream memory to clean.
                        break;
                    case 2:
                        var address = offset & ~7L;
                        var set = (int)((offset >> 3) & 0x3ff);
                        for(var way = 0; way < 2; way++)
                        {
                            var index = set + way * 1024;
                            if(!pinned[index] || tags[index] != address) continue;
                            pinned[index] = false;
                            pendingRanges.Enqueue(new KeyValuePair<long, int>(address, 8));
                        }
                        break;
                    case 3: break;
                    case 7:
                        if((control & 8) != 0) throw new BusAccessException(BusAccessError.GenericError);
                        if(pinned[line])
                        {
                            pinned[line] = false;
                            pendingRanges.Enqueue(new KeyValuePair<long, int>(tags[line], 8));
                        }
                        tags[line] = offset & ~7L;
                        pinned[line] = true;
                        pendingRanges.Enqueue(new KeyValuePair<long, int>(tags[line], 8));
                        break;
                    default: throw new BusAccessException(BusAccessError.CommandError);
                    }
                }
            }
            finally { PublishNotifications(); }
        }

        public void RecordAccess(bool hit)
        {
            lock(SyncRoot)
            {
                if(accesses != uint.MaxValue) accesses++;
                if(hit && hits != uint.MaxValue) hits++;
            }
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0: return control;
            case 8: return 2; // Streaming FIFO is empty until streaming is supported.
            case 0xc: return hits;
            case 0x10: return accesses;
            default: this.LogUnhandledRead(offset); return 0;
            }
        }
        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0:
                if(!Secure) return;
                control = ApplyOperation(control, value, operation, 0xffb);
                if((control & 8) != 0) control &= ~3u;
                configurationPending = true;
                return;
            case 0xc: hits = 0; return;
            case 0x10: accesses = 0; return;
            case 0x18:
                if(value != 0) this.Log(LogLevel.Error, "XIP streaming reads are not implemented; transfer will not complete");
                return;
            default: this.LogUnhandledWrite(offset, value); return;
            }
        }
        protected override void OnAccessCompleted() => PublishNotifications();

        private void PublishNotifications()
        {
            if(Monitor.IsEntered(SyncRoot)) return;
            lock(SyncRoot)
            {
                if(publishing) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    bool configuration;
                    KeyValuePair<long, int>[] ranges;
                    lock(SyncRoot)
                    {
                        if(!configurationPending && pendingRanges.Count == 0) { publishing = false; return; }
                        configuration = configurationPending;
                        configurationPending = false;
                        ranges = pendingRanges.ToArray();
                        pendingRanges.Clear();
                    }
                    if(configuration) ConfigurationChanged?.Invoke();
                    foreach(var range in ranges) ContentsChanged?.Invoke(range.Key, range.Value);
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private void CheckAccess(bool useCache, bool untranslated)
        {
            if(!powerPresent) throw new BusAccessException(BusAccessError.GenericError);
            if(useCache) return;
            var bit = untranslated ? (Secure ? 6 : 7) : (Secure ? 4 : 5);
            if((control & (1u << bit)) != 0) throw new BusAccessException(BusAccessError.CommandError);
        }
        private bool Secure
        {
            get
            {
                var bus = machine.GetSystemBus(this);
                if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)) return state.AttributionSecure;
                if(bus.TryGetTransactionInitiator(out var initiator) && initiator is CortexM core && core.TrustZoneEnabled) return core.SecureState;
                return true;
            }
        }
        private readonly Queue<KeyValuePair<long, int>> pendingRanges = new Queue<KeyValuePair<long, int>>();
        private bool powerPresent = true;
        private bool configurationPending;
        private bool publishing;
        private readonly byte[] data = new byte[0x4000];
        private readonly long[] tags = new long[2048];
        private readonly bool[] pinned = new bool[2048];
        private uint control;
        private uint accesses;
        private uint hits;
    }
}
