//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_DebugPortTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 0x2000);
            machine.SystemBus.Register(memory, new BusPointRegistration(0x20000000));
            port = new TestPort(machine);
        }

        [TearDown]
        public void TearDown() { machine.Dispose(); }

        [TestCase(0x01002927u, 0x00000001u)]
        [TestCase(0x11002927u, 0x10000001u)]
        [TestCase(0xf1002927u, 0xf0000001u)]
        public void ShouldSelectEachMultidropTargetAndIdentifyIt(uint targetSelect, uint dlpidr)
        {
            Assert.IsTrue(port.Target(targetSelect));
            Assert.AreEqual(0x0bc12477, port.ReadDP(0));
            port.WriteDP(8, 2);
            Assert.AreEqual(0x01002927, port.ReadDP(4));
            port.WriteDP(8, 3);
            Assert.AreEqual(dlpidr, port.ReadDP(4));
        }

        [Test]
        public void ShouldIgnoreOtherTargets()
        {
            Assert.IsFalse(port.Target(0x21002927));
            Assert.IsFalse(port.Target(0x01002926));
        }

        [Test]
        public void ShouldWaitForDebugPowerAndAcknowledgeTheHandshake()
        {
            port.Select(0xfc);
            Assert.AreEqual(2, port.TryAPRead(out var _));
            port.WriteDP(4, 0x50000000);
            Assert.AreEqual(0xf0000000, port.ReadDP(4));
            Assert.AreEqual(1, port.TryAPRead(out var _));
        }

        [Test]
        public void ShouldIdentifyTheCoreAHBAP()
        {
            PowerUp();
            Assert.AreEqual(0x04770031, port.ReadAP(0xfc));
            Assert.AreEqual(0xe00ff003, port.ReadAP(0xf8));
            Assert.AreEqual(0x03000052, port.ReadAP(0x00));
            // Only APSEL 0 exists.
            Assert.AreEqual(0, port.ReadAP(0x010000fc));
        }

        [Test]
        public void ShouldTransferThroughTheSystemBusWithOneKilobyteWrap()
        {
            PowerUp();
            port.WriteAP(0x00, 0x12);
            port.WriteAP(0x04, 0x200003fc);
            port.WriteAP(0x0c, 0x12345678);
            Assert.AreEqual(0x12345678, memory.ReadDoubleWord(0x3fc));
            Assert.AreEqual(0x20000000, port.ReadAP(0x04));
            port.WriteAP(0x04, 0x200003fc);
            Assert.AreEqual(0x12345678, port.ReadAP(0x0c));
        }

        [Test]
        public void ShouldFaultUnmappedTransfersStickily()
        {
            PowerUp();
            port.WriteAP(0x04, 0x30000000);
            port.Select(0x0c);
            Assert.AreEqual(4, port.TryAPRead(out var _));
            Assert.AreNotEqual(0, port.ReadDP(4) & 0x20);
            Assert.AreEqual(4, port.TryAPRead(out var _));
            port.WriteDP(0, 4);
            Assert.AreEqual(0, port.ReadDP(4) & 0x20);
        }

        [Test]
        public void ShouldKeepEachTargetsStateSeparate()
        {
            PowerUp();
            port.WriteAP(0x04, 0x20000100);
            Assert.IsTrue(port.Target(0x11002927));
            Assert.AreEqual(0, port.ReadDP(4) & 0x20000000);
            PowerUp();
            Assert.AreEqual(0, port.ReadAP(0x04));
            Assert.IsTrue(port.Target(0x01002927));
            Assert.AreEqual(0x20000100, port.ReadAP(0x04));
        }

        [Test]
        public void ShouldKeepTheSessionAcrossSystemResets()
        {
            Assert.IsTrue(port.Target(0x11002927));
            PowerUp();
            port.WriteAP(0x04, 0x20000100);
            port.Reset();
            Assert.AreEqual(0xf0000000, port.ReadDP(4));
            Assert.AreEqual(0x20000100, port.ReadAP(0x04));
            port.WriteDP(8, 3);
            Assert.AreEqual(0x10000001, port.ReadDP(4));
        }

        [Test]
        public void ShouldReadTheCortexM0PlusBreakpointUnitThroughTheCoreAP()
        {
            var nvic = new NVIC(machine);
            var cpu = new CortexM("cortex-m0+", machine, nvic);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
            var bpu = new ARM_FlashPatchBreakpoint(machine, cpu);
            machine.SystemBus.Register(bpu, new BusPointRegistration(0xe0002000, cpu: cpu));
            port = new TestPort(machine, cpu);
            PowerUp();
            port.WriteAP(0x04, 0xe0002000);
            Assert.AreEqual(0x40, port.ReadAP(0x0c));
            // BP_CTRL writes need KEY; there are four comparators.
            port.WriteAP(0x0c, 1);
            port.WriteAP(0x04, 0xe0002000);
            Assert.AreEqual(0x40, port.ReadAP(0x0c));
            port.WriteAP(0x04, 0xe0002000);
            port.WriteAP(0x0c, 3);
            port.WriteAP(0x04, 0xe0002000);
            Assert.AreEqual(0x41, port.ReadAP(0x0c));
            bpu.WriteDoubleWord(0x14, 0x80000101);
            bpu.WriteDoubleWord(0x18, 0x80000101);
            Assert.AreEqual(0x80000101, bpu.ReadDoubleWord(0x14));
            Assert.AreEqual(0, bpu.ReadDoubleWord(0x18));
            Assert.AreEqual(0x410cc601, ReadCpuId());
        }

        private uint ReadCpuId()
        {
            port.WriteAP(0x04, 0xe000ed00);
            return port.ReadAP(0x0c);
        }

        private void PowerUp() { port.WriteDP(4, 0x50000000); }

        private sealed class TestPort : RP2040_DebugPort
        {
            public TestPort(IMachine machine, CortexM cpu0 = null) : base(machine, cpu0, null) { }
            public bool Target(uint value) { return SelectTarget(value); }
            public void Select(uint address) { apAddress = (byte)(address & 0xc); WriteDP(8, address & ~0xfu); }
            public uint ReadDP(byte address) { Assert.AreEqual(1, (int)BeginTransfer(false, true, address, out var value)); return value; }
            public void WriteDP(byte address, uint value) { CompleteWrite(false, address, value); }
            public int TryAPRead(out uint value) { return (int)BeginTransfer(true, true, apAddress, out value); }
            public uint ReadAP(uint address)
            {
                Select(address);
                Assert.AreEqual(1, TryAPRead(out var _));
                return ReadDP(12);
            }
            public void WriteAP(uint address, uint value)
            {
                Select(address);
                Assert.AreEqual(1, (int)BeginTransfer(true, false, apAddress, out var _));
                CompleteWrite(true, apAddress, value);
            }
            private byte apAddress;
        }

        private Machine machine;
        private MappedMemory memory;
        private TestPort port;
    }
}
