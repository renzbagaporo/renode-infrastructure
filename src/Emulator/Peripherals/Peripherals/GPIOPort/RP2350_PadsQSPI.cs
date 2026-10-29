//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public sealed class RP2350_PadsQSPI : RP2APBPeripheral
    {
        public RP2350_PadsQSPI(IMachine machine, RP2350_QSPI gpio) : base(machine)
        {
            this.gpio = gpio;
            pads = new uint[6];
            Reset();
        }

        public override void Reset()
        {
            lock(SyncRoot)
            {
                ResetPads();
            }
            gpio.PublishPending();
        }

        protected override object SyncRoot => gpio.SynchronizationRoot;

        protected override void OnAccessCompleted() => gpio.PublishPending();

        private void ResetPads()
        {
            voltageSelect = 0;
            for(var pin = 0; pin < pads.Length; pin++)
            {
                var reset = pin < 3 ? 0x156u : 0x15au;
                pads[pin] = ResetHeld ? (reset & ~0x100u) | (pads[pin] & 0x100u) : reset;
                if(!ResetHeld) gpio.ResetPadConfiguration(PinMap[pin], pads[pin]);
                else gpio.SetPadConfiguration(PinMap[pin], pads[pin]);
            }
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset == 0)
            {
                return gpio.IsSecureAccess ? voltageSelect : 0;
            }
            var index = (int)(offset / 4) - 1;
            if(offset > 0 && index < pads.Length && (offset & 3) == 0)
            {
                return gpio.CanAccessPin(PinMap[index]) ? pads[index] : 0;
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset == 0)
            {
                if(!gpio.IsSecureAccess) return;
                voltageSelect = ApplyOperation(voltageSelect, value, operation, 1);
                return;
            }
            var index = (int)(offset / 4) - 1;
            if(offset > 0 && index < pads.Length && (offset & 3) == 0)
            {
                if(!gpio.CanAccessPin(PinMap[index])) return;
                pads[index] = ApplyOperation(pads[index], value, operation, 0x1ff);
                gpio.SetPadConfiguration(PinMap[index], pads[index]);
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private static readonly int[] PinMap = { 2, 4, 5, 6, 7, 3 };
        private uint voltageSelect;
        private readonly uint[] pads;
        private readonly RP2350_QSPI gpio;
    }
}
