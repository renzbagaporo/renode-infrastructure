//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SPI;


namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    // XIP aliases consult the serial controller and NOR state for each bus
    // transaction, including instruction fetches.
    public sealed class RP2040_XIP : RP2APBPeripheral
    {
        public RP2040_XIP(IMachine machine, RP2040_SSI ssi, WinbondW25QFlash flash) : base(machine)
        {
            this.ssi = ssi;
            this.flash = flash;
            ssi.ConfigurationChanged += UpdateAvailability;
            flash.StateChanged += UpdateAvailability;
            Reset();
        }

        public override void Reset()
        {
            control = 3;
            flushReady = false;
            UpdateAvailability();
        }

        public bool Available
        {
            get
            {
                var available = false;
                machine.ClockSource.ExecuteInLock(() => available =
                    ssi.TryGetXIPConfiguration(out var serialControl, out var frameFormat)
                    && FlashAcceptsRead(serialControl, frameFormat));
                return available;
            }
        }

        public event Action AvailabilityChanged;

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0: return control;
            case 4: return flushReady ? 1u : 0;
            case 8: return 2u | (flushReady ? 1u : 0);
            case 0xc:
            case 0x10: return 0;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0:
                // Disabling the cache leaves uncached flash accesses available.
                control = ApplyOperation(control, value, operation, 0xb);
                break;
            case 4:
                // NOR writes invalidate the shared mapped memory's translated
                // blocks; there are no retained data-cache lines to flush.
                flushReady = true;
                break;
            case 0xc:
            case 0x10:
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private bool FlashAcceptsRead(uint serialControl, uint frameFormat)
        {
            if(!flash.ReadAvailable) return false;
            var instructionLength = (serialControl >> 8) & 3;
            var command = (serialControl >> 24) & 0xff;
            if(frameFormat == 0)
            {
                return !flash.ContinuousReadEnabled && instructionLength == 2 && (command == 3 || command == 0xb);
            }
            if(frameFormat != 2 || !flash.QuadEnabled) return false;
            if(instructionLength == 0) return flash.ContinuousReadEnabled;
            return !flash.ContinuousReadEnabled && instructionLength == 2 && (command == 0x6b || command == 0xeb);
        }

        private void UpdateAvailability()
        {
            AvailabilityChanged?.Invoke();
        }

        private readonly RP2040_SSI ssi;
        private readonly WinbondW25QFlash flash;
        private uint control;
        private bool flushReady;
    }
}
