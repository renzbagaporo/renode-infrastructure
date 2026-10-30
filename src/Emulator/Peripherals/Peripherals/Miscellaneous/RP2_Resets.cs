//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2_Resets : RP2APBPeripheral, IPeripheralContainer<IPeripheral, NumberRegistrationPoint<int>>
    {
        public RP2_Resets(IMachine machine, bool rp2350 = false) : base(machine)
        {
            numberOfResetLines = rp2350 ? 29 : 25;
            resetMask = (1u << numberOfResetLines) - 1;
            peripherals = new Dictionary<int, IPeripheral>();
            Reset();
        }

        public void Register(IPeripheral peripheral, NumberRegistrationPoint<int> registrationPoint)
        {
            var index = registrationPoint.Address;
            if(index < 0 || index >= numberOfResetLines || peripherals.ContainsKey(index))
            {
                throw new RegistrationException("Invalid or occupied RP2 reset line");
            }
            if(!(peripheral is IRP2ResettablePeripheral))
            {
                throw new RegistrationException("RP2 reset lines require a peripheral implementing persistent reset hold");
            }
            peripherals.Add(index, peripheral);
            machine.RegisterAsAChildOf(this, peripheral, registrationPoint);
            SetResetState(index, (reset & (1u << index)) != 0);
        }

        public void Unregister(IPeripheral peripheral)
        {
            var indices = peripherals.Where(x => x.Value == peripheral).Select(x => x.Key).ToArray();
            if(indices.Length == 0)
            {
                throw new RegistrationException("Peripheral is not attached to an RP2 reset line");
            }
            foreach(var index in indices)
            {
                peripherals.Remove(index);
            }
            machine.UnregisterAsAChildOf(this, peripheral);
        }

        public IEnumerable<NumberRegistrationPoint<int>> GetRegistrationPoints(IPeripheral peripheral)
        {
            return peripherals.Where(x => x.Value == peripheral).Select(x => new NumberRegistrationPoint<int>(x.Key));
        }

        public override void Reset()
        {
            reset = resetMask;
            watchdogSelect = 0;
            foreach(var index in peripherals.Keys)
            {
                SetResetState(index, true);
            }
        }

        public IEnumerable<IRegistered<IPeripheral, NumberRegistrationPoint<int>>> Children =>
            peripherals.Select(x => Registered.Create(x.Value, new NumberRegistrationPoint<int>(x.Key)));

        public void ResetFromWatchdog()
        {
            IPeripheral[] selected;
            lock(SyncRoot)
            {
                selected = peripherals.Where(entry => (watchdogSelect & (1u << entry.Key)) != 0)
                    .Select(entry => entry.Value).ToArray();
            }
            // A watchdog pulse resets the selected peripheral's flops; the
            // software RESET/WDSEL registers and persistent reset holds remain.
            foreach(var peripheral in selected) peripheral.Reset();
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0x0:
                return reset;
            case 0x4:
                return watchdogSelect;
            case 0x8:
                return ~reset & resetMask;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0x0:
                var previous = reset;
                reset = ApplyOperation(reset, value, operation, resetMask);
                foreach(var index in peripherals.Keys)
                {
                    var mask = 1u << index;
                    if(((previous ^ reset) & mask) != 0)
                    {
                        SetResetState(index, (reset & mask) != 0);
                    }
                }
                break;
            case 0x4:
                watchdogSelect = ApplyOperation(watchdogSelect, value, operation, resetMask);
                break;
            case 0x8:
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private void SetResetState(int index, bool asserted)
        {
            ((IRP2ResettablePeripheral)peripherals[index]).ResetHeld = asserted;
        }

        private uint reset;
        private uint watchdogSelect;

        private readonly Dictionary<int, IPeripheral> peripherals;

        private readonly int numberOfResetLines;
        private readonly uint resetMask;
    }
}
