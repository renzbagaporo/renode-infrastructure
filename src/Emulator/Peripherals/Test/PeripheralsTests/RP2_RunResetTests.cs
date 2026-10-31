//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture(false)]
    [TestFixture(true)]
    public class RP2_RunResetTests
    {
        public RP2_RunResetTests(bool rp2350)
        {
            this.rp2350 = rp2350;
        }

        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            EmulationManager.Instance.CurrentEmulation.MasterTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "run_reset");
            machine.LocalTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            nvic0 = new NVIC(machine);
            var nvic1 = new NVIC(machine);
            core0 = new CortexM(rp2350 ? "cortex-m33-rp2350" : "cortex-m0+", machine, nvic0,
                enableTrustZone: rp2350);
            core1 = new CortexM(rp2350 ? "cortex-m33-rp2350" : "cortex-m0+", machine, nvic1,
                cpuId: 1, enableTrustZone: rp2350);
            machine.SystemBus.Register(core0, new CPURegistrationPoint(0));
            machine.SystemBus.Register(core1, new CPURegistrationPoint(1));
            machine.SetLocalName(core0, "cpu0");
            machine.SetLocalName(core1, "cpu1");
            machine.SystemBus.Register(nvic0, new BusPointRegistration(0xe000e000, cpu: core0));
            machine.SystemBus.Register(nvic1, new BusPointRegistration(0xe000e000, cpu: core1));
            nvic0.IRQ.Connect(core0, 0);
            nvic1.IRQ.Connect(core1, 0);
            rom = new MappedMemory(machine, 0x4000);
            machine.SystemBus.Register(rom, new BusPointRegistration(0));
            rom.WriteDoubleWord(0, 0x20001000);
            rom.WriteDoubleWord(4, 0x101);
            rom.WriteWord(0x100, 0x3401); // adds r4, #1
            rom.WriteWord(0x102, 0xe7fd); // branch back to the increment
            lowRAM = new MappedMemory(machine, 0x2000);
            highRAM = new MappedMemory(machine, 0x2000);
            machine.SystemBus.Register(lowRAM, new BusPointRegistration(0x20000000));
            machine.SystemBus.Register(highRAM, new BusPointRegistration(0x20040000));
            var flashMemory = new MappedMemory(machine, 0x200000);
            flash = new WinbondW25QFlash(machine, flashMemory);
            machine.SystemBus.Register(flash, NullRegistrationPoint.Instance);
            probe = new ResetProbe();
            machine.SystemBus.Register(probe, NullRegistrationPoint.Instance);
            if(rp2350)
            {
                debug = new RP2350_DebugPort(machine, core0, core1);
                machine.SystemBus.Register(debug, NullRegistrationPoint.Instance);
                powman = new RP2350_POWMAN(machine, core0, core1, nvic0, nvic1, debug,
                    sramLow: lowRAM, sramHigh: highRAM);
                machine.SystemBus.Register(powman, new BusPointRegistration(0x40100000));
                setRun = value => debug.OnGPIO(2, value);
            }
            else
            {
                var psm = new RP2040_PSM(machine, core0, core1, nvic0, nvic1);
                machine.SystemBus.Register(psm, new BusPointRegistration(0x40010000));
                setRun = value => psm.OnGPIO(0, value);
            }
            machine.PostCreationActions();
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        [Test]
        public void ShouldApplyChipSpecificRetentionWithoutResettingExternalNOR()
        {
            lowRAM.WriteDoubleWord(0x80, 0xfedcba98);
            highRAM.WriteDoubleWord(0x80, 0x13579bdf);
            flash.Transmit(0x06); // An external NOR write-enable latch is independent of RUN.
            flash.FinishTransmission();
            if(rp2350)
            {
                powman.WriteDoubleWord(0x202c, 0x5afe0001);
                powman.WriteDoubleWord(0xb0, 0xa1b2c3d4);
            }
            setRun(false);
            Advance();
            setRun(true);
            Advance();
            Assert.AreEqual(rp2350 ? 0u : 0xfedcba98u, lowRAM.ReadDoubleWord(0x80));
            Assert.AreEqual(rp2350 ? 0u : 0x13579bdfu, highRAM.ReadDoubleWord(0x80));
            flash.Transmit(0x05);
            Assert.AreEqual(2, flash.Transmit(0) & 2, "RUN must not reset the external NOR protocol state");
            flash.FinishTransmission();
            if(rp2350)
            {
                Assert.AreEqual((1u << 18) | 1, powman.ReadDoubleWord(0x2c));
                Assert.AreEqual(0u, powman.ReadDoubleWord(0xb0));
                powman.Reset();
                Assert.AreEqual(1u << 16, powman.ReadDoubleWord(0x2c), "Power-on reset clears DOUBLE_TAP");
            }
        }

        [Test]
        public void ShouldKeepEveryPulseReleasedBeforeTheSafeBoundary()
        {
            var before = probe.Count;
            setRun(false);
            setRun(true);
            setRun(false);
            setRun(true);
            Advance();
            Assert.AreEqual(before + 2, probe.Count);
            Assert.Greater(core0.ExecutedInstructions, 0ul, "Released RUN must not leave a stale hold");
        }

        [Test]
        public void ShouldHoldBothCoresAndRespectTheLatestPhysicalLevel()
        {
            setRun(false);
            Advance();
            var first = core0.ExecutedInstructions;
            var second = core1.ExecutedInstructions;
            core0.IsHalted = core1.IsHalted = false;
            core0.Resume();
            core1.Resume();
            if(rp2350) debug.Reset();
            Advance();
            Assert.AreEqual(first, core0.ExecutedInstructions);
            Assert.AreEqual(second, core1.ExecutedInstructions);
            setRun(true);
            setRun(false);
            Advance();
            first = core0.ExecutedInstructions;
            Advance();
            Assert.AreEqual(first, core0.ExecutedInstructions, "A stale release must not undo reasserted RUN");
            setRun(true);
            Advance();
            Assert.Greater(core0.ExecutedInstructions, first);
        }

        [Test]
        public void ShouldNotReleaseAnIndependentClockHold()
        {
            setRun(false);
            Advance();
            core0.Clocked = false;
            setRun(true);
            Advance();
            Assert.AreEqual(0ul, core0.ExecutedInstructions);
            Assert.Greater(core1.ExecutedInstructions, 0ul);
            core0.Clocked = true;
            Advance();
            Assert.Greater(core0.ExecutedInstructions, 0ul);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ShouldKeepIndependentWaitPowerSequencerAndDebuggerHolds(int hold)
        {
            // CpuWait is sampled by processor reset; its rising edge does not
            // itself reset an already running processor.
            if(hold == 0) core0.CpuWaitSignal.Set();
            setRun(false);
            Advance();
            if(hold == 1) core0.SetPowerSequencerResetHold(true);
            else if(hold == 2)
            {
                core0.EnableMemoryMappedDebug = true;
                nvic0.WriteDoubleWord(0xdf0, 0xa05f0003);
                Assert.IsTrue(SpinWait.SpinUntil(() => core0.HaltedByDebugger, TimeSpan.FromSeconds(5)));
            }
            setRun(true);
            Advance();
            Assert.AreEqual(0ul, core0.ExecutedInstructions);
            Assert.IsTrue(core0.HasAnyHaltingCondition);
            Assert.Greater(core1.ExecutedInstructions, 0ul);
            if(hold == 0) core0.CpuWaitSignal.Unset();
            else if(hold == 1) core0.SetPowerSequencerResetHold(false);
            else
            {
                nvic0.WriteDoubleWord(0xdf0, 0xa05f0000);
                Assert.IsTrue(SpinWait.SpinUntil(() => !core0.HaltedByDebugger, TimeSpan.FromSeconds(5)));
            }
            Advance();
            Assert.Greater(core0.ExecutedInstructions, 0ul);
        }

        [Test]
        public void ShouldPreserveAHostPauseArrivingAfterThePhysicalEdge()
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var blocks = 0;
            var resetObserved = 0;
            core0.SetHookAtBlockBegin((_, __) => Interlocked.Increment(ref blocks));
            using(var boundaryEntered = new ManualResetEventSlim())
            using(var boundaryReleased = new ManualResetEventSlim())
            using(var peerRestarted = new ManualResetEventSlim())
            {
                machine.MachineReset += _ => Volatile.Write(ref resetObserved, 1);
                core1.SetHookAtBlockBegin((_, __) =>
                {
                    if(Volatile.Read(ref resetObserved) != 0) peerRestarted.Set();
                });
                emulation.StartAll();
                try
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => core0.ExecutedInstructions > 0 && core1.ExecutedInstructions > 0,
                        TimeSpan.FromSeconds(5)), "Both native processors must first execute guest instructions");
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
                    {
                        boundaryEntered.Set();
                        if(!boundaryReleased.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Reset boundary was not released");
                    });
                    Assert.IsTrue(boundaryEntered.Wait(TimeSpan.FromSeconds(5)));
                    setRun(false);
                    core0.Pause();
                    core0.TimeHandle.Enabled = false;
                    var pausedBlocks = Volatile.Read(ref blocks);
                    var pausedCycles = core0.ElapsedCycles;
                    setRun(true);
                    boundaryReleased.Set();
                    Assert.IsTrue(peerRestarted.Wait(TimeSpan.FromSeconds(5)), "The peer must restart after the queued reset");
                    Assert.IsTrue(core0.IsPaused);
                    Assert.IsFalse(core0.TimeHandle.Enabled);
                    Assert.AreEqual(pausedBlocks, Volatile.Read(ref blocks));
                    Assert.AreEqual(pausedCycles, core0.ElapsedCycles);
                }
                finally
                {
                    boundaryReleased.Set();
                    emulation.PauseAll();
                }
            }
        }

        [Test]
        public void ShouldQueueRunResetFromNativeMMIOWhileAPeerWaitsForTheClock()
        {
            using(var trigger = new NativeResetTrigger(machine, setRun))
            using(var resetCompleted = new ManualResetEventSlim())
            {
                machine.SystemBus.Register(trigger, new BusPointRegistration(0x40090000));
                machine.SystemBus.Register(trigger.Reader, new BusPointRegistration(0x40094000));
                rom.WriteWord(0x100, 0x6008); // str r0, [r1]
                rom.WriteWord(0x102, 0xbf30); // wfi
                rom.WriteWord(0x104, 0xe7fe);
                rom.WriteWord(0x120, 0x6808); // ldr r0, [r1]
                rom.WriteWord(0x122, 0xbf30);
                rom.WriteWord(0x124, 0xe7fe);
                rom.WriteDoubleWord(0x1000, 0x20001000);
                rom.WriteDoubleWord(0x1004, 0x121);
                core1.InitVectorTableOffset = 0x1000;
                core0.Reset();
                core1.Reset();
                core0.SetRegister(0, 1);
                core0.SetRegister(1, 0x40090000);
                core1.SetRegister(1, 0x40094000);
                probe.BeforeReset = () =>
                {
                    Assert.IsTrue(trigger.InputReturned);
                    Assert.IsFalse(Monitor.IsEntered(trigger));
                    // The safe reset boundary has paused both native callbacks.
                    core0.SetHookAtBlockBegin(null);
                    core1.SetHookAtBlockBegin(null);
                    Assert.IsTrue(Task.Run(() => machine.ClockSource.ExecuteInLock(() => { }))
                        .Wait(TimeSpan.FromSeconds(5)), "Reset preparation still owns the originating clock lock");
                };
                probe.AfterReset = () => resetCompleted.Set();
                var before = probe.Count;
                trigger.PrepareNativeEntries(core0, core1);
                var emulation = EmulationManager.Instance.CurrentEmulation;
                try
                {
                    emulation.StartAll();
                    Assert.IsTrue(resetCompleted.Wait(TimeSpan.FromSeconds(10)), trigger.Failure ?? "Native reset did not complete");
                }
                finally
                {
                    emulation.PauseAll();
                    core0.SetHookAtBlockBegin(null);
                    core1.SetHookAtBlockBegin(null);
                }
                Assert.IsTrue(trigger.InputReturned, "The native MMIO reset input must return without joining the clock-blocked peer");
                Assert.IsTrue(trigger.PeerEntered);
                Assert.IsNull(trigger.Failure);
                Assert.AreEqual(before + 1, probe.Count);
            }
        }

        [Test]
        public void ShouldPrepareRetainedStateOnceBeforePeripheralResetWhilePaused()
        {
            var prepared = 0;
            probe.BeforeReset = () =>
            {
                Assert.AreEqual(1, prepared);
                Assert.IsTrue(core0.IsPaused);
                Assert.IsTrue(core1.IsPaused);
            };
            machine.RequestResetInSafeState(null, null, false, () =>
            {
                Assert.IsTrue(core0.IsPaused);
                Assert.IsTrue(core1.IsPaused);
                prepared++;
            });
            Advance();
            Assert.AreEqual(1, prepared);
        }

        private void Advance()
        {
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(10));
            TestContext.WriteLine($"machinePaused={machine.IsPaused}; core0: paused={core0.IsPaused}, halted={core0.IsHalted}, clocked={core0.Clocked}, wait={core0.CpuWaitSignal.IsSet}, instructions={core0.ExecutedInstructions}, pc={core0.PC}; core1: paused={core1.IsPaused}, halted={core1.IsHalted}, clocked={core1.Clocked}, wait={core1.CpuWaitSignal.IsSet}, instructions={core1.ExecutedInstructions}, pc={core1.PC}");
        }

        private sealed class ResetProbe : IPeripheral
        {
            public void Reset() { BeforeReset?.Invoke(); Count++; AfterReset?.Invoke(); }
            public int Count { get; private set; }
            public Action BeforeReset { get; set; }
            public Action AfterReset { get; set; }
        }

        private sealed class NativeResetTrigger : IDoubleWordPeripheral, IKnownSize, IDisposable
        {
            public NativeResetTrigger(Machine machine, Action<bool> setRun)
            {
                this.machine = machine;
                this.setRun = setRun;
                Reader = new PeerReader(this);
            }

            public void PrepareNativeEntries(CortexM publisher, CortexM peer)
            {
                // Both PEs must pass their clock-budget read before the publisher
                // owns the clock. The peer then attempts its native LDR within
                // that admitted grant while the publisher holds the clock lock.
                var publisherGateUsed = 0;
                var peerGateUsed = 0;
                publisher.SetHookAtBlockBegin((_, __) =>
                {
                    if(Interlocked.Exchange(ref publisherGateUsed, 1) != 0) return;
                    if(!peerNativeReady.Wait(TimeSpan.FromSeconds(5)))
                        Failure = "Peer did not reach its native entry gate";
                });
                peer.SetHookAtBlockBegin((_, __) =>
                {
                    if(Interlocked.Exchange(ref peerGateUsed, 1) != 0) return;
                    peerNativeReady.Set();
                    if(!clockOwned.Wait(TimeSpan.FromSeconds(5)))
                        Failure = "Publisher did not acquire the clock before peer entry";
                });
            }

            public uint ReadDoubleWord(long offset) => 0;

            private uint ReadPeer()
            {
                machine.SystemBus.TryGetCurrentCPU(out var cpu);
                Console.WriteLine($"Native reset peer read: core={cpu?.MultiprocessingId}, pc={cpu?.PC}");
                if(!clockOwned.Wait(TimeSpan.FromSeconds(5)))
                {
                    Failure = "Native clock owner did not start";
                    return 0;
                }
                peerEntered.Set();
                machine.ClockSource.ExecuteInLock(() => { });
                return 0;
            }

            public void WriteDoubleWord(long offset, uint value)
            {
                if(Interlocked.Exchange(ref armed, 0) == 0) return;
                machine.SystemBus.TryGetCurrentCPU(out var cpu);
                Console.WriteLine($"Native reset publisher write: core={cpu?.MultiprocessingId}, pc={cpu?.PC}");
                machine.ClockSource.ExecuteInLock(() =>
                {
                    lock(this)
                    {
                        clockOwned.Set();
                        if(!peerEntered.Wait(TimeSpan.FromSeconds(5)))
                        {
                            Failure = "Native peer did not enter its clock access";
                            return;
                        }
                        setRun(false);
                        setRun(true);
                        InputReturned = true;
                    }
                });
            }

            public void Reset() { }
            public long Size => 4;
            public bool InputReturned { get; private set; }
            public bool PeerEntered => peerEntered.IsSet;
            public string Failure { get; private set; }
            public PeerReader Reader { get; }
            public void Dispose() { clockOwned.Dispose(); peerEntered.Dispose(); peerNativeReady.Dispose(); }
            private readonly Machine machine;
            private readonly Action<bool> setRun;
            private readonly ManualResetEventSlim clockOwned = new ManualResetEventSlim();
            private readonly ManualResetEventSlim peerEntered = new ManualResetEventSlim();
            private readonly ManualResetEventSlim peerNativeReady = new ManualResetEventSlim();
            private int armed = 1;

            // Separate bus peripherals allow simultaneous native transactions;
            // the fixture deliberately shares only the peripheral clock lock.
            public sealed class PeerReader : IDoubleWordPeripheral, IKnownSize
            {
                public PeerReader(NativeResetTrigger owner) { this.owner = owner; }
                public uint ReadDoubleWord(long offset) => owner.ReadPeer();
                public void WriteDoubleWord(long offset, uint value) { }
                public void Reset() { }
                public long Size => 4;
                private readonly NativeResetTrigger owner;
            }
        }

        private readonly bool rp2350;
        private Machine machine;
        private CortexM core0;
        private CortexM core1;
        private NVIC nvic0;
        private MappedMemory rom;
        private MappedMemory lowRAM;
        private MappedMemory highRAM;
        private WinbondW25QFlash flash;
        private RP2350_DebugPort debug;
        private RP2350_POWMAN powman;
        private ResetProbe probe;
        private Action<bool> setRun;
    }
}
