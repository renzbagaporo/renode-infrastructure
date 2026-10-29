//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Memory
{
    // Pinned cache lines provide SRAM while ordinary reads fetch from serial
    // NOR. Instruction fetches and data accesses traverse the same bus path to
    // preserve QMI availability, translation, and pinned-cache semantics.
    public sealed class RP2350_XIPMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IExecutableIO, IKnownSize, IDisposable
    {
        public RP2350_XIPMemory(IMachine machine, WinbondW25QFlash flash, RP2350_QMI qmi, bool untranslated = false,
            RP2350_XIP xip = null, bool useCache = true, long windowOffset = 0, long size = 0x2000000)
        {
            if(windowOffset < 0 || size <= 0 || windowOffset + size > 0x4000000)
                throw new ArgumentException("XIP registration must fit the 64 MiB cache address space");
            this.windowOffset = windowOffset;
            Size = size;
            this.machine = machine;
            this.flash = flash;
            this.qmi = qmi;
            this.untranslated = untranslated;
            this.xip = xip;
            this.useCache = useCache;
            if(xip != null)
            {
                xip.ConfigurationChanged += InvalidateInstructions;
                xip.ContentsChanged += InvalidateCacheRange;
            }
            machine.PeripheralsChanged += OnPeripheralsChanged;
            flash.StateChanged += InvalidateInstructions;
            qmi.ConfigurationChanged += InvalidateInstructions;
        }

        public byte ReadByte(long offset) => (byte)ReadTransaction(offset, 1);
        public ushort ReadWord(long offset) => (ushort)ReadTransaction(offset, 2);
        public uint ReadDoubleWord(long offset) => (uint)ReadTransaction(offset, 4);
        public ulong ReadQuadWord(long offset) => ReadTransaction(offset, 8);

        private byte ReadSingleByte(long offset, out bool cacheHit)
        {
            cacheHit = false;
            if(offset < 0 || offset >= Size) throw new BusAccessException(BusAccessError.AddressError);
            offset += windowOffset;
            if(xip != null && xip.TryReadPinned(offset, useCache, untranslated, out var cached))
            {
                cacheHit = true;
                return cached;
            }
            if(offset >= 0x2000000) throw new BusAccessException(BusAccessError.AddressError);
            if(!qmi.Available) throw new BusAccessException(BusAccessError.GenericError);
            if(offset >= 0x1000000) return 0xff;
            if(!qmi.TryTranslate(offset, untranslated, out var physical)) throw new BusAccessException(BusAccessError.AddressError);
            return qmi.FlashReadConfigured && flash.ReadAvailable
                ? flash.UnderlyingMemory.ReadByte(physical % flash.UnderlyingMemory.Size) : (byte)0xff;
        }

        private ulong ReadTransaction(long offset, int count)
        {
            RememberReadPages(offset, count);
            return xip == null ? ReadTransactionCore(offset, count) : xip.ExecuteTransaction(() => ReadTransactionCore(offset, count));
        }

        private ulong ReadTransactionCore(long offset, int count)
        {
            ulong result = 0;
            var hit = true;
            for(var i = 0; i < count; i++)
            {
                result |= (ulong)ReadSingleByte(offset + i, out var byteHit) << (i * 8);
                hit &= byteHit;
            }
            xip?.RecordAccess(hit);
            return result;
        }

        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++) result[i] = ReadByte(offset + i);
            return result;
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset < 0 || offset >= Size) throw new BusAccessException(BusAccessError.AddressError);
            offset += windowOffset;
            if(xip != null && xip.TryWritePinned(offset, value, useCache, untranslated)) return;
            if(offset >= 0x2000000) throw new BusAccessException(BusAccessError.AddressError);
            // The serial NOR array can only be programmed with a serial command.
        }
        public void WriteWord(long offset, ushort value) => WriteTransaction(offset, value, 2);
        public void WriteDoubleWord(long offset, uint value) => WriteTransaction(offset, value, 4);
        public void WriteQuadWord(long offset, ulong value) => WriteTransaction(offset, value, 8);

        private void WriteTransaction(long offset, ulong value, int count)
        {
            void Write()
            {
                for(var i = 0; i < count; i++) WriteByte(offset + i, (byte)(value >> (i * 8)));
            }
            if(xip == null) Write();
            else xip.ExecuteTransaction(Write);
        }
        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, IPeripheral context = null)
        {
            for(var i = 0; i < count; i++) WriteByte(offset + i, array[startingIndex + i]);
        }

        public void LoadBinary(string fileName, long offset = 0)
        {
            if(windowOffset != 0) throw new ArgumentException("Flash images must be loaded through the lower XIP window");
            flash.LoadBinary(fileName, offset);
        }

        public void Dispose()
        {
            machine.PeripheralsChanged -= OnPeripheralsChanged;
            flash.StateChanged -= InvalidateInstructions;
            qmi.ConfigurationChanged -= InvalidateInstructions;
            if(xip != null)
            {
                xip.ConfigurationChanged -= InvalidateInstructions;
                xip.ContentsChanged -= InvalidateCacheRange;
            }
        }

        public void Reset() => InvalidateInstructions();
        public long Size { get; }

        private void RememberReadPages(long offset, int count)
        {
            var bus = machine.GetSystemBus(this);
            if(!bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                || !state.InstructionFetch) return;
            if(offset < 0 || count <= 0 || offset >= Size) return;
            var end = Math.Min(Size - 1, offset + count - 1);
            lock(readPages)
            {
                for(var page = offset / PageSize; page <= end / PageSize; page++) readPages.Add(page);
            }
        }

        private void InvalidateInstructions()
        {
            long[] pages;
            lock(readPages) pages = readPages.ToArray();
            InvalidatePages(pages);
        }

        private void InvalidateCacheRange(long offset, int count)
        {
            if(count <= 0 || offset + count <= windowOffset || offset >= windowOffset + Size) return;
            var first = Math.Max(0, offset - windowOffset) / PageSize;
            var last = Math.Min(Size - 1, offset + count - 1 - windowOffset) / PageSize;
            long[] pages;
            lock(readPages) pages = readPages.Where(page => page >= first && page <= last).ToArray();
            InvalidatePages(pages);
        }

        private void InvalidatePages(long[] pages)
        {
            if(pages.Length == 0) return;
            var bus = machine.GetSystemBus(this);
            var registrations = bus.GetRegistrationPoints(this).ToArray();
            if(!registrations.Any(registration => pages.Any(page =>
                (ulong)((page + 1) * PageSize) > registration.Offset
                && (ulong)(page * PageSize) < registration.Offset + registration.Range.Size))) return;
            foreach(var cpu in bus.GetCPUs().OfType<TranslationCPU>())
            {
                // A code read may still be translating when another master
                // changes the flash mapping. Clear on the CPU thread after
                // that translation returns, including any newly published TB.
                cpu.RequestTranslationCacheClearing();
            }
        }

        private void OnPeripheralsChanged(IMachine _, PeripheralsChangedEventArgs change)
        {
            if(change.Peripheral == this || change.Peripheral is ICPUWithMappedMemory) RouteDataAccessesThroughBus();
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

        private readonly HashSet<long> readPages = new HashSet<long>();
        private const long PageSize = 4096;
        private readonly long windowOffset;
        private readonly RP2350_XIP xip;
        private readonly bool useCache;
        private readonly IMachine machine;
        private readonly WinbondW25QFlash flash;
        private readonly RP2350_QMI qmi;
        private readonly bool untranslated;
    }
}
