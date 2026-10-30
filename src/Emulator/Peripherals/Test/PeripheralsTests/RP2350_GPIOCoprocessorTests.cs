//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_GPIOCoprocessorTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            access = new RP2350_AccessControl(machine);
            sio = new RP2350_SIO(machine, access);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [TestCase(0, 0)] [TestCase(0, 1)] [TestCase(0, 2)] [TestCase(0, 3)]
        [TestCase(1, 0)] [TestCase(1, 1)] [TestCase(1, 2)] [TestCase(1, 3)]
        public void ShouldShareMaskedWritesWithSioRegisters(int group, int operation)
        {
            const ulong original = 0x810012345678abcd;
            const ulong data = 0x42001111deadbeef;
            var register = group == 0 ? 0x10 : 0x30;
            foreach(var requestedMask in new[] { ulong.MaxValue, 0xfffffffful, 0xffffffff00000000ul, 1ul << 45 })
            {
                sio.WriteDoubleWord(register, unchecked((uint)original));
                sio.WriteDoubleWord(register + 4, (uint)(original >> 32));
                var before = ReadPair(register);
                var mask = requestedMask & Implemented;
                var bits = data & mask;
                var expected = operation == 0 ? (before & ~mask) | bits
                    : operation == 1 ? before ^ bits
                    : operation == 2 ? before | bits : before & ~bits;
                var result = sio.AccessGPIOCoprocessor((uint)(group * 4 + operation), data,
                    requestedMask, true, 0);
                Assert.AreEqual(expected, ReadPair(register));
                Assert.AreEqual(expected, result);
                Assert.AreEqual(expected, sio.AccessGPIOCoprocessor((uint)(16 + group * 4), 0, 0, true, 1));
            }
        }

        [TestCase(0)] [TestCase(1)]
        public void ShouldPublishPairedWritesAsOneCoherentChange(int group)
        {
            var observed = 0;
            const ulong value = 0x8100abcd76543210;
            sio.GPIOChanged += () =>
            {
                observed++;
                Assert.AreEqual(value, group == 0 ? sio.Output : sio.OutputEnable);
            };
            sio.AccessGPIOCoprocessor((uint)(group * 4), value, ulong.MaxValue, true, 0);
            Assert.AreEqual(1, observed);
        }

        [TestCase(0)] [TestCase(1)]
        public void ShouldFilterNonSecureWritesAndReadsWithoutChangingSecurePins(int group)
        {
            const ulong allowed = 0x0100000180000001;
            access.WriteDoubleWord(0xc, unchecked((uint)allowed));
            access.WriteDoubleWord(0x10, (uint)(allowed >> 32));
            sio.AccessGPIOCoprocessor((uint)(group * 4), ulong.MaxValue, ulong.MaxValue, true, 0);
            Assert.AreEqual(allowed, sio.AccessGPIOCoprocessor((uint)(16 + group * 4), 0, 0, false, 1));
            sio.AccessGPIOCoprocessor((uint)(group * 4), 0, ulong.MaxValue, false, 1);
            Assert.AreEqual(Implemented & ~allowed, sio.AccessGPIOCoprocessor((uint)(16 + group * 4), 0, 0, true, 0));
        }

        [Test]
        public void ShouldFilterInputsAndSampleBothHalvesTogether()
        {
            sio.OnGPIO(0, true);
            sio.OnGPIO(45, true);
            sio.OnGPIO(59, true);
            var expected = 1ul | (1ul << 45) | (1ul << 59);
            Assert.AreEqual(expected, sio.AccessGPIOCoprocessor(24, 0, 0, true, 0));
            access.WriteDoubleWord(0x10, 1u << (45 - 32));
            Assert.AreEqual(1ul << 45, sio.AccessGPIOCoprocessor(24, 0, 0, false, 1));
        }

        [Test]
        public void ShouldHonorForcedNonSecureCoreOneOnly()
        {
            access.WriteDoubleWord(4, 0xacce0002);
            access.WriteDoubleWord(0xc, 2);
            sio.AccessGPIOCoprocessor(0, ulong.MaxValue, ulong.MaxValue, true, 1);
            Assert.AreEqual(2ul, sio.Output);
            sio.AccessGPIOCoprocessor(0, 5, ulong.MaxValue, true, 0);
            Assert.AreEqual(5ul, sio.Output);
            Assert.AreEqual(0ul, sio.AccessGPIOCoprocessor(16, 0, 0, true, 1));
        }

        [Test]
        public void ShouldSerializeIndependentCoreAliases()
        {
            Parallel.Invoke(
                () => { for(var i = 0; i < 10001; i++) sio.AccessGPIOCoprocessor(1, 1, 1, true, 0); },
                () => { for(var i = 0; i < 10001; i++) sio.AccessGPIOCoprocessor(1, 1ul << 45, 1ul << 45, true, 1); });
            Assert.AreEqual(1ul | (1ul << 45), sio.Output);
        }

        [TestCase(8)] [TestCase(12)] [TestCase(32)]
        public void ShouldRejectInvalidTransactions(int control)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                sio.AccessGPIOCoprocessor((uint)control, ulong.MaxValue, ulong.MaxValue, true, 0));
            Assert.AreEqual(0ul, sio.Output);
        }

        private ulong ReadPair(int register) => sio.ReadDoubleWord(register)
            | ((ulong)sio.ReadDoubleWord(register + 4) << 32);

        private const ulong Implemented = 0xff00fffffffffffful;
        private Machine machine;
        private RP2350_AccessControl access;
        private RP2350_SIO sio;
    }
}
