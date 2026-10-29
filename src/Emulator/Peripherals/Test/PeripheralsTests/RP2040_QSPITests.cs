//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_QSPITests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            sio = new RP2040_SIO(machine);
            ssi = new RP2040_SSI(machine);
            gpio = new RP2040_QSPI(machine, sio, ssi);
            pads = new RP2040_PadsQSPI(machine, gpio);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldResetMuxAndPadsForFlashBoot()
        {
            Assert.AreEqual(6, gpio.Connections.Count);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0xc));
            Assert.AreEqual(0x56u, pads.ReadDoubleWord(4));
            Assert.AreEqual(0x5au, pads.ReadDoubleWord(8));
            Assert.AreEqual(0x3eu, sio.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldRouteSixQSPIBitsToSIOAfterGPIOThirty()
        {
            gpio.WriteDoubleWord(0xc, 5);
            sio.WriteDoubleWord(0x30, 2);
            sio.WriteDoubleWord(0x40, 2);
            Assert.IsTrue(gpio.Connections[1].IsSet);
            Assert.AreEqual(0x3eu, sio.ReadDoubleWord(8));
            sio.WriteDoubleWord(0x38, 2);
            Assert.IsFalse(gpio.Connections[1].IsSet);
            Assert.AreEqual(0x3cu, sio.ReadDoubleWord(8));
            Assert.AreEqual(0u, sio.ReadDoubleWord(4));
        }

        [Test]
        public void ShouldForwardAtomicChipSelectOverrides()
        {
            gpio.WriteDoubleWord(0x200c, 2u << 8);
            Assert.AreEqual(2u, ssi.ChipSelectOverride);
            gpio.WriteDoubleWord(0x100c, 1u << 8);
            Assert.AreEqual(3u, ssi.ChipSelectOverride);
            gpio.WriteDoubleWord(0x300c, 3u << 8);
            Assert.AreEqual(0u, ssi.ChipSelectOverride);
        }

        [Test]
        public void ShouldExposePadInputBeforeAndAfterInputOverride()
        {
            gpio.OnGPIO(2, false);
            gpio.WriteDoubleWord(0x14, 3u << 16);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x10) & (1u << 17));
            Assert.AreEqual(1u << 19, gpio.ReadDoubleWord(0x10) & (1u << 19));
            Assert.AreEqual(4u, sio.ReadDoubleWord(8) & 4u);
        }

        [Test]
        public void ShouldTranslateCompressedInterruptRegistersForAllDestinations()
        {
            gpio.WriteDoubleWord(0x34, 8u << 8);
            gpio.WriteDoubleWord(0x40, 8u << 8);
            gpio.WriteDoubleWord(0x4c, 8u << 8);
            gpio.OnGPIO(2, false);
            gpio.OnGPIO(2, true);
            Assert.IsTrue(gpio.Core0IRQ.IsSet);
            Assert.IsTrue(gpio.Core1IRQ.IsSet);
            Assert.IsTrue(gpio.DormantWakeIRQ.IsSet);
            Assert.AreEqual(8u << 8, gpio.ReadDoubleWord(0x3c));
            gpio.WriteDoubleWord(0x30, 8u << 8);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.IsFalse(gpio.Core1IRQ.IsSet);
            Assert.IsFalse(gpio.DormantWakeIRQ.IsSet);
        }

        [Test]
        public void ShouldDisableInputThroughPadsAndReplicateNarrowWrites()
        {
            pads.WriteByte(8, 0x1a);
            Assert.AreEqual(0x1au, pads.ReadDoubleWord(8));
            Assert.IsFalse(gpio.GetInput(1));
            pads.WriteDoubleWord(0x2008, 0x40);
            Assert.IsTrue(gpio.GetInput(1));
        }

        [Test]
        public void ShouldHoldWritesDuringQSPIResetAndRestoreAutomaticChipSelect()
        {
            gpio.WriteDoubleWord(0xc, 3u << 8);
            gpio.ResetHeld = true;
            Assert.AreEqual(0u, ssi.ChipSelectOverride);
            gpio.WriteDoubleWord(0xc, 2u << 8);
            Assert.AreEqual(0u, ssi.ChipSelectOverride);
            gpio.ResetHeld = false;
            gpio.WriteDoubleWord(0xc, 2u << 8);
            Assert.AreEqual(2u, ssi.ChipSelectOverride);
        }

        [Test]
        public void ShouldSuppressEdgeCaptureWhileHeldInReset()
        {
            gpio.ResetHeld = true;
            gpio.OnGPIO(2, false);
            gpio.OnGPIO(2, true);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x30) & 0x00cccccc);
            gpio.ResetHeld = false;
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x30) & 0x00cccccc);
            gpio.OnGPIO(2, false);
            Assert.AreEqual(4u << 8, gpio.ReadDoubleWord(0x30) & 0x00cccccc);
        }

        [Test]
        public void ShouldKeepPadResetSeparateFromIOMuxReset()
        {
            gpio.WriteDoubleWord(0xc, 5);
            pads.WriteDoubleWord(8, 0x52);
            gpio.Reset();
            Assert.AreEqual(0x52u, pads.ReadDoubleWord(8));
            gpio.WriteDoubleWord(0xc, 5);
            pads.Reset();
            Assert.AreEqual(5u, gpio.ReadDoubleWord(0xc));
        }

        [Test]
        public void ShouldPublishChipSelectChangesAfterReleasingGPIOStateLock()
        {
            ssi.ConfigurationChanged += () =>
            {
                var read = Task.Run(() => gpio.ReadDoubleWord(0xc));
                Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(1)), "QSPI state lock was held while notifying SSI");
            };
            gpio.WriteDoubleWord(0xc, 2u << 8);
            Assert.AreEqual(2u, ssi.ChipSelectOverride);
        }

        [Test]
        public void ShouldPublishPadChangesAfterReleasingSharedGPIOStateLock()
        {
            gpio.Connections[1].Connect(new PadReader(pads), 0);
            pads.WriteDoubleWord(8, 0x56);
            Assert.IsFalse(gpio.Connections[1].IsSet);
        }

        private sealed class PadReader : IGPIOReceiver
        {
            public PadReader(RP2040_PadsQSPI pads)
            {
                this.pads = pads;
            }

            public void OnGPIO(int number, bool value)
            {
                var read = Task.Run(() => pads.ReadDoubleWord(8));
                Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(1)), "Pad state lock was held while publishing a wire change");
            }

            public void Reset()
            {
            }

            private readonly RP2040_PadsQSPI pads;
        }

        private Machine machine;
        private RP2040_SIO sio;
        private RP2040_SSI ssi;
        private RP2040_QSPI gpio;
        private RP2040_PadsQSPI pads;
    }
}
