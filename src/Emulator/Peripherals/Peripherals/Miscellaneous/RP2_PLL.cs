//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2_PLL : RP2APBPeripheral
    {
        public RP2_PLL(IMachine machine, RP2_XOSC xosc, bool rp2350 = false) : base(machine)
        {
            this.xosc = xosc;
            this.rp2350 = rp2350;
            xosc.ClockChanged += UpdateClock;
            IRQ = new GPIO();
            Reset();
        }

        public override void Reset()
        {
            control = 1;
            power = 0x2d;
            feedbackDivider = 0;
            postDividers = 0x77000;
            interruptStatus = 0;
            interruptEnable = 0;
            interruptForce = 0;
            locked = false;
            lockLost = false;
            UpdateClock();
        }

        public ulong Frequency
        {
            get
            {
                var referenceDivider = control & 0x3f;
                if(referenceDivider == 0)
                {
                    return 0;
                }
                if((control & (1u << 8)) != 0)
                {
                    return xosc.Frequency / referenceDivider;
                }
                var postDivider1 = (postDividers >> 16) & 7;
                var postDivider2 = (postDividers >> 12) & 7;
                if(!locked || (power & (1u << 3)) != 0 || postDivider1 == 0 || postDivider2 == 0)
                {
                    return 0;
                }
                return xosc.Frequency * feedbackDivider / referenceDivider / postDivider1 / postDivider2;
            }
        }

        public GPIO IRQ { get; }

        public event Action ClockChanged;

        protected override uint ReadRegister(long offset)
        {
            if(!rp2350 && offset > 0xc)
            {
                this.LogUnhandledRead(offset);
                return 0;
            }
            switch(offset)
            {
            case 0x0:
                return control | (locked ? 1u << 31 : 0) | (rp2350 && lockLost ? 1u << 30 : 0);
            case 0x4:
                return power;
            case 0x8:
                return feedbackDivider;
            case 0xc:
                return postDividers;
            case 0x10:
                return interruptStatus;
            case 0x14:
                return interruptEnable;
            case 0x18:
                return interruptForce;
            case 0x1c:
                return (interruptStatus & interruptEnable) | interruptForce;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(!rp2350 && offset > 0xc)
            {
                this.LogUnhandledWrite(offset, value);
                return;
            }
            switch(offset)
            {
            case 0x0:
                control = ApplyOperation(control, value, operation, 0x13f);
                if((value & (1u << 30)) != 0)
                {
                    lockLost = false;
                }
                break;
            case 0x4:
                power = ApplyOperation(power, value, operation, 0x2d);
                break;
            case 0x8:
                feedbackDivider = ApplyOperation(feedbackDivider, value, operation, 0xfff);
                break;
            case 0xc:
                postDividers = ApplyOperation(postDividers, value, operation, 0x77000);
                break;
            case 0x10:
                interruptStatus &= ~(value & 1);
                break;
            case 0x14:
                interruptEnable = ApplyOperation(interruptEnable, value, operation, 1);
                break;
            case 0x18:
                interruptForce = ApplyOperation(interruptForce, value, operation, 1);
                break;
            case 0x1c:
                return;
            default:
                this.LogUnhandledWrite(offset, value);
                return;
            }
            UpdateClock();
        }

        private void UpdateClock()
        {
            var referenceDivider = control & 0x3f;
            var vcoFrequency = referenceDivider == 0 ? 0 : xosc.Frequency * feedbackDivider / referenceDivider;
            var newLocked = (power & 0x21) == 0 && referenceDivider != 0
                && feedbackDivider >= 16 && feedbackDivider <= 320
                && vcoFrequency >= (rp2350 ? 750000000ul : 400000000ul) && vcoFrequency <= 1600000000;
            if(locked && !newLocked)
            {
                interruptStatus |= 1;
                lockLost = true;
            }
            locked = newLocked;
            IRQ.Set(((interruptStatus & interruptEnable) | interruptForce) != 0);
            ClockChanged?.Invoke();
        }

        private uint control;
        private uint power;
        private uint feedbackDivider;
        private uint postDividers;
        private uint interruptStatus;
        private uint interruptEnable;
        private uint interruptForce;
        private bool locked;
        private bool lockLost;

        private readonly RP2_XOSC xosc;
        private readonly bool rp2350;
    }
}
