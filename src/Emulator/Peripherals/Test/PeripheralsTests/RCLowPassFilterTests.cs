//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Sensor;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RCLowPassFilterTests
    {
        [SetUp]
        public void SetUp() => machine = new Machine();

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldFollowTheSingleStageStepResponse()
        {
            var filter = new RCLowPassFilter(machine, 1000, 1000); // 1 kOhm, 1 uF: tau 1 ms
            filter.OnGPIO(0, true);
            AdvanceNanoseconds(1000000);
            Assert.AreEqual(3.3 * (1 - Math.Exp(-1)), filter.Volts, 1e-9);
            filter.OnGPIO(0, false);
            AdvanceNanoseconds(2000000);
            Assert.AreEqual(3.3 * (1 - Math.Exp(-1)) * Math.Exp(-2), filter.Volts, 1e-9);
        }

        [Test]
        public void ShouldSettleTheTwoStageNetworkAtTheInputLevel()
        {
            var filter = Carrier();
            filter.OnGPIO(0, true);
            AdvanceNanoseconds(1000);
            var early = filter.Volts;
            Assert.Greater(early, 0);
            Assert.Less(early, 0.3, "The second stage lags the first");
            AdvanceNanoseconds(200000);
            Assert.AreEqual(3.3, filter.Volts, 1e-6);
        }

        [TestCase(250u, 0.825)]
        [TestCase(400u, 1.32)]
        [TestCase(750u, 2.475)]
        public void ShouldAverageAOneMegahertzPwm(uint highNanoseconds, double mean)
        {
            var filter = Carrier();
            for(var period = 0; period < 500; period++)
            {
                filter.OnGPIO(0, true);
                AdvanceNanoseconds(highNanoseconds);
                filter.OnGPIO(0, false);
                AdvanceNanoseconds(1000 - highNanoseconds);
            }
            double sum = 0, minimum = double.MaxValue, maximum = double.MinValue;
            filter.OnGPIO(0, true);
            for(var ns = 0u; ns < 1000; ns += 10)
            {
                if(ns == highNanoseconds) filter.OnGPIO(0, false);
                var volts = filter.Volts;
                sum += volts;
                minimum = Math.Min(minimum, volts);
                maximum = Math.Max(maximum, volts);
                AdvanceNanoseconds(10);
            }
            Assert.AreEqual(mean, sum / 100, 0.002);
            Assert.Less(maximum - minimum, 0.02, "Ripple at the ADC pin stays within a few tens of LSBs");
            Assert.Greater(maximum - minimum, 0.001, "The 1 MHz ripple is not filtered away entirely");
        }

        [Test]
        public void ShouldFeedTheRP2ADCAtEachConversion()
        {
            var adc = new RP2_ADC(machine);
            machine.SystemBus.Register(adc, new BusRangeRegistration(0x4004c000, 0x1000));
            var filter = Carrier();
            ((IADC)adc).Register(filter, new NumberRegistrationPoint<int>(1));
            filter.OnGPIO(0, true);
            AdvanceNanoseconds(200000);
            adc.WriteDoubleWord(Cs, Enable | (1u << 12) | StartOnce);
            AdvanceNanoseconds(3000);
            Assert.AreEqual(4095u, adc.ReadDoubleWord(Result));
            filter.OnGPIO(0, false);
            AdvanceNanoseconds(200000);
            adc.WriteDoubleWord(Cs, Enable | (1u << 12) | StartOnce);
            AdvanceNanoseconds(3000);
            Assert.AreEqual(0u, adc.ReadDoubleWord(Result));
        }

        // Metalclaw carrier v1.r1: 330 Ohm, 10 nF, 2.2 kOhm, 1.5 nF.
        private RCLowPassFilter Carrier() => new RCLowPassFilter(machine, 330, 10, 2200, 1.5);

        private void AdvanceNanoseconds(ulong nanoseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(nanoseconds));

        private Machine machine;

        private const long Cs = 0x0, Result = 0x4;
        private const uint Enable = 1, StartOnce = 4;
    }
}
