//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;

namespace Antmicro.Renode.Peripherals.CPU
{
    // Arm IHI 0074E, chapters B4 and B5. SWDIO is sampled on rising SWCLK
    // edges; all target drive transitions happen on falling edges.
    public abstract class SerialWireDebugPort : IPeripheral, IThreeStateGPIO
    {
        protected SerialWireDebugPort()
        {
            Reset();
        }

        public void Reset()
        {
            // A callback from an accepted bus transaction may request reset.
            // Invalidate framing/output immediately; reset backend state after
            // that transaction unwinds, before any new-generation input.
            lock(sync) ResetProtocol(false);
            ProcessPending();
        }

        private void ResetProtocol(bool resetTarget)
        {
            targetResetPending |= resetTarget;
            generation++;
            pending.Clear();
            notifications.Clear();
            state = State.Dormant;
            clock = sampledClock;
            data = sampledData;
            consecutiveOnes = 0;
            alertPosition = 0;
            activationPosition = 0;
            shift = 0;
            bitCount = 0;
            resetSequencePosition = -1;
            requireIdentification = true;
            output = outputEnabled = false;
            // Publish the reset state even if a superseded release notification
            // had already changed our latch but had not reached the wire.
            notifications.Enqueue(new Notification(generation, false, false));
            pending.Enqueue(new Input(generation, targetResetPending));
        }

        public void OnGPIO(int number, bool value)
        {
            lock(sync)
            {
                switch(number)
                {
                case 0: sampledClock = value; break;
                case 1: sampledData = value; break;
                case 2:
                    if(!value && !resetAsserted)
                    {
                        resetAsserted = true;
                        ResetProtocol(true);
                    }
                    else if(value && resetAsserted)
                    {
                        resetAsserted = false;
                        clock = sampledClock;
                        data = sampledData;
                    }
                    break;
                default: return;
                }
                if(number != 2 && !resetAsserted) pending.Enqueue(new Input(generation, number, value));
            }
            ProcessPending();
        }

        private Action ProcessInput(Input input)
        {
            if(input.Reset)
            {
                return () =>
                {
                    ResetBackend();
                    var resetTarget = false;
                    lock(sync)
                    {
                        if(input.Generation == generation && input.ResetTarget && targetResetPending)
                        {
                            targetResetPending = false;
                            resetTarget = true;
                        }
                    }
                    if(resetTarget) ResetTarget();
                };
            }
            var value = input.Value;
            switch(input.Number)
            {
            case 0:
                if(clock == value) return null;
                clock = value;
                if(resetAsserted) return null;
                if(clock) return RisingEdge(input.Generation);
                FallingEdge();
                break;
            case 1: data = value; break;
            }
            return null;
        }

        private bool IsCurrent(ulong expectedGeneration)
        {
            lock(sync) return expectedGeneration == generation;
        }

        // The owner serializes backend operations, but never holds the protocol
        // monitor across bus accesses, CPU halt acknowledgements or wire callbacks.
        // Reentrant/foreign callbacks enqueue work rather than waiting for that owner.
        private void ProcessPending()
        {
            lock(sync)
            {
                if(processing) return;
                processing = true;
            }
            try
            {
                while(true)
                {
                    Action action = null;
                    Input input = default(Input);
                    Notification notification = default(Notification);
                    var publish = false;
                    lock(sync)
                    {
                        if(notifications.Count != 0)
                        {
                            notification = notifications.Dequeue();
                            publish = true;
                        }
                        else if(pending.Count != 0)
                        {
                            input = pending.Dequeue();
                            if(input.Generation == generation) action = ProcessInput(input);
                        }
                        else
                        {
                            processing = false;
                            return;
                        }
                    }
                    if(publish)
                    {
                        var handlers = DriveChanged;
                        if(handlers == null) continue;
                        foreach(Action<int, bool, bool> handler in handlers.GetInvocationList())
                        {
                            if(!IsCurrent(notification.Generation)) break;
                            handler(1, notification.Level, notification.Enabled);
                        }
                    }
                    else if(action != null && IsCurrent(input.Generation)) action();
                }
            }
            catch
            {
                lock(sync) processing = false;
                throw;
            }
        }

        public void GetOutputState(int number, out bool level, out bool enabled)
        {
            lock(sync)
            {
                level = output;
                enabled = number == 1 && outputEnabled;
            }
        }

        public event Action<int, bool, bool> DriveChanged;

        protected abstract Acknowledgement BeginTransfer(bool accessPort, bool read, byte address, out uint value);
        protected abstract void CompleteWrite(bool accessPort, byte address, uint value);
        protected abstract void WriteParityError();
        protected virtual void ResetBackend() { }
        protected virtual void LineReset() { }
        protected virtual void ResetTarget() { }
        protected virtual bool SelectTarget(uint value) { return true; }

        private Action RisingEdge(ulong requestGeneration)
        {
            // A line reset must be recognized from any active protocol state,
            // including a truncated transfer and the protocol error lockout state.
            consecutiveOnes = data ? consecutiveOnes + 1 : 0;
            if(state != State.Dormant && state != State.Activation && consecutiveOnes >= 50)
            {
                state = State.Reset;
                bitCount = 0;
                shift = 0;
                resetSequencePosition = 0;
                resetSequence = 0;
                requireIdentification = true;
                return LineReset;
            }
            if(resetSequencePosition >= 0)
            {
                if(data) resetSequence |= 1u << resetSequencePosition;
                if(++resetSequencePosition == 16)
                {
                    resetSequencePosition = -1;
                    if(resetSequence == 0xe3bc)
                    {
                        state = State.Dormant;
                        alertPosition = 0;
                        return null;
                    }
                }
            }
            switch(state)
            {
            case State.Dormant:
                var expected = ((SelectionAlert[alertPosition / 32] >> (alertPosition % 32)) & 1) != 0;
                if(data == expected)
                {
                    if(++alertPosition == 128)
                    {
                        state = State.Activation;
                        activationPosition = 0;
                        shift = 0;
                    }
                }
                else alertPosition = data ? 0 : 1;
                return null;
            case State.Activation:
                if(activationPosition >= 4 && data) shift |= 1u << (activationPosition - 4);
                if(++activationPosition == 12)
                {
                    state = shift == 0x1a ? State.Lockout : State.Dormant;
                    alertPosition = 0;
                    consecutiveOnes = 0;
                }
                return null;
            case State.Lockout:
                return null;
            case State.Reset:
                if(data) return null;
                state = State.Request;
                bitCount = 0;
                shift = 0;
                return null;
            case State.Request:
                if(bitCount == 0 && !data) return null;
                if(data) shift |= 1u << bitCount;
                if(++bitCount != 8) return null;
                // start=1, stop=0, park=1; even parity across APnDP/RnW/A[3:2].
                if((shift & 0xc1) != 0x81 || Parity((shift >> 1) & 0x1f))
                {
                    state = State.Lockout;
                    return null;
                }
                accessPort = (shift & 2) != 0;
                read = (shift & 4) != 0;
                address = (byte)((shift >> 1) & 0xc);
                targetSelection = requireIdentification && !accessPort && !read && address == 12;
                if(requireIdentification && !targetSelection && (accessPort || !read || address != 0))
                {
                    state = State.Lockout;
                    return null;
                }
                state = State.RequestTurnaround;
                bitCount = 0;
                if(targetSelection)
                {
                    acknowledgement = 0;
                    return null;
                }
                requireIdentification = false;
                var requestAP = accessPort;
                var requestRead = read;
                var requestAddress = address;
                return () =>
                {
                    var ack = BeginTransfer(requestAP, requestRead, requestAddress, out var value);
                    lock(sync)
                    {
                        if(requestGeneration != generation) return;
                        acknowledgement = ack;
                        transferValue = value;
                    }
                };
            case State.RequestTurnaround:
                state = State.Acknowledgement;
                bitCount = 0;
                return null;
            case State.Acknowledgement:
                if(++bitCount != 3) return null;
                bitCount = 0;
                if(acknowledgement != Acknowledgement.Ok || !read) state = State.WriteTurnaround;
                else state = State.ReadData;
                return null;
            case State.ReadData:
                if(++bitCount == 33) state = State.ReadTurnaround;
                return null;
            case State.ReadTurnaround:
                FinishTransfer();
                return null;
            case State.WriteTurnaround:
                if(acknowledgement != Acknowledgement.Ok && !targetSelection) FinishTransfer();
                else
                {
                    state = State.WriteData;
                    shift = 0;
                    bitCount = 0;
                }
                return null;
            case State.WriteData:
                if(bitCount < 32)
                {
                    if(data) shift |= 1u << bitCount;
                    bitCount++;
                    return null;
                }
                var writeValue = shift;
                if(targetSelection)
                {
                    if(data != Parity(writeValue))
                    {
                        state = State.Lockout;
                        return null;
                    }
                    FinishTransfer();
                    return () =>
                    {
                        var selected = SelectTarget(writeValue);
                        lock(sync)
                        {
                            if(requestGeneration == generation && !selected) state = State.Lockout;
                        }
                    };
                }
                var parityValid = data == Parity(writeValue);
                var writeAP = accessPort;
                var writeAddress = address;
                FinishTransfer();
                return parityValid ? (Action)(() => CompleteWrite(writeAP, writeAddress, writeValue)) : WriteParityError;
            }
            return null;
        }

        private void FallingEdge()
        {
            switch(state)
            {
            case State.Acknowledgement:
                SetDrive((((int)acknowledgement >> bitCount) & 1) != 0, !targetSelection);
                break;
            case State.ReadData:
                SetDrive(bitCount == 32 ? Parity(transferValue) : ((transferValue >> bitCount) & 1) != 0, true);
                break;
            default:
                SetDrive(false, false);
                break;
            }
        }

        private void FinishTransfer()
        {
            state = State.Request;
            bitCount = 0;
            shift = 0;
        }

        private void SetDrive(bool level, bool enabled)
        {
            if(output == level && outputEnabled == enabled) return;
            output = level;
            outputEnabled = enabled;
            notifications.Enqueue(new Notification(generation, level, enabled));
        }

        private static bool Parity(uint value)
        {
            value ^= value >> 16;
            value ^= value >> 8;
            value ^= value >> 4;
            return ((0x6996 >> (int)(value & 0xf)) & 1) != 0;
        }

        protected enum Acknowledgement { Ok = 1, Wait = 2, Fault = 4 }
        private enum State { Dormant, Activation, Lockout, Reset, Request, RequestTurnaround, Acknowledgement, ReadData, ReadTurnaround, WriteTurnaround, WriteData }
        // IHI 0074E B5.3.3 transmits the complete 128-bit value LSB first:
        // the least-significant word is clocked before the remaining words.
        private static readonly uint[] SelectionAlert = { 0x6209f392, 0x86852d95, 0xe3ddafe9, 0x19bc0ea2 };
        private State state;
        private bool data = true;
        private bool clock, output, outputEnabled, resetAsserted, accessPort, read, requireIdentification, targetSelection;
        private int consecutiveOnes, alertPosition, activationPosition, bitCount, resetSequencePosition;
        private uint shift, transferValue, resetSequence;
        private byte address;
        private Acknowledgement acknowledgement;
        private readonly object sync = new object();
        private readonly Queue<Input> pending = new Queue<Input>();
        private readonly Queue<Notification> notifications = new Queue<Notification>();
        private ulong generation;
        private bool processing;
        private bool sampledClock;
        private bool sampledData = true;
        private bool targetResetPending;

        private readonly struct Input
        {
            public Input(ulong generation, int number, bool value)
            {
                Generation = generation;
                Number = number;
                Value = value;
                Reset = ResetTarget = false;
            }

            public Input(ulong generation, bool resetTarget)
            {
                Generation = generation;
                Number = 0;
                Value = false;
                Reset = true;
                ResetTarget = resetTarget;
            }

            public readonly ulong Generation;
            public readonly int Number;
            public readonly bool Value, Reset, ResetTarget;
        }

        private readonly struct Notification
        {
            public Notification(ulong generation, bool level, bool enabled)
            {
                Generation = generation;
                Level = level;
                Enabled = enabled;
            }

            public readonly ulong Generation;
            public readonly bool Level, Enabled;
        }
    }
}
