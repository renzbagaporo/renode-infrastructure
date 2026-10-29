//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.Bus;
namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    public sealed class RP2350_XIPMaintenance : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public RP2350_XIPMaintenance(RP2350_XIP xip) { this.xip = xip; }
        public byte ReadByte(long offset) => throw new BusAccessException(BusAccessError.CommandError);
        public ushort ReadWord(long offset) => throw new BusAccessException(BusAccessError.CommandError);
        public uint ReadDoubleWord(long offset) => throw new BusAccessException(BusAccessError.CommandError);
        public void WriteByte(long offset, byte value) => xip.Maintain(offset);
        public void WriteWord(long offset, ushort value) => xip.Maintain(offset);
        public void WriteDoubleWord(long offset, uint value) => xip.Maintain(offset);
        public void Reset() { }
        public long Size => 0x4000000;
        private readonly RP2350_XIP xip;
    }
}
