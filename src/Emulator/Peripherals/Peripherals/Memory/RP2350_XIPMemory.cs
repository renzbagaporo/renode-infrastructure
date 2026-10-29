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
    // All accesses traverse the QMI configuration, including instruction fetches.
    // This model retains no data-cache lines. NOR writes invalidate translated
    // CPU blocks, and stores through the flash window cannot program the array.
    public sealed class RP2350_XIPMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IMapped, IKnownSize, IMemory, IDisposable
    {
        public RP2350_XIPMemory(IMachine machine, WinbondW25QFlash flash, RP2350_QMI qmi, bool untranslated = false)
        {
            this.machine = machine;
            this.flash = flash;
            this.qmi = qmi;
            this.untranslated = untranslated;
            instructionImage = new MappedMemory(machine, Size, segmentSize: 4096);
            instructionImage.SegmentTouched += RefreshInstructionPage;
            machine.PeripheralsChanged += OnPeripheralsChanged;
            flash.StateChanged += InvalidateInstructions;
            qmi.ConfigurationChanged += InvalidateInstructions;
        }

        public byte ReadByte(long offset)
        {
            return qmi.FlashReadConfigured && qmi.TryTranslate(offset, untranslated, out var physical)
                ? flash.UnderlyingMemory.ReadByte(physical % flash.UnderlyingMemory.Size) : (byte)0xff;
        }

        public ushort ReadWord(long offset) => (ushort)(ReadByte(offset) | ReadByte(offset + 1) << 8);
        public uint ReadDoubleWord(long offset) => (uint)(ReadWord(offset) | ReadWord(offset + 2) << 16);
        public ulong ReadQuadWord(long offset) => ReadDoubleWord(offset) | (ulong)ReadDoubleWord(offset + 4) << 32;

        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            var result = new byte[count];
            for(var i = 0; i < count; i++) result[i] = ReadByte(offset + i);
            return result;
        }

        public void WriteByte(long offset, byte value) { }
        public void WriteWord(long offset, ushort value) { }
        public void WriteDoubleWord(long offset, uint value) { }
        public void WriteQuadWord(long offset, ulong value) { }
        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, IPeripheral context = null) { }

        public void LoadBinary(string fileName, long offset = 0)
        {
            var data = File.ReadAllBytes(fileName);
            flash.UnderlyingMemory.WriteBytes(offset, data, 0, data.Length);
            InvalidateInstructions();
        }

        public void Dispose()
        {
            machine.PeripheralsChanged -= OnPeripheralsChanged;
            flash.StateChanged -= InvalidateInstructions;
            qmi.ConfigurationChanged -= InvalidateInstructions;
            instructionImage.Dispose();
        }

        public void Reset() => InvalidateInstructions();
        public IEnumerable<IMappedSegment> MappedSegments => instructionImage.MappedSegments;
        public long Size => 0x2000000;

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
            var data = ReadBytes(offset, 4096);
            instructionImage.WriteBytes(offset, data, 0, data.Length);
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
        private readonly IMachine machine;
        private readonly WinbondW25QFlash flash;
        private readonly RP2350_QMI qmi;
        private readonly bool untranslated;
    }
}
