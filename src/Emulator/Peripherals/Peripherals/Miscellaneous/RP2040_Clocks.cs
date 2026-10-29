//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2040_Clocks : RP2APBPeripheral
    {
        public RP2040_Clocks(IMachine machine, RP2_XOSC xosc, RP2040_ROSC rosc,
            RP2_PLL pllSys, RP2_PLL pllUsb, NVIC nvic0 = null, NVIC nvic1 = null, RP2040_SIO sio = null) : base(machine)
        {
            this.xosc = xosc;
            this.rosc = rosc;
            this.pllSys = pllSys;
            this.pllUsb = pllUsb;
            this.nvic0 = nvic0;
            this.nvic1 = nvic1;
            this.sio = sio;
            control = new uint[NumberOfClocks];
            divider = new uint[NumberOfClocks];
            xosc.ClockChanged += UpdateClocks;
            rosc.ClockChanged += UpdateClocks;
            pllSys.ClockChanged += UpdateClocks;
            pllUsb.ClockChanged += UpdateClocks;
            Reset();
        }

        public override void Reset()
        {
            Array.Clear(control, 0, control.Length);
            for(var i = 0; i < divider.Length; i++)
            {
                divider[i] = i == PeripheralClock ? 0u : 0x100;
            }
            control[SystemClock] = 0;
            selectedReferenceSource = 0;
            selectedSystemSource = 0;
            resuscitationControl = 0xff;
            UpdateClocks();
        }

        public ulong GetFrequency(int clock)
        {
            if(clock < 0 || clock >= NumberOfClocks)
            {
                return 0;
            }
            if(clock != ReferenceClock && clock != SystemClock && (control[clock] & 0xc00) != 0x800)
            {
                return 0;
            }
            var source = clock == ReferenceClock ? ReferenceSourceFrequency(selectedReferenceSource)
                : clock == SystemClock ? SystemSourceFrequency(selectedSystemSource)
                : AuxiliarySourceFrequency(clock, (control[clock] >> 5) & 0xf);
            if(clock == PeripheralClock)
            {
                return source;
            }
            ulong division = divider[clock];
            if((division >> 8) == 0)
            {
                division |= 1UL << 24;
            }
            return source * 0x100 / division;
        }

        public event Action ClockChanged;

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0 && offset < NumberOfClocks * 12)
            {
                var index = (int)(offset / 12);
                switch(offset % 12)
                {
                case 0:
                    return control[index];
                case 4:
                    return divider[index];
                case 8:
                    return index == ReferenceClock ? 1u << (int)selectedReferenceSource
                        : index == SystemClock ? 1u << (int)selectedSystemSource : 1;
                }
            }
            switch(offset)
            {
            case 0x78:
                return resuscitationControl;
            case 0x7c:
                return 0;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset >= 0 && offset < NumberOfClocks * 12)
            {
                var index = (int)(offset / 12);
                switch(offset % 12)
                {
                case 0:
                    control[index] = ApplyOperation(control[index], value, operation, ControlMasks[index] & ~(1u << 28));
                    UpdateClocks();
                    return;
                case 4:
                    divider[index] = ApplyOperation(divider[index], value, operation, DividerMasks[index]);
                    UpdateClocks();
                    return;
                case 8:
                    return;
                }
            }
            if(offset == 0x78)
            {
                resuscitationControl = ApplyOperation(resuscitationControl, value, operation, 0x111ff);
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        private void UpdateClocks()
        {
            var requestedReference = control[ReferenceClock] & 3;
            if(ReferenceSourceFrequency(requestedReference) != 0)
            {
                selectedReferenceSource = requestedReference;
            }
            var requestedSystem = control[SystemClock] & 1;
            if(SystemSourceFrequency(requestedSystem) != 0)
            {
                selectedSystemSource = requestedSystem;
            }
            var systemFrequency = GetFrequency(SystemClock);
            if(sio != null && systemFrequency != 0)
            {
                sio.Frequency = systemFrequency;
            }
            foreach(var nvic in new[] { nvic0, nvic1 })
            {
                if(nvic == null)
                {
                    continue;
                }
                if(systemFrequency != 0)
                {
                    nvic.Frequency = systemFrequency;
                }
                nvic.Clocked = systemFrequency != 0;
            }
            ClockChanged?.Invoke();
        }

        private ulong ReferenceSourceFrequency(uint source)
        {
            switch(source)
            {
            case 0:
                return rosc.Frequency;
            case 1:
                return ((control[ReferenceClock] >> 5) & 3) == 0 ? pllUsb.Frequency : 0;
            case 2:
                return xosc.Frequency;
            default:
                return 0;
            }
        }

        private ulong SystemSourceFrequency(uint source)
        {
            return source == 0 ? GetFrequency(ReferenceClock) : AuxiliarySourceFrequency(SystemClock, (control[SystemClock] >> 5) & 7);
        }

        private ulong AuxiliarySourceFrequency(int clock, uint source)
        {
            if(clock == PeripheralClock)
            {
                switch(source)
                {
                case 0: return GetFrequency(SystemClock);
                case 1: return pllSys.Frequency;
                case 2: return pllUsb.Frequency;
                case 3: return rosc.Frequency;
                case 4: return xosc.Frequency;
                default: return 0;
                }
            }
            if(clock >= 7)
            {
                switch(source)
                {
                case 0: return pllUsb.Frequency;
                case 1: return pllSys.Frequency;
                case 2: return rosc.Frequency;
                case 3: return xosc.Frequency;
                default: return 0;
                }
            }
            switch(source)
            {
            case 0: return pllSys.Frequency;
            case 1: return pllUsb.Frequency;
            case 2: return rosc.Frequency;
            case 3: return xosc.Frequency;
            case 6: return GetFrequency(ReferenceClock);
            case 7: return GetFrequency(SystemClock);
            case 8: return GetFrequency(8);
            case 9: return GetFrequency(9);
            default: return 0;
            }
        }

        private uint selectedReferenceSource;
        private uint selectedSystemSource;
        private uint resuscitationControl;

        private readonly uint[] control;
        private readonly uint[] divider;
        private readonly RP2_XOSC xosc;
        private readonly RP2040_ROSC rosc;
        private readonly RP2_PLL pllSys;
        private readonly RP2_PLL pllUsb;
        private readonly NVIC nvic0;
        private readonly NVIC nvic1;
        private readonly RP2040_SIO sio;

        private static readonly uint[] ControlMasks = { 0x131de0, 0x131de0, 0x131de0, 0x131de0, 0x63, 0xe1, 0xce0, 0x130ce0, 0x130ce0, 0x130ce0 };
        private static readonly uint[] DividerMasks = { 0xffffffff, 0xffffffff, 0xffffffff, 0xffffffff, 0x300, 0xffffffff, 0, 0x300, 0x300, 0xffffffff };

        private const int ReferenceClock = 4;
        private const int SystemClock = 5;
        private const int PeripheralClock = 6;
        private const int NumberOfClocks = 10;
    }
}
