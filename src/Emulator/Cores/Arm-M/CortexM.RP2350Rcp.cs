//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Linq;

using Antmicro.Renode.Utilities.Binding;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        [Export]
        private uint RP2350RcpWriteSalt(uint core, ulong salt)
        {
            // Only core 0's coprocessor port is wired to the salt registers.
            if(MultiprocessingId != 0)
            {
                return 0;
            }
            var target = machine.SystemBus.GetCPUs().OfType<CortexM>()
                .SingleOrDefault(cpu => cpu.Model == "cortex-m33-rp2350" && cpu.MultiprocessingId == core);
            if(target == null)
            {
                return 0;
            }
            lock(target.reset)
            {
                // The native writer release-publishes valid after the salt and
                // PRNG seed. The monitor also excludes the target's reset.
                return target.tlibRp2350RcpSetSalt(salt);
            }
        }

        [Export]
        private void RP2350RcpFault(uint panic)
        {
            foreach(var cpu in machine.SystemBus.GetCPUs().OfType<CortexM>()
                .Where(cpu => cpu.Model == "cortex-m33-rp2350"))
            {
                lock(cpu.reset)
                {
                    // Publish the native fault and its NMI level together with
                    // respect to local reset. No other CPU is paused here.
                    cpu.tlibRp2350RcpSetFault();
                    // NVIC input numbering starts at exception 16; NMI is 2.
                    if(panic == 0 || cpu != this)
                    {
                        cpu.nvic.OnGPIO(-14, true);
                    }
                }
            }
        }

#pragma warning disable 649
        // The native binding populates these delegates.
        [Import]
        private readonly Func<ulong, uint> tlibRp2350RcpSetSalt;

        [Import]
        private readonly Action tlibRp2350RcpSetFault;
#pragma warning restore 649
    }
}
