//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2040_Watchdog : RP2APBPeripheral
    {
        public RP2040_Watchdog(IMachine machine, RP2040_Clocks clocks, RP2_Timer timer = null) : base(machine)
        {
            this.clocks = clocks;
            this.timer = timer;
            scratch = new uint[8];
            tickTimer = new LimitTimer(machine.ClockSource, 1000000, this, "tick", limit: 512);
            watchdog = new LimitTimer(machine.ClockSource, 2000000, this, "watchdog", limit: CounterMask,
                workMode: WorkMode.OneShot, eventEnabled: true);
            watchdog.LimitReached += () => RequestReset(false);
            clocks.ClockChanged += UpdateClock;
            Reset();
        }

        public override void Reset()
        {
            control = 0x07000000;
            tickControl = 0x200;
            load = 0;
            reason = 0;
            Array.Clear(scratch, 0, scratch.Length);
            tickTimer.Reset();
            watchdog.Reset();
            UpdateClock();
        }

        public ulong TickFrequency => (tickControl & 0x200) != 0 ? clocks.GetFrequency(4) / TickCycles : 0;

        public event Action TickChanged;

        public event Action ResetRequested;

        protected override uint ReadRegister(long offset)
        {
            SynchronizeTime();
            if(offset >= 0xc && offset <= 0x28)
            {
                return scratch[(offset - 0xc) / 4];
            }
            switch(offset)
            {
            case 0:
                return control | (load == 0 ? 0 : (uint)watchdog.Value);
            case 4:
                return 0;
            case 8:
                return reason;
            case 0x2c:
                return tickControl | (TickFrequency != 0 ? 0x400u : 0)
                    | (tickTimer.Enabled ? ((uint)tickTimer.Value & 0x1ff) << 11 : 0);
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            SynchronizeTime();
            if(offset >= 0xc && offset <= 0x28)
            {
                var index = (offset - 0xc) / 4;
                scratch[index] = ApplyOperation(scratch[index], value, operation);
                return;
            }
            switch(offset)
            {
            case 0:
                control = ApplyOperation(control, value, operation, 0x47000000);
                if((value & 0x80000000) != 0 && operation != AtomicOperation.Clear)
                {
                    RequestReset(true);
                    return;
                }
                UpdateClock();
                break;
            case 4:
                load = value & CounterMask;
                watchdog.Enabled = false;
                if(load != 0)
                {
                    watchdog.Limit = load;
                    watchdog.ResetValue();
                }
                UpdateClock();
                break;
            case 0x2c:
                tickControl = ApplyOperation(tickControl, value, operation, 0x3ff);
                tickTimer.Enabled = false;
                tickTimer.Limit = TickCycles;
                tickTimer.ResetValue();
                UpdateClock();
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private void UpdateClock()
        {
            var referenceFrequency = clocks.GetFrequency(4);
            tickTimer.Enabled = TickFrequency != 0;
            if(referenceFrequency != 0)
            {
                tickTimer.Frequency = referenceFrequency;
            }
            watchdog.Enabled = TickFrequency != 0 && (control & 0x40000000) != 0 && load != 0;
            if(TickFrequency != 0)
            {
                // RP2040-E1: the watchdog decrements twice for each tick.
                watchdog.Frequency = TickFrequency * 2;
            }
            timer?.SetTickClock(TickFrequency, TickFrequency != 0);
            TickChanged?.Invoke();
        }

        private void SynchronizeTime()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu))
            {
                cpu.SyncTime();
            }
        }

        private void RequestReset(bool forced)
        {
            reason = forced ? 2u : 1;
            watchdog.Enabled = false;
            if(ResetRequested == null)
            {
                this.Log(LogLevel.Error, "Watchdog reset is not connected to the power/reset controller");
                return;
            }
            ResetRequested();
        }

        private uint TickCycles => (tickControl & 0x1ff) == 0 ? 512 : tickControl & 0x1ff;

        private readonly RP2040_Clocks clocks;
        private readonly RP2_Timer timer;
        private readonly LimitTimer tickTimer;
        private readonly LimitTimer watchdog;
        private readonly uint[] scratch;
        private uint control;
        private uint tickControl;
        private uint load;
        private uint reason;

        private const uint CounterMask = 0xffffff;
    }
}
