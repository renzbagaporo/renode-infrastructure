//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_SSITests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 2 * 1024 * 1024);
            flash = new WinbondW25QFlash(machine, memory, quadEnabled: false);
            ssi = new RP2040_SSI(machine, frequency: 1000000);
            machine.SystemBus.Register(ssi, new BusPointRegistration(0x18000000));
            ssi.Register(flash, 0);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldReadUniqueIdAfterFourDummyBytes()
        {
            flash.Transmit(0x4b);
            for(var i = 0; i < 4; i++) Assert.AreEqual(0xff, flash.Transmit(0));
            foreach(var expected in new byte[] { 1, 0x23, 0x45, 0x67, 0x89, 0xab, 0xcd, 0xef }) Assert.AreEqual(expected, flash.Transmit(0));
            flash.FinishTransmission();
        }

        [Test]
        public void ShouldRequireWriteEnableAndDeassertionBeforeTimedStatusWrite()
        {
            Command(1, 0, 2);
            Assert.AreEqual(0, ReadStatus(0x35));
            Command(6);
            Assert.AreEqual(2, ReadStatus(5));
            flash.Transmit(1);
            flash.Transmit(0);
            flash.Transmit(2);
            flash.FinishTransmission();
            Assert.AreEqual(3, ReadStatus(5));
            Assert.AreEqual(0, ReadStatus(0x35));
            Advance(9999);
            Assert.AreEqual(3, ReadStatus(5));
            Advance(1);
            Assert.AreEqual(0, ReadStatus(5));
            Assert.AreEqual(2, ReadStatus(0x35));
        }

        [Test]
        public void ShouldProgramWithPageWrapAndNorAndAndEraseOnlyOneSector()
        {
            memory.WriteByte(0xfe, 0xf0);
            Command(6);
            Command(2, 0, 0, 0xfe, 0x0f, 0x12, 0x34);
            Assert.AreEqual(0xf0, memory.ReadByte(0xfe));
            Advance(400);
            Assert.AreEqual(0, memory.ReadByte(0xfe));
            Assert.AreEqual(0x12, memory.ReadByte(0xff));
            Assert.AreEqual(0x34, memory.ReadByte(0));
            memory.WriteByte(4096, 0x5a);
            Command(6);
            Command(0x20, 0, 0, 0xfe);
            Assert.AreEqual(3, ReadStatus(5));
            Advance(45000);
            Assert.AreEqual(0xff, memory.ReadByte(0));
            Assert.AreEqual(0xff, memory.ReadByte(0xfe));
            Assert.AreEqual(0x5a, memory.ReadByte(4096));
        }

        [Test]
        public void ShouldNotExecuteAnIncompleteProgramOrErase()
        {
            memory.WriteByte(0, 0x5a);
            Command(6);
            Command(0x20, 0, 0);
            Advance(100000);
            Assert.AreEqual(0x5a, memory.ReadByte(0));
            Assert.AreEqual(2, ReadStatus(5));
            Command(2, 0, 0, 0);
            Assert.AreEqual(2, ReadStatus(5));
        }

        [Test]
        public void ShouldDeriveBusyAndReceiveFlagsFromFrameProgress()
        {
            Configure();
            ssi.WriteDoubleWord(0x60, 0x9f);
            ssi.WriteDoubleWord(0x60, 0);
            ssi.WriteDoubleWord(0x60, 0);
            ssi.WriteDoubleWord(0x60, 0);
            Assert.AreEqual(3u, ssi.ReadDoubleWord(0x20));
            Assert.AreEqual(1u, ssi.ReadDoubleWord(0x28) & 1);
            Assert.AreEqual(0u, ssi.ReadDoubleWord(0x24));
            Advance(15);
            Assert.AreEqual(0u, ssi.ReadDoubleWord(0x24));
            Advance(1);
            Assert.AreEqual(1u, ssi.ReadDoubleWord(0x24));
            Advance(48);
            Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            foreach(var expected in new uint[] { 0xff, 0xef, 0x40, 0x15 }) Assert.AreEqual(expected, ssi.ReadDoubleWord(0x60));
        }

        [Test]
        public void ShouldEndAutomaticTransactionsAndPreserveForcedLowAcrossFifoGaps()
        {
            Configure();
            ssi.WriteDoubleWord(0x60, 6);
            Advance(16);
            Assert.AreEqual(2, ReadStatus(5));
            ssi.ChipSelectOverride = 2;
            ssi.WriteDoubleWord(0x60, 1);
            Advance(16);
            ssi.WriteDoubleWord(0x60, 0);
            Advance(16);
            ssi.WriteDoubleWord(0x60, 2);
            Advance(16);
            ssi.ChipSelectOverride = 3;
            Assert.AreEqual(3, ReadStatus(5));
            Advance(10000);
            Assert.AreEqual(2, ReadStatus(0x35));
        }

        [Test]
        public void ShouldConfigureThroughAtomicAliases()
        {
            Configure();
            ssi.WriteDoubleWord(0x3008, 1);
            Assert.AreEqual(0u, ssi.ReadDoubleWord(8));
            ssi.WriteDoubleWord(0x14, 10);
            ssi.WriteDoubleWord(0x2008, 1);
            Assert.AreEqual(1u, ssi.ReadDoubleWord(0x1008));
            ssi.WriteDoubleWord(0x60, 6);
            Advance(79);
            Assert.AreEqual(1u, ssi.ReadDoubleWord(0x28) & 1);
            Advance(1);
            Assert.AreEqual(2, ReadStatus(5));
        }

        [Test]
        public void ShouldIgnoreCommandsWhileBusyAndRequireQeForQuadRead()
        {
            memory.WriteByte(0, 0x12);
            Command(6);
            Command(2, 0, 0, 0, 0);
            Command(4);
            Command(6);
            Command(0x20, 0, 0, 0);
            Advance(400);
            Assert.AreEqual(0, ReadStatus(5));
            Assert.AreEqual(0, memory.ReadByte(0));
            flash.Transmit(0x6b);
            for(var i = 0; i < 4; i++) flash.Transmit(0);
            Assert.AreEqual(0xff, flash.Transmit(0));
            flash.FinishTransmission();
        }

        [Test]
        public void ShouldProtectEntireEraseBlockAndProgramPage()
        {
            Command(6);
            // Protect the bottom 4 KiB sector (SEC, TB, BP0).
            Command(1, 0x64, 0);
            Advance(10000);
            memory.WriteByte(0, 0x12);
            memory.WriteByte(4096, 0x34);
            Command(6);
            Command(0x52, 0, 0, 0);
            Advance(120000);
            Assert.AreEqual(0x12, memory.ReadByte(0));
            Assert.AreEqual(0x34, memory.ReadByte(4096));
            Command(2, 0, 0, 0, 0);
            Advance(400);
            Assert.AreEqual(0x12, memory.ReadByte(0));
        }

        [Test]
        public void ShouldSendQuadEepromInstructionAndAddressAtConfiguredWidths()
        {
            Command(6);
            Command(1, 0, 2);
            Advance(10000);
            memory.WriteByte(0x100, 0x12);
            memory.WriteByte(0x101, 0x34);
            memory.WriteByte(0x102, 0x56);
            memory.WriteByte(0x103, 0x78);
            ssi.WriteDoubleWord(0, (2u << 21) | (31u << 16) | (3u << 8));
            ssi.WriteDoubleWord(0x14, 2);
            ssi.WriteDoubleWord(0xf4, 1u | (8u << 2) | (2u << 8) | (4u << 11));
            ssi.WriteDoubleWord(0x10, 1);
            ssi.WriteDoubleWord(8, 1);
            ssi.WriteDoubleWord(0x60, 0xeb);
            Assert.AreEqual(1u, ssi.ReadDoubleWord(0x20));
            ssi.WriteDoubleWord(0x60, 0x000100a0);
            Advance(56);
            Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            Assert.AreEqual(1u, ssi.ReadDoubleWord(0x24));
            Assert.AreEqual(0x12345678u, ssi.ReadDoubleWord(0x60));
        }

        private void Configure()
        {
            ssi.WriteDoubleWord(0, 7u << 16);
            ssi.WriteDoubleWord(0x14, 2);
            ssi.WriteDoubleWord(0x10, 1);
            ssi.WriteDoubleWord(8, 1);
        }

        private void Command(params byte[] data)
        {
            foreach(var value in data) flash.Transmit(value);
            flash.FinishTransmission();
        }

        private byte ReadStatus(byte command)
        {
            flash.Transmit(command);
            var value = flash.Transmit(0);
            flash.FinishTransmission();
            return value;
        }

        private void Advance(ulong microseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));
        private Machine machine;
        private MappedMemory memory;
        private WinbondW25QFlash flash;
        private RP2040_SSI ssi;
    }
}
