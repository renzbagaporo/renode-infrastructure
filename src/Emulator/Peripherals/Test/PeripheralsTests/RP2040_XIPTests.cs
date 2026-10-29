//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_XIPTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 0x200000);
            ssi = new RP2040_SSI(machine);
            machine.SystemBus.Register(ssi, new BusRangeRegistration(0x18000000UL, (ulong)ssi.Size));
            flash = new WinbondW25QFlash(machine, memory);
            ssi.Register(flash, new NumberRegistrationPoint<int>(0));
            xip = new RP2040_XIP(machine, ssi, flash);
            var window = new RP2040_XIPMemory(machine, flash, xip);
            foreach(var address in Addresses)
            {
                machine.SystemBus.Register(window, new BusRangeRegistration(address, (ulong)memory.Size));
            }
            memory.WriteByte(0, 0x42);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldBlockAllAliasesUntilSSIHasAValidReadConfiguration()
        {
            CheckAliases(0);
            Configure(0x03000218);
            Assert.IsTrue(xip.Available);
            CheckAliases(0x42);
            ssi.WriteDoubleWord(8, 0);
            Assert.IsFalse(xip.Available);
            CheckAliases(0);
        }

        [Test]
        public void ShouldBlockUnsupportedReadInstructionsAndChipSelectOverrides()
        {
            Configure(0x9f000218);
            Assert.IsFalse(xip.Available);
            CheckAliases(0);
            ssi.WriteDoubleWord(8, 0);
            Configure(0x03000218);
            ssi.ChipSelectOverride = 3;
            CheckAliases(0);
            ssi.ChipSelectOverride = 0;
            CheckAliases(0x42);
        }

        [Test]
        public void ShouldRequireFlashContinuousReadModeForInstructionlessQuadReads()
        {
            Configure(0xa000001a, quad: true);
            CheckAliases(0);
            Command(0xeb, 0, 0, 0, 0xa0, 0, 0);
            Assert.IsTrue(flash.ContinuousReadEnabled);
            CheckAliases(0x42);
            Command(0xff);
            CheckAliases(0);
        }

        [Test]
        public void ShouldBlockDuringPageProgrammingAndExposeCompletedBytesThroughEveryAlias()
        {
            Configure(0x03000218);
            Command(6);
            Command(2, 0, 0, 0, 0x02);
            Assert.IsFalse(xip.Available);
            CheckAliases(0);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(400));
            Assert.IsTrue(xip.Available);
            CheckAliases(0x02);
        }

        [Test]
        public void ShouldKeepUncachedReadsAvailableWhenCacheIsDisabled()
        {
            Configure(0x03000218);
            xip.WriteDoubleWord(0x3000, 1);
            CheckAliases(0x42);
            xip.WriteDoubleWord(4, 1);
            Assert.AreEqual(1u, xip.ReadDoubleWord(4));
            Assert.AreEqual(3u, xip.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldIgnoreDirectAndBulkXIPWritesWithoutProgrammingNOR()
        {
            Configure(0x03000218);
            foreach(var address in Addresses)
            {
                machine.SystemBus.WriteByte(address, 0);
                machine.SystemBus.WriteDoubleWord(address, 0);
                machine.SystemBus.WriteBytes(new byte[] { 0, 0, 0, 0 }, address);
                Assert.AreEqual(0x42, memory.ReadByte(0));
            }
        }

        private void Configure(uint serialControl, bool quad = false)
        {
            ssi.WriteDoubleWord(8, 0);
            ssi.WriteDoubleWord(0, 0x001f0300u | (quad ? 2u << 21 : 0));
            ssi.WriteDoubleWord(0x14, 2);
            ssi.WriteDoubleWord(0xf4, serialControl);
            ssi.WriteDoubleWord(0x10, 1);
            ssi.WriteDoubleWord(8, 1);
        }

        private void Command(params byte[] bytes)
        {
            foreach(var value in bytes)
            {
                flash.Transmit(value);
            }
            flash.FinishTransmission();
        }

        private void CheckAliases(byte expected)
        {
            foreach(var address in Addresses)
            {
                Assert.AreEqual(expected, machine.SystemBus.ReadByte(address));
            }
        }

        private static readonly ulong[] Addresses = { 0x10000000, 0x11000000, 0x12000000, 0x13000000 };
        private Machine machine;
        private MappedMemory memory;
        private RP2040_SSI ssi;
        private WinbondW25QFlash flash;
        private RP2040_XIP xip;
    }
}
