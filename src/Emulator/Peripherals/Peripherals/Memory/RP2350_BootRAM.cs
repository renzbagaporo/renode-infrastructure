//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Memory
{
    // 1 KiB APB boot RAM, including the standard atomic write aliases. It is
    // retained across warm core resets and lives in the switched XIP domain.
    public sealed class RP2350_BootRAM : RP2APBPeripheral, IMapped, IMemory, IDisposable
    {
        public RP2350_BootRAM(IMachine machine) : base(machine)
        {
            memory = new MappedMemory(machine, 0x1000);
            machine.PeripheralsChanged += OnPeripheralsChanged;
        }

        public override void Reset() { }
        public ulong ReadQuadWord(long offset) => ReadDoubleWord(offset) | (ulong)ReadDoubleWord(offset + 4) << 32;
        public void WriteQuadWord(long offset, ulong value)
        {
            WriteDoubleWord(offset, (uint)value);
            WriteDoubleWord(offset + 4, (uint)(value >> 32));
        }
        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            var data = new byte[count];
            for(var i = 0; i < count; i++) data[i] = ReadByte(offset + i);
            return data;
        }
        public void WriteBytes(long offset, byte[] data, int startingIndex, int count, IPeripheral context = null)
        {
            for(var i = 0; i < count; i++) WriteByte(offset + i, data[startingIndex + i]);
        }
        public IEnumerable<IMappedSegment> MappedSegments => memory.MappedSegments;
        public void Dispose()
        {
            machine.PeripheralsChanged -= OnPeripheralsChanged;
            memory.Dispose();
        }

        protected override uint ReadRegister(long offset) => offset < 0x400 ? memory.ReadDoubleWord(offset) : 0;

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset >= 0x400) return;
            memory.WriteDoubleWord(offset, ApplyOperation(memory.ReadDoubleWord(offset), value, operation));
            foreach(var cpu in machine.GetSystemBus(this).GetCPUs().OfType<TranslationCPU>()) cpu.InvalidateTranslationBlocks();
        }

        private void OnPeripheralsChanged(IMachine _, PeripheralsChangedEventArgs change)
        {
            if(change.Peripheral != this && !(change.Peripheral is ICPUWithMappedMemory)) return;
            var bus = machine.GetSystemBus(this);
            foreach(var registration in bus.GetRegistrationPoints(this))
            {
                foreach(var cpu in bus.GetCPUs().OfType<ICPUWithMappedMemory>())
                    cpu.RegisterAccessFlags(registration.Range.StartAddress, registration.Range.Size, isIoMemory: true);
            }
        }

        private readonly MappedMemory memory;
    }
}
