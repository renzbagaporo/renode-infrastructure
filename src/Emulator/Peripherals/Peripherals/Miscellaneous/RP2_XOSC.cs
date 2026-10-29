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
    public sealed class RP2_XOSC : RP2APBPeripheral
    {
        public RP2_XOSC(IMachine machine, ulong crystalFrequency = 12000000, bool rp2350 = false) : base(machine)
        {
            this.crystalFrequency = crystalFrequency;
            countOffset = rp2350 ? 0x10 : 0x1c;
            countMask = rp2350 ? 0xffffu : 0xffu;
            maximumRange = rp2350 ? 0xaa3u : 0xaa0u;
            startupTimer = new LimitTimer(machine.ClockSource, crystalFrequency, this, "startup", limit: 1,
                workMode: WorkMode.OneShot, eventEnabled: true);
            startupTimer.LimitReached += () =>
            {
                stable = true;
                ClockChanged?.Invoke();
            };
            countTimer = new LimitTimer(machine.ClockSource, crystalFrequency, this, "count", limit: 1,
                workMode: WorkMode.OneShot);
            Reset();
        }

        public override void Reset()
        {
            control = 0;
            startup = 0;
            dormant = false;
            stable = false;
            badWrite = false;
            startupTimer.Reset();
            countTimer.Reset();
            ClockChanged?.Invoke();
        }

        public ulong Frequency => stable ? crystalFrequency : 0;

        public event Action ClockChanged;

        protected override uint ReadRegister(long offset)
        {
            if(offset == countOffset)
            {
                return countTimer.Enabled ? (uint)countTimer.Value : 0;
            }
            switch(offset)
            {
            case 0x0:
                return control;
            case 0x4:
                return (stable ? 1u << 31 : 0) | (badWrite ? 1u << 24 : 0)
                    | (IsEnabled ? 1u << 12 : 0) | (control & 3);
            case 0x8:
                return dormant ? DormantValue : WakeValue;
            case 0xc:
                return startup;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset == countOffset)
            {
                countTimer.Enabled = false;
                var count = value & countMask;
                if(count != 0)
                {
                    countTimer.Limit = count;
                    countTimer.ResetValue();
                    countTimer.Enabled = IsEnabled && !dormant;
                }
                return;
            }
            switch(offset)
            {
            case 0x0:
                var next = ApplyOperation(control, value, operation, 0xffffff);
                var enable = (next >> 12) & 0xfff;
                var range = next & 0xfff;
                if(enable != EnableValue && enable != DisableValue)
                {
                    badWrite = true;
                    next = (next & 0xfff) | (control & 0xfff000);
                }
                if(range < 0xaa0 || range > maximumRange)
                {
                    badWrite = true;
                    next = (next & 0xfff000) | (control & 0xfff);
                }
                var wasEnabled = IsEnabled;
                control = next;
                if(wasEnabled != IsEnabled)
                {
                    UpdateRunning();
                }
                break;
            case 0x4:
                if((value & (1u << 24)) != 0)
                {
                    badWrite = false;
                }
                break;
            case 0x8:
                if(value == DormantValue || value == WakeValue)
                {
                    dormant = value == DormantValue;
                    UpdateRunning();
                }
                break;
            case 0xc:
                startup = ApplyOperation(startup, value, operation, 0x103fff);
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private void UpdateRunning()
        {
            startupTimer.Enabled = false;
            stable = false;
            if(IsEnabled && !dormant)
            {
                var cycles = (ulong)(startup & 0x3fff) * 256;
                if((startup & (1u << 20)) != 0)
                {
                    cycles *= 4;
                }
                if(cycles == 0)
                {
                    stable = true;
                }
                else
                {
                    startupTimer.Limit = cycles;
                    startupTimer.ResetValue();
                    startupTimer.Enabled = true;
                }
            }
            ClockChanged?.Invoke();
        }

        private bool IsEnabled => ((control >> 12) & 0xfff) == EnableValue;

        private uint control;
        private uint startup;
        private bool dormant;
        private bool stable;
        private bool badWrite;

        private readonly long countOffset;
        private readonly uint countMask;
        private readonly uint maximumRange;
        private readonly ulong crystalFrequency;
        private readonly LimitTimer startupTimer;
        private readonly LimitTimer countTimer;

        private const uint EnableValue = 0xfab;
        private const uint DisableValue = 0xd1e;
        private const uint DormantValue = 0x636f6d61;
        private const uint WakeValue = 0x77616b65;
    }
}
