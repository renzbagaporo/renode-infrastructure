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
    public sealed class RP2350_Ticks : RP2APBPeripheral
    {
        public RP2350_Ticks(IMachine machine, RP2350_Clocks clocks, RP2_Timer timer0 = null, RP2_Timer timer1 = null) : base(machine)
        {
            this.clocks = clocks;
            this.timer0 = timer0;
            this.timer1 = timer1;
            enabled = new bool[NumberOfTicks];
            cycles = new uint[NumberOfTicks];
            timers = new LimitTimer[NumberOfTicks];
            for(var i = 0; i < NumberOfTicks; i++)
            {
                // Reference cycle counters that wrap only after many ticks: COUNT is
                // derived from them, so a tick generator schedules no per-tick clock event.
                timers[i] = new LimitTimer(machine.ClockSource, 1, this, $"tick{i}", limit: CounterLimit(0), direction: Direction.Ascending);
            }
            clocks.ClockChanged += UpdateClocks;
            Reset();
        }

        public override void Reset()
        {
            Array.Clear(enabled, 0, enabled.Length);
            Array.Clear(cycles, 0, cycles.Length);
            foreach(var timer in timers)
            {
                timer.Reset();
            }
            UpdateClocks();
        }

        public event Action ClockChanged;

        public ulong GetFrequency(int tick)
        {
            return tick >= 0 && tick < NumberOfTicks && timers[tick].Enabled
                ? clocks.GetFrequency(4) / cycles[tick] : 0;
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset < 0 || offset >= NumberOfTicks * 12)
            {
                this.LogUnhandledRead(offset);
                return 0;
            }
            var index = (int)(offset / 12);
            switch(offset % 12)
            {
            case 0:
                return (enabled[index] ? 1u : 0) | (timers[index].Enabled ? 2u : 0);
            case 4:
                return cycles[index];
            case 8:
                return timers[index].Enabled ? Count(timers[index].Value, cycles[index]) : 0;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset < 0 || offset >= NumberOfTicks * 12)
            {
                this.LogUnhandledWrite(offset, value);
                return;
            }
            var index = (int)(offset / 12);
            switch(offset % 12)
            {
            case 0:
                enabled[index] = ApplyOperation(enabled[index] ? 1u : 0, value, operation, 1) != 0;
                break;
            case 4:
                cycles[index] = ApplyOperation(cycles[index], value, operation, 0x1ff);
                break;
            case 8:
                return;
            default:
                this.LogUnhandledWrite(offset, value);
                return;
            }
            timers[index].Enabled = false;
            timers[index].Limit = CounterLimit(cycles[index]);
            timers[index].ResetValue();
            UpdateClocks();
        }

        // COUNT counts down from CYCLES to 1 and reloads as each tick is generated.
        internal static uint Count(ulong elapsed, uint cycles)
        {
            var period = Math.Max(1u, cycles);
            return (uint)(period - elapsed % period);
        }

        // The counter wraps at a whole number of tick periods, so Count stays exact
        // across the wrap. The wrap is about 2^30 reference cycles away (seconds of
        // virtual time); a counter limit near ulong.MaxValue overflows the clock
        // source's fraction arithmetic and yields a bogus nearest limit.
        internal static ulong CounterLimit(uint cycles)
        {
            var period = (ulong)Math.Max(1u, cycles);
            return period * (CounterSpan / period);
        }

        private const ulong CounterSpan = 1UL << 30;

        private void UpdateClocks()
        {
            var frequency = clocks.GetFrequency(4);
            for(var i = 0; i < timers.Length; i++)
            {
                if(frequency != 0)
                {
                    timers[i].Frequency = frequency;
                }
                timers[i].Enabled = enabled[i] && cycles[i] != 0 && frequency != 0;
            }
            timer0?.SetTickClock(GetFrequency(2), GetFrequency(2) != 0);
            timer1?.SetTickClock(GetFrequency(3), GetFrequency(3) != 0);
            ClockChanged?.Invoke();
        }

        private readonly RP2_Timer timer0;
        private readonly RP2_Timer timer1;
        private readonly bool[] enabled;
        private readonly uint[] cycles;
        private readonly LimitTimer[] timers;
        private readonly RP2350_Clocks clocks;

        private const int NumberOfTicks = 6;
    }
}
