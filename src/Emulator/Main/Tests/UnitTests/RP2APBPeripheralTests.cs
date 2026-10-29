//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Peripherals;

using NUnit.Framework;

namespace Antmicro.Renode.UnitTests
{
    [TestFixture]
    public class RP2APBPeripheralTests
    {
        [Test]
        public void ShouldApplyAtomicAliasesWithoutReadingRegister()
        {
            var peripheral = new TestPeripheral();
            peripheral.WriteDoubleWord(0, 0x12345678);
            peripheral.WriteDoubleWord(0x1000, 0x000000ff);
            peripheral.WriteDoubleWord(0x2000, 0x80000000);
            peripheral.WriteDoubleWord(0x3000, 0x00005600);
            Assert.AreEqual(0x92340087u, peripheral.Value);
            Assert.AreEqual(0, peripheral.ReadCount);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        public void ShouldReplicateByteWritesAcrossAllLanes(int lane)
        {
            var peripheral = new TestPeripheral();
            peripheral.WriteByte(lane, 0x96);
            Assert.AreEqual(0x96969696u, peripheral.Value);
            peripheral.WriteByte(0x1000 + lane, 0xa5);
            Assert.AreEqual(0x33333333u, peripheral.Value);
        }

        [TestCase(0)]
        [TestCase(2)]
        public void ShouldReplicateHalfWordWritesAcrossBothLanes(int lane)
        {
            var peripheral = new TestPeripheral();
            peripheral.WriteWord(lane, 0x1234);
            Assert.AreEqual(0x12341234u, peripheral.Value);
            peripheral.WriteWord(0x2000 + lane, 0x8000);
            Assert.AreEqual(0x92349234u, peripheral.Value);
        }

        [Test]
        public void ShouldHoldRegistersInResetUntilReleased()
        {
            var peripheral = new TestPeripheral();
            peripheral.WriteDoubleWord(0, 0xffffffff);
            peripheral.ResetHeld = true;
            Assert.AreEqual(0u, peripheral.Value);
            peripheral.WriteDoubleWord(0x2000, 0xffffffff);
            Assert.AreEqual(0u, peripheral.Value);
            peripheral.ResetHeld = false;
            peripheral.WriteDoubleWord(0x2000, 0x1234);
            Assert.AreEqual(0x1234u, peripheral.Value);
        }

        [Test]
        public void ShouldExtractReadLanesThroughAliases()
        {
            var peripheral = new TestPeripheral();
            peripheral.WriteDoubleWord(0, 0x12345678);
            Assert.AreEqual(0x56, peripheral.ReadByte(0x3001));
            Assert.AreEqual(0x1234, peripheral.ReadWord(0x2002));
            Assert.AreEqual(0x12345678u, peripheral.ReadDoubleWord(0x1000));
        }

        private sealed class TestPeripheral : RP2APBPeripheral
        {
            public TestPeripheral() : base(null)
            {
            }

            public override void Reset()
            {
                Value = 0;
                ReadCount = 0;
            }

            public uint Value { get; private set; }

            public int ReadCount { get; private set; }

            protected override uint ReadRegister(long offset)
            {
                Assert.AreEqual(0, offset);
                ReadCount++;
                return Value;
            }

            protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
            {
                Assert.AreEqual(0, offset);
                Value = ApplyOperation(Value, value, operation);
            }
        }
    }
}
