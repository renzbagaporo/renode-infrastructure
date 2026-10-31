//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_DCPContextTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "dcp_context");
            var nvic = new NVIC(machine);
            cpu = new CortexM("cortex-m33-rp2350", machine, nvic, enableTrustZone: true);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
            nvic.IRQ.Connect(cpu, 0);
            code = new MappedMemory(machine, 0x10000);
            machine.SystemBus.Register(code, new BusPointRegistration(0));
            var ram = new MappedMemory(machine, 0x10000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            code.WriteDoubleWord(0, 0x20010000);
            code.WriteDoubleWord(4, 0x1001);
            code.WriteDoubleWord(0xc, 0x3001);
            code.WriteDoubleWord(0x18, 0x2001);
            code.WriteWord(0x2000, 0xe7fe);
            code.WriteWord(0x3000, 0xe7fe);
            machine.PostCreationActions();
            cpu.Reset();
            cpu.SP = 0x20010000;
            cpu.CPACR = 0xf00;
            machine.SystemBus.WriteDoubleWord(0xe000ed24, 0x40000, cpu);
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        [Test]
        public void ShouldMatchAllCapturedProcessedStatusCombinations()
        {
            // Literal NZCV nibbles from 1024 physical RP2350 observations,
            // CHIP_ID=0x30004927, DCP VERSION=4. Index: XF[9:6], YF[5:2],
            // saved comparison[1:0], engaged=1. This oracle is independent
            // of the emulator's boolean status implementation.
            // Raw sweep SHA256:
            // 0c3fc788239f1855bd21b2ceb1678f9b74d5167effc77884c84c5621a6a0b737
            Assert.AreEqual(1024, HardwareStatus.Length);
            for(var index = 0; index < 1024; index++)
            {
                var low = ((uint)(index >> 6) << 28) | ((uint)(index & 3) << 20) | 0x400000u;
                var high = (uint)((index >> 2) & 15) << 28;
                WritePair(4, 2, low, high);
                Issue(0xfe102430); // PCMP r2
                var expected = Convert.ToUInt32(HardwareStatus[index].ToString(), 16) << 28;
                Assert.AreEqual(expected, (uint)cpu.GetRegister(2), $"XF={index >> 6:x}, YF={(index >> 2) & 15:x}, CMP={index & 3}");
            }
        }

        [TestCase(4)]
        [TestCase(5)]
        public void ShouldSaveAndRestoreAllSixWordsAcrossAnInterveningContext(int cp)
        {
            WritePair(cp, 0, 0x89abcdef, 0x01234567);
            WritePair(cp, 1, 0x76543210, 0xfedcba98);
            WritePair(cp, 2, 0xb05a2345, 0xd0003210);
            var x = ReadPair(cp, 8, peek: true);
            var y = ReadPair(cp, 9, peek: true);
            var efd = ReadPair(cp, 10, peek: false);
            Assert.AreEqual(0x0123456789abcdeful, x);
            Assert.AreEqual(0xfedcba9876543210ul, y);
            Assert.AreEqual(0xd0003210b05a2345ul, efd);
            Issue(0xee000000u | (uint)cp << 8); // INIT
            Assert.AreEqual(0ul, ReadPair(cp, 8, peek: false));
            Assert.AreEqual(0ul, ReadPair(cp, 9, peek: true));
            Assert.AreEqual(0ul, ReadPair(cp, 10, peek: true));
            WritePair(cp, 0, (uint)x, (uint)(x >> 32));
            WritePair(cp, 1, (uint)y, (uint)(y >> 32));
            WritePair(cp, 2, (uint)efd, (uint)(efd >> 32));
            Assert.AreEqual(x, ReadPair(cp, 8, peek: false));
            Assert.AreEqual(y, ReadPair(cp, 9, peek: true));
            Assert.AreEqual(efd, ReadPair(cp, 10, peek: false));
            Issue(0xfe102030u | (uint)cp << 8); // PCMP r2
            Assert.AreEqual(0x90000000u, (uint)cpu.GetRegister(2), "Restored NaN and engaged flags");
        }

        [Test]
        public void ShouldMaskOnlyTheNineteenReservedContextBits()
        {
            WritePair(4, 2, uint.MaxValue, uint.MaxValue);
            Assert.AreEqual(0xf0003ffff07ffffful, ReadPair(4, 10, peek: false));
            for(var bit = 0; bit < 64; bit++)
            {
                var input = 1ul << bit;
                WritePair(4, 2, (uint)input, (uint)(input >> 32));
                Assert.AreEqual(input & 0xf0003ffff07ffffful, ReadPair(4, 10, peek: true), $"Context input bit {bit}");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldPreserveEngagedOnBothExponentFlagsReadVariants(bool peek)
        {
            WritePair(4, 0, 1, 2);
            Assert.AreEqual(0x400000ul, ReadPair(4, 10, peek));
            Assert.AreEqual(0x400000ul, ReadPair(4, 10, !peek));
            Issue(0xfe102430);
            Assert.AreEqual(0x80000000u, (uint)cpu.GetRegister(2));
        }

        [Test]
        public void ShouldRestoreDisengagedWithoutLosingOtherFields()
        {
            WritePair(4, 0, 1, 2);
            WritePair(4, 2, 0x80012345, 0x20003210);
            Assert.AreEqual(0x2000321080012345ul, ReadPair(4, 10, peek: false));
            Issue(0xfe102430);
            Assert.AreEqual(0u, (uint)cpu.GetRegister(2) & 0x80000000u);
            Assert.AreEqual(0x0000000200000001ul, ReadPair(4, 8, peek: false));
        }

        [Test]
        public void ShouldClearOnlyEngagedOnFinalYRead()
        {
            WritePair(4, 1, 3, 4);
            WritePair(4, 2, 0x80412345, 0x20003210);
            Assert.AreEqual(0x0000000400000003ul, ReadPair(4, 9, peek: true));
            Assert.AreEqual(0x2000321080412345ul, ReadPair(4, 10, peek: false));
            Assert.AreEqual(0x0000000400000003ul, ReadPair(4, 9, peek: false));
            Assert.AreEqual(0x2000321080012345ul, ReadPair(4, 10, peek: false));
        }

        [Test]
        public void ShouldKeepSecureAndNonSecureContextsIndependent()
        {
            WritePair(4, 2, 0x80412345, 0x20003210);
            WritePair(5, 2, 0x40002abc, 0x80001357);
            Assert.AreEqual(0x2000321080412345ul, ReadPair(4, 10, peek: false));
            Assert.AreEqual(0x8000135740002abcul, ReadPair(5, 10, peek: true));
            Issue(0xee000500); // INIT non-secure instance from Secure CP5.
            Assert.AreEqual(0ul, ReadPair(5, 10, peek: false));
            Assert.AreEqual(0x2000321080412345ul, ReadPair(4, 10, peek: false));
        }

        [Test]
        public void ShouldExposeRestoredClassificationAndImplementationVersion()
        {
            WritePair(4, 2, 0xd0412345, 0x20003210);
            Issue(0xfe102410); // PXVD r2
            Assert.AreEqual(0xd0000004u, (uint)cpu.GetRegister(2));
            Assert.AreEqual(0x20003210d0412345ul, ReadPair(4, 10, peek: false));
            Issue(0xee102410); // RXVD r2
            Assert.AreEqual(0xd0000004u, (uint)cpu.GetRegister(2));
            Assert.AreEqual(0x20003210d0012345ul, ReadPair(4, 10, peek: false));
        }

        [Test]
        public void ShouldClearBothFullContextsOnProcessorReset()
        {
            WritePair(4, 2, 0xf07fffff, 0xf0003fff);
            WritePair(5, 2, 0x80412345, 0x20003210);
            cpu.Reset();
            cpu.CPACR = 0xf00;
            Assert.AreEqual(0ul, ReadPair(4, 10, peek: false));
            Assert.AreEqual(0ul, ReadPair(5, 10, peek: true));
        }

        [TestCase(0xfc410402u)] // Reserved MCRR2 write.
        [TestCase(0xec51040bu)] // Reserved MRRC selector.
        [TestCase(0xec51041au)] // Reserved MRRC opc1.
        [TestCase(0xec51f40au)] // MRRC to PC.
        [TestCase(0xec50040au)] // MRRC with identical destinations.
        public void ShouldRejectReservedContextTransferEncodings(uint instruction)
        {
            Issue(instruction, expectSuccess: false);
            Assert.AreEqual(0x2000u, (uint)cpu.PC);
            Assert.AreEqual(0x10000u, machine.SystemBus.ReadDoubleWord(0xe000ed28, cpu));
        }

        private void WritePair(int cp, int selector, uint low, uint high)
        {
            cpu.SetRegister(0, low);
            cpu.SetRegister(1, high);
            Issue(0xec410000u | (uint)cp << 8 | (uint)selector);
        }

        private ulong ReadPair(int cp, int selector, bool peek)
        {
            Issue((peek ? 0xfc554000u : 0xec554000u) | (uint)cp << 8 | (uint)selector);
            return (uint)cpu.GetRegister(4) | ((ulong)(uint)cpu.GetRegister(5) << 32);
        }

        private void Issue(uint instruction, bool expectSuccess = true)
        {
            code.WriteDoubleWord(0x1000, (instruction << 16) | (instruction >> 16));
            cpu.ClearTranslationCache();
            cpu.PC = 0x1000;
            cpu.Step();
            if(expectSuccess) Assert.AreEqual(0x1004u, (uint)cpu.PC, $"Instruction {instruction:x8}");
        }

        private const string HardwareStatus =
                    "8aee99998aee99998aee99998aee9999aaaa9999aaaa9999aaaa9999aaaa9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "8aee99998aee99998aee99998aee9999aaaa9999aaaa9999aaaa9999aaaa9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "8aee99998aee9999eeee9999eeee9999aaaa9999aaaa9999eeee9999eeee9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "8aee99998aee9999eeee9999eeee9999aaaa9999aaaa9999eeee9999eeee9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "88889999888899998888999988889999a8ee9999a8ee9999a8ee9999a8ee9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "88889999888899998888999988889999a8ee9999a8ee9999a8ee9999a8ee9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "8888999988889999eeee9999eeee9999a8ee9999a8ee9999eeee9999eeee9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999" +
                    "8888999988889999eeee9999eeee9999a8ee9999a8ee9999eeee9999eeee9999" +
                    "9999999999999999999999999999999999999999999999999999999999999999";

        private Machine machine;
        private CortexM cpu;
        private MappedMemory code;
    }
}
