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
    public sealed class RP2350_Clocks : RP2APBPeripheral, IRP2ClockSource
    {
        public RP2350_Clocks(IMachine machine, RP2_XOSC xosc, RP2350_ROSC rosc,
            RP2_PLL pllSys, RP2_PLL pllUsb, NVIC nvic0 = null, NVIC nvic1 = null) : base(machine)
        {
            this.xosc = xosc;
            this.rosc = rosc;
            this.pllSys = pllSys;
            this.pllUsb = pllUsb;
            this.nvic0 = nvic0;
            this.nvic1 = nvic1;
            control = new uint[NumberOfClocks];
            divider = new uint[NumberOfClocks];
            monitor = new RP2_ClockMonitor(IRQ, CounterSourceFrequency);
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
                divider[i] = 0x10000;
            }
            control[SystemClock] = 0x41;
            selectedReferenceSource = 0;
            selectedSystemSource = 0;
            monitor.Reset();
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
                : clock == SystemClock ? (monitor.Resussed ? GetFrequency(ReferenceClock) : SystemSourceFrequency(selectedSystemSource))
                : AuxiliarySourceFrequency(clock, (control[clock] >> 5) & 0xf);
            ulong division = divider[clock];
            if(division == 0)
            {
                division = (ulong)DividerMasks[clock] + 0x10000;
                if(clock < 4 || clock == SystemClock)
                {
                    division = 1UL << 32;
                }
            }
            return source * 0x10000 / division;
        }

        public ulong SystemFrequency => GetFrequency(SystemClock);

        public ulong PeripheralFrequency => GetFrequency(PeripheralClock);

        public ulong USBFrequency => GetFrequency(8);

        public event Action ClockChanged;

        public GPIO IRQ { get; } = new GPIO();

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0 && offset < NumberOfClocks * 12)
            {
                var index = (int)(offset / 12);
                switch(offset % 12)
                {
                case 0:
                    return control[index] | (index != ReferenceClock && index != SystemClock && GetFrequency(index) != 0 ? 1u << 28 : 0);
                case 4:
                    return divider[index];
                case 8:
                    return index == ReferenceClock ? 1u << (int)selectedReferenceSource
                        : index == SystemClock ? 1u << (int)(monitor.Resussed ? 0 : selectedSystemSource) : 1;
                }
            }
            if(monitor.TryRead(offset - MonitorOffset, out var value))
            {
                return value;
            }
            this.LogUnhandledRead(offset);
            return 0;
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
            if(monitor.TryRead(offset - MonitorOffset, out var current)
                && monitor.TryWrite(offset - MonitorOffset, ApplyOperation(current, value, operation), out var clocksChanged))
            {
                if(clocksChanged) UpdateClocks();
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
            // A stopped clk_sys source is resuscitated onto clk_ref when enabled.
            monitor.CheckResus(GetFrequency(SystemClock), GetFrequency(ReferenceClock));
            var systemFrequency = GetFrequency(SystemClock);
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
            GateCores(systemFrequency != 0);
            RP2CpuTime.SetCoreClock(machine, systemFrequency);
            ClockChanged?.Invoke();
        }

        // Every core stops while clk_sys is stopped. The core that stopped it
        // halts after its current instruction; the other cores at their next
        // synchronization point, which keeps this core's locks out of theirs.
        private void GateCores(bool clocked)
        {
            machine.SystemBus.TryGetCurrentCPU(out var current);
            foreach(var cpu in machine.SystemBus.GetCPUs())
            {
                if(cpu == current)
                {
                    cpu.Clocked = clocked;
                }
                else if(cpu.Clocked != clocked)
                {
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => cpu.Clocked = GetFrequency(SystemClock) != 0);
                }
            }
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
            case 3:
                return 32768;
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
            if(clock == 7)
            {
                switch(source)
                {
                case 0: return GetFrequency(SystemClock);
                case 1: return pllSys.Frequency;
                case 2: return pllUsb.Frequency;
                default: return 0;
                }
            }
            if(clock == 8 || clock == 9)
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

        // FC0_SRC selections.
        private ulong CounterSourceFrequency(uint source)
        {
            switch(source)
            {
            case 1: return pllSys.Frequency;
            case 2: return pllUsb.Frequency;
            case 3: case 4: return rosc.Frequency;
            case 5: return xosc.Frequency;
            case 8: return GetFrequency(ReferenceClock);
            case 9: return GetFrequency(SystemClock);
            case 0xa: return GetFrequency(PeripheralClock);
            case 0xb: return GetFrequency(8);
            case 0xc: return GetFrequency(9);
            case 0xd: return GetFrequency(7);
            case 0xe: return 32768;
            default: return 0;
            }
        }

        private uint selectedReferenceSource;
        private uint selectedSystemSource;
        private readonly RP2_ClockMonitor monitor;

        private readonly uint[] control;
        private readonly uint[] divider;
        private readonly RP2_XOSC xosc;
        private readonly RP2350_ROSC rosc;
        private readonly RP2_PLL pllSys;
        private readonly RP2_PLL pllUsb;
        private readonly NVIC nvic0;
        private readonly NVIC nvic1;

        private static readonly uint[] ControlMasks = { 0x10131de0, 0x10131de0, 0x10131de0, 0x10131de0, 0x63, 0xe1, 0x10000ce0, 0x10130ce0, 0x10130ce0, 0x10130ce0 };
        private static readonly uint[] DividerMasks = { 0xffffffff, 0xffffffff, 0xffffffff, 0xffffffff, 0xff0000, 0xffffffff, 0x30000, 0x30000, 0xf0000, 0xf0000 };

        private const int ReferenceClock = 4;
        private const int SystemClock = 5;
        private const int PeripheralClock = 6;
        private const int NumberOfClocks = 10;
        private const long MonitorOffset = 0x84;
    }
}
