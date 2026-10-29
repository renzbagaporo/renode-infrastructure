//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public sealed class RP2040_PadsQSPI : RP2APBPeripheral
    {
        public RP2040_PadsQSPI(IMachine machine, RP2040_QSPI gpio) : base(machine)
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
                pads[pin] = pin == 0 ? 0x56u : 0x5au;
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
                pads[index] = ApplyOperation(pads[index], value, operation, 0xff);
                gpio.SetPadConfiguration(index, pads[index]);
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private uint voltageSelect;
        private readonly uint[] pads;
        private readonly RP2040_QSPI gpio;
    }
}
