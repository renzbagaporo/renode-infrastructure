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
        public RP2_PadsBank0(IMachine machine, RP2_GPIOBank0 gpio, bool rp2350 = false, bool rp2350A2PadRemap = false) : base(machine)
        {
            this.gpio = gpio;
            this.rp2350 = rp2350;
            this.rp2350A2PadRemap = rp2350 && rp2350A2PadRemap;
            pads = new uint[rp2350 ? 50 : 32];
            Reset();
        }

        public override void Reset()
        {
            lock(SyncRoot)
            {
                voltageSelect = 0;
                for(var pin = 0; pin < pads.Length; pin++)
                {
                    // The RP2040's final two registers are dedicated debug pads.
                    var reset = rp2350 ? pin < 48 ? 0x116u : 0x5au
                        : pin == 30 ? 0xdau : pin == 31 ? 0x5au : 0x56u;
                    // ISO belongs to the switched-domain isolation control and
                    // is retained across the PADS APB register-block reset.
                    pads[pin] = rp2350 && ResetHeld ? (reset & ~0x100u) | (pads[pin] & 0x100u) : reset;
                    if(rp2350 && !ResetHeld)
                    {
                        gpio.ResetPadConfiguration(pin, pads[pin]);
                    }
                    else
                    {
                        gpio.SetPadConfiguration(pin, pads[pin]);
                    }
                }
            }
            gpio.PublishPending();
        }

        protected override object SyncRoot => gpio.SynchronizationRoot;

        protected override void OnAccessCompleted() => gpio.PublishPending();

        protected override uint ReadRegister(long offset)
        {
            if(offset == 0)
            {
                return voltageSelect;
            }
            var index = (int)(offset / 4) - 1;
            if(offset > 0 && index < pads.Length && (offset & 3) == 0)
            {
                return CanAccessPad(index) ? pads[index] : 0;
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
                if(!CanAccessPad(index)) return;
                pads[index] = ApplyOperation(pads[index], value, operation, rp2350 ? 0x1ffu : 0xffu);
                gpio.SetPadConfiguration(index, pads[index]);
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private bool CanAccessPad(int pin)
        {
            // GPIO_NSMASK gates user GPIO pad registers. Global voltage/debug
            // pad registers remain subject to the PADS block access control.
            if(!rp2350 || pin >= 48) return true;
            var securityPin = rp2350A2PadRemap && pin < A2RemappedPins.Length ? A2RemappedPins[pin] : pin;
            return gpio.CanAccessPin(securityPin);
        }

        // RP2350-E3: QFN-60 A2 pad registers use the physical-pad NS mask.
        private static readonly int[] A2RemappedPins = {
            8, 9, 10, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23, 24,
            28, 29, 30, 33, 34, 35, 36, 37, 38, 39, 44, 45, 46, 47,
        };

        private readonly bool rp2350A2PadRemap;
        private uint voltageSelect;

        private readonly RP2_GPIOBank0 gpio;
        private readonly bool rp2350;
        private readonly uint[] pads;
    }
}
