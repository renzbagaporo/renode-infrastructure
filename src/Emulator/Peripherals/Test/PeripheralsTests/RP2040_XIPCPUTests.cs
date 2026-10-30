//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
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
    public class RP2040_XIPCPUTests
    {
        [Test]
        public void ShouldFaultOnDisabledFetchAfterAnEarlierCachedCall(
            [Values(0, 1)] int core, [Values(false, true)] bool warm,
            [Values(0x10000000u, 0x11000000u, 0x12000000u, 0x13000000u)] uint alias)
        {
            WithCaller(core, alias, (emulation, machine, memory, ssi, flash) =>
            {
                if(warm)
                {
                    memory.WriteDoubleWord(0x200, 1);
                    emulation.RunFor(TimeInterval.FromMicroseconds(100));
                    Assert.AreEqual(7u, memory.ReadDoubleWord(0x204));
                }
                ssi.WriteDoubleWord(8, 0);
                Assert.AreEqual(0u, machine.SystemBus.ReadDoubleWord(alias), "Disabled data reads retain their zero value");
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(0x55u, memory.ReadDoubleWord(0x204), "A cached XIP block must not execute after SSI is disabled");
            });
        }

        [Test]
        public void ShouldFetchTheCommittedNorBytesAfterAnEarlierCachedCall(
            [Values(0, 1)] int core, [Values(false, true)] bool disable,
            [Values(0x10000000u, 0x11000000u, 0x12000000u, 0x13000000u)] uint alias)
        {
            WithCaller(core, alias, (emulation, machine, memory, ssi, flash) =>
            {
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(7u, memory.ReadDoubleWord(0x204));
                if(disable) ssi.WriteDoubleWord(8, 0);
                Command(flash, 6);
                Command(flash, 2, 0, 0, 0, 3); // movs r0,#7 -> movs r0,#3 by NOR AND
                Assert.AreEqual(7, flash.UnderlyingMemory.ReadByte(0));
                Assert.AreEqual(0u, machine.SystemBus.ReadDoubleWord(alias));
                emulation.RunFor(TimeInterval.FromMicroseconds(399));
                Assert.AreEqual(7, flash.UnderlyingMemory.ReadByte(0));
                emulation.RunFor(TimeInterval.FromMicroseconds(1));
                Assert.AreEqual(3, flash.UnderlyingMemory.ReadByte(0));
                if(disable) ssi.WriteDoubleWord(8, 1);
                Assert.AreEqual(0x47702003u, machine.SystemBus.ReadDoubleWord(alias));
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(3u, memory.ReadDoubleWord(0x204), "Native execution must observe the physical NOR commit without a host cache flush or PC change");
            });
        }

        [Test]
        public void ShouldFetchForEitherCpuRegistrationOrder([Values(0, 1)] int core,
            [Values(false, true)] bool cpuBeforeWindow)
        {
            WithCaller(core, 0x10000000, (emulation, machine, memory, ssi, flash) =>
            {
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(7u, memory.ReadDoubleWord(0x204));
                ssi.WriteDoubleWord(8, 0);
                Assert.AreEqual(0u, machine.SystemBus.ReadDoubleWord(0x10000000));
                ssi.WriteDoubleWord(8, 1);
                memory.WriteDoubleWord(0x200, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(7u, memory.ReadDoubleWord(0x204));
            }, cpuBeforeWindow);
        }

        [TestCase(true)] [TestCase(false)]
        public void ShouldInvalidateBothCoresThatCachedTheSameWindow(bool program)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "xip_shared_cache");
            try
            {
                var memory = new MappedMemory(machine, 0x1000);
                machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
                var cpu0 = NewCpu(machine, 0);
                var cpu1 = NewCpu(machine, 1);
                var storage = new MappedMemory(machine, 0x200000);
                storage.WriteDoubleWord(0, 0x47702007);
                var flash = new WinbondW25QFlash(machine, storage);
                var ssi = new RP2040_SSI(machine);
                machine.SystemBus.Register(ssi, new BusRangeRegistration(Ssi, (ulong)ssi.Size));
                ssi.Register(flash, 0);
                var xip = new RP2040_XIP(machine, ssi, flash);
                var window = new RP2040_XIPMemory(machine, flash, xip);
                machine.SystemBus.Register(window, new BusRangeRegistration(0x10000000, 0x200000));
                Configure(ssi);
                var caller = new ushort[] { 0x681a, 0x2a00, 0xd0fc, 0x47a0, 0x6028, 0x2200, 0x601a, 0xe7f7 };
                for(var core = 0; core < 2; core++)
                {
                    var vectors = core * 0x80;
                    memory.WriteDoubleWord(vectors, Ram + 0xff0 - (uint)core * 0x10);
                    memory.WriteDoubleWord(vectors + 4, Ram + 0x101 + (uint)core * 0x80);
                    memory.WriteDoubleWord(vectors + 0xc, Ram + 0x321);
                    for(var i = 0; i < caller.Length; i++) memory.WriteWord(0x100 + core * 0x80 + i * 2, caller[i]);
                }
                memory.WriteWord(0x320, 0x2055);
                memory.WriteWord(0x322, 0x6028);
                memory.WriteWord(0x324, 0xbf30);
                memory.WriteWord(0x326, 0xe7fe);
                machine.PostCreationActions();
                var cpus = new[] { cpu0, cpu1 };
                for(var core = 0; core < 2; core++)
                {
                    var cpu = cpus[core];
                    cpu.InitVectorTableOffset = Ram + (uint)core * 0x80;
                    cpu.Reset();
                    cpu.SetRegister(3, Ram + 0x200 + (uint)core * 8);
                    cpu.SetRegister(4, 0x10000001);
                    cpu.SetRegister(5, Ram + 0x204 + (uint)core * 8);
                    memory.WriteDoubleWord(0x200 + core * 8, 1);
                }
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(7u, memory.ReadDoubleWord(0x204));
                Assert.AreEqual(7u, memory.ReadDoubleWord(0x20c));
                if(program)
                {
                    Command(flash, 6);
                    Command(flash, 2, 0, 0, 0, 3);
                    emulation.RunFor(TimeInterval.FromMicroseconds(400));
                    Assert.AreEqual(3, storage.ReadByte(0));
                }
                else ssi.WriteDoubleWord(8, 0);
                memory.WriteDoubleWord(0x200, 1);
                memory.WriteDoubleWord(0x208, 1);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(program ? 3u : 0x55u, memory.ReadDoubleWord(0x204));
                Assert.AreEqual(program ? 3u : 0x55u, memory.ReadDoubleWord(0x20c));
            }
            finally { emulation.RemoveMachine(machine); }
        }

        [TestCase(true)] [TestCase(false)]
        public void ShouldPublishAvailabilityWhileAnotherNativeCorePollsSsi(bool flashNotification)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "xip_peer_poll");
            using(var transitionEntered = new ManualResetEventSlim())
            using(var allowNotification = new ManualResetEventSlim())
            using(var peerAboutToRead = new ManualResetEventSlim())
            {
                Task execution = null;
                try
                {
                    var memory = new MappedMemory(machine, 0x1000);
                    machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
                    var storage = new MappedMemory(machine, 0x200000);
                    var flash = new WinbondW25QFlash(machine, storage);
                    var ssi = new RP2040_SSI(machine);
                    machine.SystemBus.Register(ssi, new BusRangeRegistration(Ssi, (ulong)ssi.Size));
                    ssi.Register(flash, 0);
                    var armed = 0;
                    Action gate = () =>
                    {
                        if(Interlocked.Exchange(ref armed, 0) == 0) return;
                        Console.WriteLine("Native availability transition entered");
                        transitionEntered.Set();
                        if(!allowNotification.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Availability fixture notification was not released");
                    };
                    // The gate precedes XIP's real subscriber. It holds the actual
                    // publisher thread while the native peer enters an SSI read.
                    if(flashNotification) flash.StateChanged += gate;
                    else ssi.ConfigurationChanged += gate;
                    var xip = new RP2040_XIP(machine, ssi, flash);
                    var window = new RP2040_XIPMemory(machine, flash, xip);
                    machine.SystemBus.Register(window, new BusRangeRegistration(0x10000000, 0x200000));
                    Configure(ssi);
                    var cpu0 = NewCpu(machine, 0);
                    var cpu1 = NewCpu(machine, 1);
                    memory.WriteDoubleWord(0, Ram + 0xff0);
                    memory.WriteDoubleWord(4, Ram + 0x101);
                    memory.WriteDoubleWord(0x80, Ram + 0xfe0);
                    memory.WriteDoubleWord(0x84, Ram + 0x181);
                    // The optional clocked serial bus master publishes an actual
                    // NOR busy transition through FinishTransmission. The other
                    // variant uses the production SSI enable register directly.
                    if(flashNotification)
                    {
                        Command(flash, 6);
                        machine.SystemBus.Register(new NativeFlashWriter(machine, flash), new BusRangeRegistration(0x40000000, 4));
                    }
                    memory.WriteWord(0x100, 0x6008); // str r0,[r1] (actual native bus write)
                    memory.WriteWord(0x102, 0x2201); // movs r2,#1
                    memory.WriteWord(0x104, 0x601a); // str r2,[r3]
                    memory.WriteWord(0x106, 0xbf30); // wfi
                    memory.WriteWord(0x108, 0xe7fe);
                    memory.WriteWord(0x180, 0x6808); // ldr r0,[r1] (SSI RXFLR)
                    memory.WriteWord(0x182, 0x3401); // adds r4,#1
                    memory.WriteWord(0x184, 0x601c); // str r4,[r3]
                    memory.WriteWord(0x186, 0xe7fb); // b polling loop
                    machine.PostCreationActions();
                    cpu0.InitVectorTableOffset = Ram;
                    cpu1.InitVectorTableOffset = Ram + 0x80;
                    cpu0.Reset(); cpu1.Reset();
                    cpu0.SetRegister(0, 0);
                    cpu0.SetRegister(1, Ssi + 8);
                    cpu0.SetRegister(3, Ram + 0x300);
                    cpu1.SetRegister(1, Ssi + 0x24);
                    cpu1.SetRegister(3, Ram + 0x304);
                    cpu1.SetRegister(4, 0);
                    var firstPeerRead = 1;
                    cpu1.AddHook(Ram + 0x180, (_, __) =>
                    {
                        if(Interlocked.Exchange(ref firstPeerRead, 0) == 0) return;
                        if(!transitionEntered.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Native publisher did not reach its transition");
                        Console.WriteLine("Native peer about to read SSI");
                        peerAboutToRead.Set();
                    });
                    if(flashNotification) cpu0.SetRegister(1, 0x40000000);
                    Volatile.Write(ref armed, 1);
                    execution = Task.Run(() => emulation.RunFor(TimeInterval.FromMicroseconds(100)));
                    Assert.IsTrue(transitionEntered.Wait(TimeSpan.FromSeconds(5)), "Native publisher did not reach availability transition");
                    Assert.IsTrue(peerAboutToRead.Wait(TimeSpan.FromSeconds(5)), "Native peer did not reach its actual SSI read");
                    Thread.Sleep(50); // Let the peer enter the shared bus/clock path.
                    allowNotification.Set();
                    Assert.IsTrue(execution.Wait(TimeSpan.FromSeconds(5)), "Availability publication deadlocked against native SSI polling");
                    Assert.AreEqual(1u, memory.ReadDoubleWord(0x300));
                    Assert.Greater(memory.ReadDoubleWord(0x304), 0u);
                }
                finally
                {
                    allowNotification.Set();
                    // The external isolated runner also bounds a model deadlock;
                    // ordinary assertion paths still release the fixture gate.
                    if(execution != null && !execution.IsCompleted) execution.Wait(TimeSpan.FromSeconds(1));
                    emulation.RemoveMachine(machine);
                }
            }
        }

        private sealed class NativeFlashWriter : IDoubleWordPeripheral
        {
            public NativeFlashWriter(Machine machine, WinbondW25QFlash flash)
            {
                this.machine = machine;
                this.flash = flash;
            }
            public uint ReadDoubleWord(long offset) => 0;
            public void WriteDoubleWord(long offset, uint value) => machine.ClockSource.ExecuteInLock(() =>
                Command(flash, 2, 0, 0, 0, 0));
            public void Reset() { }
            private readonly Machine machine;
            private readonly WinbondW25QFlash flash;
        }

        private static void WithCaller(int core, uint alias,
            Action<Emulation, Machine, MappedMemory, RP2040_SSI, WinbondW25QFlash> check, bool cpuBeforeWindow = false)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "xip_native_fetch");
            try
            {
                var memory = new MappedMemory(machine, 0x1000);
                machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
                var storage = new MappedMemory(machine, 0x200000);
                storage.WriteDoubleWord(0, 0x47702007); // movs r0,#7; bx lr
                var flash = new WinbondW25QFlash(machine, storage);
                var ssi = new RP2040_SSI(machine);
                machine.SystemBus.Register(ssi, new BusRangeRegistration(Ssi, (ulong)ssi.Size));
                ssi.Register(flash, 0);
                var xip = new RP2040_XIP(machine, ssi, flash);
                var cpu = cpuBeforeWindow ? NewCpu(machine, core) : null;
                var window = new RP2040_XIPMemory(machine, flash, xip);
                machine.SystemBus.Register(window, new BusRangeRegistration(alias, 0x200000));
                if(cpu == null) cpu = NewCpu(machine, core);
                Configure(ssi);
                memory.WriteDoubleWord(0, Ram + 0xff0);
                memory.WriteDoubleWord(4, Ram + 0x101);
                memory.WriteDoubleWord(0xc, Ram + 0x321);
                var program = new ushort[] { 0x681a, 0x2a00, 0xd0fc, 0x47a0, 0x6028, 0x2200, 0x601a, 0xe7f7 };
                for(var i = 0; i < program.Length; i++) memory.WriteWord(0x100 + i * 2, program[i]);
                memory.WriteWord(0x320, 0x2055); // movs r0,#0x55
                memory.WriteWord(0x322, 0x6028); // str r0,[r5]
                memory.WriteWord(0x324, 0xbf30); // wfi
                memory.WriteWord(0x326, 0xe7fe);
                machine.PostCreationActions();
                cpu.InitVectorTableOffset = Ram;
                cpu.Reset();
                cpu.SetRegister(3, Ram + 0x200);
                cpu.SetRegister(4, alias | 1);
                cpu.SetRegister(5, Ram + 0x204);
                check(emulation, machine, memory, ssi, flash);
            }
            finally { emulation.RemoveMachine(machine); }
        }

        private static CortexM NewCpu(Machine machine, int core)
        {
            var nvic = new NVIC(machine);
            var cpu = new CortexM("cortex-m0+", machine, nvic, cpuId: (uint)core);
            machine.SystemBus.Register(cpu, new CPURegistrationPoint(core));
            machine.SetLocalName(cpu, "cpu" + core);
            machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: cpu));
            nvic.IRQ.Connect(cpu, 0);
            cpu.PerformanceInMips = 100;
            return cpu;
        }

        private static void Configure(RP2040_SSI ssi)
        {
            ssi.WriteDoubleWord(8, 0);
            ssi.WriteDoubleWord(0, 0x001f0300);
            ssi.WriteDoubleWord(0x14, 2);
            ssi.WriteDoubleWord(0xf4, 0x03000218);
            ssi.WriteDoubleWord(0x10, 1);
            ssi.WriteDoubleWord(8, 1);
        }

        private static void Command(WinbondW25QFlash flash, params byte[] values)
        {
            foreach(var value in values) flash.Transmit(value);
            flash.FinishTransmission();
        }

        private const uint Ram = 0x20000000, Ssi = 0x18000000;
    }
}
