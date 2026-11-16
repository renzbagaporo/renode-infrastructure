//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // Two RP2_I2C blocks with no DelayedI2CBus, joined only by pulled-up wires
    // between their pads, as pico-examples slave_mem_i2c's jumpers join them.
    [TestFixture]
    public class RP2_I2CPinsTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            target = new RP2_I2C(machine, gpio: gpio, index: 0);
            controller = new RP2_I2C(machine, gpio: gpio, index: 1);
            for(var pin = 4; pin <= 7; pin++) gpio.WriteDoubleWord(pin * 8 + 4, 3); // FUNCSEL I2C
            sda = new DigitalWire(true);
            scl = new DigitalWire(true);
            sda.Connect(gpio, 4);
            sda.Connect(gpio, 6);
            scl.Connect(gpio, 5);
            scl.Connect(gpio, 7);
            target.WriteDoubleWord(Enable, 0);
            target.WriteDoubleWord(Control, 0x24); // target, fast mode, RESTART_EN
            target.WriteDoubleWord(OwnAddress, 0x17);
            target.WriteDoubleWord(Enable, 1);
            controller.WriteDoubleWord(Enable, 0);
            controller.WriteDoubleWord(Control, 0x65); // controller, fast mode
            controller.WriteDoubleWord(TargetAddress, 0x17);
            controller.WriteDoubleWord(Enable, 1);
        }

        [TearDown]
        public void TearDown()
        {
            sda.Dispose();
            scl.Dispose();
            machine.Dispose();
        }

        [Test]
        public void ShouldWriteAndReadThroughTheirPads()
        {
            controller.WriteDoubleWord(DataCommand, 0xa5);
            controller.WriteDoubleWord(DataCommand, 0x5a | StopCommand);
            AdvanceMicroseconds(200);
            Assert.AreEqual(0u, controller.ReadDoubleWord(RawInterrupts) & TxAbort, "The target acknowledged");
            Assert.AreEqual(2u, target.ReadDoubleWord(RxLevel));
            Assert.AreEqual(0xa5u, target.ReadDoubleWord(DataCommand) & 0xff);
            Assert.AreEqual(0x5au, target.ReadDoubleWord(DataCommand) & 0xff);

            controller.WriteDoubleWord(DataCommand, ReadCommand | StopCommand);
            AdvanceMicroseconds(50);
            Assert.AreNotEqual(0u, target.ReadDoubleWord(RawInterrupts) & ReadRequest, "The target holds SCL for the byte");
            Assert.AreEqual(0u, controller.ReadDoubleWord(RxLevel));
            target.WriteDoubleWord(DataCommand, 0x3c);
            AdvanceMicroseconds(200);
            Assert.AreEqual(1u, controller.ReadDoubleWord(RxLevel));
            Assert.AreEqual(0x3cu, controller.ReadDoubleWord(DataCommand) & 0xff);
        }

        [Test]
        public void ShouldAbortOnAnAddressNobodyAnswers()
        {
            controller.WriteDoubleWord(Enable, 0);
            controller.WriteDoubleWord(TargetAddress, 0x42);
            controller.WriteDoubleWord(Enable, 1);
            controller.WriteDoubleWord(DataCommand, 0x01 | StopCommand);
            AdvanceMicroseconds(200);
            Assert.AreNotEqual(0u, controller.ReadDoubleWord(RawInterrupts) & TxAbort);
            Assert.AreEqual(0u, target.ReadDoubleWord(RxLevel));
        }

        private void AdvanceMicroseconds(ulong microseconds)
        {
            for(ulong i = 0; i < microseconds; i++)
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(1));
        }

        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2_I2C target, controller;
        private DigitalWire sda, scl;

        private const long Control = 0x00, TargetAddress = 0x04, OwnAddress = 0x08, DataCommand = 0x10;
        private const long RawInterrupts = 0x34, Enable = 0x6c, RxLevel = 0x78;
        private const uint ReadCommand = 1u << 8, StopCommand = 1u << 9;
        private const uint ReadRequest = 1u << 5, TxAbort = 1u << 6;
    }
}
