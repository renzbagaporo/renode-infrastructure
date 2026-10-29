//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals
{
    // RP-series APB bridges replicate narrow writes across the data bus. The
    // upper address bits select an atomic operation on the destination register.
    public abstract class RP2APBPeripheral : IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize
    {
        public uint ReadDoubleWord(long offset)
        {
            return ReadRegister(offset & RegisterOffsetMask);
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 2) * 8));
        }

        public byte ReadByte(long offset)
        {
            return (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            if(!ResetHeld)
            {
                WriteRegister(offset & RegisterOffsetMask, value, (AtomicOperation)((offset >> 12) & 3));
            }
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteDoubleWord(offset & ~3L, (uint)value | ((uint)value << 16));
        }

        public void WriteByte(long offset, byte value)
        {
            WriteDoubleWord(offset & ~3L, (uint)value * 0x01010101u);
        }

        public abstract void Reset();

        public long Size => 0x4000;

        public bool ResetHeld
        {
            get => resetHeld;
            set
            {
                if(value && !resetHeld)
                {
                    Reset();
                }
                resetHeld = value;
            }
        }

        protected RP2APBPeripheral(IMachine machine)
        {
            this.machine = machine;
        }

        protected static uint ApplyOperation(uint previous, uint value, AtomicOperation operation, uint writableMask = uint.MaxValue)
        {
            switch(operation)
            {
            case AtomicOperation.Xor:
                return previous ^ (value & writableMask);
            case AtomicOperation.Set:
                return previous | (value & writableMask);
            case AtomicOperation.Clear:
                return previous & ~(value & writableMask);
            default:
                return (previous & ~writableMask) | (value & writableMask);
            }
        }

        protected abstract uint ReadRegister(long offset);

        protected abstract void WriteRegister(long offset, uint value, AtomicOperation operation);

        protected readonly IMachine machine;

        protected enum AtomicOperation
        {
            Write,
            Xor,
            Set,
            Clear,
        }

        private bool resetHeld;

        private const long RegisterOffsetMask = 0xfff;
    }
}
