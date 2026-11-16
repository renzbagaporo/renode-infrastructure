//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public static class RP2CpuTime
    {
        // The cores execute one instruction per clk_sys cycle, as RP2040's
        // Cortex-M0+ and RP2350's Cortex-M33 and Hazard3 roughly do. A core
        // converts the time it is granted to instructions at the start of its
        // round and the instructions it ran back to time at the end, so a
        // change a core makes while it executes applies at the next
        // synchronization point, between rounds.
        public static void SetCoreClock(IMachine machine, ulong systemFrequency)
        {
            if(systemFrequency == 0) return;
            var mips = (uint)Math.Max(1ul, (systemFrequency + 500000) / 1000000);
            Action apply = () =>
            {
                try
                {
                    foreach(var cpu in machine.SystemBus.GetCPUs().OfType<BaseCPU>())
                    {
                        if(cpu.PerformanceInMips != mips) cpu.PerformanceInMips = mips;
                    }
                }
                catch(ObjectDisposedException)
                {
                    // The machine was disposed before its next synchronization.
                }
            };
            var timeSource = machine.LocalTimeSource;
            if(timeSource == null || !machine.SystemBus.GetCPUs().OfType<BaseCPU>().Any(cpu => cpu.OnPossessedThread)) apply();
            else timeSource.ExecuteInNearestSyncedState(_ => apply());
        }

        // Brings the CPU that issued the current bus access up to its own
        // time, so a register reflects every cycle it has executed. A
        // debug-port access, a DMA access and an access made off that CPU's
        // thread have no CPU time of their own to bring forward.
        public static void SyncInitiator(IMachine machine)
        {
            if(TryGetInitiator(machine, out var cpu)) cpu.SyncTime();
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
