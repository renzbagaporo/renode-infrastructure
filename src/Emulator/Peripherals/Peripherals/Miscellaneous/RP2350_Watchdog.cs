//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_Watchdog : RP2APBPeripheral
    {
        public RP2350_Watchdog(IMachine machine, RP2350_Ticks ticks, RP2350_PSM psm, RP2350_POWMAN powman = null) : base(machine)
        {
            this.ticks = ticks;
            this.psm = psm;
            this.powman = powman;
            timer = new LimitTimer(machine.ClockSource, 1000000, this, "watchdog", limit: 0xffffff,
                workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += () => Trigger(false);
            ticks.ClockChanged += UpdateClock;
            if(powman != null) powman.WarmReset += _ => reason = 0;
            Reset();
        }
        public override void Reset()
        {
            timer.Reset();
            control = 0x07000000;
            load = reason = 0;
            Array.Clear(scratch, 0, scratch.Length);
            UpdateClock();
        }
        public void ClearScratchOnPowerReset() => Array.Clear(scratch, 0, scratch.Length);
        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state) && state.DebugAccess) return;
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }
        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0xc && offset <= 0x28 && offset % 4 == 0) return scratch[(offset - 0xc) / 4];
            switch(offset)
            {
            case 0: return control | (load == 0 ? 0 : (uint)timer.Value);
            case 4: return 0;
            case 8: return reason;
            default: this.LogUnhandledRead(offset); return 0;
            }
        }
        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset >= 0xc && offset <= 0x28 && offset % 4 == 0)
            {
                var index = (offset - 0xc) / 4;
                scratch[index] = ApplyOperation(scratch[index], value, operation);
                return;
            }
            switch(offset)
            {
            case 0:
                control = ApplyOperation(control, value, operation, 0x47000000);
                if((value & 0x80000000) != 0 && operation != AtomicOperation.Clear) { Trigger(true); return; }
                UpdateClock(); return;
            case 4:
                load = value & 0xffffff;
                timer.Enabled = false;
                timer.Limit = Math.Max(1u, load);
                timer.ResetValue();
                UpdateClock(); return;
            default: this.LogUnhandledWrite(offset, value); return;
            }
        }
        private void UpdateClock()
        {
            var frequency = ticks.GetFrequency(4);
            if(frequency != 0) timer.Frequency = frequency;
            timer.Enabled = frequency != 0 && (control & 0x40000000) != 0 && load != 0;
        }
        private void Trigger(bool forced)
        {
            timer.Enabled = false;
            load = 0;
            reason = forced ? 2u : 1u;
            if(powman != null && powman.WatchdogSelection != 0) powman.ResetFromWatchdog(this);
            else psm.ResetFromWatchdog(this);
        }
        private readonly RP2350_Ticks ticks;
        private readonly RP2350_PSM psm;
        private readonly RP2350_POWMAN powman;
        private readonly LimitTimer timer;
        private readonly uint[] scratch = new uint[8];
        private uint control;
        private uint load;
        private uint reason;
    }
}
