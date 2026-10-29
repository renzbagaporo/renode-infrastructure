//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SPI;

using Range = Antmicro.Renode.Core.Range;

namespace Antmicro.Renode.Peripherals.MemoryControllers
{
    // XIP aliases share the NOR backing memory. Removing their CPU mappings
    // when the serial interface cannot issue a flash read also blocks fetches.
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

        public bool Available => ssi.IsXIPReady && FlashAcceptsRead;

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

        private bool FlashAcceptsRead
        {
            get
            {
                if(!flash.ReadAvailable)
                {
                    return false;
                }
                var serialControl = ssi.ReadDoubleWord(0xf4);
                var instructionLength = (serialControl >> 8) & 3;
                var command = (serialControl >> 24) & 0xff;
                var frameFormat = (ssi.ReadDoubleWord(0) >> 21) & 3;
                if(frameFormat == 0)
                {
                    return !flash.ContinuousReadEnabled && instructionLength == 2 && (command == 3 || command == 0xb);
                }
                if(frameFormat != 2 || !flash.QuadEnabled)
                {
                    return false;
                }
                if(instructionLength == 0)
                {
                    return flash.ContinuousReadEnabled;
                }
                return !flash.ContinuousReadEnabled && instructionLength == 2 && (command == 0x6b || command == 0xeb);
            }
        }

        private void UpdateAvailability()
        {
            machine.GetSystemBus(this).SetAddressRangeLocked(new Range(0x10000000, 0x04000000), !Available);
            AvailabilityChanged?.Invoke();
        }

        private readonly RP2040_SSI ssi;
        private readonly WinbondW25QFlash flash;
        private uint control;
        private bool flushReady;
    }
}
