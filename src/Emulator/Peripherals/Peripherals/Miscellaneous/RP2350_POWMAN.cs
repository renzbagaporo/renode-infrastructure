//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_POWMAN : RP2APBPeripheral, IGPIOReceiver
    {
        public RP2350_POWMAN(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1,
            RP2350_DebugPort debugPort, RP2350_BootRAM bootRAM = null, RP2350_XIP xip = null,
            MappedMemory sramLow = null, MappedMemory sramHigh = null,
            RP2350_ArchitectureSelector architectureSelector = null, RP2_XOSC xosc = null, RP2350_ROSC rosc = null,
            RP2_GPIOBank0 gpio = null) : base(machine)
        {
            this.gpio = gpio;
            if(gpio != null) gpio.InputChanged += OnWakeInput;
            this.xosc = xosc;
            this.rosc = rosc;
            alarmTimer = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, "aon alarm",
                limit: 1, direction: Direction.Ascending, workMode: WorkMode.OneShot, eventEnabled: true);
            alarmTimer.LimitReached += OnAlarmTime;
            cores = new[] { core0, core1 };
            nvics = new[] { nvic0, nvic1 };
            this.debugPort = debugPort;
            this.architectureSelector = architectureSelector ?? debugPort.ArchitectureSelector;
            if(architectureSelector != null) debugPort.ArchitectureSelector = architectureSelector;
            this.bootRAM = bootRAM;
            this.xip = xip;
            this.sramLow = sramLow;
            this.sramHigh = sramHigh;
            debugPort.RescueResetChanged += OnRescueReset;
            debugPort.RunResetRequested += OnRunReset;
            Reset();
        }

        public override void Reset()
        {
            Interlocked.Increment(ref resetGeneration);
            badPassword = false;
            chipReset = 1u << 16;
            bootDisable = 0;
            debugPowerConfiguration = 0;
            debugConfiguration = 0;
            watchdogSelect = 0;
            powerState = timerControl = 0;
            alarmTimer?.Reset();
            timerBase = 0;
            timerStart = 0;
            alarmTime = 0;
            timerSource = TimerSource.LowPowerOscillator;
            interruptRaw = interruptEnable = interruptForce = 0;
            sequencerConfiguration = 0x001000f0;
            tickFrequency = new uint[] { 0x20, 0xc49c, 0x2ee0, 0 };
            lastPowerUp = 0;
            UpdateInterrupt();
            Array.Clear(powerUp, 0, powerUp.Length);
            Array.Clear(scratch, 0, scratch.Length);
            bootRAM?.SetPowerPresent(true);
            xip?.SetPowerPresent(true);
            machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20000000, 0x40000), false);
            machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20040000, 0x42000), false);
            var generation = Volatile.Read(ref resetGeneration);
            machine.LocalTimeSource?.ExecuteInNearestSyncedState(_ =>
            {
                if(generation == Volatile.Read(ref resetGeneration)) ApplyRescueHold();
            });
        }

        public void OnGPIO(int number, bool value)
        {
            if(!value || number < 0 || number >= cores.Length) return;
            // AIRCR SYSRESETREQ is an individual warm core reset on RP2350
            // (datasheet AIRCR bit 2). SW-DP and the sibling core keep running.
            var generation = Volatile.Read(ref resetGeneration);
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                if(generation != Volatile.Read(ref resetGeneration) || debugPort.RescueResetAsserted) return;
                var processors = Enumerable.Range(0, 2).Select(GetProcessor).ToArray();
                var paused = processors.Select(core => core.IsPaused && !machine.IsPaused).ToArray();
                var attached = processors.Select(core => core.TimeHandle.Enabled).ToArray();
                var core = processors[number];
                var target = architectureSelector != null ? (BaseCPU)architectureSelector.GetResetTarget(core) : core;
                var halted = core.IsHalted || target.IsHalted;
                var resume = !core.IsHalted && !(core is CortexM arm && arm.CpuWaitSignal.IsSet) && !core.HaltedByDebugger
                    && (!core.IsPaused || machine.IsPaused);
                using(machine.ObtainPausedState(true))
                {
                    architectureSelector?.HoldInReset(new[] {(ICPU)core});
                    if(architectureSelector != null) architectureSelector.ResetProcessor(number);
                    else
                    {
                        nvics[number].Reset();
                        cores[number].Reset();
                    }
                    if(halted) target.IsHalted = true;
                    if(resume || target.HasAnyHaltingCondition) target.Resume();
                }
                for(var physical = 0; physical < processors.Length; physical++)
                {
                    if(!paused[physical]) continue;
                    var pausedTarget = physical == number ? target : processors[physical];
                    pausedTarget.Pause();
                    pausedTarget.TimeHandle.Enabled = attached[physical];
                    pausedTarget.TimeHandle.DeferredEnabled = attached[physical];
                }
                architectureSelector?.ReleaseReset();
                WarmReset?.Invoke(number);
            });
        }

        private BaseCPU GetProcessor(int core) => architectureSelector?.ActiveProcessor(core) ?? cores[core];

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
            case 0x34: return sequencerConfiguration;
            case 0x50: case 0x54: case 0x58: case 0x5c: return tickFrequency[(offset - 0x50) / 4];
            case 0x60: return (uint)(timerBase >> 48) & 0xffff;
            case 0x64: return (uint)(timerBase >> 32) & 0xffff;
            case 0x68: return (uint)(timerBase >> 16) & 0xffff;
            case 0x6c: return (uint)timerBase & 0xffff;
            case 0x70: return (uint)(CurrentTime >> 32);
            case 0x74: return (uint)CurrentTime;
            case 0x78: return (uint)(alarmTime >> 48) & 0xffff;
            case 0x7c: return (uint)(alarmTime >> 32) & 0xffff;
            case 0x80: return (uint)(alarmTime >> 16) & 0xffff;
            case 0x84: return (uint)alarmTime & 0xffff;
            case 0x88:
                return timerControl | (TimerRunning && timerSource == TimerSource.Crystal ? UsingXosc : 0)
                    | (TimerRunning && timerSource == TimerSource.LowPowerOscillator ? UsingLposc : 0);
            case 0xa0: return lastPowerUp;
            case 0xe0: return interruptRaw;
            case 0xe4: return interruptEnable;
            case 0xe8: return interruptForce;
            case 0xec: return (interruptRaw | interruptForce) & interruptEnable;
            case 0x8c: case 0x90: case 0x94: case 0x98: return PowerUpSourceState((int)(offset - 0x8c) / 4);
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
            switch(offset)
            {
            case 0xe0: interruptRaw &= ~(ApplyOperation(0, value, operation) & ~TimerInterrupt); UpdateInterrupt(); return;
            case 0xe4: interruptEnable = ApplyOperation(interruptEnable, value, operation, 0xf); UpdateInterrupt(); return;
            case 0xe8: interruptForce = ApplyOperation(interruptForce, value, operation, 0xf); UpdateInterrupt(); return;
            case 0xec: return;
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
                    // Powering the switched core off waits (STATE.WAITING) until
                    // both processors sleep; any other change starts at once.
                    var generation = Volatile.Read(ref resetGeneration);
                    if((request & SwitchedCoreOff) != 0 && (current & SwitchedCoreOff) == 0)
                    {
                        powerState |= StateWaiting;
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => EnterPowerStateWhenAsleep(request, generation));
                    }
                    else
                    {
                        powerState |= StateChanging;
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
                        {
                            if(generation == Volatile.Read(ref resetGeneration)) EnterPowerState(request);
                        });
                    }
                }
                break;
            case 0x34:
                sequencerConfiguration = ApplyOperation(sequencerConfiguration, value, operation, 0x003000f3);
                break;
            case 0x50: case 0x54: case 0x58: case 0x5c:
                // LPOSC/XOSC_FREQ_KHZ: the 1 kHz tick divisors. The timer
                // counts virtual milliseconds, so they are stored only.
                var tick = (offset - 0x50) / 4;
                tickFrequency[tick] = ApplyOperation(tickFrequency[tick], value, operation, tick == 0 ? 0x3fu : 0xffffu);
                break;
            case 0x60: case 0x64: case 0x68: case 0x6c:
                // SET_TIME is written while the timer is stopped (16 bits each).
                var setShift = (int)(0x6c - offset) * 4;
                var setPart = ((ulong)ApplyOperation((uint)(timerBase >> setShift) & 0xffff, value, operation, 0xffff)) << setShift;
                timerBase = (timerBase & ~(0xffffUL << setShift)) | setPart;
                ScheduleAlarm();
                break;
            case 0x78: case 0x7c: case 0x80: case 0x84:
                var alarmShift = (int)(0x84 - offset) * 4;
                var alarmPart = ((ulong)ApplyOperation((uint)(alarmTime >> alarmShift) & 0xffff, value, operation, 0xffff)) << alarmShift;
                alarmTime = (alarmTime & ~(0xffffUL << alarmShift)) | alarmPart;
                ScheduleAlarm();
                break;
            case 0x88:
                WriteTimer(ApplyOperation(timerControl, value, operation, 0x7ff), value, operation);
                break;
            case 0x8c: case 0x90: case 0x94: case 0x98:
                var powerIndex = (offset - 0x8c) / 4;
                // STATUS (bit 9) latches a wake event; RAW_STATUS (bit 10) is the live match.
                powerUp[powerIndex] = ApplyOperation(powerUp[powerIndex], value, operation, 0x3ff);
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

        public GPIO TimerIRQ { get; } = new GPIO();

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state) && state.DebugAccess) return;
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        // AON timer (datasheet 6.5.2): a 64-bit millisecond counter on a 1 kHz
        // tick from LPOSC or XOSC, modelled from virtual time. Its alarm raises
        // TIMER.ALARM and INTR.TIMER, wakes a dormant XOSC or ROSC, and with
        // PWRUP_ON_ALARM powers the switched core back up.
        private ulong CurrentTime => TimerRunning ? timerBase + (NowTicks - timerStart) / TicksPerMillisecond : timerBase;
        private bool TimerRunning => (timerControl & TimerRun) != 0;
        private ulong NowTicks => machine.ClockSource.CurrentValue.Ticks;

        private void WriteTimer(uint next, uint value, AtomicOperation operation)
        {
            var now = CurrentTime;
            var strobes = operation == AtomicOperation.Clear ? 0 : value;
            // ALARM is write-1-to-clear through any alias; CLEAR, USE_LPOSC and
            // USE_XOSC are self-clearing strobes.
            var alarm = (timerControl & TimerAlarm) != 0 && (value & TimerAlarm) == 0;
            timerControl = (next & 0x33) | (alarm ? TimerAlarm : 0);
            if((strobes & TimerClearStrobe) != 0) now = 0;
            if((strobes & UseLposc) != 0) timerSource = TimerSource.LowPowerOscillator;
            if((strobes & UseXosc) != 0) timerSource = TimerSource.Crystal;
            timerBase = now;
            timerStart = NowTicks;
            if(!alarm) interruptRaw &= ~TimerInterrupt;
            ScheduleAlarm();
            UpdateInterrupt();
        }

        private void ScheduleAlarm()
        {
            alarmTimer.Enabled = false;
            if(!TimerRunning || (timerControl & TimerAlarmEnable) == 0) return;
            var now = CurrentTime;
            if(alarmTime <= now)
            {
                RaiseAlarm();
                return;
            }
            // The next tick boundary at or after the alarm time.
            var dueTicks = timerStart + (alarmTime - timerBase) * TicksPerMillisecond;
            var nowTicks = NowTicks;
            alarmTimer.Restart(dueTicks > nowTicks ? dueTicks - nowTicks : 1);
        }

        private void OnAlarmTime()
        {
            lock(SyncRoot)
            {
                if(TimerRunning && (timerControl & TimerAlarmEnable) != 0 && CurrentTime >= alarmTime) RaiseAlarm();
                else ScheduleAlarm();
            }
        }

        private void RaiseAlarm()
        {
            alarmTimer.Enabled = false;
            timerControl |= TimerAlarm;
            interruptRaw |= TimerInterrupt;
            UpdateInterrupt();
            // Wake a dormant oscillator: the alarm is a dormant wake source.
            if(xosc != null && xosc.ReadDoubleWord(0x8) == DormantValue) xosc.WriteDoubleWord(0x8, WakeValue);
            if(rosc != null && rosc.ReadDoubleWord(0x10) == DormantValue) rosc.WriteDoubleWord(0x10, WakeValue);
            if((timerControl & TimerPowerUpOnAlarm) != 0 && (powerState & SwitchedCoreOff) != 0) PowerUpSwitchedCore(1u << 5);
        }

        // The power sequencer brings the switched core up from a low-power state
        // (P1.x): SRAM domains as SEQ_CFG.HW_PWRUP_SRAMx says, then the
        // switched core comes out of reset and boots with CHIP_RESET.HAD_SWCORE_PD.
        // PWRUPn: GPIO SOURCE (5:0), ENABLE (6), DIRECTION high/rising (7),
        // MODE edge (8). A matching input latches STATUS and, with the switched
        // core off, powers it up as power-up request n.
        private void OnWakeInput(int pin, bool level)
        {
            var wake = -1;
            lock(SyncRoot)
            {
                for(var i = 0; i < powerUp.Length; i++)
                {
                    var source = powerUp[i];
                    if((source & 0x40) == 0 || (source & 0x3f) != pin || level != ((source & 0x80) != 0)) continue;
                    powerUp[i] |= 0x200;
                    if(wake < 0 && (powerState & SwitchedCoreOff) != 0) wake = i;
                }
            }
            if(wake >= 0) PowerUpSwitchedCore(1u << wake);
        }

        private uint PowerUpSourceState(int index)
        {
            var source = powerUp[index];
            var raw = gpio != null && (source & 0x40) != 0 && gpio.GetInput((int)(source & 0x3f)) == ((source & 0x80) != 0);
            return source | (raw ? 0x400u : 0);
        }

        // A level source already active, or an edge already latched, when the
        // switched core powers off brings it straight back up.
        private void WakeIfPowerUpPending()
        {
            for(var i = 0; i < powerUp.Length; i++)
            {
                var source = powerUp[i];
                if((source & 0x40) == 0) continue;
                var edge = (source & 0x100) != 0;
                if((edge && (source & 0x200) != 0) || (!edge && (PowerUpSourceState(i) & 0x400) != 0))
                {
                    PowerUpSwitchedCore(1u << i);
                    return;
                }
            }
        }

        private void PowerUpSwitchedCore(uint reason)
        {
            lastPowerUp = reason;
            var retained = new List<IPeripheral> { this, debugPort };
            retained.AddRange(machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral).OfType<WinbondW25QFlash>());
            var sleepState = powerState & 0xf;
            machine.RequestResetInSafeState(postReset: ApplyRescueHold, unresetable: retained, runRegisteredResetEvents: true, preReset: () =>
            {
                var wake = sleepState & ~SwitchedCoreOff;
                // HW_PWRUP_SRAMx clear means OFF -> ON across the wakeup.
                if((sequencerConfiguration & 2) == 0) wake &= ~2u;
                if((sequencerConfiguration & 1) == 0) wake &= ~1u;
                powerState = (wake << 4) | wake;
                chipReset = (chipReset & 1) | HadSwitchedCorePowerDown;
                bootRAM?.SetPowerPresent(true);
                xip?.SetPowerPresent(true);
                machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20000000, 0x40000), (wake & 2) != 0);
                machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20040000, 0x42000), (wake & 1) != 0);
                foreach(var core in cores) core.CpuWaitSignal.Set(false);
                ApplyRescueHold();
            });
        }

        private void EnterPowerStateWhenAsleep(uint request, int generation)
        {
            if(generation != Volatile.Read(ref resetGeneration) || ((powerState >> 4) & 0xf) != request) return;
            if(!Enumerable.Range(0, 2).All(IsAsleep))
            {
                machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => EnterPowerStateWhenAsleep(request, generation));
                return;
            }
            powerState &= ~StateWaiting;
            EnterPowerState(request);
        }

        // WFI and WFE both count: the bootrom parks core 1 in WFE.
        private bool IsAsleep(int number)
        {
            var core = GetProcessor(number);
            if(core.IsHalted || core.HasAnyHaltingCondition) return true;
            var nvic = core is CortexM ? nvics[number] : null;
            return nvic != null && (nvic.InSleep.IsSet || nvic.InDeepSleep.IsSet);
        }

        private void EnterPowerState(uint request)
        {
            var previous = powerState & 0xf;
            powerState = (request << 4) | request;
            if((request & ~previous & 2) != 0) sramLow?.ZeroAll();
            if((request & ~previous & 1) != 0) sramHigh?.ZeroAll();
            bootRAM?.SetPowerPresent((request & 4) == 0);
            xip?.SetPowerPresent((request & 4) == 0);
            machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20000000, 0x40000), (request & 2) != 0);
            machine.SystemBus.SetAddressRangeLocked(new Antmicro.Renode.Core.Range(0x20040000, 0x42000), (request & 1) != 0);
            foreach(var core in cores) core.CpuWaitSignal.Set((request & SwitchedCoreOff) != 0);
            if((request & ~previous & SwitchedCoreOff) != 0) WakeIfPowerUpPending();
        }

        private void UpdateInterrupt() => TimerIRQ.Set(((interruptRaw | interruptForce) & interruptEnable & TimerInterrupt) != 0);

        private void ApplyRescueHold()
        {
            debugPort.ApplyRescueHold();
        }

        private void OnRescueReset(bool asserted)
        {
            if(!asserted)
            {
                var generation = Volatile.Read(ref resetGeneration);
                machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
                {
                    if(generation == Volatile.Read(ref resetGeneration)) ApplyRescueHold();
                });
                return;
            }
            // RP-AP rescue resets POWMAN and the switched domain. It retains
            // the always-on DP/RP-AP and externally powered serial flash.
            var retained = new List<IPeripheral> { this, debugPort };
            retained.AddRange(machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral).OfType<WinbondW25QFlash>());
            machine.RequestResetInSafeState(postReset: ApplyRescueHold, unresetable: retained, runRegisteredResetEvents: false, preReset: () =>
            {
                ApplyRescueHold();
                var retainedScratch = (uint[])scratch.Clone();
                var retainedDoubleTap = chipReset & 1;
                Reset();
                Array.Copy(retainedScratch, scratch, scratch.Length);
                chipReset = (1u << 21) | 0x10 | retainedDoubleTap;
                ApplyRescueHold();
            });
        }

        private void OnRunReset()
        {
            var retained = machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral)
                .Where(peripheral => peripheral is WinbondW25QFlash).ToList();
            retained.Add(this);
            // RP2350 datasheet table 560: RUN hard-resets POWMAN and the debug
            // port, but preserves DOUBLE_TAP. Ordinary SRAM loses power. Zero
            // is the model's deterministic policy for undefined power-up data.
            machine.RequestResetInSafeState(postReset: () =>
            {
                debugPort.ApplyRunHold();
                ApplyRescueHold();
            }, unresetable: retained, runRegisteredResetEvents: true, preReset: () =>
            {
                debugPort.ApplyRunHold();
                ApplyRescueHold();
                var doubleTap = chipReset & 1;
                Reset();
                chipReset = (1u << 18) | doubleTap;
                sramLow?.ZeroAll();
                sramHigh?.ZeroAll();
            });
        }

        private readonly RP2350_BootRAM bootRAM;
        private readonly RP2350_XIP xip;
        private readonly MappedMemory sramLow;
        private readonly MappedMemory sramHigh;
        private int resetGeneration;
        private readonly CortexM[] cores;
        private readonly NVIC[] nvics;
        private readonly RP2350_DebugPort debugPort;
        private readonly RP2350_ArchitectureSelector architectureSelector;
        private readonly uint[] scratch = new uint[12];
        private readonly uint[] powerUp = new uint[4];
        private readonly RP2_GPIOBank0 gpio;
        private uint powerState;
        private uint[] tickFrequency;
        private uint timerControl;
        private ulong timerBase;
        private ulong timerStart;
        private ulong alarmTime;
        private TimerSource timerSource;
        private uint interruptRaw;
        private uint interruptEnable;
        private uint interruptForce;
        private uint sequencerConfiguration;
        private uint lastPowerUp;
        private readonly LimitTimer alarmTimer;
        private readonly RP2_XOSC xosc;
        private readonly RP2350_ROSC rosc;

        private static readonly ulong TicksPerMillisecond = TimeInterval.TicksPerSecond / 1000;
        private const uint TimerRun = 1u << 1;
        private const uint TimerClearStrobe = 1u << 2;
        private const uint TimerAlarmEnable = 1u << 4;
        private const uint TimerPowerUpOnAlarm = 1u << 5;
        private const uint TimerAlarm = 1u << 6;
        private const uint UseLposc = 1u << 8;
        private const uint UseXosc = 1u << 9;
        private const uint UsingXosc = 1u << 16;
        private const uint UsingLposc = 1u << 17;
        private const uint TimerInterrupt = 1u << 1;
        private const uint SwitchedCoreOff = 1u << 3;
        private const uint HadSwitchedCorePowerDown = 1u << 25;
        private const uint StateWaiting = 1u << 12;
        private const uint StateChanging = 1u << 13;
        private const uint DormantValue = 0x636f6d61;
        private const uint WakeValue = 0x77616b65;

        private enum TimerSource { LowPowerOscillator, Crystal }
        private uint chipReset;
        private uint bootDisable;
        private uint watchdogSelect;
        private uint debugPowerConfiguration;
        private uint debugConfiguration;
        private bool badPassword;
    }
}
