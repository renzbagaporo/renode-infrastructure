//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public static class RP2CpuTime
    {
        // Brings the CPU that issued the current bus access up to its own
        // time, so a register reflects every cycle it has executed. A
        // debug-port access, a DMA access and an access made off that CPU's
        // thread have no CPU time of their own to bring forward.
        public static void SyncInitiator(IMachine machine)
        {
            if(TryGetInitiator(machine, out var cpu)) cpu.SyncTime();
        }

        // When the current bus access happens: the issuing CPU's own time, which
        // runs ahead of the machine clock (the slowest CPU) during a quantum, or
        // the machine clock for an access without a CPU of its own.
        public static TimeInterval AccessTime(IMachine machine)
        {
            var now = machine.ClockSource.CurrentValue;
            if(!TryGetInitiator(machine, out var cpu) || cpu.TimeHandle == null) return now;
            var own = cpu.TimeHandle.TotalElapsedTime;
            return own > now ? own : now;
        }

        // The CPU whose own thread issued the current bus access, unless it is
        // a debug-port access.
        public static bool TryGetInitiator(IMachine machine, out BaseCPU cpu)
        {
            cpu = null;
            var bus = machine.SystemBus;
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state) && state.DebugAccess) return false;
            if(!bus.TryGetTransactionInitiator(out var initiator) || !(initiator is BaseCPU found) || !found.OnPossessedThread) return false;
            cpu = found;
            return true;
        }
    }
}
