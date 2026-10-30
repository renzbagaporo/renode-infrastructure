//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_SIOInputTests
    {
        [SetUp]
        public void SetUp() => machine = new Machine();

        [TearDown]
        public void TearDown() => machine.Dispose();

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(29, false)]
        [TestCase(29, true)]
        [TestCase(31, false)]
        [TestCase(31, true)]
        public void ShouldSampleHeldPadLevelAcrossSioReset(int pin, bool level)
        {
            var sio = new RP2040_SIO(machine);
            sio.OnGPIO(pin, level);
            for(var reset = 0; reset < 3; reset++)
            {
                sio.Reset();
                Assert.AreEqual(level, ReadInput(sio, pin));
            }
            sio.OnGPIO(pin, !level);
            Assert.AreEqual(!level, ReadInput(sio, pin));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void ShouldSampleBootselPullupAcrossMachineReset(bool sioFirst, bool pressed)
        {
            var sio = new RP2040_SIO(machine);
            var qspi = new RP2040_QSPI(machine, sio);
            var pads = new RP2040_PadsQSPI(machine, qspi);
            if(sioFirst) machine.SystemBus.Register(sio, new BusRangeRegistration(0xd0000000, 0x1000));
            machine.SystemBus.Register(qspi, new BusRangeRegistration(0x40018000, 0x4000));
            machine.SystemBus.Register(pads, new BusRangeRegistration(0x40020000, 0x4000));
            if(!sioFirst) machine.SystemBus.Register(sio, new BusRangeRegistration(0xd0000000, 0x1000));
            Assert.IsTrue(qspi.GetInput(1));
            if(pressed) qspi.OnGPIO(1, false);
            for(var reset = 0; reset < 3; reset++)
            {
                machine.Reset();
                Assert.AreEqual(!pressed, qspi.GetInput(1));
                Assert.AreEqual(!pressed, ReadInput(sio, 31));
            }
            qspi.ReleaseInput(1);
            Assert.IsTrue(ReadInput(sio, 31));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldResampleOutputReleaseDuringReset(bool pullUp)
        {
            var sio = new RP2040_SIO(machine);
            var gpio = new RP2_GPIOBank0(machine, sio);
            gpio.SetPadConfiguration(0, pullUp ? 0x5au : 0x56u);
            gpio.WriteDoubleWord(4, 5);
            sio.WriteDoubleWord(0x10, 1);
            sio.WriteDoubleWord(0x20, 1);
            Assert.IsTrue(gpio.GetInput(0));
            Assert.IsTrue(ReadInput(sio, 0));
            sio.Reset();
            Assert.AreEqual(0u, sio.ReadDoubleWord(0x10));
            Assert.AreEqual(0u, sio.ReadDoubleWord(0x20));
            Assert.AreEqual(pullUp, gpio.GetInput(0));
            Assert.AreEqual(pullUp, ReadInput(sio, 0));
        }

        private static bool ReadInput(RP2040_SIO sio, int pin) =>
            (sio.ReadDoubleWord(pin < 30 ? 4 : 8) & (1u << (pin < 30 ? pin : pin - 30))) != 0;

        private Machine machine;
    }
}
