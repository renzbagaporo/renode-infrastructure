//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
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
    public class RP2350_PSMWatchdogTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "psm_watchdog");
            nvic0 = new NVIC(machine);
            var nvic1 = new NVIC(machine);
            core0 = new CortexM("cortex-m33-rp2350", machine, nvic0, enableTrustZone: true);
            core1 = new CortexM("cortex-m33-rp2350", machine, nvic1, cpuId: 1, enableTrustZone: true);
            machine.SystemBus.Register(core0, new CPURegistrationPoint(0));
            machine.SystemBus.Register(core1, new CPURegistrationPoint(1));
            machine.SetLocalName(core0, "cpu0");
            machine.SetLocalName(core1, "cpu1");
            machine.SystemBus.Register(nvic0, new BusPointRegistration(0xe000e000, cpu: core0));
            machine.SystemBus.Register(nvic1, new BusPointRegistration(0xe000e000, cpu: core1));
            nvic0.IRQ.Connect(core0, 0);
            nvic1.IRQ.Connect(core1, 0);
            var rom = new MappedMemory(machine, 0x4000);
            machine.SystemBus.Register(rom, new BusPointRegistration(0));
            rom.WriteDoubleWord(0, 0x20001000);
            rom.WriteDoubleWord(4, 0x101);
            rom.WriteWord(0x100, 0xe7fe);
            ram = new MappedMemory(machine, 0x2000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            core0.IsHalted = core1.IsHalted = true;
            psm = new RP2350_PSM(machine, core0, core1, nvic0, nvic1);
            machine.SystemBus.Register(psm, new BusPointRegistration(0x40018000));
            var xosc = new RP2_XOSC(machine, rp2350: true);
            clocks = new RP2350_Clocks(machine, xosc, new RP2350_ROSC(machine),
                new RP2_PLL(machine, xosc, rp2350: true), new RP2_PLL(machine, xosc, rp2350: true));
            ticks = new RP2350_Ticks(machine, clocks);
            watchdog = new RP2350_Watchdog(machine, ticks, psm);
            resets = new RP2_Resets(machine, rp2350: true);
            psm.Register(resets, new NumberRegistrationPoint<int>(4));
            probes = new DomainProbe[23];
            for(var domain = 0; domain < probes.Length; domain++)
            {
                probes[domain] = new DomainProbe(() =>
                {
                    Assert.IsFalse(Monitor.IsEntered(psm), "Domain reset owns PSM state lock");
                    Assert.IsFalse(Monitor.IsEntered(watchdog), "Domain reset owns watchdog state lock");
                });
                psm.Register(probes[domain], new NumberRegistrationPoint<int>(domain));
            }
            machine.PostCreationActions();
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        // Figure 30 sequence, with the SRAM and processor peer interpretation
        // stated by the model. Explicit masks keep the expected graph independent
        // of the implementation's selection-expansion algorithm.
        [TestCase(1, 0x1fffffeu)] [TestCase(2, 0x1fffffcu)]
        [TestCase(3, 0x1fffff8u)] [TestCase(4, 0x1fffff0u)]
        [TestCase(5, 0x1ffffe0u)] [TestCase(6, 0x1ffffc0u)]
        [TestCase(7, 0x1ffff80u)] [TestCase(8, 0x1ffff00u)]
        [TestCase(9, 0x1fffe00u)] [TestCase(10, 0x1f00400u)]
        [TestCase(11, 0x1f00800u)] [TestCase(12, 0x1f01000u)]
        [TestCase(13, 0x1f02000u)] [TestCase(14, 0x1f04000u)]
        [TestCase(15, 0x1f08000u)] [TestCase(16, 0x1f10000u)]
        [TestCase(17, 0x1f20000u)] [TestCase(18, 0x1f40000u)]
        [TestCase(19, 0x1f80000u)] [TestCase(20, 0x1f00000u)]
        [TestCase(21, 0x1e00000u)] [TestCase(22, 0x1c00000u)]
        [TestCase(23, 0x0800000u)] [TestCase(24, 0x1000000u)]
        public void ShouldRestartOnlyTheSelectedStageAndItsSequencedDependants(int selected, uint expected)
        {
            core0.SetRegister(0, 0x1234);
            core1.SetRegister(0, 0x5678);
            psm.WriteDoubleWord(8, 1u << selected);
            Trigger();
            for(var domain = 0; domain < probes.Length; domain++)
            {
                Assert.AreEqual((expected & (1u << domain)) != 0 ? 1 : 0, probes[domain].Count, $"Selected {selected}, domain {domain}");
            }
            Assert.AreEqual((expected & (1u << 23)) == 0 ? 0x1234u : 0u, (uint)core0.GetRegister(0));
            Assert.AreEqual((expected & (1u << 24)) == 0 ? 0x5678u : 0u, (uint)core1.GetRegister(0));
            Assert.AreEqual(1u << selected, psm.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldLeaveSystemDomainsUntouchedWithNoSelection()
        {
            core0.SetRegister(0, 0x1234);
            core1.SetRegister(0, 0x5678);
            Trigger();
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
            Assert.AreEqual(0x1234u, (uint)core0.GetRegister(0));
            Assert.AreEqual(0x5678u, (uint)core1.GetRegister(0));
        }

        [Test]
        public void ShouldResetBootSecurityFlopsAndRetainPhysicalMemoryAndWatchdogState()
        {
            var bootram = new RP2350_BootRAM(machine);
            psm.Register(bootram, new NumberRegistrationPoint<int>(9));
            bootram.WriteDoubleWord(0x20, 0xabcdef12);
            bootram.WriteDoubleWord(0x800, 0x11);
            bootram.WriteDoubleWord(0x804, 0x22);
            Assert.AreEqual(4u, bootram.ReadDoubleWord(0x814));
            Assert.AreEqual(0xfbu, bootram.ReadDoubleWord(0x808));
            ram.WriteDoubleWord(0x100, 0x87654321);
            watchdog.WriteDoubleWord(0x1c, 0x6ab73121);
            psm.WriteDoubleWord(8, 1u << 9);
            Trigger();
            Assert.AreEqual(0u, bootram.ReadDoubleWord(0x800));
            Assert.AreEqual(0u, bootram.ReadDoubleWord(0x804));
            Assert.AreEqual(0xffu, bootram.ReadDoubleWord(0x808));
            Assert.AreEqual(0xabcdef12u, bootram.ReadDoubleWord(0x20));
            Assert.AreEqual(0x87654321u, ram.ReadDoubleWord(0x100));
            Assert.AreEqual(0x6ab73121u, watchdog.ReadDoubleWord(0x1c));
            Assert.AreEqual(2u, watchdog.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldResetClockAndTickConfigurationWithoutRearmingTheTriggeredWatchdog()
        {
            psm.Register(clocks, new NumberRegistrationPoint<int>(5));
            psm.Register(ticks, new NumberRegistrationPoint<int>(5));
            clocks.WriteDoubleWord(0x30, 2);
            clocks.WriteDoubleWord(0x34, 0x30000);
            clocks.WriteDoubleWord(0x40, 0x28000);
            ticks.WriteDoubleWord(0x34, 12);
            ticks.WriteDoubleWord(0x30, 1);
            Assert.AreEqual(0x30000u, clocks.ReadDoubleWord(0x34));
            Assert.AreEqual(12u, ticks.ReadDoubleWord(0x34));
            psm.WriteDoubleWord(8, 1u << 5);
            Trigger();
            Assert.AreEqual(0u, clocks.ReadDoubleWord(0x30));
            Assert.AreEqual(0x10000u, clocks.ReadDoubleWord(0x34));
            Assert.AreEqual(0x10000u, clocks.ReadDoubleWord(0x40));
            Assert.AreEqual(0u, ticks.ReadDoubleWord(0x30));
            Assert.AreEqual(0u, ticks.ReadDoubleWord(0x34));
            Assert.AreEqual(0ul, ticks.GetFrequency(4));
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            Assert.AreEqual(1, probes[5].Count);
        }

        [Test]
        public void ShouldPreserveClocksAndBootSecurityStateForAProcessorOnlyReset()
        {
            var bootram = new RP2350_BootRAM(machine);
            psm.Register(bootram, new NumberRegistrationPoint<int>(9));
            psm.Register(clocks, new NumberRegistrationPoint<int>(5));
            psm.Register(ticks, new NumberRegistrationPoint<int>(5));
            bootram.WriteDoubleWord(0x800, 1);
            clocks.WriteDoubleWord(0x34, 0x30000);
            ticks.WriteDoubleWord(0x34, 12);
            psm.WriteDoubleWord(8, 1u << 23);
            Trigger();
            Assert.AreEqual(1u, bootram.ReadDoubleWord(0x800));
            Assert.AreEqual(0x30000u, clocks.ReadDoubleWord(0x34));
            Assert.AreEqual(12u, ticks.ReadDoubleWord(0x34));
        }

        [Test]
        public void ShouldPulseIndependentSubsystemWDSELWithoutResettingTheController()
        {
            var selected = new ResetProbe(machine);
            var peer = new ResetProbe(machine);
            resets.Register(selected, new NumberRegistrationPoint<int>(26));
            resets.Register(peer, new NumberRegistrationPoint<int>(27));
            resets.WriteDoubleWord(0, 0);
            selected.WriteDoubleWord(0, 0x1234);
            peer.WriteDoubleWord(0, 0x5678);
            resets.WriteDoubleWord(4, 1u << 26);
            Trigger();
            Assert.AreEqual(0u, selected.ReadDoubleWord(0));
            Assert.AreEqual(0x5678u, peer.ReadDoubleWord(0));
            Assert.AreEqual(0u, resets.ReadDoubleWord(0));
            Assert.AreEqual(1u << 26, resets.ReadDoubleWord(4));
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
        }

        [Test]
        public void ShouldRestoreAllSubsystemResetHoldsWhenTheMasterResetStageRestarts()
        {
            var selected = new ResetProbe(machine);
            resets.Register(selected, new NumberRegistrationPoint<int>(26));
            resets.WriteDoubleWord(0, 0);
            resets.WriteDoubleWord(4, 1u << 26);
            selected.WriteDoubleWord(0, 0x1234);
            psm.WriteDoubleWord(8, 1u << 4);
            Trigger();
            Assert.IsTrue(selected.ResetHeld);
            Assert.AreEqual(0u, selected.ReadDoubleWord(0));
            Assert.AreEqual(0x1fffffffu, resets.ReadDoubleWord(0));
            Assert.AreEqual(0u, resets.ReadDoubleWord(4));
        }

        [Test]
        public void ShouldRestoreAccessControllerConfigurationAndLocks()
        {
            var access = new RP2350_AccessControl(machine);
            psm.Register(access, new NumberRegistrationPoint<int>(22));
            access.WriteDoubleWord(0xc, 0xabcdef12);
            access.WriteDoubleWord(0, 0xacce0008);
            Assert.AreEqual(0xabcdef12u, access.ReadDoubleWord(0xc));
            psm.WriteDoubleWord(8, 1u << 22);
            Trigger();
            Assert.AreEqual(0u, access.ReadDoubleWord(0xc));
            Assert.AreEqual(4u, access.ReadDoubleWord(0));
        }

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(2, false)]
        [TestCase(2, true)]
        [TestCase(1, true)]
        public void ShouldKeepAndAutomaticallyReleaseIndependentProcessorExecutionHolds(int hold, bool watchdogReset)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            core0.IsHalted = core1.IsHalted = false;
            core0.Reset();
            core1.Reset();
            core0.SetRegister(0, 0x1234);
            var blocks = 0;
            core0.SetHookAtBlockBegin((_, __) => Interlocked.Increment(ref blocks));
            emulation.StartAll();
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => core0.ExecutedInstructions > 0 && core1.ExecutedInstructions > 0,
                    TimeSpan.FromSeconds(2)), "Both native processors must execute before the hold");
                if(hold == 0) core0.Clocked = false;
                else if(hold == 1) core0.CpuWaitSignal.Set();
                else core0.SetPowerSequencerResetHold(true);
                // CpuWait is sampled on reset, whereas clock/reset holds stop
                // an already running processor. Its case always requests reset.
                var beforeResetBlocks = Volatile.Read(ref blocks);
                if(watchdogReset)
                {
                    psm.WriteDoubleWord(8, 1u << 23);
                    watchdog.WriteDoubleWord(0, 0x80000000);
                }
                using(var completed = new ManualResetEventSlim())
                {
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => completed.Set());
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(2)));
                }
                Assert.IsTrue(core0.HasAnyHaltingCondition);
                Assert.AreEqual(watchdogReset ? 0u : 0x1234u, (uint)core0.GetRegister(0));
                if(hold != 1) Assert.AreEqual(beforeResetBlocks, Volatile.Read(ref blocks));
                var heldBlocks = Volatile.Read(ref blocks);
                var instructions = core0.ExecutedInstructions;
                Thread.Sleep(20);
                Assert.AreEqual(heldBlocks, Volatile.Read(ref blocks));
                Assert.AreEqual(instructions, core0.ExecutedInstructions);
                if(hold == 0) Assert.IsFalse(core0.Clocked);
                if(hold == 1) Assert.IsTrue(core0.CpuWaitSignal.IsSet);
                if(hold == 0) core0.Clocked = true;
                else if(hold == 1) core0.CpuWaitSignal.Unset();
                else core0.SetPowerSequencerResetHold(false);
                Assert.IsTrue(SpinWait.SpinUntil(() => Volatile.Read(ref blocks) > heldBlocks, TimeSpan.FromSeconds(2)),
                    "Releasing the independent hold must automatically restore native execution");
                Assert.IsFalse(core0.HasAnyHaltingCondition);
                Assert.IsFalse(core0.IsPaused);
            }
            finally { emulation.PauseAll(); }
        }

        [Test]
        public void ShouldRespectProcessorWarmResetDebugCatch()
        {
            core0.EnableMemoryMappedDebug = true;
            nvic0.WriteDoubleWord(0xdf0, 0xa05f0003);
            Assert.IsTrue(SpinWait.SpinUntil(() => core0.HaltedByDebugger, TimeSpan.FromSeconds(2)));
            nvic0.WriteDoubleWord(0xdfc, 1);
            psm.WriteDoubleWord(8, 1u << 23);
            Trigger();
            Assert.IsTrue(core0.HaltedByDebugger);
            Assert.IsTrue(core0.HasAnyHaltingCondition);
            Assert.AreEqual(0x100u, (uint)core0.PC);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ShouldKeepAnIndividuallyHostPausedProcessorDetachedWhileEitherCoreResets(int selectedCore)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            core0.IsHalted = core1.IsHalted = false;
            core0.Reset();
            core1.Reset();
            core1.SetRegister(0, 0x5678);
            var blocks = 0;
            core0.SetHookAtBlockBegin((_, __) => Interlocked.Increment(ref blocks));
            emulation.StartAll();
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => core0.ExecutedInstructions > 0 && core1.ExecutedInstructions > 0, TimeSpan.FromSeconds(2)));
                core0.Pause();
                // Bare Pause keeps a time barrier. Detach the host-paused sink
                // so the peer can advance to the synchronization callback.
                core0.TimeHandle.Enabled = false;
                core0.SetRegister(0, 0x1234);
                Assert.IsTrue(core0.IsPaused);
                Assert.IsFalse(machine.IsPaused);
                var pausedBlocks = Volatile.Read(ref blocks);
                var cycles = core0.ElapsedCycles;
                psm.WriteDoubleWord(8, 1u << (23 + selectedCore));
                using(var completed = new ManualResetEventSlim())
                {
                    watchdog.WriteDoubleWord(0, 0x80000000);
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => completed.Set());
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(2)));
                }
                var target = selectedCore == 0 ? core0 : core1;
                Assert.AreEqual(0u, (uint)target.GetRegister(0));
                Assert.IsTrue(core0.IsPaused);
                var instructions = core0.ExecutedInstructions;
                Thread.Sleep(20);
                Assert.IsTrue(core0.IsPaused);
                Assert.AreEqual(instructions, core0.ExecutedInstructions);
                Assert.AreEqual(pausedBlocks, Volatile.Read(ref blocks));
                Assert.AreEqual(cycles, core0.ElapsedCycles);
                Assert.IsFalse(core0.TimeHandle.Enabled);
            }
            finally { emulation.PauseAll(); }
        }

        [Test]
        public void ShouldCancelTheQueuedSequenceWhenPSMResets()
        {
            psm.WriteDoubleWord(8, 1u << 5);
            watchdog.WriteDoubleWord(0, 0x80000000);
            psm.Reset();
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(1));
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
        }

        [TestCase(-1)] [TestCase(23)] [TestCase(25)]
        public void ShouldRejectInvalidPeripheralDomainRegistrations(int domain) =>
            Assert.Throws<RegistrationException>(() => psm.Register(new DomainProbe(null), new NumberRegistrationPoint<int>(domain)));

        private void Trigger()
        {
            watchdog.WriteDoubleWord(0, 0x80000000);
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(1));
        }

        private sealed class DomainProbe : IPeripheral
        {
            public DomainProbe(Action callback) { this.callback = callback; }
            public void Reset() { callback?.Invoke(); Count++; }
            public int Count { get; private set; }
            private readonly Action callback;
        }

        private sealed class ResetProbe : RP2APBPeripheral
        {
            public ResetProbe(IMachine machine) : base(machine) { }
            public override void Reset() => value = 0;
            protected override uint ReadRegister(long offset) => value;
            protected override void WriteRegister(long offset, uint newValue, AtomicOperation operation) => value = ApplyOperation(value, newValue, operation);
            private uint value;
        }

        private Machine machine;
        private CortexM core0;
        private CortexM core1;
        private NVIC nvic0;
        private RP2350_PSM psm;
        private RP2350_Clocks clocks;
        private RP2350_Ticks ticks;
        private RP2350_Watchdog watchdog;
        private RP2_Resets resets;
        private MappedMemory ram;
        private DomainProbe[] probes;
    }
}
