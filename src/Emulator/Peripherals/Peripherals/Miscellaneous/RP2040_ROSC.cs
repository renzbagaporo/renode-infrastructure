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
    public sealed class RP2040_ROSC : RP2APBPeripheral
    {
        // The free-running ring frequency is board/temperature dependent. This
        // parameter specifies the nominal ring frequency before the divider.
        public RP2040_ROSC(IMachine machine, ulong ringFrequency = 96000000) : base(machine)
        {
            this.ringFrequency = ringFrequency;
            countTimer = new LimitTimer(machine.ClockSource, ringFrequency, this, "count", limit: 1,
                workMode: WorkMode.OneShot, eventEnabled: true);
            countTimer.LimitReached += () => countArmed = false;
            Reset();
        }

        public override void Reset()
        {
            control = 0xfabaa0;
            frequencyA = 0;
            frequencyB = 0;
            random = 0x3f04b16d;
            divider = 0xab0;
            phase = 8;
            dormant = false;
            badWrite = false;
            countArmed = false;
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
                return dormant ? 0x636f6d61u : 0x77616b65u;
            case 0x10:
                return divider;
            case 0x14:
                return phase;
            case 0x18:
                return (Frequency != 0 ? 0x80010000u : 0) | (IsEnabled ? 0x1000u : 0) | (badWrite ? 1u << 24 : 0);
            case 0x1c:
                var bit = random & 1;
                random = (random >> 1) ^ (bit != 0 ? 0x80200003u : 0);
                return bit;
            case 0x20:
                if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
                {
                    cpu.SyncTime();
                }
                return countArmed ? (uint)countTimer.Value : 0;
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
                    frequencyA = nextFrequency & 0x7777;
                }
                else
                {
                    frequencyB = nextFrequency & 0x7777;
                }
                break;
            case 0xc:
                if(value != 0x636f6d61 && value != 0x77616b65)
                {
                    badWrite = true;
                }
                dormant = value == 0x636f6d61;
                break;
            case 0x10:
                divider = ApplyOperation(divider, value, operation, 0xfff);
                if((divider & 0xfe0) != 0xaa0)
                {
                    divider = 0xabf;
                    badWrite = true;
                }
                break;
            case 0x14:
                var nextPhase = ApplyOperation(phase, value, operation, 0xfff);
                if(((nextPhase >> 4) & 0xff) != 0xaa)
                {
                    badWrite = true;
                    return;
                }
                phase = nextPhase & 0xf;
                break;
            case 0x18:
                if((value & (1u << 24)) != 0)
                {
                    badWrite = false;
                }
                return;
            case 0x20:
                countTimer.Enabled = false;
                var count = value & 0xff;
                countArmed = count != 0;
                if(countArmed)
                {
                    countTimer.Limit = count;
                    countTimer.ResetValue();
                }
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                return;
            }
            if(countArmed)
            {
                countTimer.Enabled = Frequency != 0;
                if(Frequency != 0)
                {
                    countTimer.Frequency = Frequency;
                }
            }
            ClockChanged?.Invoke();
        }

        private bool IsEnabled => ((control >> 12) & 0xfff) != 0xd1e;

        private uint Divider => (divider & 0x1f) == 0 ? 32 : divider & 0x1f;

        private uint control;
        private uint frequencyA;
        private uint frequencyB;
        private uint random;
        private uint divider;
        private uint phase;
        private bool dormant;
        private bool badWrite;
        private bool countArmed;

        private readonly ulong ringFrequency;
        private readonly LimitTimer countTimer;
    }
}
