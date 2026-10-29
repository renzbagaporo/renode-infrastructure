//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

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
    // The cache is not retained: all reads fetch the NOR backing data. XIP
    // writes invalidate cache tags and must never program the serial flash.
    public sealed class RP2040_XIPMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IMapped, IKnownSize, IMemory
    {
        public RP2040_XIPMemory(IMachine machine, WinbondW25QFlash flash, RP2040_XIP xip)
        {
            this.machine = machine;
            this.flash = flash;
            this.xip = xip;
            flash.StateChanged += InvalidateInstructions;
            xip.AvailabilityChanged += RouteDataAccessesThroughBus;
            machine.PeripheralsChanged += (_, change) =>
            {
                if(change.Peripheral == this || change.Peripheral is ICPUWithMappedMemory)
                {
                    RouteDataAccessesThroughBus();
                }
            };
        }

        public byte ReadByte(long offset) => xip.Available ? flash.UnderlyingMemory.ReadByte(offset) : (byte)0;

        public ushort ReadWord(long offset) => xip.Available ? flash.UnderlyingMemory.ReadWord(offset) : (ushort)0;

        public uint ReadDoubleWord(long offset) => xip.Available ? flash.UnderlyingMemory.ReadDoubleWord(offset) : 0;

        public ulong ReadQuadWord(long offset) => xip.Available ? flash.UnderlyingMemory.ReadQuadWord(offset) : 0;

        public void WriteByte(long offset, byte value)
        {
        }

        public void WriteWord(long offset, ushort value)
        {
        }

        public void WriteDoubleWord(long offset, uint value)
        {
        }

        public void WriteQuadWord(long offset, ulong value)
        {
        }

        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            return xip.Available ? flash.UnderlyingMemory.ReadBytes(offset, count) : new byte[count];
        }

        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, IPeripheral context = null)
        {
        }

        public void LoadBinary(string fileName, long offset = 0)
        {
            var data = File.ReadAllBytes(fileName);
            flash.UnderlyingMemory.WriteBytes(offset, data, 0, data.Length);
            InvalidateInstructions();
        }

        public void Reset()
        {
            RouteDataAccessesThroughBus();
        }

        public long Size => flash.UnderlyingMemory.Size;

        public IEnumerable<IMappedSegment> MappedSegments => flash.UnderlyingMemory.MappedSegments;

        private void RouteDataAccessesThroughBus()
        {
            if(!xip.Available)
            {
                return;
            }
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
            foreach(var cpu in machine.GetSystemBus(this).GetCPUs().OfType<TranslationCPU>())
            {
                cpu.InvalidateTranslationBlocks();
            }
            RouteDataAccessesThroughBus();
        }

        private readonly IMachine machine;
        private readonly WinbondW25QFlash flash;
        private readonly RP2040_XIP xip;
    }
}
