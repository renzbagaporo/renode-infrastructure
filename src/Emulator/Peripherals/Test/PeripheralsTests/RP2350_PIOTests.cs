//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_PIOTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine, rp2350: true);
            pio = new RP2040_PIO(machine, gpio, frequency: 1000000, rp2350: true);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldExposeTheV1RegisterMap()
        {
            Assert.AreEqual(0x10200404u, pio.ReadDoubleWord(0x44));
            pio.WriteDoubleWord(0x168, 0x11);
            Assert.AreEqual(0x10u, pio.ReadDoubleWord(0x168));
            // IRQ flags 4-7 reach INTR bits 12-15 on the RP2350.
            pio.WriteDoubleWord(0x34, 0x20);
            Assert.AreEqual(1u << 13, pio.ReadDoubleWord(0x16c) & 0xff00);
            pio.WriteDoubleWord(0x170, 1u << 13);
            Assert.AreEqual(1u << 13, pio.ReadDoubleWord(0x170));
            Assert.AreEqual(1u << 13, pio.ReadDoubleWord(0x178) & 0xff00);
            Assert.IsTrue(pio.IRQ0.IsSet);
        }

        [Test]
        public void ShouldDriveRelocatedPinsWithGPIOBase16()
        {
            UsePin(22);
            UsePin(6);
            pio.WriteDoubleWord(0x168, 16);
            // PINCTRL: SET_BASE 6, SET_COUNT 1.
            pio.WriteDoubleWord(PinControl, (1u << 26) | (6u << 5));
            Force(0xe081); // set pindirs, 1
            Force(0xe001); // set pins, 1
            GetOutputState(22, out var value, out var enabled);
            Assert.IsTrue(value);
            Assert.IsTrue(enabled);
            GetOutputState(6, out _, out enabled);
            Assert.IsFalse(enabled);
            // Moving the window back releases GPIO 22 and drives GPIO 6.
            pio.WriteDoubleWord(0x168, 0);
            GetOutputState(22, out _, out enabled);
            Assert.IsFalse(enabled);
            GetOutputState(6, out value, out enabled);
            Assert.IsTrue(value);
            Assert.IsTrue(enabled);
        }

        [Test]
        public void ShouldRebaseWaitGPIOIndices()
        {
            UsePin(38);
            pio.WriteDoubleWord(0x168, 16);
            gpio.OnGPIO(38, false);
            Load(0x0000); // jmp 0
            pio.WriteDoubleWord(0, 1);
            Force(0x2096); // wait 1 gpio 22, which is GPIO 38
            Advance(5);
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(ExecControl) & 0x80000000);
            gpio.OnGPIO(38, true);
            Advance(5);
            Assert.AreEqual(0u, pio.ReadDoubleWord(ExecControl) & 0x80000000);
        }

        [Test]
        public void ShouldMaskInPinsToInCount()
        {
            for(var pin = 0; pin < 3; pin++)
            {
                UsePin(pin);
                gpio.OnGPIO(pin, true);
            }
            Advance(5);
            // SHIFTCTRL: OUT shifts right, IN shifts left, IN_COUNT 2.
            pio.WriteDoubleWord(ShiftControl, (1u << 19) | 2);
            Force(0x4003); // in pins, 3
            Force(0x8000); // push noblock
            Assert.AreEqual(3u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldMoveToPinDirections()
        {
            UsePin(4);
            // PINCTRL: OUT_BASE 4, OUT_COUNT 1.
            pio.WriteDoubleWord(PinControl, (1u << 20) | 4);
            Force(0xe021); // set x, 1
            Force(0xa061); // mov pindirs, x
            GetOutputState(4, out _, out var enabled);
            Assert.IsTrue(enabled);
        }

        [Test]
        public void ShouldSelectAnIRQFlagAsStatus()
        {
            // EXECCTRL: wrap top 31, STATUS_SEL 2 (IRQ), STATUS_N 3.
            pio.WriteDoubleWord(ExecControl, 0x1f000 | (2u << 5) | 3);
            Force(0xa025); // mov x, status
            Force(0xa0c1); // mov isr, x
            Force(0x8000); // push noblock
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x20));
            pio.WriteDoubleWord(0x34, 1u << 3);
            Force(0xa025);
            Force(0xa0c1);
            Force(0x8000);
            Assert.AreEqual(uint.MaxValue, pio.ReadDoubleWord(0x20));
        }

        // Route a bank pin to PIO0 (function 6) with its pad out of isolation.
        private void UsePin(int pin)
        {
            gpio.SetPadConfiguration(pin, 0x56);
            gpio.WriteDoubleWord(pin * 8 + 4, 6);
        }

        private void GetOutputState(int pin, out bool value, out bool enabled)
        {
            var status = gpio.ReadDoubleWord(pin * 8);
            value = (status & (1u << 9)) != 0;
            enabled = (status & (1u << 13)) != 0;
        }

        private void Force(ushort instruction) => pio.WriteDoubleWord(0xd8, instruction);
        private void Load(params ushort[] words)
        {
            for(var index = 0; index < words.Length; index++) pio.WriteDoubleWord(0x48 + 4 * index, words[index]);
        }
        private void Advance(ulong microseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2040_PIO pio;

        private const long ExecControl = 0xcc, ShiftControl = 0xd0, PinControl = 0xdc;
    }
}
