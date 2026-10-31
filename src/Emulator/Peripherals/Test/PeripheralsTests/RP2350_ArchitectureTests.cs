// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.IO;
using System.Linq;
using Antmicro.Renode.Core;
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
            var vectors = new MappedMemory(machine, 0x1000);
            machine.SystemBus.Register(vectors, new BusPointRegistration(0));
            vectors.WriteDoubleWord(0, 0x20000ff0);
            vectors.WriteDoubleWord(4, 0x101);
            vectors.WriteDoubleWord(0x100, 0xe7febf00); // Arm nop; branch to self.
            var ram = new MappedMemory(machine, 0x1000);
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
            selector = new RP2350_ArchitectureSelector(machine, arms[0], arms[1], riscv[0], riscv[1], nvics[0], nvics[1], otp);
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
        private CortexM[] arms;
        private RP2350_Hazard3[] riscv;
    }
}
