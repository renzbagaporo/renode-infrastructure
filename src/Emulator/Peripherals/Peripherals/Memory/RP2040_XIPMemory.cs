//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Memory
{
    // While the SSI and NOR accept XIP reads, CPUs read and fetch directly
    // from the NOR contents, mapped read-only; writes reach this peripheral and
    // are ignored. Otherwise every CPU access goes through the bus and observes
    // SSI/NOR availability. Other bus masters always use the bus. Programming
    // remains available only through the flash serial interface.
    // Each XIP alias is its own instance over the shared NOR: cached
    // (0x10, 0x11) or not (0x12, 0x13), allocating (0x10) or not. While the
    // XIP controller measures its cache counters, CPU data reads of a cached
    // alias go through the bus and are recorded in the cache, and writes to
    // the cached allocating alias deallocate their line. The instance that
    // is cached and allocating owns the NOR contents.
    public sealed class RP2040_XIPMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IExecutableIO, IKnownSize, IDisposable
    {
        public RP2040_XIPMemory(IMachine machine, WinbondW25QFlash flash, RP2040_XIP xip, bool cached = true, bool allocate = true)
        {
            this.machine = machine;
            this.flash = flash;
            this.xip = xip;
            this.cached = cached;
            this.allocate = allocate;
            // Availability notifications arrive at the end of every serial
            // transaction; translations are cleared when the array or the
            // availability actually changes.
            flash.ArrayChanged += InvalidateArray;
            xip.AvailabilityChanged += UpdateDirectAccess;
            machine.PeripheralsChanged += OnPeripheralsChanged;
        }

        public byte ReadByte(long offset) => (byte)ReadTransaction(offset, 1);
        public ushort ReadWord(long offset) => (ushort)ReadTransaction(offset, 2);
        public uint ReadDoubleWord(long offset) => (uint)ReadTransaction(offset, 4);
        public ulong ReadQuadWord(long offset) => ReadTransaction(offset, 8);

        private ulong ReadTransaction(long offset, int count)
        {
            if(offset < 0 || offset > Size - count) throw new BusAccessException(BusAccessError.AddressError);
            SynchronizeDataAccess();
            var fetch = MarkInstructionFetch();
            ulong value = 0;
            machine.ClockSource.ExecuteInLock(() =>
            {
                if(!CanRead()) return;
                for(var i = 0; i < count; i++) value |= (ulong)flash.UnderlyingMemory.ReadByte(offset + i) << (8 * i);
                if(cached && !fetch) xip.RecordRead(offset, allocate);
            });
            return value;
        }

        private bool MarkInstructionFetch()
        {
            if(!machine.GetSystemBus(this).TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                || !state.InstructionFetch) return false;
            Volatile.Write(ref hasFetchedInstructions, 1);
            return true;
        }

        private void SynchronizeDataAccess()
        {
            var bus = machine.GetSystemBus(this);
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                && (state.InstructionFetch || state.DebugAccess)) return;
            if(bus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        private bool CanRead()
        {
            if(xip.Available) return true;
            if(machine.GetSystemBus(this).TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                && state.InstructionFetch) throw new BusAccessException(BusAccessError.GenericError);
            return false;
        }

        public void WriteByte(long offset, byte value) => Write(offset);

        public void WriteWord(long offset, ushort value) => Write(offset);

        public void WriteDoubleWord(long offset, uint value) => Write(offset);

        public void WriteQuadWord(long offset, ulong value) => Write(offset);

        private void Write(long offset)
        {
            if(cached && allocate) xip.DeallocateLine(offset);
        }

        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            SynchronizeDataAccess();
            MarkInstructionFetch();
            var result = new byte[count];
            machine.ClockSource.ExecuteInLock(() =>
            {
                if(CanRead()) result = flash.UnderlyingMemory.ReadBytes(offset, count);
            });
            return result;
        }

        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, IPeripheral context = null)
        {
        }

        public void LoadBinary(string fileName, long offset = 0)
        {
            var data = File.ReadAllBytes(fileName);
            machine.ClockSource.ExecuteInLock(() =>
            {
                flash.UnderlyingMemory.WriteBytes(offset, data, 0, data.Length);
                InvalidateInstructions();
            });
        }

        public void Dispose()
        {
            machine.PeripheralsChanged -= OnPeripheralsChanged;
            flash.ArrayChanged -= InvalidateArray;
            xip.AvailabilityChanged -= UpdateDirectAccess;
            if(cached && allocate) flash.UnderlyingMemory.Dispose();
        }

        public void Reset()
        {
            RouteDataAccessesThroughBus();
            UpdateDirectAccess(force: true);
        }

        public long Size => flash.UnderlyingMemory.Size;

        private void OnPeripheralsChanged(IMachine _, PeripheralsChangedEventArgs change)
        {
            if(change.Peripheral == this && change.Operation == PeripheralsChangedEventArgs.PeripheralChangeType.Addition)
            {
                MapFlashContents();
            }
            if(change.Peripheral == this || change.Peripheral is ICPUWithMappedMemory)
            {
                RouteDataAccessesThroughBus();
                UpdateDirectAccess(force: true);
            }
        }

        // Each alias is registered separately, so map every registration
        // point when it appears.
        private void MapFlashContents()
        {
            var bus = machine.GetSystemBus(this);
            foreach(var registration in bus.GetRegistrationPoints(this))
            {
                if(!mappedAliases.Add(registration.Range.StartAddress)) continue;
                var start = registration.Range.StartAddress - registration.Offset;
                foreach(var segment in flash.UnderlyingMemory.MappedSegments)
                {
                    if(segment.StartingOffset >= registration.Range.Size + registration.Offset) continue;
                    var size = Math.Min(segment.Size, registration.Range.Size + registration.Offset - segment.StartingOffset);
                    bus.MapMemory(new SystemBus.MappedSegmentWrapper(segment, start, size, null), this, relative: false);
                }
            }
        }

        private void InvalidateArray(long offset, long count) => InvalidateInstructions();

        private void UpdateDirectAccess() => UpdateDirectAccess(force: false);

        private void UpdateDirectAccess(bool force)
        {
            if(mappedAliases.Count == 0)
            {
                InvalidateInstructions();
                return;
            }
            var available = xip.Available && !(cached && xip.Measuring);
            lock(directAccessLock)
            {
                if(!force && available == directAccess) return;
                directAccess = available;
                // Code translated while XIP was available must not run while
                // it is not, and the reverse.
                InvalidateInstructions();
                if(available)
                {
                    // Translations may now come from the mapping without a bus fetch.
                    Volatile.Write(ref hasFetchedInstructions, 1);
                }
                var bus = machine.GetSystemBus(this);
                foreach(var registration in bus.GetRegistrationPoints(this))
                {
                    foreach(var cpu in bus.GetCPUs().OfType<TranslationCPU>())
                    {
                        cpu.RequestReadOnlyMemoryAccess(registration.Range, available);
                    }
                }
            }
        }

        private void RouteDataAccessesThroughBus()
        {
            var bus = machine.GetSystemBus(this);
            foreach(var registration in bus.GetRegistrationPoints(this))
            {
                foreach(var cpu in bus.GetCPUs().OfType<ICPUWithMappedMemory>())
                {
                    cpu.RegisterAccessFlags(registration.Range.StartAddress, registration.Range.Size, isIoMemory: true);
                }
            }
        }

        private void InvalidateInstructions()
        {
            if(Volatile.Read(ref hasFetchedInstructions) == 0) return;
            foreach(var cpu in machine.GetSystemBus(this).GetCPUs().OfType<TranslationCPU>())
            {
                // Request clearing on the CPU thread after a concurrent code
                // translation returns, including any newly published block.
                cpu.RequestTranslationCacheClearing();
            }
        }

        private int hasFetchedInstructions;
        private readonly HashSet<ulong> mappedAliases = new HashSet<ulong>();
        private bool directAccess;
        private readonly object directAccessLock = new object();
        private readonly IMachine machine;
        private readonly WinbondW25QFlash flash;
        private readonly RP2040_XIP xip;
        private readonly bool cached;
        private readonly bool allocate;
    }
}
