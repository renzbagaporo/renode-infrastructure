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
    public class RP2350_SIOInputTests
    {
        [SetUp]
        public void SetUp() => machine = new Machine();

        [TearDown]
        public void TearDown() => machine.Dispose();

        [TestCase(0, false)]
        [TestCase(0, true)]
        [TestCase(47, false)]
        [TestCase(47, true)]
        [TestCase(59, false)]
        [TestCase(59, true)]
        public void ShouldKeepExternallyHeldInputAcrossSioReset(int pin, bool level)
        {
            var sio = new RP2350_SIO(machine);
            Assert.IsFalse(ReadInput(sio, pin));
            sio.OnGPIO(pin, level);
            Assert.AreEqual(level, ReadInput(sio, pin));
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
        public void ShouldSampleBootselPullupAcrossMachineReset(bool sioRegisteredFirst, bool pressed)
        {
            var sio = new RP2350_SIO(machine);
            var qspi = new RP2350_QSPI(machine, sio);
            var pads = new RP2350_PadsQSPI(machine, qspi);
            if(sioRegisteredFirst) machine.SystemBus.Register(sio, new BusRangeRegistration(0xd0000000, 0x1000));
            machine.SystemBus.Register(qspi, new BusRangeRegistration(0x40030000, 0x4000));
            machine.SystemBus.Register(pads, new BusRangeRegistration(0x40040000, 0x4000));
            if(!sioRegisteredFirst) machine.SystemBus.Register(sio, new BusRangeRegistration(0xd0000000, 0x1000));
            Assert.AreEqual(0x15au, pads.ReadDoubleWord(0x18));
            Assert.IsTrue(qspi.GetInput(3));
            Assert.IsTrue(ReadInput(sio, 59));
            if(pressed) qspi.OnGPIO(3, false);
            for(var reset = 0; reset < 3; reset++)
            {
                machine.Reset();
                // The ROM removes pad isolation before its nine CSn strap samples.
                pads.WriteDoubleWord(0x3018, 0x100);
                Assert.AreEqual(!pressed, qspi.GetInput(3));
                Assert.AreEqual(!pressed, ReadInput(sio, 59));
                uint samples = 0;
                for(var sample = 0; sample < 9; sample++) samples += (sio.ReadDoubleWord(8) >> 27) & 1;
                Assert.AreEqual(pressed, samples < 5);
            }
            qspi.ReleaseInput(3);
            Assert.IsTrue(ReadInput(sio, 59));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldResampleOutputReleaseDuringSioReset(bool pullUp)
        {
            var sio = new RP2350_SIO(machine);
            var gpio = new RP2_GPIOBank0(machine, sio, rp2350: true);
            gpio.SetPadConfiguration(0, pullUp ? 0x5au : 0x56u);
            gpio.WriteDoubleWord(4, 5);
            sio.WriteDoubleWord(0x10, 1);
            sio.WriteDoubleWord(0x30, 1);
            Assert.IsTrue(gpio.GetInput(0));
            Assert.IsTrue(ReadInput(sio, 0));
            sio.Reset();
            Assert.AreEqual(0u, sio.ReadDoubleWord(0x10));
            Assert.AreEqual(0u, sio.ReadDoubleWord(0x30));
            Assert.AreEqual(pullUp, gpio.GetInput(0));
            Assert.AreEqual(pullUp, ReadInput(sio, 0));
            Assert.AreEqual(pullUp, gpio.Connections[0].IsSet);
        }

        private static bool ReadInput(RP2350_SIO sio, int pin) =>
            (sio.ReadDoubleWord(pin < 32 ? 4 : 8) & (1u << (pin % 32))) != 0;

        private Machine machine;
    }
}
