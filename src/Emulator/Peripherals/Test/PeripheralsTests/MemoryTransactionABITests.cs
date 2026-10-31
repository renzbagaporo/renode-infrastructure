// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.Reflection;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class MemoryTransactionABITests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "memory_abi");
            recorder = new Recorder(machine);
            machine.SystemBus.Register(recorder, new BusPointRegistration(Base));
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldPassEffectiveRiscVPrivilegeThroughManagedReadAndWriteFallbacks(bool mprv)
        {
            var cpu = new RP2350_Hazard3(machine, architectureEnabled: true);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            cpu.MSTATUS = mprv ? 1u << 17 : 0u;
            Exercise(cpu, Base);
            Assert.AreEqual(!mprv, recorder.RiscVPrivilege);
            Assert.IsFalse(recorder.InstructionFetch);
        }

        [Test]
        public void ShouldUseTheActualArmAddressForManagedFallbackSecurityAttribution()
        {
            var nvic = new NVIC(machine);
            var cpu = new CortexM("cortex-m33", machine, nvic, enableTrustZone: true);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
            nvic.IRQ.Connect(cpu, 0);
            nvic.WriteDoubleWord(0xdd8, 0); // SAU region0.
            nvic.WriteDoubleWord(0xddc, Base + 0x1000);
            nvic.WriteDoubleWord(0xde0, Base + 0x1fe1);
            nvic.WriteDoubleWord(0xdd0, 1);
            Exercise(cpu, Base);
            Assert.IsTrue(recorder.ArmSecure);
            Exercise(cpu, Base + 0x1000);
            Assert.IsFalse(recorder.ArmSecure);
            Assert.AreEqual(0x1000, recorder.Offset);
        }

        private void Exercise(TranslationCPU cpu, ulong address)
        {
            var read = typeof(TranslationCPU).GetMethod("ReadDoubleWordFromBus", Flags, null, new[] {typeof(ulong)}, null);
            var write = typeof(TranslationCPU).GetMethod("WriteDoubleWordToBus", Flags, null, new[] {typeof(ulong), typeof(ulong)}, null);
            Assert.AreEqual(0xcafebabeUL, (ulong)read.Invoke(cpu, new object[] {address}));
            write.Invoke(cpu, new object[] {address, 0x12345678UL});
            Assert.AreEqual(0x12345678u, recorder.Value);
        }

        private sealed class Recorder : IDoubleWordPeripheral, IKnownSize
        {
            public Recorder(Machine machine) { this.machine = machine; }
            public uint ReadDoubleWord(long offset) { Capture(offset); return 0xcafebabe; }
            public void WriteDoubleWord(long offset, uint value) { Capture(offset); Value = value; }
            public void Reset() { }
            public long Size => 0x2000;
            public bool RiscVPrivilege;
            public bool ArmSecure;
            public bool InstructionFetch;
            public uint Value;
            public long Offset;
            private void Capture(long offset)
            {
                Offset = offset;
                if(machine.SystemBus.TryGetCurrentContextState<RP2350_Hazard3.ContextState>(out var _, out var rv))
                {
                    RiscVPrivilege = rv.Privileged;
                    InstructionFetch = rv.InstructionFetch;
                }
                if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var arm))
                    ArmSecure = arm.AttributionSecure;
            }
            private readonly Machine machine;
        }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const uint Base = 0x40001000;
        private Machine machine;
        private Recorder recorder;
    }
}
