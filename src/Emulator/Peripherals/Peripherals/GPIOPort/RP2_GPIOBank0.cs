//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    [GPIO(NumberOfInputs = 48)]
    public sealed class RP2_GPIOBank0 : RP2APBPeripheral, IThreeStateGPIO, INumberedGPIOOutput
    {
        public RP2_GPIOBank0(IMachine machine, IRP2GPIO sio = null, bool rp2350 = false, int numberOfPins = 0,
            RP2350_AccessControl accessControl = null, int securityPinOffset = 0) : base(machine)
        {
            this.sio = sio;
            this.rp2350 = rp2350;
            this.accessControl = accessControl;
            this.securityPinOffset = securityPinOffset;
            registerCount = rp2350 ? 48 : 30;
            this.numberOfPins = numberOfPins == 0 ? registerCount : numberOfPins;
            if(this.numberOfPins < 1 || this.numberOfPins > registerCount)
            {
                throw new ConstructionException("GPIO pin count is outside the selected RP2 variant");
            }
            if(securityPinOffset < 0 || securityPinOffset + this.numberOfPins > 64)
            {
                throw new ConstructionException("GPIO security pin range must fit the 64 SIO bit positions");
            }
            interruptGroupCount = rp2350 ? 6 : 4;
            rawInterruptOffset = rp2350 ? 0x230 : 0xf0;
            control = new uint[registerCount];
            pads = new uint[registerCount];
            isolated = new bool[registerCount];
            isolatedOutput = new bool[registerCount];
            isolatedEnable = new bool[registerCount];
            external = new bool?[registerCount];
            sampled = new bool[registerCount];
            interruptInput = new bool[registerCount];
            drivenValue = new bool[registerCount];
            drivenEnable = new bool[registerCount];
            wireLevel = new bool[registerCount];
            peripheralOutput = new ulong[32];
            peripheralEnable = new ulong[32];
            edgeInterrupts = new uint[interruptGroupCount];
            interruptEnable = new uint[3, interruptGroupCount];
            interruptForce = new uint[3, interruptGroupCount];
            var connections = new Dictionary<int, IGPIO>();
            for(var pin = 0; pin < registerCount; pin++)
            {
                connections.Add(pin, new GPIO());
                pads[pin] = rp2350 ? 0x16u : 0x56u;
                isolated[pin] = rp2350;
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            Core0IRQ = new GPIO();
            Core1IRQ = new GPIO();
            DormantWakeIRQ = new GPIO();
            Core0NonSecureIRQ = new GPIO();
            Core1NonSecureIRQ = new GPIO();
            DormantWakeNonSecureIRQ = new GPIO();
            interruptOutputs = rp2350
                ? new[] { Core0IRQ, Core1IRQ, DormantWakeIRQ, Core0NonSecureIRQ, Core1NonSecureIRQ, DormantWakeNonSecureIRQ }
                : new[] { Core0IRQ, Core1IRQ, DormantWakeIRQ };
            interruptState = new bool[interruptOutputs.Length];
            if(accessControl != null)
            {
                accessControl.GPIOAccessChanged += OnGPIOAccessChanged;
            }
            if(sio != null)
            {
                sio.GPIOChanged += UpdatePins;
            }
            Reset();
        }

        public void OnGPIO(int number, bool value)
        {
            if(!IsBondedPin(number)) return;
            lock(SyncRoot)
            {
                external[number] = value;
                UpdatePin(number);
                UpdateInterrupts();
            }
            PublishPending();
        }

        public void ReleaseInput(int pin)
        {
            if(!IsBondedPin(pin)) return;
            lock(SyncRoot)
            {
                external[pin] = null;
                UpdatePin(pin);
                UpdateInterrupts();
            }
            PublishPending();
        }

        public void SetPeripheralOutput(int function, int pin, bool value, bool enabled)
        {
            if(function < 0 || function >= peripheralOutput.Length || !IsBondedPin(pin)) return;
            lock(SyncRoot)
            {
                var mask = 1UL << pin;
                peripheralOutput[function] = value ? peripheralOutput[function] | mask : peripheralOutput[function] & ~mask;
                peripheralEnable[function] = enabled ? peripheralEnable[function] | mask : peripheralEnable[function] & ~mask;
                UpdatePin(pin);
                UpdateInterrupts();
            }
            PublishPending();
        }

        public void SetPadConfiguration(int pin, uint value)
        {
            if(pin < 0 || pin >= registerCount) return;
            lock(SyncRoot)
            {
                var isolate = rp2350 && (value & 0x100) != 0;
                if(isolate && !isolated[pin])
                {
                    isolatedOutput[pin] = drivenValue[pin];
                    isolatedEnable[pin] = drivenEnable[pin];
                }
                if(!isolate)
                {
                    pads[pin] = value & 0xff;
                }
                isolated[pin] = isolate;
                UpdatePin(pin);
                UpdateInterrupts();
            }
            PublishPending();
        }

        // Cold reset initializes always-on isolation latches from the pad's
        // reset signals. APB resets use SetPadConfiguration and retain them.
        public void ResetPadConfiguration(int pin, uint value)
        {
            if(pin < 0 || pin >= registerCount) return;
            lock(SyncRoot)
            {
                pads[pin] = value & 0xff;
                isolated[pin] = rp2350 && (value & 0x100) != 0;
                isolatedOutput[pin] = false;
                isolatedEnable[pin] = false;
                UpdatePin(pin);
                UpdateInterrupts();
            }
            PublishPending();
        }

        internal bool IsSecureAccess => !rp2350 || RP2350_GPIOAccess.IsSecure(machine, accessControl);

        internal bool CanAccessPin(int pin)
        {
            return IsSecureAccess
                || (accessControl?.GPIOIsNonSecure(pin + securityPinOffset) ?? false);
        }

        public bool GetInput(int pin)
        {
            lock(SyncRoot)
            {
                return IsBondedPin(pin) && sampled[pin];
            }
        }

        public void GetOutputState(int number, out bool level, out bool enabled)
        {
            lock(SyncRoot)
            {
                if(!IsBondedPin(number))
                {
                    throw new RecoverableException($"GPIO {number} is not bonded on this model");
                }
                level = drivenValue[number];
                enabled = drivenEnable[number];
            }
        }

        public override void Reset()
        {
            lock(SyncRoot)
            {
                resetting = true;
                try
                {
                    for(var pin = 0; pin < control.Length; pin++) control[pin] = 0x1f;
                    Array.Clear(edgeInterrupts, 0, edgeInterrupts.Length);
                    Array.Clear(interruptEnable, 0, interruptEnable.Length);
                    Array.Clear(interruptForce, 0, interruptForce.Length);
                    UpdatePinsLocked();
                }
                finally
                {
                    resetting = false;
                }
            }
            PublishPending();
        }

        // Composite IO/PADS blocks share the state monitor and publish only
        // after their outer APB access has released it.
        internal object SynchronizationRoot => SyncRoot;

        internal void PublishPending()
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
                    lock(SyncRoot)
                    {
                        if(notifications.Count == 0)
                        {
                            publishing = false;
                            return;
                        }
                        notification = notifications.Dequeue();
                    }
                    switch(notification.Kind)
                    {
                    case NotificationKind.Input:
                        sio?.OnGPIO(notification.Pin, notification.Value);
                        InputChanged?.Invoke(notification.Pin, notification.Value);
                        break;
                    case NotificationKind.Wire:
                        Connections[notification.Pin].Set(notification.Value);
                        break;
                    case NotificationKind.Drive:
                        DriveChanged?.Invoke(notification.Pin, notification.Value, notification.Enabled);
                        break;
                    case NotificationKind.Interrupt:
                        interruptOutputs[notification.Pin].Set(notification.Value);
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

        protected override void OnAccessCompleted() => PublishPending();

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public GPIO Core0IRQ { get; }

        public GPIO Core1IRQ { get; }

        public GPIO DormantWakeIRQ { get; }

        public GPIO Core0NonSecureIRQ { get; }

        public GPIO Core1NonSecureIRQ { get; }

        public GPIO DormantWakeNonSecureIRQ { get; }

        public event Action<int, bool> InputChanged;

        // Consumers resolving a shared wire need both the driven level and OE.
        public event Action<int, bool, bool> DriveChanged;

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0 && offset < registerCount * 8)
            {
                var pin = (int)(offset / 8);
                if(!CanAccessPin(pin)) return 0;
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
                var nonSecure = ((offset - 0x200) & 8) != 0;
                if(!nonSecure && !RP2350_GPIOAccess.IsSecure(machine, accessControl)) return 0;
                var firstPin = ((offset & 4) != 0) ? 32 : 0;
                uint summary = 0;
                for(var bit = 0; bit < 32 && firstPin + bit < numberOfPins; bit++)
                {
                    var pin = firstPin + bit;
                    var mask = NonSecureInterruptMask(pin / 8);
                    if(!nonSecure) mask = ~mask;
                    if((MaskedInterrupts(target, pin / 8) & mask & (0xfu << ((pin % 8) * 4))) != 0)
                    {
                        summary |= 1u << bit;
                    }
                }
                return summary;
            }
            if(TryDecodeInterrupt(offset, out var targetIndex, out var kind, out var group))
            {
                var visible = AccessibleInterruptMask(group);
                switch(kind)
                {
                case 0: return RawInterrupts(group) & visible;
                case 1: return interruptEnable[targetIndex, group] & visible;
                case 2: return interruptForce[targetIndex, group] & visible;
                case 3: return MaskedInterrupts(targetIndex, group) & visible;
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
                    if(!CanAccessPin(pin)) return;
                    control[pin] = ApplyFunctionSecurity(pin,
                        ApplyOperation(control[pin], value, operation, rp2350 ? 0x3003f01fu : 0x3003331fu));
                    if(IsBondedPin(pin)) UpdatePin(pin);
                    UpdateInterrupts();
                }
                return;
            }
            if(TryDecodeInterrupt(offset, out var target, out var kind, out var group))
            {
                switch(kind)
                {
                case 0:
                    edgeInterrupts[group] &= ~(value & 0xcccccccc & AccessibleInterruptMask(group));
                    break;
                case 1:
                    interruptEnable[target, group] = ApplyOperation(interruptEnable[target, group], value, operation, AccessibleInterruptMask(group));
                    break;
                case 2:
                    interruptForce[target, group] = ApplyOperation(interruptForce[target, group], value, operation, AccessibleInterruptMask(group));
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

        private void OnGPIOAccessChanged()
        {
            lock(SyncRoot)
            {
                for(var pin = 0; pin < numberOfPins; pin++)
                {
                    control[pin] = ApplyFunctionSecurity(pin, control[pin]);
                }
                UpdatePinsLocked();
            }
            PublishPending();
        }

        private uint ApplyFunctionSecurity(int pin, uint value)
        {
            if(rp2350 && (accessControl?.GPIOIsNonSecure(pin + securityPinOffset) ?? false)
                && !IsFunctionNonSecure(pin, (int)(value & 0x1f)))
            {
                return value | 0x1f;
            }
            return value;
        }

        private bool IsFunctionNonSecure(int pin, int function)
        {
            if(function == 5 || function == 31) return true;
            long permission;
            if(securityPinOffset >= 56)
            {
                if(function != 0) return false;
                permission = pin + securityPinOffset < 58 ? 0x48 : 0xe4; // USB or QMI
            }
            else
            {
                switch(function)
                {
                case 0: permission = 0x80; break; // HSTX
                case 1: permission = 0x90 + ((pin >> 3) & 1) * 4; break; // SPI
                case 2: case 11: permission = 0xa0 + (((pin >> 2) ^ (pin >> 3)) & 1) * 4; break; // UART
                case 3: permission = 0x84 + ((pin >> 1) & 1) * 4; break; // I2C
                case 4: permission = 0x8c; break; // PWM
                case 6: case 7: case 8: permission = 0x4c + (function - 6) * 4; break; // PIO
                case 9:
                    permission = pin == 0 || pin == 8 || pin == 19 || pin == 47 ? 0xe4 // QMI CS1n
                        : pin >= 1 && pin <= 5 ? 0xe8 // CoreSight trace
                        : 0xc0; // clock inputs and outputs
                    break;
                case 10: permission = 0x48; break; // USB power signals
                default: return false;
                }
            }
            return accessControl?.PeripheralIsNonSecure(permission) ?? false;
        }

        private uint NonSecureInterruptMask(int group)
        {
            uint result = 0;
            for(var i = 0; i < 8 && group * 8 + i < numberOfPins; i++)
            {
                if(accessControl?.GPIOIsNonSecure(group * 8 + i + securityPinOffset) ?? false)
                {
                    result |= 0xfu << (4 * i);
                }
            }
            return result;
        }

        private uint AccessibleInterruptMask(int group)
        {
            return InterruptMask(group) & (!rp2350 || RP2350_GPIOAccess.IsSecure(machine, accessControl)
                ? uint.MaxValue : NonSecureInterruptMask(group));
        }

        private void UpdatePins()
        {
            lock(SyncRoot) UpdatePinsLocked();
            PublishPending();
        }

        private void UpdatePinsLocked()
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
            var value = Override(PeripheralValue(pin, function), (control[pin] >> (rp2350 ? 12 : 8)) & 3);
            var enabled = Override(PeripheralEnabled(pin, function), (control[pin] >> (rp2350 ? 14 : 12)) & 3);
            enabled &= (pads[pin] & 0x80) == 0;
            if(isolated[pin])
            {
                value = isolatedOutput[pin];
                enabled = isolatedEnable[pin];
            }
            var driveChanged = value != drivenValue[pin] || enabled != drivenEnable[pin];
            drivenValue[pin] = value;
            drivenEnable[pin] = enabled;
            var padInput = ReadPadInput(pin);
            var input = Override(padInput, (control[pin] >> 16) & 3);
            if(sampled[pin] != input)
            {
                sampled[pin] = input;
                notifications.Enqueue(new Notification(NotificationKind.Input, pin, input));
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
            var wire = ReadWireLevel(pin);
            if(wire != wireLevel[pin])
            {
                wireLevel[pin] = wire;
                notifications.Enqueue(new Notification(NotificationKind.Wire, pin, wire));
            }
            if(driveChanged)
            {
                notifications.Enqueue(new Notification(NotificationKind.Drive, pin, value, enabled));
            }
        }

        private bool ReadPadInput(int pin)
        {
            return (pads[pin] & 0x40) != 0 && ReadWireLevel(pin);
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
            for(var target = 0; target < interruptState.Length; target++)
            {
                var state = false;
                for(var group = 0; group < interruptGroupCount; group++)
                {
                    var mask = !rp2350 ? uint.MaxValue
                        : target < 3 ? ~NonSecureInterruptMask(group) : NonSecureInterruptMask(group);
                    state |= (MaskedInterrupts(target % 3, group) & mask) != 0;
                }
                if(state != interruptState[target])
                {
                    interruptState[target] = state;
                    notifications.Enqueue(new Notification(NotificationKind.Interrupt, target, state));
                }
            }
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

        private readonly struct Notification
        {
            public Notification(NotificationKind kind, int pin, bool value, bool enabled = false)
            {
                Kind = kind;
                Pin = pin;
                Value = value;
                Enabled = enabled;
            }

            public readonly NotificationKind Kind;
            public readonly int Pin;
            public readonly bool Value;
            public readonly bool Enabled;
        }

        private enum NotificationKind
        {
            Input,
            Wire,
            Drive,
            Interrupt,
        }

        private bool resetting;
        private bool publishing;
        private readonly Queue<Notification> notifications = new Queue<Notification>();
        private readonly bool[] interruptState;
        private readonly GPIO[] interruptOutputs;
        private readonly RP2350_AccessControl accessControl;
        private readonly int securityPinOffset;
        private readonly bool[] isolated;
        private readonly bool[] isolatedOutput;
        private readonly bool[] isolatedEnable;

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
        private readonly bool[] wireLevel;
        private readonly ulong[] peripheralOutput;
        private readonly ulong[] peripheralEnable;
        private readonly uint[] edgeInterrupts;
        private readonly uint[,] interruptEnable;
        private readonly uint[,] interruptForce;
    }
}
