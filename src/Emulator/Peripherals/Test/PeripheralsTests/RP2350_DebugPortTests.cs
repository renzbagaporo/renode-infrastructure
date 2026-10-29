//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Memory;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_DebugPortTests
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

        [Test]
        public void ShouldExposeBankedDPv3IdentificationAndPowerHandshake()
        {
            Assert.AreEqual(0x4c013477, port.ReadDP(0));
            Assert.AreEqual(0x40, port.ReadDP(4));
            port.WriteDP(8, 2);
            Assert.AreEqual(1, port.ReadDP(0));
            port.WriteDP(8, 3);
            Assert.AreEqual(0, port.ReadDP(0));
            port.WriteDP(8, 0);
            port.WriteDP(4, 0x50000000);
            Assert.AreEqual(0xf0000040, port.ReadDP(4));
            port.WriteDP(4, 0);
            Assert.AreEqual(0x40, port.ReadDP(4));
        }

        [Test]
        public void ShouldWaitForDebugPowerExceptForAlwaysOnRPAP()
        {
            port.Select(0x2dfc);
            Assert.AreEqual(2, port.TryAPRead(out var _));
            port.Select(0x80000);
            Assert.AreEqual(1, port.TryAPRead(out var _));
            Assert.AreEqual(0, port.ReadDP(12));
        }

        [Test]
        public void ShouldReturnPreviousAPReadAndRetainTheLastValueInReadBuffer()
        {
            PowerUp();
            port.Select(0x2dfc);
            Assert.AreEqual(1, port.TryAPRead(out var first));
            Assert.AreEqual(0, first);
            port.Select(0x4dfc);
            Assert.AreEqual(1, port.TryAPRead(out var previous));
            Assert.AreEqual(0x000101a0, previous);
            Assert.AreEqual(0x000101a0, port.ReadDP(12));
        }

        [Test]
        public void ShouldFaultUnknownAPAddressesAndClearStickyErrorThroughAbort()
        {
            PowerUp();
            port.Select(0x3000);
            Assert.AreEqual(4, port.TryAPRead(out var _));
            Assert.AreNotEqual(0, port.ReadDP(4) & 0x20);
            port.Select(0x2dfc);
            Assert.AreEqual(4, port.TryAPRead(out var _));
            port.WriteDP(0, 4);
            Assert.AreEqual(1, port.TryAPRead(out var _));
            Assert.AreEqual(0x000101a0, port.ReadDP(12));
        }

        [Test]
        public void ShouldUseBothSelectRegistersWithoutAliasingHighAddressBits()
        {
            PowerUp();
            port.WriteDP(8, 5);
            port.WriteDP(4, 1);
            port.Select(0x2dfc);
            Assert.AreEqual(4, port.TryAPRead(out var _));
        }

        [Test]
        public void ShouldNotEnumerateRPAPAsACoreSightComponent()
        {
            PowerUp();
            Assert.AreEqual(0x2003, port.ReadAP(0));
            Assert.AreEqual(0x4003, port.ReadAP(4));
            Assert.AreEqual(0, port.ReadAP(8));
            Assert.AreEqual(9, port.ReadAP(0xfd0));
            Assert.AreEqual(0, port.ReadAP(0x80ff4));
        }

        [Test]
        public void ShouldBlockAPAccessOnWriteParityErrorsUntilAbortClearsThem()
        {
            PowerUp();
            port.BadParity();
            port.Select(0x2dfc);
            Assert.AreEqual(4, port.TryAPRead(out var _));
            Assert.AreNotEqual(0, port.ReadDP(4) & 0x80);
            port.WriteDP(0, 8);
            Assert.AreEqual(1, port.TryAPRead(out var _));
        }

        [TestCase(0, 1, 0x0000aa00u)]
        [TestCase(1, 2, 0xbbaa0000u)]
        [TestCase(2, 0, 0xddccbbaau)]
        public void ShouldRouteLaneAlignedMemoryTransfersThroughTheSystemBus(int size, int offset, uint expected)
        {
            PowerUp();
            port.WriteAP(0x2d00, (uint)(0x10 | size));
            port.WriteAP(0x2d04, (uint)(0x20000000 + offset));
            port.WriteAP(0x2d0c, expected);
            port.WriteAP(0x2d04, (uint)(0x20000000 + offset));
            Assert.AreEqual(expected, port.ReadAP(0x2d0c));
            Assert.AreEqual(expected, memory.ReadDoubleWord(0));
            Assert.AreEqual(0x20000000 + offset + (1 << size), port.ReadAP(0x2d04));
        }

        [Test]
        public void ShouldWrapAutoincrementAtOneKilobyteAndKeepAPStateIndependent()
        {
            PowerUp();
            port.WriteAP(0x2d00, 0x12);
            port.WriteAP(0x2d04, 0x200003fc);
            port.WriteAP(0x2d0c, 0x12345678);
            Assert.AreEqual(0x12345678, memory.ReadDoubleWord(0x3fc));
            Assert.AreEqual(0x20000000, port.ReadAP(0x2d04));
            Assert.AreEqual(0, port.ReadAP(0x4d04));
        }

        [Test]
        public void ShouldFaultMisalignedAndUnmappedTransfersWithoutWritingMemory()
        {
            PowerUp();
            port.WriteAP(0x2d04, 0x20000001);
            port.WriteAP(0x2d0c, 0x12345678);
            Assert.AreEqual(0, memory.ReadDoubleWord(0));
            Assert.AreNotEqual(0, port.ReadDP(4) & 0x20);
            port.WriteDP(0, 4);
            port.WriteAP(0x2d04, 0x30000000);
            port.Select(0x2d0c);
            Assert.AreEqual(4, port.TryAPRead(out var _));
        }

        [Test]
        public void ShouldUseBankedDataRegistersWithoutIncrementingTAR()
        {
            PowerUp();
            port.WriteAP(0x2d04, 0x20000007);
            port.WriteAP(0x2d18, 0x12345678);
            Assert.AreEqual(0x12345678, memory.ReadDoubleWord(8));
            Assert.AreEqual(0x20000007, port.ReadAP(0x2d04));
        }

        [Test]
        public void ShouldNotifyBothEdgesOfRescueReset()
        {
            var edges = new System.Collections.Generic.List<bool>();
            port.RescueResetChanged += edges.Add;
            port.WriteAP(0x80000, 0x80000000);
            port.WriteAP(0x80000, 0x80000000);
            port.WriteAP(0x80000, 0);
            CollectionAssert.AreEqual(new[] { true, false }, edges);
        }

        private void PowerUp() { port.WriteDP(4, 0x50000000); }

        private sealed class TestPort : RP2350_DebugPort
        {
            public TestPort(IMachine machine) : base(machine, null, null) { }
            public void BadParity() { WriteParityError(); }
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
