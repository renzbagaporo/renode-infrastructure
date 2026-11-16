//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_PinSPIControllerTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            spi = new RP2_SPI(machine, gpio: gpio, index: 0);
            for(var pin = Rx; pin <= Tx; pin++) gpio.WriteDoubleWord(pin * 8 + 4, 1); // FUNCSEL SPI
            controller = new RP2_PinSPIController(machine, gpio, Select, Clock, Rx, Tx, intervalMilliseconds: 1);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldExchangeAPageWithAnRP2SpiSlaveOneByteAWindow()
        {
            spi.WriteDoubleWord(0x00, 7); // 8 bits, mode 0
            spi.WriteDoubleWord(0x10, 2);
            spi.WriteDoubleWord(0x04, 2 | 4); // SSE, MS
            foreach(var b in new uint[] { 0xff, 0xfe, 0x81, 0x00 }) spi.WriteDoubleWord(0x08, b);
            controller.SetMosi("00 01 a5 7e");
            AdvanceMicroseconds(5000);
            Assert.AreEqual(0, controller.PagesSent, "Nothing is sent before Start");
            controller.Start(2);
            AdvanceMicroseconds(1100);
            Assert.AreEqual(1, controller.PagesSent, "The first page one interval after Start");
            var received = new uint[4];
            for(var i = 0; i < 4; i++) received[i] = spi.ReadDoubleWord(0x08);
            CollectionAssert.AreEqual(new uint[] { 0x00, 0x01, 0xa5, 0x7e }, received, "The slave read the MOSI page");
            Assert.AreEqual("ff fe 81 00", controller.TakeMisoLog(), "The controller read the slave's TX FIFO");
            AdvanceMicroseconds(1100);
            Assert.AreEqual(2, controller.PagesSent);
            Assert.AreEqual("00 00 00 00", controller.TakeMisoLog(), "An empty TX FIFO sends zeros");
            AdvanceMicroseconds(5000);
            Assert.AreEqual(2, controller.PagesSent, "Only the pages Start asked for");
        }

        private void AdvanceMicroseconds(ulong microseconds) =>
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2_SPI spi;
        private RP2_PinSPIController controller;

        private const int Rx = 16, Select = 17, Clock = 18, Tx = 19;
    }
}
