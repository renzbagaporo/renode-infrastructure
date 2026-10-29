//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Threading;
using System.Linq;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2350_PSM : RP2_PSM
    {
        public RP2350_PSM(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1)
            : base(machine, core0, core1, nvic0, nvic1, processor0Bit: Processor0Bit, domainMask: 0x1ffffff)
        {
        }

        public void ResetFromWatchdog(RP2350_Watchdog watchdog)
        {
            uint selected;
            int generation;
            lock(SyncRoot)
            {
                selected = watchdogSelect;
                generation = resetGeneration;
            }
            if((selected & 1) != 0)
            {
                // PROC_COLD restarts the power-on sequence; always-on debug,
                // power scratch and externally powered NOR remain available.
                var retained = machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral)
                    .Where(peripheral => peripheral is RP2350_DebugPort || peripheral is RP2350_POWMAN
                        || peripheral is WinbondW25QFlash || peripheral == watchdog).Distinct().ToList();
                machine.RequestResetInSafeState(unresetable: retained);
                return;
            }
            var resume = cores.Select(core => !core.IsHalted && !core.CpuWaitSignal.IsSet && !core.HaltedByDebugger
                && (!core.IsPaused || machine.IsPaused)).ToArray();
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                if(generation != Volatile.Read(ref resetGeneration)) return;
                for(var core = 0; core < cores.Length; core++)
                {
                    if((selected & (1u << (Processor0Bit + core))) == 0) continue;
                    nvics[core].Reset();
                    cores[core].Reset();
                    // Reset's pause request must be normalized even when an
                    // independent hold prevents the PE from starting a thread.
                    if(resume[core] || cores[core].HasAnyHaltingCondition) cores[core].Resume();
                }
                var unsupported = selected & ~ProcessorMask;
                if(unsupported != 0) this.Log(LogLevel.Warning, "Selective watchdog reset for PSM domains 0x{0:x} is not implemented", unsupported);
            });
        }

        private const int Processor0Bit = 23;
    }
}
