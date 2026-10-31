//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging.Profiling;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_BUSCTRLTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            controller = new RP2040_BUSCTRL(machine);
            machine.SystemBus.Register(controller, new BusRangeRegistration(Busctrl, 0x4000));
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldResetEveryCounterAndDisableItsSelector()
        {
            for(var i = 0; i < 4; i++)
            {
                Assert.AreEqual(31u, controller.ReadDoubleWord(12 + i * 8));
                Assert.AreEqual(0u, controller.ReadDoubleWord(8 + i * 8));
                controller.WriteDoubleWord(12 + i * 8, 5);
            }
            controller.NotifyBusAccess(Sram5);
            controller.Reset();
            Assert.AreEqual(0u, controller.ReadDoubleWord(0));
            Assert.AreEqual(0u, controller.ReadDoubleWord(4));
            controller.NotifyBusAccess(Sram5);
            for(var i = 0; i < 4; i++)
            {
                Assert.AreEqual(31u, controller.ReadDoubleWord(12 + i * 8));
                Assert.AreEqual(0u, controller.ReadDoubleWord(8 + i * 8));
            }
        }

        [TestCase(0x40030000ul, 1u)]
        [TestCase(0x50000000ul, 3u)]
        [TestCase(0x50400000ul, 3u)]
        [TestCase(0x20041000ul, 5u)]
        [TestCase(0x20040000ul, 7u)]
        [TestCase(0x2000000cul, 9u)]
        [TestCase(0x20000008ul, 11u)]
        [TestCase(0x20000004ul, 13u)]
        [TestCase(0x20000000ul, 15u)]
        [TestCase(0x21000000ul, 15u)]
        [TestCase(0x21010000ul, 13u)]
        [TestCase(0x21020000ul, 11u)]
        [TestCase(0x21030000ul, 9u)]
        [TestCase(0x10000000ul, 17u)]
        [TestCase(0x13000000ul, 17u)]
        [TestCase(0x18000000ul, 17u)]
        [TestCase(0x00000100ul, 19u)]
        public void ShouldCountTheSelectedPortAndOnlyActualContestedCompletions(ulong address, uint selectedEvent)
        {
            controller.WriteDoubleWord(12, selectedEvent);
            controller.WriteDoubleWord(20, selectedEvent - 1);
            controller.WriteDoubleWord(28, selectedEvent);
            controller.NotifyBusAccess(address, count: 3);
            controller.NotifyBusAccess(address, contested: true, count: 2);
            Assert.AreEqual(5u, controller.ReadDoubleWord(8));
            Assert.AreEqual(2u, controller.ReadDoubleWord(16));
            Assert.AreEqual(5u, controller.ReadDoubleWord(24));
            Assert.AreEqual(0u, controller.ReadDoubleWord(32));
        }

        [TestCase(20u)] [TestCase(21u)] [TestCase(30u)] [TestCase(31u)]
        public void ShouldRetainDisabledReservedSelectorsWithoutCounting(uint selector)
        {
            controller.WriteDoubleWord(12, selector);
            foreach(var address in new[] { 0ul, 0x10000000ul, Sram5, 0x40030000ul, 0x50000000ul })
                controller.NotifyBusAccess(address, contested: true, count: uint.MaxValue);
            Assert.AreEqual(selector, controller.ReadDoubleWord(12));
            Assert.AreEqual(0u, controller.ReadDoubleWord(8));
        }

        [TestCase(0xd0000000ul)] [TestCase(0xe000e000ul)] [TestCase(0x20042000ul)]
        public void ShouldExcludePrivateProcessorAndUnmappedSramAddresses(ulong address)
        {
            for(uint selector = 0; selector < 20; selector++)
            {
                controller.WriteDoubleWord(12, selector);
                controller.NotifyBusAccess(address, contested: true);
                Assert.AreEqual(0u, controller.ReadDoubleWord(8));
            }
        }

        [TestCase(0)] [TestCase(0x1000)] [TestCase(0x2000)] [TestCase(0x3000)]
        public void ShouldSaturateAndClearOnAnyWriteIncludingAtomicAliases(int alias)
        {
            controller.WriteDoubleWord(12, 5);
            controller.NotifyBusAccess(Sram5, count: 0xfffffe);
            Assert.AreEqual(0xfffffeu, controller.ReadDoubleWord(8));
            controller.NotifyBusAccess(Sram5, count: uint.MaxValue);
            Assert.AreEqual(0xffffffu, controller.ReadDoubleWord(8));
            controller.NotifyBusAccess(Sram5);
            Assert.AreEqual(0xffffffu, controller.ReadDoubleWord(8));
            controller.WriteDoubleWord(alias + 8, 0xdeadbeef);
            Assert.AreEqual(0u, controller.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldNotManufactureEventsWhenInspectingCountersOrAdvancingTime()
        {
            controller.WriteDoubleWord(12, 5);
            controller.NotifyBusAccess(Sram5, count: 17);
            for(var i = 0; i < 100; i++) Assert.AreEqual(17u, controller.ReadDoubleWord(8));
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(500));
            Assert.AreEqual(17u, controller.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldMaskPriorityAndSelectorFieldsAndApplyAtomicOperations()
        {
            controller.WriteDoubleWord(0, uint.MaxValue);
            Assert.AreEqual(0x1111u, controller.ReadDoubleWord(0));
            Assert.AreEqual(1u, controller.ReadDoubleWord(4));
            controller.WriteDoubleWord(0x3000, 0x1010);
            Assert.AreEqual(0x101u, controller.ReadDoubleWord(0));
            controller.WriteDoubleWord(0x1000, 0x1101);
            Assert.AreEqual(0x1000u, controller.ReadDoubleWord(0));
            controller.WriteDoubleWord(0x2000, 0x10);
            Assert.AreEqual(0x1010u, controller.ReadDoubleWord(0));
            controller.WriteDoubleWord(0x300c, 0x1a);
            Assert.AreEqual(5u, controller.ReadDoubleWord(12));
            controller.WriteDoubleWord(0x100c, 0x10000001);
            Assert.AreEqual(4u, controller.ReadDoubleWord(12));
            controller.WriteDoubleWord(0x200c, 0x10000002);
            Assert.AreEqual(6u, controller.ReadDoubleWord(12));
            controller.WriteDoubleWord(4, 0);
            Assert.AreEqual(1u, controller.ReadDoubleWord(4));
        }

        [Test]
        public void ShouldReplicateNarrowWritesAndKeepReadLanes()
        {
            controller.WriteByte(1, 1);
            Assert.AreEqual(0x101u, controller.ReadDoubleWord(0));
            Assert.AreEqual(1, controller.ReadByte(0));
            Assert.AreEqual(1, controller.ReadByte(1));
            Assert.AreEqual(0, controller.ReadByte(2));
            controller.WriteWord(2, 0x10);
            Assert.AreEqual(0x10u, controller.ReadDoubleWord(0));
            controller.WriteByte(12, 5);
            controller.NotifyBusAccess(Sram5, count: 123);
            controller.WriteByte(9, 0);
            Assert.AreEqual(0u, controller.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldHoldResetAndResumeOnlyAfterReconfiguration()
        {
            controller.WriteDoubleWord(12, 5);
            controller.NotifyBusAccess(Sram5);
            controller.ResetHeld = true;
            controller.WriteDoubleWord(12, 5);
            controller.NotifyBusAccess(Sram5, count: 100);
            Assert.AreEqual(31u, controller.ReadDoubleWord(12));
            Assert.AreEqual(0u, controller.ReadDoubleWord(8));
            controller.ResetHeld = false;
            controller.NotifyBusAccess(Sram5);
            Assert.AreEqual(0u, controller.ReadDoubleWord(8));
            controller.WriteDoubleWord(12, 5);
            controller.NotifyBusAccess(Sram5);
            Assert.AreEqual(1u, controller.ReadDoubleWord(8));
        }

        [TestCase(0)] [TestCase(1)]
        public void ShouldCountActualMappedNativeDataAfterWarmTranslationAndDisableCleanly(int core)
        {
            WithNative(core, (emulation, cpu, busctrl, data, execute) =>
            {
                execute();
                Assert.AreEqual(1u, data.ReadDoubleWord(0), $"PC={cpu.PC.RawValue:x}, IPSR={cpu.XProgramStatusRegister & 0x1ff}, R1={cpu.GetRegister(1).RawValue:x}, instructions={cpu.ExecutedInstructions}, halted={cpu.IsHalted}");
                Assert.AreEqual(0u, busctrl.ReadDoubleWord(8));
                busctrl.WriteDoubleWord(12, 5);
                execute();
                Assert.AreEqual(2u, data.ReadDoubleWord(0));
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8), "One real load and one real store; native decode fetches are excluded");
                busctrl.WriteDoubleWord(12, 31);
                execute();
                Assert.AreEqual(3u, data.ReadDoubleWord(0));
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8));
            });
        }

        [Test]
        public void ShouldStartCountingImmediatelyAfterNativeSelectorWriteInWarmBlock()
        {
            WithNative(0, (emulation, cpu, busctrl, data, execute) =>
            {
                cpu.SetRegister(0, 31);
                cpu.SetRegister(2, Busctrl + 12);
                execute();
                Assert.AreEqual(1u, data.ReadDoubleWord(0));
                Assert.AreEqual(0u, busctrl.ReadDoubleWord(8));
                cpu.SetRegister(0, 5);
                execute();
                Assert.AreEqual(2u, data.ReadDoubleWord(0));
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8));
                cpu.SetRegister(0, 31);
                execute();
                Assert.AreEqual(3u, data.ReadDoubleWord(0));
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8), "Native disable must also affect the following accesses in the same warm block");
            }, configureFromGuest: true);
        }

        [Test]
        public void ShouldSwitchObserversInsideAlreadyRunningMailboxLoopWithoutChangingPc()
        {
            WithNative(0, (emulation, cpu, busctrl, data, execute) =>
            {
                var target = cpu.GetMachine();
                var mailbox = new MappedMemory(target, 0x1000);
                target.SystemBus.Register(mailbox, new BusRangeRegistration(0x20040000, 0x1000));
                mailbox.WriteDoubleWord(0, 31);
                cpu.SetRegister(2, Busctrl + 12);
                cpu.SetRegister(3, 0x20040000);
                cpu.SetRegister(6, 0);
                cpu.PC = 0x101; // Initial entry only; all three phases continue the running loop.
                using(var completed = new ManualResetEventSlim())
                {
                    cpu.AddHook(0x110, (_, __) => completed.Set());
                    Action<uint, uint, uint> phase = (selector, expectedData, expectedCount) =>
                    {
                        completed.Reset();
                        mailbox.WriteDoubleWord(0, selector);
                        emulation.StartAll();
                        try { Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(10))); }
                        finally { emulation.PauseAll(); }
                        Assert.AreEqual(expectedData, data.ReadDoubleWord(0));
                        Assert.AreEqual(expectedCount, busctrl.ReadDoubleWord(8));
                    };
                    phase(31, 1, 0);
                    phase(5, 2, 2);
                    phase(31, 3, 2);
                }
            }, program: new ushort[] {
                0x6818, // ldr r0,[r3] -- mailbox is on SRAM4, not the selected SRAM5 port
                0x42b0, // cmp r0,r6
                0xd0fc, // beq 0x100 -- same translated polling loop across phases
                0x4606, // mov r6,r0
                0x6010, // str r0,[r2] -- switch the BUSCTRL observer inside the executing block
                0x680c, // ldr r4,[r1]
                0x3401, // adds r4,#1
                0x600c, // str r4,[r1]
                0xe7f6  // b 0x100
            });
        }

        [Test]
        public void ShouldRetireEveryStoreAndWritebackWhenEnablingInsideStm()
        {
            WithNative(0, (emulation, cpu, busctrl, data, execute) =>
            {
                busctrl.WriteDoubleWord(20, 5);
                busctrl.NotifyBusAccess(Sram5, count: 7);
                busctrl.WriteDoubleWord(20, 31);
                Assert.AreEqual(7u, busctrl.ReadDoubleWord(16));
                cpu.SetRegister(0, 5);
                cpu.SetRegister(2, Busctrl + 12);
                cpu.SetRegister(3, 0xdeadbeef);
                execute();
                Assert.AreEqual(1u, data.ReadDoubleWord(0));
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8));
                Assert.AreEqual(0u, busctrl.ReadDoubleWord(16), "The second STM transfer must clear PERFCTR1");
                Assert.AreEqual(Busctrl + 20, cpu.GetRegister(2).RawValue, "STM base writeback must retire normally");
            }, configureFromGuest: true, multipleRegisterWrite: true);
        }

        [Test]
        public void ShouldComposeLegacyAndMultipleNativeMemoryObservers()
        {
            WithNative(0, (emulation, cpu, busctrl, data, execute) =>
            {
                var legacy = 0;
                var first = 0;
                var second = 0;
                MemoryAccessHook oldHook = (pc, operation, va, pa, width, value) => { if(pa == Sram5) legacy++; };
                MemoryAccessHook firstHook = (pc, operation, va, pa, width, value) => { if(pa == Sram5) first++; };
                MemoryAccessHook secondHook = (pc, operation, va, pa, width, value) => { if(pa == Sram5) second++; };
                cpu.SetHookAtMemoryAccess(oldHook);
                cpu.AddMemoryAccessHook(firstHook);
                cpu.AddMemoryAccessHook(secondHook);
                busctrl.WriteDoubleWord(12, 5);
                execute();
                Assert.AreEqual(2, legacy); Assert.AreEqual(2, first); Assert.AreEqual(2, second);
                cpu.RemoveMemoryAccessHook(firstHook);
                execute();
                Assert.AreEqual(4, legacy); Assert.AreEqual(2, first); Assert.AreEqual(4, second);
                cpu.SetHookAtMemoryAccess(null);
                execute();
                Assert.AreEqual(4, legacy); Assert.AreEqual(2, first); Assert.AreEqual(6, second);
                Assert.AreEqual(6u, busctrl.ReadDoubleWord(8));
                busctrl.WriteDoubleWord(12, 31);
                cpu.RemoveMemoryAccessHook(secondHook);
                execute();
                Assert.AreEqual(6, second);
                Assert.AreEqual(4u, data.ReadDoubleWord(0));
            });
        }

        [TestCase(0x7808, 0x7008, 0x80u, 1)] // ldrb / strb
        [TestCase(0x8808, 0x8008, 0x8080u, 2)] // ldrh / strh
        [TestCase(0x5688, 0x7008, 0xffffff80u, 1)] // ldrsb / strb
        [TestCase(0x5e88, 0x8008, 0xffff8080u, 2)] // ldrsh / strh
        [TestCase(0x6808, 0x6008, 0x80008080u, 4)] // ldr / str
        public void ShouldPreserveNativeLoadSignednessAndStoreWidths(int load, int store, uint expected, int width)
        {
            WithNative(0, (emulation, cpu, busctrl, data, execute) =>
            {
                data.WriteDoubleWord(0, 0x80008080);
                cpu.SetRegister(2, 0);
                execute(); // Warm the same translations and nonzero data TLB slot with observation disabled.
                Assert.AreEqual(expected, cpu.GetRegister(0).RawValue);
                var widths = new System.Collections.Generic.List<byte>();
                MemoryAccessHook hook = (pc, operation, va, pa, size, value) => { if(pa == Sram5) widths.Add((byte)size); };
                cpu.AddMemoryAccessHook(hook);
                busctrl.WriteDoubleWord(12, 5);
                execute();
                Assert.AreEqual(expected, cpu.GetRegister(0).RawValue);
                Assert.AreEqual(0x80008080u, data.ReadDoubleWord(0));
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8));
                CollectionAssert.AreEqual(new[] { (byte)width, (byte)width }, widths);
                busctrl.WriteDoubleWord(12, 31);
                cpu.RemoveMemoryAccessHook(hook);
                execute();
                Assert.AreEqual(expected, cpu.GetRegister(0).RawValue);
                Assert.AreEqual(2u, busctrl.ReadDoubleWord(8));
            }, program: new[] { (ushort)load, (ushort)store });
        }

        [Test]
        public void ShouldPreserveNativeDoublewordTransfersAcrossObserverTransitions()
        {
            WithNative(0, (emulation, cpu, busctrl, data, execute) =>
            {
                data.WriteDoubleWord(0, 0x12345678);
                data.WriteDoubleWord(4, 0x89abcdef);
                execute();
                busctrl.WriteDoubleWord(12, 5);
                execute();
                Assert.AreEqual(0x12345678u, cpu.GetRegister(0).RawValue);
                Assert.AreEqual(0x89abcdefu, cpu.GetRegister(2).RawValue);
                Assert.AreEqual(0x12345678u, data.ReadDoubleWord(0));
                Assert.AreEqual(0x89abcdefu, data.ReadDoubleWord(4));
                Assert.AreEqual(4u, busctrl.ReadDoubleWord(8)); // Two word beats in each direction.
                busctrl.WriteDoubleWord(12, 31);
                execute();
                Assert.AreEqual(4u, busctrl.ReadDoubleWord(8));
            }, program: new ushort[] { 0xe9d1, 0x0200, 0xe9c1, 0x0200 }, cpuType: "cortex-m33");
        }

        [Test]
        public void ShouldCountAcceptedDmaReadsAndTheirDrainedWrites()
        {
            var source = new MappedMemory(machine, 0x1000);
            var destination = new MappedMemory(machine, 0x1000);
            var dma = new RP2040_DMA(machine, frequency: 1000000);
            var busctrl = new RP2040_BUSCTRL(machine, dma: dma);
            machine.SystemBus.Register(source, new BusRangeRegistration(Sram5, 0x1000));
            machine.SystemBus.Register(destination, new BusRangeRegistration(0x20040000, 0x1000));
            machine.SystemBus.Register(dma, new BusRangeRegistration(0x50000000, 0x4000));
            machine.SystemBus.Register(busctrl, new BusRangeRegistration(0x40070000, 0x4000));
            source.WriteDoubleWord(0, 0x12345678);
            busctrl.WriteDoubleWord(12, 5);
            busctrl.WriteDoubleWord(20, 7);
            dma.WriteDoubleWord(0x10, 1u | (2u << 2) | (63u << 15));
            dma.WriteDoubleWord(0, (uint)Sram5);
            dma.WriteDoubleWord(4, 0x20040000);
            dma.WriteDoubleWord(8, 1);
            dma.WriteDoubleWord(0x430, 1);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(1));
            Assert.AreEqual(1u, busctrl.ReadDoubleWord(8));
            Assert.AreEqual(0u, busctrl.ReadDoubleWord(16));
            dma.WriteDoubleWord(0x444, 1); // Accepted reads still drain through the write pipeline.
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(1));
            Assert.AreEqual(0x12345678u, destination.ReadDoubleWord(0));
            Assert.AreEqual(1u, busctrl.ReadDoubleWord(8));
            Assert.AreEqual(1u, busctrl.ReadDoubleWord(16));
        }

        private static void WithNative(int core, Action<Emulation, CortexM, RP2040_BUSCTRL, MappedMemory, Action> action,
            bool configureFromGuest = false, bool multipleRegisterWrite = false, ushort[] program = null, string cpuType = "cortex-m0+")
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var target = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(target, "busctrl_native");
            try
            {
                var rom = new MappedMemory(target, 0x1000);
                var data = new MappedMemory(target, 0x1000);
                target.SystemBus.Register(rom, new BusRangeRegistration(0, 0x1000));
                target.SystemBus.Register(data, new BusRangeRegistration(Sram5, 0x1000));
                rom.WriteDoubleWord(0, (uint)Sram5 + 0xff0);
                rom.WriteDoubleWord(4, 0x101);
                var completionAddress = configureFromGuest ? 0x108ul : 0x106ul;
                if(program != null)
                {
                    for(var i = 0; i < program.Length; i++) rom.WriteWord(0x100 + 2 * i, program[i]);
                    completionAddress = (ulong)(0x100 + 2 * program.Length);
                    rom.WriteWord((long)completionAddress, 0x3401); // adds r4,#1
                    rom.WriteWord((long)completionAddress + 2, 0xe7fd); // b to active loop
                }
                else if(configureFromGuest)
                {
                    rom.WriteWord(0x100, multipleRegisterWrite ? (ushort)0xc209 : (ushort)0x6010); // stmia r2!,{r0,r3} / str r0,[r2]
                    rom.WriteWord(0x102, multipleRegisterWrite ? (ushort)0x680c : (ushort)0x680b); // ldr r4/r3,[r1]
                    rom.WriteWord(0x104, multipleRegisterWrite ? (ushort)0x3401 : (ushort)0x3301); // adds r4/r3,#1
                    rom.WriteWord(0x106, multipleRegisterWrite ? (ushort)0x600c : (ushort)0x600b); // str r4/r3,[r1]
                    rom.WriteWord(0x108, 0x3501); // adds r5,#1 -- keep the core active after publication
                    rom.WriteWord(0x10a, 0xe7fd); // b 0x108
                }
                else
                {
                    rom.WriteWord(0x100, 0x6808); // ldr r0,[r1]
                    rom.WriteWord(0x102, 0x3001); // adds r0,#1
                    rom.WriteWord(0x104, 0x6008); // str r0,[r1]
                    rom.WriteWord(0x106, 0x3401); // adds r4,#1 -- keep the core active after publication
                    rom.WriteWord(0x108, 0xe7fd); // b 0x106
                }
                var nvic = new NVIC(target);
                var cpu = new CortexM(cpuType, target, nvic, cpuId: (uint)core);
                target.SystemBus.Register(cpu, new CPURegistrationPoint(core));
                target.SetLocalName(cpu, "cpu" + core);
                target.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
                nvic.IRQ.Connect(cpu, 0);
                cpu.PerformanceInMips = 1;
                var busctrl = new RP2040_BUSCTRL(target, core0: cpu);
                target.SystemBus.Register(busctrl, new BusRangeRegistration(Busctrl, 0x4000));
                target.PostCreationActions();
                cpu.InitVectorTableOffset = 0;
                cpu.Reset();
                cpu.SetRegister(1, Sram5);
                using(var completed = new ManualResetEventSlim())
                {
                    // Keep this hook installed across runs so the warm-cache
                    // checks genuinely reuse translated code and data paths.
                    cpu.AddHook(completionAddress, (_, __) => completed.Set());
                    Action execute = () =>
                    {
                        completed.Reset();
                        cpu.PC = 0x101;
                        emulation.StartAll();
                        try
                        {
                            Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(10)),
                                $"Native access sequence did not complete: PC={cpu.PC.RawValue:x}, instructions={cpu.ExecutedInstructions}");
                        }
                        finally { emulation.PauseAll(); }
                    };
                    action(emulation, cpu, busctrl, data, execute);
                }
            }
            finally { emulation.RemoveMachine(target); }
        }

        private Machine machine;
        private RP2040_BUSCTRL controller;
        private const ulong Sram5 = 0x20041000;
        private const ulong Busctrl = 0x40030000;
    }
}
