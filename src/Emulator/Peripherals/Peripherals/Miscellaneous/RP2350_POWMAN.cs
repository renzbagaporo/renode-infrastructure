//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_POWMAN : RP2APBPeripheral, IGPIOReceiver
    {
        public RP2350_POWMAN(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1,
            RP2350_DebugPort debugPort) : base(machine)
        {
            cores = new[] { core0, core1 };
            nvics = new[] { nvic0, nvic1 };
            this.debugPort = debugPort;
            debugPort.RescueResetChanged += OnRescueReset;
            Reset();
        }

        public override void Reset()
        {
            badPassword = false;
            chipReset = 1u << 16;
            bootDisable = 0;
            debugPowerConfiguration = 0;
            debugConfiguration = 0;
            watchdogSelect = 0;
            rescueHeld = false;
            powerState = timerControl = 0;
            Array.Clear(powerUp, 0, powerUp.Length);
            Array.Clear(scratch, 0, scratch.Length);
        }

        public void OnGPIO(int number, bool value)
        {
            if(!value || number < 0 || number >= cores.Length) return;
            // AIRCR SYSRESETREQ is an individual warm core reset on RP2350
            // (datasheet AIRCR bit 2). SW-DP and the sibling core keep running.
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                nvics[number].Reset();
                cores[number].Reset();
                WarmReset?.Invoke(number);
            });
        }

        public event Action<int> WarmReset;
        public uint WatchdogSelection => watchdogSelect;

        public void ResetFromWatchdog(RP2350_Watchdog watchdog)
        {
            var selected = watchdogSelect;
            var cause = (selected & 1) != 0 ? 1u << 22 : (selected & 0x10) != 0 ? 1u << 23
                : (selected & 0x100) != 0 ? 1u << 24 : 1u << 28;
            var retainedFlags = chipReset & 0x11;
            var retainedScratch = (uint[])scratch.Clone();
            if((selected & 0x11) != 0)
            {
                Reset();
                Array.Copy(retainedScratch, scratch, scratch.Length);
            }
            chipReset = cause | retainedFlags;
            if((selected & 0x111) != 0) powerState = 0;
            watchdog.ClearScratchOnPowerReset();
            var retained = new List<IPeripheral> { this, debugPort, watchdog };
            retained.AddRange(machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral).OfType<WinbondW25QFlash>());
            machine.RequestResetInSafeState(unresetable: retained);
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0xb0 && offset <= 0xdc && offset % 4 == 0) return scratch[(offset - 0xb0) / 4];
            switch(offset)
            {
            case 0: return badPassword ? 1u : 0;
            case 0x2c: return chipReset;
            case 0x30: return watchdogSelect;
            case 0x38: return powerState;
            case 0x88: return timerControl;
            case 0x8c: case 0x90: case 0x94: case 0x98: return powerUp[(offset - 0x8c) / 4];
            case 0xa4: return debugPowerConfiguration;
            case 0xa8: return bootDisable;
            case 0xac: return debugConfiguration;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset >= 0xb0 && offset <= 0xdc && offset % 4 == 0)
            {
                var index = (offset - 0xb0) / 4;
                scratch[index] = ApplyOperation(scratch[index], value, operation);
                return;
            }
            // Registers through DBGCONFIG require the 16-bit password, including
            // atomic aliases. Scratch and interrupt registers are not protected.
            if(offset <= 0xac && (value >> 16) != 0x5afe)
            {
                badPassword = true;
                return;
            }
            value &= 0xffff;
            switch(offset)
            {
            case 0:
                if((value & 1) != 0) badPassword = false;
                break;
            case 0x2c:
                if((value & 0x10) != 0) chipReset &= ~0x10u;
                chipReset = ApplyOperation(chipReset, value, operation, 1);
                break;
            case 0x38:
                var request = (ApplyOperation(powerState, value, operation, 0xf0) >> 4) & 0xf;
                var current = powerState & 0xf;
                powerState = (powerState & 0xf) | (request << 4);
                if((request & 4) != 0 && (request & 8) == 0 || ((request & ~current) != 0 && (current & ~request) != 0))
                {
                    powerState |= 0x400;
                    return;
                }
                if(request != current)
                {
                    powerState |= 0x2000;
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
                    {
                        powerState = (request << 4) | request;
                        machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20000000, 0x40000), (request & 2) != 0);
                        machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20040000, 0x42000), (request & 1) != 0);
                        machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x400e0000, 0x4000), (request & 4) != 0);
                        foreach(var core in cores) core.CpuWaitSignal.Set((request & 8) != 0);
                    });
                }
                break;
            case 0x88:
                timerControl = ApplyOperation(timerControl, value, operation, 0x7ff);
                break;
            case 0x8c: case 0x90: case 0x94: case 0x98:
                var powerIndex = (offset - 0x8c) / 4;
                powerUp[powerIndex] = ApplyOperation(powerUp[powerIndex], value, operation, 0x3fff);
                break;
            case 0x30:
                watchdogSelect = ApplyOperation(watchdogSelect, value, operation, 0x1111);
                break;
            case 0xa4:
                debugPowerConfiguration = ApplyOperation(debugPowerConfiguration, value, operation, 1);
                break;
            case 0xa8:
                if((value & 1) != 0) bootDisable &= ~1u;
                bootDisable |= value & 2;
                break;
            case 0xac:
                debugConfiguration = ApplyOperation(debugConfiguration, value, operation, 0xf);
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private void OnRescueReset(bool asserted)
        {
            rescueHeld = asserted;
            if(!asserted)
            {
                foreach(var core in cores) core.CpuWaitSignal.Unset();
                return;
            }
            // A rescue request resets POWMAN and the switched domain but retains
            // the always-on DP/RP-AP and the externally powered serial flash.
            var retainedScratch = (uint[])scratch.Clone();
            var retainedDoubleTap = chipReset & 1;
            Reset();
            Array.Copy(retainedScratch, scratch, scratch.Length);
            rescueHeld = true;
            chipReset = (1u << 21) | 0x10 | retainedDoubleTap;
            var retained = new List<IPeripheral> { this, debugPort };
            retained.AddRange(machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral).OfType<WinbondW25QFlash>());
            foreach(var core in cores) core.CpuWaitSignal.Set();
            machine.RequestResetInSafeState(() =>
            {
                foreach(var core in cores) core.CpuWaitSignal.Set(rescueHeld);
            }, retained);
        }

        private readonly CortexM[] cores;
        private readonly NVIC[] nvics;
        private readonly RP2350_DebugPort debugPort;
        private readonly uint[] scratch = new uint[12];
        private readonly uint[] powerUp = new uint[4];
        private uint powerState;
        private uint timerControl;
        private uint chipReset;
        private uint bootDisable;
        private uint watchdogSelect;
        private uint debugPowerConfiguration;
        private uint debugConfiguration;
        private bool badPassword;
        private bool rescueHeld;
    }
}
