//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_GPIOBank0Tests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
        }

        [TearDown]
        public void TearDown()
        {
            machine?.Dispose();
        }

        [Test]
        public void ShouldRequireInputEnableAndReleasedIsolationOnRP2350()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.OnGPIO(4, true);
            Assert.IsFalse(gpio.GetInput(4));
            gpio.SetPadConfiguration(4, 0x56);
            Assert.IsTrue(gpio.GetInput(4));
            gpio.SetPadConfiguration(4, 0x156);
            Assert.IsFalse(gpio.GetInput(4));
        }

        [Test]
        public void ShouldGatePeripheralOutputByFunctionEnableAndPadControl()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.SetPeripheralOutput(5, 2, true, true);
            gpio.WriteDoubleWord(2 * 8 + 4, 5);
            Assert.IsFalse(gpio.Connections[2].IsSet);
            gpio.SetPadConfiguration(2, 0x56);
            Assert.IsTrue(gpio.Connections[2].IsSet);
            Assert.AreEqual(1u << 13, gpio.ReadDoubleWord(2 * 8) & (1u << 13));
            gpio.SetPadConfiguration(2, 0xd6);
            Assert.IsFalse(gpio.Connections[2].IsSet);
            gpio.SetPadConfiguration(2, 0x56);
            gpio.WriteDoubleWord(2 * 8 + 4, 0x1f);
            Assert.IsFalse(gpio.Connections[2].IsSet);
        }

        [Test]
        public void ShouldApplyInputOutputAndEnableOverridesIndependently()
        {
            var gpio = new RP2_GPIOBank0(machine);
            gpio.WriteDoubleWord(4, 0x1f | (3u << 12) | (3u << 14));
            Assert.IsTrue(gpio.Connections[0].IsSet);
            Assert.IsTrue(gpio.GetInput(0));
            gpio.WriteDoubleWord(0x2004, 2u << 16);
            Assert.IsFalse(gpio.GetInput(0));
            Assert.AreNotEqual(0u, gpio.ReadDoubleWord(0) & (1u << 17));
        }

        [Test]
        public void ShouldResolveReleasedOpenDrainOutputThroughPullUp()
        {
            var gpio = new RP2_GPIOBank0(machine);
            gpio.SetPadConfiguration(4, 0x5a);
            gpio.WriteDoubleWord(4 * 8 + 4, 3);
            gpio.SetPeripheralOutput(3, 4, false, false);
            Assert.IsTrue(gpio.GetInput(4));
            gpio.SetPeripheralOutput(3, 4, false, true);
            Assert.IsFalse(gpio.GetInput(4));
            gpio.SetPeripheralOutput(3, 4, false, false);
            Assert.IsTrue(gpio.GetInput(4));
        }

        [Test]
        public void ShouldLatchAndAcknowledgeEdgeInterruptsIndependentlyOfLevels()
        {
            var gpio = new RP2_GPIOBank0(machine);
            gpio.WriteDoubleWord(0x100, 8);
            gpio.OnGPIO(0, true);
            Assert.IsTrue(gpio.Core0IRQ.IsSet);
            Assert.IsFalse(gpio.Core1IRQ.IsSet);
            Assert.AreEqual(0xau, gpio.ReadDoubleWord(0xf0) & 0xf);
            gpio.WriteDoubleWord(0xf0, 8);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.AreEqual(2u, gpio.ReadDoubleWord(0xf0) & 0xf);
            gpio.WriteDoubleWord(0xf0, 2);
            Assert.AreEqual(2u, gpio.ReadDoubleWord(0xf0) & 0xf);
        }

        [Test]
        public void ShouldRouteRP2350UpperGPIOInterruptToSelectedCore()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.SetPadConfiguration(44, 0x56);
            gpio.WriteDoubleWord(0x290 + 5 * 4, 8u << 16);
            gpio.OnGPIO(44, true);
            Assert.IsTrue(gpio.Core1IRQ.IsSet);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.AreEqual(1u << 12, gpio.ReadDoubleWord(0x214));
            gpio.WriteDoubleWord(0x230 + 5 * 4, 8u << 16);
            Assert.IsFalse(gpio.Core1IRQ.IsSet);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldNotLatchEdgeWhenResetRemovesDrivenHighOutput(bool rp2350)
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: rp2350);
            gpio.SetPadConfiguration(0, 0x56);
            gpio.WriteDoubleWord(4, 0x1f | (3u << 12) | (3u << 14));
            Assert.IsTrue(gpio.GetInput(0));
            gpio.Reset();
            var interruptOffset = rp2350 ? 0x230 : 0xf0;
            Assert.AreEqual(1u, gpio.ReadDoubleWord(interruptOffset) & 0xf);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldIgnoreEdgesDuringResetHoldAndDetectEdgesAfterRelease(bool rp2350)
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: rp2350);
            gpio.SetPadConfiguration(0, 0x56);
            var interruptOffset = rp2350 ? 0x230 : 0xf0;
            gpio.ResetHeld = true;
            gpio.OnGPIO(0, true);
            Assert.AreEqual(2u, gpio.ReadDoubleWord(interruptOffset) & 0xf);
            gpio.OnGPIO(0, false);
            Assert.AreEqual(1u, gpio.ReadDoubleWord(interruptOffset) & 0xf);
            gpio.ResetHeld = false;
            gpio.OnGPIO(0, true);
            Assert.AreEqual(0xau, gpio.ReadDoubleWord(interruptOffset) & 0xf);
        }

        [Test]
        public void ShouldApplyPadAtomicAliasesAndVariantResetValues()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true);
            Assert.AreEqual(0x116u, pads.ReadDoubleWord(4));
            gpio.OnGPIO(0, true);
            pads.WriteDoubleWord(0x3004, 0x100);
            Assert.IsFalse(gpio.GetInput(0));
            pads.WriteDoubleWord(0x2004, 0x40);
            Assert.IsTrue(gpio.GetInput(0));
            Assert.AreEqual(0x56u, pads.ReadDoubleWord(4));
        }

        private Machine machine;
    }
}
