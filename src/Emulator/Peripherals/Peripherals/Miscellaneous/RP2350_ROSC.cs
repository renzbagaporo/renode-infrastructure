//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_ROSC : RP2APBPeripheral
    {
        // The free-running ring frequency is board/temperature dependent. This
        // parameter specifies the nominal ring frequency before the divider.
        public RP2350_ROSC(IMachine machine, ulong ringFrequency = 192000000) : base(machine)
        {
            this.ringFrequency = ringFrequency;
            countTimer = new LimitTimer(machine.ClockSource, ringFrequency, this, "count", limit: 1,
                workMode: WorkMode.OneShot);
            Reset();
        }

        public override void Reset()
        {
            control = 0xfabaa0;
            frequencyA = 0x88;
            frequencyB = 0;
            random = 0x3f04b16d;
            divider = 0xaa20;
            phase = 8;
            dormant = false;
            badWrite = false;
            countTimer.Reset();
            ClockChanged?.Invoke();
        }

        public ulong Frequency => IsEnabled && !dormant ? ringFrequency / Divider : 0;

        public event Action ClockChanged;

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0x0:
                return control;
            case 0x4:
                return frequencyA;
            case 0x8:
                return frequencyB;
            case 0xc:
                return random;
            case 0x10:
                return dormant ? 0x636f6d61u : 0x77616b65u;
            case 0x14:
                return divider;
            case 0x18:
                return phase;
            case 0x1c:
                return (Frequency != 0 ? 0x80010000u : 0) | (IsEnabled ? 0x1000u : 0) | (badWrite ? 1u << 24 : 0);
            case 0x20:
                var bit = random & 1;
                random = (random >> 1) ^ (bit != 0 ? 0x80200003u : 0);
                return bit;
            case 0x24:
                return countTimer.Enabled ? (uint)countTimer.Value : 0;
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
                control = ApplyOperation(control, value, operation, 0xffffff);
                var enable = (control >> 12) & 0xfff;
                if(enable != 0xfab && enable != 0xd1e)
                {
                    badWrite = true;
                    control = (control & 0xfff) | 0xfab000;
                }
                break;
            case 0x4:
            case 0x8:
                var nextFrequency = ApplyOperation(offset == 4 ? frequencyA : frequencyB, value, operation);
                if((nextFrequency >> 16) != 0x9696)
                {
                    badWrite = true;
                    return;
                }
                if(offset == 4)
                {
                    frequencyA = nextFrequency & 0x77ff;
                }
                else
                {
                    frequencyB = nextFrequency & 0x7777;
                }
                break;
            case 0xc:
                random = ApplyOperation(random, value, operation);
                break;
            case 0x10:
                if(value != 0x636f6d61 && value != 0x77616b65)
                {
                    return;
                }
                dormant = value == 0x636f6d61;
                break;
            case 0x14:
                divider = ApplyOperation(divider, value, operation, 0xffff);
                if((divider & 0xff80) != 0xaa00)
                {
                    divider = 0xaa00;
                    badWrite = true;
                }
                break;
            case 0x18:
                var nextPhase = ApplyOperation(phase, value, operation, 0xfff);
                if(((nextPhase >> 4) & 0xff) != 0xaa)
                {
                    badWrite = true;
                    return;
                }
                phase = nextPhase & 0xf;
                break;
            case 0x1c:
                if((value & (1u << 24)) != 0)
                {
                    badWrite = false;
                }
                return;
            case 0x24:
                countTimer.Enabled = false;
                var count = value & 0xffff;
                if(count != 0 && Frequency != 0)
                {
                    countTimer.Frequency = Frequency;
                    countTimer.Limit = count;
                    countTimer.ResetValue();
                    countTimer.Enabled = true;
                }
                return;
            default:
                this.LogUnhandledWrite(offset, value);
                return;
            }
            ClockChanged?.Invoke();
        }

        private bool IsEnabled => ((control >> 12) & 0xfff) != 0xd1e;

        private uint Divider => (divider & 0x7f) == 0 ? 128 : divider & 0x7f;

        private uint control;
        private uint frequencyA;
        private uint frequencyB;
        private uint random;
        private uint divider;
        private uint phase;
        private bool dormant;
        private bool badWrite;

        private readonly ulong ringFrequency;
        private readonly LimitTimer countTimer;
    }
}
