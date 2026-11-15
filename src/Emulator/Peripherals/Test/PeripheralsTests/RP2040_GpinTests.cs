//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_GpinTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            var xosc = new RP2_XOSC(machine);
            clocks = new RP2040_Clocks(machine, xosc, new RP2040_ROSC(machine),
                new RP2_PLL(machine, xosc), new RP2_PLL(machine, xosc), gpio: gpio);
            // clk_rtc from CLKSRC_GPIN0, undivided.
            clocks.WriteDoubleWord(0x70, 1u << 8);
            clocks.WriteDoubleWord(0x6c, 0x800 | (4u << 5));
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldClockFromTheEdgeRateOnGpio20AndStopWhenTheEdgesStop()
        {
            Assert.AreEqual(0ul, clocks.GetFrequency(RtcClock));
            // The carrier harness's square wave: 21333 ns period.
            for(var i = 0; i < 50; i++)
            {
                gpio.OnGPIO(20, true);
                AdvanceNanoseconds(10667);
                gpio.OnGPIO(20, false);
                AdvanceNanoseconds(10666);
            }
            Assert.AreEqual(46875.0, clocks.GetFrequency(RtcClock), 46875 * 0.01);
            AdvanceNanoseconds(4 * 21333 + 1000);
            Assert.AreEqual(0ul, clocks.GetFrequency(RtcClock), "Four periods without an edge stop the clock");
        }

        [Test]
        public void ShouldUseTheSetFrequencyWithoutEdges()
        {
            clocks.Gpin0Frequency = 48000;
            Assert.AreEqual(48000ul, clocks.GetFrequency(RtcClock));
        }

        private void AdvanceNanoseconds(ulong nanoseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(nanoseconds));

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2040_Clocks clocks;

        private const int RtcClock = 9;
    }
}
