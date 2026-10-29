//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_AccessControl : RP2APBPeripheral
    {
        public RP2350_AccessControl(IMachine machine) : base(machine) { Reset(); }
        public override void Reset()
        {
            lock(SyncRoot)
            {
                locked = 4;
                forceCoreNonSecure = 0;
                ResetConfiguration();
            }
            PublishNotifications();
        }

        public uint GPIONonSecureMask0 => gpioMasks[0];
        public uint GPIONonSecureMask1 => gpioMasks[1];
        public bool PeripheralIsNonSecure(long registerOffset)
        {
            // NSP gates both Non-secure privileged and unprivileged access;
            // NSU alone cannot grant access when NSP is clear.
            return registerOffset >= 0x14 && registerOffset <= 0xe8 && registerOffset % 4 == 0
                && (permissions[(registerOffset - 0x14) / 4] & 2) != 0;
        }
        public bool GPIOIsNonSecure(int pin) => pin >= 0 && pin < 64 && (gpioMasks[pin / 32] & (1u << (pin % 32))) != 0;
        public bool CoreIsForcedNonSecure(int coreIndex) => coreIndex == 1 && (forceCoreNonSecure & 2) != 0;
        public event Action GPIOAccessChanged;

        protected override void OnAccessCompleted() => PublishNotifications();

        protected override uint ReadRegister(long offset)
        {
            if(!IsSecurePrivileged(out var _)) throw new BusAccessException(BusAccessError.CommandError);
            switch(offset)
            {
            case 0: return locked;
            case 4: return forceCoreNonSecure;
            case 8: return 0;
            case 0xc: return gpioMasks[0];
            case 0x10: return gpioMasks[1];
            default:
                if(offset >= 0x14 && offset <= 0xe8 && offset % 4 == 0) return permissions[(offset - 0x14) / 4];
                this.LogUnhandledRead(offset); return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            var securePrivileged = IsSecurePrivileged(out var master);
            if(!securePrivileged)
            {
                // A Non-secure privileged writer can change NSU only if NSP
                // grants it access to that peripheral's permission register.
                if(offset >= 0x14 && offset <= 0xe8 && offset % 4 == 0
                    && machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                    && state.Privileged && !state.AttributionSecure)
                {
                    var index = (offset - 0x14) / 4;
                    if((permissions[index] & 2) != 0 && (value >> 16) == 0xacce)
                    {
                        if((locked & (1u << master)) == 0)
                        {
                            var updated = ApplyOperation(permissions[index], value, operation, 1);
                            notificationPending |= updated != permissions[index];
                            permissions[index] = updated;
                        }
                        return;
                    }
                }
                throw new BusAccessException(BusAccessError.CommandError);
            }
            if((locked & (1u << master)) != 0) return;
            if(offset != 0xc && offset != 0x10 && (value >> 16) != 0xacce) throw new BusAccessException(BusAccessError.CommandError);
            switch(offset)
            {
            case 0: locked |= value & 0xb; return;
            case 4:
                var forced = ApplyOperation(forceCoreNonSecure, value, operation, 2);
                notificationPending |= forced != forceCoreNonSecure;
                forceCoreNonSecure = forced;
                return;
            case 8: if((value & 1) != 0) ResetConfiguration(); return;
            case 0xc: case 0x10:
                var index = (offset - 0xc) / 4;
                var mask = ApplyOperation(gpioMasks[index], value, operation, index == 0 ? uint.MaxValue : 0xff00ffff);
                notificationPending |= mask != gpioMasks[index];
                gpioMasks[index] = mask;
                return;
            default:
                if(offset >= 0x14 && offset <= 0xe8 && offset % 4 == 0)
                {
                    var permissionIndex = (offset - 0x14) / 4;
                    var permission = ApplyOperation(permissions[permissionIndex], value, operation, 0xff);
                    notificationPending |= permission != permissions[permissionIndex];
                    permissions[permissionIndex] = permission;
                }
                else this.LogUnhandledWrite(offset, value);
                return;
            }
        }

        private bool IsSecurePrivileged(out int master)
        {
            master = 3;
            if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var transaction) && transaction.DebugAccess)
                return transaction.AttributionSecure && transaction.Privileged;
            if(!machine.SystemBus.TryGetTransactionInitiator(out var initiator)) return true;
            if(!(initiator is CortexM core)) { master = 2; return false; }
            master = (int)core.MultiprocessingId;
            if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state))
                return state.AttributionSecure && state.Privileged && !CoreIsForcedNonSecure(master);
            return (!core.TrustZoneEnabled || core.SecureState) && !CoreIsForcedNonSecure(master);
        }

        private void ResetConfiguration()
        {
            Array.Clear(gpioMasks, 0, gpioMasks.Length);
            for(var i = 0; i < permissions.Length; i++)
            {
                var offset = i * 4 + 0x14;
                permissions[i] = offset <= 0x40 || offset == 0x60 ? 0xffu
                    : offset == 0x58 || offset == 0x5c || offset >= 0xb0 && offset != 0xb8 && offset != 0xe8 ? 0xb8u
                    : offset == 0xb8 || offset == 0xe8 ? 0xf8u : 0xfcu;
            }
            notificationPending = true;
        }

        private void PublishNotifications()
        {
            if(Monitor.IsEntered(SyncRoot)) return;
            lock(SyncRoot)
            {
                if(publishing) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    lock(SyncRoot)
                    {
                        if(!notificationPending) { publishing = false; return; }
                        notificationPending = false;
                    }
                    GPIOAccessChanged?.Invoke();
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private bool notificationPending;
        private bool publishing;
        private readonly uint[] permissions = new uint[(0xe8 - 0x14) / 4 + 1];
        private readonly uint[] gpioMasks = new uint[2];
        private uint locked;
        private uint forceCoreNonSecure;
    }
}
