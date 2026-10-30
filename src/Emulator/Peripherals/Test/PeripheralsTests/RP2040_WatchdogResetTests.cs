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
    public class RP2040_WatchdogResetTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "watchdog");
            nvic0 = new NVIC(machine);
            var nvic1 = new NVIC(machine);
            core0 = new CortexM("cortex-m0+", machine, nvic0);
            core1 = new CortexM("cortex-m0+", machine, nvic1, cpuId: 1);
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
            core0.InitVectorTableOffset = core1.InitVectorTableOffset = 0;
            core0.IsHalted = core1.IsHalted = true;
            psm = new RP2040_PSM(machine, core0, core1, nvic0, nvic1);
            machine.SystemBus.Register(psm, new BusPointRegistration(0x40010000));
            var xosc = new RP2_XOSC(machine);
            clocks = new RP2040_Clocks(machine, xosc, new RP2040_ROSC(machine),
                new RP2_PLL(machine, xosc), new RP2_PLL(machine, xosc));
            watchdog = new RP2040_Watchdog(machine, clocks, psm: psm);
            machine.SystemBus.Register(watchdog, new BusPointRegistration(0x40058000));
            resets = new RP2_Resets(machine);
            psm.Register(resets, new NumberRegistrationPoint<int>(3));
            probes = new DomainProbe[15];
            for(var domain = 0; domain < probes.Length; domain++)
            {
                probes[domain] = new DomainProbe(() =>
                {
                    Assert.IsFalse(Monitor.IsEntered(psm), "A child reset ran under the PSM state lock");
                    Assert.IsFalse(Monitor.IsEntered(watchdog), "A child reset ran under the watchdog state lock");
                });
                psm.Register(probes[domain], new NumberRegistrationPoint<int>(domain));
            }
            machine.PostCreationActions();
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        [Test]
        public void ShouldLeaveAllDomainsAndProcessorsUntouchedWhenNoResetIsSelected()
        {
            core0.SetRegister(0, 0x1234);
            core1.SetRegister(0, 0x5678);
            Trigger();
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
            Assert.AreEqual(0x1234u, (uint)core0.GetRegister(0));
            Assert.AreEqual(0x5678u, (uint)core1.GetRegister(0));
            Assert.AreEqual(2u, watchdog.ReadDoubleWord(8));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ShouldResetOnlyTheSelectedProcessor(int core)
        {
            core0.SetRegister(0, 0x1234);
            core1.SetRegister(0, 0x5678);
            psm.WriteDoubleWord(8, 1u << (15 + core));
            Trigger();
            Assert.AreEqual(core == 0 ? 0u : 0x1234u, (uint)core0.GetRegister(0));
            Assert.AreEqual(core == 1 ? 0u : 0x5678u, (uint)core1.GetRegister(0));
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
        }

        [Test]
        public void ShouldRestartTheClockDomainAndItsDependantsWithoutResettingOscillatorsOrWatchdog()
        {
            watchdog.WriteDoubleWord(0x1c, 0x6ab73121);
            ram.WriteDoubleWord(0x100, 0x89abcdef);
            psm.WriteDoubleWord(8, 1u << 2);
            Trigger();
            Assert.AreEqual(0, probes[0].Count);
            Assert.AreEqual(0, probes[1].Count);
            for(var domain = 2; domain < probes.Length; domain++) Assert.AreEqual(1, probes[domain].Count);
            Assert.AreEqual(0x6ab73121u, watchdog.ReadDoubleWord(0x1c));
            Assert.AreEqual(2u, watchdog.ReadDoubleWord(8));
            Assert.AreEqual(0x89abcdefu, ram.ReadDoubleWord(0x100));
        }

        [Test]
        public void ShouldRespectSequenceOrderRatherThanRegisterBitOrder()
        {
            psm.WriteDoubleWord(8, 1u << 12); // XIP precedes ROM/SRAM and bus.
            Trigger();
            foreach(var domain in new[] { 0, 1, 2, 3, 13 }) Assert.AreEqual(0, probes[domain].Count);
            foreach(var domain in new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 14 }) Assert.AreEqual(1, probes[domain].Count);
        }

        [Test]
        public void ShouldPulseIndependentSubsystemSelectionWithoutResettingTheControllerRegisters()
        {
            var selected = new ResetProbe(machine);
            var peer = new ResetProbe(machine);
            resets.Register(selected, new NumberRegistrationPoint<int>(22));
            resets.Register(peer, new NumberRegistrationPoint<int>(23));
            resets.WriteDoubleWord(0, 0);
            selected.WriteDoubleWord(0, 0x1234);
            peer.WriteDoubleWord(0, 0x5678);
            resets.WriteDoubleWord(4, 1u << 22);
            Trigger();
            Assert.AreEqual(0u, selected.ReadDoubleWord(0));
            Assert.AreEqual(0x5678u, peer.ReadDoubleWord(0));
            Assert.AreEqual(0u, resets.ReadDoubleWord(0));
            Assert.AreEqual(1u << 22, resets.ReadDoubleWord(4));
            Assert.AreEqual(0x1ffffffu, resets.ReadDoubleWord(8));
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
        }

        [Test]
        public void ShouldRetainManualSubsystemResetHoldsDuringAWatchdogPulse()
        {
            var selected = new ResetProbe(machine);
            resets.Register(selected, new NumberRegistrationPoint<int>(22));
            resets.WriteDoubleWord(4, 1u << 22);
            Trigger();
            Assert.IsTrue(selected.ResetHeld);
            Assert.AreEqual(0x1ffffffu, resets.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldRetainOnlyTheRunClockDividersDuringAClockDomainReset()
        {
            clocks.WriteDoubleWord(0x34, 0x200);
            clocks.WriteDoubleWord(0x40, 0x700);
            clocks.WriteDoubleWord(0x58, 0x300);
            Assert.AreEqual(0x200u, clocks.ReadDoubleWord(0x34));
            Assert.AreEqual(0x700u, clocks.ReadDoubleWord(0x40));
            psm.Register(clocks, new NumberRegistrationPoint<int>(2));
            psm.WriteDoubleWord(8, 1u << 2);
            Trigger();
            Assert.AreEqual(0x200u, clocks.ReadDoubleWord(0x34));
            Assert.AreEqual(0x700u, clocks.ReadDoubleWord(0x40));
            Assert.AreEqual(0x100u, clocks.ReadDoubleWord(0x58));
            clocks.Reset();
            Assert.AreEqual(0x100u, clocks.ReadDoubleWord(0x34));
            Assert.AreEqual(0x100u, clocks.ReadDoubleWord(0x40));
        }

        [TestCase(-1)]
        [TestCase(15)]
        [TestCase(17)]
        public void ShouldRejectInvalidPeripheralDomainRegistration(int domain)
        {
            Assert.Throws<RegistrationException>(() => psm.Register(new DomainProbe(null), new NumberRegistrationPoint<int>(domain)));
        }

        [Test]
        public void ShouldResetEveryRegisteredMemberOfOneDomainOnce()
        {
            var peer = new DomainProbe(null);
            psm.Register(peer, new NumberRegistrationPoint<int>(12));
            psm.WriteDoubleWord(8, 1u << 12);
            Trigger();
            Assert.AreEqual(1, probes[12].Count);
            Assert.AreEqual(1, peer.Count);
        }

        [Test]
        public void ShouldKeepAnIndependentProcessorClockGate()
        {
            core0.Clocked = false;
            psm.WriteDoubleWord(8, 1u << 15);
            Trigger();
            Assert.IsFalse(core0.Clocked);
            Assert.IsTrue(core0.HasAnyHaltingCondition);
            Assert.AreEqual(0, core0.ExecutedInstructions);
        }

        [Test]
        public void ShouldKeepAnIndependentProcessorWaitSignal()
        {
            core0.CpuWaitSignal.Set();
            psm.WriteDoubleWord(8, 1u << 15);
            Trigger();
            Assert.IsTrue(core0.CpuWaitSignal.IsSet);
            Assert.IsTrue(core0.IsHalted);
            Assert.AreEqual(0, core0.ExecutedInstructions);
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ShouldKeepAHostPausedProcessorDetachedFromTheTimeDomainPaused(int selectedCore)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            core0.IsHalted = false;
            core1.IsHalted = false;
            core0.Reset();
            core1.Reset();
            core1.SetRegister(0, 0x5678);
            var blocks = 0;
            core0.SetHookAtBlockBegin((_, __) => Interlocked.Increment(ref blocks));
            emulation.StartAll();
            try
            {
                Assert.IsTrue(SpinWait.SpinUntil(() => core0.ExecutedInstructions > 0, TimeSpan.FromSeconds(2)));
                Assert.IsTrue(SpinWait.SpinUntil(() => core1.ExecutedInstructions > 0, TimeSpan.FromSeconds(2)));
                core0.Pause();
                // A paused CPU with an enabled time handle blocks the whole
                // time domain. Detach it so the peer can reach the reset boundary.
                core0.TimeHandle.Enabled = false;
                Assert.IsFalse(machine.IsPaused);
                Assert.IsTrue(core0.IsPaused);
                core0.SetRegister(0, 0x1234);
                var pausedBlocks = Volatile.Read(ref blocks);
                var cycles = core0.ElapsedCycles;
                psm.WriteDoubleWord(8, 1u << (15 + selectedCore));
                var target = selectedCore == 0 ? core0 : core1;
                using(var completed = new ManualResetEventSlim())
                {
                    watchdog.WriteDoubleWord(0, 0x80000000);
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => completed.Set());
                    Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(2)), "The watchdog reset must complete while the machine runs");
                }
                Assert.AreEqual(0u, (uint)target.GetRegister(0));
                Assert.IsTrue(core0.IsPaused);
                var instructions = core0.ExecutedInstructions;
                Thread.Sleep(20);
                Assert.IsTrue(core0.IsPaused);
                Assert.AreEqual(instructions, core0.ExecutedInstructions);
                Assert.AreEqual(pausedBlocks, Volatile.Read(ref blocks), "No guest block may begin during temporary resume/restore");
                Assert.AreEqual(cycles, core0.ElapsedCycles, "The paused processor must execute no guest cycles");
                Assert.IsFalse(core0.TimeHandle.Enabled, "The caller's detached time handle must remain detached");
            }
            finally
            {
                emulation.PauseAll();
            }
        }

        [Test]
        public void ShouldKeepAnIndependentProcessorResetHold()
        {
            core0.SetPowerSequencerResetHold(true);
            psm.WriteDoubleWord(8, 1u << 15);
            Trigger();
            Assert.IsTrue(core0.HasAnyHaltingCondition);
            core0.SetPowerSequencerResetHold(false);
            Assert.IsFalse(core0.HasAnyHaltingCondition);
        }

        [Test]
        public void ShouldRespectDebuggerResetCatch()
        {
            core0.EnableMemoryMappedDebug = true;
            nvic0.WriteDoubleWord(0xdf0, 0xa05f0003);
            Assert.IsTrue(SpinWait.SpinUntil(() => core0.HaltedByDebugger, TimeSpan.FromSeconds(2)));
            nvic0.WriteDoubleWord(0xdfc, 1); // DEMCR.VC_CORERESET.
            psm.WriteDoubleWord(8, 1u << 15);
            Trigger();
            Assert.IsTrue(core0.HaltedByDebugger);
            Assert.IsTrue(core0.HasAnyHaltingCondition);
            Assert.AreEqual(0x100u, (uint)core0.PC);
        }

        [Test]
        public void ShouldDiscardAQueuedWatchdogResetAfterThePowerSequencerResets()
        {
            psm.WriteDoubleWord(8, 1u << 2);
            watchdog.WriteDoubleWord(0, 0x80000000);
            psm.Reset();
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(1));
            foreach(var probe in probes) Assert.AreEqual(0, probe.Count);
        }

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
        private RP2040_PSM psm;
        private RP2_Resets resets;
        private RP2040_Clocks clocks;
        private RP2040_Watchdog watchdog;
        private MappedMemory ram;
        private DomainProbe[] probes;
    }
}
