//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    [GPIO(NumberOfInputs = 48)]
    public sealed class RP2_GPIOBank0 : RP2APBPeripheral, IGPIOReceiver, INumberedGPIOOutput
    {
        public RP2_GPIOBank0(IMachine machine, IRP2GPIO sio = null, bool rp2350 = false, int numberOfPins = 0) : base(machine)
        {
            this.sio = sio;
            this.rp2350 = rp2350;
            registerCount = rp2350 ? 48 : 30;
            this.numberOfPins = numberOfPins == 0 ? registerCount : numberOfPins;
            if(this.numberOfPins < 1 || this.numberOfPins > registerCount)
            {
                throw new ConstructionException("GPIO pin count is outside the selected RP2 variant");
            }
            interruptGroupCount = rp2350 ? 6 : 4;
            rawInterruptOffset = rp2350 ? 0x230 : 0xf0;
            control = new uint[registerCount];
            pads = new uint[registerCount];
            external = new bool?[registerCount];
            sampled = new bool[registerCount];
            interruptInput = new bool[registerCount];
            drivenValue = new bool[registerCount];
            drivenEnable = new bool[registerCount];
            peripheralOutput = new ulong[32];
            peripheralEnable = new ulong[32];
            edgeInterrupts = new uint[interruptGroupCount];
            interruptEnable = new uint[3, interruptGroupCount];
            interruptForce = new uint[3, interruptGroupCount];
            var connections = new Dictionary<int, IGPIO>();
            for(var pin = 0; pin < registerCount; pin++)
            {
                connections.Add(pin, new GPIO());
                pads[pin] = rp2350 ? 0x116u : 0x56u;
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            Core0IRQ = new GPIO();
            Core1IRQ = new GPIO();
            DormantWakeIRQ = new GPIO();
            if(sio != null)
            {
                sio.GPIOChanged += UpdatePins;
            }
            Reset();
        }

        public void OnGPIO(int number, bool value)
        {
            if(!IsBondedPin(number))
            {
                return;
            }
            external[number] = value;
            UpdatePin(number);
            UpdateInterrupts();
        }

        public void ReleaseInput(int pin)
        {
            if(!IsBondedPin(pin))
            {
                return;
            }
            external[pin] = null;
            UpdatePin(pin);
            UpdateInterrupts();
        }

        public void SetPeripheralOutput(int function, int pin, bool value, bool enabled)
        {
            if(function < 0 || function >= peripheralOutput.Length || !IsBondedPin(pin))
            {
                return;
            }
            var mask = 1UL << pin;
            peripheralOutput[function] = value ? peripheralOutput[function] | mask : peripheralOutput[function] & ~mask;
            peripheralEnable[function] = enabled ? peripheralEnable[function] | mask : peripheralEnable[function] & ~mask;
            UpdatePin(pin);
            UpdateInterrupts();
        }

        public void SetPadConfiguration(int pin, uint value)
        {
            if(pin < 0 || pin >= registerCount)
            {
                return;
            }
            pads[pin] = value;
            UpdatePin(pin);
            UpdateInterrupts();
        }

        public bool GetInput(int pin)
        {
            return IsBondedPin(pin) && sampled[pin];
        }

        public override void Reset()
        {
            resetting = true;
            try
            {
                for(var pin = 0; pin < control.Length; pin++)
                {
                    control[pin] = 0x1f;
                }
                Array.Clear(edgeInterrupts, 0, edgeInterrupts.Length);
                Array.Clear(interruptEnable, 0, interruptEnable.Length);
                Array.Clear(interruptForce, 0, interruptForce.Length);
                UpdatePins();
            }
            finally
            {
                resetting = false;
            }
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public GPIO Core0IRQ { get; }

        public GPIO Core1IRQ { get; }

        public GPIO DormantWakeIRQ { get; }

        public event Action<int, bool> InputChanged;

        // Consumers resolving a shared wire need both the driven level and OE.
        public event Action<int, bool, bool> DriveChanged;

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0 && offset < registerCount * 8)
            {
                var pin = (int)(offset / 8);
                if((offset & 4) != 0)
                {
                    return control[pin];
                }
                var input = ReadPadInput(pin);
                var status = (drivenValue[pin] ? 1u << 9 : 0) | (drivenEnable[pin] ? 1u << 13 : 0)
                    | (input ? 1u << 17 : 0) | (interruptInput[pin] ? 1u << 26 : 0);
                if(!rp2350)
                {
                    var function = control[pin] & 0x1f;
                    status |= (PeripheralValue(pin, function) ? 1u << 8 : 0)
                        | (PeripheralEnabled(pin, function) ? 1u << 12 : 0)
                        | (sampled[pin] ? 1u << 19 : 0) | (input ? 1u << 24 : 0);
                }
                return status;
            }
            if(rp2350 && offset >= 0x200 && offset < 0x230)
            {
                var target = (int)((offset - 0x200) / 16);
                if(((offset - 0x200) & 8) != 0)
                {
                    return 0;
                }
                var firstPin = ((offset & 4) != 0) ? 32 : 0;
                uint summary = 0;
                for(var bit = 0; bit < 32 && firstPin + bit < numberOfPins; bit++)
                {
                    var pin = firstPin + bit;
                    if((MaskedInterrupts(target, pin / 8) & (0xfu << ((pin % 8) * 4))) != 0)
                    {
                        summary |= 1u << bit;
                    }
                }
                return summary;
            }
            if(TryDecodeInterrupt(offset, out var targetIndex, out var kind, out var group))
            {
                switch(kind)
                {
                case 0: return RawInterrupts(group);
                case 1: return interruptEnable[targetIndex, group];
                case 2: return interruptForce[targetIndex, group];
                case 3: return MaskedInterrupts(targetIndex, group);
                }
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset >= 0 && offset < registerCount * 8)
            {
                if((offset & 4) != 0)
                {
                    var pin = (int)(offset / 8);
                    control[pin] = ApplyOperation(control[pin], value, operation, 0x3003f01f);
                    UpdatePin(pin);
                    UpdateInterrupts();
                }
                return;
            }
            if(TryDecodeInterrupt(offset, out var target, out var kind, out var group))
            {
                switch(kind)
                {
                case 0:
                    edgeInterrupts[group] &= ~(value & 0xcccccccc);
                    break;
                case 1:
                    interruptEnable[target, group] = ApplyOperation(interruptEnable[target, group], value, operation, InterruptMask(group));
                    break;
                case 2:
                    interruptForce[target, group] = ApplyOperation(interruptForce[target, group], value, operation, InterruptMask(group));
                    break;
                }
                UpdateInterrupts();
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private bool TryDecodeInterrupt(long offset, out int target, out int kind, out int group)
        {
            target = 0;
            kind = 0;
            group = 0;
            var index = (int)((offset - rawInterruptOffset) / 4);
            if(offset < rawInterruptOffset || (offset & 3) != 0 || index >= interruptGroupCount * 10)
            {
                return false;
            }
            group = index % interruptGroupCount;
            var block = index / interruptGroupCount;
            if(block != 0)
            {
                target = (block - 1) / 3;
                kind = (block - 1) % 3 + 1;
            }
            return true;
        }

        private uint RawInterrupts(int group)
        {
            var result = edgeInterrupts[group];
            for(var index = 0; index < 8; index++)
            {
                var pin = group * 8 + index;
                if(pin >= numberOfPins)
                {
                    break;
                }
                result |= (interruptInput[pin] ? 2u : 1u) << (index * 4);
            }
            return result;
        }

        private uint MaskedInterrupts(int target, int group)
        {
            return (RawInterrupts(group) & interruptEnable[target, group]) | interruptForce[target, group];
        }

        private uint InterruptMask(int group)
        {
            var pins = Math.Min(8, Math.Max(0, numberOfPins - group * 8));
            return pins == 8 ? uint.MaxValue : (1u << (pins * 4)) - 1;
        }

        private void UpdatePins()
        {
            for(var pin = 0; pin < numberOfPins; pin++)
            {
                UpdatePin(pin);
            }
            UpdateInterrupts();
        }

        private void UpdatePin(int pin)
        {
            var function = control[pin] & 0x1f;
            var value = Override(PeripheralValue(pin, function), (control[pin] >> 12) & 3);
            var enabled = Override(PeripheralEnabled(pin, function), (control[pin] >> 14) & 3);
            enabled &= (pads[pin] & 0x80) == 0 && (!rp2350 || (pads[pin] & 0x100) == 0);
            var driveChanged = value != drivenValue[pin] || enabled != drivenEnable[pin];
            drivenValue[pin] = value;
            drivenEnable[pin] = enabled;
            var padInput = ReadPadInput(pin);
            var input = Override(padInput, (control[pin] >> 16) & 3);
            if(sampled[pin] != input)
            {
                sampled[pin] = input;
                sio?.OnGPIO(pin, input);
                InputChanged?.Invoke(pin, input);
            }
            var irqInput = Override(padInput, (control[pin] >> 28) & 3);
            if(irqInput != interruptInput[pin])
            {
                if(!resetting && !ResetHeld)
                {
                    edgeInterrupts[pin / 8] |= (irqInput ? 8u : 4u) << ((pin % 8) * 4);
                }
                interruptInput[pin] = irqInput;
            }
            Connections[pin].Set(ReadWireLevel(pin));
            if(driveChanged)
            {
                DriveChanged?.Invoke(pin, value, enabled);
            }
        }

        private bool ReadPadInput(int pin)
        {
            return (pads[pin] & 0x40) != 0 && (!rp2350 || (pads[pin] & 0x100) == 0) && ReadWireLevel(pin);
        }

        private bool ReadWireLevel(int pin)
        {
            if(external[pin].HasValue)
            {
                return external[pin].Value;
            }
            if(drivenEnable[pin])
            {
                return drivenValue[pin];
            }
            var pulls = pads[pin] & 0xc;
            return pulls == 0xc ? sampled[pin] : pulls == 8;
        }

        private bool PeripheralValue(int pin, uint function)
        {
            return function == 5 && sio != null ? (sio.Output & (1UL << pin)) != 0
                : (peripheralOutput[function] & (1UL << pin)) != 0;
        }

        private bool PeripheralEnabled(int pin, uint function)
        {
            return function == 5 && sio != null ? (sio.OutputEnable & (1UL << pin)) != 0
                : (peripheralEnable[function] & (1UL << pin)) != 0;
        }

        private void UpdateInterrupts()
        {
            var state = new bool[3];
            for(var target = 0; target < state.Length; target++)
            {
                for(var group = 0; group < interruptGroupCount; group++)
                {
                    state[target] |= MaskedInterrupts(target, group) != 0;
                }
            }
            Core0IRQ.Set(state[0]);
            Core1IRQ.Set(state[1]);
            DormantWakeIRQ.Set(state[2]);
        }

        private bool IsBondedPin(int pin) => pin >= 0 && pin < numberOfPins;

        private static bool Override(bool value, uint mode)
        {
            switch(mode)
            {
            case 1: return !value;
            case 2: return false;
            case 3: return true;
            default: return value;
            }
        }

        private bool resetting;

        private readonly IRP2GPIO sio;
        private readonly bool rp2350;
        private readonly int registerCount;
        private readonly int numberOfPins;
        private readonly int interruptGroupCount;
        private readonly long rawInterruptOffset;
        private readonly uint[] control;
        private readonly uint[] pads;
        private readonly bool?[] external;
        private readonly bool[] sampled;
        private readonly bool[] interruptInput;
        private readonly bool[] drivenValue;
        private readonly bool[] drivenEnable;
        private readonly ulong[] peripheralOutput;
        private readonly ulong[] peripheralEnable;
        private readonly uint[] edgeInterrupts;
        private readonly uint[,] interruptEnable;
        private readonly uint[,] interruptForce;
    }
}
