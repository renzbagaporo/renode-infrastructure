//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2350_PSM : RP2_PSM, IPeripheralContainer<IPeripheral, NumberRegistrationPoint<int>>
    {
        public RP2350_PSM(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1)
            : base(machine, core0, core1, nvic0, nvic1, processor0Bit: Processor0Bit, domainMask: 0x1ffffff)
        {
        }

        public void Register(IPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            var domain = registrationPoint.Address;
            if(domain < 0 || domain >= Processor0Bit || domains.ContainsKey(peripheral))
            {
                throw new RegistrationException("Invalid or repeated RP2350 power domain registration");
            }
            domains.Add(peripheral, domain);
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Unregister(IPeripheral peripheral)
        {
            if(!domains.Remove(peripheral)) throw new RegistrationException("Peripheral has no RP2350 power domain");
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        public IEnumerable<NumberRegistrationPoint<int>> GetRegistrationPoints(IPeripheral peripheral) =>
            domains.Where(entry => entry.Key == peripheral).Select(entry => new NumberRegistrationPoint<int>(entry.Value));

        public IEnumerable<IRegistered<IPeripheral, NumberRegistrationPoint<int>>> Children =>
            domains.Select(entry => Registered.Create(entry.Key, new NumberRegistrationPoint<int>(entry.Value)));

        public void ResetFromWatchdog(RP2350_Watchdog watchdog)
        {
            uint selected;
            int generation;
            KeyValuePair<IPeripheral, int>[] peripherals;
            lock(SyncRoot)
            {
                selected = ExpandWatchdogSelection(watchdogSelect);
                generation = resetGeneration;
                peripherals = domains.ToArray();
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
                var paused = cores.Select(core => core.IsPaused && !machine.IsPaused).ToArray();
                // Domain resets run after watchdog/clock callbacks release their
                // state locks and while processors cannot receive a time grant.
                using(machine.ObtainPausedState(true))
                {
                    if((selected & (1u << 4)) == 0)
                    {
                        foreach(var controller in peripherals.Select(entry => entry.Key).OfType<RP2_Resets>())
                        {
                            controller.ResetFromWatchdog();
                        }
                    }
                    for(var domain = 0; domain <= Processor0Bit + 1; domain++)
                    {
                        if((selected & (1u << domain)) == 0) continue;
                        if(domain >= Processor0Bit)
                        {
                            var core = domain - Processor0Bit;
                            nvics[core].Reset();
                            cores[core].Reset();
                            // Normalize Reset's pause request even when an
                            // independent hardware/debug hold prevents execution.
                            if(resume[core] || cores[core].HasAnyHaltingCondition) cores[core].Resume();
                            continue;
                        }
                        foreach(var peripheral in peripherals.Where(entry => entry.Value == domain).Select(entry => entry.Key))
                        {
                            peripheral.Reset();
                        }
                    }
                }
                // Machine's temporary pause may resume an individually paused
                // own-life. Restore host pauses within this synchronization phase.
                for(var core = 0; core < cores.Length; core++)
                {
                    if(paused[core]) cores[core].Pause();
                }
            });
        }

        private static uint ExpandWatchdogSelection(uint selected)
        {
            uint result = 0;
            for(var domain = 0; domain <= Processor0Bit + 1; domain++)
            {
                if((selected & (1u << domain)) == 0) continue;
                // Datasheet 7.4/Figure 30: stages 0..9 precede the SRAM group,
                // then XIP, SIO, ACCESSCTRL and the processor complex. Individual
                // SRAM controllers and processors within their groups are modeled
                // as independent peers; additional peer dependencies are unspecified.
                if(domain < 10 || domain >= 20 && domain < Processor0Bit)
                {
                    result |= DomainMask & ~((1u << domain) - 1);
                }
                else if(domain < 20)
                {
                    result |= (1u << domain) | 0x1f00000;
                }
                else result |= 1u << domain;
            }
            return result;
        }

        // Functional reset sequencing; no cycle-level assertion/release latency.
        private readonly Dictionary<IPeripheral, int> domains = new Dictionary<IPeripheral, int>();
        private const int Processor0Bit = 23;
        private const uint DomainMask = 0x1ffffff;
    }
}
