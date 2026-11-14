//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.I2C;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_I2CTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            i2c = new RP2_I2C(machine);
            irq = new GPIOReceiver();
            i2c.IRQ.Connect(irq, 0);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldDropTheInterruptWhenAReadClearsIt()
        {
            i2c.WriteDoubleWord(0x00, 0); // target mode
            i2c.WriteDoubleWord(0x6c, 1); // ENABLE
            // A target holds TX data until a read request; the 17th byte overflows.
            for(var i = 0; i < 17; i++) i2c.WriteDoubleWord(0x10, (uint)i);
            Assert.AreEqual(TxOver, i2c.ReadDoubleWord(0x2c), "Only TX_OVER is pending");
            Assert.IsTrue(irq.State);
            i2c.ReadDoubleWord(0x4c); // CLR_TX_OVER
            Assert.AreEqual(0u, i2c.ReadDoubleWord(0x2c));
            Assert.IsFalse(irq.State, "The read cleared the only pending interrupt");
        }

        private Machine machine;
        private RP2_I2C i2c;
        private GPIOReceiver irq;

        private const uint TxOver = 1u << 3;

        private sealed class GPIOReceiver : IGPIOReceiver
        {
            public void OnGPIO(int number, bool value) => State = value;
            public void Reset() => State = false;
            public bool State { get; private set; }
        }
    }
}
