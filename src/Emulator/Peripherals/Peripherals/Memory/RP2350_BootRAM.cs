//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Memory
{
    // 1 KiB APB data RAM with the standard atomic aliases. Instruction fetch
    // is disconnected from the peripheral bus, including the boot RAM.
    public sealed class RP2350_BootRAM : RP2APBPeripheral, IBytePeripheral, IWordPeripheral
    {
        public RP2350_BootRAM(IMachine machine, RP2350_AccessControl accessControl = null) : base(machine)
        {
            this.accessControl = accessControl;
            Reset();
        }

        // Retained across warm resets of either processor.
        public override void Reset()
        {
            lock(SyncRoot)
            {
                writeOnce[0] = writeOnce[1] = 0;
                unclaimedLocks = 0xff;
            }
        }

        // The RAM array samples byte enables, including on atomic aliases.
        // The control registers use the peripheral bridge's replicated data.
        public new void WriteByte(long offset, byte value)
        {
            if((offset & 0xfff) >= 0x400) { base.WriteByte(offset, value); return; }
            WriteRAMLanes(offset, value, 0xff);
        }

        public new void WriteWord(long offset, ushort value)
        {
            if((offset & 0xfff) >= 0x400) { base.WriteWord(offset, value); return; }
            WriteRAMLanes(offset & ~1L, value, 0xffff);
        }

        private void WriteRAMLanes(long offset, uint value, uint mask)
        {
            lock(SyncRoot)
            {
                RequireSecureAccess();
                if(ResetHeld) return;
                var shift = (int)(offset & 3) * 8;
                var index = (offset & 0x3ff) / 4;
                words[index] = ApplyOperation(words[index], value << shift, (AtomicOperation)((offset >> 12) & 3), mask << shift);
            }
        }

        protected override uint ReadRegister(long offset)
        {
            RequireSecureAccess();
            if(offset < 0x400) return words[offset / 4];
            if(offset == 0x800 || offset == 0x804) return writeOnce[(offset - 0x800) / 4];
            if(offset == 0x808) return unclaimedLocks;
            if(offset >= 0x80c && offset <= 0x828 && (offset & 3) == 0)
            {
                var bit = 1u << (int)((offset - 0x80c) / 4);
                var claimed = unclaimedLocks & bit;
                unclaimedLocks &= ~bit;
                return claimed;
            }
            throw new BusAccessException(BusAccessError.AddressError);
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            RequireSecureAccess();
            if(offset < 0x400)
            {
                words[offset / 4] = ApplyOperation(words[offset / 4], value, operation);
                return;
            }
            if(offset == 0x800 || offset == 0x804)
            {
                var index = (offset - 0x800) / 4;
                writeOnce[index] |= ApplyOperation(writeOnce[index], value, operation);
                return;
            }
            if(offset == 0x808) return;
            if(offset >= 0x80c && offset <= 0x828 && (offset & 3) == 0)
            {
                unclaimedLocks |= 1u << (int)((offset - 0x80c) / 4);
                return;
            }
            throw new BusAccessException(BusAccessError.AddressError);
        }

        private void RequireSecureAccess()
        {
            var bus = machine.SystemBus;
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state))
            {
                if(!state.DebugAccess && bus.TryGetTransactionInitiator(out var initiator) && initiator is CortexM core
                    && accessControl != null && accessControl.CoreIsForcedNonSecure((int)core.MultiprocessingId))
                    throw new BusAccessException(BusAccessError.CommandError);
                if(!state.AttributionSecure) throw new BusAccessException(BusAccessError.CommandError);
            }
            else if(bus.TryGetTransactionInitiator(out var initiator) && initiator is CortexM core && core.TrustZoneEnabled
                && (!core.SecureState || accessControl != null && accessControl.CoreIsForcedNonSecure((int)core.MultiprocessingId)))
                throw new BusAccessException(BusAccessError.CommandError);
        }

        private readonly RP2350_AccessControl accessControl;
        private readonly uint[] writeOnce = new uint[2];
        private uint unclaimedLocks;
        private readonly uint[] words = new uint[0x100];
    }
}
