//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class CortexM_DebugTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine(createLocalTimeSource: true);
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "debug_test");
            nvic = new NVIC(machine);
            cpu = new CortexM("cortex-m33", machine, nvic, enableTrustZone: true);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
            cpu.EnableMemoryMappedDebug = true;
        }

        [TearDown]
        public void TearDown() { EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine); }

        [Test]
        public void ShouldRequireTheDebugKeyAndEnableBeforeHalting()
        {
            nvic.WriteDoubleWord(0xdf0, 3);
            Assert.IsFalse(cpu.HaltedByDebugger);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0002);
            Assert.IsFalse(cpu.HaltedByDebugger);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            Assert.IsTrue(cpu.HaltedByDebugger);
            Assert.IsTrue(cpu.ExternalDebugEnabled);
            Assert.AreEqual(0x30003, nvic.ReadDoubleWord(0xdf0) & 0x30003);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            Assert.AreNotEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x4000000);
            WaitForHalt(false);
            Assert.IsFalse(cpu.HaltedByDebugger);
            Assert.AreEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x4000000);
        }

        [Test]
        public void ShouldKeepMemoryMappedDebugExecutionControlOptIn()
        {
            cpu.EnableMemoryMappedDebug = false;
            cpu.SetRegister(0, 0x12345678);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            nvic.WriteDoubleWord(0xdf8, 0xabcdef00);
            nvic.WriteDoubleWord(0xdf4, 0x10000);
            Assert.IsFalse(cpu.HaltedByDebugger);
            Assert.IsFalse(cpu.ExternalDebugEnabled);
            Assert.AreEqual(0x12345678, (uint)cpu.GetRegister(0));
        }

        [TestCase(0)]
        [TestCase(12)]
        [TestCase(14)]
        [TestCase(17)]
        [TestCase(18)]
        public void ShouldTransferActualCoreRegisters(int selector)
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            nvic.WriteDoubleWord(0xdf8, 0x12345670);
            nvic.WriteDoubleWord(0xdf4, (uint)(selector | 0x10000));
            WaitForRegisterTransfer();
            var index = selector <= 15 ? selector : selector == 17 ? (int)CortexMRegisters.MSPCURR : (int)CortexMRegisters.PSPCURR;
            Assert.AreEqual(0x12345670, (uint)cpu.GetRegister(index));
            cpu.SetRegister(index, 0xabcdef00);
            nvic.WriteDoubleWord(0xdf4, (uint)selector);
            WaitForRegisterTransfer();
            Assert.AreEqual(0xabcdef00, nvic.ReadDoubleWord(0xdf8));
        }

        [Test]
        public void ShouldIgnoreRegisterTransfersWhileRunning()
        {
            cpu.SetRegister(0, 0x12345678);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            nvic.WriteDoubleWord(0xdf8, 0xabcdef00);
            nvic.WriteDoubleWord(0xdf4, 0x10000);
            Assert.AreEqual(0x12345678, (uint)cpu.GetRegister(0));
            Assert.AreEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x10000);
        }

        [Test]
        public void ShouldTransferThePackedSpecialRegisters()
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            nvic.WriteDoubleWord(0xdf8, 0x01014001);
            nvic.WriteDoubleWord(0xdf4, 0x10014);
            WaitForRegisterTransfer();
            Assert.AreEqual(1, (uint)cpu.GetRegister((int)CortexMRegisters.PRIMASK));
            Assert.AreEqual(0x40, (uint)cpu.GetRegister((int)CortexMRegisters.BasePri));
            Assert.AreEqual(1, (uint)cpu.GetRegister((int)CortexMRegisters.FAULTMASK));
            Assert.AreEqual(1, (uint)cpu.GetRegister((int)CortexMRegisters.Control));
            nvic.WriteDoubleWord(0xdf4, 20);
            WaitForRegisterTransfer();
            Assert.AreEqual(0x01014001, nvic.ReadDoubleWord(0xdf8));
        }

        [Test]
        public void ShouldExposeProcessorDiscoveryAndStickyResetStatus()
        {
            Assert.AreEqual(0x47702a04, nvic.ReadDoubleWord(0xfbc));
            Assert.AreEqual(0x90, nvic.ReadDoubleWord(0xff4));
            Assert.AreNotEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x2000000);
            Assert.AreEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x2000000);
        }

        [Test]
        public void ShouldLatchExternalBreakpointAndClearFaultStatusWithWriteOne()
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            nvic.ReportExternalDebugBreakpoint();
            Assert.AreEqual(2, nvic.ReadDoubleWord(0xd30));
            Assert.AreEqual(3, nvic.ReadDoubleWord(0xdf0) & 3);
            nvic.WriteDoubleWord(0xd30, 1);
            Assert.AreEqual(2, nvic.ReadDoubleWord(0xd30));
            nvic.WriteDoubleWord(0xd30, 2);
            Assert.AreEqual(0, nvic.ReadDoubleWord(0xd30));
        }

        [Test]
        public void ShouldCatchResetAtTheActualResetVectorAndResumeWhenCatchIsDisabled()
        {
            var ram = new MappedMemory(machine, 0x2000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            ram.WriteDoubleWord(0, 0x20001000);
            ram.WriteDoubleWord(4, 0x20000101);
            ram.WriteWord(0x100, 0x3001);
            ram.WriteWord(0x102, 0xe7fd);
            cpu.InitVectorTableOffset = 0x20000000;
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            nvic.WriteDoubleWord(0xdfc, 0x01000001);
            cpu.Reset();
            Assert.AreNotEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x2000000);
            WaitForHalt();
            Assert.IsTrue(cpu.HaltedByDebugger);
            Assert.AreEqual(0x20000100, (uint)cpu.PC & ~1u);
            Assert.AreEqual(0x20001000, (uint)cpu.GetRegister(13));
            Assert.AreEqual(8, nvic.ReadDoubleWord(0xd30));
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            Assert.AreEqual(0, cpu.ExecutedInstructions);
            Assert.AreEqual(0x01000001, nvic.ReadDoubleWord(0xdfc));
            nvic.WriteDoubleWord(0xdfc, 0);
            cpu.Reset();
            WaitForHalt(false);
            Assert.IsFalse(cpu.HaltedByDebugger);
            Assert.AreEqual(0, nvic.ReadDoubleWord(0xdf0) & 2);
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            Assert.Greater((uint)cpu.GetRegister(0), 0,
                $"PC={cpu.PC}, state={cpu.EmulationState}, executed={cpu.ExecutedInstructions}, halted={cpu.HaltedByDebugger}, locked={nvic.Lockup.IsSet}");
        }

        [Test]
        public void ShouldRejectCompletionOfAStepSupersededByANewerDebugRequest()
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f000d);
            Assert.IsTrue(nvic.TryGetExternalDebugStepRequest(out var oldGeneration));
            Assert.IsTrue(nvic.ExternalDebugMaskInterrupts);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            Assert.IsFalse(nvic.ReportExternalDebugStep(oldGeneration));
            Assert.IsFalse(nvic.ExternalDebugHaltRequested);
            nvic.WriteDoubleWord(0xdf0, 0xa05f000d);
            Assert.IsTrue(nvic.TryGetExternalDebugStepRequest(out var generation));
            Assert.AreNotEqual(oldGeneration, generation);
            Assert.IsFalse(nvic.ReportExternalDebugStep(oldGeneration));
            Assert.IsTrue(nvic.ExternalDebugStepRequested);
            Assert.IsTrue(nvic.ReportExternalDebugStep(generation));
            Assert.IsTrue(nvic.ExternalDebugHaltRequested);
            Assert.IsFalse(nvic.ExternalDebugStepRequested);
            Assert.AreEqual(1, nvic.ReadDoubleWord(0xd30));
        }

        [Test]
        public void ShouldLetABreakpointSupersedeAPendingStepCompletion()
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f000d);
            Assert.IsTrue(nvic.TryGetExternalDebugStepRequest(out var generation));
            nvic.ReportExternalDebugBreakpoint();
            Assert.IsFalse(nvic.ReportExternalDebugStep(generation));
            Assert.AreEqual(2, nvic.ReadDoubleWord(0xd30));
        }

        [TestCase(26, true)]
        [TestCase(26, false)]
        [TestCase(28, true)]
        [TestCase(28, false)]
        public void ShouldMaskBothSecurityBanksOfSysTickAndPendSVWhileDebugMaskIsSet(int pendingBit, bool secure)
        {
            nvic.WriteDoubleWord(0xd04, 1u << pendingBit, secure);
            Assert.IsNotNull(nvic.FindPendingInterrupt());
            nvic.WriteDoubleWord(0xdf0, 0xa05f000b);
            Assert.IsNull(nvic.FindPendingInterrupt());
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            Assert.IsNotNull(nvic.FindPendingInterrupt());
        }

        [Test]
        public void ShouldMaskExternalIRQsWithoutClearingTheirPendingState()
        {
            nvic.WriteDoubleWord(0x100, 1, true);
            nvic.OnGPIO(0, true);
            Assert.AreEqual(16, nvic.FindPendingInterrupt());
            nvic.WriteDoubleWord(0xdf0, 0xa05f000b);
            Assert.IsNull(nvic.FindPendingInterrupt());
            Assert.AreEqual(1, nvic.ReadDoubleWord(0x200, true) & 1);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            Assert.AreEqual(16, nvic.FindPendingInterrupt());
        }

        [TestCase(2)]
        [TestCase(3)]
        public void ShouldRetainNMIAndHardFaultWhileDebugMaskIsSet(int exception)
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f000b);
            nvic.SetPendingIRQ(exception);
            Assert.AreEqual(exception, nvic.FindPendingInterrupt());
        }

        [Test]
        public void ShouldSelectTheRequestedCoreThroughItsActualMEMAP()
        {
            var nvic1 = new NVIC(machine);
            var cpu1 = new CortexM("cortex-m33", machine, nvic1, cpuId: 1, enableTrustZone: true);
            machine.SystemBus.Register(cpu1, new CPURegistrationPoint(1));
            machine.SetLocalName(cpu1, "cpu1");
            machine.SystemBus.Register(nvic1, new BusPointRegistration(0xe000e000, cpu: cpu1));
            var port = new TestDebugPort(machine, cpu, cpu1);
            port.PowerUp();
            foreach(var ap in new uint[] { 0x2000, 0x4000 })
            {
                port.WriteMemory(ap, 0xe000edf0, 0xa05f0003);
                Assert.IsTrue(SpinWait.SpinUntil(() => (port.ReadMemory(ap, 0xe000edf0) & 0x30000) == 0x30000, TimeSpan.FromSeconds(5)));
                port.WriteMemory(ap, 0xe000edf8, ap);
                port.WriteMemory(ap, 0xe000edf4, 0x10000);
                Assert.IsTrue(SpinWait.SpinUntil(() => (port.ReadMemory(ap, 0xe000edf0) & 0x10000) != 0, TimeSpan.FromSeconds(5)));
            }
            Assert.AreEqual(0x2000, (uint)cpu.GetRegister(0));
            Assert.AreEqual(0x4000, (uint)cpu1.GetRegister(0));
            Assert.AreEqual(0x411fd210, port.ReadMemory(0x2000, 0xe000ed00));
            Assert.AreEqual(0x411fd210, port.ReadMemory(0x4000, 0xe000ed00));
        }

        [Test]
        public void ShouldUseMEMAPSecurityAttributesRatherThanTheHaltedCoresSecurityState()
        {
            var ram = new MappedMemory(machine, 0x2000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            cpu.SecureState = false;
            cpu.VectorTableOffset = 0x20000100;
            cpu.SecureState = true;
            cpu.VectorTableOffset = 0x20000200;
            cpu.SecureState = false;
            var port = new TestDebugPort(machine, cpu, cpu);
            port.PowerUp();
            Assert.AreEqual(0x20000200, port.ReadMemory(0x2000, 0xe000ed08));
            port.WriteAP(0x2d00, 0x4b800052);
            Assert.AreEqual(0x20000100, port.ReadMemory(0x2000, 0xe000ed08));
            Assert.IsFalse(cpu.SecureState);
        }

        [Test]
        public void ShouldKeepDebuggerSecurityIndependentOfTheSelectedCoresForcedNonSecureState()
        {
            var nvic1 = new NVIC(machine);
            var cpu1 = new CortexM("cortex-m33", machine, nvic1, cpuId: 1, enableTrustZone: true);
            machine.SystemBus.Register(cpu1, new CPURegistrationPoint(1));
            machine.SetLocalName(cpu1, "cpu1");
            machine.SystemBus.Register(nvic1, new BusPointRegistration(0xe000e000, cpu: cpu1));
            var accessControl = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, accessControl: accessControl);
            machine.SystemBus.Register(gpio, new BusPointRegistration(0x40028000));
            accessControl.WriteDoubleWord(4, 0xacce0002); // FORCE_CORE_NS.PROC1
            var port = new TestDebugPort(machine, cpu, cpu1);
            port.PowerUp();
            port.WriteMemory(0x4000, 0x40028004, 5); // Secure GPIO0 CTRL, SIO function
            Assert.AreEqual(5, port.ReadMemory(0x4000, 0x40028004), "Secure MEM-AP access remains Secure");
            var coreState = new CortexM.ContextState { Privileged = true, CpuSecure = true, AttributionSecure = true };
            Assert.AreEqual(0, machine.SystemBus.ReadDoubleWord(0x40028004, cpu1, (ulong)coreState),
                "The same core's own accesses are forced Non-secure");
            port.WriteAP(0x4d00, 0x4b800052); // MEM-AP HNONSEC
            Assert.AreEqual(0, port.ReadMemory(0x4000, 0x40028004), "Non-secure debug attributes are still honored");
        }

        [Test]
        public void ShouldHaltAndResumeFromAnotherMachinesClockCallback()
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var previousQuantum = emulation.MasterTimeSource.Quantum;
            emulation.MasterTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            machine.LocalTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            var probe = new Machine(createLocalTimeSource: true);
            probe.LocalTimeSource.Quantum = TimeInterval.FromMicroseconds(1);
            emulation.AddMachine(probe, "probe_test");
            try
            {
                var probeNvic = new NVIC(probe);
                var probeCpu = new CortexM("cortex-m0+", probe, probeNvic);
                probe.SystemBus.Register(probeCpu, new CPURegistrationPoint(0));
                probe.SetLocalName(probeCpu, "probe_cpu");
                probe.SystemBus.Register(probeNvic, new BusPointRegistration(0xe000e000, cpu: probeCpu));
                var probeRam = new MappedMemory(probe, 0x2000);
                probe.SystemBus.Register(probeRam, new BusPointRegistration(0x20000000));
                probeRam.WriteDoubleWord(0, 0x20001000);
                probeRam.WriteDoubleWord(4, 0x20000101);
                probeRam.WriteWord(0x100, 0xe7fe); // b .
                probeCpu.InitVectorTableOffset = 0x20000000;
                probeCpu.Reset();
                var ram = new MappedMemory(machine, 0x2000);
                machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
                ram.WriteDoubleWord(0, 0x20001000);
                ram.WriteDoubleWord(4, 0x20000101);
                ram.WriteWord(0x100, 0x3001); // adds r0, #1
                ram.WriteWord(0x102, 0xe7fd); // b 0x20000100
                cpu.InitVectorTableOffset = 0x20000000;
                cpu.Reset();
                nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.Greater((uint)cpu.GetRegister(0), 0, "CPU must execute the RAM loop before the foreign halt callback");
                probe.ScheduleAction(TimeInterval.FromMicroseconds(5), _ =>
                    nvic.WriteDoubleWord(0xdf0, 0xa05f0003), "halt_target");
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                WaitForHalt();
                var haltedCount = (uint)cpu.GetRegister(0);
                probe.ScheduleAction(TimeInterval.FromMicroseconds(5), _ =>
                    nvic.WriteDoubleWord(0xdf0, 0xa05f0001), "resume_target");
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                WaitForHalt(false);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.Greater(probeCpu.ExecutedInstructions, 0);
                Assert.Greater(haltedCount, 0);
                Assert.Greater((uint)cpu.GetRegister(0), haltedCount,
                    $"PC={cpu.PC}, executed={cpu.ExecutedInstructions}, state={cpu.EmulationState}, halted={cpu.IsHalted}, debugger={cpu.HaltedByDebugger}, clocked={cpu.Clocked}, handle={cpu.TimeHandle.Enabled}");
            }
            finally
            {
                emulation.RemoveMachine(probe);
                emulation.MasterTimeSource.Quantum = previousQuantum;
            }
        }

        [TestCase(2, false)]
        [TestCase(0, true)]
        [TestCase(1, true)]
        public void ShouldReturnMEMAPFaultForRejectedFPBTransactions(int size, bool privileged)
        {
            var fpb = new ARM_FlashPatchBreakpoint(machine, cpu);
            machine.SystemBus.Register(fpb, new BusPointRegistration(0xe0002000, cpu: cpu));
            var port = new TestDebugPort(machine, cpu, null);
            port.PowerUp();
            var csw = (privileged ? 0x0b800040u : 0x09800040u) | (uint)size;
            port.WriteAP(0x2d00, csw);
            port.WriteAP(0x2d04, 0xe0002000);
            port.AssertReadFault(0x2d0c);
            port.WriteAP(0x2d04, 0xe0002008);
            port.WriteAP(0x2d0c, 0x101);
            port.AssertStickyFaultAndClear();
            Assert.AreEqual(0, fpb.ReadDoubleWord(8), "Rejected bus write changed the comparator");
            Assert.AreEqual(0x10000080, fpb.ReadDoubleWord(0));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldDiscardQueuedRegisterWritesSupersededByResumeOrReset(bool resetCore)
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            cpu.SetRegister(0, 0x11223344);
            var schedulerMonitor = typeof(CortexM).GetField("debugWorkerSync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(cpu);
            lock(schedulerMonitor)
            {
                nvic.WriteDoubleWord(0xdf8, 0x55667788);
                nvic.WriteDoubleWord(0xdf4, 0x10000);
                if(resetCore) cpu.Reset();
                else nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            }
            WaitForHalt(false);
            Assert.AreEqual(resetCore ? 0u : 0x11223344u, (uint)cpu.GetRegister(0));
            Assert.AreEqual(0, nvic.ReadDoubleWord(0xdf0) & 0x10000);
        }

        [TestCase(0x20000100u, false)]
        [TestCase(0x20000101u, false)]
        [TestCase(0x20000102u, false)]
        [TestCase(0x20000103u, false)]
        [TestCase(0x20000100u, true)]
        [TestCase(0x20000101u, true)]
        [TestCase(0x20000102u, true)]
        [TestCase(0x20000103u, true)]
        public void ShouldTransferAnAlignedDebugReturnAddressWithoutChangingThumbState(uint address, bool thumb)
        {
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            WriteDebugRegister(16, thumb ? 0x01000000u : 0u);
            WriteDebugRegister(15, address);
            Assert.AreEqual(address & ~1u, (uint)cpu.PC);
            Assert.AreEqual(thumb ? 0x01000000u : 0u, (uint)cpu.XProgramStatusRegister & 0x01000000u);
            nvic.WriteDoubleWord(0xdf4, 15);
            WaitForRegisterTransfer();
            Assert.AreEqual(address & ~1u, nvic.ReadDoubleWord(0xdf8));
        }

        [Test]
        public void ShouldLaunchDebugCodeBeforeTheFirstNativeInstructionWithoutReloadingResetVectors()
        {
            var ram = new MappedMemory(machine, 0x4000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            ram.WriteDoubleWord(0, 0x20003000);
            ram.WriteDoubleWord(4, 0x20000101);
            ram.WriteWord(0x100, 0xe7fe); // Reset vector: b .
            ram.WriteWord(0x202, 0x3001); // Debug entry: adds r0, #1
            ram.WriteWord(0x204, 0xbe00); // bkpt #0
            cpu.InitVectorTableOffset = 0x20000000;
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            WriteDebugRegister(0, 7);
            WriteDebugRegister(13, 0x20002000);
            WriteDebugRegister(15, 0x20000203);
            WriteDebugRegister(16, 0x01000000);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            WaitForHalt(false);
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            WaitForHalt();
            Assert.AreEqual(8, (uint)cpu.GetRegister(0));
            Assert.AreEqual(0x20000204u, (uint)cpu.PC);
            Assert.AreEqual(0x20002000u, (uint)cpu.GetRegister(13));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ShouldRaiseInvalidStateAfterAThumbClearDebugReturnAddressWrite(bool enableUsageFault, bool warmCodeCache)
        {
            nvic.IRQ.Connect(cpu, 0);
            var secureAccess = new CortexM.ContextState { Privileged = true, CpuSecure = true, AttributionSecure = true };
            var ram = new MappedMemory(machine, 0x4000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            ram.WriteDoubleWord(0, 0x20003000);
            ram.WriteDoubleWord(4, 0x20000101);
            ram.WriteDoubleWord(0xc, 0x20000301); // HardFault
            ram.WriteDoubleWord(0x18, 0x20000341); // UsageFault
            ram.WriteWord(0x100, 0xe7fe);
            ram.WriteWord(0x200, 0x2001); // movs r0, #1: forbidden with EPSR.T clear.
            ram.WriteWord(0x202, 0xbe00);
            ram.WriteWord(0x300, 0xbe00);
            ram.WriteWord(0x340, 0xbe00);
            cpu.InitVectorTableOffset = 0x20000000;
            nvic.WriteDoubleWord(0xdf0, 0xa05f0003);
            WaitForHalt();
            WriteDebugRegister(13, 0x20002000);
            if(warmCodeCache)
            {
                WriteDebugRegister(16, 0x01000000);
                WriteDebugRegister(15, 0x20000201);
                nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
                WaitForHalt(false);
                EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
                WaitForHalt();
                Assert.AreEqual(1, (uint)cpu.GetRegister(0), "The same instruction page must execute before the Thumb-clear attempt");
                Assert.AreEqual(0x20000202u, (uint)cpu.PC);
                WriteDebugRegister(0, 0);
            }
            machine.SystemBus.WriteDoubleWord(0xe000ed24, enableUsageFault ? 1u << 18 : 0u, cpu, (ulong)secureAccess);
            WriteDebugRegister(16, 0);
            WriteDebugRegister(15, 0x20000201);
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            WaitForHalt(false);
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(100));
            WaitForHalt();
            var faultStatus = machine.SystemBus.ReadDoubleWord(0xe000ed28, cpu, (ulong)secureAccess);
            Assert.AreEqual(0x20000u, faultStatus & 0x20000u);
            var hardFaultStatus = machine.SystemBus.ReadDoubleWord(0xe000ed2c, cpu, (ulong)secureAccess);
            Assert.AreEqual(enableUsageFault ? 0u : 0x40000000u, hardFaultStatus & 0x40000000u);
            Assert.AreEqual(0, cpu.FaultStatusNonSecure & 0x20000u, "The Secure fault must not set the Non-secure syndrome");
            Assert.AreEqual(enableUsageFault ? 6u : 3u, (uint)cpu.XProgramStatusRegister & 0x1ffu);
            Assert.AreEqual(enableUsageFault ? 0x20000340u : 0x20000300u, (uint)cpu.PC);
            Assert.AreEqual(0, (uint)cpu.GetRegister(0), "The instruction with EPSR.T clear must not execute");
            var stackPointer = (uint)cpu.GetRegister(13);
            Assert.AreEqual(0x20001fe0u, stackPointer);
            Assert.AreEqual(0x20000200u, ram.ReadDoubleWord(stackPointer - 0x20000000 + 24));
            Assert.AreEqual(0, ram.ReadDoubleWord(stackPointer - 0x20000000 + 28) & 0x01000000u,
                "The frame must preserve the faulting EPSR.T state");
        }

        private void WriteDebugRegister(int selector, uint value)
        {
            nvic.WriteDoubleWord(0xdf8, value);
            nvic.WriteDoubleWord(0xdf4, 0x10000u | (uint)selector);
            WaitForRegisterTransfer();
        }

        private void WaitForHalt(bool halted = true)
        {
            Assert.IsTrue(SpinWait.SpinUntil(() =>
                ((nvic.ReadDoubleWord(0xdf0) & 0x20000) != 0) == halted
                && cpu.HaltedByDebugger == halted, TimeSpan.FromSeconds(5)), "Actual DHCSR halt acknowledgement timed out");
        }

        private void WaitForRegisterTransfer()
        {
            Assert.IsTrue(SpinWait.SpinUntil(() => (nvic.ReadDoubleWord(0xdf0) & 0x10000) != 0,
                TimeSpan.FromSeconds(5)), "DCRSR transfer acknowledgement timed out");
        }

        private Machine machine;
        private NVIC nvic;
        private CortexM cpu;

        private sealed class TestDebugPort : RP2350_DebugPort
        {
            public TestDebugPort(IMachine machine, CortexM cpu0, CortexM cpu1) : base(machine, cpu0, cpu1) { }
            public void PowerUp() { CompleteWrite(false, 4, 0x50000000); }
            public void WriteAP(uint address, uint value)
            {
                CompleteWrite(false, 8, address & ~0xfu);
                Assert.AreEqual(1, (int)BeginTransfer(true, false, (byte)(address & 0xc), out var _));
                CompleteWrite(true, (byte)(address & 0xc), value);
            }
            public void AssertReadFault(uint address)
            {
                CompleteWrite(false, 8, address & ~0xfu);
                Assert.AreEqual(4, (int)BeginTransfer(true, true, (byte)(address & 0xc), out var _));
                AssertStickyFaultAndClear();
            }
            public void AssertStickyFaultAndClear()
            {
                Assert.AreEqual(1, (int)BeginTransfer(false, true, 4, out var control));
                Assert.AreNotEqual(0, control & 0x20);
                CompleteWrite(false, 0, 4);
                Assert.AreEqual(1, (int)BeginTransfer(false, true, 4, out control));
                Assert.AreEqual(0, control & 0x20);
            }
            public uint ReadAP(uint address)
            {
                CompleteWrite(false, 8, address & ~0xfu);
                Assert.AreEqual(1, (int)BeginTransfer(true, true, (byte)(address & 0xc), out var _));
                Assert.AreEqual(1, (int)BeginTransfer(false, true, 12, out var value));
                return value;
            }
            public void WriteMemory(uint ap, uint address, uint value)
            {
                WriteAP(ap + 0xd04, address);
                WriteAP(ap + 0xd0c, value);
            }
            public uint ReadMemory(uint ap, uint address)
            {
                WriteAP(ap + 0xd04, address);
                return ReadAP(ap + 0xd0c);
            }
        }
    }
}
