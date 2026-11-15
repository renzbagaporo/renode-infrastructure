//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Timers
{
    // RP2040 (8 slices) and RP2350 (12 slices) PWM. Each slice counts clk_sys
    // through its 8.4 fractional divider, free-running (0..TOP) or phase-correct
    // (0..TOP..0). Channel outputs are high while the counter is below CC and
    // drive every GPIO mapped to the slice through the PWM function. A slice's
    // timer fires only when an output changes, the counter wraps or turns.
    public sealed class RP2_PWM : RP2APBPeripheral, INumberedGPIOOutput
    {
        public RP2_PWM(IMachine machine, IRP2ClockSource clocks = null, RP2_GPIOBank0 gpio = null,
            ulong frequency = 125000000, bool rp2350 = false) : base(machine)
        {
            this.clocks = clocks;
            this.gpio = gpio;
            this.rp2350 = rp2350;
            fallbackFrequency = frequency;
            slices = new Slice[rp2350 ? 12 : 8];
            var connections = new Dictionary<int, IGPIO>();
            for(var i = 0; i < slices.Length; i++) connections.Add(i, new GPIO());
            Connections = connections;
            for(var i = 0; i < slices.Length; i++)
            {
                var slice = new Slice { Index = i };
                // Slice timers count virtual-time ticks, so each event fires on
                // the first tick of its due cycle; see CycleStart.
                slice.Timer = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, $"slice{i}",
                    limit: 1, direction: Direction.Ascending, workMode: WorkMode.OneShot, eventEnabled: true);
                slice.Timer.LimitReached += () => OnEvent(slice);
                slices[i] = slice;
            }
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            if(gpio != null) gpio.InputChanged += OnPinInput;
            cycleAnchorTime = machine.ClockSource.CurrentValue.Ticks;
            cycleFrequency = CounterFrequency;
            Reset();
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    Reanchor();
                    foreach(var slice in slices)
                    {
                        slice.Timer.Reset();
                        slice.Control = 0;
                        slice.Divider = 1 << 4;
                        slice.Counter = 0;
                        slice.Compare = 0;
                        slice.Top = 0xffff;
                        slice.CountingDown = false;
                        slice.Elapsed16 = 0;
                        slice.PendingTicks = 0;
                        slice.Outputs = 0;
                    }
                    rawInterrupts = 0;
                    for(var i = 0; i < 2; i++) interruptEnable[i] = interruptForce[i] = 0;
                    foreach(var slice in slices) QueueOutputs(slice);
                    QueueInterrupts();
                }
            });
            PublishPending();
        }

        public GPIO IRQ { get; } = new GPIO();

        // Slice n's DREQ pulses once per counter wrap; connect it to a pulsed
        // DMA request input.
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        // The RP2350's second wrap interrupt; unused on the RP2040.
        public GPIO IRQ1 { get; } = new GPIO();

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        protected override void OnAccessCompleted() => PublishPending();

        protected override void OnResetHeldChanged()
        {
            foreach(var slice in slices)
            {
                Settle(slice);
                Schedule(slice);
            }
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset < slices.Length * SliceStride)
            {
                var slice = slices[offset / SliceStride];
                switch(offset % SliceStride)
                {
                case 0x0: return slice.Control & ~(PhaseRetard | PhaseAdvance);
                case 0x4: return slice.Divider;
                case 0x8: Settle(slice); Schedule(slice); return slice.Counter;
                case 0xc: return slice.Compare;
                case 0x10: return slice.Top;
                }
            }
            switch(offset - CommonOffset)
            {
            case 0x0:
                var enabled = 0u;
                foreach(var slice in slices) if((slice.Control & Enable) != 0) enabled |= 1u << slice.Index;
                return enabled;
            case 0x4: return rawInterrupts;
            case 0x8: return interruptEnable[0];
            case 0xc: return interruptForce[0];
            case 0x10: return Interrupts(0);
            case 0x14: return rp2350 ? interruptEnable[1] : 0;
            case 0x18: return rp2350 ? interruptForce[1] : 0;
            case 0x1c: return rp2350 ? Interrupts(1) : 0;
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset < slices.Length * SliceStride)
            {
                var slice = slices[offset / SliceStride];
                Settle(slice);
                switch(offset % SliceStride)
                {
                case 0x0:
                    var control = ApplyOperation(slice.Control, value, operation, 0xff);
                    if((control & PhaseAdvance) != 0) Step(slice);
                    // Retarding skips the next counter tick.
                    if((control & PhaseRetard) != 0) slice.PendingTicks = -1;
                    if((control & 2) != (slice.Control & 2)) slice.CountingDown = false;
                    slice.Control = control & ~(PhaseRetard | PhaseAdvance);
                    slice.BHigh = ReadB(slice);
                    break;
                case 0x4: slice.Divider = ApplyOperation(slice.Divider, value, operation, 0xfff); break;
                case 0x8: slice.Counter = (ushort)ApplyOperation(slice.Counter, value, operation, 0xffff); slice.Elapsed16 = 0; break;
                case 0xc: slice.Compare = ApplyOperation(slice.Compare, value, operation); break;
                case 0x10: slice.Top = (ushort)ApplyOperation(slice.Top, value, operation, 0xffff); break;
                }
                QueueOutputs(slice);
                Schedule(slice);
                return;
            }
            switch(offset - CommonOffset)
            {
            case 0x0:
                var current = 0u;
                foreach(var slice in slices) if((slice.Control & Enable) != 0) current |= 1u << slice.Index;
                var mask = ApplyOperation(current, value, operation, (1u << slices.Length) - 1);
                foreach(var slice in slices)
                {
                    Settle(slice);
                    var bit = (mask & (1u << slice.Index)) != 0;
                    slice.Control = bit ? slice.Control | Enable : slice.Control & ~Enable;
                    slice.BHigh = ReadB(slice);
                    Schedule(slice);
                }
                return;
            case 0x4: rawInterrupts &= ~(value & ((1u << slices.Length) - 1)); break;
            case 0x8: interruptEnable[0] = ApplyOperation(interruptEnable[0], value, operation, (1u << slices.Length) - 1); break;
            case 0xc: interruptForce[0] = ApplyOperation(interruptForce[0], value, operation, (1u << slices.Length) - 1); break;
            case 0x14: if(rp2350) interruptEnable[1] = ApplyOperation(interruptEnable[1], value, operation, 0xfff); break;
            case 0x18: if(rp2350) interruptForce[1] = ApplyOperation(interruptForce[1], value, operation, 0xfff); break;
            case 0x10: case 0x1c: break;
            default: this.LogUnhandledWrite(offset, value); return;
            }
            QueueInterrupts();
        }

        // The slice's timer runs on clk_sys: always when free-running, while B
        // is high when gated by B, and never when B's edges clock the divider.
        private bool Running(Slice slice) => !ResetHeld && (slice.Control & Enable) != 0 && CounterFrequency != 0
            && (DivMode(slice) == 0 || (DivMode(slice) == GatedByB && slice.BHigh));

        private static uint DivMode(Slice slice) => (slice.Control >> 4) & 3;

        // B pins are inputs in the B-driven divider modes. A level change settles
        // a gated count; a selected edge advances the divider by one clk_sys cycle.
        private void OnPinInput(int pin, bool level)
        {
            if((pin & 1) == 0 || pin >= (rp2350 ? 48 : 30) || SliceOfPin(pin) >= slices.Length) return;
            if(gpio.GetFunction(pin) != PwmFunction) return;
            var slice = slices[SliceOfPin(pin)];
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    var previous = slice.BHigh;
                    slice.BHigh = level;
                    var mode = DivMode(slice);
                    if(mode == GatedByB && previous != level)
                    {
                        Settle(slice);
                        Schedule(slice);
                    }
                    else if(!ResetHeld && (slice.Control & Enable) != 0
                        && ((mode == RisingEdgesOfB && !previous && level) || (mode == FallingEdgesOfB && previous && !level)))
                    {
                        var units = slice.Elapsed16 + 16;
                        var period = TickPeriod(slice);
                        slice.Elapsed16 = units % period;
                        Advance(slice, (long)(units / period));
                    }
                }
            });
            PublishPending();
        }

        private bool ReadB(Slice slice)
        {
            if(gpio == null) return false;
            for(var pin = 1; pin < (rp2350 ? 48 : 30); pin += 2)
            {
                if(SliceOfPin(pin) == slice.Index && gpio.GetFunction(pin) == PwmFunction && gpio.GetInput(pin)) return true;
            }
            return false;
        }

        // Counter ticks are slice.Divider (8.4 fixed point) clk_sys/16 units apart.
        private ulong TickPeriod(Slice slice) => (slice.Divider >> 4) == 0 ? (256u << 4) | (slice.Divider & 0xf) : slice.Divider;

        // Brings the counter up to the current time from the timer's elapsed count.
        private void Settle(Slice slice)
        {
            if(!slice.Timer.Enabled) return;
            var units = slice.Elapsed16 + (CurrentCycle - slice.StartCycle) * 16;
            slice.Timer.Enabled = false;
            var period = TickPeriod(slice);
            var ticks = (long)(units / period);
            slice.Elapsed16 = units % period;
            Advance(slice, ticks);
        }

        // Advances the counter by whole ticks, raising wrap flags on the way.
        private void Advance(Slice slice, long ticks)
        {
            ticks += slice.PendingTicks;
            slice.PendingTicks = 0;
            if(ticks < 0)
            {
                slice.PendingTicks = ticks;
                return;
            }
            while(ticks > 0)
            {
                Normalize(slice);
                var step = Math.Min(ticks, (long)TicksToTurn(slice));
                ticks -= step;
                Move(slice, step);
            }
            QueueOutputs(slice);
        }

        private void Step(Slice slice)
        {
            Move(slice, 1);
            QueueOutputs(slice);
        }

        // A phase-correct counter above TOP while counting up turns down.
        private static void Normalize(Slice slice)
        {
            if((slice.Control & 2) != 0 && !slice.CountingDown && slice.Counter > slice.Top)
            {
                slice.Counter = slice.Top;
                slice.CountingDown = true;
            }
        }

        // Ticks until the counter wraps (free-running) or turns (phase-correct).
        // Phase-correct counting holds TOP and 0 for one tick each, so a period
        // is 2 * (TOP + 1) ticks.
        private ulong TicksToTurn(Slice slice)
        {
            if(PhaseCorrect(slice) && slice.CountingDown) return (ulong)slice.Counter + 1;
            return (ulong)(slice.Top - Math.Min(slice.Counter, slice.Top)) + 1;
        }

        private void Move(Slice slice, long ticks)
        {
            if(!PhaseCorrect(slice))
            {
                var next = (long)slice.Counter + ticks;
                if(next > slice.Top)
                {
                    slice.Counter = 0;
                    rawInterrupts |= 1u << slice.Index;
                    slice.Wraps++;
                    QueueInterrupts();
                }
                else slice.Counter = (ushort)next;
                return;
            }
            if(slice.CountingDown)
            {
                if(ticks > slice.Counter)
                {
                    slice.Counter = 0;
                    slice.CountingDown = false;
                    rawInterrupts |= 1u << slice.Index;
                    slice.Wraps++;
                    QueueInterrupts();
                }
                else slice.Counter = (ushort)(slice.Counter - ticks);
            }
            else
            {
                var next = (long)slice.Counter + ticks;
                if(next > slice.Top)
                {
                    slice.Counter = slice.Top;
                    slice.CountingDown = true;
                }
                else slice.Counter = (ushort)next;
            }
        }

        private bool PhaseCorrect(Slice slice) => (slice.Control & 2) != 0;

        // The next tick count at which an output changes or the counter wraps/turns.
        private ulong TicksToNextEvent(Slice slice)
        {
            var best = TicksToTurn(slice);
            foreach(var level in new[] { slice.Compare & 0xffff, slice.Compare >> 16 })
            {
                ulong ticks = 0;
                if(!slice.CountingDown && level > slice.Counter && level <= slice.Top) ticks = level - slice.Counter;
                else if(slice.CountingDown && level != 0 && level - 1 < slice.Counter) ticks = slice.Counter - (level - 1);
                if(ticks != 0 && ticks < best) best = ticks;
            }
            return Math.Max(1ul, best);
        }

        private void Schedule(Slice slice)
        {
            slice.Timer.Enabled = false;
            if(!Running(slice)) return;
            Normalize(slice);
            var ticks = TicksToNextEvent(slice);
            if(slice.PendingTicks < 0) ticks += (ulong)(-slice.PendingTicks);
            // The divided clock ticks on whole clk_sys cycles; a fractional divider
            // spaces them unevenly, as the hardware's accumulator does.
            var units = ticks * TickPeriod(slice);
            var cycles = units > slice.Elapsed16 ? (units - slice.Elapsed16 + 15) / 16 : 1;
            slice.StartCycle = CurrentCycle;
            slice.DueCycle = slice.StartCycle + Math.Max(1ul, cycles);
            var now = machine.ClockSource.CurrentValue.Ticks;
            var start = CycleStart(slice.DueCycle);
            slice.Timer.Restart(start > now ? start - now : 1);
        }

        private void OnEvent(Slice slice)
        {
            lock(SyncRoot)
            {
                var units = slice.Elapsed16 + (slice.DueCycle - slice.StartCycle) * 16;
                var period = TickPeriod(slice);
                slice.Elapsed16 = units % period;
                Advance(slice, (long)(units / period));
                Schedule(slice);
            }
            PublishPending();
        }

        private void OnClockChanged()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    foreach(var slice in slices) Settle(slice);
                    Reanchor();
                    foreach(var slice in slices) Schedule(slice);
                }
            });
            PublishPending();
        }

        private ulong CounterFrequency => clocks?.SystemFrequency ?? fallbackFrequency;

        // clk_sys cycles elapsed: cycleAnchor at virtual time cycleAnchorTime,
        // counted at cycleFrequency since then. Cycle c starts at the first
        // virtual-time tick at which CurrentCycle reads c.
        private ulong CurrentCycle => cycleAnchor + (ulong)((System.Numerics.BigInteger)(machine.ClockSource.CurrentValue.Ticks - cycleAnchorTime) * cycleFrequency / TimeInterval.TicksPerSecond);

        private ulong CycleStart(ulong cycle)
        {
            if(cycleFrequency == 0) return ulong.MaxValue;
            var cycles = (System.Numerics.BigInteger)cycle - cycleAnchor;
            return cycleAnchorTime + (ulong)((cycles * TimeInterval.TicksPerSecond + cycleFrequency - 1) / cycleFrequency);
        }

        // Cycles counted at the old frequency up to now stay counted.
        private void Reanchor()
        {
            cycleAnchor = CurrentCycle;
            cycleAnchorTime = machine.ClockSource.CurrentValue.Ticks;
            cycleFrequency = CounterFrequency;
        }

        private uint Interrupts(int bank) => (rawInterrupts & interruptEnable[bank]) | interruptForce[bank];

        private void QueueOutputs(Slice slice)
        {
            var outputs = 0;
            var levels = new[] { slice.Compare & 0xffff, slice.Compare >> 16 };
            for(var channel = 0; channel < 2; channel++)
            {
                var high = slice.Counter < levels[channel];
                if((slice.Control & (4u << channel)) != 0) high = !high;
                if(high) outputs |= 1 << channel;
            }
            if(DivMode(slice) != 0) outputs |= BInput;
            if(outputs == slice.Outputs && slice.Published) return;
            slice.Outputs = outputs;
            slice.Published = true;
            pending.Enqueue(Tuple.Create(slice.Index, outputs, machine.ClockSource.CurrentValue));
        }

        private void QueueInterrupts() => interruptsPending = true;

        private void PublishPending()
        {
            List<Tuple<int, int, TimeInterval>> outputs;
            bool interrupts;
            uint state0, state1;
            var wraps = new int[slices.Length];
            lock(SyncRoot)
            {
                for(var i = 0; i < slices.Length; i++)
                {
                    wraps[i] = slices[i].Wraps;
                    slices[i].Wraps = 0;
                }
                if(pending.Count == 0 && !interruptsPending && Array.TrueForAll(wraps, count => count == 0)) return;
                outputs = new List<Tuple<int, int, TimeInterval>>(pending);
                pending.Clear();
                interrupts = interruptsPending;
                interruptsPending = false;
                state0 = Interrupts(0);
                state1 = rp2350 ? Interrupts(1) : 0;
            }
            if(gpio != null)
            {
                foreach(var output in outputs)
                {
                    // Delivered after the locks are released: give the change its own time.
                    using(SignalTime.At(machine, output.Item3))
                    {
                        for(var pin = 0; pin < (rp2350 ? 48 : 30); pin++)
                        {
                            if(SliceOfPin(pin) != output.Item1) continue;
                            var input = (pin & 1) != 0 && (output.Item2 & BInput) != 0;
                            gpio.SetPeripheralOutput(PwmFunction, pin, (output.Item2 & (1 << (pin & 1))) != 0, !input);
                        }
                    }
                }
            }
            if(interrupts)
            {
                IRQ.Set(state0 != 0);
                IRQ1.Set(state1 != 0);
            }
            for(var i = 0; i < wraps.Length; i++)
            {
                for(var pulse = 0; pulse < wraps[i]; pulse++)
                {
                    Connections[i].Set(true);
                    Connections[i].Set(false);
                }
            }
        }

        private int SliceOfPin(int pin) => pin < 32 ? (pin >> 1) & 7 : 8 + (((pin - 32) >> 1) & 3);

        private uint rawInterrupts;
        private bool interruptsPending;
        private ulong cycleAnchor;
        private ulong cycleAnchorTime;
        private ulong cycleFrequency;

        private readonly uint[] interruptEnable = new uint[2];
        private readonly uint[] interruptForce = new uint[2];
        private readonly Queue<Tuple<int, int, TimeInterval>> pending = new Queue<Tuple<int, int, TimeInterval>>();
        private readonly Slice[] slices;
        private readonly IRP2ClockSource clocks;
        private readonly RP2_GPIOBank0 gpio;
        private readonly bool rp2350;
        private readonly ulong fallbackFrequency;

        private long CommonOffset => slices.Length * SliceStride;

        private const long SliceStride = 0x14;
        private const uint Enable = 1;
        private const uint PhaseRetard = 1 << 6;
        private const uint PhaseAdvance = 1 << 7;
        private const int PwmFunction = 4;
        private const int BInput = 4;
        private const uint GatedByB = 1, RisingEdgesOfB = 2, FallingEdgesOfB = 3;

        private sealed class Slice
        {
            public int Index;
            public LimitTimer Timer;
            public uint Control, Divider, Compare;
            public ushort Counter, Top;
            public bool CountingDown, Published, BHigh;
            // Wraps whose DREQ pulses are still to be published.
            public int Wraps;
            public int Outputs;
            // clk_sys/16 units since the last whole counter tick.
            public ulong Elapsed16;
            // A negative count is a retarded tick still to be skipped.
            public long PendingTicks;
            // The clk_sys cycles the running timer started at and fires at.
            public ulong StartCycle, DueCycle;
        }
    }
}
