// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_Hazard3Tests
    {
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine, "hazard3");
            cpu = new RP2350_Hazard3(machine, architectureEnabled: true);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            memory = new MappedMemory(machine, 0x1000);
            machine.SystemBus.Register(memory, new BusPointRegistration(Ram));
            cpu.ResetVector = Code;
            cpu.Reset();
            cpu.MTVEC = Ram + 0x300;
            memory.WriteDoubleWord(0x300, Wfi);
            machine.PostCreationActions();
        }

        [TearDown]
        public void TearDown() => EmulationManager.Instance.CurrentEmulation.RemoveMachine(machine);

        [TestCase(0xfedcba98u, 0u, 8, 0x98u)]
        [TestCase(0xfedcba98u, 4u, 4, 9u)]
        [TestCase(0xfedcba98u, 24u, 8, 0xfeu)]
        [TestCase(0x80000000u, 31u, 1, 1u)]
        [TestCase(0xff000000u, 28u, 8, 15u)]
        [TestCase(0x12345678u, 36u, 8, 0x67u)]
        public void ShouldExtractTheSpecifiedFieldWithRegisterAndImmediateShift(uint input, uint shift, int bits, uint expected)
        {
            cpu.SetRegister(10, input);
            cpu.SetRegister(12, shift);
            var common = ((uint)(bits - 1) << 26) | (10u << 15) | 0x0b;
            Program(common | (12u << 20) | (11u << 7),
                common | ((shift & 31) << 20) | (4u << 12) | (13u << 7), Wfi);
            Advance();
            Assert.AreEqual(expected, (uint)cpu.GetRegister(11));
            Assert.AreEqual(expected, (uint)cpu.GetRegister(13));
            Assert.AreEqual(0u, (uint)cpu.MCAUSE);
        }

        [Test]
        public void ShouldRejectReservedBextEncodings()
        {
            Program(0x0200000b, Wfi); // BEXT reserved bit25, not a defined operation.
            Advance();
            Assert.AreEqual(2u, (uint)cpu.MCAUSE);
            Assert.AreEqual(Code, (uint)cpu.MEPC);
            Assert.AreEqual(0x0200000bu, (uint)cpu.MTVAL);
        }

        [Test]
        public void ShouldConsumeEachStickyEventOnce()
        {
            Program(0x00100293, Block, 0x00128293, Block, 0x00128293, Wfi);
            Advance();
            Assert.AreEqual(1u, (uint)cpu.GetRegister(5));
            cpu.ReceiveSystemEvent();
            Advance();
            Assert.AreEqual(2u, (uint)cpu.GetRegister(5));
            Advance();
            Assert.AreEqual(2u, (uint)cpu.GetRegister(5), "A consumed event must not satisfy another block");
            cpu.ReceiveSystemEvent();
            Advance();
            Assert.AreEqual(3u, (uint)cpu.GetRegister(5));
        }

        [Test]
        public void ShouldFeedUnblockBackToItsOwnPhysicalSocket()
        {
            Program(Unblock, Block, 0x00700293, Wfi);
            Advance();
            Assert.AreEqual(7u, (uint)cpu.GetRegister(5));
        }

        [Test]
        public void ShouldApplyIndexedInterruptArrayReadModifyWriteToTheRequestedWindow()
        {
            cpu.SetRegister(10, 0x00010002); // IRQ32 in enable window2.
            cpu.SetRegister(11, 2);
            cpu.SetRegister(12, 0x00010002);
            Program(Csr(0xbe0, 0, 10, 1), Csr(0xbe0, 5, 11, 2),
                Csr(0xbe0, 6, 12, 3), Csr(0xbe0, 7, 11, 2), Wfi);
            Advance();
            Assert.AreEqual(0x10000u, (uint)cpu.GetRegister(5));
            Assert.AreEqual(0x10000u, (uint)cpu.GetRegister(6));
            Assert.AreEqual(0u, (uint)cpu.GetRegister(7));
        }

        [Test]
        public void ShouldChooseHighestPriorityAndClearOnlyTheConsumedForcedInterrupt()
        {
            cpu.SetRegister(10, (3u << 16)); // Enable IRQ0 and1.
            cpu.SetRegister(11, (3u << 16)); // Force both.
            cpu.SetRegister(12, (0x91u << 16)); // Priorities IRQ0=1, IRQ1=9.
            Program(Csr(0xbe0, 0, 10, 1), Csr(0xbe2, 0, 11, 1), Csr(0xbe3, 0, 12, 1),
                Csr(0xbe4, 5, 0, 2), Csr(0xbe4, 6, 0, 2), Csr(0xbe4, 7, 0, 2), Wfi);
            Advance();
            Assert.AreEqual(4u, (uint)cpu.GetRegister(5));
            Assert.AreEqual(0u, (uint)cpu.GetRegister(6));
            Assert.AreEqual(0x80000000u, (uint)cpu.GetRegister(7));
        }

        [Test]
        public void ShouldEnforceAnUnlockedPmpRegionInMachineModeWhenPmpcfgmEnablesIt()
        {
            cpu.SetRegister(10, ((Ram + 0x400) >> 2) | 3u); // 32-byte NAPOT guard.
            cpu.SetRegister(11, 0x18); // NAPOT, no read/write/execute, not locked.
            cpu.SetRegister(12, 1);
            cpu.SetRegister(14, Ram + 0x400);
            memory.WriteDoubleWord(0x400, 0xdeadbeef);
            Program(Csr(0x3b0, 0, 10, 1), Csr(0x3a0, 0, 11, 1), Csr(0xbd0, 0, 12, 1),
                0x00072783, Wfi); // lw a5,0(a4)
            Advance();
            Assert.AreEqual(5u, (uint)cpu.MCAUSE);
            Assert.AreEqual(Code + 12, (uint)cpu.MEPC);
            Assert.AreEqual(Ram + 0x400, (uint)cpu.MTVAL);
        }

        [Test]
        public void ShouldExecuteZcmpWithoutZcmtAndPreserveStackLayout()
        {
            cpu.SetRegister(2, Ram + 0x800);
            cpu.SetRegister(1, 0x12345678);
            cpu.SetRegister(8, 0xaabbccdd);
            cpu.SetRegister(9, 0x87654321);
            Program(0x0001b862, Wfi); // cm.push {ra,s0-s1},-16; c.nop
            Advance();
            Assert.AreEqual(Ram + 0x7f0, (uint)cpu.GetRegister(2));
            Assert.AreEqual(0x87654321u, memory.ReadDoubleWord(0x7fc));
            Assert.AreEqual(0xaabbccddu, memory.ReadDoubleWord(0x7f8));
            Assert.AreEqual(0x12345678u, memory.ReadDoubleWord(0x7f4));
            cpu.SetRegister(1, 0);
            cpu.SetRegister(8, 0);
            cpu.SetRegister(9, 0);
            memory.WriteDoubleWord(0x120, 0x0001ba62); // cm.pop {ra,s0-s1},16; c.nop
            memory.WriteDoubleWord(0x124, Wfi);
            cpu.PC = Code + 0x20;
            cpu.RequestWakeUpFromWfi();
            Advance();
            Assert.AreEqual(Ram + 0x800, (uint)cpu.GetRegister(2));
            Assert.AreEqual(0x12345678u, (uint)cpu.GetRegister(1));
            Assert.AreEqual(0xaabbccddu, (uint)cpu.GetRegister(8));
            Assert.AreEqual(0x87654321u, (uint)cpu.GetRegister(9));
            Assert.AreEqual(0u, (uint)cpu.MCAUSE);
        }

        [Test]
        public void ShouldRejectZcmtWhenOnlyZcmpIsEnabled()
        {
            Program(0x0001a002, Wfi); // cm.jt is not part of RP2350's Zcmp.
            Advance();
            Assert.AreEqual(2u, (uint)cpu.MCAUSE);
            Assert.AreEqual(Code, (uint)cpu.MEPC);
            Assert.AreEqual(0xa002u, (uint)cpu.MTVAL);
        }

        [Test]
        public void ShouldPreserveAssertedExternalPinsAcrossProcessorReset()
        {
            cpu.OnGPIO(35, true);
            cpu.Reset();
            cpu.MTVEC = Ram + 0x300;
            cpu.SetRegister(10, 2); // Read pending window2.
            Program(Csr(0xbe1, 5, 10, 2), Wfi);
            Advance();
            Assert.AreEqual(8u << 16, (uint)cpu.GetRegister(5));
        }

        [Test]
        public void ShouldAtomicallySaveAndClearTimerAndSoftwareInterruptEnables()
        {
            cpu.MIE = 0x88;
            cpu.SetRegister(10, 2);
            Program(Csr(0xbe5, 5, 10, 1), Csr(0x304, 6, 0, 2),
                Csr(0xbe5, 7, 0, 2), Csr(0xbe5, 0, 5, 1), Csr(0x304, 8, 0, 2), Wfi);
            Advance();
            Assert.AreEqual(0x800cu, (uint)cpu.GetRegister(5));
            Assert.AreEqual(0u, (uint)cpu.GetRegister(6));
            Assert.AreEqual(0u, (uint)cpu.GetRegister(7) & 15u, "Save fields read zero outside the clearing access");
            Assert.AreEqual(0x88u, (uint)cpu.GetRegister(8));
        }

        [Test]
        public void ShouldTakeAnExternalInterruptFromWfiAndRestorePriorityOnMret()
        {
            cpu.SetRegister(10, 1u << 16);
            cpu.SetRegister(11, 0x800);
            cpu.SetRegister(12, 8);
            Program(Csr(0xbe0, 0, 10, 1), Csr(0x304, 0, 11, 1), Csr(0x300, 0, 12, 2), Wfi, Wfi);
            // Handler snapshots context, acknowledges the physical source's
            // enable, increments a register, and returns through actual mret.
            memory.WriteDoubleWord(0x300, Csr(0xbe5, 5, 0, 2));
            memory.WriteDoubleWord(0x304, Csr(0xbe0, 0, 10, 3));
            memory.WriteDoubleWord(0x308, 0x00130313); // addi t1,t1,1
            memory.WriteDoubleWord(0x30c, 0x30200073); // mret
            Advance();
            Assert.AreEqual(0u, (uint)cpu.GetRegister(6));
            cpu.OnGPIO(0, true);
            Advance();
            Assert.AreEqual(1u, (uint)cpu.GetRegister(6));
            Assert.AreEqual(0x8000000bu, (uint)cpu.MCAUSE);
            Assert.AreEqual(0x00018001u, (uint)cpu.GetRegister(5));
            Assert.AreEqual(Code + 20, (uint)cpu.PC);
        }

        [Test]
        public void ShouldTrapAnUnimplementedMachineCsr()
        {
            var opcode = Csr(0xbc0, 5, 0, 2);
            Program(opcode, Wfi);
            Advance();
            Assert.AreEqual(2u, (uint)cpu.MCAUSE);
            Assert.AreEqual(Code, (uint)cpu.MEPC);
            Assert.AreEqual(opcode, (uint)cpu.MTVAL);
        }

        [Test]
        public void ShouldRejectMachineInterruptCsrsInUserMode()
        {
            var opcode = Csr(0xbe0, 5, 0, 2);
            EnterUserProgram(opcode, false);
            Advance();
            Assert.AreEqual(2u, (uint)cpu.MCAUSE);
            Assert.AreEqual(Code + 16, (uint)cpu.MEPC);
            Assert.AreEqual(opcode, (uint)cpu.MTVAL);
        }

        [TestCase(Block, false)]
        [TestCase(Unblock, false)]
        [TestCase(Block, true)]
        [TestCase(Unblock, true)]
        public void ShouldEnforceTwForUserModeHazard3SleepHints(uint opcode, bool trapWait)
        {
            EnterUserProgram(opcode, trapWait);
            cpu.ReceiveSystemEvent();
            Advance();
            if(trapWait)
            {
                Assert.AreEqual(2u, (uint)cpu.MCAUSE);
                Assert.AreEqual(Code + 16, (uint)cpu.MEPC);
                Assert.AreEqual(opcode, (uint)cpu.MTVAL);
            }
            else
            {
                Assert.AreEqual(0u, (uint)cpu.MCAUSE);
                Assert.AreEqual(7u, (uint)cpu.GetRegister(15));
                Assert.AreEqual(PrivilegeLevel.User, cpu.CurrentPrivilegeLevel);
            }
        }

        private void EnterUserProgram(uint opcode, bool trapWait)
        {
            cpu.SetRegister(10, (Ram >> 2) | 0x1ffu); // Entire 4KiB test RAM, NAPOT.
            cpu.SetRegister(11, 0x1f); // R/W/X for U-mode.
            cpu.SetRegister(12, trapWait ? 1u << 21 : 0u);
            cpu.MEPC = Code + 16;
            Program(Csr(0x3b0, 0, 10, 1), Csr(0x3a0, 0, 11, 1),
                Csr(0x300, 0, 12, 1), 0x30200073, opcode, 0x00700793, Wfi);
        }

        [TestCase(52, 8u)]
        [TestCase(53, 128u)]
        public void ShouldPreserveLocalInputLevelsAcrossProcessorReset(int input, uint expected)
        {
            cpu.OnGPIO(input, true);
            cpu.Reset();
            cpu.MTVEC = Ram + 0x300;
            Program(Csr(0x344, 5, 0, 2), Wfi);
            Advance();
            Assert.AreEqual(expected, (uint)cpu.GetRegister(5));
        }

        [TestCase(true, false)]
        [TestCase(true, true)]
        [TestCase(false, false)]
        [TestCase(false, true)]
        public void ShouldDenyUnprivilegedDataAccessToSecureBootRam(bool userMode, bool write)
        {
            const uint BootRam = 0x40000000;
            var bootram = new RP2350_BootRAM(machine);
            machine.SystemBus.Register(bootram, new BusPointRegistration(BootRam));
            bootram.WriteDoubleWord(0, 0xcafef00d);
            cpu.SetRegister(10, 0x1fffffffu); // One NAPOT region allows all bus addresses.
            cpu.SetRegister(11, 0x1f);
            cpu.SetRegister(12, userMode ? 0u : 1u << 17); // MPRV with MPP=User.
            cpu.SetRegister(13, BootRam);
            cpu.SetRegister(15, 0x12345678);
            var access = write ? 0x00f6a023u : 0x0006a283u; // sw a5 / lw t0
            cpu.MEPC = Code + 16;
            if(userMode)
                Program(Csr(0x3b0, 0, 10, 1), Csr(0x3a0, 0, 11, 1),
                    Csr(0x300, 0, 12, 1), 0x30200073, access, Wfi);
            else
                Program(Csr(0x3b0, 0, 10, 1), Csr(0x3a0, 0, 11, 1),
                    Csr(0x300, 0, 12, 1), access, Wfi);
            Advance();
            Assert.AreEqual(write ? 7u : 5u, (uint)cpu.MCAUSE);
            Assert.AreEqual(BootRam, (uint)cpu.MTVAL);
            Assert.AreEqual(Code + (userMode ? 16u : 12u), (uint)cpu.MEPC);
            Assert.AreEqual(0u, (uint)cpu.GetRegister(5));
            Assert.AreEqual(0xcafef00du, bootram.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldNotConsumeAForcedInterruptWhenCsrrwSuppressesItsRead()
        {
            cpu.SetRegister(10, 1u << 16);
            Program(Csr(0xbe0, 0, 10, 1), Csr(0xbe2, 0, 10, 1),
                Csr(0xbe4, 0, 0, 1), Csr(0xbe4, 5, 0, 2), Csr(0xbe4, 6, 0, 2), Wfi);
            Advance();
            Assert.AreEqual(0u, (uint)cpu.GetRegister(5));
            Assert.AreEqual(0x80000000u, (uint)cpu.GetRegister(6));
        }

        [Test]
        public void ShouldKeepHazard3CustomInstructionsDisabledOnGenericRiscV()
        {
            cpu.IsHalted = true;
            var generic = new RiscV32(machine, "rv32imac_zicsr_zifencei", hartId: 1);
            machine.SystemBus.Register(generic, new CPURegistrationPoint(1));
            machine.SetLocalName(generic, "generic");
            generic.ResetVector = Code;
            generic.Reset();
            generic.MTVEC = Ram + 0x300;
            Program(Block, Unblock, 0x0000000b, Wfi);
            Advance();
            Assert.AreEqual(2u, (uint)generic.MCAUSE);
            Assert.AreEqual(Code + 8, (uint)generic.MEPC);
            Assert.AreEqual(0x0000000bu, (uint)generic.MTVAL);
        }

        [Test]
        public void ShouldTakeARequiredInstructionBusFaultAfterPrecedingInstructionsRetire()
        {
            const uint DeviceCode = 0x40001000;
            machine.SystemBus.Register(new FaultingInstructionMemory(), new BusPointRegistration(DeviceCode));
            cpu.ResetVector = DeviceCode;
            cpu.Reset();
            cpu.RegisterAccessFlags(DeviceCode, 0x1000, isIoMemory: true);
            cpu.MTVEC = Ram + 0x300;
            Advance();
            Assert.AreEqual(7u, (uint)cpu.GetRegister(5), "The preceding addi must execute before the fetch fault");
            Assert.AreEqual(1u, (uint)cpu.MCAUSE);
            Assert.AreEqual(DeviceCode + 4, (uint)cpu.MEPC);
            Assert.AreEqual(DeviceCode + 4, (uint)cpu.MTVAL);
        }

        private sealed class FaultingInstructionMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
        {
            public byte ReadByte(long offset) => (byte)(ReadWord(offset & ~1L) >> ((int)(offset & 1) * 8));
            public void WriteByte(long offset, byte value) => throw new BusAccessException(BusAccessError.CommandError);
            public ushort ReadWord(long offset)
            {
                if(offset == 0) return 0x0293;
                if(offset == 2) return 0x0070; // addi t0,zero,7
                throw new BusAccessException(BusAccessError.CommandError);
            }
            public uint ReadDoubleWord(long offset) => (uint)(ReadWord(offset) | ReadWord(offset + 2) << 16);
            public void WriteWord(long offset, ushort value) => throw new BusAccessException(BusAccessError.CommandError);
            public void WriteDoubleWord(long offset, uint value) => throw new BusAccessException(BusAccessError.CommandError);
            public void Reset() { }
            public long Size => 0x1000;
        }

        private void Program(params uint[] words)
        {
            for(var i = 0; i < words.Length; i++) memory.WriteDoubleWord(0x100 + i * 4, words[i]);
        }

        private static uint Csr(uint csr, uint rd, uint rs1, uint operation) =>
            (csr << 20) | (rs1 << 15) | (operation << 12) | (rd << 7) | 0x73;

        private void Advance() => EmulationManager.Instance.CurrentEmulation.RunFor(TimeInterval.FromMicroseconds(10));

        private const uint Ram = 0x20000000;
        private const uint Code = Ram + 0x100;
        private const uint Wfi = 0x10500073;
        private const uint Block = 0x00002033;
        private const uint Unblock = 0x00102033;
        private Machine machine;
        private RP2350_Hazard3 cpu;
        private MappedMemory memory;
    }
}
