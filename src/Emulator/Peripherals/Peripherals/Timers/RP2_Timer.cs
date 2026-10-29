//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    [GPIO(NumberOfInputs = 2)]
    public sealed class RP2_Timer : RP2APBPeripheral, IHasFrequency, IGPIOReceiver, INumberedGPIOOutput
    {
        public RP2_Timer(IMachine machine, ulong frequency = 1000000, bool clocked = false, bool rp2350 = false) : base(machine)
        {
            this.frequency = frequency;
            this.clocked = clocked;
            this.rp2350 = rp2350;
            interruptOffset = rp2350 ? 0x3c : 0x34;
            counter = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "counter",
                direction: Direction.Ascending);
            alarmTimers = new LimitTimer[NumberOfAlarms];
            alarmValues = new uint[NumberOfAlarms];
            debugActive = new bool[2];
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < NumberOfAlarms; i++)
            {
                var index = i;
                connections.Add(i, new GPIO());
                alarmTimers[i] = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, $"alarm{i}",
                    limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
                alarmTimers[i].LimitReached += () => FireAlarm(index);
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            Reset();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= debugActive.Length)
            {
                return;
            }
            SynchronizeTime();
            debugActive[number] = value;
            UpdateClock();
        }

        public void SetTickClock(ulong tickFrequency, bool enabled)
        {
            SynchronizeTime();
            frequency = tickFrequency;
            clocked = enabled;
            UpdateClock();
        }

        public override void Reset()
        {
            counter.Reset();
            foreach(var alarm in alarmTimers)
            {
                alarm.Reset();
            }
            Array.Clear(alarmValues, 0, alarmValues.Length);
            // Tick clock inputs belong to their source's reset domain.
            // Resetting this timer resets its counter, not the incoming clock.
            pendingLowTime = 0;
            latchedHighTime = 0;
            armed = 0;
            interruptStatus = 0;
            interruptEnable = 0;
            interruptForce = 0;
            debugPause = 7;
            paused = false;
            locked = false;
            source = 0;
            UpdateClock();
            UpdateInterrupts();
        }

        public ulong Frequency
        {
            get => frequency;
            set
            {
                SynchronizeTime();
                frequency = value;
                UpdateClock();
            }
        }

        public bool Clocked
        {
            get => clocked;
            set
            {
                SynchronizeTime();
                clocked = value;
                UpdateClock();
            }
        }

        public ulong SystemFrequency
        {
            get => systemFrequency;
            set
            {
                SynchronizeTime();
                systemFrequency = value;
                UpdateClock();
            }
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        protected override void OnResetHeldChanged()
        {
            UpdateClock();
        }

        protected override uint ReadRegister(long offset)
        {
            SynchronizeTime();
            if(offset >= 0x10 && offset < 0x20)
            {
                return alarmValues[(offset - 0x10) / 4];
            }
            if(offset >= interruptOffset && offset < interruptOffset + 16)
            {
                switch(offset - interruptOffset)
                {
                case 0: return interruptStatus;
                case 4: return interruptEnable;
                case 8: return interruptForce;
                case 12: return (interruptStatus & interruptEnable) | interruptForce;
                }
            }
            switch(offset)
            {
            case 0x0:
            case 0x4:
                return 0;
            case 0x8:
                return latchedHighTime;
            case 0xc:
                var value = counter.Value;
                latchedHighTime = (uint)(value >> 32);
                return (uint)value;
            case 0x20:
                return armed;
            case 0x24:
                return (uint)(counter.Value >> 32);
            case 0x28:
                return (uint)counter.Value;
            case 0x2c:
                return debugPause;
            case 0x30:
                return paused ? 1u : 0;
            case 0x34:
                if(rp2350)
                {
                    return locked ? 1u : 0;
                }
                break;
            case 0x38:
                if(rp2350)
                {
                    return source;
                }
                break;
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            SynchronizeTime();
            if(locked)
            {
                return;
            }
            if(offset >= 0x10 && offset < 0x20)
            {
                var index = (int)((offset - 0x10) / 4);
                alarmValues[index] = ApplyOperation(alarmValues[index], value, operation);
                armed |= 1u << index;
                ScheduleAlarm(index);
                return;
            }
            if(offset >= interruptOffset && offset < interruptOffset + 16)
            {
                switch(offset - interruptOffset)
                {
                case 0: interruptStatus &= ~(value & 0xf); break;
                case 4: interruptEnable = ApplyOperation(interruptEnable, value, operation, 0xf); break;
                case 8: interruptForce = ApplyOperation(interruptForce, value, operation, 0xf); break;
                }
                UpdateInterrupts();
                return;
            }
            switch(offset)
            {
            case 0x0:
                counter.Value = ((ulong)value << 32) | pendingLowTime;
                RescheduleAlarms();
                return;
            case 0x4:
                pendingLowTime = value;
                return;
            case 0x8:
            case 0xc:
            case 0x24:
            case 0x28:
                return;
            case 0x20:
                armed &= ~(value & 0xf);
                RescheduleAlarms();
                return;
            case 0x2c:
                debugPause = ApplyOperation(debugPause, value, operation, 6);
                UpdateClock();
                return;
            case 0x30:
                paused = ApplyOperation(paused ? 1u : 0, value, operation, 1) != 0;
                UpdateClock();
                return;
            case 0x34:
                if(rp2350)
                {
                    locked = (value & 1) != 0;
                    return;
                }
                break;
            case 0x38:
                if(rp2350)
                {
                    source = ApplyOperation(source, value, operation, 1);
                    UpdateClock();
                    return;
                }
                break;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private void UpdateClock()
        {
            var effectiveFrequency = source != 0 ? systemFrequency : frequency;
            if(effectiveFrequency != 0)
            {
                counter.Frequency = effectiveFrequency;
                foreach(var timer in alarmTimers)
                {
                    timer.Frequency = effectiveFrequency;
                }
            }
            counter.Enabled = !ResetHeld && effectiveFrequency != 0 && (source != 0 || clocked) && !paused
                && !(debugActive[0] && (debugPause & 2) != 0)
                && !(debugActive[1] && (debugPause & 4) != 0);
            RescheduleAlarms();
        }

        private void ScheduleAlarm(int index)
        {
            var timer = alarmTimers[index];
            timer.Enabled = false;
            if(!counter.Enabled || (armed & (1u << index)) == 0)
            {
                return;
            }
            var distance = unchecked(alarmValues[index] - (uint)counter.Value);
            if(distance == 0)
            {
                FireAlarm(index);
                return;
            }
            timer.Limit = distance;
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void RescheduleAlarms()
        {
            for(var index = 0; index < alarmTimers.Length; index++)
            {
                ScheduleAlarm(index);
            }
        }

        private void FireAlarm(int index)
        {
            armed &= ~(1u << index);
            interruptStatus |= 1u << index;
            UpdateInterrupts();
        }

        private void UpdateInterrupts()
        {
            var pending = (interruptStatus & interruptEnable) | interruptForce;
            for(var index = 0; index < NumberOfAlarms; index++)
            {
                Connections[index].Set((pending & (1u << index)) != 0);
            }
        }

        private void SynchronizeTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
            }
        }

        private ulong frequency;
        private ulong systemFrequency;
        private bool clocked;
        private uint pendingLowTime;
        private uint latchedHighTime;
        private uint armed;
        private uint interruptStatus;
        private uint interruptEnable;
        private uint interruptForce;
        private uint debugPause;
        private bool paused;
        private bool locked;
        private uint source;

        private readonly bool rp2350;
        private readonly long interruptOffset;
        private readonly LimitTimer counter;
        private readonly LimitTimer[] alarmTimers;
        private readonly uint[] alarmValues;
        private readonly bool[] debugActive;

        private const int NumberOfAlarms = 4;
    }
}
