//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public sealed class RP2_PadsBank0 : RP2APBPeripheral
    {
        public RP2_PadsBank0(IMachine machine, RP2_GPIOBank0 gpio, bool rp2350 = false) : base(machine)
        {
            this.gpio = gpio;
            this.rp2350 = rp2350;
            pads = new uint[rp2350 ? 48 : 32];
            Reset();
        }

        public override void Reset()
        {
            voltageSelect = 0;
            for(var pin = 0; pin < pads.Length; pin++)
            {
                // The RP2040's final two registers are dedicated debug pads.
                pads[pin] = rp2350 ? 0x116u : pin == 30 ? 0xdau : pin == 31 ? 0x5au : 0x56u;
                gpio.SetPadConfiguration(pin, pads[pin]);
            }
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset == 0)
            {
                return voltageSelect;
            }
            var index = (int)(offset / 4) - 1;
            if(offset > 0 && index < pads.Length && (offset & 3) == 0)
            {
                return pads[index];
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset == 0)
            {
                voltageSelect = ApplyOperation(voltageSelect, value, operation, 1);
                return;
            }
            var index = (int)(offset / 4) - 1;
            if(offset > 0 && index < pads.Length && (offset & 3) == 0)
            {
                pads[index] = ApplyOperation(pads[index], value, operation, rp2350 ? 0x1ffu : 0xffu);
                gpio.SetPadConfiguration(index, pads[index]);
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private uint voltageSelect;

        private readonly RP2_GPIOBank0 gpio;
        private readonly bool rp2350;
        private readonly uint[] pads;
    }
}
