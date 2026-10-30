//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;

using Antmicro.Migrant;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.UART
{
    // RP2040/RP2350 use a PL011 UART with 32-entry FIFOs and an atomic APB
    // interposer. The IUART connection represents complete serial characters;
    // electrical pin sampling and the IrDA encoder are not modeled.
    public sealed class RP2_UART : RP2APBPeripheral, IUART, IGPIOReceiver
    {
        public RP2_UART(IMachine machine, IRP2ClockSource clocks = null, ulong frequency = 125000000) : base(machine)
        {
            this.clocks = clocks;
            fallbackFrequency = frequency;
            tx = new Queue<byte>();
            rx = new Queue<uint>();
            notifications = new Queue<Notification>();
            signalStates = new bool[4];
            modemInputs = new[] { true, true, true, true };
            txTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency * 4), this, "transmit frame",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            timeoutTimer = new LimitTimer(machine.ClockSource, Math.Max(1ul, frequency * 4), this, "receive timeout",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            txTimer.LimitReached += CompleteTransmit;
            timeoutTimer.LimitReached += ReceiveTimeout;
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            Reset();
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    txTimer.Reset();
                    timeoutTimer.Reset();
                    tx.Clear();
                    rx.Clear();
                    notifications.Clear();
                    generation++;
                    shifting = timeoutPending = pendingOverrun = false;
                    integerDivisor = fractionalDivisor = lineControl = activeDivisor = 0;
                    control = 0x300;
                    fifoLevels = 0x12;
                    interruptMask = interruptStatus = dmaControl = receiveStatus = irdaDivisor = 0;
                    UpdateClock();
                    UpdateSignals();
                }
            });
            PublishPending();
        }

        public void WriteChar(byte value)
        {
            SynchronizeTime();
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    if(ReceiveEnabled && !Loopback) ReceiveCharacter(value);
                }
            });
            PublishPending();
        }

        // Modem inputs are active-low: 0=CTS, 1=DSR, 2=DCD, 3=RI.
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= modemInputs.Length)
            {
                this.Log(LogLevel.Warning, "Invalid modem input {0}", number);
                return;
            }
            SynchronizeTime();
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    if(modemInputs[number] != value)
                    {
                        modemInputs[number] = value;
                        if(!ResetHeld && !Loopback) interruptStatus |= ModemInterrupt(number);
                        TryStartTransmit();
                        UpdateSignals();
                    }
                }
            });
            PublishPending();
        }

        public uint BaudRate => activeDivisor == 0 ? 0 : (uint)(Frequency * 4 / activeDivisor);

        public Bits StopBits => (lineControl & 8) != 0 ? Bits.Two : Bits.One;

        public Parity ParityBit => (lineControl & 2) == 0 ? Parity.None
            : (lineControl & 0x80) != 0 ? ((lineControl & 4) != 0 ? Parity.Forced0 : Parity.Forced1)
            : (lineControl & 4) != 0 ? Parity.Even : Parity.Odd;

        public GPIO IRQ { get; } = new GPIO();
        public GPIO RTS { get; } = new GPIO();
        public GPIO RxDMA { get; } = new GPIO();
        public GPIO TxDMA { get; } = new GPIO();

        [field: Transient]
        public event Action<byte> CharReceived;

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess() => SynchronizeTime();

        protected override void OnAccessCompleted() => PublishPending();

        protected override void OnResetHeldChanged()
        {
            UpdateClock();
            UpdateSignals();
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0xfe0 && offset <= 0xffc && (offset & 3) == 0)
            {
                return Identification[(offset - 0xfe0) / 4];
            }
            switch(offset)
            {
            case 0x00:
                if(rx.Count == 0) return 0;
                var value = rx.Dequeue();
                // RSR errors are sticky until ECR; DR carries a separate
                // status marker alongside each received character.
                if(rx.Count < ReceiveThreshold) interruptStatus &= ~ReceiveInterrupt;
                if(rx.Count == 0)
                {
                    timeoutTimer.Enabled = false;
                    timeoutPending = false;
                    interruptStatus &= ~TimeoutInterrupt;
                }
                UpdateSignals();
                return value;
            case 0x04: return receiveStatus;
            case 0x18:
                return (tx.Count == 0 ? 0x80u : 0) | (rx.Count >= Capacity ? 0x40u : 0)
                    | (tx.Count >= Capacity ? 0x20u : 0) | (rx.Count == 0 ? 0x10u : 0)
                    | (shifting || tx.Count != 0 ? 8u : 0) | ModemFlags;
            case 0x20: return irdaDivisor;
            case 0x24: return integerDivisor;
            case 0x28: return fractionalDivisor;
            case 0x2c: return lineControl;
            case 0x30: return control;
            case 0x34: return fifoLevels;
            case 0x38: return interruptMask;
            case 0x3c: return interruptStatus;
            case 0x40: return interruptStatus & interruptMask;
            case 0x44: return 0;
            case 0x48: return dmaControl;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            // These UARTs implement atomic aliases with a downstream read and
            // write, including the read side effects of the selected register.
            if(operation != AtomicOperation.Write) value = ApplyOperation(ReadRegister(offset), value, operation);
            switch(offset)
            {
            case 0x00:
                if(tx.Count < Capacity) tx.Enqueue((byte)value);
                else this.Log(LogLevel.Warning, "Transmit FIFO write while full");
                if(tx.Count > TransmitThreshold) interruptStatus &= ~TransmitInterrupt;
                TryStartTransmit();
                break;
            case 0x04:
                receiveStatus = 0;
                break;
            case 0x18:
                break;
            case 0x20:
                irdaDivisor = value & 0xff;
                break;
            case 0x24:
                integerDivisor = value & 0xffff;
                break;
            case 0x28:
                fractionalDivisor = value & 0x3f;
                break;
            case 0x2c:
                if(((lineControl ^ value) & 0x10) != 0 && rx.Count != 0)
                {
                    this.Log(LogLevel.Warning, "Changing FIFO mode with unread receive data is not modeled");
                }
                if((lineControl & 0x10) != 0 && (value & 0x10) == 0)
                {
                    // Disabling FEN flushes pending TX data (PL011 TRM 3.3.8).
                    tx.Clear();
                    interruptStatus &= ~TransmitInterrupt;
                }
                lineControl = value & 0xff;
                // The divisor registers are latched by a UARTLCR_H write.
                activeDivisor = integerDivisor * 64 + fractionalDivisor;
                TryStartTransmit();
                RestartTimeout();
                break;
            case 0x30:
                var oldModem = ModemFlags;
                control = value & 0xff87;
                if((control & 6) != 0) this.Log(LogLevel.Warning, "IrDA mode is not implemented");
                var changedModem = oldModem ^ ModemFlags;
                for(var i = 0; i < 4; i++)
                {
                    if((changedModem & ModemFlag(i)) != 0) interruptStatus |= ModemInterrupt(i);
                }
                TryStartTransmit();
                UpdateClock();
                break;
            case 0x34:
                fifoLevels = value & 0x3f;
                if(rx.Count < ReceiveThreshold) interruptStatus &= ~ReceiveInterrupt;
                else interruptStatus |= ReceiveInterrupt;
                if(tx.Count > TransmitThreshold) interruptStatus &= ~TransmitInterrupt;
                break;
            case 0x38:
                interruptMask = value & 0x7ff;
                break;
            case 0x44:
                interruptStatus &= ~(value & 0x7ff);
                if((value & TimeoutInterrupt) != 0) RestartTimeout();
                break;
            case 0x48:
                dmaControl = value & 7;
                break;
            default:
                if(offset < 0xfe0 || offset > 0xffc) this.LogUnhandledWrite(offset, value);
                break;
            }
            UpdateSignals();
        }

        private void TryStartTransmit()
        {
            if(shifting || tx.Count == 0 || !TransmitEnabled || activeDivisor == 0 || Frequency == 0
                || (lineControl & 1) != 0 || ((control & 0x8000) != 0 && (ModemFlags & 1) == 0)) return;
            var previousCount = tx.Count;
            shiftByte = (byte)(tx.Dequeue() & DataMask);
            shifting = true;
            shiftLoopback = Loopback;
            if(previousCount > TransmitThreshold && tx.Count <= TransmitThreshold || Capacity == 1 && previousCount != 0)
            {
                interruptStatus |= TransmitInterrupt;
            }
            txTimer.Enabled = false;
            txTimer.Limit = activeDivisor * (ulong)FrameBits;
            txTimer.ResetValue();
            txTimer.Enabled = true;
        }

        private void CompleteTransmit()
        {
            lock(SyncRoot)
            {
                if(!shifting || ResetHeld) return;
                shifting = false;
                if(shiftLoopback)
                {
                    if(ReceiveEnabled) ReceiveCharacter(shiftByte);
                }
                else notifications.Enqueue(new Notification(TransmittedCharacter, shiftByte, generation));
                TryStartTransmit();
                UpdateSignals();
            }
            PublishPending();
        }

        private void ReceiveCharacter(byte value)
        {
            if(rx.Count >= Capacity)
            {
                pendingOverrun = true;
                receiveStatus |= 8;
                interruptStatus |= OverrunInterrupt;
            }
            else
            {
                rx.Enqueue((uint)(value & DataMask) | (pendingOverrun ? 0x800u : 0));
                pendingOverrun = false;
                if(rx.Count >= ReceiveThreshold) interruptStatus |= ReceiveInterrupt;
            }
            RestartTimeout();
            UpdateSignals();
        }

        private void RestartTimeout()
        {
            timeoutTimer.Enabled = false;
            timeoutPending = rx.Count != 0 && activeDivisor != 0;
            if(timeoutPending)
            {
                timeoutTimer.Limit = activeDivisor * 32ul;
                timeoutTimer.ResetValue();
                timeoutTimer.Enabled = ReceiveEnabled && Frequency != 0;
            }
        }

        private void ReceiveTimeout()
        {
            lock(SyncRoot)
            {
                timeoutPending = false;
                if(ReceiveEnabled && rx.Count != 0) interruptStatus |= TimeoutInterrupt;
                UpdateSignals();
            }
            PublishPending();
        }

        private void OnClockChanged()
        {
            SynchronizeTime();
            machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClock(); });
            PublishPending();
        }

        private void UpdateClock()
        {
            var frequency = Frequency;
            if(frequency != 0)
            {
                txTimer.Frequency = frequency * 4;
                timeoutTimer.Frequency = frequency * 4;
            }
            txTimer.Enabled = !ResetHeld && frequency != 0 && shifting;
            timeoutTimer.Enabled = frequency != 0 && timeoutPending && ReceiveEnabled;
            TryStartTransmit();
            UpdateSignals();
        }

        private void UpdateSignals()
        {
            QueueSignal(0, !ResetHeld && (interruptStatus & interruptMask) != 0);
            QueueSignal(1, !RTSAsserted);
            var dmaEnabled = !ResetHeld && (control & 1) != 0 && Frequency != 0;
            QueueSignal(2, dmaEnabled && (dmaControl & 1) != 0 && rx.Count != 0
                && ((dmaControl & 4) == 0 || (interruptStatus & 0x780) == 0));
            QueueSignal(3, dmaEnabled && (dmaControl & 2) != 0 && tx.Count < Capacity);
        }

        private void QueueSignal(int signal, bool state)
        {
            if(signalStates[signal] == state) return;
            signalStates[signal] = state;
            notifications.Enqueue(new Notification(signal, state ? 1 : 0, generation));
        }

        // Signal receivers and serial peers may synchronously call another
        // device. Publish after releasing the UART state lock.
        private void PublishPending()
        {
            // ResetHeld invokes Reset while the APB base owns this monitor.
            // Its completion hook publishes after that outer lock is released.
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
                    lock(SyncRoot)
                    {
                        if(notifications.Count == 0)
                        {
                            publishing = false;
                            return;
                        }
                        notification = notifications.Dequeue();
                        if(notification.Generation != generation) continue;
                    }
                    switch(notification.Signal)
                    {
                    case 0: IRQ.Set(notification.Value != 0); break;
                    case 1: RTS.Set(notification.Value != 0); break;
                    case 2: RxDMA.Set(notification.Value != 0); break;
                    case 3: TxDMA.Set(notification.Value != 0); break;
                    default: CharReceived?.Invoke((byte)notification.Value); break;
                    }
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private void SynchronizeTime()
        {
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        private uint ModemFlags => Loopback
            ? ((RTSAsserted ? 1u : 0) | ((control & 0x400) != 0 ? 2u : 0)
                | ((control & 0x1000) != 0 ? 4u : 0) | ((control & 0x2000) != 0 ? 0x100u : 0))
            : ((!modemInputs[0] ? 1u : 0) | (!modemInputs[1] ? 2u : 0)
                | (!modemInputs[2] ? 4u : 0) | (!modemInputs[3] ? 0x100u : 0));

        private bool RTSAsserted => !ResetHeld && ((control & 0x4000) != 0
            ? rx.Count < ReceiveThreshold : (control & 0x800) != 0);
        private bool Loopback => (control & 0x80) != 0;
        private bool TransmitEnabled => !ResetHeld && (control & 0x101) == 0x101 && (control & 2) == 0;
        private bool ReceiveEnabled => !ResetHeld && (control & 0x201) == 0x201 && (control & 2) == 0 && Frequency != 0 && activeDivisor != 0;
        private ulong Frequency => clocks == null ? fallbackFrequency : clocks.PeripheralFrequency;
        private int Capacity => (lineControl & 0x10) != 0 ? 32 : 1;
        private int DataBits => 5 + (int)((lineControl >> 5) & 3);
        private int DataMask => (1 << DataBits) - 1;
        private int FrameBits => 1 + DataBits + ((lineControl & 2) != 0 ? 1 : 0) + ((lineControl & 8) != 0 ? 2 : 1);
        private int ReceiveThreshold => Capacity == 1 ? 1 : FifoThreshold((int)(fifoLevels >> 3) & 7);
        private int TransmitThreshold => Capacity == 1 ? 0 : FifoThreshold((int)fifoLevels & 7);
        private static int FifoThreshold(int selection) => selection <= 4 ? FifoThresholds[selection] : 16;
        private static uint ModemFlag(int input) => input == 3 ? 0x100u : 1u << input;
        private static uint ModemInterrupt(int input) => 1u << (input == 0 ? 1 : input == 1 ? 3 : input == 2 ? 2 : 0);

        private struct Notification
        {
            public Notification(int signal, int value, uint generation)
            {
                Signal = signal;
                Value = value;
                Generation = generation;
            }
            public readonly int Signal;
            public readonly int Value;
            public readonly uint Generation;
        }

        private readonly IRP2ClockSource clocks;
        private readonly ulong fallbackFrequency;
        private readonly Queue<byte> tx;
        private readonly Queue<uint> rx;
        private readonly Queue<Notification> notifications;
        private readonly bool[] signalStates;
        private readonly bool[] modemInputs;
        private readonly LimitTimer txTimer;
        private readonly LimitTimer timeoutTimer;
        private bool shifting, shiftLoopback, timeoutPending, pendingOverrun, publishing;
        private byte shiftByte;
        private uint generation;
        private uint integerDivisor, fractionalDivisor, activeDivisor, lineControl, control, fifoLevels;
        private uint interruptMask, interruptStatus, dmaControl, receiveStatus, irdaDivisor;

        private static readonly int[] FifoThresholds = { 4, 8, 16, 24, 28 };
        private static readonly byte[] Identification = { 0x11, 0x10, 0x34, 0, 0x0d, 0xf0, 5, 0xb1 };
        private const int TransmittedCharacter = 4;
        private const uint ReceiveInterrupt = 1u << 4;
        private const uint TransmitInterrupt = 1u << 5;
        private const uint TimeoutInterrupt = 1u << 6;
        private const uint OverrunInterrupt = 1u << 10;
    }
}
