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
    public abstract class RP2APBPeripheral : IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize, IRP2ResettablePeripheral
    {
        public uint ReadDoubleWord(long offset)
        {
            try
            {
                OnBeforeAccess();
                lock(SyncRoot)
                {
                    return ReadRegister(offset & RegisterOffsetMask);
                }
            }
            finally
            {
                OnAccessCompleted();
            }
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
            try
            {
                OnBeforeAccess();
                lock(SyncRoot)
                {
                    if(!resetHeld)
                    {
                        WriteRegister(offset & RegisterOffsetMask, value, (AtomicOperation)((offset >> 12) & 3));
                    }
                }
            }
            finally
            {
                OnAccessCompleted();
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
            get
            {
                lock(SyncRoot)
                {
                    return resetHeld;
                }
            }
            set
            {
                try
                {
                    lock(SyncRoot)
                    {
                        if(value == resetHeld)
                        {
                            return;
                        }
                        resetHeld = value;
                        if(value)
                        {
                            Reset();
                        }
                        OnResetHeldChanged();
                    }
                }
                finally
                {
                    OnAccessCompleted();
                }
            }
        }

        protected RP2APBPeripheral(IMachine machine)
        {
            this.machine = machine;
        }

        // External protocol callbacks use the same monitor as register accesses
        // and reset transitions. Shared-memory peripherals can supply its lock.
        protected virtual object SyncRoot => this;

        // Clock synchronization may dispatch bus transfers back into this device;
        // perform it before taking the register/reset state monitor.
        protected virtual void OnBeforeAccess()
        {
        }

        // Devices with synchronous signal receivers publish queued notifications
        // after releasing their state monitor, avoiding cross-device lock cycles.
        protected virtual void OnAccessCompleted()
        {
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

        protected virtual void OnResetHeldChanged()
        {
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
