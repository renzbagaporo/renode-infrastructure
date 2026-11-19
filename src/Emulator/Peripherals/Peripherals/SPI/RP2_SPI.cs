//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SPI
{
    // RP2040/RP2350 SPI is an ARM PL022 SSP with 8-entry FIFOs. This model
    // implements the Motorola frame format. As a master it drives SCK, TX and
    // FSS on its GPIO function 1 pins with the configured polarity, phase,
    // frame size and bit rate (clk_peri / (CPSDVSR * (1 + SCR))), and samples
    // RX at each sampling edge. With SPH=0, FSS pulses high for one SCK
    // period between back-to-back frames; with SPH=1 it stays low. When the target is reached through delayed
    // wires, InputDelayNanoseconds moves each sample by the round trip so the
    // target's response to that edge is read. As a slave it follows CSn and
    // SCK on its input pins, samples RX on the sampling edges, and drives TX
    // while selected (unless SOD is set) from its transmit FIFO, sending zeros
    // when that is empty. The TI and Microwire formats are not modeled.
    public sealed class RP2_SPI : RP2APBPeripheral
    {
        // As a master: called as each frame starts with its transmitted word,
        // its size in bits and its start time; a word returned is what the
        // frame receives, in place of the samples of the receive pin. For a
        // target that is not on the pins, such as a native Metalclaw bridge.
        public Func<ushort, int, TimeInterval, ushort?> FrameTarget { get; set; }

        public RP2_SPI(IMachine machine, IRP2ClockSource clocks = null, ulong frequency = 125000000,
            RP2_GPIOBank0 gpio = null, int index = -1, ulong inputDelayNanoseconds = 0) : base(machine)
        {
            this.clocks = clocks;
            fallbackFrequency = frequency;
            // Chips register one after another while a platform loads.
            machine.PeripheralsChanged += (_, __) => sharedMachine = null;
            this.gpio = index >= 0 ? gpio : null;
            InputDelayNanoseconds = inputDelayNanoseconds;
            var rxPins = new List<int>();
            var pins = new List<int>[3] { new List<int>(), new List<int>(), new List<int>() };
            if(this.gpio != null)
            {
                // GPIO function 1 repeats SPI0/SPI1 in blocks of eight pins;
                // RX, CSn, SCK and TX follow pin % 4.
                for(var pin = 0; pin < MaximumPins; pin++)
                {
                    if(pin / 8 % 2 != index) continue;
                    switch(pin % 4)
                    {
                    case 0: rxPins.Add(pin); break;
                    case 1: pins[FssLine].Add(pin); break;
                    case 2: pins[ClockLine].Add(pin); break;
                    case 3: pins[DataLine].Add(pin); break;
                    }
                }
                this.gpio.InputChanged += OnPinInput;
            }
            this.rxPins = rxPins.ToArray();
            outputPins = new[] { pins[0].ToArray(), pins[1].ToArray(), pins[2].ToArray() };
            tx = new Queue<ushort>();
            rx = new Queue<ushort>();
            samples = new Queue<Sample>();
            frames = new Dictionary<ulong, Frame>();
            notifications = new Queue<Notification>();
            lineLevels = new bool[3];
            lineEnabled = new bool[3];
            signalStates = new bool[3];
            edgeTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "serial clock edge",
                limit: 1, workMode: WorkMode.Periodic, eventEnabled: true);
            sampleTimer = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, "delayed receive sample",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            timeoutTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "receive timeout",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            edgeTimer.LimitReached += OnClockEdge;
            sampleTimer.LimitReached += ProcessSamples;
            timeoutTimer.LimitReached += OnReceiveTimeout;
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            Reset();
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    edgeTimer.Reset();
                    sampleTimer.Reset();
                    timeoutTimer.Reset();
                    tx.Clear();
                    rx.Clear();
                    samples.Clear();
                    frames.Clear();
                    notifications.Clear();
                    targets.Clear();
                    generation++;
                    shifting = false;
                    pulseHalves = 0;
                    slaveSelected = slaveLoaded = false;
                    slaveBits = 0;
                    slaveIn = slaveOut = 0;
                    control0 = control1 = prescale = interruptMask = rawInterrupts = dmaControl = 0;
                    UpdateClock();
                    UpdatePins();
                    UpdateSignals();
                }
            });
            PublishPending();
        }

        // Round trip from a sampling edge to the target's response at RX.
        public ulong InputDelayNanoseconds { get; set; }

        public GPIO IRQ { get; } = new GPIO();
        public GPIO RxDMA { get; } = new GPIO();
        public GPIO TxDMA { get; } = new GPIO();

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess()
        {
            RP2CpuTime.SyncInitiator(machine);
        }

        protected override void OnAccessCompleted() => PublishPending();

        protected override void OnResetHeldChanged()
        {
            UpdateClock();
            UpdatePins();
            UpdateSignals();
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0xfe0 && offset <= 0xffc && (offset & 3) == 0) return Identification[(offset - 0xfe0) / 4];
            switch(offset)
            {
            case 0x00: return control0;
            case 0x04: return control1;
            case 0x08:
                if(rx.Count == 0) return 0;
                var value = rx.Dequeue();
                RestartTimeout();
                UpdateSignals();
                return value;
            case 0x0c:
                return (tx.Count == 0 ? 1u : 0) | (tx.Count < Capacity ? 2u : 0) | (rx.Count != 0 ? 4u : 0)
                    | (rx.Count == Capacity ? 8u : 0) | (Busy ? 0x10u : 0);
            case 0x10: return prescale;
            case 0x14: return interruptMask;
            case 0x18: return RawInterrupts;
            case 0x1c: return RawInterrupts & interruptMask;
            case 0x24: return dmaControl;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            // Atomic aliases perform a downstream read and write, including
            // the read side effects of the selected register.
            if(operation != AtomicOperation.Write) value = ApplyOperation(ReadRegister(offset), value, operation);
            switch(offset)
            {
            case 0x00:
                control0 = value & 0xffff;
                if(((control0 >> 4) & 3) != 0) this.Log(LogLevel.Warning, "Only the Motorola SPI frame format is modeled");
                UpdateClock();
                break;
            case 0x04:
                control1 = value & 0xf;
                if(!SlaveMode) slaveSelected = false;
                UpdatePins();
                TryStartFrame(AccessorTime());
                break;
            case 0x08:
                if(tx.Count < Capacity) tx.Enqueue((ushort)(value & FrameMask));
                else this.Log(LogLevel.Warning, "Transmit FIFO write while full");
                TryStartFrame(AccessorTime());
                break;
            case 0x10:
                prescale = value & 0xfe;
                UpdateClock();
                break;
            case 0x14:
                interruptMask = value & 0xf;
                break;
            case 0x20:
                rawInterrupts &= ~(value & 3);
                break;
            case 0x24:
                dmaControl = value & 3;
                break;
            default:
                if(offset < 0xfe0 || offset > 0xffc) this.LogUnhandledWrite(offset, value);
                break;
            }
            UpdateSignals();
        }

        // A frame a CPU's write starts begins at that CPU's time (start), which
        // leads the machine clock when another RP2 chip shares the machine (see
        // AccessorTime): its first edge waits out the lead, and its select and
        // first bit carry that time, so they follow the chip select the CPU
        // drove through SIO just before.
        private void TryStartFrame(TimeInterval? start = null)
        {
            if(shifting || tx.Count == 0 || !Enabled || (control1 & 4) != 0 || HalfPeriod == 0 || Frequency == 0) return;
            frame = new Frame(++frameId, tx.Dequeue(), FrameBits);
            frames[frame.Id] = frame;
            shifting = true;
            pulseHalves = 0;
            edge = 0;
            var now = machine.ClockSource.CurrentValue;
            var lead = start.HasValue && start.Value > now ? start.Value - now : TimeInterval.Empty;
            var leadCycles = (ulong)((System.Numerics.BigInteger)lead.Ticks * Frequency / TimeInterval.TicksPerSecond);
            edgeTimer.Enabled = false;
            edgeTimer.Limit = HalfPeriod + leadCycles;
            edgeTimer.ResetValue();
            edgeTimer.Enabled = true;
            lineTime = leadCycles != 0 ? start : null;
            SetLine(FssLine, false);
            // With SPH=0 the first bit is valid half a clock before the first edge.
            if(!SecondEdgeSamples) SetLine(DataLine, frame.Bit(0));
            lineTime = null;
            // A frame target answers the whole frame at its start, with the
            // word the master then receives; the pins still carry the frame.
            // It is asked once the select and first bit are published
            // (PublishPending), so it sees the frame after its select, and
            // answers before the first sampling edge half a clock later. In
            // loopback (SSPCR1.LBM) the block receives its own frame and
            // drives no pins, so no target sees it.
            if(!Loopback && FrameTarget != null) targets.Enqueue(Tuple.Create(frame, now + lead));
        }

        // The time of the CPU making the current access, in a machine holding
        // several RP2 chips; null otherwise, as the frame then starts at the
        // machine clock.
        private TimeInterval? AccessorTime()
        {
            if(!sharedMachine.HasValue) sharedMachine = machine.GetPeripheralsOfType<RP2_Chip>().Skip(1).Any();
            return sharedMachine.Value ? RP2CpuTime.InitiatorTime(machine) : null;
        }

        private bool? sharedMachine;
        private TimeInterval? lineTime;

        private void OnClockEdge()
        {
            lock(SyncRoot)
            {
                if(!shifting)
                {
                    edgeTimer.Enabled = false;
                    return;
                }
                // The first edge of a frame started ahead of the clock is past.
                if(edgeTimer.Limit != HalfPeriod) edgeTimer.Limit = HalfPeriod;
                if(pulseHalves > 0)
                {
                    // The end of FSS's pulse between back-to-back frames.
                    if(--pulseHalves == 0)
                    {
                        shifting = false;
                        TryStartFrame();
                        if(!shifting) SetLine(FssLine, true);
                        UpdateSignals();
                    }
                }
                else OnFrameEdge();
            }
            PublishPending();
        }

        private void OnFrameEdge()
        {
            edge++;
            var leading = edge % 2 == 1;
            var bit = (edge - 1) / 2;
            SetLine(ClockLine, leading ? !IdleHigh : IdleHigh);
            if(leading == !SecondEdgeSamples)
            {
                Capture(frame, bit);
            }
            else if(!SecondEdgeSamples)
            {
                if(bit + 1 < frame.Bits) SetLine(DataLine, frame.Bit(bit + 1));
            }
            else
            {
                SetLine(DataLine, frame.Bit(bit));
            }
            if(edge == frame.Bits * 2)
            {
                EndFrame(frame);
                if(!SecondEdgeSamples && tx.Count != 0 && Enabled)
                {
                    // With SPH=0, FSS pulses high for one SCK period
                    // between back-to-back frames, so a target sees
                    // every frame as a select window of its own.
                    SetLine(FssLine, true);
                    pulseHalves = 2;
                }
                else
                {
                    shifting = false;
                    edgeTimer.Enabled = false;
                    TryStartFrame();
                    if(!shifting) SetLine(FssLine, true);
                }
            }
            UpdateSignals();
        }

        private void Capture(Frame target, int bit)
        {
            var now = machine.ClockSource.CurrentValue;
            var delay = Loopback ? 0 : InputDelayNanoseconds;
            if(delay == 0)
            {
                Store(target, bit, Loopback ? target.Bit(bit) : rxLevel);
                return;
            }
            samples.Enqueue(new Sample(now + TimeInterval.FromTicks(delay * TimeInterval.TicksPerNanosecond), target.Id, bit));
            if(!sampleTimer.Enabled) ArmSampleTimer(now);
        }

        private void ArmSampleTimer(TimeInterval now)
        {
            if(samples.Count == 0) return;
            var due = samples.Peek().Due;
            sampleTimer.Enabled = false;
            sampleTimer.Limit = Math.Max(1ul, due > now ? (due - now).Ticks : 1);
            sampleTimer.ResetValue();
            sampleTimer.Enabled = true;
        }

        private void ProcessSamples()
        {
            lock(SyncRoot)
            {
                var now = machine.ClockSource.CurrentValue;
                while(samples.Count != 0 && samples.Peek().Due <= now)
                {
                    var sample = samples.Dequeue();
                    if(frames.TryGetValue(sample.Frame, out var target)) Store(target, sample.Bit, rxLevel);
                }
                ArmSampleTimer(now);
                UpdateSignals();
            }
            PublishPending();
        }

        // A received frame enters the receive FIFO once all its bits are in
        // and its clock has ended: with SPH=0 the last bit is sampled half an
        // SCK period before the frame's trailing edge, and a CPU that waits
        // for the word (spi_write_read_blocking) and then raises its chip
        // select must not do so with SCK still high.
        private void Store(Frame target, int bit, bool level)
        {
            if(target.Answered) return;
            if(level) target.Received |= (ushort)(1 << (target.Bits - 1 - bit));
            if(++target.Sampled < target.Bits || !target.Ended) return;
            Receive(target);
        }

        private void EndFrame(Frame target)
        {
            target.Ended = true;
            if(target.Sampled >= target.Bits) Receive(target);
        }

        private void Receive(Frame target)
        {
            frames.Remove(target.Id);
            if(rx.Count >= Capacity) rawInterrupts |= OverrunInterrupt;
            else rx.Enqueue(target.Received);
            RestartTimeout();
        }

        // The receive timeout fires after 32 bit periods without a new frame
        // or a read while the receive FIFO holds data.
        private void RestartTimeout()
        {
            timeoutTimer.Enabled = false;
            if(rx.Count == 0 || HalfPeriod == 0 || Frequency == 0) return;
            timeoutTimer.Limit = HalfPeriod * 64ul;
            timeoutTimer.ResetValue();
            timeoutTimer.Enabled = true;
        }

        private void OnReceiveTimeout()
        {
            lock(SyncRoot)
            {
                if(rx.Count != 0) rawInterrupts |= TimeoutInterrupt;
                UpdateSignals();
            }
            PublishPending();
        }

        private void OnPinInput(int pin, bool level)
        {
            var rxPin = Array.IndexOf(rxPins, pin) >= 0;
            var selectPin = Array.IndexOf(outputPins[FssLine], pin) >= 0;
            var clockPin = Array.IndexOf(outputPins[ClockLine], pin) >= 0;
            if(!rxPin && !selectPin && !clockPin) return;
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    if(gpio.GetFunction(pin) != SpiFunction) return;
                    if(rxPin) rxLevel = level;
                    else if(SlaveMode && selectPin) OnSlaveSelect(!level);
                    else if(SlaveMode && clockPin) OnSlaveClock(level);
                    UpdateSignals();
                }
            });
            PublishPending();
        }

        // Slave ----------------------------------------------------------

        // As a slave: one whole frame clocked in at once by a controller that
        // is not on the DUT's pins, CSn falling before it and rising after.
        // The block receives the word as if sampled from its receive pin and
        // the frame it shifts out is returned: the next transmit FIFO entry,
        // or zeros when that is empty. Null when the block is not an enabled
        // slave, or SOD keeps its output off.
        public ushort? ClockSlaveFrame(ushort word)
        {
            ushort? shiftedOut = null;
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    if(!Enabled || !SlaveMode) return;
                    LoadSlaveFrame();
                    var output = (ushort)(slaveOut & FrameMask);
                    slaveLoaded = false;
                    if(rx.Count >= Capacity) rawInterrupts |= OverrunInterrupt;
                    else rx.Enqueue((ushort)(word & FrameMask));
                    RestartTimeout();
                    UpdateSignals();
                    if((control1 & 8) == 0) shiftedOut = output;
                }
            });
            PublishPending();
            return shiftedOut;
        }

        private void OnSlaveSelect(bool selected)
        {
            if(slaveSelected == selected) return;
            slaveSelected = selected;
            slaveBits = 0;
            slaveIn = 0;
            if(selected)
            {
                LoadSlaveFrame();
                // With SPH=0 the first bit is valid as soon as CSn falls.
                if(!SecondEdgeSamples) DriveSlaveBit(0);
            }
            // A frame loaded but not started stays loaded across CSn rising.
            UpdatePins();
        }

        private void OnSlaveClock(bool level)
        {
            if(!slaveSelected) return;
            var leading = level != IdleHigh;
            if(leading == !SecondEdgeSamples)
            {
                slaveIn = (ushort)((slaveIn << 1) | (rxLevel ? 1 : 0));
                if(++slaveBits < FrameBits) return;
                if(rx.Count >= Capacity) rawInterrupts |= OverrunInterrupt;
                else rx.Enqueue((ushort)(slaveIn & FrameMask));
                RestartTimeout();
                slaveBits = 0;
                slaveIn = 0;
                slaveLoaded = false;
            }
            else
            {
                // SPH=0 shifts the next bit out on the trailing edge, SPH=1 the
                // bit about to be sampled on the leading edge. A back-to-back
                // frame starts on this edge with its first bit. With SPH=0
                // and nothing to send, zeros go out but no frame is taken: a
                // frame written before CSn falls again goes out in that window.
                if(slaveBits == 0)
                {
                    if(!SecondEdgeSamples && tx.Count == 0 && !slaveLoaded)
                    {
                        slaveOut = 0;
                        DriveSlaveBit(0);
                        return;
                    }
                    LoadSlaveFrame();
                }
                DriveSlaveBit(slaveBits);
            }
        }

        // Takes the next transmit frame when a frame starts, once per frame.
        private void LoadSlaveFrame()
        {
            if(slaveLoaded) return;
            slaveLoaded = true;
            slaveOut = tx.Count != 0 ? tx.Dequeue() : (ushort)0;
        }

        private void DriveSlaveBit(int bit) => SetLine(DataLine, ((slaveOut >> (FrameBits - 1 - bit)) & 1) != 0);

        private void OnClockChanged()
        {
            machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClock(); });
            PublishPending();
        }

        private void UpdateClock()
        {
            var frequency = Frequency;
            if(frequency != 0)
            {
                edgeTimer.Frequency = frequency;
                timeoutTimer.Frequency = frequency;
            }
            if(ResetHeld || frequency == 0) edgeTimer.Enabled = false;
            TryStartFrame();
        }

        // A master drives its outputs while enabled; loopback keeps them idle.
        // A selected slave drives TX only, unless SOD disables its output.
        private void UpdatePins()
        {
            var master = Enabled && !SlaveMode && !Loopback;
            var slave = Enabled && SlaveMode && !Loopback && slaveSelected && (control1 & 8) == 0;
            for(var line = 0; line < 3; line++)
            {
                var drive = master || (slave && line == DataLine);
                if(lineEnabled[line] == drive) continue;
                lineEnabled[line] = drive;
                notifications.Enqueue(new Notification(line, lineLevels[line], drive, generation, machine.ClockSource.CurrentValue));
            }
            if(!shifting)
            {
                SetLine(ClockLine, IdleHigh);
                SetLine(FssLine, true);
            }
        }

        private void SetLine(int line, bool level)
        {
            if(lineLevels[line] == level) return;
            lineLevels[line] = level;
            notifications.Enqueue(new Notification(line, level, lineEnabled[line], generation, lineTime ?? machine.ClockSource.CurrentValue));
        }

        private void UpdateSignals()
        {
            QueueSignal(0, !ResetHeld && (RawInterrupts & interruptMask) != 0);
            QueueSignal(1, !ResetHeld && (dmaControl & 1) != 0 && rx.Count != 0);
            QueueSignal(2, !ResetHeld && (dmaControl & 2) != 0 && tx.Count < Capacity);
        }

        private void QueueSignal(int signal, bool state)
        {
            if(signalStates[signal] == state) return;
            signalStates[signal] = state;
            notifications.Enqueue(new Notification(SignalBase + signal, state, true, generation));
        }

        // Publish pins and signals after releasing the state lock.
        private void PublishPending()
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
                    Notification notification;
                    Tuple<Frame, TimeInterval> target = null;
                    lock(SyncRoot)
                    {
                        if(notifications.Count == 0 && targets.Count == 0)
                        {
                            publishing = false;
                            return;
                        }
                        if(notifications.Count == 0) target = targets.Dequeue();
                        notification = target == null ? notifications.Dequeue() : default(Notification);
                        if(target == null && notification.Generation != generation) continue;
                    }
                    if(target != null)
                    {
                        var answer = FrameTarget?.Invoke(target.Item1.Value, target.Item1.Bits, target.Item2);
                        if(!answer.HasValue) continue;
                        lock(SyncRoot)
                        {
                            target.Item1.Answered = true;
                            target.Item1.Received = (ushort)(answer.Value & FrameMask);
                            target.Item1.Sampled = target.Item1.Bits;
                        }
                        continue;
                    }
                    switch(notification.Target)
                    {
                    case SignalBase: IRQ.Set(notification.Level); break;
                    case SignalBase + 1: RxDMA.Set(notification.Level); break;
                    case SignalBase + 2: TxDMA.Set(notification.Level); break;
                    default:
                        if(gpio == null) break;
                        // Delivered after the locks are released: give the edge its own time.
                        using(SignalTime.At(machine, notification.Time))
                        {
                            foreach(var pin in outputPins[notification.Target])
                            {
                                gpio.SetPeripheralOutput(SpiFunction, pin, notification.Level, notification.Enabled);
                            }
                        }
                        break;
                    }
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private uint RawInterrupts => rawInterrupts | (rx.Count >= Capacity / 2 ? ReceiveInterrupt : 0)
            | (tx.Count <= Capacity / 2 ? TransmitInterrupt : 0);
        private bool Busy => shifting || tx.Count != 0 || samples.Count != 0 || (slaveSelected && slaveBits != 0);
        private bool SlaveMode => (control1 & 4) != 0;
        private bool Enabled => !ResetHeld && (control1 & 2) != 0;
        private bool Loopback => (control1 & 1) != 0;
        private bool IdleHigh => (control0 & 0x40) != 0;
        private bool SecondEdgeSamples => (control0 & 0x80) != 0;
        private int FrameBits => Math.Max(4, (int)(control0 & 0xf) + 1);
        private uint FrameMask => (1u << FrameBits) - 1;
        // SSPCLKOUT = clk_peri / (CPSDVSR * (1 + SCR)); CPSDVSR is even.
        private ulong HalfPeriod => prescale < 2 ? 0 : prescale * (1 + ((control0 >> 8) & 0xff)) / 2;
        private ulong Frequency => clocks == null ? fallbackFrequency : clocks.PeripheralFrequency;

        private sealed class Frame
        {
            public Frame(ulong id, ushort value, int bits)
            {
                Id = id;
                Value = value;
                Bits = bits;
            }

            public bool Bit(int index) => (Value >> (Bits - 1 - index) & 1) != 0;

            public readonly ulong Id;
            public readonly ushort Value;
            public readonly int Bits;
            public ushort Received;
            public int Sampled;
            public bool Ended;
            public bool Answered;
        }

        private readonly struct Sample
        {
            public Sample(TimeInterval due, ulong frame, int bit)
            {
                Due = due;
                Frame = frame;
                Bit = bit;
            }

            public readonly TimeInterval Due;
            public readonly ulong Frame;
            public readonly int Bit;
        }

        private readonly struct Notification
        {
            public Notification(int target, bool level, bool enabled, uint generation, TimeInterval time = default(TimeInterval))
            {
                Target = target;
                Level = level;
                Enabled = enabled;
                Generation = generation;
                Time = time;
            }

            public readonly int Target;
            public readonly bool Level;
            public readonly bool Enabled;
            public readonly uint Generation;
            // When a line change happened.
            public readonly TimeInterval Time;
        }

        private readonly IRP2ClockSource clocks;
        private readonly ulong fallbackFrequency;
        private readonly RP2_GPIOBank0 gpio;
        private readonly int[] rxPins;
        private readonly int[][] outputPins;
        private readonly Queue<ushort> tx;
        private readonly Queue<ushort> rx;
        private readonly Queue<Sample> samples;
        private readonly Dictionary<ulong, Frame> frames;
        private readonly Queue<Notification> notifications;
        private readonly bool[] lineLevels;
        private readonly bool[] lineEnabled;
        private readonly bool[] signalStates;
        private readonly LimitTimer edgeTimer;
        private readonly LimitTimer sampleTimer;
        private readonly LimitTimer timeoutTimer;
        private Frame frame;
        private ulong frameId;
        private int edge;
        private bool shifting, publishing, rxLevel, slaveSelected, slaveLoaded;
        private int pulseHalves;
        private int slaveBits;
        private ushort slaveIn, slaveOut;
        private uint generation;
        private uint control0, control1, prescale, interruptMask, rawInterrupts, dmaControl;

        private const int Capacity = 8;
        private const int MaximumPins = 48;
        private const int SpiFunction = 1;
        private const int ClockLine = 0;
        private readonly Queue<Tuple<Frame, TimeInterval>> targets = new Queue<Tuple<Frame, TimeInterval>>();
        private const int DataLine = 1;
        private const int FssLine = 2;
        private const int SignalBase = 3;
        private const uint OverrunInterrupt = 1;
        private const uint TimeoutInterrupt = 2;
        private const uint ReceiveInterrupt = 4;
        private const uint TransmitInterrupt = 8;
        private static readonly uint[] Identification = { 0x22, 0x10, 0x34, 0, 0x0d, 0xf0, 0x05, 0xb1 };
    }
}
