//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_OTPTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            otp = new RP2350_OTP(machine);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldProgramARowThroughTheBootROMSBPISequence()
        {
            // ECC encoding of "He" (0x6548), as the boot ROM computes it.
            const uint value = 0x096548;
            otp.WriteDoubleWord(Usr, 0); // DCTRL off: SBPI owns the fuses
            for(var i = 0; i < 12; i++) WriteByte(Dap, 0x30 + i, 0);
            WriteByte(Pmc, 0x3f, 0xa);
            Program(0x400, value);
            otp.WriteDoubleWord(Usr, 1);
            Assert.AreEqual(value, otp.ReadRaw(0x400));
            Assert.IsTrue(otp.TryReadECC(0x400, out var data));
            Assert.AreEqual(0x6548, data);
        }

        [Test]
        public void ShouldOnlySetFuseBits()
        {
            otp.WriteDoubleWord(Usr, 0);
            Program(0x410, 0x00f00f);
            Program(0x410, 0x0f0000);
            Assert.AreEqual(0x0ff00fu, otp.ReadRaw(0x410));
        }

        [Test]
        public void ShouldReportCompletionAndClearItOnWrite()
        {
            otp.WriteDoubleWord(Instruction, 1u << 29 | 1u << 28 | Dap << 16 | 0xfcu << 8 | 0x12 | ExecuteBit);
            Assert.AreEqual(Done, otp.ReadDoubleWord(Status) & Done);
            otp.WriteDoubleWord(Status, Done);
            Assert.AreEqual(0u, otp.ReadDoubleWord(Status) & Done);
            Assert.AreEqual(0u, otp.ReadDoubleWord(Instruction) & ExecuteBit, "EXEC self-clears");
            otp.WriteDoubleWord(Instruction, 1u << 28 | Dap << 16 | 0xbcu << 8 | ExecuteBit);
            Assert.AreEqual(1u, otp.ReadDoubleWord(Status) & 1, "RDATA_VLD");
            Assert.AreEqual(0x12u, otp.ReadDoubleWord(ReadData0));
        }

        [Test]
        public void ShouldCorrectEachECCRowOfAWordOnItsOwn()
        {
            // "He" with 'H' turned into 'x' (two flipped bits), then "ll".
            otp.WriteDoubleWord(Usr, 0);
            Program(0x400, 0x096548 | 0x30);
            Program(0x401, 0x396c6c);
            otp.WriteDoubleWord(Usr, 1);
            Assert.IsTrue(otp.TryReadECC(0x401, out var good));
            Assert.AreEqual(0x6c6c, good);
            var data = new RP2350_OTPData(machine, otp);
            Assert.AreEqual(0xffff, data.ReadWord(0x800), "The uncorrectable row reads as all ones");
            Assert.AreEqual(good, data.ReadWord(0x802));
            Assert.AreEqual(0xffffu | (uint)good << 16, data.ReadDoubleWord(0x800));
        }

        // The boot ROM's s_varm_otp_program_blocking.
        private void Program(int row, uint data)
        {
            WriteByte(Dap, 0x3c, (uint)row & 0xff);
            WriteByte(Dap, 0x3d, (uint)row >> 8);
            WriteByte(Dap, 0x00, data & 0xff);
            WriteByte(Dap, 0x01, (data >> 8) & 0xff);
            WriteByte(Dap, 0x20, (data >> 16) & 0xff);
            Execute(1u << 29 | Pmc << 16 | 0x01 << 8); // START
            while((ReadByte(Pmc, 0x3f) & 0x80) != 0) { }
            Execute(1u << 29 | Pmc << 16 | 0x02 << 8); // STOP
        }

        private void WriteByte(uint target, int address, uint data) =>
            Execute(1u << 29 | 1u << 28 | target << 16 | (0xc0u | (uint)address) << 8 | (data & 0xff));

        private uint ReadByte(uint target, int address)
        {
            Execute(1u << 28 | target << 16 | (0x80u | (uint)address) << 8);
            return otp.ReadDoubleWord(ReadData0) & 0xff;
        }

        private void Execute(uint instruction)
        {
            otp.WriteDoubleWord(Instruction, instruction | ExecuteBit);
            Assert.AreNotEqual(0u, otp.ReadDoubleWord(Status) & (Done | Miss), "The instruction completed");
            otp.WriteDoubleWord(Status, Done | Miss);
        }

        private Machine machine;
        private RP2350_OTP otp;

        private const long Instruction = 0x100, ReadData0 = 0x114, Status = 0x124, Usr = 0x128;
        private const uint ExecuteBit = 1u << 30, Done = 1u << 4, Miss = 1u << 8;
        private const uint Dap = 0x02, Pmc = 0x3a;
    }
}
