//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

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

        public virtual void Reset()
        {
            state = State.Dormant;
            consecutiveOnes = 0;
            alertPosition = 0;
            activationPosition = 0;
            shift = 0;
            bitCount = 0;
            resetSequencePosition = -1;
            requireIdentification = true;
            SetDrive(false, false);
        }

        public void OnGPIO(int number, bool value)
        {
            switch(number)
            {
            case 0:
                if(clock == value) return;
                clock = value;
                if(resetAsserted) return;
                if(clock) RisingEdge();
                else FallingEdge();
                break;
            case 1:
                data = value;
                break;
            case 2:
                if(!value && !resetAsserted)
                {
                    Reset();
                    resetAsserted = true;
                    ResetTarget();
                }
                else if(value) resetAsserted = false;
                break;
            }
        }

        public void GetOutputState(int number, out bool level, out bool enabled)
        {
            level = output;
            enabled = number == 1 && outputEnabled;
        }

        public event Action<int, bool, bool> DriveChanged;

        protected abstract Acknowledgement BeginTransfer(bool accessPort, bool read, byte address, out uint value);
        protected abstract void CompleteWrite(bool accessPort, byte address, uint value);
        protected abstract void WriteParityError();
        protected virtual void LineReset() { }
        protected virtual void ResetTarget() { }
        protected virtual bool SelectTarget(uint value) { return true; }

        private void RisingEdge()
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
                LineReset();
                return;
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
                        return;
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
                return;
            case State.Activation:
                if(activationPosition >= 4 && data) shift |= 1u << (activationPosition - 4);
                if(++activationPosition == 12)
                {
                    state = shift == 0x1a ? State.Lockout : State.Dormant;
                    alertPosition = 0;
                    consecutiveOnes = 0;
                }
                return;
            case State.Lockout:
                return;
            case State.Reset:
                if(data) return;
                state = State.Request;
                bitCount = 0;
                shift = 0;
                return;
            case State.Request:
                if(bitCount == 0 && !data) return;
                if(data) shift |= 1u << bitCount;
                if(++bitCount != 8) return;
                // start=1, stop=0, park=1; even parity across APnDP/RnW/A[3:2].
                if((shift & 0xc1) != 0x81 || Parity((shift >> 1) & 0x1f))
                {
                    state = State.Lockout;
                    return;
                }
                accessPort = (shift & 2) != 0;
                read = (shift & 4) != 0;
                address = (byte)((shift >> 1) & 0xc);
                targetSelection = requireIdentification && !accessPort && !read && address == 12;
                if(requireIdentification && !targetSelection && (accessPort || !read || address != 0))
                {
                    state = State.Lockout;
                    return;
                }
                if(targetSelection) acknowledgement = 0;
                else
                {
                    requireIdentification = false;
                    acknowledgement = BeginTransfer(accessPort, read, address, out transferValue);
                }
                state = State.RequestTurnaround;
                bitCount = 0;
                return;
            case State.RequestTurnaround:
                state = State.Acknowledgement;
                bitCount = 0;
                return;
            case State.Acknowledgement:
                if(++bitCount != 3) return;
                bitCount = 0;
                if(acknowledgement != Acknowledgement.Ok || !read) state = State.WriteTurnaround;
                else state = State.ReadData;
                return;
            case State.ReadData:
                if(++bitCount == 33) state = State.ReadTurnaround;
                return;
            case State.ReadTurnaround:
                FinishTransfer();
                return;
            case State.WriteTurnaround:
                if(acknowledgement != Acknowledgement.Ok && !targetSelection) FinishTransfer();
                else
                {
                    state = State.WriteData;
                    shift = 0;
                    bitCount = 0;
                }
                return;
            case State.WriteData:
                if(bitCount < 32)
                {
                    if(data) shift |= 1u << bitCount;
                    bitCount++;
                    return;
                }
                if(targetSelection)
                {
                    if(data != Parity(shift) || !SelectTarget(shift))
                    {
                        state = State.Lockout;
                        return;
                    }
                }
                else if(data == Parity(shift)) CompleteWrite(accessPort, address, shift);
                else WriteParityError();
                FinishTransfer();
                return;
            }
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
            DriveChanged?.Invoke(1, level, enabled);
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
        private static readonly uint[] SelectionAlert = { 0x19bc0ea2, 0xe3ddafe9, 0x86852d95, 0x6209f392 };
        private State state;
        private bool data = true;
        private bool clock, output, outputEnabled, resetAsserted, accessPort, read, requireIdentification, targetSelection;
        private int consecutiveOnes, alertPosition, activationPosition, bitCount, resetSequencePosition;
        private uint shift, transferValue, resetSequence;
        private byte address;
        private Acknowledgement acknowledgement;
    }
}
