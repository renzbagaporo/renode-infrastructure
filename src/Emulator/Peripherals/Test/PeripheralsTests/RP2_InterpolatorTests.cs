//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_InterpolatorTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            sio = new RP2040_SIO(machine);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldAccumulateATimesTableOnPop()
        {
            Write(0, Ctrl0, Mask(0, 31));
            Write(0, Base0, 9);
            for(uint i = 1; i <= 10; i++) Assert.AreEqual(9 * i, Read(0, PopLane0));
            Assert.AreEqual(90u, Read(0, Accum0));
        }

        [Test]
        public void ShouldRotateBeforeMasking()
        {
            Write(0, Accum0, 0x12345678);
            Write(0, Ctrl0, Mask(0, 31) | 4);
            Assert.AreEqual(0x81234567u, Read(0, PeekLane0));
            Write(0, Ctrl0, Mask(4, 7) | 4);
            Assert.AreEqual(0x60u, Read(0, PeekLane0));
            Assert.AreEqual(0u, Read(0, Accum0) ^ 0x12345678u, "PEEK leaves the accumulator alone");
            Assert.AreNotEqual(0u, Read(0, Ctrl0) & (1u << 23), "Masked-off MSBs set OVERF0");
        }

        [Test]
        public void ShouldSignExtendFromTheMaskMsb()
        {
            Write(0, Accum0, 0xf);
            Write(0, Ctrl0, Mask(0, 3) | Signed);
            Assert.AreEqual(0xffffffffu, Read(0, PeekLane0));
            Write(0, Base1And0, 0xfffe0002);
            Assert.AreEqual(2u, Read(0, Base0));
            Write(0, Ctrl1, Signed);
            Write(0, Base1And0, 0xfffe0002);
            Assert.AreEqual(0xfffffffeu, Read(0, Base1), "BASE1 is sign-extended for a SIGNED lane 1");
        }

        [Test]
        public void ShouldCrossInputsAndResults()
        {
            Write(0, Accum0, 5);
            Write(0, Accum1, 7);
            Write(0, Ctrl0, Mask(0, 31) | CrossInput | CrossResult);
            Write(0, Ctrl1, Mask(0, 31));
            Write(0, Base0, 100);
            Write(0, Base1, 200);
            Assert.AreEqual(107u, Read(0, PopLane0), "Lane 0 reads ACCUM1");
            Assert.AreEqual(207u, Read(0, Accum0), "CROSS_RESULT feeds lane 1's result to ACCUM0");
            Assert.AreEqual(207u, Read(0, Accum1));
            Write(0, Base2, 1000);
            Assert.AreEqual(1000u + 207 + 207, Read(0, PeekFull));
        }

        [Test]
        public void ShouldBlendOnInterpolator0()
        {
            Write(0, Ctrl0, Blend);
            Write(0, Ctrl1, Mask(0, 7));
            Write(0, Base0, 1000);
            Write(0, Base1, 2000);
            Write(0, Accum1, 128);
            Assert.AreEqual(1500u, Read(0, PeekLane1));
            Assert.AreEqual(128u, Read(0, PeekLane0), "LANE0 yields the blend fraction");
            // The datasheet's simple_blend3: lane 1's SIGNED extends both BASE_1AND0 halves.
            Write(0, Ctrl1, Mask(0, 7) | Signed);
            Write(0, Base1And0, 0xe000f000);
            Assert.AreEqual(0xffffe800u, Read(0, PeekLane1));
            Write(1, Ctrl0, Blend);
            Assert.AreEqual(0u, Read(1, Ctrl0), "Interpolator 1 has no BLEND");
        }

        [Test]
        public void ShouldClampOnInterpolator1()
        {
            Write(1, Ctrl0, Mask(0, 31) | Signed | Clamp);
            Write(1, Base0, unchecked((uint)-10));
            Write(1, Base1, 10);
            Write(1, Accum0, 100);
            Assert.AreEqual(10u, Read(1, PeekLane0));
            Write(1, Accum0, unchecked((uint)-100));
            Assert.AreEqual(unchecked((uint)-10), Read(1, PeekLane0));
            Write(1, Accum0, 3);
            Assert.AreEqual(3u, Read(1, PeekLane0));
        }

        private static uint Mask(int lsb, int msb) => (uint)(lsb << 5 | msb << 10);

        private void Write(int interpolator, long register, uint value) => sio.WriteDoubleWord(0x80 + interpolator * 0x40 + register, value);
        private uint Read(int interpolator, long register) => sio.ReadDoubleWord(0x80 + interpolator * 0x40 + register);

        private Machine machine;
        private RP2040_SIO sio;

        private const long Accum0 = 0x00, Accum1 = 0x04, Base0 = 0x08, Base1 = 0x0c, Base2 = 0x10;
        private const long PopLane0 = 0x14, PeekLane0 = 0x20, PeekLane1 = 0x24, PeekFull = 0x28;
        private const long Ctrl0 = 0x2c, Ctrl1 = 0x30, Base1And0 = 0x3c;
        private const uint Signed = 1u << 15, CrossInput = 1u << 16, CrossResult = 1u << 17, Blend = 1u << 21, Clamp = 1u << 22;
    }
}
