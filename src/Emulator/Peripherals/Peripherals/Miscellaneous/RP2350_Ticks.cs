//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_Ticks : RP2APBPeripheral
    {
        public RP2350_Ticks(IMachine machine, RP2350_Clocks clocks) : base(machine)
        {
            this.clocks = clocks;
            enabled = new bool[NumberOfTicks];
            cycles = new uint[NumberOfTicks];
            timers = new LimitTimer[NumberOfTicks];
            for(var i = 0; i < NumberOfTicks; i++)
            {
                timers[i] = new LimitTimer(machine.ClockSource, 1, this, $"tick{i}", limit: 1);
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
        }

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
                return (uint)timers[index].Value;
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
            timers[index].Limit = Math.Max(1u, cycles[index]);
            timers[index].ResetValue();
            UpdateClocks();
        }

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
        }

        private readonly bool[] enabled;
        private readonly uint[] cycles;
        private readonly LimitTimer[] timers;
        private readonly RP2350_Clocks clocks;

        private const int NumberOfTicks = 6;
    }
}
