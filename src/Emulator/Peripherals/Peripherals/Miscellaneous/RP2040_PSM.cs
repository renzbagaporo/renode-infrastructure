//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2040_PSM : RP2_PSM
    {
        public RP2040_PSM(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1)
            : base(machine, core0, core1, nvic0, nvic1, processor0Bit: 15, domainMask: 0x1ffff)
        {
        }
    }
}
