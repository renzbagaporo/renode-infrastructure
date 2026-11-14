//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_PWMTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            // A 1 MHz clk_sys: one counter tick per microsecond at divider 1.
            pwm = new RP2_PWM(machine, gpio: gpio, frequency: 1000000);
            gpio.WriteDoubleWord(4, 4); // GPIO0 FUNCSEL = PWM (slice 0 A)
            gpio.WriteDoubleWord(12, 4); // GPIO1 FUNCSEL = PWM (slice 0 B)
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldProduceTheFreeRunningDutyCycleAndPeriod()
        {
            pwm.WriteDoubleWord(0x10, 9); // TOP: 10-tick period
            pwm.WriteDoubleWord(0x0c, 3 | (7u << 16)); // CC A 3, B 7
            pwm.WriteDoubleWord(0x00, 1); // enable
            var a = Sample(0, 40);
            var b = Sample(1, 40);
            Assert.AreEqual(12, a.Count(x => x), "A is high 3 of every 10 ticks");
            Assert.AreEqual(28, b.Count(x => x), "B is high 7 of every 10 ticks");
            Assert.AreEqual(new[] { 0, 10, 20, 30 }, RisingEdges(a));
        }

        [Test]
        public void ShouldRaiseTheWrapInterruptAndClearIt()
        {
            var irq = new GPIOReceiver();
            pwm.IRQ.Connect(irq, 0);
            pwm.WriteDoubleWord(0xa8, 1); // INTE slice 0
            pwm.WriteDoubleWord(0x10, 4);
            pwm.WriteDoubleWord(0x00, 1);
            Advance(3);
            Assert.IsFalse(irq.State);
            Advance(3);
            Assert.IsTrue(irq.State, "The counter wrapped after 5 ticks");
            Assert.AreEqual(1u, pwm.ReadDoubleWord(0xa4));
            pwm.WriteDoubleWord(0xa4, 1);
            Assert.IsFalse(irq.State);
        }

        [Test]
        public void ShouldCountUpAndDownInPhaseCorrectMode()
        {
            pwm.WriteDoubleWord(0x10, 5); // TOP 5: period 12 ticks
            pwm.WriteDoubleWord(0x0c, 2);
            pwm.WriteDoubleWord(0x00, 1 | 2); // enable, phase-correct
            var a = Sample(0, 30);
            // Counter 0..5 then 5..0: below CC for 4 of every 12 ticks, at
            // samples 0, 1, 10, 11, 12, 13, 22, 23, 24 and 25.
            Assert.AreEqual(10, a.Count(x => x));
            Assert.AreEqual(new[] { 0, 10, 22 }, RisingEdges(a));
            var counts = new List<uint>();
            for(var i = 0; i < 6; i++)
            {
                counts.Add(pwm.ReadDoubleWord(0x08));
                Advance(1);
            }
            // Ticks 30-35 are positions 6-11 of the period: the counter holds TOP
            // for one tick at the turn, then counts down.
            Assert.AreEqual(new uint[] { 5, 4, 3, 2, 1, 0 }, counts.ToArray());
        }

        [Test]
        public void ShouldInvertAChannelAndApplyTheFractionalDivider()
        {
            pwm.WriteDoubleWord(0x04, (2u << 4) | 8); // divide by 2.5
            pwm.WriteDoubleWord(0x10, 3); // 4 ticks = 10 us
            pwm.WriteDoubleWord(0x0c, 2);
            pwm.WriteDoubleWord(0x00, 1 | 4); // enable, invert A
            var a = Sample(0, 40);
            Assert.AreEqual(20, a.Count(x => x), "Inverted A is high for the upper half");
            Assert.AreEqual(new[] { 5, 15, 25, 35 }, RisingEdges(a));
        }

        [Test]
        public void ShouldReadTheCounterBetweenEvents()
        {
            pwm.WriteDoubleWord(0x10, 99);
            pwm.WriteDoubleWord(0x00, 1);
            Advance(37);
            Assert.AreEqual(37u, pwm.ReadDoubleWord(0x08));
            pwm.WriteDoubleWord(0x08, 90);
            Advance(15);
            Assert.AreEqual(5u, pwm.ReadDoubleWord(0x08), "The counter wrapped at TOP from the written value");
        }

        [Test]
        public void ShouldKeepTheCounterOnTheSystemClockWhenACycleIsNotAWholeNanosecond()
        {
            // At 150 MHz a cycle is 6.67 ns. Over 1000 periods of 16 cycles
            // (divider 4, TOP 3) the counter must read each cycle's value.
            machine.Dispose();
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            pwm = new RP2_PWM(machine, gpio: gpio, frequency: 150000000);
            pwm.WriteDoubleWord(0x04, 4 << 4);
            pwm.WriteDoubleWord(0x10, 3);
            pwm.WriteDoubleWord(0x0c, 1);
            pwm.WriteDoubleWord(0x00, 1);
            ulong now = 0;
            for(ulong cycle = 0; cycle < 16000; cycle++)
            {
                // The middle of the cycle, clear of either rounding of its edges.
                var middle = (cycle * 20 + 10 + 2) / 3;
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(middle - now));
                now = middle;
                Assert.AreEqual((uint)((cycle / 4) % 4), pwm.ReadDoubleWord(0x08), $"Counter at cycle {cycle}");
            }
        }

        [Test]
        public void ShouldCountOnlyWhileBIsHighInGatedMode()
        {
            var bDriven = true;
            gpio.DriveChanged += (pin, value, enabled) => { if(pin == 1) bDriven = enabled; };
            pwm.WriteDoubleWord(0x00, 1 | (1u << 4)); // enable, DIVMODE level-gated by B
            Assert.IsFalse(bDriven, "B is an input");
            Advance(10);
            Assert.AreEqual(0u, pwm.ReadDoubleWord(0x08), "B low: no counting");
            gpio.OnGPIO(1, true);
            Advance(30);
            gpio.OnGPIO(1, false);
            Advance(20);
            gpio.OnGPIO(1, true);
            Advance(10);
            Assert.AreEqual(40u, pwm.ReadDoubleWord(0x08));
        }

        [TestCase(2u, true)]
        [TestCase(3u, false)]
        public void ShouldClockTheDividerOnBEdges(uint mode, bool rising)
        {
            pwm.WriteDoubleWord(0x04, 2u << 4); // divide by 2
            pwm.WriteDoubleWord(0x00, 1 | (mode << 4));
            for(var i = 0; i < 5; i++)
            {
                gpio.OnGPIO(1, true);
                Advance(1);
                gpio.OnGPIO(1, false);
                Advance(1);
            }
            Assert.AreEqual(2u, pwm.ReadDoubleWord(0x08), "5 edges through a divider of 2");
            gpio.OnGPIO(1, true);
            gpio.OnGPIO(1, false);
            Assert.AreEqual(3u, pwm.ReadDoubleWord(0x08), rising ? "The sixth rising edge" : "The sixth falling edge");
        }

        private List<bool> Sample(int pin, int microseconds)
        {
            var samples = new List<bool>();
            for(var i = 0; i < microseconds; i++)
            {
                samples.Add((gpio.ReadDoubleWord(pin * 8) & (1u << 9)) != 0);
                Advance(1);
            }
            return samples;
        }

        private static int[] RisingEdges(List<bool> samples)
        {
            var edges = new List<int>();
            for(var i = 0; i < samples.Count; i++)
            {
                if(samples[i] && (i == 0 || !samples[i - 1])) edges.Add(i);
            }
            return edges.ToArray();
        }

        private void Advance(ulong microseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2_PWM pwm;

        private sealed class GPIOReceiver : IGPIOReceiver
        {
            public void OnGPIO(int number, bool value) => State = value;
            public void Reset() => State = false;
            public bool State { get; private set; }
        }
    }
}
