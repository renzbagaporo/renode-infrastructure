// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.Threading;
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
    [TestFixture]
    public class RP2350_ResetEngineIntegrationTests
    {
        [SetUp]
        public void SetUp()
        {
            TestContext.Progress.WriteLine("START " + TestContext.CurrentContext.Test.Name);
            EmulationManager.Instance.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            if(machine != null) EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);
            machine = null;
        }

        [Test]
        public void ShouldConstructAndResetStandaloneWithoutATimeDomain()
        {
            using(var standalone = new Machine())
            {
                Assert.IsNull(standalone.LocalTimeSource);
                var port = new TestPort(standalone, null, null, null);
                RP2350_POWMAN power = null;
                Assert.DoesNotThrow(() => power = new RP2350_POWMAN(standalone, null, null, null, null, port));
                Assert.DoesNotThrow(() => power.Reset());
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldRunAnActualArmAircrWriteAndExecuteTheRequestedRiscVEngine(bool alreadyRunning)
        {
            Create();
            if(alreadyRunning) Advance();
            // Shared reset ROM dispatches by genuine SIO physical CPUID.
            rom.WriteWord(0x100, 0x4807); // ldr r0,=SIO.CPUID
            rom.WriteWord(0x102, 0x6800); // ldr r0,[r0]
            rom.WriteWord(0x104, 0x2800); // cmp r0,#0
            rom.WriteWord(0x106, 0xd11b); // bne sibling loop at 0x140
            rom.WriteWord(0x108, 0x4806); // ldr r0,=OTP.ARCHSEL
            rom.WriteWord(0x10a, 0x2101); // movs r1,#1
            rom.WriteWord(0x10c, 0x6001); // str r1,[r0]
            rom.WriteWord(0x10e, 0x4806); // ldr r0,=AIRCR
            rom.WriteWord(0x110, 0x4906); // ldr r1,=VECTKEY|SYSRESETREQ
            rom.WriteWord(0x112, 0x6001); // actual guest architectural reset request
            rom.WriteWord(0x114, 0xe7fe);
            rom.WriteDoubleWord(0x120, 0xd0000000);
            rom.WriteDoubleWord(0x124, 0x40120158);
            rom.WriteDoubleWord(0x128, 0xe000ed0c);
            rom.WriteDoubleWord(0x12c, 0x05fa0004);
            rom.WriteWord(0x140, 0x3401);
            rom.WriteWord(0x142, 0xe7fd);
            if(alreadyRunning) powman.OnGPIO(0, true);
            Advance();
            Assert.IsTrue(ReferenceEquals(rv[0], selector.ActiveProcessor(0)));
            Assert.AreEqual(1u, otp.ReadDoubleWord(0x15c));
            Assert.Greater(rv[0].ExecutedInstructions, 0u);
            Assert.Greater((uint)rv[0].GetRegister(15), 0u);
            Assert.AreEqual(0u, arm[0].ExecutedInstructions);
            Assert.Greater(arm[1].ExecutedInstructions, 0u, $"Sibling paused={arm[1].IsPaused}, halted={arm[1].IsHalted}, held={arm[1].HasAnyHaltingCondition}, attached={arm[1].TimeHandle.Enabled}, deferred={arm[1].TimeHandle.DeferredEnabled}, PC={arm[1].PC}");
            Assert.AreEqual(0u, rv[1].ExecutedInstructions);
        }

        [Test]
        public void ShouldRetainSiblingMemoryAndAlwaysOnStateAcrossIndividualSysReset()
        {
            Create();
            Advance();
            arm[0].SetRegister(5, 0x12345678);
            arm[1].SetRegister(5, 0x87654321);
            low.WriteDoubleWord(0x80, 0x11223344);
            high.WriteDoubleWord(0x80, 0x55667788);
            bootram.WriteDoubleWord(0, 0xa1b2c3d4);
            powman.WriteDoubleWord(0xb0, 0xfedcba98);
            SetWriteEnable();
            debug.WriteDP(4, 0x50000000);
            var dpControl = debug.ReadDP(4);
            var resets = probe.Count;
            powman.OnGPIO(0, true);
            Advance();
            Assert.AreEqual(0u, (uint)arm[0].GetRegister(5));
            Assert.AreEqual(0x87654321u, (uint)arm[1].GetRegister(5));
            Assert.AreEqual(0x11223344u, low.ReadDoubleWord(0x80));
            Assert.AreEqual(0x55667788u, high.ReadDoubleWord(0x80));
            Assert.AreEqual(0xa1b2c3d4u, bootram.ReadDoubleWord(0));
            Assert.AreEqual(0xfedcba98u, powman.ReadDoubleWord(0xb0));
            Assert.AreEqual(dpControl, debug.ReadDP(4));
            Assert.AreEqual(resets, probe.Count);
            AssertWriteEnable();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldTransferAnEarlyOrLateHostPauseAcrossSysReset(bool late)
        {
            Create();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var entered = new ManualResetEventSlim())
            using(var release = new ManualResetEventSlim())
            using(var completed = new ManualResetEventSlim())
            {
                powman.WarmReset += _ => completed.Set();
                emulation.StartAll();
                try
                {
                    WaitForArmExecution();
                    if(late)
                    {
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => {
                            entered.Set();
                            if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Boundary was not released");
                        });
                        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                    }
                    else Detach(arm[0]);
                    otp.WriteDoubleWord(0x158, 1);
                    powman.OnGPIO(0, true);
                    if(late) Detach(arm[0]);
                    release.Set();
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                    Assert.IsTrue(ReferenceEquals(rv[0], selector.ActiveProcessor(0)));
                    Assert.IsTrue(rv[0].IsPaused);
                    Assert.IsFalse(rv[0].TimeHandle.Enabled);
                    Assert.AreEqual(0u, rv[0].ExecutedInstructions);
                    Assert.AreEqual(0u, arm[0].ExecutedInstructions);
                    Attach(rv[0]);
                    Assert.IsTrue(SpinWait.SpinUntil(() => rv[0].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
                    Assert.AreEqual(0u, arm[0].ExecutedInstructions);
                }
                finally { release.Set(); emulation.PauseAll(); }
            }
        }

        [Test]
        public void ShouldPreserveAnExplicitSiblingPauseDuringSysReset()
        {
            Create();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var completed = new ManualResetEventSlim())
            {
                powman.WarmReset += _ => completed.Set();
                emulation.StartAll();
                try
                {
                    WaitForArmExecution();
                    Detach(arm[1]);
                    arm[1].SetRegister(5, 0x12345678);
                    var count = arm[1].ExecutedInstructions;
                    powman.OnGPIO(0, true);
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                    Assert.IsTrue(arm[1].IsPaused);
                    Assert.IsFalse(arm[1].TimeHandle.Enabled);
                    Assert.AreEqual(count, arm[1].ExecutedInstructions);
                    Assert.AreEqual(0x12345678u, (uint)arm[1].GetRegister(5));
                    Assert.IsTrue(SpinWait.SpinUntil(() => arm[0].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
                }
                finally { emulation.PauseAll(); }
            }
        }

        [Test]
        public void ShouldKeepIndependentResetAndDebuggerHoldsThroughWarmReset()
        {
            Create();
            Advance();
            otp.WriteDoubleWord(0x158, 1);
            rv[0].HaltedByDebugger = true;
            powman.OnGPIO(0, true);
            Advance();
            Assert.IsTrue(rv[0].HasAnyHaltingCondition);
            Assert.AreEqual(0u, rv[0].ExecutedInstructions);
            Assert.AreEqual(0u, arm[0].ExecutedInstructions);
            rv[0].HaltedByDebugger = false;
            Advance();
            Assert.Greater(rv[0].ExecutedInstructions, 0u);
        }

        [Test]
        public void ShouldReturnRunningRiscVToTheFactoryArmArchitectureOnPhysicalRun()
        {
            Create();
            Switch(3, 3);
            Assert.Greater(rv[0].ExecutedInstructions, 0u);
            Assert.Greater(rv[1].ExecutedInstructions, 0u);
            low.WriteDoubleWord(0x80, 0x12345678);
            high.WriteDoubleWord(0x80, 0x87654321);
            powman.WriteDoubleWord(0x202c, 0x5afe0001);
            powman.WriteDoubleWord(0xb0, 0x12345678);
            SetWriteEnable();
            debug.OnGPIO(2, false);
            Advance();
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x158));
            AssertAllCountsZero();
            Assert.IsTrue(arm[0].HasAnyHaltingCondition);
            Assert.IsTrue(arm[1].HasAnyHaltingCondition);
            Assert.AreEqual(0u, low.ReadDoubleWord(0x80));
            Assert.AreEqual(0u, high.ReadDoubleWord(0x80));
            Assert.AreEqual((1u << 18) | 1, powman.ReadDoubleWord(0x2c));
            Assert.AreEqual(0u, powman.ReadDoubleWord(0xb0));
            AssertWriteEnable();
            debug.OnGPIO(2, true);
            Advance();
            AssertArmExecuting();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldResetAMixedFixtureOnRescueAndAcceptEarlyRelease(bool earlyRelease)
        {
            Create();
            Switch(2, 2);
            powman.WriteDoubleWord(0x202c, 0x5afe0001);
            powman.WriteDoubleWord(0xb0, 0x13579bdf);
            SetWriteEnable();
            var count = probe.Count;
            debug.SetRescue(true);
            if(earlyRelease) debug.SetRescue(false);
            Advance();
            Assert.AreEqual(count + 1, probe.Count);
            Assert.AreEqual((1u << 21) | 0x11, powman.ReadDoubleWord(0x2c));
            Assert.AreEqual(0x13579bdfu, powman.ReadDoubleWord(0xb0));
            AssertWriteEnable();
            if(!earlyRelease)
            {
                AssertAllCountsZero();
                Assert.IsTrue(arm[0].HasAnyHaltingCondition);
                Assert.IsTrue(arm[1].HasAnyHaltingCondition);
                debug.SetRescue(false);
                Advance();
            }
            AssertArmExecuting();
        }

        [Test]
        public void ShouldDropRescueWhenPhysicalRunResetsTheRpApRegister()
        {
            Create();
            Switch(2, 2);
            debug.SetRescue(true);
            Advance();
            AssertAllCountsZero();
            debug.OnGPIO(2, false);
            Advance();
            Assert.IsFalse(debug.RescueAsserted);
            AssertAllCountsZero();
            Assert.IsTrue(arm[0].HasAnyHaltingCondition);
            debug.OnGPIO(2, true);
            Advance();
            AssertArmExecuting();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldReconcileAFullDebugPortResetWithoutLeavingARescueHold(bool riscvDefault)
        {
            Create();
            if(riscvDefault) SetRiscVFactoryDefault();
            Advance();
            debug.SetRescue(true);
            Advance();
            AssertAllCountsZero();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var completed = new ManualResetEventSlim())
            {
                emulation.StartAll();
                try
                {
                    machine.RequestResetInSafeState(postReset: () => completed.Set());
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                    Assert.IsFalse(debug.RescueAsserted);
                    var active = selector.ActiveProcessor(0);
                    Assert.IsTrue(SpinWait.SpinUntil(() => active.ExecutedInstructions > 0, TimeSpan.FromSeconds(5)),
                        $"Active engine remained paused={active.IsPaused}, held={active.HasAnyHaltingCondition}, attached={active.TimeHandle.Enabled}");
                }
                finally { emulation.PauseAll(); }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldReleaseOnlyRescueAndKeepPsmCpuWaitAndDebuggerHolds(bool useSelector)
        {
            Create(useSelector);
            Advance();
            // CPUWAIT is sampled at reset, so establish it before the rescue edge.
            arm[1].CpuWaitSignal.Set();
            debug.SetRescue(true);
            Advance();
            psm.WriteDoubleWord(0x2004, 1u << 23);
            Advance();
            arm[0].HaltedByDebugger = true;
            debug.SetRescue(false);
            Advance();
            Assert.AreEqual(0u, arm[0].ExecutedInstructions);
            Assert.AreEqual(0u, arm[1].ExecutedInstructions);
            Assert.IsTrue(arm[1].CpuWaitSignal.IsSet);
            Assert.IsTrue(arm[0].HaltedByDebugger);
            arm[0].HaltedByDebugger = false;
            Advance();
            Assert.AreEqual(0u, arm[0].ExecutedInstructions);
            psm.WriteDoubleWord(0x3004, 1u << 23);
            Advance();
            Assert.Greater(arm[0].ExecutedInstructions, 0u);
            Assert.AreEqual(0u, arm[1].ExecutedInstructions);
            Assert.IsTrue(arm[1].CpuWaitSignal.IsSet);
            arm[1].CpuWaitSignal.Unset();
            Advance();
            Assert.Greater(arm[1].ExecutedInstructions, 0u);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldPreserveAnEarlyOrLatePhysicalHostPauseAcrossRescue(bool late)
        {
            Create();
            Switch(1, 1);
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var entered = new ManualResetEventSlim())
            using(var release = new ManualResetEventSlim())
            {
                emulation.StartAll();
                try
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => rv[0].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => {
                        entered.Set();
                        if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Boundary was not released");
                    });
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                    if(!late) Detach(rv[0]);
                    var resets = probe.Count;
                    debug.SetRescue(true);
                    if(late) Detach(rv[0]);
                    release.Set();
                    Assert.IsTrue(SpinWait.SpinUntil(() => probe.Count > resets, TimeSpan.FromSeconds(5)));
                    Assert.IsTrue(SpinWait.SpinUntil(() => ReferenceEquals(arm[0], selector.ActiveProcessor(0)), TimeSpan.FromSeconds(5)));
                    debug.SetRescue(false);
                    using(var released = new ManualResetEventSlim())
                    {
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => released.Set());
                        Assert.IsTrue(released.Wait(TimeSpan.FromSeconds(5)));
                    }
                    Assert.IsTrue(arm[0].IsPaused);
                    Assert.IsFalse(arm[0].TimeHandle.Enabled);
                    Assert.IsFalse(arm[0].HasAnyHaltingCondition);
                    Assert.AreEqual(0u, arm[0].ExecutedInstructions);
                    Assert.IsTrue(SpinWait.SpinUntil(() => arm[1].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
                    Attach(arm[0]);
                    Assert.IsTrue(SpinWait.SpinUntil(() => arm[0].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
                    Assert.AreEqual(0u, rv[0].ExecutedInstructions);
                }
                finally { release.Set(); emulation.PauseAll(); }
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ShouldWarmResetAnActiveRiscVProcessorWithoutResettingItsSibling(int core)
        {
            Create();
            Switch(3, 3);
            var peer = 1 - core;
            rv[peer].SetRegister(20, 0x12345678);
            var peerCount = rv[peer].ExecutedInstructions;
            low.WriteDoubleWord(0x80, 0x87654321);
            var resets = probe.Count;
            otp.WriteDoubleWord(0x158, (uint)(1 << peer));
            powman.OnGPIO(core, true);
            Advance();
            Assert.IsTrue(ReferenceEquals(arm[core], selector.ActiveProcessor(core)));
            Assert.IsTrue(ReferenceEquals(rv[peer], selector.ActiveProcessor(peer)));
            Assert.Greater(arm[core].ExecutedInstructions, 0u);
            Assert.AreEqual(0u, rv[core].ExecutedInstructions);
            Assert.Greater(rv[peer].ExecutedInstructions, peerCount);
            Assert.AreEqual(0x12345678u, (uint)rv[peer].GetRegister(20));
            Assert.AreEqual(0x87654321u, low.ReadDoubleWord(0x80));
            Assert.AreEqual(resets, probe.Count);
        }

        [Test]
        public void ShouldSampleWarmArchitectureOnlyAfterAnIndependentPsmHoldReleases()
        {
            Create();
            Advance();
            psm.WriteDoubleWord(0x2004, 1u << 23);
            Advance();
            otp.WriteDoubleWord(0x158, 1);
            rv[0].HaltedByDebugger = true;
            powman.OnGPIO(0, true);
            Advance();
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
            AssertCoreCountsZero(0);
            psm.WriteDoubleWord(0x3004, 1u << 23);
            Advance();
            Assert.AreEqual(1u, otp.ReadDoubleWord(0x15c));
            Assert.IsTrue(rv[0].HasAnyHaltingCondition);
            AssertCoreCountsZero(0);
            rv[0].HaltedByDebugger = false;
            Advance();
            Assert.Greater(rv[0].ExecutedInstructions, 0u);
            Assert.AreEqual(0u, arm[0].ExecutedInstructions);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldHonorRescueOnAllEnginesFromFactoryRiscV(bool beforeFirstRun)
        {
            Create();
            SetRiscVFactoryDefault();
            if(!beforeFirstRun) Advance();
            debug.SetRescue(true);
            Advance();
            Assert.AreEqual(3u, otp.ReadDoubleWord(0x15c));
            AssertAllCountsZero();
            foreach(var cpu in rv) Assert.IsTrue(cpu.HasAnyHaltingCondition);
            debug.SetRescue(false);
            Advance();
            foreach(var cpu in rv) Assert.Greater(cpu.ExecutedInstructions, 0u);
            foreach(var cpu in arm) Assert.AreEqual(0u, cpu.ExecutedInstructions);
        }

        private void AssertCoreCountsZero(int core)
        {
            Assert.AreEqual(0u, arm[core].ExecutedInstructions);
            Assert.AreEqual(0u, rv[core].ExecutedInstructions);
        }

        private void Create(bool useSelector = true)
        {
            machine = new Machine();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            emulation.MasterTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            emulation.AddMachine(machine, "reset_engines");
            machine.LocalTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            arm = new CortexM[2];
            nvic = new[] {new NVIC(machine), new NVIC(machine)};
            for(var core = 0; core < 2; core++)
            {
                arm[core] = new CortexM("cortex-m33-rp2350", machine, nvic[core], cpuId: (uint)core, enableTrustZone: true);
                machine.SystemBus.Register(arm[core], new CPURegistrationPoint(core));
                machine.SetLocalName(arm[core], "arm" + core);
                machine.SystemBus.Register(nvic[core], new BusPointRegistration(0xe000e000, cpu: arm[core]));
                nvic[core].IRQ.Connect(arm[core], 0);
            }
            rom = new MappedMemory(machine, 0x8000);
            machine.SystemBus.Register(rom, new BusPointRegistration(0));
            rom.WriteDoubleWord(0, 0x20001000);
            rom.WriteDoubleWord(4, 0x101);
            rom.WriteWord(0x100, 0x3401); // adds r4,#1
            rom.WriteWord(0x102, 0xe7fd); // branch back
            machine.SystemBus.Register(new RP2350_SIO(machine), new BusPointRegistration(0xd0000000));
            rom.WriteDoubleWord(0x7dfc, 0x00178793); // addi a5,a5,1
            rom.WriteDoubleWord(0x7e00, 0xffdff06f); // branch back
            low = new MappedMemory(machine, 0x1000);
            high = new MappedMemory(machine, 0x1000);
            machine.SystemBus.Register(low, new BusPointRegistration(0x20000000));
            machine.SystemBus.Register(high, new BusPointRegistration(0x20040000));
            otp = new RP2350_OTP(machine);
            machine.SystemBus.Register(otp, new BusPointRegistration(0x40120000));
            psm = new RP2350_PSM(machine, arm[0], arm[1], nvic[0], nvic[1]);
            machine.SystemBus.Register(psm, new BusPointRegistration(0x40018000));
            rv = new RP2350_Hazard3[2];
            if(useSelector)
            {
                for(var core = 0; core < 2; core++)
                {
                    rv[core] = new RP2350_Hazard3(machine, (uint)core);
                    machine.SystemBus.Register(rv[core], new CPURegistrationPoint(core + 2));
                    machine.SetLocalName(rv[core], "rv" + core);
                }
                selector = new RP2350_ArchitectureSelector(machine, arm[0], arm[1], rv[0], rv[1], nvic[0], nvic[1], otp, psm);
                machine.SystemBus.Register(selector, NullRegistrationPoint.Instance);
            }
            debug = new TestPort(machine, arm[0], arm[1], useSelector ? selector : null);
            machine.SystemBus.Register(debug, NullRegistrationPoint.Instance);
            bootram = new RP2350_BootRAM(machine);
            machine.SystemBus.Register(bootram, new BusPointRegistration(0x400e0000));
            powman = new RP2350_POWMAN(machine, arm[0], arm[1], nvic[0], nvic[1], debug,
                bootRAM: bootram, sramLow: low, sramHigh: high, architectureSelector: useSelector ? selector : null);
            machine.SystemBus.Register(powman, new BusPointRegistration(0x40100000));
            nvic[0].SystemResetRequest.Connect(powman, 0);
            nvic[1].SystemResetRequest.Connect(powman, 1);
            flash = new WinbondW25QFlash(machine, new MappedMemory(machine, 0x200000));
            machine.SystemBus.Register(flash, NullRegistrationPoint.Instance);
            probe = new ResetProbe();
            machine.SystemBus.Register(probe, NullRegistrationPoint.Instance);
            machine.PostCreationActions();
        }

        private void Switch(uint architecture, uint cores)
        {
            otp.WriteDoubleWord(0x158, architecture);
            psm.WriteDoubleWord(0x2004, cores << 23);
            Advance();
            psm.WriteDoubleWord(0x3004, cores << 23);
            Advance();
        }

        private void SetRiscVFactoryDefault()
        {
            var bytes = new byte[4096 * 4];
            for(var row = 0x40; row < 0x43; row++) bytes[row * 4] = 8;
            var path = System.IO.Path.GetTempFileName();
            try { System.IO.File.WriteAllBytes(path, bytes); otp.LoadRawImage(path); selector.Reset(); }
            finally { System.IO.File.Delete(path); }
        }

        private void Advance() => EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
        private void WaitForArmExecution() => Assert.IsTrue(SpinWait.SpinUntil(() => arm[0].ExecutedInstructions > 0 && arm[1].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
        private static void Detach(BaseCPU cpu) { cpu.Pause(); cpu.TimeHandle.Enabled = false; cpu.TimeHandle.DeferredEnabled = false; }
        private static void Attach(BaseCPU cpu) { cpu.TimeHandle.Enabled = true; cpu.TimeHandle.DeferredEnabled = true; cpu.Resume(); }
        private void SetWriteEnable() { flash.Transmit(0x06); flash.FinishTransmission(); }
        private void AssertWriteEnable() { flash.Transmit(0x05); Assert.AreEqual(2, flash.Transmit(0) & 2); flash.FinishTransmission(); }
        private void AssertAllCountsZero()
        {
            foreach(var cpu in arm) Assert.AreEqual(0u, cpu.ExecutedInstructions);
            foreach(var cpu in rv) if(cpu != null) Assert.AreEqual(0u, cpu.ExecutedInstructions);
        }
        private void AssertArmExecuting()
        {
            foreach(var cpu in arm) Assert.Greater(cpu.ExecutedInstructions, 0u);
            foreach(var cpu in rv) if(cpu != null) Assert.AreEqual(0u, cpu.ExecutedInstructions);
        }
        private sealed class ResetProbe : IPeripheral { public void Reset() { Count++; } public int Count; }
        private sealed class TestPort : RP2350_DebugPort
        {
            public TestPort(IMachine machine, CortexM first, CortexM second, RP2350_ArchitectureSelector selector)
                : base(machine, first, second, selector) { }
            public void WriteDP(byte address, uint value) => CompleteWrite(false, address, value);
            public uint ReadDP(byte address)
            {
                var result = BeginTransfer(false, true, address, out var value);
                Assert.AreEqual(Acknowledgement.Ok, result);
                return value;
            }
            public bool RescueAsserted
            {
                get
                {
                    CompleteWrite(false, 8, 0x80000);
                    Assert.AreEqual(Acknowledgement.Ok, BeginTransfer(true, true, 0, out var _));
                    return (ReadDP(12) & 0x80000000) != 0;
                }
            }
            public void SetRescue(bool held)
            {
                CompleteWrite(false, 8, 0x80000);
                CompleteWrite(true, 0, held ? 0x80000000u : 0u);
            }
        }
        private Machine machine;
        private CortexM[] arm;
        private RP2350_Hazard3[] rv;
        private NVIC[] nvic;
        private MappedMemory rom, low, high;
        private RP2350_OTP otp;
        private RP2350_PSM psm;
        private RP2350_ArchitectureSelector selector;
        private TestPort debug;
        private RP2350_POWMAN powman;
        private RP2350_BootRAM bootram;
        private WinbondW25QFlash flash;
        private ResetProbe probe;
    }
}
