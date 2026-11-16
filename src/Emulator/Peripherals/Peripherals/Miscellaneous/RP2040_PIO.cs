//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // RP2040 PIO v0. SMs execute the shared instruction memory; FIFOs and
    // instruction stalls are independent for each machine. With rp2350 set it
    // is an RP2350 PIO v1 block: GPIOBASE relocates the 32 PIO pins (WAIT GPIO
    // included) to GPIO 16-47, and the v1 register map, IN_COUNT, STATUS_SEL,
    // MOV PINDIRS and WAIT JMPPIN apply. IRQ PREV/NEXT and the RX FIFO
    // random-access MOV and PUTGET registers are not modelled.
    public sealed class RP2040_PIO : RP2APBPeripheral, INumberedGPIOOutput
    {
        public RP2040_PIO(IMachine machine, RP2_GPIOBank0 gpio, IRP2ClockSource clocks = null,
            int pioId = 0, ulong frequency = 125000000, bool rp2350 = false) : base(machine)
        {
            if(pioId < 0 || pioId > (rp2350 ? 2 : 1)) throw new ConstructionException(rp2350 ? "RP2350 has three PIO blocks" : "RP2040 has two PIO blocks");
            this.rp2350 = rp2350;
            this.gpio = gpio;
            this.clocks = clocks;
            function = 6 + pioId;
            this.frequency = clocks == null ? frequency : clocks.SystemFrequency;
            cycleAnchorTime = machine.ClockSource.CurrentValue.Ticks;
            var connections = new Dictionary<int, IGPIO>();
            for(var index = 0; index < 10; index++) connections.Add(index, new GPIO());
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            for(var index = 0; index < 4; index++)
            {
                var sm = new StateMachine(index);
                // Machine timers count virtual-time ticks, so each fires on the
                // first tick of its due cycle; see CycleStart.
                sm.Timer = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, $"sm{index}",
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

        // A loop that returns to its own state with no effect outside its
        // machine is parked until something it reads or writes changes, and
        // then resumes at the exact cycle and step it would have reached; see
        // TraceLoop. Disabling it runs every PIO clock, for comparison.
        public bool ParkSteadyLoops
        {
            get => parkSteadyLoops;
            set
            {
                machine.ClockSource.ExecuteInLock(() =>
                {
                    lock(SyncRoot)
                    {
                        parkSteadyLoops = value;
                        ResumeSteadyLoops();
                    }
                });
                PublishPending();
            }
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(ResetState);
            PublishPending();
        }

        protected override IClockSource AccessClock => machine.ClockSource;

        private void ResetState()
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
                Rebase(0);
                Array.Clear(instructions, 0, instructions.Length);
                Array.Clear(irqEnable, 0, irqEnable.Length);
                Array.Clear(irqForce, 0, irqForce.Length);
                foreach(var sm in stateMachines)
                {
                    sm.Timer.Enabled = false;
                    sm.Scheduled = false;
                    sm.Steady = sm.Recording = sm.SteadyCandidate = false;
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
                    sm.Edge256 = (BigInteger)CurrentCycle * 256;
                    ClearExecution(sm);
                }
                for(var pin = 0; pin < 32; pin++) QueuePin(pin);
                UpdateSignals();
            }
        }

        protected override void OnResetHeldChanged()
        {
            foreach(var sm in stateMachines) Schedule(sm);
            UpdateSignals();
        }

        protected override void OnAccessCompleted() => PublishPending();

        protected override uint ReadRegister(long offset)
        {
            if(rp2350)
            {
                if(offset >= 0x128 && offset < 0x168) return 0;
                if(offset == 0x168) return (uint)gpioBase;
                if(offset >= 0x16c && offset < 0x188) offset -= V1InterruptOffset;
            }
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
                case 12:
                    ResumeSteadyLoop(sm);
                    return (uint)sm.PC;
                case 16:
                    ResumeSteadyLoop(sm);
                    return sm.Pending ? sm.Instruction : instructions[sm.PC];
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
            case 0x44: return rp2350 ? 0x10200404u : 0x00200404u;
            case 0x128: return InterruptStatus;
            }
            // Instruction memory and TX FIFOs are write-only.
            if((offset >= 0x10 && offset < 0x20) || (offset >= 0x48 && offset < 0xc8)) return 0;
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            // A steady loop has no FIFO effects, so only TXF writes leave it be.
            // Anything else may change what it executes, so resume and retrace.
            if(offset < 0x10 || offset >= 0x20)
            {
                ResumeSteadyLoops();
                foreach(var sm in stateMachines) sm.Recording = false;
            }
            if(rp2350)
            {
                if(offset >= 0x128 && offset < 0x168)
                {
                    WarnOnce("RXF PUTGET registers are not modelled");
                    return;
                }
                if(offset == 0x168)
                {
                    Rebase((int)(ApplyOperation((uint)gpioBase, value, operation) & 0x10));
                    WakeAll();
                    return;
                }
                if(offset >= 0x16c && offset < 0x188) offset -= V1InterruptOffset;
            }
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
                    if(sm.CountLoop && sm.PC == address) { MaterializeCountLoop(sm); Schedule(sm, advance: false); }
                    if(sm.JumpLoop && sm.PC == address) { ClearExecution(sm); Schedule(sm); }
                }
                return;
            }
            if(offset >= 0xc8 && offset < 0x128)
            {
                var sm = stateMachines[(offset - 0xc8) / 0x18];
                MaterializeCountLoop(sm);
                switch((offset - 0xc8) % 0x18)
                {
                case 0:
                    sm.ClockDivider = ApplyOperation(sm.ClockDivider, value, operation, 0xffffff00);
                    Reschedule(sm);
                    break;
                case 4:
                    sm.ExecControl = ApplyOperation(sm.ExecControl, value, operation, rp2350 ? 0x7fffffffu : 0x7fffff9fu);
                    if(sm.JumpLoop) ClearExecution(sm);
                    Reschedule(sm);
                    BeginPinCycle();
                    ResolvePinWrites();
                    break;
                case 8:
                    var previous = sm.ShiftControl;
                    sm.ShiftControl = ApplyOperation(previous, value, operation, rp2350 ? 0xffffc01fu : 0xffff0000u);
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
                case 0: irqEnable[index] = ApplyOperation(irqEnable[index], value, operation, rp2350 ? 0xffffu : 0xfffu); break;
                case 4: irqForce[index] = ApplyOperation(irqForce[index], value, operation, rp2350 ? 0xffffu : 0xfffu); break;
                }
                UpdateSignals();
                return;
            }
            switch(offset)
            {
            case 0:
                foreach(var sm in stateMachines) MaterializeCountLoop(sm);
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
                        sm.Edge256 = (BigInteger)CurrentCycle * 256 + Divider256(sm) * remaining;
                    }
                    Reschedule(sm);
                }
                BeginPinCycle();
                ResolvePinWrites();
                break;
            case 8:
                debug &= ~value;
                foreach(var sm in stateMachines) if(sm.Stalled) Reschedule(sm);
                break;
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
            lock(SyncRoot) TickAt(CurrentCycle);
            PublishPending();
        }

        private void TickAt(ulong now)
        {
            {
                foreach(var sm in stateMachines)
                {
                    // A machine whose own pin changes from an earlier cycle have not
                    // reached the GPIO yet, typically because another thread is still
                    // delivering them to a device that answers synchronously (an SWD
                    // target), would sample that device's inputs before it saw them.
                    // Hold it for one cycle, shifting its clock phase so its own
                    // instruction timing is kept. Other machines, which may be paced
                    // by external inputs, keep running.
                    if(sm.UndeliveredPinChanges == 0 || !sm.Scheduled || sm.DueTick > now) continue;
                    sm.Edge256 += 256;
                    sm.DueTick = now + 1;
                    sm.Timer.Enabled = false;
                    StartTimer(sm, sm.DueTick);
                }
                ExecuteCycle(now);
            }
        }

        // A CPU access first runs every cycle due up to that CPU's own time, so
        // the PIO meets the CPU at the same point of its instruction stream
        // however far the machine clock (the slowest core) lags it. Pin changes
        // are delivered after each cycle, as for timer-driven cycles; the timer
        // events for cycles already run then find nothing due.
        // The DMA steps along at the block's time, one bus phase a cycle, while
        // its next transfer is paced by a PIO DREQ or unpaced: a state machine
        // fed or drained by DMA then sees its FIFO serviced as on silicon, not
        // only once the machine clock catches up. Any other DMA work stops the
        // block where it is, since that transfer belongs to the machine clock.
        // A CPU access to the DMA's registers runs this too (RP2040_DMA).
        internal void CatchUpToAccessor()
        {
            var bus = machine.SystemBus;
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state) && state.DebugAccess) return;
            if(!bus.TryGetTransactionInitiator(out var initiator) || !(initiator is BaseCPU cpu) || !cpu.OnPossessedThread || cpu.TimeHandle == null) return;
            var target = cpu.TimeHandle.TotalElapsedTime.Ticks;
            if(dma == null) dma = machine.GetPeripheralsOfType<RP2040_DMA>().FirstOrDefault();
            while(true)
            {
                var ran = false;
                machine.ClockSource.ExecuteInLock(() =>
                {
                    ulong dmaStep = 0;
                    lock(SyncRoot)
                    {
                        if(frequency == 0 || target <= cycleAnchorTime) return;
                        var targetCycle = cycleAnchor + (ulong)((BigInteger)(target - cycleAnchorTime) * frequency / TimeInterval.TicksPerSecond);
                        // An input change already scheduled from another machine
                        // is the first level the block cannot know in advance.
                        var change = gpio.NextInputChange(gpioBase, 32);
                        if(change.HasValue)
                        {
                            var changeCycle = CycleAt(change.Value);
                            if(changeCycle < targetCycle) targetCycle = changeCycle;
                        }
                        var next = ulong.MaxValue;
                        foreach(var sm in stateMachines) if(sm.Scheduled && sm.DueTick < next) next = sm.DueTick;
                        var dmaNext = ulong.MaxValue;
                        switch(dma?.NextAheadWork() ?? AheadWork.None)
                        {
                        case AheadWork.Blocked:
                            return;
                        case AheadWork.Step:
                            dmaNext = Math.Max(CurrentCycle, CycleAt(TimeInterval.FromTicks(dma.StepAheadTime))) + 1;
                            break;
                        }
                        if(Math.Min(next, dmaNext) > targetCycle)
                        {
                            if(targetCycle > aheadCycle) aheadCycle = targetCycle;
                            return;
                        }
                        if(dmaNext < next)
                        {
                            if(dmaNext > aheadCycle) aheadCycle = dmaNext;
                            dmaStep = CycleStart(dmaNext);
                        }
                        else
                        {
                            if(next > aheadCycle) aheadCycle = next;
                            TickAt(CurrentCycle);
                        }
                        ran = true;
                    }
                    // The DMA's bus phase may access this block, so it runs
                    // outside the block's lock, as the DMA's own timer does.
                    if(dmaStep != 0) dma.StepAhead(dmaStep);
                });
                PublishPending();
                if(!ran) return;
            }
        }

        private void ExecuteCycle(ulong now)
        {
            {
                executingCycle = true;
                cycleIRQFlags = irqFlags;
                cycleIRQSet = cycleIRQClear = 0;
                // Machines whose timers fire separately on one cycle strobe the
                // latch together: a later pass keeps the earlier passes' strobes.
                if(pinCycle != now) BeginPinCycle();
                pinCycle = now;
                // Clock enables on the same system edge are simultaneous. Run
                // all of them before resolving pin strobes in SM priority order.
                foreach(var sm in stateMachines)
                {
                    if(!sm.Scheduled || sm.DueTick > now) continue;
                    sm.Scheduled = false;
                    sm.CountLoop = false;
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
                    var step = sm.Recording && fetched ? CaptureStep(sm) : default(LoopStep);
                    stepEffect = false;
                    stepReadMask = stepReadValues = 0;
                    trackingReads = true;
                    var completed = Execute(sm);
                    trackingReads = false;
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
                        else if(fetched && !forced && SideCount(sm) == 0 && sm.PC == pc && (instruction & 31) == pc
                            && (instruction >> 13) == 0 && (condition == 2 || condition == 4))
                        {
                            // A counting delay loop has no effect outside its own
                            // counter until it falls through: run straight to the
                            // last iteration, which executes normally.
                            var usesX = condition == 2;
                            var counter = usesX ? sm.X : sm.Y;
                            if(counter > 1)
                            {
                                sm.CountLoop = true;
                                sm.CountLoopUsesX = usesX;
                                sm.CountLoopStart = sm.Edge256;
                                sm.CountLoopValue = counter;
                                sm.CountLoopCycles = 1 + sm.Delay;
                                sm.Edge256 += (BigInteger)Divider256(sm) * (ulong)sm.CountLoopCycles * counter;
                                if(usesX) sm.X = 0; else sm.Y = 0;
                            }
                        }
                        TraceLoop(sm, step, pc, fetched && !forced);
                        Schedule(sm, advance: false);
                    }
                    else sm.Recording = false;
                    if(!completed && sm.RetryNextCycle)
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
                // A loop is parked only once the cycle that closed it has
                // resolved its pin writes without changing the latch.
                foreach(var sm in stateMachines)
                {
                    if(!sm.SteadyCandidate) continue;
                    sm.SteadyCandidate = false;
                    if(!sm.RecordingBroken && WritesHoldLatch(sm) && InputsSettled(sm.TraceReadMask, sm.TraceReadValues)) ParkSteadyLoop(sm);
                    else StartRecording(sm);
                }
                UpdateSignals();
            }
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
                else input = rp2350 && ReadInput(JumpPinWait(sm, index));
                complete = input == polarity;
                break;
            case 2: // IN
                if(!sm.Shifted)
                {
                    var count = BitCount(argument);
                    var bits = ReadSource(sm, (argument >> 5) & 7, count) & Mask(count);
                    sm.ISR = count == 32 ? bits : InRight(sm) ? (sm.ISR >> count) | (bits << (32 - count)) : (sm.ISR << count) | bits;
                    sm.InputCount = Math.Min(32, sm.InputCount + count);
                    sm.Shifted = true;
                }
                if(AutoPush(sm) && sm.InputCount >= PushThreshold(sm)) complete = Push(sm, true);
                break;
            case 3: // OUT
                // A forced OUT (SMx_INSTR) refills an exhausted OSR and shifts in
                // the one execution, and never refills after it, so it consumes
                // exactly one TX word: pio_sm_drain_tx_fifo, which issues forced
                // OUTs until the FIFO is empty, then leaves the OSR empty.
                if(AutoPull(sm) && sm.OutputCount >= PullThreshold(sm))
                {
                    var pulled = TryPull(sm);
                    if(!pulled || !sm.Forced)
                    {
                        if(pulled) sm.RetryNextCycle = true;
                        else debug |= 1u << (24 + sm.Index);
                        complete = false;
                        break;
                    }
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
                if(!sm.Forced && AutoPull(sm) && sm.OutputCount >= PullThreshold(sm)) TryPull(sm);
                break;
            case 4: // PUSH / PULL
                if(rp2350 && (argument & 0x10) != 0)
                {
                    WarnOnce("MOV to or from RX FIFO entries is not modelled");
                    break;
                }
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
                // MOV to PINS or PINDIRS keeps OUT_COUNT bits. Read no more pins
                // than that unless the bit order is reversed, so a steady loop
                // depends only on the pins that reach its outputs.
                var destination = (argument >> 5) & 7;
                var toPins = (destination == 0 || (destination == 3 && rp2350)) && ((argument >> 3) & 3) != 2;
                var moved = ReadSource(sm, argument & 7, toPins ? OutCount(sm) : 32);
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
                case 3: if(rp2350) SetPins(sm, OutBase(sm), OutCount(sm), moved, true); break;
                case 4: sm.Exec = (ushort)moved; break;
                case 5: nextPC = (int)(moved & 31); jump = true; break;
                case 6: sm.ISR = moved; sm.InputCount = 0; break;
                case 7: sm.OSR = moved; sm.OutputCount = 0; break;
                }
                break;
            case 6: // IRQ
                if(rp2350 && (argument & 8) != 0) WarnOnce("IRQ PREV/NEXT targets other PIO blocks, which is not modelled; using this block's flag");
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

        private uint ReadIRQFlags()
        {
            stepEffect = true;
            return executingCycle ? cycleIRQFlags : irqFlags;
        }

        private void SetIRQ(uint mask)
        {
            stepEffect = true;
            if(executingCycle) cycleIRQSet |= mask;
            else { irqFlags |= mask; WakeAll(); }
        }

        private void ClearIRQ(uint mask)
        {
            stepEffect = true;
            if(executingCycle) cycleIRQClear |= mask;
            else { irqFlags &= ~mask; WakeAll(); }
        }

        private uint ReadSource(StateMachine sm, int source, int pinCount = 32)
        {
            switch(source)
            {
            case 0:
                uint pins = 0;
                var now = CurrentCycle;
                // RP2350 SHIFTCTRL.IN_COUNT masks the pins IN and MOV read (0 is 32).
                if(rp2350) pinCount = Math.Min(pinCount, Threshold((int)(sm.ShiftControl & 31)));
                for(var bit = 0; bit < pinCount; bit++) if(ReadInput((InBase(sm) + bit) & 31, now)) pins |= 1u << bit;
                return pins;
            case 1: return sm.X;
            case 2: return sm.Y;
            case 3: return 0;
            case 5:
                stepEffect = true;
                if(rp2350)
                {
                    // EXECCTRL.STATUS_SEL (6:5): TX level, RX level or IRQ flag STATUS_N (4:0).
                    var selection = (sm.ExecControl >> 5) & 3;
                    var n = (int)(sm.ExecControl & 31);
                    if(selection == 2) return (ReadIRQFlags() & (1u << IrqIndex(sm, n))) != 0 ? uint.MaxValue : 0;
                    return (selection == 1 ? sm.Rx.Count : sm.Tx.Count) < n ? uint.MaxValue : 0;
                }
                var level = (sm.ExecControl & (1u << 4)) != 0 ? sm.Rx.Count : sm.Tx.Count;
                return level < (sm.ExecControl & 15) ? uint.MaxValue : 0;
            case 6: return sm.ISR;
            case 7: return sm.OSR;
            default: return 0;
            }
        }

        private bool Push(StateMachine sm, bool block)
        {
            stepEffect = true;
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

        // Even a failed pull depends on the TX FIFO, which a steady loop must not.
        private bool TryPull(StateMachine sm)
        {
            stepEffect = true;
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
            pinCycle = ulong.MaxValue;
            foreach(var sm in stateMachines)
            {
                sm.WriteMask = sm.DirectionMask = 0;
                sm.WriteValue = sm.DirectionValue = 0;
            }
        }

        private void ResolvePinWrites()
        {
            var now = CurrentCycle;
            foreach(var sm in stateMachines)
            {
                if(!sm.Steady || !SteadyStepAt(sm, now, out var step)) continue;
                sm.WriteMask |= step.WriteMask;
                sm.WriteValue = (sm.WriteValue & ~step.WriteMask) | step.WriteValue;
                sm.DirectionMask |= step.DirectionMask;
                sm.DirectionValue = (sm.DirectionValue & ~step.DirectionMask) | step.DirectionValue;
            }
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
            foreach(var sm in stateMachines)
            {
                if(sm.Recording && (changed & sm.TraceWriteMask) != 0) sm.RecordingBroken = true;
                // A parked loop's writes would have restored a pin another
                // write changed; let it run to restore it.
                if(sm.Steady && (changed & sm.SteadyWriteMask) != 0) ResumeSteadyLoop(sm);
            }
            for(var pin = 0; pin < 32; pin++)
            {
                if((changed & (1u << pin)) == 0) continue;
                var owners = 0u;
                foreach(var sm in stateMachines)
                {
                    var stickyMask = (sm.ExecControl & (1u << 17)) != 0 ? sm.StickyMask : 0;
                    if(((stickyMask | sm.WriteMask | sm.DirectionMask) & (1u << pin)) != 0) owners |= 1u << sm.Index;
                }
                QueuePin(pin, owners);
            }
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

        // Owners are the state machines whose writes made the change; each is held
        // until the change is delivered.
        private void QueuePin(int pin, uint owners = 0)
        {
            notifications.Enqueue(new Notification { Pin = pin + gpioBase, Value = (output & (1u << pin)) != 0,
                Enabled = (outputEnable & (1u << pin)) != 0, IsPin = true, Owners = owners, Time = PinChangeTime() });
            foreach(var sm in stateMachines) if((owners & (1u << sm.Index)) != 0) sm.UndeliveredPinChanges++;
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
                uint result = (irqFlags & (rp2350 ? 0xffu : 15u)) << 8;
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
                    if(notification.IsPin)
                    {
                        // Delivered after the locks are released: give the change its own time.
                        using(SignalTime.At(machine, notification.Time)) gpio.SetPeripheralOutput(function, notification.Pin, notification.Value, notification.Enabled);
                        if(notification.Owners != 0)
                        {
                            lock(SyncRoot)
                            {
                                foreach(var sm in stateMachines) if((notification.Owners & (1u << sm.Index)) != 0) sm.UndeliveredPinChanges--;
                            }
                        }
                    }
                    else Connections[notification.Pin].Set(notification.Value);
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private bool ReadInput(int pin) => ReadInput(pin, CurrentCycle);

        private bool ReadInput(int pin, ulong now)
        {
            var history = inputHistory[pin];
            while(history.Count != 0 && history.Peek().Tick <= now) synchronizedInput[pin] = history.Dequeue().Value;
            var value = (inputSyncBypass & (1u << pin)) != 0 ? gpio.GetInput(pin + gpioBase) : synchronizedInput[pin];
            if(trackingReads)
            {
                stepReadMask |= 1u << pin;
                if(value) stepReadValues |= 1u << pin;
            }
            return value;
        }

        private void OnInputChanged(int gpioPin, bool level)
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                // GPIOBASE is read under the clock lock, as Rebase changes it.
                var pin = gpioPin - gpioBase;
                if(pin >= 0 && pin < 32) UpdateInput(pin, level);
            });
        }

        // GPIOBASE: PIO pin n is GPIO n + base. Moving the window releases the
        // old pins and samples the new ones without input synchronizer delay.
        private void Rebase(int newBase)
        {
            lock(SyncRoot)
            {
                if(newBase != gpioBase)
                {
                    for(var pin = 0; pin < 32; pin++)
                    {
                        notifications.Enqueue(new Notification { Pin = pin + gpioBase, IsPin = true, Time = PinChangeTime() });
                    }
                    gpioBase = newBase;
                }
                for(var pin = 0; pin < 32; pin++)
                {
                    inputHistory[pin].Clear();
                    synchronizedInput[pin] = gpio.GetInput(pin + gpioBase);
                    QueuePin(pin);
                }
            }
        }

        private static int JumpPinWait(StateMachine sm, int index) => ((int)((sm.ExecControl >> 24) & 31) + (index & 3)) & 31;

        private void WarnOnce(string message)
        {
            if(warned.Add(message)) this.Log(LogLevel.Warning, message);
        }

        private void UpdateInput(int pin, bool level)
        {
            lock(SyncRoot)
            {
                ReadInput(pin);
                var now = ArrivalCycle();
                inputHistory[pin].Enqueue(new InputTransition { Tick = now + 2, Value = level });
                var visibleTick = now + ((inputSyncBypass & (1u << pin)) != 0 ? 1ul : 2ul);
                foreach(var sm in stateMachines)
                {
                    // The steps up to the change keep their old input, and the
                    // machine runs them again from here to meet the new one.
                    if(sm.Steady && (sm.SteadyReadMask & (1u << pin)) != 0)
                    {
                        ResumeSteadyLoop(sm);
                        continue;
                    }
                    if(!sm.Stalled) continue;
                    var dependsOnPin = InputDependency(sm);
                    if(dependsOnPin == pin && (!sm.Timer.Enabled || sm.DueTick > visibleTick))
                        Schedule(sm, wake: true, earliestTick: visibleTick);
                }
            }
        }

        // The cycle an input change reaches the synchronizers at. A change made
        // at a cycle the block has already run, because a CPU access brought it
        // ahead of the machine clock (CatchUpToAccessor), arrives now; a burst
        // of such changes on any pins keeps its spacing from there, so a fast
        // signal (a clock and its data) is delayed whole instead of having its
        // edges land on one cycle, where they would cancel.
        private ulong ArrivalCycle()
        {
            var now = CurrentCycle;
            var made = CycleAt(SignalTime.Of(machine));
            if(made >= now) return now;
            if(lateArrival < now || made < lateMade) lateArrival = now;
            else lateArrival += made - lateMade;
            lateMade = made;
            return lateArrival;
        }

        private ulong CycleAt(TimeInterval time)
        {
            if(time.Ticks <= cycleAnchorTime) return cycleAnchor;
            return cycleAnchor + (ulong)((BigInteger)(time.Ticks - cycleAnchorTime) * frequency / TimeInterval.TicksPerSecond);
        }

        private int InputDependency(StateMachine sm)
        {
            if(sm.JumpLoop) return ((sm.Instruction >> 5) & 7) == 6 ? (int)((sm.ExecControl >> 24) & 31) : -1;
            if((sm.Instruction >> 13) != 1) return -1;
            var source = (sm.Instruction >> 5) & 3;
            if(source == 3 && rp2350) return JumpPinWait(sm, sm.Instruction & 31);
            if(source > 1) return -1;
            return source == 0 ? sm.Instruction & 31 : (InBase(sm) + (sm.Instruction & 31)) & 31;
        }

        private void Reschedule(StateMachine sm)
        {
            // Debug stall flags reflect actual execution attempts. When W1C
            // clears an active FIFO stall, retry once on its next clock enable;
            // the failed instruction reasserts the flag and sleeps again.
            var ready = sm.Stalled && (CanResume(sm) || (CurrentStallDebugFlag(sm) & ~debug) != 0);
            ulong visibleTick = 0;
            if(sm.Stalled && !ready)
            {
                var pin = InputDependency(sm);
                if(pin >= 0 && (inputSyncBypass & (1u << pin)) == 0 && inputHistory[pin].Count != 0)
                    visibleTick = inputHistory[pin].Peek().Tick;
            }
            Schedule(sm, wake: ready || visibleTick != 0, earliestTick: visibleTick);
        }

        private static uint CurrentStallDebugFlag(StateMachine sm)
        {
            var argument = sm.Instruction & 0xff;
            var opcode = sm.Instruction >> 13;
            if(opcode == 2 && AutoPush(sm) && sm.InputCount >= PushThreshold(sm) && sm.Rx.Count >= RxCapacity(sm))
                return 1u << sm.Index;
            if(opcode == 3 && AutoPull(sm) && sm.OutputCount >= PullThreshold(sm) && sm.Tx.Count == 0)
                return 1u << (24 + sm.Index);
            if(opcode != 4 || (argument & 0x20) == 0) return 0;
            if((argument & 0x80) != 0)
            {
                var conditional = AutoPull(sm) || (argument & 0x40) != 0;
                return sm.Tx.Count == 0 && (!conditional || sm.OutputCount >= PullThreshold(sm)) ? 1u << (24 + sm.Index) : 0;
            }
            return sm.Rx.Count >= RxCapacity(sm) && ((argument & 0x40) == 0 || sm.InputCount >= PushThreshold(sm)) ? 1u << sm.Index : 0;
        }

        private void OnClockChanged()
        {
            SynchronizeTime();
            machine.ClockSource.ExecuteInLock(UpdateClock);
            PublishPending();
        }

        private void UpdateClock()
        {
            lock(SyncRoot)
            {
                // Cycles counted at the old frequency up to now stay counted.
                cycleAnchor = ClockCycle;
                cycleAnchorTime = machine.ClockSource.CurrentValue.Ticks;
                frequency = clocks.SystemFrequency;
                foreach(var sm in stateMachines) Reschedule(sm);
                BeginPinCycle();
                ResolvePinWrites();
            }
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
            if(sm.Steady)
            {
                RestoreSteadyLoop(sm);
                advance = false;
            }
            // Disabling first brings the clock up to date before the due tick is
            // computed; other machines due at this tick run in that update.
            sm.Timer.Enabled = false;
            sm.Scheduled = false;
            sm.PrefetchOnly = false;
            if(ResetHeld || frequency == 0 || (!Enabled(sm) && !sm.Forced) || (sm.Stalled && !wake)) return;
            var now = (BigInteger)CurrentCycle;
            var earliest = BigInteger.Max(now + 1, earliestTick);
            if(sm.Forced)
            {
                sm.DueTick = (ulong)earliest;
                sm.Scheduled = true;
                StartTimer(sm, sm.DueTick);
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
            sm.DueTick = (ulong)BigInteger.Min(ulong.MaxValue, due);
            sm.Scheduled = true;
            StartTimer(sm, sm.DueTick);
        }

        private ulong CyclesUntilExecution(StateMachine sm)
        {
            var remaining = sm.Edge256 - ((BigInteger)CurrentCycle + 1) * 256;
            return remaining < 0 ? 1 : (ulong)(remaining / Divider256(sm) + 1);
        }

        private BigInteger NextClockEdge(StateMachine sm, ulong divider = 0)
        {
            var earliest = ((BigInteger)CurrentCycle + 1) * 256;
            if(divider == 0) divider = Divider256(sm);
            var remaining = sm.Edge256 - earliest;
            return remaining >= 0 ? sm.Edge256 - (remaining / divider) * divider
                : sm.Edge256 + ((-remaining + divider - 1) / divider) * divider;
        }

        private void Restart(StateMachine sm)
        {
            sm.Steady = sm.Recording = sm.SteadyCandidate = false;
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

        // Restores the exact counter and next edge of a skipped counting loop at the
        // current cycle, before anything outside the machine changes or reads it.
        private void MaterializeCountLoop(StateMachine sm)
        {
            if(!sm.CountLoop) return;
            sm.CountLoop = false;
            var period = (BigInteger)Divider256(sm) * (ulong)sm.CountLoopCycles;
            // Iterations due up to and including the current cycle have run.
            var now = (BigInteger)CurrentCycle * 256 + 255;
            BigInteger done = 0;
            if(now >= sm.CountLoopStart) done = BigInteger.Min(sm.CountLoopValue, (now - sm.CountLoopStart) / period + 1);
            var remaining = (uint)(sm.CountLoopValue - done);
            if(sm.CountLoopUsesX) sm.X = remaining; else sm.Y = remaining;
            sm.Edge256 = sm.CountLoopStart + done * period;
        }

        // A pass of a loop is traced from the step a jump back or a wrap
        // reaches. If it returns there with the machine's state unchanged, with
        // no FIFO, IRQ or STATUS access, with the pins it read unchanged and its
        // pin writes leaving the latch as it was, every later pass repeats it
        // until one of those inputs changes. The machine is then parked:
        // RestoreSteadyLoop rebuilds the step and state it would have reached at
        // the current cycle, and it runs on from there.
        private void TraceLoop(StateMachine sm, LoopStep step, int pc, bool plain)
        {
            // OUT_STICKY re-asserts each step's own last write, which a parked pass does not track.
            if(!parkSteadyLoops || !plain || stepEffect || sm.JumpLoop || sm.CountLoop || sm.Injected || sm.RetryNextCycle
                || (sm.ExecControl & (1u << 17)) != 0)
            {
                sm.Recording = false;
                return;
            }
            if(!sm.Recording)
            {
                if(sm.PC <= pc) StartRecording(sm);
                return;
            }
            if((sm.TraceReadMask & stepReadMask & (sm.TraceReadValues ^ stepReadValues)) != 0 || sm.UndeliveredPinChanges != 0)
                sm.RecordingBroken = true;
            step.WriteMask = sm.WriteMask;
            step.WriteValue = sm.WriteValue;
            step.DirectionMask = sm.DirectionMask;
            step.DirectionValue = sm.DirectionValue;
            sm.Trace.Add(step);
            sm.TraceReadMask |= stepReadMask;
            sm.TraceReadValues |= stepReadValues;
            sm.TraceWriteMask |= sm.WriteMask | sm.DirectionMask;
            if(sm.PC == sm.LoopHead)
            {
                if(!sm.RecordingBroken && SameState(sm, sm.Trace[0])) sm.SteadyCandidate = true;
                else StartRecording(sm);
            }
            else if(sm.Trace.Count >= MaxLoopSteps) sm.Recording = false;
        }

        private static void StartRecording(StateMachine sm)
        {
            sm.Recording = true;
            sm.RecordingBroken = false;
            sm.LoopHead = sm.PC;
            sm.Trace.Clear();
            sm.TraceReadMask = sm.TraceReadValues = sm.TraceWriteMask = 0;
        }

        // Every write of the pass leaves the latch as it is now. A pass whose
        // writes held the latch only because another machine's writes landed
        // on the same cycles depends on that machine's phase, and is not steady.
        private bool WritesHoldLatch(StateMachine sm)
        {
            foreach(var step in sm.Trace)
            {
                if(((step.WriteValue ^ output) & step.WriteMask) != 0) return false;
                if(((step.DirectionValue ^ outputEnable) & step.DirectionMask) != 0) return false;
            }
            return true;
        }

        // No input change the traced pass did not see is on its way.
        private bool InputsSettled(uint mask, uint values)
        {
            for(var pin = 0; pin < 32; pin++)
            {
                if((mask & (1u << pin)) == 0) continue;
                // Another machine's read may already have taken a change the pass did not see.
                if(inputHistory[pin].Count != 0) return false;
                var level = (inputSyncBypass & (1u << pin)) != 0 ? gpio.GetInput(pin + gpioBase) : synchronizedInput[pin];
                if(level != ((values & (1u << pin)) != 0)) return false;
            }
            return true;
        }

        private static void ParkSteadyLoop(StateMachine sm)
        {
            sm.Recording = false;
            var head = sm.Trace[0].Edge256;
            var period = sm.Edge256 - head;
            if(period <= 0) return;
            var steps = sm.Trace.ToArray();
            for(var i = 0; i < steps.Length; i++) steps[i].Edge256 -= head;
            sm.SteadySteps = steps;
            sm.SteadyBase = head;
            sm.SteadyPeriod = period;
            sm.SteadyReadMask = sm.TraceReadMask;
            sm.SteadyWriteMask = sm.TraceWriteMask;
            sm.Steady = true;
            sm.Timer.Enabled = false;
            sm.Scheduled = false;
        }

        // Steps due up to and including the current cycle have run.
        private void RestoreSteadyLoop(StateMachine sm)
        {
            sm.Steady = false;
            var elapsed = (BigInteger)CurrentCycle * 256 + 255 - sm.SteadyBase;
            var passes = elapsed < 0 ? BigInteger.Zero : elapsed / sm.SteadyPeriod;
            var phase = elapsed - passes * sm.SteadyPeriod;
            var index = 0;
            while(index < sm.SteadySteps.Length && sm.SteadySteps[index].Edge256 <= phase) index++;
            if(index == sm.SteadySteps.Length)
            {
                index = 0;
                passes += 1;
            }
            var step = sm.SteadySteps[index];
            ClearExecution(sm);
            sm.PC = step.PC;
            sm.X = step.X;
            sm.Y = step.Y;
            sm.OSR = step.OSR;
            sm.ISR = step.ISR;
            sm.InputCount = step.InputCount;
            sm.OutputCount = step.OutputCount;
            sm.StickyMask = step.StickyMask;
            sm.StickyValue = step.StickyValue;
            sm.Edge256 = sm.SteadyBase + passes * sm.SteadyPeriod + step.Edge256;
        }

        // The step of a parked pass that executes on the given cycle, if any:
        // its pin writes still take part in that cycle's priority resolution.
        private static bool SteadyStepAt(StateMachine sm, ulong tick, out LoopStep found)
        {
            var start = (BigInteger)tick * 256 - sm.SteadyBase;
            found = default(LoopStep);
            if(start + 255 < 0) return false;
            var first = start < 0 ? BigInteger.Zero : start / sm.SteadyPeriod;
            for(var pass = first; pass <= first + 1; pass++)
            {
                foreach(var step in sm.SteadySteps)
                {
                    var edge = pass * sm.SteadyPeriod + step.Edge256;
                    if(edge < start || edge > start + 255) continue;
                    found = step;
                    return true;
                }
            }
            return false;
        }

        private void ResumeSteadyLoop(StateMachine sm)
        {
            if(sm.Steady) Schedule(sm, advance: false);
        }

        private void ResumeSteadyLoops()
        {
            foreach(var sm in stateMachines) ResumeSteadyLoop(sm);
        }

        private static LoopStep CaptureStep(StateMachine sm)
        {
            return new LoopStep
            {
                PC = sm.PC, X = sm.X, Y = sm.Y, OSR = sm.OSR, ISR = sm.ISR,
                InputCount = sm.InputCount, OutputCount = sm.OutputCount,
                StickyMask = sm.StickyMask, StickyValue = sm.StickyValue, Edge256 = sm.Edge256,
            };
        }

        private static bool SameState(StateMachine sm, LoopStep step)
        {
            return sm.PC == step.PC && sm.X == step.X && sm.Y == step.Y && sm.OSR == step.OSR && sm.ISR == step.ISR
                && sm.InputCount == step.InputCount && sm.OutputCount == step.OutputCount
                && sm.StickyMask == step.StickyMask && sm.StickyValue == step.StickyValue;
        }

        private static void ClearExecution(StateMachine sm)
        {
            sm.Pending = sm.Forced = sm.Injected = sm.Started = sm.Shifted = sm.IrqSet = sm.Stalled = sm.RetryNextCycle = sm.JumpLoop = false;
            sm.Exec = null;
        }

        protected override void OnBeforeAccess()
        {
            SynchronizeTime();
            CatchUpToAccessor();
        }

        private void SynchronizeTime() => RP2CpuTime.SyncInitiator(machine);

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
            // Pin changes this machine made that are not delivered yet (under SyncRoot).
            public int UndeliveredPinChanges;
            public bool Scheduled, PrefetchOnly;
            public int PC, InputCount, OutputCount, Delay, LoopCycles;
            public ushort Instruction;
            public ushort? Exec;
            public bool Pending, Forced, Injected, Started, Shifted, IrqSet, Stalled, RetryNextCycle, JumpLoop;
            public BigInteger Edge256;
            // A JMP X--/Y-- to itself skipped to its last iteration; see MaterializeCountLoop.
            public bool CountLoop, CountLoopUsesX;
            public BigInteger CountLoopStart;
            public uint CountLoopValue;
            public int CountLoopCycles;
            // A loop pass being traced, or the parked steady pass; see TraceLoop.
            public bool Recording, RecordingBroken, SteadyCandidate, Steady;
            public int LoopHead;
            public readonly List<LoopStep> Trace = new List<LoopStep>();
            public uint TraceReadMask, TraceReadValues, TraceWriteMask, SteadyReadMask, SteadyWriteMask;
            // Steps with Edge256 relative to SteadyBase, the head of the parked pass.
            public LoopStep[] SteadySteps;
            public BigInteger SteadyBase, SteadyPeriod;
        }

        // The machine state before a step of a traced loop, and its clock edge.
        private struct LoopStep
        {
            public int PC, InputCount, OutputCount;
            public uint X, Y, OSR, ISR, StickyMask, StickyValue;
            // The pin strobes the step made, which a parked pass replays.
            public uint WriteMask, WriteValue, DirectionMask, DirectionValue;
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
            public uint Owners;
            // When a pin change happened.
            public TimeInterval Time;
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
        // clk_sys cycles elapsed: cycleAnchor at virtual time cycleAnchorTime,
        // counted at the current frequency since then. Cycle c starts at the
        // first virtual-time tick at which CurrentCycle reads c.
        private ulong ClockCycle => cycleAnchor + (ulong)((BigInteger)(machine.ClockSource.CurrentValue.Ticks - cycleAnchorTime) * frequency / TimeInterval.TicksPerSecond);

        // The machine clock's cycle, or the cycle an accessing CPU has brought
        // the machines to (CatchUpToAccessor), whichever is later.
        private ulong CurrentCycle => Math.Max(ClockCycle, aheadCycle);

        private ulong CycleStart(ulong cycle)
        {
            var cycles = (BigInteger)cycle - cycleAnchor;
            return cycleAnchorTime + (ulong)((cycles * TimeInterval.TicksPerSecond + frequency - 1) / frequency);
        }

        // The start of the cycle a pin change is made on, which a CPU access may
        // have brought ahead of the machine clock (CatchUpToAccessor).
        private TimeInterval PinChangeTime()
        {
            var now = machine.ClockSource.CurrentValue;
            if(frequency == 0) return now;
            var start = TimeInterval.FromTicks(CycleStart(CurrentCycle));
            return start > now ? start : now;
        }

        // Fires the machine's timer on the first tick of its due cycle.
        private void StartTimer(StateMachine sm, ulong due)
        {
            if(frequency == 0) return;
            var now = machine.ClockSource.CurrentValue.Ticks;
            var start = CycleStart(due);
            sm.Timer.Restart(start > now ? start - now : 1);
        }

        private ulong cycleAnchor;
        private ulong cycleAnchorTime;
        private ulong aheadCycle;
        private RP2040_DMA dma;
        // The latest late input change: the cycle it was made at, and the one it arrived at.
        private ulong lateMade, lateArrival;
        private readonly int function;
        private readonly bool rp2350;
        private readonly HashSet<string> warned = new HashSet<string>();
        private int gpioBase;
        // RP2350 moves INTR and IRQ0/IRQ1 INTE/INTF/INTS from 0x128 to 0x16c.
        private const long V1InterruptOffset = 0x44;
        private uint control, debug, irqFlags, inputSyncBypass, output, outputEnable;
        private ulong frequency;
        private bool publishing;
        private bool executingCycle;
        private bool parkSteadyLoops = true;
        // The cycle whose execution strobes the machines' write masks hold.
        private ulong pinCycle = ulong.MaxValue;
        // What the executing step read and whether it touched a FIFO, an IRQ
        // flag or STATUS; see TraceLoop.
        private bool trackingReads, stepEffect;
        private uint stepReadMask, stepReadValues;
        private const int MaxLoopSteps = 32;
        private uint cycleIRQFlags, cycleIRQSet, cycleIRQClear;
    }
}
