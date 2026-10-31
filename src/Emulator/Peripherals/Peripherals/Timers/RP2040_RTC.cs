//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
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
            irqChanges = new Queue<IRQChange>();
            secondTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "second",
                limit: 1, workMode: WorkMode.Periodic, eventEnabled: true);
            secondTimer.LimitReached += Tick;
            controlTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "control synchronization",
                limit: 2, workMode: WorkMode.OneShot, eventEnabled: true);
            controlTimer.LimitReached += () => CompleteSynchronization(false);
            // Use the RTC's two-cycle synchronization approximation for the
            // alarm enable as well; this is not a clk_sys-accurate CDC model.
            alarmControlTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "alarm synchronization",
                limit: 2, workMode: WorkMode.OneShot, eventEnabled: true);
            alarmControlTimer.LimitReached += () => CompleteSynchronization(true);
            if(clocks != null)
            {
                clocks.ClockChanged += OnClockChanged;
            }
            Reset();
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    secondTimer.Reset();
                    controlTimer.Reset();
                    alarmControlTimer.Reset();
                    divider = setupDate = setupTime = control = date = time = latchedDate = alarmDate = alarmTime = 0;
                    interruptEnable = interruptForce = 0;
                    active = matchActive = alarmRaw = irqState = false;
                    irqChanges.Clear();
                    generation++;
                    irqChanges.Enqueue(new IRQChange(false, generation));
                    UpdateClock();
                }
            });
            PublishIRQChanges();
        }

        public GPIO IRQ { get; }

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess() => SynchronizeTime();

        protected override void OnAccessCompleted() => PublishIRQChanges();

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0: return divider;
            case 4: return setupDate;
            case 8: return setupTime;
            case 0xc: return control | (active ? 2u : 0);
            case 0x10: return alarmDate | (matchActive ? 0x20000000u : 0);
            case 0x14: return alarmTime;
            case 0x18: return latchedDate;
            case 0x1c:
                latchedDate = date;
                return time;
            case 0x20: return alarmRaw ? 1u : 0;
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
            case 0x10:
                var requestedAlarm = ApplyOperation(alarmDate, value, operation, 0x17ffff1f);
                if(((alarmDate ^ requestedAlarm) & 0x10000000) != 0)
                {
                    alarmControlTimer.Enabled = false;
                    alarmControlTimer.ResetValue();
                }
                alarmDate = requestedAlarm;
                break;
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
            secondTimer.Frequency = controlTimer.Frequency = alarmControlTimer.Frequency = Math.Max(1ul, inputFrequency);
            secondTimer.Enabled = active && inputFrequency != 0 && !ResetHeld;
            controlTimer.Enabled = active != ((control & 1) != 0) && inputFrequency != 0 && !ResetHeld;
            alarmControlTimer.Enabled = matchActive != AlarmEnabled && inputFrequency != 0 && !ResetHeld;
            UpdateIRQ();
        }

        private void OnClockChanged()
        {
            SynchronizeTime();
            machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClock(); });
            PublishIRQChanges();
        }

        private void CompleteSynchronization(bool alarm)
        {
            lock(SyncRoot)
            {
                if(alarm)
                {
                    matchActive = AlarmEnabled;
                    if(!matchActive) alarmRaw = false;
                }
                else active = (control & 1) != 0;
                UpdateClock();
            }
            PublishIRQChanges();
        }

        private void Tick()
        {
            lock(SyncRoot)
            {
                if(!active || ResetHeld) return;
                AdvanceCalendar();
                // Sampling the comparison on calendar updates allows software
                // to acknowledge and rearm a repeating alarm in the same second.
                // Exact internal comparator pipeline latency is not modeled.
                alarmRaw = matchActive && MatchesAlarm;
                UpdateIRQ();
            }
            PublishIRQChanges();
        }

        private void AdvanceCalendar()
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

        private bool MatchesAlarm => MatchField(date, alarmDate, 0x00fff000, 26)
            && MatchField(date, alarmDate, 0x00000f00, 25)
            && MatchField(date, alarmDate, 0x0000001f, 24)
            && MatchField(time, alarmTime, 0x07000000, 31)
            && MatchField(time, alarmTime, 0x001f0000, 30)
            && MatchField(time, alarmTime, 0x00003f00, 29)
            && MatchField(time, alarmTime, 0x0000003f, 28);

        private bool AlarmEnabled => (alarmDate & 0x10000000) != 0;

        private uint InterruptStatus => ((alarmRaw ? 1u : 0) & interruptEnable) | interruptForce;

        private void UpdateIRQ()
        {
            var state = !ResetHeld && InterruptStatus != 0;
            if(irqState == state) return;
            irqState = state;
            irqChanges.Enqueue(new IRQChange(state, generation));
        }

        // APB reset may call Reset while still owning SyncRoot. Its completion
        // hook drains the queue after that outer monitor has been released.
        private void PublishIRQChanges()
        {
            if(Monitor.IsEntered(SyncRoot)) return;
            lock(SyncRoot)
            {
                if(publishing) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    IRQChange change;
                    lock(SyncRoot)
                    {
                        if(irqChanges.Count == 0)
                        {
                            publishing = false;
                            return;
                        }
                        change = irqChanges.Dequeue();
                        if(change.Generation != generation) continue;
                    }
                    IRQ.Set(change.State);
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private static bool MatchField(uint current, uint alarm, uint mask, int enableBit)
        {
            return (alarm & (1u << enableBit)) == 0 || (current & mask) == (alarm & mask);
        }

        private void SynchronizeTime()
        {
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu)
            {
                cpu.SyncTime();
            }
        }

        private readonly struct IRQChange
        {
            public IRQChange(bool state, ulong generation)
            {
                State = state;
                Generation = generation;
            }

            public bool State { get; }
            public ulong Generation { get; }
        }

        private readonly RP2040_Clocks clocks;
        private readonly ulong frequency;
        private readonly LimitTimer secondTimer;
        private readonly LimitTimer controlTimer;
        private readonly LimitTimer alarmControlTimer;
        private readonly Queue<IRQChange> irqChanges;
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
        private bool matchActive;
        private bool alarmRaw;
        private bool irqState;
        private bool publishing;
        private ulong generation;
    }
}
