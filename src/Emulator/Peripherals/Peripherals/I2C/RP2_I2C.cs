//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.I2C
{
    // RP2040/RP2350 I2C is a Synopsys DW_apb_i2c with 16-entry FIFOs,
    // clocked by clk_sys. This model carries the bus at byte level over a
    // DelayedI2CBus, in controller (master) or target (slave) mode, with
    // 7-bit addresses. As a controller it runs the TX FIFO command queue
    // (data, CMD, STOP, RESTART) at the configured SCL rate and waits for each
    // target answer; as a target it acknowledges its address, fills the RX
    // FIFO and raises RD_REQ until firmware supplies the byte to send.
    // 10-bit addressing, multi-controller arbitration, high speed mode and
    // SDA/SCL pin levels are not modeled.
    public sealed class RP2_I2C : RP2APBPeripheral, IDelayedI2CBusEndpoint
    {
        public RP2_I2C(IMachine machine, IRP2ClockSource clocks = null, ulong frequency = 125000000,
            RP2_GPIOBank0 gpio = null, int index = -1) : base(machine)
        {
            this.clocks = clocks;
            fallbackFrequency = frequency;
            this.gpio = index >= 0 ? gpio : null;
            this.index = index;
            tx = new Queue<ushort>();
            rx = new Queue<ushort>();
            pending = new Queue<Action>();
            signalStates = new bool[3];
            phaseTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency), this, "I2C bus phase",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            phaseTimer.LimitReached += OnPhaseElapsed;
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            Reset();
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    Quiesce();
                    generation++;
                    control = ControlReset;
                    targetAddress = 0x55;
                    ownAddress = 0x55;
                    standardHigh = 0x28;
                    standardLow = 0x2f;
                    fastHigh = 0x06;
                    fastLow = 0x0d;
                    interruptMask = 0x8ff;
                    rxThreshold = txThreshold = 0;
                    enable = 0;
                    sdaHold = 1;
                    nackOnly = 0;
                    dmaControl = 0;
                    dmaTransmitLevel = dmaReceiveLevel = 0;
                    sdaSetup = 0x64;
                    acknowledgeGeneralCall = 1;
                    spikeLength = 7;
                    latched = 0;
                    abortSource = 0;
                    abortFlushing = false;
                    UpdateClock();
                    UpdateSignals();
                }
            });
            PublishPending();
        }

        public void AttachToBus(DelayedI2CBus bus, int sdaPin, int sclPin)
        {
            if(this.bus != null) throw new RecoverableException("This I2C controller is already connected to a bus");
            if(gpio != null && (sdaPin >= 0 || sclPin >= 0))
            {
                // GPIO function 3 maps even pins to SDA and odd pins to SCL;
                // the block is (pin / 2) % 2.
                if(sdaPin < 0 || sclPin < 0 || sdaPin % 2 != 0 || sclPin % 2 != 1
                    || sdaPin / 2 % 2 != index || sclPin / 2 % 2 != index)
                {
                    throw new RecoverableException($"GPIO {sdaPin} and {sclPin} are not SDA and SCL of I2C{index}");
                }
            }
            this.bus = bus;
            this.sdaPin = sdaPin;
            this.sclPin = sclPin;
        }

        public void OnBusEvent(I2CBusEvent busEvent)
        {
            lock(SyncRoot)
            {
                switch(busEvent.Type)
                {
                case I2CBusEventType.Acknowledge:
                case I2CBusEventType.Data:
                    OnAnswer(busEvent);
                    break;
                default:
                    OnTargetEvent(busEvent);
                    break;
                }
                UpdateSignals();
            }
            PublishPending();
        }

        public GPIO IRQ { get; } = new GPIO();
        public GPIO RxDMA { get; } = new GPIO();
        public GPIO TxDMA { get; } = new GPIO();

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        protected override void OnAccessCompleted() => PublishPending();

        protected override void OnResetHeldChanged()
        {
            Quiesce();
            UpdateClock();
            UpdateSignals();
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0x00: return control;
            case 0x04: return targetAddress;
            case 0x08: return ownAddress;
            case 0x10: return ReadData();
            case 0x14: return standardHigh;
            case 0x18: return standardLow;
            case 0x1c: return fastHigh;
            case 0x20: return fastLow;
            case 0x2c: return RawInterrupts & interruptMask;
            case 0x30: return interruptMask;
            case 0x34: return RawInterrupts;
            case 0x38: return rxThreshold;
            case 0x3c: return txThreshold;
            case 0x40:
                if((latched & TxAbortInterrupt) != 0) ClearAbort();
                // ACTIVITY stays set while the bus is in use.
                latched &= ~ClearableInterrupts | (Active ? ActivityInterrupt : 0);
                return 0;
            case 0x44: return Clear(RxUnderInterrupt);
            case 0x48: return Clear(RxOverInterrupt);
            case 0x4c: return Clear(TxOverInterrupt);
            case 0x50: return Clear(ReadRequestInterrupt);
            case 0x54:
                if((latched & TxAbortInterrupt) != 0) ClearAbort();
                return Clear(TxAbortInterrupt);
            case 0x58: return Clear(RxDoneInterrupt);
            case 0x5c: return Active ? 0 : Clear(ActivityInterrupt);
            case 0x60: return Clear(StopInterrupt);
            case 0x64: return Clear(StartInterrupt);
            case 0x68: return Clear(GeneralCallInterrupt);
            case 0x6c: return enable;
            case 0x70:
                return (Active ? 1u : 0) | (tx.Count < Depth ? 2u : 0) | (tx.Count == 0 ? 4u : 0)
                    | (rx.Count != 0 ? 8u : 0) | (rx.Count == Depth ? 0x10u : 0)
                    | (ControllerBusy ? 0x20u : 0) | (targetActive ? 0x40u : 0);
            case 0x74: return (uint)tx.Count;
            case 0x78: return (uint)rx.Count;
            case 0x7c: return sdaHold;
            case 0x80: return abortSource;
            case 0x84: return nackOnly;
            case 0x88: return dmaControl;
            case 0x8c: return dmaTransmitLevel;
            case 0x90: return dmaReceiveLevel;
            case 0x94: return sdaSetup;
            case 0x98: return acknowledgeGeneralCall;
            case 0x9c: return Enabled ? 1u : 0;
            case 0xa0: return spikeLength;
            case 0xa8: return Clear(RestartInterrupt);
            case 0xf4: return 0;
            case 0xf8: return 0x3230312a;
            case 0xfc: return 0x44570140;
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
                if(WhileDisabled(offset))
                {
                    // Fast mode is the highest speed; a higher setting reads back as fast.
                    value &= 0x7ff;
                    if(((value >> 1) & 3) == 3) value = (value & ~6u) | 4;
                    if(((value >> 1) & 3) == 0) value = (value & ~6u) | (control & 6);
                    if((value & 0x18) != 0) this.Log(LogLevel.Warning, "10-bit addressing is not modeled");
                    control = value;
                }
                break;
            case 0x04: if(WhileDisabled(offset)) targetAddress = value & 0xfff; break;
            case 0x08: if(WhileDisabled(offset)) ownAddress = value & 0x3ff; break;
            case 0x10: WriteData(value); break;
            case 0x14: if(WhileDisabled(offset)) standardHigh = Math.Max(6u, value & 0xffff); break;
            case 0x18: if(WhileDisabled(offset)) standardLow = Math.Max(8u, value & 0xffff); break;
            case 0x1c: if(WhileDisabled(offset)) fastHigh = Math.Max(6u, value & 0xffff); break;
            case 0x20: if(WhileDisabled(offset)) fastLow = Math.Max(8u, value & 0xffff); break;
            case 0x30: interruptMask = value & AllInterrupts; break;
            case 0x38: rxThreshold = Math.Min(value & 0xff, Depth - 1); break;
            case 0x3c: txThreshold = Math.Min(value & 0xff, Depth - 1); break;
            case 0x6c: WriteEnable(value & 7); break;
            case 0x7c: sdaHold = value & 0xffffff; break;
            case 0x84: nackOnly = value & 1; break;
            case 0x88: dmaControl = value & 3; break;
            case 0x8c: dmaTransmitLevel = value & 0xf; break;
            case 0x90: dmaReceiveLevel = value & 0xf; break;
            case 0x94: if(WhileDisabled(offset)) sdaSetup = value & 0xff; break;
            case 0x98: acknowledgeGeneralCall = value & 1; break;
            case 0xa0: if(WhileDisabled(offset)) spikeLength = Math.Max(1u, value & 0xff); break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
            UpdateSignals();
        }

        private bool WhileDisabled(long offset)
        {
            if(!Enabled) return true;
            this.Log(LogLevel.Warning, "Write to 0x{0:X} ignored while the controller is enabled", offset);
            return false;
        }

        private uint Clear(uint interrupt)
        {
            latched &= ~interrupt;
            return 0;
        }

        private void WriteEnable(uint value)
        {
            var wasEnabled = Enabled;
            var abort = (value & 2) != 0;
            enable = value & 5;
            if(wasEnabled && !Enabled)
            {
                Quiesce();
                return;
            }
            if(abort && Enabled && ControllerMode && ControllerBusy)
            {
                Abort(UserAbort);
                return;
            }
            TryStartCommand();
        }

        // Disabling or reset releases the bus: an owned transfer ends with
        // STOP, an outstanding target answer is given as released lines, and
        // both FIFOs are flushed.
        private void Quiesce()
        {
            phaseTimer.Enabled = false;
            if(transferActive) SendToBus(I2CBusEvent.Stop(transfer));
            transfer++;
            transferActive = false;
            phase = Phase.Idle;
            awaiting = 0;
            commandActive = false;
            if(readRequest.HasValue) Answer(readRequest.Value, I2CBusEvent.Reply(readRequest.Value.Transfer, 0xff));
            if(heldWrite.HasValue) Answer(heldWrite.Value, I2CBusEvent.Acknowledge(heldWrite.Value.Transfer, false));
            readRequest = null;
            heldWrite = null;
            targetSelected = false;
            targetActive = false;
            tx.Clear();
            rx.Clear();
        }

        private uint ReadData()
        {
            if(rx.Count == 0)
            {
                latched |= RxUnderInterrupt;
                return 0;
            }
            var value = (uint)rx.Dequeue();
            if(heldWrite.HasValue)
            {
                var held = heldWrite.Value;
                heldWrite = null;
                StoreReceived(held.Data);
                Answer(held, I2CBusEvent.Acknowledge(held.Transfer, nackOnly == 0));
            }
            return value;
        }

        private void WriteData(uint value)
        {
            if(!Enabled || abortFlushing) return;
            if(tx.Count >= Depth)
            {
                latched |= TxOverInterrupt;
                return;
            }
            tx.Enqueue((ushort)(value & 0x7ff));
            if(ControllerMode) TryStartCommand();
            else ServeReadRequest();
        }

        // Controller -----------------------------------------------------

        private void TryStartCommand()
        {
            if(!Enabled || !ControllerMode || ResetHeld || phase != Phase.Idle || (enable & 4) != 0 || tx.Count == 0) return;
            var command = tx.Peek();
            var read = (command & ReadCommand) != 0;
            if(!transferActive)
            {
                BeginAddress(read, false);
            }
            else if((command & RestartCommand) != 0 || read != transferRead)
            {
                if((control & RestartEnable) != 0) BeginAddress(read, true);
                // Without RESTART_EN the controller ends the transfer and starts anew.
                else StartPhase(Phase.Stop, BitCycles);
            }
            else
            {
                BeginData();
            }
        }

        private void BeginAddress(bool read, bool repeated)
        {
            transferRead = read;
            repeatedStart = repeated;
            if(!repeated) latched |= ActivityInterrupt;
            latched |= StartInterrupt;
            // START (or repeated START) and the eight address bits.
            StartPhase(Phase.Address, BitCycles / 2 + 8 * BitCycles);
        }

        private void BeginData()
        {
            command = tx.Dequeue();
            commandActive = true;
            if((command & ReadCommand) != 0)
            {
                AwaitAnswers(Phase.AwaitRead, I2CBusEvent.Read(transfer), selected);
                answerData = 0xff;
                if(awaiting == 0) StartPhase(Phase.ReadBits, 9 * BitCycles);
            }
            else
            {
                StartPhase(Phase.WriteBits, 8 * BitCycles);
            }
        }

        private void OnPhaseElapsed()
        {
            lock(SyncRoot)
            {
                var elapsed = phase;
                phase = Phase.Idle;
                switch(elapsed)
                {
                case Phase.Address:
                    transfer++;
                    transferActive = true;
                    answerAcknowledged = 0;
                    AwaitAnswers(Phase.AwaitAddress, I2CBusEvent.Start(transfer, (int)(targetAddress & 0x7f), transferRead, repeatedStart),
                        Routed ? bus.PeerCount(this) : 0);
                    if(awaiting == 0) StartPhase(Phase.AddressAcknowledge, BitCycles);
                    break;
                case Phase.AddressAcknowledge:
                    selected = answerAcknowledged;
                    firstByte = true;
                    if(selected == 0) Abort(AddressNack);
                    else BeginData();
                    break;
                case Phase.WriteBits:
                    answerAcknowledged = 0;
                    AwaitAnswers(Phase.AwaitWrite, I2CBusEvent.Write(transfer, (byte)command), selected);
                    if(awaiting == 0) StartPhase(Phase.WriteAcknowledge, BitCycles);
                    break;
                case Phase.WriteAcknowledge:
                    if(answerAcknowledged == 0) Abort(DataNack);
                    else CompleteCommand();
                    break;
                case Phase.ReadBits:
                    StoreReceived(answerData);
                    CompleteCommand();
                    break;
                case Phase.Stop:
                    SendToBus(I2CBusEvent.Stop(transfer));
                    transferActive = false;
                    latched |= StopInterrupt;
                    TryStartCommand();
                    break;
                }
                UpdateSignals();
            }
            PublishPending();
        }

        private void OnAnswer(I2CBusEvent answer)
        {
            if(answer.Transfer != transfer || awaiting == 0) return;
            if(answer.Type == I2CBusEventType.Acknowledge && (phase == Phase.AwaitAddress || phase == Phase.AwaitWrite))
            {
                if(answer.Acknowledged) answerAcknowledged++;
            }
            else if(answer.Type == I2CBusEventType.Data && phase == Phase.AwaitRead)
            {
                // Targets driving one bus resolve to the wired AND.
                answerData &= answer.Data;
            }
            else return;
            if(--awaiting != 0) return;
            switch(phase)
            {
            case Phase.AwaitAddress: StartPhase(Phase.AddressAcknowledge, BitCycles); break;
            case Phase.AwaitWrite: StartPhase(Phase.WriteAcknowledge, BitCycles); break;
            case Phase.AwaitRead: StartPhase(Phase.ReadBits, 9 * BitCycles); break;
            }
        }

        private void AwaitAnswers(Phase next, I2CBusEvent request, int answers)
        {
            phase = next;
            awaiting = answers;
            if(answers != 0) SendToBus(request);
        }

        private void CompleteCommand()
        {
            commandActive = false;
            firstByte = false;
            if((command & StopCommand) != 0) StartPhase(Phase.Stop, BitCycles);
            else TryStartCommand();
        }

        private void Abort(uint source)
        {
            commandActive = false;
            awaiting = 0;
            abortSource = source | ((uint)tx.Count << 23);
            tx.Clear();
            abortFlushing = true;
            latched |= TxAbortInterrupt;
            StartPhase(Phase.Stop, BitCycles);
        }

        private void ClearAbort()
        {
            abortFlushing = false;
            abortSource = 0;
        }

        private void StartPhase(Phase next, ulong cycles)
        {
            phase = next;
            phaseTimer.Enabled = false;
            phaseTimer.Limit = Math.Max(1ul, cycles);
            phaseTimer.ResetValue();
            phaseTimer.Enabled = true;
        }

        private void StoreReceived(byte data)
        {
            var value = (ushort)(data | (firstByte ? FirstDataByte : 0));
            firstByte = false;
            if(rx.Count >= Depth)
            {
                latched |= RxOverInterrupt;
                return;
            }
            rx.Enqueue(value);
        }

        // Target ---------------------------------------------------------

        private void OnTargetEvent(I2CBusEvent busEvent)
        {
            var listening = TargetMode && Routed;
            switch(busEvent.Type)
            {
            case I2CBusEventType.Start:
                if(busEvent.Sender == acknowledgedController) acknowledged = false;
                if(!listening)
                {
                    Answer(busEvent, I2CBusEvent.Acknowledge(busEvent.Transfer, false));
                    return;
                }
                FinishTargetRead();
                latched |= StartInterrupt;
                var wasSelected = targetSelected;
                var address = busEvent.Address;
                var generalCall = address == 0 && acknowledgeGeneralCall != 0 && !busEvent.IsRead;
                targetSelected = generalCall || address == (ownAddress & 0x7f);
                if(targetSelected)
                {
                    if(!targetActive) latched |= ActivityInterrupt;
                    targetActive = true;
                    targetRead = busEvent.IsRead;
                    firstByte = true;
                    if(generalCall) latched |= GeneralCallInterrupt;
                    if(busEvent.Repeated && wasSelected) latched |= RestartInterrupt;
                    acknowledged = true;
                    acknowledgedController = busEvent.Sender;
                    acknowledgedTransfer = busEvent.Transfer;
                }
                Answer(busEvent, I2CBusEvent.Acknowledge(busEvent.Transfer, targetSelected));
                break;
            case I2CBusEventType.Write:
                if(!AnswersTo(busEvent)) return;
                if(!listening || !targetSelected)
                {
                    Answer(busEvent, I2CBusEvent.Acknowledge(busEvent.Transfer, false));
                    return;
                }
                if(rx.Count >= Depth)
                {
                    if((control & RxFullHold) != 0)
                    {
                        heldWrite = new Pending(busEvent.Sender, busEvent.Transfer, busEvent.Data);
                        return;
                    }
                    latched |= RxOverInterrupt;
                    Answer(busEvent, I2CBusEvent.Acknowledge(busEvent.Transfer, nackOnly == 0));
                    return;
                }
                StoreReceived(busEvent.Data);
                Answer(busEvent, I2CBusEvent.Acknowledge(busEvent.Transfer, nackOnly == 0));
                break;
            case I2CBusEventType.Read:
                if(!AnswersTo(busEvent)) return;
                if(!listening || !targetSelected)
                {
                    Answer(busEvent, I2CBusEvent.Reply(busEvent.Transfer, 0xff));
                    return;
                }
                readRequest = new Pending(busEvent.Sender, busEvent.Transfer, 0);
                if(tx.Count == 0) latched |= ReadRequestInterrupt;
                ServeReadRequest();
                break;
            case I2CBusEventType.Stop:
                if(busEvent.Sender == acknowledgedController) acknowledged = false;
                if(!listening) return;
                if(targetSelected || (control & StopIfAddressed) == 0) latched |= StopInterrupt;
                FinishTargetRead();
                if(readRequest.HasValue) Answer(readRequest.Value, I2CBusEvent.Reply(readRequest.Value.Transfer, 0xff));
                if(heldWrite.HasValue) Answer(heldWrite.Value, I2CBusEvent.Acknowledge(heldWrite.Value.Transfer, false));
                readRequest = null;
                heldWrite = null;
                targetSelected = false;
                targetActive = false;
                break;
            }
        }

        private bool AnswersTo(I2CBusEvent busEvent) =>
            acknowledged && busEvent.Sender == acknowledgedController && busEvent.Transfer == acknowledgedTransfer;

        // The controller ended a read from this target, NACKing the last byte.
        private void FinishTargetRead()
        {
            if(targetSelected && targetRead) latched |= RxDoneInterrupt;
        }

        private void ServeReadRequest()
        {
            if(!readRequest.HasValue || tx.Count == 0) return;
            var request = readRequest.Value;
            readRequest = null;
            Answer(request, I2CBusEvent.Reply(request.Transfer, (byte)tx.Dequeue()));
        }

        private void Answer(I2CBusEvent request, I2CBusEvent answer)
        {
            var to = request.Sender;
            if(to == null) return;
            QueueBusEvent(answer, to);
        }

        private void Answer(Pending request, I2CBusEvent answer)
        {
            if(request.Sender != null) QueueBusEvent(answer, request.Sender);
        }

        private void SendToBus(I2CBusEvent busEvent) => QueueBusEvent(busEvent, null);

        private void QueueBusEvent(I2CBusEvent busEvent, IDelayedI2CBusEndpoint to)
        {
            var target = bus;
            if(target == null) return;
            pending.Enqueue(() => target.Send(this, busEvent, to));
        }

        // Common ---------------------------------------------------------

        private void OnClockChanged()
        {
            machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClock(); });
        }

        private void UpdateClock()
        {
            var frequency = Frequency;
            if(frequency != 0) phaseTimer.Frequency = frequency;
        }

        private void UpdateSignals()
        {
            QueueSignal(0, !ResetHeld && (RawInterrupts & interruptMask) != 0);
            QueueSignal(1, !ResetHeld && Enabled && (dmaControl & 1) != 0 && rx.Count > dmaReceiveLevel);
            QueueSignal(2, !ResetHeld && Enabled && (dmaControl & 2) != 0 && tx.Count <= dmaTransmitLevel);
        }

        private void QueueSignal(int signal, bool state)
        {
            if(signalStates[signal] == state) return;
            signalStates[signal] = state;
            var line = signal == 0 ? IRQ : signal == 1 ? RxDMA : TxDMA;
            var current = generation;
            pending.Enqueue(() => { if(current == generation) line.Set(state); });
        }

        // Publish signals and bus events after releasing the state lock.
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
                    Action action;
                    lock(SyncRoot)
                    {
                        if(pending.Count == 0)
                        {
                            publishing = false;
                            return;
                        }
                        action = pending.Dequeue();
                    }
                    action();
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private uint RawInterrupts
        {
            get
            {
                var value = latched;
                if(Enabled && rx.Count > rxThreshold) value |= RxFullInterrupt;
                // With TX_EMPTY_CTRL the controller also waits for the last
                // popped command to finish on the bus.
                var commandDone = !ControllerMode || (control & TxEmptyControl) == 0 || !commandActive;
                if(Enabled && tx.Count <= txThreshold && commandDone) value |= TxEmptyInterrupt;
                return value;
            }
        }

        private bool Routed => bus != null && (gpio == null || sdaPin < 0
            || (gpio.GetFunction(sdaPin) == I2CFunction && gpio.GetFunction(sclPin) == I2CFunction));
        private bool Enabled => !ResetHeld && (enable & 1) != 0;
        private bool ControllerMode => (control & MasterMode) != 0;
        private bool TargetMode => Enabled && !ControllerMode && (control & SlaveDisable) == 0;
        private bool ControllerBusy => transferActive || phase != Phase.Idle;
        private bool Active => ControllerBusy || targetActive;
        private ulong Frequency => clocks == null ? fallbackFrequency : clocks.SystemFrequency;
        // SCL high lasts HCNT + SPKLEN + 7 ic_clk cycles and low LCNT + 1.
        private ulong BitCycles => ((control >> 1) & 3) == 1
            ? standardHigh + spikeLength + 7 + standardLow + 1
            : fastHigh + spikeLength + 7 + fastLow + 1;

        private enum Phase
        {
            Idle,
            Address,
            AwaitAddress,
            AddressAcknowledge,
            WriteBits,
            AwaitWrite,
            WriteAcknowledge,
            AwaitRead,
            ReadBits,
            Stop,
        }

        private readonly struct Pending
        {
            public Pending(IDelayedI2CBusEndpoint sender, ulong transfer, byte data)
            {
                Sender = sender;
                Transfer = transfer;
                Data = data;
            }

            public readonly IDelayedI2CBusEndpoint Sender;
            public readonly ulong Transfer;
            public readonly byte Data;
        }

        private readonly IRP2ClockSource clocks;
        private readonly ulong fallbackFrequency;
        private readonly RP2_GPIOBank0 gpio;
        private readonly int index;
        private readonly Queue<ushort> tx;
        private readonly Queue<ushort> rx;
        private readonly Queue<Action> pending;
        private readonly bool[] signalStates;
        private readonly LimitTimer phaseTimer;
        private DelayedI2CBus bus;
        private int sdaPin = -1, sclPin = -1;
        private Phase phase;
        private ulong transfer;
        private int awaiting, selected, answerAcknowledged;
        private byte answerData;
        private ushort command;
        private bool transferActive, transferRead, repeatedStart, commandActive, abortFlushing, firstByte;
        private bool targetSelected, targetActive, targetRead, acknowledged;
        // The controller and transfer this target acknowledged; it answers
        // every byte of that transfer, with NACK or 0xFF once disabled.
        private IDelayedI2CBusEndpoint acknowledgedController;
        private ulong acknowledgedTransfer;
        private Pending? readRequest, heldWrite;
        private bool publishing;
        private uint generation;
        private uint control, targetAddress, ownAddress, standardHigh, standardLow, fastHigh, fastLow;
        private uint interruptMask, rxThreshold, txThreshold, enable, sdaHold, nackOnly, dmaControl;
        private uint dmaTransmitLevel, dmaReceiveLevel, sdaSetup, acknowledgeGeneralCall, spikeLength;
        private uint latched, abortSource;

        private const int Depth = 16;
        private const int I2CFunction = 3;
        private const uint ControlReset = 0x65;
        private const uint MasterMode = 1;
        private const uint RestartEnable = 1u << 5;
        private const uint SlaveDisable = 1u << 6;
        private const uint StopIfAddressed = 1u << 7;
        private const uint TxEmptyControl = 1u << 8;
        private const uint RxFullHold = 1u << 9;
        private const ushort ReadCommand = 1 << 8;
        private const ushort StopCommand = 1 << 9;
        private const ushort RestartCommand = 1 << 10;
        private const ushort FirstDataByte = 1 << 11;
        private const uint RxUnderInterrupt = 1u << 0;
        private const uint RxOverInterrupt = 1u << 1;
        private const uint RxFullInterrupt = 1u << 2;
        private const uint TxOverInterrupt = 1u << 3;
        private const uint TxEmptyInterrupt = 1u << 4;
        private const uint ReadRequestInterrupt = 1u << 5;
        private const uint TxAbortInterrupt = 1u << 6;
        private const uint RxDoneInterrupt = 1u << 7;
        private const uint ActivityInterrupt = 1u << 8;
        private const uint StopInterrupt = 1u << 9;
        private const uint StartInterrupt = 1u << 10;
        private const uint GeneralCallInterrupt = 1u << 11;
        private const uint RestartInterrupt = 1u << 12;
        private const uint AllInterrupts = 0x1fff;
        private const uint ClearableInterrupts = AllInterrupts & ~(RxFullInterrupt | TxEmptyInterrupt);
        private const uint AddressNack = 1u << 0;
        private const uint DataNack = 1u << 3;
        private const uint UserAbort = 1u << 16;
    }
}
