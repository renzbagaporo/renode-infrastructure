//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    public sealed class RP2040_RTC : RP2APBPeripheral
    {
        public RP2040_RTC(IMachine machine, RP2040_Clocks clocks = null, ulong frequency = 46875) : base(machine)
        {
            this.clocks = clocks;
            this.frequency = frequency;
            IRQ = new GPIO();
            secondTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "second",
                limit: 1, workMode: WorkMode.Periodic, eventEnabled: true);
            secondTimer.LimitReached += Tick;
            controlTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "control synchronization",
                limit: 2, workMode: WorkMode.OneShot, eventEnabled: true);
            controlTimer.LimitReached += () =>
            {
                active = (control & 1) != 0;
                UpdateClock();
            };
            if(clocks != null)
            {
                clocks.ClockChanged += UpdateClock;
            }
            Reset();
        }

        public override void Reset()
        {
            secondTimer.Reset();
            controlTimer.Reset();
            divider = setupDate = setupTime = control = date = time = latchedDate = alarmDate = alarmTime = 0;
            interruptEnable = interruptForce = 0;
            active = false;
            UpdateClock();
        }

        public GPIO IRQ { get; }

        protected override uint ReadRegister(long offset)
        {
            SynchronizeTime();
            switch(offset)
            {
            case 0: return divider;
            case 4: return setupDate;
            case 8: return setupTime;
            case 0xc: return control | (active ? 2u : 0);
            case 0x10: return alarmDate | ((alarmDate & 0x10000000) != 0 ? 0x20000000u : 0);
            case 0x14: return alarmTime;
            case 0x18: return latchedDate;
            case 0x1c:
                latchedDate = date;
                return time;
            case 0x20: return MatchesAlarm ? 1u : 0;
            case 0x24: return interruptEnable;
            case 0x28: return interruptForce;
            case 0x2c: return InterruptStatus;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            SynchronizeTime();
            switch(offset)
            {
            case 0:
                divider = ApplyOperation(divider, value, operation, 0xffff);
                secondTimer.Limit = divider + 1;
                break;
            case 4: setupDate = ApplyOperation(setupDate, value, operation, 0x00ffff1f); break;
            case 8: setupTime = ApplyOperation(setupTime, value, operation, 0x071f3f3f); break;
            case 0xc:
                var requested = ApplyOperation(control, value, operation, 0x111);
                if((requested & 0x10) != 0)
                {
                    date = setupDate;
                    time = setupTime;
                    secondTimer.ResetValue();
                }
                if(((control ^ requested) & 1) != 0)
                {
                    controlTimer.Enabled = false;
                    controlTimer.ResetValue();
                    controlTimer.Enabled = true;
                }
                control = requested & ~0x10u;
                break;
            case 0x10: alarmDate = ApplyOperation(alarmDate, value, operation, 0x17ffff1f); break;
            case 0x14: alarmTime = ApplyOperation(alarmTime, value, operation, 0xf71f3f3f); break;
            case 0x24: interruptEnable = ApplyOperation(interruptEnable, value, operation, 1); break;
            case 0x28: interruptForce = ApplyOperation(interruptForce, value, operation, 1); break;
            default: this.LogUnhandledWrite(offset, value); break;
            }
            UpdateClock();
        }

        protected override void OnResetHeldChanged()
        {
            UpdateClock();
        }

        private void UpdateClock()
        {
            var inputFrequency = clocks == null ? frequency : clocks.GetFrequency(9);
            secondTimer.Frequency = controlTimer.Frequency = Math.Max(1ul, inputFrequency);
            secondTimer.Enabled = active && inputFrequency != 0 && !ResetHeld;
            controlTimer.Enabled = active != ((control & 1) != 0) && inputFrequency != 0 && !ResetHeld;
            IRQ.Set(InterruptStatus != 0);
        }

        private void Tick()
        {
            var second = (time & 0x3f) + 1;
            var minute = (time >> 8) & 0x3f;
            var hour = (time >> 16) & 0x1f;
            var weekday = (time >> 24) & 7;
            if(second >= 60)
            {
                second = 0;
                if(++minute >= 60)
                {
                    minute = 0;
                    if(++hour >= 24)
                    {
                        hour = 0;
                        weekday = (weekday + 1) % 7;
                        AdvanceDate();
                    }
                }
            }
            time = (weekday << 24) | (hour << 16) | (minute << 8) | second;
            IRQ.Set(InterruptStatus != 0);
        }

        private void AdvanceDate()
        {
            var day = (date & 0x1f) + 1;
            var month = (date >> 8) & 0xf;
            var year = (date >> 12) & 0xfff;
            var days = month == 2 ? ((control & 0x100) == 0 && year % 4 == 0 ? 29u : 28u)
                : month == 4 || month == 6 || month == 9 || month == 11 ? 30u : 31u;
            if(day > days)
            {
                day = 1;
                if(++month > 12)
                {
                    month = 1;
                    year = (year + 1) & 0xfff;
                }
            }
            date = (year << 12) | (month << 8) | day;
        }

        private bool MatchesAlarm => (alarmDate & 0x10000000) != 0
            && MatchField(date, alarmDate, 0x00fff000, 26)
            && MatchField(date, alarmDate, 0x00000f00, 25)
            && MatchField(date, alarmDate, 0x0000001f, 24)
            && MatchField(time, alarmTime, 0x07000000, 31)
            && MatchField(time, alarmTime, 0x001f0000, 30)
            && MatchField(time, alarmTime, 0x00003f00, 29)
            && MatchField(time, alarmTime, 0x0000003f, 28);

        private uint InterruptStatus => ((MatchesAlarm ? 1u : 0) & interruptEnable) | interruptForce;

        private static bool MatchField(uint current, uint alarm, uint mask, int enableBit)
        {
            return (alarm & (1u << enableBit)) == 0 || (current & mask) == (alarm & mask);
        }

        private void SynchronizeTime()
        {
            if(machine.GetSystemBus(this).TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
            }
        }

        private readonly RP2040_Clocks clocks;
        private readonly ulong frequency;
        private readonly LimitTimer secondTimer;
        private readonly LimitTimer controlTimer;
        private uint divider;
        private uint setupDate;
        private uint setupTime;
        private uint control;
        private uint date;
        private uint time;
        private uint latchedDate;
        private uint alarmDate;
        private uint alarmTime;
        private uint interruptEnable;
        private uint interruptForce;
        private bool active;
    }
}
