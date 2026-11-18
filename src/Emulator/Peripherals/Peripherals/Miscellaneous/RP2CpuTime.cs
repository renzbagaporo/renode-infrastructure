//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public static class RP2CpuTime
    {
        // The cores execute one instruction per clk_sys cycle, as RP2040's
        // Cortex-M0+ and RP2350's Cortex-M33 and Hazard3 roughly do. A core
        // converts the time it is granted to instructions at the start of its
        // round and the instructions it ran back to time at the end, so a
        // change a core makes while it executes applies at the next
        // synchronization point, between rounds. The clocks model passes
        // itself, so only its own chip's cores follow its clk_sys.
        public static void SetCoreClock(IMachine machine, ulong systemFrequency, IPeripheral chipMember = null)
        {
            if(systemFrequency == 0) return;
            var mips = (uint)Math.Max(1ul, (systemFrequency + 500000) / 1000000);
            Action apply = () =>
            {
                try
                {
                    foreach(var cpu in Cores(machine, chipMember))
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
            if(timeSource == null || !Cores(machine, chipMember).Any(cpu => cpu.OnPossessedThread)) apply();
            else timeSource.ExecuteInNearestSyncedState(_ => apply());
        }

        private static IEnumerable<BaseCPU> Cores(IMachine machine, IPeripheral chipMember) =>
            chipMember == null ? machine.SystemBus.GetCPUs().OfType<BaseCPU>() : RP2_Chip.Cores(machine, chipMember);

        // Brings the CPU that issued the current bus access up to its own
        // time, so a register reflects every cycle it has executed. A
        // debug-port access, a DMA access and an access made off that CPU's
        // thread have no CPU time of their own to bring forward.
        public static void SyncInitiator(IMachine machine)
        {
            if(TryGetInitiator(machine, out var cpu)) cpu.SyncTime();
        }

        // The time the CPU making the current bus access has reached, to the
        // instruction (TranslationCPU.OwnElapsedTime); null without one.
        public static TimeInterval? InitiatorTime(IMachine machine)
        {
            if(!TryGetInitiator(machine, out var cpu) || cpu.TimeHandle == null) return null;
            return cpu is TranslationCPU translation ? translation.OwnElapsedTime : cpu.TimeHandle.TotalElapsedTime;
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

        // A read of `offset` on `peripheral` that returned `value`. When the
        // same CPU read the same register from the same PC, got the same
        // value and executed at most a few instructions since, it is polling
        // in a tight loop: its time skips ahead without executing, up to
        // `stableFor` (how long the value cannot change; null when the
        // peripheral cannot tell) and never past the CPU's next
        // synchronization point (TranslationCPU.SkipWhilePolling). A register
        // that changes on its own schedule (a counter, with `stableFor` until
        // its next count) is polled whatever value each read returned.
        public static void SkipIfPolling(IMachine machine, IPeripheral peripheral, long offset, uint value, TimeInterval? stableFor = null)
        {
            if(!TryGetInitiator(machine, out var initiator) || !(initiator is TranslationCPU cpu)) return;
            var executed = cpu.ExecutedInstructions;
            var pc = (ulong)cpu.PC;
            var poll = polls.GetOrCreateValue(cpu);
            var spinning = ReferenceEquals(poll.Peripheral, peripheral) && poll.Offset == offset
                && (stableFor.HasValue || poll.Value == value) && poll.PC == pc && executed - poll.Executed <= SpinInstructions;
            poll.Peripheral = peripheral;
            poll.Offset = offset;
            poll.Value = value;
            poll.PC = pc;
            poll.Executed = executed;
            if(spinning) cpu.SkipWhilePolling(stableFor ?? TimeInterval.FromSeconds(1));
        }

        private sealed class Poll
        {
            public IPeripheral Peripheral;
            public long Offset;
            public uint Value;
            public ulong PC;
            public ulong Executed;
        }

        // Only this CPU's thread uses its entry.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TranslationCPU, Poll> polls =
            new System.Runtime.CompilerServices.ConditionalWeakTable<TranslationCPU, Poll>();

        private const ulong SpinInstructions = 16;
    }
}
