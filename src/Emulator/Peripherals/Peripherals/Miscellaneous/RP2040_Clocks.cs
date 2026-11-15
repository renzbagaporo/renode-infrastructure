//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Time;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2040_Clocks : RP2APBPeripheral, IRP2ClockSource
    {
        public RP2040_Clocks(IMachine machine, RP2_XOSC xosc, RP2040_ROSC rosc,
            RP2_PLL pllSys, RP2_PLL pllUsb, NVIC nvic0 = null, NVIC nvic1 = null, RP2040_SIO sio = null,
            RP2_GPIOBank0 gpio = null) : base(machine)
        {
            for(var i = 0; i < 2; i++)
            {
                var index = i;
                gpinTimeout[i] = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, $"gpin{i} timeout",
                    limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
                gpinTimeout[i].LimitReached += () => SetMeasuredGpin(index, 0);
            }
            if(gpio != null) gpio.InputChanged += OnGpinInput;
            this.xosc = xosc;
            this.rosc = rosc;
            this.pllSys = pllSys;
            this.pllUsb = pllUsb;
            this.nvic0 = nvic0;
            this.nvic1 = nvic1;
            this.sio = sio;
            control = new uint[NumberOfClocks];
            divider = new uint[NumberOfClocks];
            monitor = new RP2_ClockMonitor(IRQ, CounterSourceFrequency);
            xosc.ClockChanged += UpdateClocks;
            rosc.ClockChanged += UpdateClocks;
            pllSys.ClockChanged += UpdateClocks;
            pllUsb.ClockChanged += UpdateClocks;
            Reset();
        }

        public override void Reset() => ResetClocks(false);

        public void ResetFromWatchdog() => ResetClocks(true);

        private void ResetClocks(bool retainRunDividers)
        {
            var referenceDivider = divider[ReferenceClock];
            var systemDivider = divider[SystemClock];
            Array.Clear(control, 0, control.Length);
            for(var i = 0; i < divider.Length; i++)
            {
                divider[i] = i == PeripheralClock ? 0u : 0x100;
            }
            // The clk_ref/clk_sys dividers use rst_n_run, not the PSM CLOCKS
            // reset, so that their outputs keep running through a soft restart.
            if(retainRunDividers)
            {
                divider[ReferenceClock] = referenceDivider;
                divider[SystemClock] = systemDivider;
            }
            control[SystemClock] = 0;
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

        // CLKSRC_GPIN0 and GPIN1: the external clock driven into GPIO20 and
        // GPIO22. Its frequency is measured from the period between rising
        // edges at the pin and drops to 0 after four periods without one; with
        // no edges, the value set here stands in for a clock source.
        public ulong Gpin0Frequency
        {
            get => measuredGpin[0] != 0 ? measuredGpin[0] : gpin0Frequency;
            set
            {
                gpin0Frequency = value;
                machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClocks(); });
            }
        }

        public ulong Gpin1Frequency
        {
            get => measuredGpin[1] != 0 ? measuredGpin[1] : gpin1Frequency;
            set
            {
                gpin1Frequency = value;
                machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClocks(); });
            }
        }

        private void OnGpinInput(int pin, bool level)
        {
            var index = pin == 20 ? 0 : pin == 22 ? 1 : -1;
            if(index < 0 || !level) return;
            var now = machine.ClockSource.CurrentValue.Ticks;
            var period = lastGpinRise[index] == 0 ? 0 : now - lastGpinRise[index];
            lastGpinRise[index] = now;
            if(period == 0) return;
            gpinTimeout[index].Restart(4 * period);
            SetMeasuredGpin(index, TimeInterval.TicksPerSecond / period);
        }

        // A change under 0.5% of the measured rate leaves the clocks as they are.
        private void SetMeasuredGpin(int index, ulong frequency)
        {
            var previous = measuredGpin[index];
            if(frequency != 0 && previous != 0 && Math.Abs((double)frequency - previous) < previous * 0.005) return;
            if(frequency == previous) return;
            measuredGpin[index] = frequency;
            if(frequency == 0) lastGpinRise[index] = 0;
            machine.ClockSource.ExecuteInLock(() => { lock(SyncRoot) UpdateClocks(); });
        }

        public ulong SystemFrequency => GetFrequency(SystemClock);

        public ulong PeripheralFrequency => GetFrequency(PeripheralClock);

        public ulong USBFrequency => GetFrequency(7);

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
                    return control[index];
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
                switch((control[ReferenceClock] >> 5) & 3)
                {
                case 0: return pllUsb.Frequency;
                case 1: return Gpin0Frequency;
                case 2: return Gpin1Frequency;
                default: return 0;
                }
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
                case 5: return Gpin0Frequency;
                case 6: return Gpin1Frequency;
                default: return 0;
                }
            }
            if(clock >= 7 || clock == SystemClock)
            {
                // clk_usb, clk_adc and clk_rtc; clk_sys orders its first two
                // sources the other way round.
                var swap = clock == SystemClock && source < 2;
                switch(swap ? 1 - source : source)
                {
                case 0: return pllUsb.Frequency;
                case 1: return pllSys.Frequency;
                case 2: return rosc.Frequency;
                case 3: return xosc.Frequency;
                case 4: return Gpin0Frequency;
                case 5: return Gpin1Frequency;
                default: return 0;
                }
            }
            // clk_gpout0-3.
            switch(source)
            {
            case 0: return pllSys.Frequency;
            case 1: return Gpin0Frequency;
            case 2: return Gpin1Frequency;
            case 3: return pllUsb.Frequency;
            case 4: return rosc.Frequency;
            case 5: return xosc.Frequency;
            case 6: return GetFrequency(SystemClock);
            case 7: return GetFrequency(7);
            case 8: return GetFrequency(8);
            case 9: return GetFrequency(9);
            case 0xa: return GetFrequency(ReferenceClock);
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
            case 0xb: return GetFrequency(7);
            case 0xc: return GetFrequency(8);
            case 0xd: return GetFrequency(9);
            default: return 0;
            }
        }

        private uint selectedReferenceSource;
        private uint selectedSystemSource;
        private readonly RP2_ClockMonitor monitor;

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

        private ulong gpin0Frequency;
        private ulong gpin1Frequency;
        private readonly ulong[] measuredGpin = new ulong[2];
        private readonly ulong[] lastGpinRise = new ulong[2];
        private readonly LimitTimer[] gpinTimeout = new LimitTimer[2];

        private const int ReferenceClock = 4;
        private const int SystemClock = 5;
        private const int PeripheralClock = 6;
        private const int NumberOfClocks = 10;
        private const long MonitorOffset = 0x78;
    }
}
