// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    internal static class RP2350_GPIOAccess
    {
        public static bool IsSecure(IMachine machine, RP2350_AccessControl accessControl)
        {
            var bus = machine.SystemBus;
            if(bus.TryGetCurrentCPU(out var cpu) && accessControl != null
                && accessControl.CoreIsForcedNonSecure((int)cpu.MultiprocessingId))
            {
                return false;
            }
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state))
            {
                return state.AttributionSecure;
            }
            if(cpu is CortexM cortexM && cortexM.TrustZoneEnabled)
            {
                return cortexM.SecureState;
            }
            // Debug transactions without explicit attributes use the Secure view.
            return true;
        }
    }
}
