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

namespace Antmicro.Renode.Peripherals.Memory
{
    // Immutable mask ROM. Images are provisioned explicitly; CPU and debugger
    // stores, including writes through the data bus, cannot modify the array.
    public sealed class RP2350_ROM : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IMapped, IMemory, IKnownSize, IDisposable
    {
        public RP2350_ROM(IMachine machine)
        {
            this.machine = machine;
            memory = new MappedMemory(machine, Size);
            machine.PeripheralsChanged += OnPeripheralsChanged;
        }

        public byte ReadByte(long offset) => memory.ReadByte(offset);
        public ushort ReadWord(long offset) => memory.ReadWord(offset);
        public uint ReadDoubleWord(long offset) => memory.ReadDoubleWord(offset);
        public ulong ReadQuadWord(long offset) => memory.ReadQuadWord(offset);
        public byte[] ReadBytes(long offset, int count, IPeripheral context = null) => memory.ReadBytes(offset, count, context);
        public void WriteByte(long offset, byte value) { }
        public void WriteWord(long offset, ushort value) { }
        public void WriteDoubleWord(long offset, uint value) { }
        public void WriteQuadWord(long offset, ulong value) { }
        public void WriteBytes(long offset, byte[] data, int startingIndex, int count, IPeripheral context = null) { }

        public void LoadBinary(string fileName, long offset = 0)
        {
            var data = File.ReadAllBytes(fileName);
            if(offset < 0 || offset + data.LongLength > Size)
                throw new ArgumentException("ROM image exceeds the 32 KiB mask ROM");
            memory.WriteBytes(offset, data, 0, data.Length);
            foreach(var cpu in machine.SystemBus.GetCPUs().OfType<TranslationCPU>()) cpu.InvalidateTranslationBlocks();
        }

        public void Reset() { }
        public long Size => 0x8000;
        public IEnumerable<IMappedSegment> MappedSegments => memory.MappedSegments;
        public void Dispose()
        {
            machine.PeripheralsChanged -= OnPeripheralsChanged;
            memory.Dispose();
        }

        private void OnPeripheralsChanged(IMachine _, PeripheralsChangedEventArgs change)
        {
            if(change.Peripheral != this && !(change.Peripheral is ICPUWithMappedMemory)) return;
            var bus = machine.SystemBus;
            foreach(var registration in bus.GetRegistrationPoints(this))
                foreach(var cpu in bus.GetCPUs().OfType<ICPUWithMappedMemory>())
                    cpu.RegisterAccessFlags(registration.Range.StartAddress, registration.Range.Size, isIoMemory: true);
        }

        private readonly IMachine machine;
        private readonly MappedMemory memory;
    }
}
