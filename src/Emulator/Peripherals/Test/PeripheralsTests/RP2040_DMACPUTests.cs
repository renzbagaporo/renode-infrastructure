//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_DMACPUTests
    {
        [Test]
        public void ShouldObserveClockedCompletionWhileExecutingNativeBusyPoll()
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "dma_cpu_test");
            try
            {
                var memory = new MappedMemory(machine, 0x1000);
                var dma = new RP2040_DMA(machine, frequency: 1000000);
                var nvic = new NVIC(machine);
                var cpu = new CortexM("cortex-m0+", machine, nvic);
                machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
                machine.SetLocalName(cpu, "cpu");
                machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
                machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
                machine.SystemBus.Register(dma, new BusRangeRegistration(Dma, 0x4000));
                cpu.PerformanceInMips = 100;
                memory.WriteDoubleWord(0, Ram + 0xff0);
                memory.WriteDoubleWord(4, Ram + 0x101);
                var program = new ushort[]
                {
                    0x2301, // movs r3, #1
                    0x6013, // str r3, [r2] (trigger DMA)
                    0x3401, // adds r4, #1
                    0x6803, // ldr r3, [r0] (CTRL_TRIG)
                    0x420b, // tst r3, r1 (BUSY)
                    0xd1fb, // bne polling loop
                    0x6034, // str r4, [r6] (record poll count)
                    0xbf30, // wfi
                    0xe7fe, // b .
                };
                for(var i = 0; i < program.Length; i++) memory.WriteWord(0x100 + 2 * i, program[i]);
                memory.WriteDoubleWord(0x400, 0x11223344);
                dma.WriteDoubleWord(0, Ram + 0x400);
                dma.WriteDoubleWord(4, Ram + 0x404);
                dma.WriteDoubleWord(8, 1);
                dma.WriteDoubleWord(0x10, 1u | (2u << 2) | (63u << 15));
                cpu.InitVectorTableOffset = Ram;
                cpu.Reset();
                cpu.SetRegister(0, Dma + 12);
                cpu.SetRegister(1, 1u << 24);
                cpu.SetRegister(2, Dma + 0x430);
                cpu.SetRegister(4, 0);
                cpu.SetRegister(6, Ram + 0x300);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(0x11223344u, memory.ReadDoubleWord(0x404));
                var polls = memory.ReadDoubleWord(0x300);
                Assert.That(polls, Is.GreaterThan(0).And.LessThan(200),
                    $"Polls={polls}, PC={cpu.PC}, instructions={cpu.ExecutedInstructions}");
            }
            finally { emulation.RemoveMachine(machine); }
        }

        [Test]
        public void ShouldReportCPUProgressBeforeReadingIdleDMARegisters()
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "dma_cpu_sync_test");
            try
            {
                var memory = new MappedMemory(machine, 0x1000);
                var dma = new RP2040_DMA(machine, frequency: 1000000);
                var nvic = new NVIC(machine);
                var cpu = new CortexM("cortex-m0+", machine, nvic);
                machine.SystemBus.Register(cpu, new CPURegistrationPoint(0));
                machine.SetLocalName(cpu, "cpu");
                machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
                machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
                machine.SystemBus.Register(dma, new BusRangeRegistration(Dma, 0x4000));
                machine.SystemBus.Register(new ProgressProbe(machine), new BusRangeRegistration(0x40000000, 4));
                cpu.PerformanceInMips = 100;
                memory.WriteDoubleWord(0, Ram + 0xff0);
                memory.WriteDoubleWord(4, Ram + 0x101);
                for(var i = 0; i < 32; i++) memory.WriteWord(0x100 + 2 * i, 0xbf00); // nop
                // End a native block so SyncTime can report its retired
                // instructions, independently of partial-block accounting.
                memory.WriteWord(0x140, 0xe000); // b 0x20000144
                memory.WriteWord(0x142, 0xbf00); // skipped nop
                memory.WriteWord(0x144, 0x6808); // ldr r0, [r1] (idle DMA CTRL_TRIG)
                memory.WriteWord(0x146, 0x681a); // ldr r2, [r3] (observe reported CPU progress without syncing)
                memory.WriteWord(0x148, 0x6032); // str r2, [r6]
                memory.WriteWord(0x14a, 0xbf30); // wfi
                memory.WriteWord(0x14c, 0xe7fe); // b .
                cpu.InitVectorTableOffset = Ram;
                cpu.Reset();
                cpu.SetRegister(1, Dma + 12);
                cpu.SetRegister(3, 0x40000000);
                cpu.SetRegister(6, Ram + 0x300);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                var reportedCycles = memory.ReadDoubleWord(0x300);
                Assert.That(reportedCycles, Is.GreaterThanOrEqualTo(32u).And.LessThanOrEqualTo(34u),
                    $"Reported CPU cycles={reportedCycles}, PC={cpu.PC}, instructions={cpu.ExecutedInstructions}");
            }
            finally { emulation.RemoveMachine(machine); }
        }

        private sealed class ProgressProbe : IDoubleWordPeripheral
        {
            public ProgressProbe(Machine machine) { this.machine = machine; }
            public uint ReadDoubleWord(long offset)
            {
                Assert.IsTrue(machine.SystemBus.TryGetTransactionInitiator(out var initiator));
                return (uint)((BaseCPU)initiator).ElapsedCycles;
            }
            public void WriteDoubleWord(long offset, uint value) { }
            public void Reset() { }
            private readonly Machine machine;
        }

        private const uint Ram = 0x20000000, Dma = 0x50000000;
    }
}
