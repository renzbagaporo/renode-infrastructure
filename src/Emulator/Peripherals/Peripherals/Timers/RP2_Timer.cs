//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Miscellaneous;
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
            counterRunning = false;
            SetCounter(0);
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

        // The CPU's time is brought forward before the access takes the clock
        // and state locks: advancing it runs clock events, which may reach
        // other machines, so it must not happen under this timer's lock.
        protected override void OnBeforeAccess() => SynchronizeTime();

        // A CPU polling the counter reads it millions of times each virtual
        // second, and bringing its time forward is most of a read's cost. That
        // is skipped while the CPU cannot have reached the counter's next tick
        // since its last synchronized read: the machine's time lies between its
        // time then and the CPU's time now, so the read returns the same value.
        // An alarm due inside that tick may then fire up to the CPU's lead later.
        protected override void OnBeforeRead(long offset)
        {
            if(offset != 0xc && offset != 0x24 && offset != 0x28)
            {
                SynchronizeTime();
                return;
            }
            if(!RP2CpuTime.TryGetInitiator(machine, out var cpu)) return;
            // Only this CPU's thread uses its record.
            CounterSync record;
            lock(counterSyncs)
            {
                if(!counterSyncs.TryGetValue(cpu, out record)) counterSyncs[cpu] = record = new CounterSync();
            }
            if(record.Valid && record.Generation == counterGeneration)
            {
                // Every instruction before the synchronized read was reported then.
                var lead = TimeInterval.FromCPUCycles(cpu.ExecutedInstructions - record.Instructions + 2, cpu.PerformanceInMips, out var _);
                if(cpu.TimeHandle.TotalElapsedTime.Ticks + lead.Ticks < record.NextTick) return;
            }
            cpu.SyncTime();
            record.Instructions = cpu.ExecutedInstructions;
            // A change made while the next tick is computed leaves the record stale.
            record.Generation = counterGeneration;
            record.Valid = counterRunning;
            record.NextTick = counterRunning ? NextTickTime() : 0;
        }

        private sealed class CounterSync
        {
            public bool Valid;
            public ulong Instructions, NextTick, Generation;
        }

        protected override uint ReadRegister(long offset)
        {
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
                var value = ReadCounter();
                latchedHighTime = (uint)(value >> 32);
                SkipIfPolling(offset, (uint)value);
                return (uint)value;
            case 0x20:
                return armed;
            case 0x24:
                return (uint)(ReadCounter() >> 32);
            case 0x28:
                var low = (uint)ReadCounter();
                SkipIfPolling(offset, low);
                return low;
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
                SetCounter(((ulong)value << 32) | pendingLowTime);
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
                foreach(var timer in alarmTimers)
                {
                    timer.Frequency = effectiveFrequency;
                }
            }
            var running = !ResetHeld && effectiveFrequency != 0 && (source != 0 || clocked) && !paused
                && !(debugActive[0] && (debugPause & 2) != 0)
                && !(debugActive[1] && (debugPause & 4) != 0);
            var rate = effectiveFrequency != 0 ? effectiveFrequency : counterFrequency;
            if(running != counterRunning || rate != counterFrequency)
            {
                // Counts made at the old rate up to now stay counted.
                SetCounter(CounterValue);
                counterRunning = running;
                counterFrequency = rate;
                counterGeneration++;
            }
            RescheduleAlarms();
        }

        private void ScheduleAlarm(int index)
        {
            var timer = alarmTimers[index];
            timer.Enabled = false;
            if(!counterRunning || (armed & (1u << index)) == 0)
            {
                return;
            }
            var distance = unchecked(alarmValues[index] - (uint)CounterValue);
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

        // The counter is anchored at a value and a clock-source time, and read
        // from the clock source's current time without a clock entry, as a CPU
        // polling it reads it millions of times each virtual second.
        private ulong CounterValue => CounterAt(machine.ClockSource.CurrentValue.Ticks);

        // The count a CPU reads: at its own time, which runs ahead of the
        // clock source within its round. The clock source holds the time
        // every core has reached, so a core running ahead of the other
        // (spinning on the counter, or skipping its polls) would read a count
        // that stands still until the other core caught up, and then jumps.
        private ulong ReadCounter()
        {
            var clock = machine.ClockSource.CurrentValue.Ticks;
            var own = RP2CpuTime.InitiatorTime(machine)?.Ticks ?? clock;
            return CounterAt(Math.Max(own, clock));
        }

        private ulong CounterAt(ulong ticks)
        {
            if(!counterRunning) return counterBase;
            var elapsed = ticks > counterBaseTicks ? ticks - counterBaseTicks : 0;
            return counterBase + (ulong)((UInt128)elapsed * counterFrequency / TimeInterval.TicksPerSecond);
        }

        // A CPU spinning on the counter (busy_wait_us) reads it many times per
        // microsecond; with SkipPollingTime its time runs on to the next
        // count instead (RP2CpuTime.SkipIfPolling). Off by default, as the
        // spinning CPU then executes fewer instructions.
        public bool SkipPollingTime { get; set; }

        private void SkipIfPolling(long offset, uint value)
        {
            if(!SkipPollingTime) return;
            TimeInterval? stableFor = null;
            if(counterRunning)
            {
                // The next count after the polling CPU's own time, which runs
                // ahead of the clock source within its round.
                var now = Math.Max((RP2CpuTime.InitiatorTime(machine) ?? machine.ClockSource.CurrentValue).Ticks, counterBaseTicks);
                var counts = (UInt128)(now - counterBaseTicks) * counterFrequency / TimeInterval.TicksPerSecond;
                var next = counterBaseTicks + (ulong)(((counts + 1) * TimeInterval.TicksPerSecond + counterFrequency - 1) / counterFrequency);
                stableFor = TimeInterval.FromTicks(next > now ? next - now : 0);
            }
            RP2CpuTime.SkipIfPolling(machine, this, offset, value, stableFor);
        }

        private void SetCounter(ulong value)
        {
            counterBase = value;
            counterBaseTicks = machine.ClockSource.CurrentValue.Ticks;
            counterGeneration++;
        }

        // The clock-source time at which the running counter next increments.
        private ulong NextTickTime()
        {
            var next = (UInt128)(CounterValue - counterBase + 1) * TimeInterval.TicksPerSecond;
            return counterBaseTicks + (ulong)((next + counterFrequency - 1) / counterFrequency);
        }

        private void SynchronizeTime()
        {
            RP2CpuTime.SyncInitiator(machine);
        }

        private ulong frequency;
        private ulong systemFrequency;
        private ulong counterBase, counterBaseTicks, counterFrequency;
        private bool counterRunning;
        private ulong counterGeneration;
        // Each CPU's last counter read whose time was brought forward.
        private readonly Dictionary<BaseCPU, CounterSync> counterSyncs = new Dictionary<BaseCPU, CounterSync>();
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
        private readonly LimitTimer[] alarmTimers;
        private readonly uint[] alarmValues;
        private readonly bool[] debugActive;

        private const int NumberOfAlarms = 4;
    }
}
