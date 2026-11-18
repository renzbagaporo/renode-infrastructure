//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.I2C
{
    // The SDA and SCL pads of an RP2_I2C with no DelayedI2CBus: the block's
    // byte-level bus events become open-drain pin levels on the GPIO bank,
    // and the levels other devices put on the pads become bus events.
    //
    // As a controller's bus it plays each event's waveform at half the SCL
    // bit period per step (START, the eight bits with SDA changed while SCL
    // is low, the SDA hold time (IC_SDA_HOLD) after SCL falls, the ninth clock with the target's ACK sampled while SCL is
    // high, a read's bits sampled the same way, the controller's ACK or NACK
    // of a read byte before the next byte, a repeated START or the STOP) and
    // waits while another device holds SCL low; SCL pulled low again during
    // its high time restarts that high time once it rises. It answers with the ACK or
    // the byte it sampled.
    //
    // As a target's bus it follows START and STOP (SDA changing while SCL is
    // high) and samples bits on SCL rising. It holds SCL low after a byte
    // until the target answers it, drives the ACK or NACK on the ninth clock,
    // and holds SCL low before a read byte until the target supplies it, then
    // shifts it out on SCL falling edges. Arbitration between controllers is
    // not modelled.
    internal sealed class RP2_I2CPins : IDelayedI2CBusEndpoint
    {
        public RP2_I2CPins(IMachine machine, IDelayedI2CBusEndpoint owner, RP2_GPIOBank0 gpio, int index,
            Func<ulong> halfPeriodCycles, Func<ulong> holdCycles, Func<ulong> frequency, Func<bool> targetMode, Func<ulong> spikeCycles)
        {
            this.spikeCycles = spikeCycles;
            this.holdCycles = holdCycles;
            this.machine = machine;
            this.owner = owner;
            this.gpio = gpio;
            this.index = index;
            this.halfPeriodCycles = halfPeriodCycles;
            this.frequency = frequency;
            this.targetMode = targetMode;
            timer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency()), this, "I2C pins",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += OnStep;
            filter = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency()), this, "I2C spike filter",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            filter.LimitReached += OnFiltered;
            gpio.InputChanged += OnInput;
        }

        public void Reset() => Reset(true);

        // A reset releases the pads; with flush false they are released at the
        // next pin activity, for a caller holding its owner's state monitor.
        public void Reset(bool flush)
        {
            lock(sync)
            {
                timer.Enabled = false;
                filter.Enabled = false;
                pendingInputs.Clear();
                answers.Clear();
                filteredSda = filteredScl = true;
                ops.Clear();
                waitingForScl = false;
                highPhase = false;
                holdPending = false;
                readPending = false;
                state = TargetState.Idle;
                Release();
            }
            if(flush) Flush();
        }

        // Whether the block's SDA and SCL are on pads with the I2C function.
        public bool Selected
        {
            get
            {
                FindPins(out var sda, out var _);
                return sda >= 0;
            }
        }

        public void AttachToBus(DelayedI2CBus bus, int sdaPin, int sclPin)
        {
        }

        // The owner's events: a controller's requests or a target's answers.
        // A register access makes them, at the accessing CPU's time, which
        // leads the clock the block's own timing runs on; the block acts on
        // them at its next ic_clk cycle, so every pad change it makes is on
        // that clock and keeps its order against the ones before and after.
        public void OnBusEvent(I2CBusEvent busEvent)
        {
            lock(sync)
            {
                FindPins(out sdaPin, out sclPin);
                if(sdaPin < 0) return;
                switch(busEvent.Type)
                {
                case I2CBusEventType.Start: QueueStart(busEvent); break;
                case I2CBusEventType.Write: QueueWrite(busEvent); break;
                case I2CBusEventType.Read: QueueRead(busEvent); break;
                case I2CBusEventType.Stop: QueueStop(); break;
                case I2CBusEventType.Acknowledge:
                case I2CBusEventType.Data:
                    answers.Enqueue(busEvent);
                    break;
                }
                if(!timer.Enabled && !waitingForScl) Schedule(1);
            }
        }

        // Controller -----------------------------------------------------

        private void QueueStart(I2CBusEvent request)
        {
            if(readPending) QueueAcknowledgeBit(false);
            if(controllerHolding)
            {
                // A repeated START: release SDA, then SCL, then START.
                ops.Enqueue(Op.Sda(true));
                ops.Enqueue(Op.Wait());
                ops.Enqueue(Op.Scl(true));
                ops.Enqueue(Op.WaitScl());
                ops.Enqueue(Op.WaitHigh());
            }
            ops.Enqueue(Op.Sda(false));
            ops.Enqueue(Op.Wait());
            ops.Enqueue(Op.Scl(false));
            ops.Enqueue(Op.Hold());
            QueueByte((byte)((request.Address << 1) | (request.IsRead ? 1 : 0)));
            QueueAcknowledgeSample(request.Transfer);
        }

        private void QueueWrite(I2CBusEvent request)
        {
            QueueByte(request.Data);
            QueueAcknowledgeSample(request.Transfer);
        }

        private void QueueRead(I2CBusEvent request)
        {
            if(readPending) QueueAcknowledgeBit(true);
            ops.Enqueue(Op.Sda(true));
            for(var bit = 0; bit < 8; bit++)
            {
                ops.Enqueue(Op.Wait());
                ops.Enqueue(Op.Scl(true));
                ops.Enqueue(Op.WaitScl());
                ops.Enqueue(Op.WaitHigh());
                ops.Enqueue(Op.SampleBit());
                ops.Enqueue(Op.Scl(false));
            }
            ops.Enqueue(Op.AnswerData(request.Transfer));
            ops.Enqueue(Op.PendRead());
        }

        private void QueueStop()
        {
            if(!controllerHolding && ops.Count == 0) return;
            if(readPending) QueueAcknowledgeBit(false);
            ops.Enqueue(Op.Sda(false));
            ops.Enqueue(Op.Wait());
            ops.Enqueue(Op.Scl(true));
            ops.Enqueue(Op.WaitScl());
            ops.Enqueue(Op.WaitHigh());
            ops.Enqueue(Op.Sda(true));
            ops.Enqueue(Op.Unhold());
        }

        private void QueueByte(byte value)
        {
            for(var bit = 7; bit >= 0; bit--)
            {
                ops.Enqueue(Op.Sda(((value >> bit) & 1) != 0));
                ops.Enqueue(Op.Wait());
                ops.Enqueue(Op.Scl(true));
                ops.Enqueue(Op.WaitScl());
                ops.Enqueue(Op.WaitHigh());
                ops.Enqueue(Op.Scl(false));
            }
        }

        // The ninth clock of a byte the controller sent: the target's ACK.
        private void QueueAcknowledgeSample(ulong transfer)
        {
            ops.Enqueue(Op.Sda(true));
            ops.Enqueue(Op.Wait());
            ops.Enqueue(Op.Scl(true));
            ops.Enqueue(Op.WaitScl());
            ops.Enqueue(Op.WaitHigh());
            ops.Enqueue(Op.SampleAcknowledge());
            ops.Enqueue(Op.Scl(false));
            ops.Enqueue(Op.AnswerAcknowledge(transfer));
        }

        // The ninth clock of a byte the controller read: its ACK or NACK.
        private void QueueAcknowledgeBit(bool acknowledge)
        {
            ops.Enqueue(Op.Sda(!acknowledge));
            ops.Enqueue(Op.Wait());
            ops.Enqueue(Op.Scl(true));
            ops.Enqueue(Op.WaitScl());
            ops.Enqueue(Op.WaitHigh());
            ops.Enqueue(Op.Scl(false));
            ops.Enqueue(Op.Sda(true));
            ops.Enqueue(Op.UnpendRead());
        }

        private void Schedule(ulong cycles)
        {
            timer.Enabled = false;
            timer.Frequency = Math.Max(1ul, frequency());
            timer.Limit = cycles;
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void PushFront(Op op)
        {
            var rest = ops.ToArray();
            ops.Clear();
            ops.Enqueue(op);
            foreach(var other in rest) ops.Enqueue(other);
        }

        private void OnStep()
        {
            lock(sync)
            {
                while(answers.Count != 0)
                {
                    var answer = answers.Dequeue();
                    if(answer.Type == I2CBusEventType.Acknowledge) TargetAcknowledge(answer.Acknowledged);
                    else TargetData(answer.Data);
                }
                highPhase = false;
                RunOps();
            }
            Flush();
        }

        private void RunOps()
        {
            while(ops.Count != 0)
            {
                var op = ops.Dequeue();
                switch(op.Kind)
                {
                case OpKind.Sda:
                    if(holdPending)
                    {
                        // SDA changes the hold time after SCL falls, as
                        // IC_SDA_HOLD sets, never on the same instant.
                        holdPending = false;
                        PushFront(op);
                        Schedule(Math.Max(1ul, holdCycles()));
                        return;
                    }
                    Drive(sdaPin, op.Level);
                    break;
                case OpKind.Scl:
                    Drive(sclPin, op.Level);
                    holdPending = !op.Level;
                    break;
                case OpKind.WaitHigh:
                    // SCL's high time; another device pulling SCL low in it
                    // restarts it once SCL rises again (clock synchronization).
                    highPhase = true;
                    goto case OpKind.Wait;
                case OpKind.Wait:
                    holdPending = false;
                    Schedule(Math.Max(1ul, halfPeriodCycles()));
                    return;
                case OpKind.WaitScl:
                    // Another device stretching the clock holds SCL low.
                    if(!Level(sclPin))
                    {
                        waitingForScl = true;
                        return;
                    }
                    break;
                case OpKind.SampleBit: shift = (byte)((shift << 1) | (Level(sdaPin) ? 1 : 0)); break;
                case OpKind.SampleAcknowledge: acknowledgeSample = !Level(sdaPin); break;
                case OpKind.AnswerAcknowledge: Deliver(I2CBusEvent.Acknowledge(op.Transfer, acknowledgeSample)); break;
                case OpKind.AnswerData: Deliver(I2CBusEvent.Reply(op.Transfer, shift)); break;
                case OpKind.PendRead: readPending = true; break;
                case OpKind.UnpendRead: readPending = false; break;
                case OpKind.Hold: controllerHolding = true; break;
                case OpKind.Unhold: controllerHolding = false; break;
                }
            }
        }

        // Target ---------------------------------------------------------

        // The pads reach the block through its spike suppression (IC_FS_SPKLEN):
        // a change is taken SPKLEN ic_clk cycles after it, and a pulse shorter
        // than that, a change undone before it is taken, never is.
        private void OnInput(int pin, bool level)
        {
            lock(sync)
            {
                FindPins(out var sda, out var scl);
                if(sda < 0 || (pin != sda && pin != scl)) return;
                var undone = -1;
                for(var i = pendingInputs.Count - 1; i >= 0; i--)
                {
                    if(pendingInputs[i].Pin != pin) continue;
                    if(pendingInputs[i].Level != level) undone = i;
                    break;
                }
                if(undone >= 0) pendingInputs.RemoveAt(undone);
                else pendingInputs.Add((pin, level));
                if(pendingInputs.Count == 0) filter.Enabled = false;
                else if(!filter.Enabled)
                {
                    filter.Frequency = Math.Max(1ul, frequency());
                    filter.Limit = Math.Max(1ul, spikeCycles());
                    filter.ResetValue();
                    filter.Enabled = true;
                }
            }
        }

        private void OnFiltered()
        {
            lock(sync)
            {
                filter.Enabled = false;
                var taken = pendingInputs.ToArray();
                pendingInputs.Clear();
                foreach(var input in taken) TakeInput(input.Pin, input.Level);
            }
            Flush();
        }

        private void TakeInput(int pin, bool level)
        {
            FindPins(out var sda, out var scl);
            if(sda < 0 || (pin != sda && pin != scl)) return;
            sdaPin = sda;
            sclPin = scl;
            // START, STOP and data bits are told from the filtered lines,
            // which the filter delays alike, so their order holds.
            if(pin == scl) filteredScl = level;
            else filteredSda = level;
            if(pin == scl)
            {
                if(highPhase && !level && !sclLow)
                {
                    // Held low again before the high time ended.
                    timer.Enabled = false;
                    highPhase = false;
                    PushFront(Op.WaitHigh());
                    PushFront(Op.WaitScl());
                    waitingForScl = true;
                }
                else if(waitingForScl && level)
                {
                    waitingForScl = false;
                    RunOps();
                }
                if(!controllerHolding && targetMode()) OnTargetClock(level);
            }
            else if(!controllerHolding && targetMode() && filteredScl)
            {
                if(!level) OnTargetStart();
                else OnTargetStop();
            }
        }

        private void OnTargetStart()
        {
            repeated = state != TargetState.Idle;
            transfer++;
            state = TargetState.Address;
            bits = 0;
            shift = 0;
        }

        private void OnTargetStop()
        {
            if(state == TargetState.Idle) return;
            state = TargetState.Idle;
            Release();
            Deliver(I2CBusEvent.Stop(transfer));
        }

        private void OnTargetClock(bool rising)
        {
            switch(state)
            {
            case TargetState.Address:
            case TargetState.Receive:
                if(rising)
                {
                    shift = (byte)((shift << 1) | (filteredSda ? 1 : 0));
                    bits++;
                }
                else if(bits == 8)
                {
                    // Hold SCL until the target decides on its ACK.
                    Drive(sclPin, false);
                    var address = state == TargetState.Address;
                    readTransfer = address && (shift & 1) != 0;
                    state = TargetState.AwaitAcknowledge;
                    Deliver(address ? I2CBusEvent.Start(transfer, shift >> 1, (shift & 1) != 0, repeated) : I2CBusEvent.Write(transfer, shift));
                }
                break;
            case TargetState.Acknowledging:
                if(rising) break;
                // The ninth clock ends: release SDA.
                Drive(sdaPin, true);
                if(!acknowledgedLast) state = TargetState.Ignore;
                else if(readTransfer) RequestRead();
                else
                {
                    state = TargetState.Receive;
                    bits = 0;
                    shift = 0;
                }
                break;
            case TargetState.Transmit:
                if(rising) break;
                if(++bits < 8) Drive(sdaPin, ((outgoing >> (7 - bits)) & 1) != 0);
                else
                {
                    Drive(sdaPin, true);
                    state = TargetState.ControllerAcknowledge;
                }
                break;
            case TargetState.ControllerAcknowledge:
                if(rising) controllerAcknowledged = !filteredSda;
                else if(controllerAcknowledged) RequestRead();
                else state = TargetState.Ignore;
                break;
            }
        }

        private void RequestRead()
        {
            // Hold SCL until the target supplies the byte.
            Drive(sclPin, false);
            state = TargetState.AwaitData;
            Deliver(I2CBusEvent.Read(transfer));
        }

        private void TargetAcknowledge(bool acknowledged)
        {
            if(state != TargetState.AwaitAcknowledge) return;
            acknowledgedLast = acknowledged;
            Drive(sdaPin, !acknowledged);
            state = TargetState.Acknowledging;
            Drive(sclPin, true);
        }

        private void TargetData(byte data)
        {
            if(state != TargetState.AwaitData) return;
            outgoing = data;
            bits = 0;
            Drive(sdaPin, (data & 0x80) != 0);
            state = TargetState.Transmit;
            Drive(sclPin, true);
        }

        // Pins -----------------------------------------------------------

        // GPIO function 3: SDA on even pins and SCL on the odd pin above, for
        // block (pin / 2) % 2; the lowest pair routed to this block.
        private void FindPins(out int sda, out int scl)
        {
            sda = scl = -1;
            for(var pin = index * 2; pin + 1 < 48; pin += 4)
            {
                if(gpio.GetFunction(pin) != I2CFunction || gpio.GetFunction(pin + 1) != I2CFunction) continue;
                sda = pin;
                scl = pin + 1;
                return;
            }
        }

        // Open drain: low drives the pad, high releases it.
        private void Drive(int pin, bool high)
        {
            if(pin < 0) return;
            if(pin == sdaPin) sdaLow = !high;
            else sclLow = !high;
            // The block's pad changes all come from its clock handlers, which may
            // run inside a CPU access that synced the clock forward; each takes
            // the clock's time, not that CPU's, so two changes a hold time apart
            // keep their gap and none goes ahead of the clock.
            var at = machine.ClockSource.CurrentValue;
            actions.Enqueue(() =>
            {
                using(SignalTime.At(machine, at)) gpio.SetPeripheralOutput(I2CFunction, pin, false, !high);
            });
        }

        private void Release()
        {
            if(sdaPin >= 0) Drive(sdaPin, true);
            if(sclPin >= 0) Drive(sclPin, true);
            controllerHolding = false;
        }

        // The line level: low while this block or another device drives it.
        private bool Level(int pin)
        {
            if(pin == sdaPin && sdaLow) return false;
            if(pin == sclPin && sclLow) return false;
            return gpio.GetPadLevel(pin) ?? true;
        }

        private void Deliver(I2CBusEvent busEvent)
        {
            var from = busEvent.WithSender(this);
            actions.Enqueue(() => owner.OnBusEvent(from));
        }

        // Pin changes and events to the owner, outside this lock.
        private void Flush()
        {
            while(true)
            {
                Action action;
                lock(sync)
                {
                    if(flushing || actions.Count == 0) return;
                    flushing = true;
                    action = actions.Dequeue();
                }
                try { action(); }
                finally { lock(sync) flushing = false; }
            }
        }

        private readonly IMachine machine;
        private readonly IDelayedI2CBusEndpoint owner;
        private readonly RP2_GPIOBank0 gpio;
        private readonly int index;
        private readonly Func<ulong> halfPeriodCycles;
        private readonly Func<ulong> holdCycles;
        private readonly Func<ulong> frequency;
        private readonly Func<bool> targetMode;
        private readonly LimitTimer timer;
        private readonly LimitTimer filter;
        private readonly Func<ulong> spikeCycles;
        private readonly List<(int Pin, bool Level)> pendingInputs = new List<(int Pin, bool Level)>();
        private readonly Queue<Op> ops = new Queue<Op>();
        private readonly Queue<Action> actions = new Queue<Action>();
        private readonly Queue<I2CBusEvent> answers = new Queue<I2CBusEvent>();
        private readonly object sync = new object();
        private int sdaPin = -1, sclPin = -1;
        private bool sdaLow, sclLow, waitingForScl, highPhase, holdPending, readPending, controllerHolding, acknowledgeSample, flushing;
        private bool repeated, readTransfer, acknowledgedLast, controllerAcknowledged;
        private bool filteredSda = true, filteredScl = true;
        private TargetState state;
        private ulong transfer;
        private int bits;
        private byte shift, outgoing;

        private const int I2CFunction = 3;

        private enum TargetState
        {
            Idle,
            Address,
            Receive,
            AwaitAcknowledge,
            Acknowledging,
            AwaitData,
            Transmit,
            ControllerAcknowledge,
            Ignore,
        }

        private enum OpKind
        {
            Sda,
            Scl,
            Wait,
            WaitHigh,
            WaitScl,
            SampleBit,
            SampleAcknowledge,
            AnswerAcknowledge,
            AnswerData,
            PendRead,
            UnpendRead,
            Hold,
            Unhold,
        }

        private readonly struct Op
        {
            public static Op Sda(bool level) => new Op(OpKind.Sda, level, 0);
            public static Op Scl(bool level) => new Op(OpKind.Scl, level, 0);
            public static Op Wait() => new Op(OpKind.Wait, false, 0);
            public static Op WaitScl() => new Op(OpKind.WaitScl, false, 0);
            public static Op WaitHigh() => new Op(OpKind.WaitHigh, false, 0);
            public static Op SampleBit() => new Op(OpKind.SampleBit, false, 0);
            public static Op SampleAcknowledge() => new Op(OpKind.SampleAcknowledge, false, 0);
            public static Op AnswerAcknowledge(ulong transfer) => new Op(OpKind.AnswerAcknowledge, false, transfer);
            public static Op AnswerData(ulong transfer) => new Op(OpKind.AnswerData, false, transfer);
            public static Op PendRead() => new Op(OpKind.PendRead, false, 0);
            public static Op UnpendRead() => new Op(OpKind.UnpendRead, false, 0);
            public static Op Hold() => new Op(OpKind.Hold, false, 0);
            public static Op Unhold() => new Op(OpKind.Unhold, false, 0);

            private Op(OpKind kind, bool level, ulong transfer)
            {
                Kind = kind;
                Level = level;
                Transfer = transfer;
            }

            public readonly OpKind Kind;
            public readonly bool Level;
            public readonly ulong Transfer;
        }
    }
}
