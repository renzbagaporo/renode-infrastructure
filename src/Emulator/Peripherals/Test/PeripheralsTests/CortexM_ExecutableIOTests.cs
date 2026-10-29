//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture("cortex-m33")]
    [TestFixture("cortex-m33-rp2350")]
    public class CortexM_ExecutableIOTests
    {
        public CortexM_ExecutableIOTests(string model)
        {
            this.model = model;
        }
        [SetUp]
        public void SetUp()
        {
            EmulationManager.Instance.Clear();
            emulation = EmulationManager.Instance.CurrentEmulation;
            machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "executable_io");
            nvic = new NVIC(machine);
            cpu = new CortexM(model, machine, nvic, enableTrustZone: true);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
            machine.SetLocalName(cpu, "cpu");
            machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
            nvic.IRQ.Connect(cpu, 0);
            cpu.EnableMemoryMappedDebug = true;
            ram = new MappedMemory(machine, 0x4000);
            machine.SystemBus.Register(ram, new BusPointRegistration(0x20000000));
            ram.WriteDoubleWord(0, 0x20003000);
            ram.WriteDoubleWord(4, 0x20000101);
            ram.WriteDoubleWord(0xc, 0x20000301);
            ram.WriteWord(0x300, 0xbe00);
            memory = new FaultingMemory(machine);
            machine.SystemBus.Register(memory, new BusPointRegistration(Code));
            cpu.RegisterAccessFlags(Code, 0x1000, isIoMemory: true);
            cpu.InitVectorTableOffset = 0x20000000;
            cpu.Reset();
            cpu.SetRegister(13, 0x20002000);

        }
        [TearDown]
        public void TearDown()
        {
            emulation.RemoveMachine(machine);
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ShouldRaiseInstructionBusFaultOnTheFirstExecutableIoFetch(bool previouslyExecuted)
        {
            memory.SetWord(0, 0xbe00);
            if (previouslyExecuted)
            {
                Launch(Code);
                Assert.AreEqual(Code, (uint)cpu.PC);
                cpu.RequestTranslationCacheClearing();
            }
            memory.Reads = 0;
            memory.FailFrom = 0;
            Launch(Code);
            AssertInstructionFault(Code, 0);
            Assert.LessOrEqual(memory.Reads, 2, "A failing fetch must stop translation immediately");
            Assert.IsTrue(memory.LastState.InstructionFetch);
            Assert.IsFalse(memory.LastState.DebugAccess);
        }
        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void ShouldExecutePrecedingInstructionsBeforeAnExecutableIoFetchFault(bool secondHalfword, bool enableReadCache)
        {
            memory.SetWord(0, 0x3001);
            memory.SetWord(2, secondHalfword ? (ushort)0xf000 : (ushort)0x2201);
            memory.FailFrom = secondHalfword ? 4 : 2;
            if (enableReadCache)
                cpu.EnableReadCache(Code + (uint)memory.FailFrom, 1);
            Launch(Code);
            AssertInstructionFault(Code + 2, 1);
            Assert.LessOrEqual(memory.Reads, 7);
        }
        [Test]
        public void ShouldFaultAtTheStartOfA32BitInstructionWhenItsSecondHalfwordFails()
        {
            memory.SetWord(0, 0xf000);
            memory.FailFrom = 2;
            Launch(Code);
            AssertInstructionFault(Code, 0);
            Assert.LessOrEqual(memory.Reads, 5);
        }
        [Test]
        public void ShouldDiscardASpeculativeFetchFailureWhenAPrecedingStoreMakesTheNextFetchSucceed()
        {
            memory.SetWord(0, 0x6001);
            memory.SetWord(2, 0x222a);
            memory.SetWord(4, 0xbe00);
            memory.FailFrom = 2;
            var control = new FetchControl(memory);
            machine.SystemBus.Register(control, new BusPointRegistration(0x40000000));
            cpu.SetRegister(0, 0x40000000);
            cpu.SetRegister(1, 1);
            Launch(Code);
            Assert.AreEqual(Code + 4, (uint)cpu.PC);
            Assert.AreEqual(42, (uint)cpu.GetRegister(2));
            Assert.AreEqual(1, control.Writes);
            Assert.AreEqual(0, cpu.FaultStatus);
            Assert.AreEqual(BfarSentinel, cpu.BusFaultAddress);
        }
        [Test]
        public void ShouldPreservePreciseDataBusFaultClassification()
        {
            ram.WriteWord(0x100, 0x4801);
            ram.WriteWord(0x102, 0x6801);
            ram.WriteWord(0x104, 0xbe00);
            ram.WriteDoubleWord(0x108, Code);
            memory.FailFrom = 0;
            Launch(0x20000100);
            Assert.AreEqual(0x8200u, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(Code, cpu.BusFaultAddress);
            Assert.AreEqual(0x20000300u, (uint)cpu.PC);
            Assert.AreEqual(0, (uint)cpu.GetRegister(1));
            Assert.IsFalse(memory.LastState.InstructionFetch);
        }
        [Test]
        public void ShouldPreserveSharedAndBankedCcrFieldsAcrossSecurityViews()
        {
            cpu.ConfigurationAndControlRegister = 0x110;
            Assert.AreEqual(0x110u, cpu.ConfigurationAndControlRegister & 0x118u);
            Assert.AreEqual(0x100u, cpu.ConfigurationAndControlRegisterNonSecure & 0x118u);
            cpu.ConfigurationAndControlRegisterNonSecure = 0x8;
            Assert.AreEqual(0x10u, cpu.ConfigurationAndControlRegister & 0x118u);
            Assert.AreEqual(0x8u, cpu.ConfigurationAndControlRegisterNonSecure & 0x118u);
            cpu.ConfigurationAndControlRegister = 0x100;
            Assert.AreEqual(0x100u, cpu.ConfigurationAndControlRegister & 0x118u);
            Assert.AreEqual(0x108u, cpu.ConfigurationAndControlRegisterNonSecure & 0x118u);
        }

        [Test]
        public void ShouldIgnoreNonSecureWritesToTheSecureOnlyCcrTrapBit()
        {
            var secondNvic = new NVIC(machine);
            var m55 = new CortexM("cortex-m55", machine, secondNvic, cpuId: 1, enableTrustZone: true);
            machine.SystemBus.Register(m55, new CPURegistrationPoint(1));
            m55.ConfigurationAndControlRegister = 0x100010;
            m55.ConfigurationAndControlRegisterNonSecure = 0x8;
            Assert.AreEqual(0x100010u, m55.ConfigurationAndControlRegister & 0x100118u);
            Assert.AreEqual(0x8u, m55.ConfigurationAndControlRegisterNonSecure & 0x100118u);
            m55.ConfigurationAndControlRegister = 0;
            m55.ConfigurationAndControlRegisterNonSecure = 0x100008;
            Assert.AreEqual(0, m55.ConfigurationAndControlRegister & 0x100000u);
            Assert.AreEqual(0x8u, m55.ConfigurationAndControlRegisterNonSecure & 0x100118u);
        }

        [Test]
        public void ShouldPreserveAnEarlierDataFaultAddressWhenAnInstructionFetchFails()
        {
            ram.WriteWord(0x100, 0xde00);
            ram.WriteWord(0x300, 0x4802);
            ram.WriteWord(0x302, 0x6801);
            ram.WriteWord(0x304, 0x38ff);
            ram.WriteWord(0x306, 0x4700);
            ram.WriteDoubleWord(0x30c, Code + 0x100);
            cpu.ConfigurationAndControlRegister = 0x100;
            memory.FailFrom = 0;
            Launch(0x20000100);
            Assert.AreEqual(0x8300u, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(Code + 0x100, cpu.BusFaultAddress);
            Assert.IsTrue(nvic.Lockup.IsSet);
        }
        [Test]
        public void ShouldRespectBfhfnmignForDataBusFaultsInHardFault()
        {
            ram.WriteWord(0x100, 0xde00);
            ram.WriteWord(0x300, 0x4801);
            ram.WriteWord(0x302, 0x6801);
            ram.WriteWord(0x304, 0xbe00);
            ram.WriteDoubleWord(0x308, Code);
            cpu.ConfigurationAndControlRegister = 0x100;
            Assert.AreEqual(0x100u, cpu.ConfigurationAndControlRegister & 0x100u);
            memory.FailFrom = 0;
            Launch(0x20000100);
            Assert.AreEqual(0x8200u, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(Code, cpu.BusFaultAddress);
            Assert.AreEqual(0x20000304u, (uint)cpu.PC, $"CCR={cpu.ConfigurationAndControlRegister:X} XPSR={cpu.XProgramStatusRegister} CFSR={cpu.FaultStatus:X}");
            Assert.AreEqual(3u, (uint)cpu.XProgramStatusRegister & 0x1ffu);
            Assert.IsFalse(nvic.Lockup.IsSet);
        }
        [Test]
        public void ShouldNotSuppressAnInstructionBusFaultInHardFaultWithBfhfnmign()
        {
            ram.WriteWord(0x100, 0xde00);
            ram.WriteDoubleWord(0xc, Code | 1);
            cpu.ConfigurationAndControlRegister = 0x100;
            Assert.AreEqual(0x100u, cpu.ConfigurationAndControlRegister & 0x100u);
            memory.FailFrom = 0;
            Launch(0x20000100);
            Assert.AreEqual(0x100u, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(BfarSentinel, cpu.BusFaultAddress);
            Assert.IsTrue(nvic.Lockup.IsSet);
            Assert.AreEqual(0xeffffffeu, (uint)cpu.PC);
        }
        [Test]
        public void ShouldApplyMpuExecuteNeverBeforeAnExecutableIoBusTransaction()
        {
            memory.FailFrom = 0;
            WriteSecure(0xe000ed98, 0);
            WriteSecure(0xe000ed9c, Code | 1);
            WriteSecure(0xe000eda0, Code + 0xfe1);
            WriteSecure(0xe000ed94, 5);
            Launch(Code);
            Assert.AreEqual(1u, cpu.FaultStatus & 1u);
            Assert.AreEqual(0, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(0, memory.Reads);
        }
        [Test]
        public void ShouldApplySauSecurityBeforeAnExecutableIoBusTransaction()
        {
            memory.FailFrom = 0;
            WriteSecure(0xe000edd8, 0);
            WriteSecure(0xe000eddc, Code);
            WriteSecure(0xe000ede0, Code + 0xfe1);
            WriteSecure(0xe000edd0, 1);
            Launch(Code);
            Assert.AreEqual(0x10u, cpu.SecureFaultStatus & 0x10u);
            Assert.AreEqual(0, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(0, memory.Reads);
        }
        private void Launch(uint pc)
        {
            cpu.PC = pc | 1;
            nvic.WriteDoubleWord(0xdf0, 0xa05f0001);
            Assert.IsTrue(SpinWait.SpinUntil(() => !cpu.HaltedByDebugger, TimeSpan.FromSeconds(5)));
            emulation.RunFor(TimeInterval.FromMicroseconds(100));
            emulation.RunFor(TimeInterval.FromMicroseconds(100));
        }
        private void AssertInstructionFault(uint faultPc, uint precedingAdds)
        {
            Assert.AreEqual(0x100u, cpu.FaultStatus & 0xff00u);
            Assert.AreEqual(BfarSentinel, cpu.BusFaultAddress);
            Assert.AreEqual(0x20000300u, (uint)cpu.PC);
            Assert.AreEqual(precedingAdds, (uint)cpu.GetRegister(0));
            Assert.AreEqual(0x20001fe0u, (uint)cpu.GetRegister(13));
            Assert.AreEqual(faultPc, ram.ReadDoubleWord(0x1ff8));
        }
        private void WriteSecure(ulong address, uint value)
        {
            machine.SystemBus.WriteDoubleWord(address, value, cpu, 7);
        }
        private const uint Code = 0x10000000, BfarSentinel = 0;
        private readonly string model; private Emulation emulation; private Machine machine; private CortexM cpu; private NVIC nvic; private MappedMemory ram; private FaultingMemory memory;
        private sealed class FetchControl : IDoubleWordPeripheral, IKnownSize
        {
            public long Size => 4;
            public FetchControl(FaultingMemory memory)
            {
                this.memory = memory;
            }
            public int Writes; private readonly FaultingMemory memory;
            public void Reset()
            {
            }
            public uint ReadDoubleWord(long offset) => 0;
            public void WriteDoubleWord(long offset, uint value)
            {
                Writes++;
                memory.FailFrom = long.MaxValue;
            }
        }
        private sealed class FaultingMemory : IExecutableIO
        {
            public FaultingMemory(Machine machine)
            {
                this.machine = machine;
            }
            private readonly Machine machine; private readonly byte[] bytes = new byte[0x1000];
            public long Size => bytes.Length; public long FailFrom = long.MaxValue; public int Reads; public CortexM.ContextState LastState;
            public void SetWord(int offset, ushort value)
            {
                bytes[offset] = (byte)value;
                bytes[offset + 1] = (byte)(value >> 8);
            }
            public void Reset()
            {
            }
            private ulong Read(long offset, int count)
            {
                Reads++;
                machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state);
                LastState = state;
                if (offset + count > FailFrom)
                    throw new BusAccessException(BusAccessError.GenericError);
                ulong result = 0;
                for (var i = 0; i < count; i++)
                    result |= (ulong)bytes[offset + i] << (8 * i);
                return result;
            }
            public byte ReadByte(long offset) => (byte)Read(offset, 1); public ushort ReadWord(long offset) => (ushort)Read(offset, 2);
            public uint ReadDoubleWord(long offset) => (uint)Read(offset, 4); public ulong ReadQuadWord(long offset) => Read(offset, 8);
            public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
            {
                var r = new byte[count];
                for (var i = 0; i < count; i++)
                    r[i] = ReadByte(offset + i);
                return r;
            }
            public void WriteByte(long offset, byte value)
            {
            }
            public void WriteWord(long offset, ushort value)
            {
            }
            public void WriteDoubleWord(long offset, uint value)
            {
            }
            public void WriteQuadWord(long offset, ulong value)
            {
            }
            public void WriteBytes(long offset, byte[] array, int start, int count, IPeripheral context = null)
            {
            }
        }
    }
}
