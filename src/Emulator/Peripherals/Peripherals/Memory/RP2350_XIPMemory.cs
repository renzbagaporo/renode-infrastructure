//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Memory
{
    // Pinned cache lines provide SRAM while ordinary reads fetch from serial
    // NOR. Instruction images follow QMI translation and cache-tag changes;
    // data accesses traverse the bus to preserve store and fault semantics.
    public sealed class RP2350_XIPMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IMapped, IKnownSize, IMemory, IDisposable
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
            instructionImage = new MappedMemory(machine, Size, segmentSize: 4096);
            instructionImage.SegmentTouched += RefreshInstructionPage;
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
        public void WriteWord(long offset, ushort value)
        {
            for(var i = 0; i < 2; i++) WriteByte(offset + i, (byte)(value >> (i * 8)));
        }
        public void WriteDoubleWord(long offset, uint value)
        {
            for(var i = 0; i < 4; i++) WriteByte(offset + i, (byte)(value >> (i * 8)));
        }
        public void WriteQuadWord(long offset, ulong value)
        {
            for(var i = 0; i < 8; i++) WriteByte(offset + i, (byte)(value >> (i * 8)));
        }
        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, IPeripheral context = null)
        {
            for(var i = 0; i < count; i++) WriteByte(offset + i, array[startingIndex + i]);
        }

        public void LoadBinary(string fileName, long offset = 0)
        {
            if(windowOffset != 0) throw new ArgumentException("Flash images must be loaded through the lower XIP window");
            var data = File.ReadAllBytes(fileName);
            flash.UnderlyingMemory.WriteBytes(offset, data, 0, data.Length);
            InvalidateInstructions();
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
            instructionImage.Dispose();
        }

        public void Reset() => InvalidateInstructions();
        public IEnumerable<IMappedSegment> MappedSegments => instructionImage.MappedSegments;
        public long Size { get; }

        private void InvalidateInstructions()
        {
            for(var page = 0; page < Size / 4096; page++)
            {
                if(instructionImage.IsTouched(page)) RefreshInstructionPage(page);
            }
            foreach(var cpu in machine.GetSystemBus(this).GetCPUs().OfType<TranslationCPU>()) cpu.InvalidateTranslationBlocks();
            RouteDataAccessesThroughBus();
        }

        private void RefreshInstructionPage(int page)
        {
            var offset = (long)page * 4096;
            var data = new byte[4096];
            for(var i = 0; i < data.Length; i++)
            {
                try { data[i] = ReadSingleByte(offset + i, out var _); }
                catch(BusAccessException) { data[i] = 0xff; }
            }
            instructionImage.WriteBytes(offset, data, 0, data.Length);
        }

        private void InvalidateCacheRange(long offset, int count)
        {
            if(offset + count <= windowOffset || offset >= windowOffset + Size) return;
            offset -= windowOffset;
            var touched = false;
            for(var page = (int)(offset / 4096); page <= (offset + count - 1) / 4096; page++)
            {
                if(!instructionImage.IsTouched(page)) continue;
                RefreshInstructionPage(page);
                touched = true;
            }
            if(touched)
                foreach(var cpu in machine.GetSystemBus(this).GetCPUs().OfType<TranslationCPU>()) cpu.InvalidateTranslationBlocks();
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

        private readonly MappedMemory instructionImage;
        private readonly long windowOffset;
        private readonly RP2350_XIP xip;
        private readonly bool useCache;
        private readonly IMachine machine;
        private readonly WinbondW25QFlash flash;
        private readonly RP2350_QMI qmi;
        private readonly bool untranslated;
    }
}
