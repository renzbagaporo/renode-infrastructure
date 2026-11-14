//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Numerics;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    // The pico-sdk canned DCP sequences (hardware/dcp_canned.inc.S), issued one
    // instruction at a time, with the CPU arithmetic of dmul and fmul done here.
    [TestFixture]
    public class RP2350_DCPArithmeticTests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "dcp_arithmetic");
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
            code.WriteWord(0x2000, 0xe7fe);
            machine.PostCreationActions();
            cpu.Reset();
            cpu.SP = 0x20010000;
            cpu.CPACR = 0xf00;
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        [Test]
        public void ShouldAddAndSubtractCorrectlyRounded()
        {
            var random = new Random(1);
            for(var i = 0; i < 200; i++)
            {
                var x = RandomDouble(random);
                var y = RandomDouble(random);
                Assert.AreEqual(Bits(x + y), Binary(x, y, Add1, Rdda), $"{x:R} + {y:R}");
                Assert.AreEqual(Bits(x - y), Binary(x, y, Sub1, Rdds), $"{x:R} - {y:R}");
            }
            Assert.AreEqual(Bits(0.30000000000000004), Binary(0.1, 0.2, Add1, Rdda));
        }

        [Test]
        public void ShouldMultiplyFromTheCpuFormedProduct()
        {
            var random = new Random(2);
            for(var i = 0; i < 200; i++)
            {
                var x = RandomDouble(random);
                var y = RandomDouble(random);
                Assert.AreEqual(Bits(x * y), Multiply(x, y), $"{x:R} * {y:R}");
            }
            Assert.AreEqual(Bits(-0.0), Multiply(-2.0, 0.0));
            Assert.AreEqual(Bits(double.PositiveInfinity), Multiply(1e300, 1e300));
        }

        [Test]
        public void ShouldDivideAndTakeSquareRoots()
        {
            var random = new Random(3);
            for(var i = 0; i < 100; i++)
            {
                var x = Math.Abs(RandomDouble(random));
                var y = RandomDouble(random);
                WriteDouble(Wxup, x);
                WriteDouble(Wyup, y);
                Cdp(8, 0, 1); // NRDD
                Assert.AreEqual(Bits(x / y), ReadPair(Rddd), $"{x:R} / {y:R}");
                WriteDouble(Wxup, x);
                Cdp(2, 1, 0); // SQR0
                Cdp(8, 0, 1);
                Assert.AreEqual(Bits(Math.Sqrt(x)), ReadPair(Rddq), $"sqrt {x:R}");
            }
        }

        [TestCase(1234567, 1234567.0)]
        [TestCase(-42, -42.0)]
        [TestCase(int.MinValue, -2147483648.0)]
        public void ShouldConvertSignedIntegersToDouble(int input, double expected)
        {
            cpu.SetRegister(0, (uint)input);
            cpu.SetRegister(1, (uint)input);
            Issue(Mcrr(7, 0)); // WXIC
            Cdp(0, 1, 0); // ADD0
            Cdp(1, 1, 1); // SUB1
            Cdp(8, 0, 1); // NRDD
            Assert.AreEqual(Bits(expected), ReadPair(Rdds));
        }

        [TestCase(2.75, 2u, 3u)]
        [TestCase(-2.5, 0xfffffffeu, 0xfffffffeu)]
        [TestCase(3.5, 3u, 4u)]
        [TestCase(1e20, 0x7fffffffu, 0x7fffffffu)]
        public void ShouldConvertDoubleToSignedIntegers(double input, uint truncated, uint rounded)
        {
            Assert.AreEqual(truncated, ToInteger(input, 2));
            Assert.AreEqual(rounded, ToInteger(input, 3));
        }

        [Test]
        public void ShouldConvertBetweenSingleAndDouble()
        {
            cpu.SetRegister(0, BitConverter.SingleToUInt32Bits(1.1f));
            cpu.SetRegister(1, BitConverter.SingleToUInt32Bits(1.1f));
            Issue(Mcrr(1, 2)); // WXYU
            Cdp(8, 0, 1); // NRDD
            Assert.AreEqual(Bits((double)1.1f), ReadPair(Rddg));
            WriteDouble(Wxup, 0.1);
            Cdp(8, 2, 1); // NRDF
            Assert.AreEqual(BitConverter.SingleToUInt32Bits(0.1f), ReadSingle(2, 5)); // RDFG
        }

        [Test]
        public void ShouldMultiplySinglesFromTheCpuFormedProduct()
        {
            cpu.SetRegister(0, BitConverter.SingleToUInt32Bits(3.3f));
            cpu.SetRegister(1, BitConverter.SingleToUInt32Bits(-0.7f));
            Issue(Mcrr(1, 2)); // WXYU
            Issue(Mrrc(1, 1)); // RXYH: xm hi, ym hi
            var product = (ulong)(uint)cpu.GetRegister(4) * (uint)cpu.GetRegister(5);
            cpu.SetRegister(0, (uint)product);
            cpu.SetRegister(1, (uint)(product >> 32));
            Issue(Mcrr(10, 0)); // WXFM
            Cdp(8, 2, 1); // NRDF
            Assert.AreEqual(BitConverter.SingleToUInt32Bits(3.3f * -0.7f), ReadSingle(2, 2)); // RDFM
        }

        [Test]
        public void ShouldCompareThroughAdd0()
        {
            WriteDouble(Wxup, -3.0);
            WriteDouble(Wyup, 2.0);
            Cdp(0, 1, 0); // ADD0
            Issue(0xee104430); // RCMP r4
            Assert.AreEqual(0u, (uint)cpu.GetRegister(4) >> 28 & 7, "-3 < 2: not equal, not greater or equal, ordered");
            WriteDouble(Wxup, 2.0);
            Cdp(0, 1, 0);
            Issue(0xee104430);
            Assert.AreEqual(6u, (uint)cpu.GetRegister(4) >> 28 & 7, "2 == 2");
        }

        [Test]
        public void ShouldKeepADivisionsOperandsAcrossASavedAndRestoredContext()
        {
            WriteDouble(Wxup, 10.0);
            WriteDouble(Wyup, 4.0);
            // An interrupt saves the context as the SDK does (PXMD, PYMD, REFD),
            // runs its own canned sequence and restores it.
            var xm = ReadPair(Mrrc(0, 8) | Peek);
            var ym = ReadPair(Mrrc(0, 9) | Peek);
            var efd = ReadPair(Mrrc(0, 10));
            Assert.AreEqual(Bits(6.0), Binary(1.0, 5.0, Add1, Rdda));
            WritePairValue(Mcrr(0, 0), xm);
            WritePairValue(Mcrr(0, 1), ym);
            WritePairValue(Mcrr(0, 2), efd);
            Cdp(8, 0, 1);
            Assert.AreEqual(Bits(2.5), ReadPair(Rddd));
        }

        private ulong Binary(double x, double y, (uint opc1, uint opc2) operation, uint read)
        {
            WriteDouble(Wxup, x);
            WriteDouble(Wyup, y);
            Cdp(0, 1, 0); // ADD0
            Cdp(operation.opc1, 1, operation.opc2);
            Cdp(8, 0, 1); // NRDD
            return ReadPair(read);
        }

        // dcp_dmul_m: the CPU multiplies the Q62 mantissas and writes the
        // sticky low half and the high half back.
        private ulong Multiply(double x, double y)
        {
            WriteDouble(Wxup, x);
            WriteDouble(Wyup, y);
            var xm = ReadPair(Mrrc(0, 4)); // RXMS 0
            var ym = ReadPair(Mrrc(0, 5)); // RYMS 0
            var product = (BigInteger)xm * ym;
            var low = (ulong)(product & ulong.MaxValue);
            var high = (ulong)(product >> 64);
            WritePairValue(Mcrr(2, 0), low); // WXMS
            WritePairValue(Mcrr(3, 0), high); // WXMO
            Cdp(8, 0, 1); // NRDD
            return ReadPair(Rddm);
        }

        private uint ToInteger(double input, uint rounding)
        {
            WritePairValue(Mcrr(8, 0), Bits(input)); // WXDC
            Cdp(0, 1, 0); // ADD0
            Cdp(1, 1, 0); // ADD1
            Cdp(8, 0, rounding); // NTDC / NRDC
            return ReadSingle(3, 0); // RDIC
        }

        private static double RandomDouble(Random random)
        {
            var mantissa = 1.0 + random.NextDouble();
            return (random.Next(2) == 0 ? -1 : 1) * mantissa * Math.Pow(2, random.Next(-60, 60));
        }

        private static ulong Bits(double value) => (ulong)BitConverter.DoubleToInt64Bits(value);

        private void WriteDouble(uint instruction, double value) => WritePairValue(instruction, Bits(value));

        private void WritePairValue(uint instruction, ulong value)
        {
            cpu.SetRegister(0, (uint)value);
            cpu.SetRegister(1, (uint)(value >> 32));
            Issue(instruction);
        }

        private ulong ReadPair(uint instruction)
        {
            Issue(instruction);
            return (uint)cpu.GetRegister(4) | ((ulong)(uint)cpu.GetRegister(5) << 32);
        }

        private uint ReadSingle(uint crm, uint opc2)
        {
            Issue(0xee104010u | Coprocessor << 8 | opc2 << 5 | crm); // MRC p4,#0,r4,c0,cCRm,#opc2
            return (uint)cpu.GetRegister(4);
        }

        private void Cdp(uint opc1, uint crm, uint opc2) => Issue(0xee000000u | opc1 << 20 | Coprocessor << 8 | opc2 << 5 | crm);

        private static uint Mcrr(uint opc1, uint crm) => 0xec410000u | Coprocessor << 8 | opc1 << 4 | crm; // r0, r1
        private static uint Mrrc(uint opc1, uint crm) => 0xec554000u | Coprocessor << 8 | opc1 << 4 | crm; // r4, r5

        private void Issue(uint instruction)
        {
            code.WriteDoubleWord(0x1000, (instruction << 16) | (instruction >> 16));
            cpu.ClearTranslationCache();
            cpu.PC = 0x1000;
            cpu.Step();
            Assert.AreEqual(0x1004u, (uint)cpu.PC, $"Instruction {instruction:x8}");
        }

        private Machine machine;
        private CortexM cpu;
        private MappedMemory code;

        private const uint Coprocessor = 4;
        private const uint Peek = 1u << 28;
        private static readonly uint Wxup = Mcrr(1, 0), Wyup = Mcrr(1, 1);
        private static readonly uint Rdda = Mrrc(1, 0), Rdds = Mrrc(3, 0), Rddm = Mrrc(5, 0);
        private static readonly uint Rddd = Mrrc(7, 0), Rddq = Mrrc(9, 0), Rddg = Mrrc(11, 0);
        private static readonly (uint, uint) Add1 = (1, 0), Sub1 = (1, 1);
    }
}
