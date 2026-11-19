//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_SPITests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            spi = new RP2_SPI(machine, gpio: gpio, index: 0);
            for(var pin = Rx; pin <= Tx; pin++) gpio.WriteDoubleWord(pin * 8 + 4, 1); // FUNCSEL SPI
            gpio.DriveChanged += (pin, value, enabled) =>
            {
                if(pin != Tx) return;
                txLevel = value;
                txDriven = enabled;
            };
            gpio.OnGPIO(Select, true);
            gpio.OnGPIO(Clock, false);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldExchangeAFrameAsASlave(bool secondEdge)
        {
            spi.WriteDoubleWord(0x00, 7 | (secondEdge ? 0x80u : 0)); // 8 bits, SPO 0
            spi.WriteDoubleWord(0x10, 2);
            spi.WriteDoubleWord(0x04, 2 | 4); // SSE, MS
            spi.WriteDoubleWord(0x08, 0xa5);
            Assert.IsFalse(txDriven, "An unselected slave leaves TX undriven");
            gpio.OnGPIO(Select, false);
            Assert.IsTrue(txDriven);
            var sent = 0;
            const int received = 0x3c;
            for(var bit = 7; bit >= 0; bit--)
            {
                gpio.OnGPIO(Rx, ((received >> bit) & 1) != 0);
                if(!secondEdge)
                {
                    // SPH=0: the slave's bit is valid before the sampling (rising) edge.
                    sent = (sent << 1) | (txLevel ? 1 : 0);
                    gpio.OnGPIO(Clock, true);
                    gpio.OnGPIO(Clock, false);
                }
                else
                {
                    // SPH=1: the slave shifts on the rising edge; both sample on the falling one.
                    gpio.OnGPIO(Clock, true);
                    sent = (sent << 1) | (txLevel ? 1 : 0);
                    gpio.OnGPIO(Clock, false);
                }
            }
            Assert.AreEqual(0xa5, sent, "MISO carried the transmit FIFO frame");
            Assert.AreEqual(4u, spi.ReadDoubleWord(0x0c) & 4, "RNE");
            Assert.AreEqual((uint)received, spi.ReadDoubleWord(0x08));
            gpio.OnGPIO(Select, true);
            Assert.IsFalse(txDriven);
        }

        [Test]
        public void ShouldLoopAFrameBackInsideTheBlockWithoutAskingItsTarget()
        {
            var asked = 0;
            spi.FrameTarget = (mosi, bits, at) => { asked++; return 0x00; };
            spi.WriteDoubleWord(0x00, 7); // 8 bits
            spi.WriteDoubleWord(0x10, 2);
            spi.WriteDoubleWord(0x04, 2 | 1); // SSE, LBM
            spi.WriteDoubleWord(0x08, 0xa5);
            AdvanceMicroseconds(100);
            Assert.AreEqual(0, asked, "a loopback frame reaches no target");
            Assert.AreEqual(0xa5u, spi.ReadDoubleWord(0x08), "the block receives its own frame");
        }

        [Test]
        public void ShouldExchangeWholeFramesClockedInAsASlave()
        {
            spi.WriteDoubleWord(0x00, 7);
            spi.WriteDoubleWord(0x10, 2);
            Assert.IsNull(spi.ClockSlaveFrame(0x41), "A disabled block takes no frame");
            spi.WriteDoubleWord(0x04, 2); // SSE as a master
            Assert.IsNull(spi.ClockSlaveFrame(0x41), "A master takes no frame clocked in");
            spi.WriteDoubleWord(0x04, 2 | 4); // SSE, MS
            spi.WriteDoubleWord(0x08, 0xa5);
            Assert.AreEqual((ushort)0xa5, spi.ClockSlaveFrame(0x3c), "The transmit FIFO's frame shifted out");
            Assert.AreEqual((ushort)0, spi.ClockSlaveFrame(0x1c3), "An empty FIFO shifts out zeros");
            Assert.AreEqual(0x3cu, spi.ReadDoubleWord(0x08));
            Assert.AreEqual(0xc3u, spi.ReadDoubleWord(0x08), "Only the frame's bits are received");
            Assert.AreEqual(0u, spi.ReadDoubleWord(0x0c) & 4, "RNE");
            for(var frame = 0; frame < 9; frame++) spi.ClockSlaveFrame((ushort)frame);
            Assert.AreEqual(1u, spi.ReadDoubleWord(0x18) & 1, "A ninth unread frame overruns");
            spi.WriteDoubleWord(0x04, 2 | 4 | 8); // SOD
            Assert.IsNull(spi.ClockSlaveFrame(0), "SOD keeps the output off");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldSendEachFrameOnceAcrossChipSelectPulses(bool pulse)
        {
            spi.WriteDoubleWord(0x00, 7);
            spi.WriteDoubleWord(0x10, 2);
            spi.WriteDoubleWord(0x04, 2 | 4);
            spi.WriteDoubleWord(0x08, 0x81);
            spi.WriteDoubleWord(0x08, 0x42);
            gpio.OnGPIO(Select, false);
            var first = Exchange();
            if(pulse)
            {
                gpio.OnGPIO(Select, true);
                gpio.OnGPIO(Select, false);
            }
            var second = Exchange();
            gpio.OnGPIO(Select, true);
            Assert.AreEqual(0x81, first);
            Assert.AreEqual(0x42, second);
            Assert.AreEqual(1u, spi.ReadDoubleWord(0x0c) & 1, "Both frames left the transmit FIFO");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldPulseSelectBetweenBackToBackMasterFramesOnlyWithTheFirstEdgeSampling(bool secondEdge)
        {
            var select = new System.Collections.Generic.List<(bool Level, ulong Microseconds)>();
            gpio.DriveChanged += (pin, value, enabled) =>
            {
                if(pin == Select && enabled) select.Add((value, (ulong)machine.ClockSource.CurrentValue.TotalMicroseconds));
            };
            // 8 bits, SCR 124 with CPSDVSR 2: a 1 us half period at 125 MHz.
            spi.WriteDoubleWord(0x00, 7 | (124u << 8) | (secondEdge ? 0x80u : 0));
            spi.WriteDoubleWord(0x10, 2);
            spi.WriteDoubleWord(0x08, 0x81);
            spi.WriteDoubleWord(0x08, 0x42);
            spi.WriteDoubleWord(0x04, 2);
            for(var i = 0; i < 60; i++) AdvanceMicroseconds(1);
            var levels = select.ConvertAll(change => change.Level);
            if(secondEdge)
            {
                CollectionAssert.AreEqual(new[] { true, false, true }, levels, "Idle high, then SPH=1 holds FSS low across the frames");
                return;
            }
            CollectionAssert.AreEqual(new[] { true, false, true, false, true }, levels, "Idle high, then SPH=0 pulses FSS between the frames");
            Assert.AreEqual(16ul, select[2].Microseconds, "FSS rises after the first frame's 16 half periods");
            Assert.AreEqual(18ul, select[3].Microseconds, "and stays high for one SCK period");
            spi.ReadDoubleWord(0x08);
            Assert.AreEqual(4u, spi.ReadDoubleWord(0x0c) & 4, "Both frames were received");
            spi.ReadDoubleWord(0x08);
            Assert.AreEqual(0u, spi.ReadDoubleWord(0x0c) & 4);
        }

        private void AdvanceMicroseconds(ulong microseconds) =>
            ((Antmicro.Renode.Time.BaseClockSource)machine.ClockSource).Advance(Antmicro.Renode.Time.TimeInterval.FromMicroseconds(microseconds));

        [Test]
        public void ShouldTakeAnSph0SlaveFrameWrittenAfterTheLastWindowAtTheNextSelect()
        {
            spi.WriteDoubleWord(0x00, 7);
            spi.WriteDoubleWord(0x10, 2);
            spi.WriteDoubleWord(0x04, 2 | 4);
            spi.WriteDoubleWord(0x08, 0x81);
            gpio.OnGPIO(Select, false);
            Assert.AreEqual(0x81, Exchange());
            gpio.OnGPIO(Select, true);
            // The transmit FIFO was empty when that window ended.
            spi.WriteDoubleWord(0x08, 0x5a);
            gpio.OnGPIO(Select, false);
            Assert.AreEqual(0x5a, Exchange(), "The frame written between windows goes out in the next one");
            gpio.OnGPIO(Select, true);
        }

        // One SPH=0 frame from the master's side: returns the bits on MISO.
        private int Exchange()
        {
            var sent = 0;
            for(var bit = 0; bit < 8; bit++)
            {
                sent = (sent << 1) | (txLevel ? 1 : 0);
                gpio.OnGPIO(Clock, true);
                gpio.OnGPIO(Clock, false);
            }
            return sent;
        }

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2_SPI spi;
        private bool txLevel, txDriven;

        private const int Rx = 16, Select = 17, Clock = 18, Tx = 19;
    }
}
