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

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2040_PSM : RP2_PSM, IPeripheralContainer<IPeripheral, NumberRegistrationPoint<int>>
    {
        public RP2040_PSM(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1)
            : base(machine, core0, core1, nvic0, nvic1, processor0Bit: 15, domainMask: 0x1ffff)
        {
        }

        public void Register(IPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            var domain = registrationPoint.Address;
            if(domain < 0 || domain >= 15 || domains.ContainsKey(peripheral))
            {
                throw new RegistrationException("Invalid or repeated RP2040 power domain registration");
            }
            domains.Add(peripheral, domain);
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
        }

        public void Unregister(IPeripheral peripheral)
        {
            if(!domains.Remove(peripheral)) throw new RegistrationException("Peripheral has no RP2040 power domain");
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        public IEnumerable<NumberRegistrationPoint<int>> GetRegistrationPoints(IPeripheral peripheral) =>
            domains.Where(entry => entry.Key == peripheral).Select(entry => new NumberRegistrationPoint<int>(entry.Value));

        public IEnumerable<IRegistered<IPeripheral, NumberRegistrationPoint<int>>> Children =>
            domains.Select(entry => Registered.Create(entry.Key, new NumberRegistrationPoint<int>(entry.Value)));

        public void ResetFromWatchdog()
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
            var resume = cores.Select(core => !core.IsHalted && !core.CpuWaitSignal.IsSet && !core.HaltedByDebugger
                && (!core.IsPaused || machine.IsPaused)).ToArray();
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                if(generation != Volatile.Read(ref resetGeneration)) return;
                // CPU and peripheral resets run at a synchronization boundary,
                // outside the watchdog/clock/state callback which requested them.
                using(machine.ObtainPausedState(true))
                {
                    if((selected & (1u << 3)) == 0)
                    {
                        foreach(var controller in peripherals.Select(entry => entry.Key).OfType<RP2_Resets>())
                        {
                            controller.ResetFromWatchdog();
                        }
                    }
                    foreach(var domain in ResetOrder)
                    {
                        if((selected & (1u << domain)) == 0) continue;
                        if(domain >= 15)
                        {
                            var core = domain - 15;
                            nvics[core].Reset();
                            cores[core].Reset();
                            if(resume[core] || cores[core].HasAnyHaltingCondition) cores[core].Resume();
                            continue;
                        }
                        foreach(var peripheral in peripherals.Where(entry => entry.Value == domain).Select(entry => entry.Key))
                        {
                            if(peripheral is RP2040_Clocks clocks) clocks.ResetFromWatchdog();
                            else peripheral.Reset();
                        }
                    }
                }
            });
        }

        private static uint ExpandWatchdogSelection(uint selected)
        {
            uint result = 0;
            for(var domain = 0; domain < Descendants.Length; domain++)
            {
                if((selected & (1u << domain)) != 0) result |= Descendants[domain];
            }
            return result;
        }

        private readonly Dictionary<IPeripheral, int> domains = new Dictionary<IPeripheral, int>();

        // RP2040 datasheet 2.13.2/2.13.4: sequence dependencies are not register
        // bit order. Figure 27 groups ROM/SRAM and the processor complex;
        // individual controls within each group are modeled as independent
        // peers. The document does not specify additional peer dependencies.
        // Reset assertion/release is functional and instantaneous here.
        private static readonly int[] ResetOrder = { 0, 1, 2, 3, 13, 12, 5, 6, 7, 8, 9, 10, 11, 4, 14, 15, 16 };
        private static readonly uint[] Descendants =
        {
            0x1ffff, 0x1fffe, 0x1fffc, 0x1fff8,
            0x1c010, 0x1c030, 0x1c050, 0x1c090,
            0x1c110, 0x1c210, 0x1c410, 0x1c810,
            0x1dff0, 0x1fff0, 0x1c000, 0x08000, 0x10000,
        };
    }
}
