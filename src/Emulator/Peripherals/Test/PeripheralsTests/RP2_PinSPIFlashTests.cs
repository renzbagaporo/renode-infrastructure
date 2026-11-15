//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // pico-examples pio/spi's spi_cpha0 controller (SPI mode 0 on PIO) against
    // the pin-level flash, with CS on another state machine's SET pin.
    [TestFixture]
    public class RP2_PinSPIFlashTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            pio = new RP2040_PIO(machine, gpio, frequency: 1000000);
            flash = new RP2_PinSPIFlash(machine, gpio, Cs, Sck, Mosi, Miso);
            foreach(var pin in new[] { Miso, Cs, Sck, Mosi }) gpio.WriteDoubleWord(pin * 8 + 4, 6); // FUNCSEL = PIO0
            // SM1 drives CS from its SET pin, high (deselected) to start.
            pio.WriteDoubleWord(0xdc + 0x18, (1u << 26) | ((uint)Cs << 5));
            Force(1, 0xe001); // set pins, 1
            Force(1, 0xe081); // set pindirs, 1
            // SM0: SCK is side-set pin 0, MOSI OUT pin 0, MISO IN pin 0.
            pio.WriteDoubleWord(0xdc, (1u << 29) | (2u << 26) | (1u << 20) | ((uint)Miso << 15) | ((uint)Sck << 10) | ((uint)Sck << 5) | Mosi);
            Force(0, 0xe083); // set pindirs, 3: SCK and MOSI out
            pio.WriteDoubleWord(0xd0, (1u << 16) | (1u << 17) | (8u << 20) | (8u << 25)); // autopush, autopull, 8 bits, MSB first
            pio.WriteDoubleWord(0xcc, 1u << 12); // wrap 0 to 1
            pio.WriteDoubleWord(0x48, 0x6101); // out pins, 1 side 0 [1]
            pio.WriteDoubleWord(0x4c, 0x5101); // in pins, 1 side 1 [1]
            pio.WriteDoubleWord(0, 1);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldAnswerTheJEDECID()
        {
            CollectionAssert.AreEqual(new byte[] { 0xff, 0xef, 0x40, 0x15 }, Transfer(0x9f, 0, 0, 0));
        }

        [Test]
        public void ShouldStartOnTheFirstFallingEdgeWhenSCKIsHighAtSelect()
        {
            // A controller may still hold SCK high from its last trailing edge
            // when CS falls: the flash then shifts out on the first falling edge
            // (SPI mode 3) instead of at the select.
            Force(0, 0xb042); // nop side 1: SCK high
            Advance(4);
            CollectionAssert.AreEqual(new byte[] { 0xff, 0xef, 0x40, 0x15 }, Transfer(0x9f, 0, 0, 0));
        }

        [Test]
        public void ShouldEraseProgramAndReadBackAsTheExampleDoes()
        {
            Transfer(0x06);
            Transfer(0x20, 0x00, 0x00, 0x00); // sector erase 0
            Assert.AreEqual(0, Transfer(0x05, 0)[1] & 1, "BUSY after the erase");
            Transfer(0x06);
            Transfer(0x02, 0x00, 0x01, 0x00, 0x11, 0x22, 0x33); // program at 0x100
            CollectionAssert.AreEqual(new byte[] { 0x11, 0x22, 0x33, 0xff }, Transfer(0x03, 0x00, 0x01, 0x00, 0, 0, 0, 0).Skip(4).ToArray());
            Assert.AreEqual(0, Transfer(0x05, 0)[1] & 2, "WEL after the program");
        }

        [Test]
        public void ShouldProgramAndReadAWholePage()
        {
            Transfer(0x06);
            Transfer(0x20, 0, 0, 0);
            Transfer(0x06);
            Transfer(new byte[] { 0x02, 0, 0, 0 }.Concat(Enumerable.Range(0, 256).Select(i => (byte)i)).ToArray());
            var read = Transfer(new byte[] { 0x03, 0, 0, 0 }.Concat(new byte[256]).ToArray()).Skip(4).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(), read);
        }

        [Test]
        public void ShouldIgnoreProgramWithoutWriteEnable()
        {
            Transfer(0x02, 0x00, 0x00, 0x10, 0x00);
            Assert.AreEqual(0xff, flash.ReadByte(0x10));
        }

        [Test]
        public void ShouldLogEachCSWindowOfMOSIBytes()
        {
            Transfer(0x06);
            Transfer(0x03, 0x01, 0x02, 0x03, 0x00);
            Assert.AreEqual("06\n03 01 02 03 00", flash.TakeMosiLog());
        }

        private byte[] Transfer(params byte[] bytes)
        {
            Force(1, 0xe000); // CS low
            Advance(4);
            var received = bytes.Select(b =>
            {
                pio.WriteDoubleWord(0x10, (uint)b << 24);
                for(var i = 0; i < 100 && ((pio.ReadDoubleWord(0x0c) >> 4) & 0xf) == 0; i++) Advance(4);
                return (byte)pio.ReadDoubleWord(0x20);
            }).ToArray();
            Advance(4);
            Force(1, 0xe001); // CS high
            Advance(4);
            return received;
        }

        private void Force(int sm, ushort instruction) => pio.WriteDoubleWord(0xd8 + sm * 0x18, instruction);
        private void Advance(ulong microseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2040_PIO pio;
        private RP2_PinSPIFlash flash;

        private const int Miso = 16;
        private const int Cs = 17;
        private const int Sck = 18;
        private const int Mosi = 19;
    }
}
