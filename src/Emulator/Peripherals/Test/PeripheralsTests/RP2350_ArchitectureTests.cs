// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_ArchitectureTests
    {
        [SetUp]
        public void SetUp()
        {
            TestContext.Progress.WriteLine("START " + TestContext.CurrentContext.Test.Name);
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "architecture");
            EmulationManager.Instance.CurrentEmulation.MasterTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            otp = new RP2350_OTP(machine);
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        // A2 priority: ARM_DISABLE, RISCV_DISABLE, SECURE_BOOT, DEFAULT_ARCHSEL.
        [TestCase(0u, 0u, 0u, 3u)]
        [TestCase(0u, 8u, 3u, 3u)]
        [TestCase(0u, 1u, 0u, 0u)]
        [TestCase(0u, 9u, 0u, 0u)]
        [TestCase(1u, 0u, 3u, 3u)]
        [TestCase(1u, 9u, 3u, 3u)]
        [TestCase(2u, 8u, 0u, 0u)]
        [TestCase(3u, 1u, 3u, 3u)]
        public void ShouldApplyA2FusePriority(uint crit0, uint crit1, uint expectedDefault, uint expectedRequest)
        {
            LoadFuses(crit0, crit1);
            Assert.AreEqual(expectedDefault, otp.ReadDoubleWord(0x158));
            otp.WriteDoubleWord(0x158, 3);
            Assert.AreEqual(expectedRequest, otp.ReadDoubleWord(0x158));
            otp.LatchArchitecture(0);
            otp.LatchArchitecture(1);
            Assert.AreEqual(expectedRequest, otp.ReadDoubleWord(0x15c));
        }

        [Test]
        public void ShouldLatchEachPhysicalCoreOnlyOnItsResetRelease()
        {
            Assert.IsFalse(otp.LatchArchitecture(0));
            Assert.IsFalse(otp.LatchArchitecture(1));
            otp.WriteDoubleWord(0x158, 3);
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
            Assert.IsTrue(otp.LatchArchitecture(1));
            Assert.AreEqual(2u, otp.ReadDoubleWord(0x15c));
            otp.Reset(); // OTP-controller reset is later than architecture capture.
            Assert.AreEqual(3u, otp.ReadDoubleWord(0x158));
            Assert.AreEqual(2u, otp.ReadDoubleWord(0x15c));
            otp.ResetArchitectureSelection();
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x158));
            Assert.AreEqual(2u, otp.ReadDoubleWord(0x15c));
            Assert.IsFalse(otp.LatchArchitecture(1));
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
        }

        [Test]
        public void ShouldSwitchBothWaysWithoutExecutingTheInactiveEngines()
        {
            BuildEngines();
            Advance();
            Assert.Greater(arms[0].ExecutedInstructions, 0u);
            Assert.Greater(arms[1].ExecutedInstructions, 0u);
            Assert.AreEqual(0u, riscv[0].ExecutedInstructions);
            Assert.AreEqual(0u, riscv[1].ExecutedInstructions);
            otp.WriteDoubleWord(0x158, 3);
            Advance();
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
            Dispatch(() => {
                for(var core = 0; core < 2; core++)
                {
                    selector.SetPowerSequencerResetHold(core, true);
                    selector.ResetProcessor(core);
                    Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
                }
            });
            Dispatch(() => {
                for(var core = 0; core < 2; core++)
                {
                    selector.SetPowerSequencerResetHold(core, false);
                    selector.ActiveProcessor(core).Resume();
                }
            });
            Assert.AreEqual(3u, otp.ReadDoubleWord(0x15c));
            for(var core = 0; core < 2; core++)
            {
                Assert.AreEqual((uint)core, (uint)riscv[core].GetRegister(6), "SIO CPUID uses physical PE identity");
                Assert.AreEqual((uint)core, (uint)riscv[core].GetRegister(7), "mhartid uses physical PE identity");
                Assert.Greater(riscv[core].ExecutedInstructions, 0u, State());
                Assert.AreEqual(0u, arms[core].ExecutedInstructions);
                Assert.AreSame(riscv[core], selector.ActiveProcessor(core));
                Assert.AreEqual(core + 2, riscv[core].Slot);
            }
            otp.WriteDoubleWord(0x158, 0);
            Dispatch(() => {
                for(var core = 0; core < 2; core++)
                {
                    selector.SetPowerSequencerResetHold(core, true);
                    selector.ResetProcessor(core);
                }
            });
            Assert.AreEqual(3u, otp.ReadDoubleWord(0x15c));
            Dispatch(() => {
                for(var core = 0; core < 2; core++)
                {
                    selector.SetPowerSequencerResetHold(core, false);
                    selector.ActiveProcessor(core).Resume();
                }
            });
            Assert.AreEqual(0u, otp.ReadDoubleWord(0x15c));
            for(var core = 0; core < 2; core++)
            {
                Assert.Greater(arms[core].ExecutedInstructions, 0u);
                Assert.AreEqual(0u, riscv[core].ExecutedInstructions);
                Assert.AreSame(arms[core], selector.ActiveProcessor(core));
            }
        }

        [Test]
        public void ShouldKeepIndependentRunRescueAndArchitectureHolds()
        {
            BuildEngines();
            Advance();
            var before0 = arms[0].ExecutedInstructions;
            var before1 = arms[1].ExecutedInstructions;
            Dispatch(() => {
                selector.SetRunResetHold(true);
                selector.SetRescueResetHold(true);
                before0 = arms[0].ExecutedInstructions;
                before1 = arms[1].ExecutedInstructions;
                ResumeEngines();
            });
            Assert.AreEqual(before0, arms[0].ExecutedInstructions);
            Assert.AreEqual(before1, arms[1].ExecutedInstructions);
            foreach(var cpu in riscv) Assert.AreEqual(0u, cpu.ExecutedInstructions);
            Dispatch(() => { selector.SetRunResetHold(false); ResumeEngines(); });
            Assert.AreEqual(before0, arms[0].ExecutedInstructions);
            Assert.AreEqual(before1, arms[1].ExecutedInstructions);
            Dispatch(() => { selector.SetRescueResetHold(false); ResumeEngines(); });
            Assert.Greater(arms[0].ExecutedInstructions, before0, State());
            Assert.Greater(arms[1].ExecutedInstructions, before1);
            foreach(var cpu in riscv) Assert.AreEqual(0u, cpu.ExecutedInstructions);
        }

        [Test]
        public void ShouldReleasePowerSequencerHoldsOnAChipReset()
        {
            BuildEngines();
            Advance();
            Dispatch(() => {
                for(var core = 0; core < 2; core++)
                {
                    selector.SetPowerSequencerResetHold(core, true);
                    selector.ResetProcessor(core);
                }
            });
            foreach(var cpu in arms) Assert.AreEqual(0u, cpu.ExecutedInstructions);
            Dispatch(() => { selector.Reset(); ResumeEngines(); });
            foreach(var cpu in arms) Assert.Greater(cpu.ExecutedInstructions, 0u);
            foreach(var cpu in riscv) Assert.AreEqual(0u, cpu.ExecutedInstructions);
        }

        [Test]
        public void ShouldPreserveTheArchitectureRequestAcrossProcessorColdReset()
        {
            BuildEngines();
            Advance();
            Dispatch(() => {
                otp.WriteDoubleWord(0x158, 3);
                psm.WriteDoubleWord(8, 1); // PROC_COLD and its later dependent domains.
                psm.ResetFromWatchdog(watchdog);
            });
            Assert.AreEqual(3u, otp.ReadDoubleWord(0x158));
            Assert.AreEqual(3u, otp.ReadDoubleWord(0x15c));
            foreach(var cpu in arms) Assert.AreEqual(0u, cpu.ExecutedInstructions);
            foreach(var cpu in riscv) Assert.Greater(cpu.ExecutedInstructions, 0u);
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ShouldPreserveAnExplicitHostPauseAcrossAWatchdogArchitectureSwitch(bool pauseAfterRequest, bool processorCold)
        {
            BuildEngines();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var entered = new ManualResetEventSlim())
            using(var release = new ManualResetEventSlim())
            using(var completed = new ManualResetEventSlim())
            {
                emulation.StartAll();
                try
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => arms[0].ExecutedInstructions > 0 && arms[1].ExecutedInstructions > 0,
                        TimeSpan.FromSeconds(5)));
                    if(pauseAfterRequest)
                    {
                        machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => {
                            entered.Set();
                            if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Boundary was not released");
                        });
                        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                    }
                    else
                    {
                        TestContext.Progress.WriteLine("pause: before early pause");
                        arms[0].Pause();
                        arms[0].TimeHandle.Enabled = false;
                    }
                    TestContext.Progress.WriteLine("pause: scheduling reset");
                    otp.WriteDoubleWord(0x158, 1);
                    psm.WriteDoubleWord(8, processorCold ? 1u : 1u << 23);
                    psm.ResetFromWatchdog(watchdog);
                    if(pauseAfterRequest)
                    {
                        arms[0].Pause();
                        arms[0].TimeHandle.Enabled = false;
                    }
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => completed.Set());
                    release.Set();
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                    TestContext.Progress.WriteLine("pause: callback completed");
                    Assert.IsTrue(SpinWait.SpinUntil(() => ReferenceEquals(riscv[0], selector.ActiveProcessor(0)), TimeSpan.FromSeconds(5)));
                    Assert.AreSame(riscv[0], selector.ActiveProcessor(0));
                    Assert.IsTrue(riscv[0].IsPaused);
                    Assert.IsFalse(riscv[0].TimeHandle.Enabled);
                    Assert.AreEqual(0u, riscv[0].ExecutedInstructions);
                    Assert.AreEqual(0u, arms[0].ExecutedInstructions);
                    TestContext.Progress.WriteLine("pause: restoring explicit host resume");
                    riscv[0].TimeHandle.Enabled = true;
                    riscv[0].TimeHandle.DeferredEnabled = true;
                    riscv[0].Resume();
                    Assert.IsTrue(SpinWait.SpinUntil(() => riscv[0].ExecutedInstructions > 0, TimeSpan.FromSeconds(5)));
                    Assert.AreEqual(0u, arms[0].ExecutedInstructions);
                }
                finally { TestContext.Progress.WriteLine("pause: cleanup"); release.Set(); emulation.PauseAll(); }
            }
        }

        [Test]
        public void ShouldRouteMixedSocketSioAndExternalInterruptsByPhysicalIdentity()
        {
            BuildEngines();
            ram.WriteDoubleWord(0x20c, 0x00080537); // lui a0,0x80
            ram.WriteDoubleWord(0x210, 0x00250513); // IRQ35, indexed bank2
            ram.WriteDoubleWord(0x214, 0xbe051073); // csrw meiea,a0
            ram.WriteDoubleWord(0x218, 0x80000593); // li a1,-2048
            ram.WriteDoubleWord(0x21c, 0x30459073); // csrw mie,a1
            ram.WriteDoubleWord(0x220, 0x30046073); // csrsi mstatus,8
            ram.WriteDoubleWord(0x224, 0x10500073);
            ram.WriteDoubleWord(0x228, 0x10500073); // Valid return PC after the first WFI.
            ram.WriteDoubleWord(0x300, 0xbe053073); // csrc meiea,a0
            ram.WriteDoubleWord(0x304, 0x0002ae03); // lw t3,CPUID(t0)
            ram.WriteDoubleWord(0x308, 0x001e8e93); // addi t4,t4,1
            ram.WriteDoubleWord(0x30c, 0x30200073); // mret
            Advance();
            SwitchCore1();
            Assert.AreSame(arms[0], selector.ActiveProcessor(0));
            Assert.AreSame(riscv[1], selector.ActiveProcessor(1));
            Assert.Greater(arms[0].ExecutedInstructions, 0u);
            Assert.AreEqual(0u, arms[1].ExecutedInstructions);
            Assert.AreEqual(0u, riscv[0].ExecutedInstructions);
            Assert.AreEqual(1u, (uint)riscv[1].GetRegister(6));
            Assert.AreEqual(1u, (uint)riscv[1].GetRegister(7));
            riscv[1].MTVEC = 0x20000300;
            riscv[1].OnGPIO(35, true);
            Advance();
            Assert.AreEqual(1u, (uint)riscv[1].GetRegister(28), "IRQ handler reads physical PE1 SIO bank");
            Assert.AreEqual(1u, (uint)riscv[1].GetRegister(29));
            Assert.AreEqual(0x8000000bu, (uint)riscv[1].MCAUSE);
            Assert.AreEqual(0u, riscv[0].ExecutedInstructions);
            Assert.AreEqual(0u, arms[1].ExecutedInstructions);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ShouldDeliverGuestEventsAcrossArchitectures(bool fromArm)
        {
            BuildEngines();
            vectors.WriteDoubleWord(0x100, fromArm ? 0xbf40bf20u : 0x3001bf20u);
            vectors.WriteDoubleWord(0x104, 0xbf00bf30); // wfi;nop
            ram.WriteDoubleWord(0x200, 0x00002033); // h3.block
            ram.WriteDoubleWord(0x204, fromArm ? 0x00100793u : 0x00102033u); // li a5,1 / h3.unblock
            ram.WriteDoubleWord(0x208, 0x10500073);
            Advance();
            SwitchCore1();
            Assert.AreEqual(0u, (uint)riscv[1].GetRegister(15));
            Assert.AreEqual(0u, (uint)arms[0].GetRegister(0));
            if(fromArm) arms[0].SetEventFlag();
            else riscv[1].ReceiveSystemEvent();
            Advance();
            if(fromArm) Assert.AreEqual(1u, (uint)riscv[1].GetRegister(15));
            else Assert.AreEqual(1u, (uint)arms[0].GetRegister(0));
            Assert.AreEqual(0u, riscv[0].ExecutedInstructions);
            Assert.AreEqual(0u, arms[1].ExecutedInstructions);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldKeepAnExcludedPhysicalProcessorAndItsAlternativeEngineUnchanged(bool retainSelector)
        {
            BuildEngines();
            Advance();
            SwitchCore1();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var completed = new ManualResetEventSlim())
            {
                emulation.StartAll();
                try
                {
                    riscv[1].Pause();
                    riscv[1].TimeHandle.Enabled = false;
                    riscv[1].SetRegister(15, 0x12345678);
                    arms[1].SetRegister(0, 0x87654321);
                    var pc = riscv[1].PC;
                    var instructions = riscv[1].ExecutedInstructions;
                    otp.WriteDoubleWord(0x158, 0); // Requests an architecture different from retained PE1.
                    machine.RequestResetInSafeState(postReset: () => completed.Set(),
                        unresetable: retainSelector ? new IPeripheral[] {riscv[1], selector} : new IPeripheral[] {riscv[1]});
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(riscv[1], selector.ActiveProcessor(1));
                    Assert.AreEqual(2u, otp.ReadDoubleWord(0x15c));
                    Assert.AreEqual(0u, otp.ReadDoubleWord(0x158));
                    Assert.AreEqual(0x12345678u, (uint)riscv[1].GetRegister(15));
                    Assert.AreEqual(0x87654321u, (uint)arms[1].GetRegister(0));
                    Assert.AreEqual(pc, riscv[1].PC);
                    Assert.AreEqual(instructions, riscv[1].ExecutedInstructions);
                    Assert.IsTrue(riscv[1].IsPaused);
                    Assert.IsFalse(riscv[1].TimeHandle.Enabled);
                }
                finally { emulation.PauseAll(); }
            }
        }

        [Test]
        public void ShouldRejectOverlappingResetControllerOwnershipBeforeQueuingAReset()
        {
            BuildEngines();
            var overlapping = new OverlappingController(arms[0]);
            machine.RegisterAsAChildOf(machine.SystemBus, overlapping, Antmicro.Renode.Core.Structure.NullRegistrationPoint.Instance);
            arms[0].SetRegister(0, 0x12345678);
            Assert.Throws<RecoverableException>(() => machine.RequestResetInSafeState());
            Assert.AreEqual(0x12345678u, (uint)arms[0].GetRegister(0));
        }

        private sealed class OverlappingController : IPeripheral, ICPUResetController
        {
            public OverlappingController(ICPU cpu) { this.cpu = cpu; }
            public bool Contains(ICPU candidate) => ReferenceEquals(candidate, cpu);
            public bool IsActive(ICPU candidate) => Contains(candidate);
            public bool SameProcessor(ICPU first, ICPU second) => Contains(first) && Contains(second);
            public ICPU GetResetTarget(ICPU previousCPU) => cpu;
            public void HoldInReset(System.Collections.Generic.IEnumerable<ICPU> affectedCPUs) => Assert.Fail("Overlapping controller was accepted");
            public void ReleaseReset() => Assert.Fail("Overlapping controller was accepted");
            public void Reset() { }
            private readonly ICPU cpu;
        }

        [Test]
        public void ShouldResolveTheActiveEngineAtTheWatchdogResetBoundary()
        {
            BuildEngines();
            ram.WriteDoubleWord(0x20c, 0x00178793); // addi a5,a5,1
            ram.WriteDoubleWord(0x210, 0xffdff06f); // j -4
            var emulation = EmulationManager.Instance.CurrentEmulation;
            using(var entered = new ManualResetEventSlim())
            using(var release = new ManualResetEventSlim())
            using(var completed = new ManualResetEventSlim())
            {
                emulation.StartAll();
                try
                {
                    Assert.IsTrue(SpinWait.SpinUntil(() => arms[0].ExecutedInstructions > 0 && arms[1].ExecutedInstructions > 0,
                        TimeSpan.FromSeconds(5)));
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => {
                        entered.Set();
                        if(!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Boundary was not released");
                    });
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => {
                        otp.WriteDoubleWord(0x158, 1);
                        selector.SetPowerSequencerResetHold(0, true);
                        selector.ResetProcessor(0);
                        selector.SetPowerSequencerResetHold(0, false);
                        selector.ActiveProcessor(0).Resume();
                    });
                    psm.WriteDoubleWord(8, 1u << 23);
                    psm.ResetFromWatchdog(watchdog); // Request still sees Arm; boundary must resolve RV.
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => completed.Set());
                    release.Set();
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(riscv[0], selector.ActiveProcessor(0));
                    var count = riscv[0].ExecutedInstructions;
                    Assert.IsTrue(SpinWait.SpinUntil(() => riscv[0].ExecutedInstructions > count + 100, TimeSpan.FromSeconds(5)), State());
                    Assert.AreEqual(0u, arms[0].ExecutedInstructions);
                    Assert.AreEqual(0u, riscv[1].ExecutedInstructions);
                }
                finally { release.Set(); emulation.PauseAll(); }
            }
        }

        private void SwitchCore1()
        {
            Dispatch(() => {
                otp.WriteDoubleWord(0x158, 2);
                psm.WriteDoubleWord(0x2004, 1u << 24);
            });
            Dispatch(() => psm.WriteDoubleWord(0x3004, 1u << 24));
        }

        private void Dispatch(Action action)
        {
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => action());
            Advance(); // Dispatch occurs at the end of this synchronization quantum.
            Advance(); // Observe a full grant after the physical transition.

        }

        private string State() => string.Join("; ", arms.Cast<TranslationCPU>().Concat(riscv).Select(c =>
            $"{c.Model}@{c.Slot} pc={c.PC} count={c.ExecutedInstructions} paused={c.IsPaused} held={c.HasAnyHaltingCondition}"));

        private void ResumeEngines()
        {
            foreach(var cpu in arms) cpu.Resume();
            foreach(var cpu in riscv) cpu.Resume();
        }

        private void BuildEngines()
        {
            vectors = new MappedMemory(machine, 0x1000);
            machine.SystemBus.Register(vectors, new BusPointRegistration(0));
            vectors.WriteDoubleWord(0, 0x20000ff0);
            vectors.WriteDoubleWord(4, 0x101);
            vectors.WriteDoubleWord(0x100, 0xe7febf00); // Arm nop; branch to self.
            ram = new MappedMemory(machine, 0x1000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            ram.WriteDoubleWord(0x200, 0xd00002b7); // lui t0, SIO
            ram.WriteDoubleWord(0x204, 0x0002a303); // lw t1,CPUID(t0)
            ram.WriteDoubleWord(0x208, 0xf14023f3); // csrr t2,mhartid
            ram.WriteDoubleWord(0x20c, 0x10500073); // wfi
            machine.SystemBus.Register(new RP2350_SIO(machine), new BusPointRegistration(0xd0000000));
            var nvics = new[] {new NVIC(machine), new NVIC(machine)};
            arms = new CortexM[2];
            riscv = new RP2350_Hazard3[2];
            for(var core = 0; core < 2; core++)
            {
                arms[core] = new CortexM("cortex-m33-rp2350", machine, nvics[core], cpuId: (uint)core, enableTrustZone: true);
                machine.SystemBus.Register(arms[core], new CPURegistrationPoint(core));
                machine.SetLocalName(arms[core], "arm" + core);
                machine.SystemBus.Register(nvics[core], new BusPointRegistration(0xe000e000, cpu: arms[core]));
                nvics[core].IRQ.Connect(arms[core], 0);
                riscv[core] = new RP2350_Hazard3(machine, (uint)core);
                machine.SystemBus.Register(riscv[core], new CPURegistrationPoint(core + 2));
                machine.SetLocalName(riscv[core], "rv" + core);
                Assert.AreEqual(0x7dfcu, (uint)riscv[core].ResetVector);
                riscv[core].ResetVector = 0x20000200;
                riscv[core].Reset();
            }
            psm = new RP2350_PSM(machine, arms[0], arms[1], nvics[0], nvics[1]);
            machine.SystemBus.Register(psm, new BusPointRegistration(0x40018000));
            machine.SystemBus.Register(otp, new BusPointRegistration(0x40120000));
            var xosc = new RP2_XOSC(machine, rp2350: true);
            var clocks = new RP2350_Clocks(machine, xosc, new RP2350_ROSC(machine),
                new RP2_PLL(machine, xosc, rp2350: true), new RP2_PLL(machine, xosc, rp2350: true));
            var ticks = new RP2350_Ticks(machine, clocks);
            watchdog = new RP2350_Watchdog(machine, ticks, psm);
            machine.SystemBus.Register(watchdog, new BusPointRegistration(0x400d8000));
            selector = new RP2350_ArchitectureSelector(machine, arms[0], arms[1], riscv[0], riscv[1], nvics[0], nvics[1], otp, psm);
            machine.RegisterAsAChildOf(machine.SystemBus, selector, Antmicro.Renode.Core.Structure.NullRegistrationPoint.Instance);
            machine.PostCreationActions();
        }

        private void LoadFuses(uint crit0, uint crit1)
        {
            var bytes = new byte[4096 * 4];
            for(var copy = 0; copy < 3; copy++)
            {
                Array.Copy(BitConverter.GetBytes(crit0), 0, bytes, (0x38 + copy) * 4, 4);
                Array.Copy(BitConverter.GetBytes(crit1), 0, bytes, (0x40 + copy) * 4, 4);
            }
            var path = Path.GetTempFileName();
            try { File.WriteAllBytes(path, bytes); otp.LoadRawImage(path); }
            finally { File.Delete(path); }
        }

        private void Advance() => EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(10));
        private Machine machine;
        private RP2350_OTP otp;
        private RP2350_ArchitectureSelector selector;
        private MappedMemory vectors;
        private MappedMemory ram;
        private RP2350_PSM psm;
        private RP2350_Watchdog watchdog;
        private CortexM[] arms;
        private RP2350_Hazard3[] riscv;
    }
}
