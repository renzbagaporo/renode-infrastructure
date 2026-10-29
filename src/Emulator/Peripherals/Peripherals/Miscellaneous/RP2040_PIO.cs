//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // RP2040 PIO v0. SMs execute the shared instruction memory; FIFOs and
    // instruction stalls are independent for each machine.
    public sealed class RP2040_PIO : RP2APBPeripheral, INumberedGPIOOutput
    {
        public RP2040_PIO(IMachine machine, RP2_GPIOBank0 gpio, IRP2ClockSource clocks = null,
            int pioId = 0, ulong frequency = 125000000) : base(machine)
        {
            if(pioId < 0 || pioId > 1) throw new ConstructionException("RP2040 has two PIO blocks");
            this.gpio = gpio;
            this.clocks = clocks;
            function = 6 + pioId;
            this.frequency = clocks == null ? frequency : clocks.SystemFrequency;
            cycleCounter = new LimitTimer(machine.ClockSource, Math.Max(1ul, this.frequency), this, "cycles",
                direction: Direction.Ascending, enabled: this.frequency != 0);
            var connections = new Dictionary<int, IGPIO>();
            for(var index = 0; index < 10; index++) connections.Add(index, new GPIO());
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            for(var index = 0; index < 4; index++)
            {
                var sm = new StateMachine(index);
                sm.Timer = new LimitTimer(machine.ClockSource, Math.Max(1ul, this.frequency), this, $"sm{index}",
                    limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
                sm.Timer.LimitReached += () => Tick(sm);
                stateMachines[index] = sm;
            }
            for(var pin = 0; pin < 32; pin++)
            {
                inputHistory[pin] = new Queue<InputTransition>();
                synchronizedInput[pin] = gpio.GetInput(pin);
            }
            gpio.InputChanged += OnInputChanged;
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            Reset();
        }

        // 0..3: TX FIFO has space, 4..7: RX FIFO has data, 8..9: processor IRQs.
        public IReadOnlyDictionary<int, IGPIO> Connections { get; }
        public GPIO IRQ0 => (GPIO)Connections[8];
        public GPIO IRQ1 => (GPIO)Connections[9];

        public override void Reset()
        {
            lock(SyncRoot)
            {
                control = 0;
                debug = 0;
                irqFlags = 0;
                executingCycle = false;
                cycleIRQFlags = cycleIRQSet = cycleIRQClear = 0;
                inputSyncBypass = 0;
                output = 0;
                outputEnable = 0;
                Array.Clear(instructions, 0, instructions.Length);
                Array.Clear(irqEnable, 0, irqEnable.Length);
                Array.Clear(irqForce, 0, irqForce.Length);
                foreach(var sm in stateMachines)
                {
                    sm.Timer.Enabled = false;
                    sm.Scheduled = false;
                    sm.ResumeCycles = 0;
                    sm.StickyMask = sm.StickyValue = sm.WriteMask = sm.WriteValue = sm.DirectionMask = sm.DirectionValue = 0;
                    sm.ClockDivider = 0x10000;
                    sm.ExecControl = 0x1f000;
                    sm.ShiftControl = 0xc0000;
                    sm.PinControl = 5u << 26;
                    sm.PC = 0;
                    sm.X = sm.Y = sm.OSR = sm.ISR = 0;
                    sm.OutputCount = 32;
                    sm.InputCount = 0;
                    sm.Tx.Clear();
                    sm.Rx.Clear();
                    sm.Edge256 = (BigInteger)cycleCounter.Value * 256;
                    ClearExecution(sm);
                }
                for(var pin = 0; pin < 32; pin++) QueuePin(pin);
                UpdateSignals();
            }
            PublishPending();
        }

        protected override void OnResetHeldChanged()
        {
            foreach(var sm in stateMachines) Schedule(sm);
            UpdateSignals();
        }

        protected override void OnAccessCompleted() => PublishPending();

        protected override uint ReadRegister(long offset)
        {
            SynchronizeTime();
            if(offset >= 0x20 && offset < 0x30)
            {
                var sm = stateMachines[(offset - 0x20) / 4];
                if(sm.Rx.Count == 0)
                {
                    debug |= 1u << (8 + sm.Index);
                    return sm.LastRx;
                }
                sm.LastRx = sm.Rx.Dequeue();
                Wake(sm);
                UpdateSignals();
                return sm.LastRx;
            }
            if(offset >= 0xc8 && offset < 0x128)
            {
                var sm = stateMachines[(offset - 0xc8) / 0x18];
                switch((offset - 0xc8) % 0x18)
                {
                case 0: return sm.ClockDivider;
                case 4: return sm.ExecControl | (sm.Forced && sm.Stalled ? 0x80000000u : 0);
                case 8: return sm.ShiftControl;
                case 12: return (uint)sm.PC;
                case 16: return sm.Pending ? sm.Instruction : instructions[sm.PC];
                case 20: return sm.PinControl;
                }
            }
            if(offset >= 0x12c && offset < 0x144)
            {
                var index = (int)((offset - 0x12c) / 12);
                switch((offset - 0x12c) % 12)
                {
                case 0: return irqEnable[index];
                case 4: return irqForce[index];
                case 8: return (InterruptStatus & irqEnable[index]) | irqForce[index];
                }
            }
            switch(offset)
            {
            case 0: return control;
            case 4:
                uint status = 0;
                foreach(var sm in stateMachines)
                {
                    if(sm.Rx.Count >= RxCapacity(sm)) status |= 1u << sm.Index;
                    if(sm.Rx.Count == 0) status |= 1u << (8 + sm.Index);
                    if(sm.Tx.Count >= TxCapacity(sm)) status |= 1u << (16 + sm.Index);
                    if(sm.Tx.Count == 0) status |= 1u << (24 + sm.Index);
                }
                return status;
            case 8: return debug;
            case 12:
                uint levels = 0;
                foreach(var sm in stateMachines) levels |= (uint)(sm.Tx.Count | (sm.Rx.Count << 4)) << (sm.Index * 8);
                return levels;
            case 0x30: return irqFlags;
            case 0x34: return 0;
            case 0x38: return inputSyncBypass;
            case 0x3c: return output;
            case 0x40: return outputEnable;
            case 0x44: return 0x00200404;
            case 0x128: return InterruptStatus;
            }
            // Instruction memory and TX FIFOs are write-only.
            if((offset >= 0x10 && offset < 0x20) || (offset >= 0x48 && offset < 0xc8)) return 0;
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            SynchronizeTime();
            if(offset >= 0x10 && offset < 0x20)
            {
                var sm = stateMachines[(offset - 0x10) / 4];
                if(sm.Tx.Count >= TxCapacity(sm)) debug |= 1u << (16 + sm.Index);
                else sm.Tx.Enqueue(ApplyOperation(0, value, operation));
                Wake(sm);
                UpdateSignals();
                return;
            }
            if(offset >= 0x48 && offset < 0xc8)
            {
                // Atomic aliases read a write-only instruction RAM port as zero.
                var address = (int)((offset - 0x48) / 4);
                instructions[address] = (ushort)ApplyOperation(0, value, operation, 0xffff);
                foreach(var sm in stateMachines)
                {
                    if(sm.JumpLoop && sm.PC == address) { ClearExecution(sm); Schedule(sm); }
                }
                return;
            }
            if(offset >= 0xc8 && offset < 0x128)
            {
                var sm = stateMachines[(offset - 0xc8) / 0x18];
                switch((offset - 0xc8) % 0x18)
                {
                case 0:
                    sm.ClockDivider = ApplyOperation(sm.ClockDivider, value, operation, 0xffffff00);
                    Reschedule(sm);
                    break;
                case 4:
                    sm.ExecControl = ApplyOperation(sm.ExecControl, value, operation, 0x7fffff9f);
                    if(sm.JumpLoop) ClearExecution(sm);
                    Reschedule(sm);
                    BeginPinCycle();
                    ResolvePinWrites();
                    break;
                case 8:
                    var previous = sm.ShiftControl;
                    sm.ShiftControl = ApplyOperation(previous, value, operation, 0xffff0000);
                    if(((previous ^ sm.ShiftControl) & 0xc0000000) != 0)
                    {
                        sm.Tx.Clear();
                        sm.Rx.Clear();
                    }
                    Reschedule(sm);
                    break;
                case 16:
                    ClearExecution(sm);
                    sm.Pending = sm.Forced = true;
                    sm.Instruction = (ushort)ApplyOperation(0, value, operation, 0xffff);
                    BeginPinCycle();
                    Execute(sm);
                    ResolvePinWrites();
                    var retry = sm.RetryNextCycle;
                    sm.RetryNextCycle = false;
                    Schedule(sm, wake: retry);
                    break;
                case 20:
                    sm.PinControl = ApplyOperation(sm.PinControl, value, operation);
                    if(sm.JumpLoop) ClearExecution(sm);
                    Reschedule(sm);
                    break;
                }
                UpdateSignals();
                return;
            }
            if(offset >= 0x12c && offset < 0x144)
            {
                var index = (int)((offset - 0x12c) / 12);
                switch((offset - 0x12c) % 12)
                {
                case 0: irqEnable[index] = ApplyOperation(irqEnable[index], value, operation, 0xfff); break;
                case 4: irqForce[index] = ApplyOperation(irqForce[index], value, operation, 0xfff); break;
                }
                UpdateSignals();
                return;
            }
            switch(offset)
            {
            case 0:
                var previousControl = control;
                control = ApplyOperation(control, value, operation, 0xf);
                var strobes = ApplyOperation(0, value, operation);
                foreach(var sm in stateMachines)
                {
                    var wasEnabled = (previousControl & (1u << sm.Index)) != 0;
                    if(wasEnabled && !Enabled(sm) && sm.Scheduled && !sm.Stalled && !sm.Forced) sm.ResumeCycles = CyclesUntilExecution(sm);
                    if(!wasEnabled && Enabled(sm) && sm.ResumeCycles != 0)
                    {
                        sm.Edge256 = NextClockEdge(sm) + Divider256(sm) * (sm.ResumeCycles - 1);
                        sm.ResumeCycles = 0;
                    }
                    if((strobes & (1u << (4 + sm.Index))) != 0) Restart(sm);
                    if((strobes & (1u << (8 + sm.Index))) != 0)
                    {
                        var remaining = sm.Scheduled ? CyclesUntilExecution(sm) : 1ul;
                        sm.Edge256 = (BigInteger)cycleCounter.Value * 256 + Divider256(sm) * remaining;
                    }
                    Reschedule(sm);
                }
                BeginPinCycle();
                ResolvePinWrites();
                break;
            case 8: debug &= ~value; break;
            case 0x30: irqFlags &= ~(value & 0xff); WakeAll(); break;
            case 0x34: irqFlags |= value & 0xff; WakeAll(); break;
            case 0x38: inputSyncBypass = ApplyOperation(inputSyncBypass, value, operation); WakeAll(); break;
            case 4:
            case 12:
            case 0x3c:
            case 0x40:
            case 0x44:
            case 0x128: break;
            default: this.LogUnhandledWrite(offset, value); break;
            }
            UpdateSignals();
        }

        private void Tick(StateMachine triggeringMachine)
        {
            lock(SyncRoot)
            {
                var now = cycleCounter.Value;
                executingCycle = true;
                cycleIRQFlags = irqFlags;
                cycleIRQSet = cycleIRQClear = 0;
                BeginPinCycle();
                // Clock enables on the same system edge are simultaneous. Run
                // all of them before resolving pin strobes in SM priority order.
                foreach(var sm in stateMachines)
                {
                    if(!sm.Scheduled || sm.DueTick > now) continue;
                    sm.Scheduled = false;
                    sm.Timer.Enabled = false;
                    if(ResetHeld || frequency == 0 || (!Enabled(sm) && !sm.Forced)) continue;
                    if(sm.PrefetchOnly)
                    {
                        sm.PrefetchOnly = false;
                        if(AutoPull(sm) && sm.OutputCount >= PullThreshold(sm)) TryPull(sm);
                        Schedule(sm, advance: false);
                        continue;
                    }
                    if(!sm.Pending)
                    {
                        sm.Pending = true;
                        sm.Instruction = instructions[sm.PC];
                    }
                    var pc = sm.PC;
                    var fetched = !sm.Forced && !sm.Injected;
                    var forced = sm.Forced;
                    var instruction = sm.Instruction;
                    var completed = Execute(sm);
                    if(completed)
                    {
                        if(!forced) sm.Edge256 += Divider256(sm) * (ulong)(1 + sm.Delay);
                        // A JMP which returns to itself has no changing machine
                        // state. Its next useful execution is a pin/config change.
                        // Keep the loop period to retain delay and divider phase.
                        var condition = (instruction >> 5) & 7;
                        if(fetched && SideCount(sm) == 0 && sm.PC == pc && (instruction & 31) == pc
                            && (instruction >> 13) == 0 && (condition == 0 || (condition == 6 && ReadInput((int)((sm.ExecControl >> 24) & 31)))))
                        {
                            sm.Pending = sm.Stalled = sm.JumpLoop = true;
                            sm.Instruction = instruction;
                            sm.LoopCycles = 1 + sm.Delay;
                        }
                        Schedule(sm, advance: false);
                    }
                    else if(sm.RetryNextCycle)
                    {
                        sm.RetryNextCycle = false;
                        if(!forced) sm.Edge256 += Divider256(sm);
                        Schedule(sm, advance: false, wake: true);
                    }
                }
                var previousIRQFlags = irqFlags;
                irqFlags = (irqFlags | cycleIRQSet) & ~cycleIRQClear;
                executingCycle = false;
                if(previousIRQFlags != irqFlags) WakeAll();
                foreach(var sm in stateMachines) if(sm.Stalled && !sm.Scheduled) Reschedule(sm);
                ResolvePinWrites();
                UpdateSignals();
            }
            PublishPending();
        }

        private bool Execute(StateMachine sm)
        {
            var instruction = sm.Instruction;
            var opcode = instruction >> 13;
            var argument = instruction & 0xff;
            // A stalled instruction asserts side-set on every execution clock.
            // Treat the side strobe separately from OUT/SET and inline enable.
            sm.Started = false;
            ApplySideSet(sm, instruction);
            sm.Started = true;
            var nextPC = sm.PC;
            var jump = false;
            var complete = true;
            switch(opcode)
            {
            case 0: // JMP
                var condition = (argument >> 5) & 7;
                bool taken;
                switch(condition)
                {
                case 0: taken = true; break;
                case 1: taken = sm.X == 0; break;
                case 2: taken = sm.X-- != 0; break;
                case 3: taken = sm.Y == 0; break;
                case 4: taken = sm.Y-- != 0; break;
                case 5: taken = sm.X != sm.Y; break;
                case 6: taken = ReadInput((int)((sm.ExecControl >> 24) & 31)); break;
                default: taken = sm.OutputCount < PullThreshold(sm); break;
                }
                if(taken) { nextPC = argument & 31; jump = true; }
                break;
            case 1: // WAIT
                var polarity = (argument & 0x80) != 0;
                var source = (argument >> 5) & 3;
                var index = argument & 31;
                bool input;
                if(source == 0) input = ReadInput(index);
                else if(source == 1) input = ReadInput((InBase(sm) + index) & 31);
                else if(source == 2)
                {
                    var mask = 1u << IrqIndex(sm, index);
                    input = (ReadIRQFlags() & mask) != 0;
                    if(input && polarity) ClearIRQ(mask);
                }
                else input = false;
                complete = input == polarity;
                break;
            case 2: // IN
                if(!sm.Shifted)
                {
                    var count = BitCount(argument);
                    var bits = ReadSource(sm, (argument >> 5) & 7) & Mask(count);
                    sm.ISR = count == 32 ? bits : InRight(sm) ? (sm.ISR >> count) | (bits << (32 - count)) : (sm.ISR << count) | bits;
                    sm.InputCount = Math.Min(32, sm.InputCount + count);
                    sm.Shifted = true;
                }
                if(AutoPush(sm) && sm.InputCount >= PushThreshold(sm)) complete = Push(sm, true);
                break;
            case 3: // OUT
                if(AutoPull(sm) && sm.OutputCount >= PullThreshold(sm))
                {
                    if(TryPull(sm)) sm.RetryNextCycle = true;
                    else debug |= 1u << (24 + sm.Index);
                    complete = false;
                    break;
                }
                var outCount = BitCount(argument);
                var data = OutRight(sm) ? sm.OSR & Mask(outCount) : sm.OSR >> (32 - outCount);
                sm.OSR = outCount == 32 ? 0 : OutRight(sm) ? sm.OSR >> outCount : sm.OSR << outCount;
                sm.OutputCount = Math.Min(32, sm.OutputCount + outCount);
                switch((argument >> 5) & 7)
                {
                case 0: SetPins(sm, OutBase(sm), OutCount(sm), data, false); break;
                case 1: sm.X = data; break;
                case 2: sm.Y = data; break;
                case 3: break;
                case 4: SetPins(sm, OutBase(sm), OutCount(sm), data, true); break;
                case 5: nextPC = (int)(data & 31); jump = true; break;
                case 6: sm.ISR = data; sm.InputCount = outCount; break;
                case 7: sm.Exec = (ushort)data; break;
                }
                if(AutoPull(sm) && sm.OutputCount >= PullThreshold(sm)) TryPull(sm);
                break;
            case 4: // PUSH / PULL
                if((argument & 0x80) != 0)
                {
                    if((AutoPull(sm) || (argument & 0x40) != 0) && sm.OutputCount < PullThreshold(sm)) break;
                    if(!TryPull(sm))
                    {
                        if((argument & 0x20) != 0)
                        {
                            debug |= 1u << (24 + sm.Index);
                            complete = false;
                        }
                        else { sm.OSR = sm.X; sm.OutputCount = 0; }
                    }
                }
                else if((argument & 0x40) == 0 || sm.InputCount >= PushThreshold(sm))
                {
                    complete = Push(sm, (argument & 0x20) != 0);
                }
                break;
            case 5: // MOV
                var moved = ReadSource(sm, argument & 7);
                switch((argument >> 3) & 3)
                {
                case 1: moved = ~moved; break;
                case 2: moved = Reverse(moved); break;
                }
                switch((argument >> 5) & 7)
                {
                case 0: SetPins(sm, OutBase(sm), OutCount(sm), moved, false); break;
                case 1: sm.X = moved; break;
                case 2: sm.Y = moved; break;
                case 4: sm.Exec = (ushort)moved; break;
                case 5: nextPC = (int)(moved & 31); jump = true; break;
                case 6: sm.ISR = moved; sm.InputCount = 0; break;
                case 7: sm.OSR = moved; sm.OutputCount = 0; break;
                }
                break;
            case 6: // IRQ
                var irqMask = 1u << IrqIndex(sm, argument & 31);
                if((argument & 0x40) != 0) ClearIRQ(irqMask);
                else if((argument & 0x20) == 0) SetIRQ(irqMask);
                else if(!sm.IrqSet)
                {
                    SetIRQ(irqMask);
                    sm.IrqSet = true;
                    complete = false;
                }
                else complete = (ReadIRQFlags() & irqMask) == 0;
                break;
            case 7: // SET
                var immediate = (uint)(argument & 31);
                switch((argument >> 5) & 7)
                {
                case 0: SetPins(sm, SetBase(sm), SetCount(sm), immediate, false); break;
                case 1: sm.X = immediate; break;
                case 2: sm.Y = immediate; break;
                case 4: SetPins(sm, SetBase(sm), SetCount(sm), immediate, true); break;
                }
                break;
            }
            if(AutoPull(sm) && opcode != 3 && sm.OutputCount >= PullThreshold(sm)) TryPull(sm);
            sm.Stalled = !complete;
            if(!complete) return false;
            if(jump) sm.PC = nextPC;
            else if(!sm.Forced && !sm.Injected)
            {
                var wrapTop = (int)((sm.ExecControl >> 12) & 31);
                sm.PC = sm.PC == wrapTop ? (int)((sm.ExecControl >> 7) & 31) : (sm.PC + 1) & 31;
            }
            sm.Delay = sm.Forced || sm.Exec.HasValue ? 0 : ((instruction >> 8) & 31) & ((1 << (5 - SideCount(sm))) - 1);
            var exec = sm.Exec;
            var forced = sm.Forced;
            ClearExecution(sm);
            if(exec.HasValue)
            {
                sm.Pending = sm.Injected = true;
                sm.Forced = forced;
                sm.Instruction = exec.Value;
            }
            return true;
        }

        private uint ReadIRQFlags() => executingCycle ? cycleIRQFlags : irqFlags;

        private void SetIRQ(uint mask)
        {
            if(executingCycle) cycleIRQSet |= mask;
            else { irqFlags |= mask; WakeAll(); }
        }

        private void ClearIRQ(uint mask)
        {
            if(executingCycle) cycleIRQClear |= mask;
            else { irqFlags &= ~mask; WakeAll(); }
        }

        private uint ReadSource(StateMachine sm, int source)
        {
            switch(source)
            {
            case 0:
                uint pins = 0;
                for(var bit = 0; bit < 32; bit++) if(ReadInput((InBase(sm) + bit) & 31)) pins |= 1u << bit;
                return pins;
            case 1: return sm.X;
            case 2: return sm.Y;
            case 3: return 0;
            case 5:
                var level = (sm.ExecControl & (1u << 4)) != 0 ? sm.Rx.Count : sm.Tx.Count;
                return level < (sm.ExecControl & 15) ? uint.MaxValue : 0;
            case 6: return sm.ISR;
            case 7: return sm.OSR;
            default: return 0;
            }
        }

        private bool Push(StateMachine sm, bool block)
        {
            if(sm.Rx.Count >= RxCapacity(sm))
            {
                debug |= 1u << sm.Index;
                if(block) return false;
            }
            else sm.Rx.Enqueue(sm.ISR);
            sm.ISR = 0;
            sm.InputCount = 0;
            return true;
        }

        private bool TryPull(StateMachine sm)
        {
            if(sm.Tx.Count == 0) return false;
            sm.OSR = sm.Tx.Dequeue();
            sm.OutputCount = 0;
            return true;
        }

        private void ApplySideSet(StateMachine sm, ushort instruction)
        {
            var count = SideCount(sm);
            if(count == 0) return;
            var optional = (sm.ExecControl & (1u << 30)) != 0;
            if(optional && (instruction & 0x1000) == 0) return;
            var bits = (uint)(instruction >> (13 - count));
            SetPins(sm, (int)((sm.PinControl >> 10) & 31), count - (optional ? 1 : 0), bits,
                (sm.ExecControl & (1u << 29)) != 0);
        }

        private void SetPins(StateMachine sm, int pinBase, int count, uint bits, bool directions)
        {
            // Pin writes are strobes into a block-wide latch. Side-set takes
            // precedence when it overlaps an OUT or SET in the same cycle.
            uint mask = 0;
            uint value = 0;
            for(var bit = 0; bit < Math.Min(32, count); bit++)
            {
                var pin = (pinBase + bit) & 31;
                mask |= 1u << pin;
                if((bits & (1u << bit)) != 0) value |= 1u << pin;
            }
            if(sm.Started && ((sm.ExecControl & (1u << 29)) != 0) == directions)
            {
                var sideCount = SideCount(sm);
                var optional = (sm.ExecControl & (1u << 30)) != 0;
                if(sideCount != 0 && (!optional || (sm.Instruction & 0x1000) != 0))
                {
                    for(var bit = 0; bit < sideCount - (optional ? 1 : 0); bit++)
                        mask &= ~(1u << (((int)(sm.PinControl >> 10) + bit) & 31));
                }
            }
            if(directions)
            {
                sm.DirectionMask |= mask;
                sm.DirectionValue = (sm.DirectionValue & ~mask) | (value & mask);
            }
            else
            {
                if(sm.Started)
                {
                    if((sm.ExecControl & (1u << 18)) != 0 && (bits & (1u << (int)((sm.ExecControl >> 19) & 31))) == 0)
                    {
                        if((sm.ExecControl & (1u << 17)) != 0) sm.StickyMask = 0;
                        return;
                    }
                    sm.StickyMask = mask;
                    sm.StickyValue = value;
                }
                sm.WriteMask |= mask;
                sm.WriteValue = (sm.WriteValue & ~mask) | (value & mask);
            }
        }

        private void BeginPinCycle()
        {
            foreach(var sm in stateMachines)
            {
                sm.WriteMask = sm.DirectionMask = 0;
                sm.WriteValue = sm.DirectionValue = 0;
            }
        }

        private void ResolvePinWrites()
        {
            var now = cycleCounter.Value;
            foreach(var sm in stateMachines)
            {
                if(!HasStalledSideSet(sm) || !ClockEnableAt(sm, now)) continue;
                sm.Started = false;
                ApplySideSet(sm, sm.Instruction);
                sm.Started = true;
            }
            var previousOutput = output;
            var previousEnable = outputEnable;
            foreach(var sm in stateMachines)
            {
                var stickyMask = (sm.ExecControl & (1u << 17)) != 0 ? sm.StickyMask : 0;
                var mask = stickyMask | sm.WriteMask;
                var value = (sm.StickyValue & stickyMask & ~sm.WriteMask) | (sm.WriteValue & sm.WriteMask);
                output = (output & ~mask) | value;
                outputEnable = (outputEnable & ~sm.DirectionMask) | (sm.DirectionValue & sm.DirectionMask);
            }
            var changed = (previousOutput ^ output) | (previousEnable ^ outputEnable);
            for(var pin = 0; pin < 32; pin++) if((changed & (1u << pin)) != 0) QueuePin(pin);
            foreach(var sm in stateMachines)
            {
                if(!HasStalledSideSet(sm)) continue;
                SideSetState(sm, out var mask, out var value, out var directions);
                var nextTick = (ulong)(NextClockEdge(sm, sm.Forced ? 256ul : Divider256(sm)) / 256);
                // Higher-priority persistent writes can mask this strobe. They
                // need no correction event when the lower write cannot win.
                for(var higher = sm.Index + 1; higher < stateMachines.Length; higher++)
                {
                    var other = stateMachines[higher];
                    if(!directions && (other.ExecControl & (1u << 17)) != 0) mask &= ~other.StickyMask;
                    if(HasStalledSideSet(other) && ClockEnableAt(other, nextTick))
                    {
                        SideSetState(other, out var otherMask, out _, out var otherDirections);
                        if(directions == otherDirections) mask &= ~otherMask;
                    }
                }
                var current = directions ? outputEnable : output;
                if(((current ^ value) & mask) != 0 && (!sm.Timer.Enabled || sm.DueTick > nextTick))
                    Schedule(sm, wake: true, earliestTick: nextTick);
            }
        }

        private bool HasStalledSideSet(StateMachine sm)
        {
            return !ResetHeld && frequency != 0 && (Enabled(sm) || sm.Forced) && sm.Stalled && sm.Started && SideCount(sm) != 0
                && ((sm.ExecControl & (1u << 30)) == 0 || (sm.Instruction & 0x1000) != 0);
        }

        private static void SideSetState(StateMachine sm, out uint mask, out uint value, out bool directions)
        {
            var count = SideCount(sm);
            var optional = (sm.ExecControl & (1u << 30)) != 0;
            var data = (uint)(sm.Instruction >> (13 - count));
            var pinBase = (int)((sm.PinControl >> 10) & 31);
            directions = (sm.ExecControl & (1u << 29)) != 0;
            mask = value = 0;
            for(var bit = 0; bit < count - (optional ? 1 : 0); bit++)
            {
                var pin = (pinBase + bit) & 31;
                mask |= 1u << pin;
                if((data & (1u << bit)) != 0) value |= 1u << pin;
            }
        }

        private static bool ClockEnableAt(StateMachine sm, ulong tick)
        {
            var divider = sm.Forced ? 256ul : Divider256(sm);
            var delta = ((BigInteger)tick + 1) * 256 - 1 - sm.Edge256;
            var steps = delta >= 0 ? delta / divider : -((-delta + divider - 1) / divider);
            var edge = sm.Edge256 + steps * divider;
            return edge >= 0 && edge / 256 == tick;
        }

        private void QueuePin(int pin)
        {
            notifications.Enqueue(new Notification { Pin = pin, Value = (output & (1u << pin)) != 0,
                Enabled = (outputEnable & (1u << pin)) != 0, IsPin = true });
        }

        private void UpdateSignals()
        {
            foreach(var sm in stateMachines)
            {
                QueueSignal(sm.Index, !ResetHeld && sm.Tx.Count < TxCapacity(sm));
                QueueSignal(sm.Index + 4, !ResetHeld && sm.Rx.Count != 0);
            }
            var status = InterruptStatus;
            for(var index = 0; index < 2; index++) QueueSignal(8 + index, ((status & irqEnable[index]) | irqForce[index]) != 0);
        }

        private uint InterruptStatus
        {
            get
            {
                uint result = (irqFlags & 15) << 8;
                foreach(var sm in stateMachines)
                {
                    if(sm.Rx.Count != 0) result |= 1u << sm.Index;
                    if(sm.Tx.Count < TxCapacity(sm)) result |= 1u << (4 + sm.Index);
                }
                return result;
            }
        }

        private void QueueSignal(int index, bool value)
        {
            if(signalState[index] == value) return;
            signalState[index] = value;
            notifications.Enqueue(new Notification { Pin = index, Value = value });
        }

        private void PublishPending()
        {
            if(System.Threading.Monitor.IsEntered(SyncRoot)) return;
            lock(SyncRoot)
            {
                if(publishing) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    Notification notification;
                    lock(SyncRoot)
                    {
                        if(notifications.Count == 0) { publishing = false; return; }
                        notification = notifications.Dequeue();
                    }
                    if(notification.IsPin) gpio.SetPeripheralOutput(function, notification.Pin, notification.Value, notification.Enabled);
                    else Connections[notification.Pin].Set(notification.Value);
                }
            }
            finally
            {
                lock(SyncRoot) publishing = false;
            }
        }

        private bool ReadInput(int pin)
        {
            var history = inputHistory[pin];
            var now = cycleCounter.Value;
            while(history.Count != 0 && history.Peek().Tick <= now) synchronizedInput[pin] = history.Dequeue().Value;
            return (inputSyncBypass & (1u << pin)) != 0 ? gpio.GetInput(pin) : synchronizedInput[pin];
        }

        private void OnInputChanged(int pin, bool level)
        {
            if(pin >= 32) return;
            lock(SyncRoot)
            {
                ReadInput(pin);
                var now = cycleCounter.Value;
                inputHistory[pin].Enqueue(new InputTransition { Tick = now + 2, Value = level });
                var visibleTick = now + ((inputSyncBypass & (1u << pin)) != 0 ? 1ul : 2ul);
                foreach(var sm in stateMachines)
                {
                    if(!sm.Stalled) continue;
                    var dependsOnPin = InputDependency(sm);
                    if(dependsOnPin == pin && (!sm.Timer.Enabled || sm.DueTick > visibleTick))
                        Schedule(sm, wake: true, earliestTick: visibleTick);
                }
            }
        }

        private static int InputDependency(StateMachine sm)
        {
            if(sm.JumpLoop) return ((sm.Instruction >> 5) & 7) == 6 ? (int)((sm.ExecControl >> 24) & 31) : -1;
            if((sm.Instruction >> 13) != 1) return -1;
            var source = (sm.Instruction >> 5) & 3;
            if(source > 1) return -1;
            return source == 0 ? sm.Instruction & 31 : (InBase(sm) + (sm.Instruction & 31)) & 31;
        }

        private void Reschedule(StateMachine sm)
        {
            var ready = sm.Stalled && CanResume(sm);
            ulong visibleTick = 0;
            if(sm.Stalled && !ready)
            {
                var pin = InputDependency(sm);
                if(pin >= 0 && (inputSyncBypass & (1u << pin)) == 0 && inputHistory[pin].Count != 0)
                    visibleTick = inputHistory[pin].Peek().Tick;
            }
            Schedule(sm, wake: ready || visibleTick != 0, earliestTick: visibleTick);
        }

        private void OnClockChanged()
        {
            SynchronizeTime();
            lock(SyncRoot)
            {
                frequency = clocks.SystemFrequency;
                cycleCounter.Frequency = Math.Max(1ul, frequency);
                cycleCounter.Enabled = frequency != 0;
                foreach(var sm in stateMachines)
                {
                    sm.Timer.Frequency = Math.Max(1ul, frequency);
                    Reschedule(sm);
                }
                BeginPinCycle();
                ResolvePinWrites();
            }
            PublishPending();
        }

        private void WakeAll(StateMachine except = null)
        {
            foreach(var sm in stateMachines) if(sm != except) Wake(sm);
        }

        private void Wake(StateMachine sm)
        {
            if(sm.Stalled && !sm.Timer.Enabled && CanResume(sm)) Schedule(sm, wake: true);
            else if(!sm.Stalled && sm.Scheduled && AutoPull(sm) && sm.OutputCount >= PullThreshold(sm) && sm.Tx.Count != 0)
                Schedule(sm, advance: false);
        }

        private bool CanResume(StateMachine sm)
        {
            if(AutoPull(sm) && sm.OutputCount >= PullThreshold(sm) && sm.Tx.Count != 0) return true;
            var argument = sm.Instruction & 0xff;
            switch(sm.Instruction >> 13)
            {
            case 0:
                return sm.JumpLoop && ((argument >> 5) & 7) == 6
                    && !ReadInput((int)((sm.ExecControl >> 24) & 31));
            case 1:
                var index = argument & 31;
                var source = (argument >> 5) & 3;
                var value = source == 0 ? ReadInput(index) : source == 1 ? ReadInput((InBase(sm) + index) & 31)
                    : source == 2 && (irqFlags & (1u << IrqIndex(sm, index))) != 0;
                return value == ((argument & 0x80) != 0);
            case 2: return !AutoPush(sm) || sm.InputCount < PushThreshold(sm) || sm.Rx.Count < RxCapacity(sm);
            case 3: return !AutoPull(sm) || sm.Tx.Count != 0 || sm.OutputCount < PullThreshold(sm);
            case 4:
                if((argument & 0x80) != 0)
                    return ((AutoPull(sm) || (argument & 0x40) != 0) && sm.OutputCount < PullThreshold(sm))
                        || sm.Tx.Count != 0 || (argument & 0x20) == 0;
                return ((argument & 0x40) != 0 && sm.InputCount < PushThreshold(sm))
                    || sm.Rx.Count < RxCapacity(sm) || (argument & 0x20) == 0;
            case 6: return (irqFlags & (1u << IrqIndex(sm, argument & 31))) == 0;
            default: return true;
            }
        }

        private void Schedule(StateMachine sm, bool advance = true, bool wake = false, ulong earliestTick = 0)
        {
            sm.Timer.Enabled = false;
            sm.Scheduled = false;
            sm.PrefetchOnly = false;
            if(ResetHeld || frequency == 0 || (!Enabled(sm) && !sm.Forced) || (sm.Stalled && !wake)) return;
            var now = (BigInteger)cycleCounter.Value;
            var earliest = BigInteger.Max(now + 1, earliestTick);
            if(sm.Forced)
            {
                sm.DueTick = (ulong)earliest;
                sm.Scheduled = true;
                sm.Timer.Limit = (ulong)(earliest - now);
                sm.Timer.ResetValue();
                sm.Timer.Enabled = true;
                return;
            }
            if(advance || sm.Edge256 / 256 < earliest)
            {
                var divider = Divider256(sm) * (ulong)(sm.JumpLoop ? sm.LoopCycles : 1);
                var elapsed = earliest * 256 - sm.Edge256;
                if(elapsed > 0) sm.Edge256 += ((elapsed + divider - 1) / divider) * divider;
            }
            var due = sm.Edge256 / 256;
            if(Enabled(sm) && !sm.Stalled && AutoPull(sm) && sm.OutputCount >= PullThreshold(sm) && sm.Tx.Count != 0)
            {
                var prefetch = NextClockEdge(sm) / 256;
                if(prefetch < due)
                {
                    due = prefetch;
                    sm.PrefetchOnly = true;
                }
            }
            var ticks = due - now;
            sm.DueTick = (ulong)due;
            sm.Scheduled = true;
            sm.Timer.Limit = (ulong)BigInteger.Min(ulong.MaxValue, BigInteger.Max(1, ticks));
            sm.Timer.ResetValue();
            sm.Timer.Enabled = true;
        }

        private ulong CyclesUntilExecution(StateMachine sm)
        {
            var remaining = sm.Edge256 - ((BigInteger)cycleCounter.Value + 1) * 256;
            return remaining < 0 ? 1 : (ulong)(remaining / Divider256(sm) + 1);
        }

        private BigInteger NextClockEdge(StateMachine sm, ulong divider = 0)
        {
            var earliest = ((BigInteger)cycleCounter.Value + 1) * 256;
            if(divider == 0) divider = Divider256(sm);
            var remaining = sm.Edge256 - earliest;
            return remaining >= 0 ? sm.Edge256 - (remaining / divider) * divider
                : sm.Edge256 + ((-remaining + divider - 1) / divider) * divider;
        }

        private void Restart(StateMachine sm)
        {
            sm.Timer.Enabled = false;
            sm.Scheduled = false;
            sm.ResumeCycles = 0;
            sm.Edge256 = NextClockEdge(sm);
            ClearExecution(sm);
            sm.InputCount = 0;
            sm.OutputCount = 32;
            sm.ISR = 0;
            sm.Delay = 0;
            sm.StickyMask = 0;
        }

        private static void ClearExecution(StateMachine sm)
        {
            sm.Pending = sm.Forced = sm.Injected = sm.Started = sm.Shifted = sm.IrqSet = sm.Stalled = sm.RetryNextCycle = sm.JumpLoop = false;
            sm.Exec = null;
        }

        private void SynchronizeTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu)) cpu.SyncTime();
        }

        private bool Enabled(StateMachine sm) => (control & (1u << sm.Index)) != 0;
        private static int TxCapacity(StateMachine sm) => (sm.ShiftControl & (1u << 31)) != 0 ? 0 : (sm.ShiftControl & (1u << 30)) != 0 ? 8 : 4;
        private static int RxCapacity(StateMachine sm) => (sm.ShiftControl & (1u << 30)) != 0 ? 0 : (sm.ShiftControl & (1u << 31)) != 0 ? 8 : 4;
        private static ulong Divider256(StateMachine sm) => (sm.ClockDivider >> 16) == 0 ? 65536ul * 256 : sm.ClockDivider >> 8;
        private static int InBase(StateMachine sm) => (int)((sm.PinControl >> 15) & 31);
        private static int OutBase(StateMachine sm) => (int)(sm.PinControl & 31);
        private static int OutCount(StateMachine sm) => Math.Min(32, (int)((sm.PinControl >> 20) & 63));
        private static int SetBase(StateMachine sm) => (int)((sm.PinControl >> 5) & 31);
        private static int SetCount(StateMachine sm) => (int)((sm.PinControl >> 26) & 7);
        private static int SideCount(StateMachine sm) => Math.Min(5, (int)(sm.PinControl >> 29));
        private static bool InRight(StateMachine sm) => (sm.ShiftControl & (1u << 18)) != 0;
        private static bool OutRight(StateMachine sm) => (sm.ShiftControl & (1u << 19)) != 0;
        private static bool AutoPush(StateMachine sm) => (sm.ShiftControl & (1u << 16)) != 0;
        private static bool AutoPull(StateMachine sm) => (sm.ShiftControl & (1u << 17)) != 0;
        private static int PushThreshold(StateMachine sm) => Threshold((int)((sm.ShiftControl >> 20) & 31));
        private static int PullThreshold(StateMachine sm) => Threshold((int)((sm.ShiftControl >> 25) & 31));
        private static int Threshold(int encoded) => encoded == 0 ? 32 : encoded;
        private static int BitCount(int argument) => Threshold(argument & 31);
        private static uint Mask(int bits) => bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        private static int IrqIndex(StateMachine sm, int index) => (index & 4) | ((index + ((index & 16) != 0 ? sm.Index : 0)) & 3);
        private static uint Reverse(uint value)
        {
            value = ((value & 0x55555555) << 1) | ((value >> 1) & 0x55555555);
            value = ((value & 0x33333333) << 2) | ((value >> 2) & 0x33333333);
            value = ((value & 0x0f0f0f0f) << 4) | ((value >> 4) & 0x0f0f0f0f);
            value = ((value & 0x00ff00ff) << 8) | ((value >> 8) & 0x00ff00ff);
            return (value << 16) | (value >> 16);
        }

        private sealed class StateMachine
        {
            public StateMachine(int index) { Index = index; }
            public readonly int Index;
            public readonly Queue<uint> Tx = new Queue<uint>();
            public readonly Queue<uint> Rx = new Queue<uint>();
            public LimitTimer Timer;
            public uint ClockDivider, ExecControl, ShiftControl, PinControl;
            public uint X, Y, OSR, ISR, LastRx;
            public uint StickyMask, StickyValue, WriteMask, WriteValue, DirectionMask, DirectionValue;
            public ulong DueTick, ResumeCycles;
            public bool Scheduled, PrefetchOnly;
            public int PC, InputCount, OutputCount, Delay, LoopCycles;
            public ushort Instruction;
            public ushort? Exec;
            public bool Pending, Forced, Injected, Started, Shifted, IrqSet, Stalled, RetryNextCycle, JumpLoop;
            public BigInteger Edge256;
        }

        private struct InputTransition
        {
            public ulong Tick;
            public bool Value;
        }

        private struct Notification
        {
            public int Pin;
            public bool IsPin, Value, Enabled;
        }

        private readonly StateMachine[] stateMachines = new StateMachine[4];
        private readonly ushort[] instructions = new ushort[32];
        private readonly uint[] irqEnable = new uint[2];
        private readonly uint[] irqForce = new uint[2];
        private readonly bool[] signalState = new bool[10];
        private readonly bool[] synchronizedInput = new bool[32];
        private readonly Queue<InputTransition>[] inputHistory = new Queue<InputTransition>[32];
        private readonly Queue<Notification> notifications = new Queue<Notification>();
        private readonly RP2_GPIOBank0 gpio;
        private readonly IRP2ClockSource clocks;
        private readonly LimitTimer cycleCounter;
        private readonly int function;
        private uint control, debug, irqFlags, inputSyncBypass, output, outputEnable;
        private ulong frequency;
        private bool publishing;
        private bool executingCycle;
        private uint cycleIRQFlags, cycleIRQSet, cycleIRQClear;
    }
}
