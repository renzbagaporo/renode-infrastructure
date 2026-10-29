//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_XIPMemoryTests
    {
        [TestCase(0x10000000u, 0)]
        [TestCase(0x14000000u, 0)]
        [TestCase(0x1c000000u, 0)]
        [TestCase(0x10000000u, 1)]
        [TestCase(0x14000000u, 1)]
        [TestCase(0x1c000000u, 1)]
        [TestCase(0x10000000u, 0, 0x1000u)]
        [TestCase(0x14000000u, 0, 0x1000u)]
        [TestCase(0x1c000000u, 1, 0x1000u)]
        [TestCase(0x10000000u, 0, 0u, true)]
        [TestCase(0x14000000u, 0, 0u, true)]
        [TestCase(0x1c000000u, 0, 0u, true)]
        [TestCase(0x10000000u, 1, 0u, true)]
        [TestCase(0x14000000u, 1, 0u, true)]
        [TestCase(0x1c000000u, 1, 0u, true)]
        [TestCase(0x12000000u, 0, 0x1ffc000u, false, true)]
        [TestCase(0x12000000u, 1, 0x1ffc000u, false, true)]
        [TestCase(0x10000000u, 0, 0u, false, false, true)]
        [TestCase(0x14000000u, 1, 0u, false, false, true)]
        [TestCase(0x1c000000u, 1, 0u, false, false, true)]
        [TestCase(0x10000000u, 0, 0u, true, false, false, true)]
        [TestCase(0x14000000u, 0, 0u, true, false, false, true)]
        [TestCase(0x1c000000u, 0, 0u, true, false, false, true)]
        [TestCase(0x10000000u, 1, 0u, true, false, false, true)]
        [TestCase(0x14000000u, 1, 0u, true, false, false, true)]
        [TestCase(0x1c000000u, 1, 0u, true, false, false, true)]
        public void ShouldObserveProgrammedInstructionsAfterAnEarlierFetch(uint alias, int core,
            uint registrationOffset = 0, bool mutateDuringFetch = false, bool pinned = false,
            bool registerCpuLate = false, bool loadThroughOtherAlias = false)
        {
            EmulationManager.Instance.Clear();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "xip_coherence");
            try
            {
                var memory = new MappedMemory(machine, 0x1000);
                var storage = new MappedMemory(machine, 0x400000);
                var flash = new WinbondW25QFlash(machine, storage);
                var qmi = new RP2350_QMI(machine, flash);
                var cache = pinned ? new RP2350_XIP(machine) : null;
                var window = new RP2350_XIPMemory(machine, flash, qmi,
                    untranslated: alias == 0x1c000000, useCache: pinned || alias == 0x10000000,
                    xip: cache, windowOffset: pinned ? 0x2000000 : 0);
                var nvic = new NVIC(machine);
                var cpu = new CortexM("cortex-m33-rp2350", machine, nvic, cpuId: (uint)core);
                void RegisterCpu()
                {
                    machine.SystemBus.Register(cpu, new CPURegistrationPoint(core));
                    machine.SetLocalName(cpu, "cpu");
                    machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
                    nvic.IRQ.Connect(cpu, 0);
                }
                if(!registerCpuLate) RegisterCpu();
                machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
                machine.SystemBus.Register(qmi, new BusRangeRegistration(0x400d0000, 0x4000));
                if(pinned) machine.SystemBus.Register(cache, new BusRangeRegistration(0x400c8000, 0x1000));
                machine.SystemBus.Register(window, new BusRangeRegistration(alias,
                    0x2000000 - registrationOffset, offset: registrationOffset));
                var loaderWindow = window;
                if(loadThroughOtherAlias)
                {
                    loaderWindow = new RP2350_XIPMemory(machine, flash, qmi);
                    machine.SystemBus.Register(loaderWindow, new BusRangeRegistration(
                        alias == 0x10000000 ? 0x14000000u : 0x10000000u, 0x2000000));
                }
                if(registerCpuLate) RegisterCpu();
                // A RAM caller enters the same flash address for both calls.
                // The host changes only the RAM command and flash contents;
                // it never changes PC or clears the native translation cache.
                memory.WriteDoubleWord(0, Ram + 0xff0);
                memory.WriteDoubleWord(4, Ram + 0x101);
                var caller = new ushort[]
                {
                    0x681a, // ldr r2, [r3]
                    0x2a00, // cmp r2, #0
                    0xd0fc, // beq wait
                    0x47a0, // blx r4
                    0x6028, // str r0, [r5]
                    0x2200, // movs r2, #0
                    0x601a, // str r2, [r3]
                    0xe7f7, // b wait
                };
                for(var i = 0; i < caller.Length; i++) memory.WriteWord(0x100 + 2 * i, caller[i]);
                if(pinned)
                {
                    new RP2350_XIPMaintenance(cache).WriteByte(0x3ffc007, 0);
                    window.WriteWord(registrationOffset, 0x2003); // movs r0, #3
                    window.WriteWord(registrationOffset + 2, 0x4770); // bx lr
                }
                else
                {
                    storage.WriteWord(registrationOffset, 0x2003);
                    storage.WriteWord(registrationOffset + 2, 0x4770);
                }
                cpu.InitVectorTableOffset = Ram;
                cpu.Reset();
                cpu.SetRegister(3, Ram + 0x200);
                cpu.SetRegister(4, alias | 1);
                cpu.SetRegister(5, Ram + 0x204);
                Task mutation = null;
                if(mutateDuringFetch)
                {
                    var captured = new ManualResetEventSlim();
                    var changed = new ManualResetEventSlim();
                    var armed = 1;
                    var path = Path.Combine(Path.GetTempPath(), "xip_race_" + Guid.NewGuid().ToString("N") + ".bin");
                    File.WriteAllBytes(path, new byte[] { 1, 0x20, 0x70, 0x47 });
                    mutation = Task.Run(() =>
                    {
                        Assert.IsTrue(captured.Wait(TimeSpan.FromSeconds(5)), "Native first read was not captured");
                        loaderWindow.LoadBinary(path);
                        changed.Set();
                    });
                    machine.SystemBus.SetHookAfterPeripheralRead<ushort>(window, (value, offset) =>
                    {
                        if(offset == registrationOffset && Interlocked.Exchange(ref armed, 0) == 1)
                        {
                            captured.Set();
                            Assert.IsTrue(changed.Wait(TimeSpan.FromSeconds(5)), "Concurrent flash load did not finish");
                        }
                        return value;
                    });
                }
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(0u, memory.ReadDoubleWord(0x200), "First native call did not return");
                if(mutation != null)
                {
                    Assert.IsTrue(mutation.Wait(TimeSpan.FromSeconds(5)));
                    Assert.That(memory.ReadDoubleWord(0x204), Is.EqualTo(1u).Or.EqualTo(3u),
                        "The in-flight first read may precede the change");
                }
                else if(pinned)
                {
                    Assert.AreEqual(3u, memory.ReadDoubleWord(0x204));
                    qmi.WriteDoubleWord(0, 1); // Serial direct mode cannot block pinned SRAM.
                    window.WriteWord(registrationOffset, 0x2001);
                    cache.Reset(); // Pinned tags and data survive the cache-domain reset.
                }
                else
                {
                    Assert.AreEqual(3u, memory.ReadDoubleWord(0x204));
                    flash.Transmit(0x06);
                    flash.FinishTransmission();
                    foreach(var value in new byte[] { 0x02, (byte)(registrationOffset >> 16),
                        (byte)(registrationOffset >> 8), (byte)registrationOffset, 0x01, 0x20 }) flash.Transmit(value);
                    flash.FinishTransmission();
                    emulation.RunFor(TimeInterval.FromMicroseconds(500));
                }
                Assert.AreEqual(0x2001, pinned ? window.ReadWord(registrationOffset) : storage.ReadWord(registrationOffset));
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(0u, memory.ReadDoubleWord(0x200), "Second native call did not return");
                Assert.AreEqual(1u, memory.ReadDoubleWord(0x204),
                    $"Stale flash opcode from alias 0x{alias:X}, core {core}, PC={cpu.PC}");
            }
            finally { emulation.RemoveMachine(machine); }
        }

        [TestCase(0x10000000u, 0, false)]
        [TestCase(0x14000000u, 0, false)]
        [TestCase(0x1c000000u, 0, false)]
        [TestCase(0x10000000u, 1, false)]
        [TestCase(0x14000000u, 1, false)]
        [TestCase(0x1c000000u, 1, false)]
        [TestCase(0x10000000u, 0, true)]
        [TestCase(0x14000000u, 0, true)]
        [TestCase(0x1c000000u, 0, true)]
        [TestCase(0x10000000u, 1, true)]
        [TestCase(0x14000000u, 1, true)]
        [TestCase(0x1c000000u, 1, true)]
        [TestCase(0x10000000u, 0, false, true)]
        [TestCase(0x14000000u, 1, false, true)]
        [TestCase(0x1c000000u, 0, true, true)]
        [TestCase(0x10000000u, 1, true, true)]
        public void ShouldFaultNativeFetchWhileSerialDirectModeOwnsFlash(uint alias, int core, bool warm,
            bool executeNever = false)
        {
            EmulationManager.Instance.Clear();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "xip_fetch_fault");
            try
            {
                var ram = new MappedMemory(machine, 0x1000);
                var storage = new MappedMemory(machine, 0x400000);
                var flash = new WinbondW25QFlash(machine, storage);
                var qmi = new RP2350_QMI(machine, flash);
                var window = new RP2350_XIPMemory(machine, flash, qmi,
                    untranslated: alias == 0x1c000000, useCache: alias == 0x10000000);
                var nvic = new NVIC(machine);
                var cpu = new CortexM("cortex-m33-rp2350", machine, nvic,
                    cpuId: (uint)core, enableTrustZone: true, numberOfMPURegions: 8);
                machine.SystemBus.Register(cpu, new CPURegistrationPoint(core));
                machine.SetLocalName(cpu, "cpu");
                machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
                nvic.IRQ.Connect(cpu, 0);
                machine.SystemBus.Register(ram, new BusRangeRegistration(Ram, 0x1000));
                machine.SystemBus.Register(qmi, new BusRangeRegistration(0x400d0000, 0x4000));
                machine.SystemBus.Register(window, new BusRangeRegistration(alias, 0x2000000));
                ram.WriteDoubleWord(0, Ram + 0xff0);
                ram.WriteDoubleWord(4, Ram + 0x101);
                ram.WriteDoubleWord(0x10, Ram + 0x301); // MemManage handler
                ram.WriteDoubleWord(0x14, Ram + 0x321); // BusFault handler
                var caller = new ushort[] { 0x681a, 0x2a00, 0xd0fc, 0x47a0, 0x6028, 0x2200, 0x601a, 0xe7f7 };
                for(var i = 0; i < caller.Length; i++) ram.WriteWord(0x100 + 2 * i, caller[i]);
                ram.WriteWord(0x300, 0x2604); // movs r6, #4
                ram.WriteWord(0x302, 0x602e); // str r6, [r5]
                ram.WriteWord(0x304, 0xe7fe);
                ram.WriteWord(0x320, 0x2605); // movs r6, #5
                ram.WriteWord(0x322, 0x602e);
                ram.WriteWord(0x324, 0xe7fe);
                storage.WriteWord(0, 0x2003); // This must not execute while QMI owns the bus.
                storage.WriteWord(2, 0x4770);
                cpu.InitVectorTableOffset = Ram;
                cpu.Reset();
                cpu.SetRegister(3, Ram + 0x200);
                cpu.SetRegister(4, alias | 1);
                cpu.SetRegister(5, Ram + 0x204);
                var access = (ulong)new CortexM.ContextState { Privileged = true, CpuSecure = true, AttributionSecure = true };
                machine.SystemBus.WriteDoubleWord(0xe000ed24, (1u << 16) | (1u << 17), cpu, access);
                var previousBusFaultAddress = machine.SystemBus.ReadDoubleWord(0xe000ed38, cpu, access);
                if(warm)
                {
                    ram.WriteDoubleWord(0x200, 1);
                    emulation.RunFor(TimeInterval.FromMicroseconds(100));
                    Assert.AreEqual(3u, ram.ReadDoubleWord(0x204));
                    cpu.SetRegister(0, 0);
                }
                if(executeNever)
                {
                    machine.SystemBus.WriteDoubleWord(0xe000ed98, 0, cpu, access);
                    machine.SystemBus.WriteDoubleWord(0xe000ed9c, alias | 1, cpu, access);
                    machine.SystemBus.WriteDoubleWord(0xe000eda0, ((alias + 0xfff) & ~31u) | 1, cpu, access);
                    machine.SystemBus.WriteDoubleWord(0xe000ed94, 5, cpu, access);
                }
                qmi.WriteDoubleWord(0, 1);
                ram.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(executeNever ? 4u : 5u, ram.ReadDoubleWord(0x204), "Actual fault handler did not execute");
                var faults = machine.SystemBus.ReadDoubleWord(0xe000ed28, cpu, access);
                Assert.AreEqual(executeNever ? 1u : 0x100u, faults & 0xffffu,
                    $"Wrong instruction-fetch syndrome, CFSR={faults:X}, PC={cpu.PC}");
                Assert.AreEqual(previousBusFaultAddress, machine.SystemBus.ReadDoubleWord(0xe000ed38, cpu, access),
                    "Instruction-fetch failure must not overwrite BFAR");
                Assert.AreEqual(0, (uint)cpu.GetRegister(0), "The unavailable flash opcode executed");
                var sp = (uint)cpu.GetRegister(13);
                Assert.AreEqual(alias, ram.ReadDoubleWord(sp - Ram + 24), "Wrong stacked fetch PC");
            }
            finally { emulation.RemoveMachine(machine); }
        }

        [Test]
        public void ShouldInvalidateBothNativeCoresSharingFlashThroughDifferentAliases()
        {
            EmulationManager.Instance.Clear();
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "shared_xip_coherence");
            try
            {
                var ram = new MappedMemory(machine, 0x1000);
                var storage = new MappedMemory(machine, 0x400000);
                var flash = new WinbondW25QFlash(machine, storage);
                var qmi = new RP2350_QMI(machine, flash);
                machine.SystemBus.Register(ram, new BusRangeRegistration(Ram, 0x1000));
                machine.SystemBus.Register(qmi, new BusRangeRegistration(0x400d0000, 0x4000));
                var aliases = new uint[] { 0x10000000, 0x14000000 };
                for(var core = 0; core < 2; core++)
                {
                    var nvic = new NVIC(machine);
                    var cpu = new CortexM("cortex-m33-rp2350", machine, nvic, cpuId: (uint)core);
                    machine.SystemBus.Register(cpu, new CPURegistrationPoint(core));
                    machine.SetLocalName(cpu, "cpu" + core);
                    machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
                    nvic.IRQ.Connect(cpu, 0);
                    var window = new RP2350_XIPMemory(machine, flash, qmi, useCache: core == 0);
                    machine.SystemBus.Register(window, new BusRangeRegistration(aliases[core], 0x2000000));
                    cpu.InitVectorTableOffset = Ram;
                    cpu.Reset();
                    cpu.SetRegister(3, Ram + 0x200 + (uint)core * 16);
                    cpu.SetRegister(4, aliases[core] | 1);
                    cpu.SetRegister(5, Ram + 0x204 + (uint)core * 16);
                }
                ram.WriteDoubleWord(0, Ram + 0xff0);
                ram.WriteDoubleWord(4, Ram + 0x101);
                var caller = new ushort[] { 0x681a, 0x2a00, 0xd0fc, 0x47a0, 0x6028, 0x2200, 0x601a, 0xe7f7 };
                for(var i = 0; i < caller.Length; i++) ram.WriteWord(0x100 + 2 * i, caller[i]);
                storage.WriteWord(0, 0x2003);
                storage.WriteWord(2, 0x4770);
                machine.PostCreationActions();
                ram.WriteDoubleWord(0x200, 1);
                ram.WriteDoubleWord(0x210, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(0u, ram.ReadDoubleWord(0x200));
                Assert.AreEqual(0u, ram.ReadDoubleWord(0x210));
                Assert.AreEqual(3u, ram.ReadDoubleWord(0x204));
                Assert.AreEqual(3u, ram.ReadDoubleWord(0x214));
                flash.Transmit(0x06);
                flash.FinishTransmission();
                foreach(var value in new byte[] { 0x02, 0, 0, 0, 1, 0x20 }) flash.Transmit(value);
                flash.FinishTransmission();
                emulation.RunFor(TimeInterval.FromMicroseconds(500));
                Assert.AreEqual(0x2001, storage.ReadWord(0));
                ram.WriteDoubleWord(0x200, 1);
                ram.WriteDoubleWord(0x210, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(0u, ram.ReadDoubleWord(0x200));
                Assert.AreEqual(0u, ram.ReadDoubleWord(0x210));
                Assert.That(new uint[] { ram.ReadDoubleWord(0x204), ram.ReadDoubleWord(0x214) },
                    Is.EqualTo(new uint[] { 1, 1 }), "Both native cores must discard their cached flash opcode");
            }
            finally { emulation.RemoveMachine(machine); }
        }

        private const uint Ram = 0x20000000;
    }
}
