//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Analog;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_ADCTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            adc = new RP2_ADC(machine);
        }

        private uint ConvertChannel(int channel)
        {
            adc.WriteDoubleWord(Cs, Enable | ((uint)channel << 12) | StartOnce);
            AdvanceNanoseconds(3000);
            return adc.ReadDoubleWord(Result);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldConvertOnceIn96Cycles()
        {
            adc.SetChannelVoltage(0, 1.65);
            adc.WriteDoubleWord(Cs, Enable);
            Assert.AreNotEqual(0u, adc.ReadDoubleWord(Cs) & Ready);
            adc.WriteDoubleWord(Cs, Enable | StartOnce);
            Assert.AreEqual(0u, adc.ReadDoubleWord(Cs) & Ready, "Converting");
            AdvanceNanoseconds(1990);
            Assert.AreEqual(0u, adc.ReadDoubleWord(Cs) & Ready, "96 cycles of 48 MHz are 2 us");
            AdvanceNanoseconds(20);
            Assert.AreNotEqual(0u, adc.ReadDoubleWord(Cs) & Ready);
            Assert.AreEqual(2048u, adc.ReadDoubleWord(Result));
        }

        [Test]
        public void ShouldConvertThePadLevelOfAChannelWithoutASetVoltage()
        {
            var gpio = new RP2_GPIOBank0(machine);
            adc = new RP2_ADC(machine, gpio: gpio);
            Assert.AreEqual(0u, ConvertChannel(1), "GPIO27's reset pull-down");
            gpio.OnGPIO(27, true);
            Assert.AreEqual(4095u, ConvertChannel(1), "A wire driving GPIO27 high");
            Assert.AreEqual(0u, ConvertChannel(0), "GPIO26 is not driven");
            gpio.ReleaseInput(27);
            Assert.AreEqual(0u, ConvertChannel(1));
            gpio.OnGPIO(27, true);
            adc.SetChannelVoltage(1, 1.65);
            Assert.AreEqual(2048u, ConvertChannel(1), "A set voltage takes precedence");
        }

        [Test]
        public void ShouldReadTheTemperatureSensorWhenEnabled()
        {
            adc.WriteDoubleWord(Cs, Enable | TemperatureEnable | (4u << 12) | StartOnce);
            AdvanceNanoseconds(3000);
            Assert.AreEqual(876u, adc.ReadDoubleWord(Result), "0.706 V at 27 C");
            adc.TemperatureCelsius = 77;
            adc.WriteDoubleWord(Cs, Enable | TemperatureEnable | (4u << 12) | StartOnce);
            AdvanceNanoseconds(3000);
            Assert.AreEqual(769u, adc.ReadDoubleWord(Result), "0.61995 V at 77 C");
        }

        [Test]
        public void ShouldRoundRobinIntoTheFifo()
        {
            adc.SetChannelVoltage(0, 0.33);
            adc.SetChannelVoltage(1, 0.66);
            adc.SetChannelVoltage(2, 0.99);
            adc.WriteDoubleWord(Fcs, FifoEnable | FifoShift);
            adc.WriteDoubleWord(Cs, Enable | StartMany | (0x6u << 16));
            AdvanceNanoseconds(2000 * 4 + 10);
            adc.WriteDoubleWord(Cs, Enable | (0x6u << 16));
            Assert.AreEqual(4u, (adc.ReadDoubleWord(Fcs) >> 16) & 0xf);
            // Channel 0 first, then 1, 2, 1, each shifted to 8 bits.
            Assert.AreEqual(410u >> 4, adc.ReadDoubleWord(Fifo));
            Assert.AreEqual(819u >> 4, adc.ReadDoubleWord(Fifo));
            Assert.AreEqual(1229u >> 4, adc.ReadDoubleWord(Fifo));
            Assert.AreEqual(819u >> 4, adc.ReadDoubleWord(Fifo));
        }

        [Test]
        public void ShouldPaceFreeRunningConversionsWithTheDivider()
        {
            adc.WriteDoubleWord(Fcs, FifoEnable);
            adc.WriteDoubleWord(Div, 479u << 8); // one sample per 480 cycles: 10 us
            adc.WriteDoubleWord(Cs, Enable | StartMany);
            AdvanceNanoseconds(31000);
            Assert.AreEqual(3u, (adc.ReadDoubleWord(Fcs) >> 16) & 0xf, "Conversions start every 10 us and end 2 us later: 3 by 31 us");
        }

        [Test]
        public void ShouldRaiseInterruptAndRequestAtTheThresholdAndFlagOverflow()
        {
            var irq = new GPIOReceiver();
            var dreq = new GPIOReceiver();
            adc.IRQ.Connect(irq, 0);
            adc.DREQ.Connect(dreq, 0);
            adc.WriteDoubleWord(Inte, 1);
            adc.WriteDoubleWord(Fcs, FifoEnable | FifoDreqEnable | (2u << 24));
            adc.WriteDoubleWord(Cs, Enable | StartMany);
            AdvanceNanoseconds(2010);
            Assert.IsFalse(irq.State, "One sample is below THRESH 2");
            Assert.IsFalse(dreq.State);
            AdvanceNanoseconds(2000);
            Assert.IsTrue(irq.State);
            Assert.IsTrue(dreq.State);
            AdvanceNanoseconds(2000 * 3);
            Assert.AreNotEqual(0u, adc.ReadDoubleWord(Fcs) & FifoOver, "The fifth sample found the FIFO full");
            adc.ReadDoubleWord(Fifo);
            adc.ReadDoubleWord(Fifo);
            adc.ReadDoubleWord(Fifo);
            Assert.IsFalse(irq.State, "Draining below THRESH clears the interrupt");
            adc.WriteDoubleWord(Fcs, FifoEnable | FifoOver);
            Assert.AreEqual(0u, adc.ReadDoubleWord(Fcs) & FifoOver);
        }

        private void AdvanceNanoseconds(ulong nanoseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(nanoseconds));

        private Machine machine;
        private RP2_ADC adc;

        private const long Cs = 0x00, Result = 0x04, Fcs = 0x08, Fifo = 0x0c, Div = 0x10, Inte = 0x18;
        private const uint Enable = 1, TemperatureEnable = 2, StartOnce = 4, StartMany = 8, Ready = 1u << 8;
        private const uint FifoEnable = 1, FifoShift = 2, FifoDreqEnable = 8, FifoOver = 1u << 11;

        private sealed class GPIOReceiver : IGPIOReceiver
        {
            public void OnGPIO(int number, bool value) => State = value;
            public void Reset() => State = false;
            public bool State { get; private set; }
        }
    }
}
