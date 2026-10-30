//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_SIONarrowAccessTests
    {
        [Test]
        public void ShouldReadNativeCoreId([Values(0, 1, 2)] int view,
            [Values(0, 1)] int core, [Values(1, -1, 2, 4)] int width)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            Run(rp2350, mirror, core, 0, width, null, result => Assert.AreEqual((uint)core, result.ReadDoubleWord(0)));
        }

        [Test]
        public void ShouldSelectReadByteLanes([Values(0, 1, 2)] int view,
            [Values(0, 1, 2, 3)] int lane)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            Run(rp2350, mirror, 1, 0x10 + lane, 1,
                fixture => fixture.Write(0x10, 0x25b6c7d8),
                result => Assert.AreEqual((0x25b6c7d8u >> (lane * 8)) & 0xff, result.ReadDoubleWord(0)));
        }

        [Test]
        public void ShouldSelectReadHalfwordLanes([Values(0, 1, 2)] int view,
            [Values(0, 2)] int lane)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            Run(rp2350, mirror, 1, 0x10 + lane, 2,
                fixture => fixture.Write(0x10, 0x25b6c7d8),
                result => Assert.AreEqual((0x25b6c7d8u >> (lane * 8)) & 0xffff, result.ReadDoubleWord(0)));
        }

        [Test]
        public void ShouldPopOneFifoWordEvenWhenSelectingAnUpperLane([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            var lane = width == 1 ? 3 : 2;
            Run(rp2350, mirror, 1, 0x58 + lane, width,
                fixture => { fixture.Write(0x54, 0xa1b2c3d4); fixture.Write(0x54, 0x12345678); },
                result =>
                {
                    Assert.AreEqual(width == 1 ? 0xa1u : 0xa1b2u, result.ReadDoubleWord(0));
                    Assert.AreEqual(0x12345678u, result.ReadDoubleWord(4), "A narrow FIFO read must pop exactly one whole word");
                    Assert.AreEqual(2u, result.ReadDoubleWord(8), "FIFO must be empty without an underflow");
                }, fifoTail: true);
        }

        [Test]
        public void ShouldClaimSpinlockWhenSelectedLaneIsZero([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            Run(rp2350, mirror, 1, width == 1 ? 0x103 : 0x102, width, null,
                result =>
                {
                    Assert.AreEqual(0u, result.ReadDoubleWord(0));
                    Assert.AreEqual(0u, result.ReadDoubleWord(4), "Upper-lane read must claim the full spinlock");
                }, secondRead: 0x100, after: fixture => Assert.AreEqual(0u, fixture.Read(0x100), "The other core must observe the claimed spinlock"));
        }

        [Test]
        public void ShouldReplicateNarrowWritesWithoutReadingTheRegister([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width, [Values(0, 2)] int lane)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            var value = width == 1 ? 0x96u : 0x96abu;
            var expected = (width == 1 ? 0x96969696u : 0x96ab96abu) & (rp2350 ? 0xffffffff : 0x3fffffff);
            Run(rp2350, mirror, 1, 0x10 + lane, width,
                fixture => { fixture.Write(0x10, 0x12345678); fixture.CPU.SetRegister(0, value); },
                result => Assert.AreEqual(expected, result.ReadDoubleWord(0)), write: true);
        }

        [Test]
        public void ShouldNotReadFifoOnIgnoredNarrowWrite([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width)
        {
            var rp2350 = view != 0;
            var mirror = view == 2;
            Run(rp2350, mirror, 1, 0x58, width,
                fixture => { fixture.Write(0x54, 0xa1b2c3d4); fixture.CPU.SetRegister(0, 0); },
                result => Assert.AreEqual(0xa1b2c3d4u, result.ReadDoubleWord(0)), write: true);
        }

        [Test]
        public void ShouldSignExtendNativeNarrowReads([Values(0, 1, 2)] int view,
            [Values(-1, -2)] int width)
        {
            Run(view != 0, view == 2, 1, 0x10, width,
                fixture => fixture.Write(0x10, 0x25b6c7d8),
                result => Assert.AreEqual(width == -1 ? 0xffffffd8u : 0xffffc7d8u, result.ReadDoubleWord(0)));
        }

        [Test]
        public void ShouldReplicateWritesToAtomicGpioSetAlias([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width)
        {
            var value = width == 1 ? 0x96u : 0x96abu;
            var repeated = width == 1 ? 0x96969696u : 0x96ab96abu;
            var expected = (0x12345678u | repeated) & (view != 0 ? uint.MaxValue : 0x3fffffff);
            Run(view != 0, view == 2, 1, (view == 0 ? 0x14 : 0x18) + 2, width,
                fixture => { fixture.Write(0x10, 0x12345678); fixture.CPU.SetRegister(0, value); },
                result => Assert.AreEqual(expected, result.ReadDoubleWord(0)), write: true, secondRead: 0x10);
        }

        [Test]
        public void ShouldSendOneReplicatedFifoWord([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width)
        {
            var value = width == 1 ? 0x96u : 0x96abu;
            Run(view != 0, view == 2, 1, 0x56, width,
                fixture => fixture.CPU.SetRegister(0, value), result => { }, write: true,
                after: fixture =>
                {
                    Assert.AreEqual(3u, fixture.Read(0x50));
                    Assert.AreEqual(width == 1 ? 0x96969696u : 0x96ab96abu, fixture.Read(0x58));
                    Assert.AreEqual(2u, fixture.Read(0x50));
                });
        }

        [Test]
        public void ShouldReleaseSpinlockWithAnyNarrowWrite([Values(0, 1, 2)] int view,
            [Values(1, 2)] int width)
        {
            Run(view != 0, view == 2, 1, width == 1 ? 0x103 : 0x102, width,
                fixture => { Assert.AreEqual(1u, fixture.Read(0x100)); fixture.CPU.SetRegister(0, 0); },
                result => Assert.AreEqual(1u, result.ReadDoubleWord(0)), write: true);
        }

        [TestCase(1)] [TestCase(2)]
        public void ShouldPreserveDividerDirtyOnNarrowRemainderRead(int width)
        {
            Run(false, false, 0, 0x74, width,
                fixture => { fixture.Write(0x60, 100); fixture.Write(0x64, 3); },
                result =>
                {
                    Assert.AreEqual(1u, result.ReadDoubleWord(0));
                    Assert.AreEqual(3u, result.ReadDoubleWord(4), "Remainder read preserves DIRTY");
                }, secondRead: 0x78);
        }

        [TestCase(1)] [TestCase(2)]
        public void ShouldClearDividerDirtyOnOneNarrowQuotientRead(int width)
        {
            Run(false, false, 0, width == 1 ? 0x73 : 0x72, width,
                fixture => { fixture.Write(0x60, 0x12345678); fixture.Write(0x64, 1); },
                result =>
                {
                    Assert.AreEqual(width == 1 ? 0x12u : 0x1234u, result.ReadDoubleWord(0));
                    Assert.AreEqual(1u, result.ReadDoubleWord(4), "Narrow quotient read clears DIRTY and preserves READY");
                }, secondRead: 0x78);
        }

        [TestCase(1)] [TestCase(2)]
        public void ShouldKeepNonSecureFifoSeparateAndRejectNonSecureMirror(int width)
        {
            Run(true, false, 1, 0x58, 4,
                fixture =>
                {
                    fixture.Write(0x54, 0x12345678);
                    fixture.WriteNonSecure(0x54, 0xa1b2c3d4);
                }, result => Assert.AreEqual(0x12345678u, result.ReadDoubleWord(0)),
                after: fixture =>
                {
                    var secure = (ulong)new CortexM.ContextState { Privileged = true, CpuSecure = true, AttributionSecure = true, DebugAccess = true };
                    var nonSecure = (ulong)new CortexM.ContextState { Privileged = true, CpuSecure = false, AttributionSecure = false, DebugAccess = true };
                    if(width == 1)
                    {
                        Assert.Throws<BusAccessException>(() => fixture.Bus.ReadByte(SioNS + 0x5b, fixture.CPU, nonSecure));
                        Assert.Throws<BusAccessException>(() => fixture.Bus.WriteByte(SioNS + 0x10, 0xff, fixture.CPU, nonSecure));
                        Assert.AreEqual(0xa1, fixture.Bus.ReadByte(SioNS + 0x5b, fixture.CPU, secure));
                    }
                    else
                    {
                        Assert.Throws<BusAccessException>(() => fixture.Bus.ReadWord(SioNS + 0x5a, fixture.CPU, nonSecure));
                        Assert.Throws<BusAccessException>(() => fixture.Bus.WriteWord(SioNS + 0x10, 0xffff, fixture.CPU, nonSecure));
                        Assert.AreEqual(0xa1b2, fixture.Bus.ReadWord(SioNS + 0x5a, fixture.CPU, secure));
                    }
                    Assert.AreEqual(2u, fixture.Bus.ReadDoubleWord(Sio + 0x50, fixture.CPU, secure));
                    Assert.AreEqual(2u, fixture.Bus.ReadDoubleWord(Sio + 0x50, fixture.CPU, nonSecure));
                });
        }

        [TestCase(1)] [TestCase(2)]
        public void ShouldApplyNonSecureGpioMaskOnNarrowReadsAndWrites(int width)
        {
            Run(true, false, 1, 0, 4,
                fixture => fixture.Access.WriteDoubleWord(0xc, 0x100),
                result => Assert.AreEqual(1u, result.ReadDoubleWord(0)),
                after: fixture =>
                {
                    var secure = (ulong)new CortexM.ContextState { Privileged = true, CpuSecure = true, AttributionSecure = true, DebugAccess = true };
                    var nonSecure = (ulong)new CortexM.ContextState { Privileged = true, CpuSecure = false, AttributionSecure = false, DebugAccess = true };
                    fixture.Write(0x10, 0x25b6c7d8);
                    if(width == 1)
                    {
                        Assert.AreEqual(1, fixture.Bus.ReadByte(SioNS + 0x11, fixture.CPU, secure));
                        Assert.AreEqual(1, fixture.Bus.ReadByte(Sio + 0x11, fixture.CPU, nonSecure));
                        fixture.Bus.WriteByte(SioNS + 0x11, 0, fixture.CPU, secure);
                        Assert.AreEqual(0x25b6c6d8u, fixture.Read(0x10));
                        fixture.Bus.WriteByte(Sio + 0x11, 0xff, fixture.CPU, nonSecure);
                    }
                    else
                    {
                        Assert.AreEqual(0x100, fixture.Bus.ReadWord(SioNS + 0x10, fixture.CPU, secure));
                        Assert.AreEqual(0x100, fixture.Bus.ReadWord(Sio + 0x10, fixture.CPU, nonSecure));
                        fixture.Bus.WriteWord(SioNS + 0x10, 0, fixture.CPU, secure);
                        Assert.AreEqual(0x25b6c6d8u, fixture.Read(0x10));
                        fixture.Bus.WriteWord(Sio + 0x10, 0xffff, fixture.CPU, nonSecure);
                    }
                    Assert.AreEqual(0x25b6c7d8u, fixture.Read(0x10));
                });
        }

        private static void Run(bool rp2350, bool mirror, int core, int offset, int width,
            Action<Fixture> prepare, Action<ResultMemory> check, bool write = false,
            bool fifoTail = false, int? secondRead = null, Action<Fixture> after = null)
        {
            var emulation = EmulationManager.Instance.CurrentEmulation;
            var machine = new Machine(createLocalTimeSource: true);
            emulation.AddMachine(machine, "sio_narrow");
            try
            {
                var fixture = new Fixture(machine, rp2350, mirror, core);
                var memory = fixture.Memory;
                memory.WriteDoubleWord(0, Ram + 0xff0);
                memory.WriteDoubleWord(4, Ram + 0x101);
                var program = new List<ushort>();
                if(!rp2350 && offset >= 0x70 && offset <= 0x78)
                {
                    // The divider takes eight 125 MHz clocks. Sixteen native
                    // instructions at 100 MIPS let READY settle. End the native
                    // block so SyncTime reports those retired instructions.
                    for(var i = 0; i < 16; i++) program.Add(0xbf00);
                    program.Add(0xe000); // b over the following nop
                    program.Add(0xbf00);
                }
                if(write)
                {
                    program.Add(width == 1 ? (ushort)0x7008 : (ushort)0x8008); // strb/strh r0, [r1]
                    program.Add(0x6820); // ldr r0, [r4] (full register)
                }
                else program.Add(width == 1 ? (ushort)0x7808 : width == -1 ? (ushort)0x5688
                    : width == 2 ? (ushort)0x8808 : width == -2 ? (ushort)0x5e88 : (ushort)0x6808); // ldrb/ldrsb/ldrh/ldr r0, [r1(,r2)]
                program.Add(0x6018); // str r0, [r3]
                if(fifoTail || secondRead.HasValue)
                {
                    program.Add(0x6820); // ldr r0, [r4]
                    program.Add(0x6058); // str r0, [r3, #4]
                }
                if(fifoTail)
                {
                    program.Add(0x6828); // ldr r0, [r5] (FIFO status)
                    program.Add(0x6098); // str r0, [r3, #8]
                }
                program.Add(0x2001); // movs r0, #1
                program.Add(0x60d8); // str r0, [r3, #12] (execution marker)
                program.Add(0xbf30); // wfi
                program.Add(0xe7fe); // b .
                for(var i = 0; i < program.Count; i++) memory.WriteWord(0x100 + 2 * i, program[i]);
                fixture.CPU.InitVectorTableOffset = Ram;
                fixture.CPU.Reset();
                fixture.CPU.SetRegister(1, fixture.Address + (uint)offset);
                fixture.CPU.SetRegister(2, 0);
                fixture.CPU.SetRegister(3, Ram + 0x300);
                fixture.CPU.SetRegister(4, fixture.Address + (uint)(secondRead ?? (offset & ~3)));
                fixture.CPU.SetRegister(5, fixture.Address + 0x50);
                prepare?.Invoke(fixture);
                emulation.RunFor(TimeInterval.FromMicroseconds(100));
                Assert.AreEqual(1u, memory.ReadDoubleWord(0x30c), $"Native sequence did not complete; PC={fixture.CPU.PC}");
                // Present just the result region to the checks.
                check(new ResultMemory(memory));
                after?.Invoke(fixture);
            }
            finally { emulation.RemoveMachine(machine); }
        }

        private sealed class Fixture
        {
            public Fixture(Machine machine, bool rp2350, bool mirror, int core)
            {
                Bus = machine.SystemBus;
                var nvic = new NVIC(machine);
                CPU = new CortexM(rp2350 ? "cortex-m33-rp2350" : "cortex-m0+", machine, nvic,
                    cpuId: (uint)core, enableTrustZone: rp2350);
                machine.SystemBus.Register(CPU, new CPURegistrationPoint(core));
                machine.SetLocalName(CPU, "cpu");
                machine.SystemBus.Register(nvic, new BusPointRegistration(0xe000e000, cpu: CPU));
                nvic.IRQ.Connect(CPU, 0);
                Memory = new MappedMemory(machine, 0x1000);
                machine.SystemBus.Register(Memory, new BusRangeRegistration(Ram, 0x1000));
                if(rp2350)
                {
                    Access = new RP2350_AccessControl(machine);
                    Access.WriteDoubleWord(0xc, uint.MaxValue);
                    sio2350 = new RP2350_SIO(machine, Access);
                    machine.SystemBus.Register(sio2350, new BusRangeRegistration(Sio, 0x1000));
                    machine.SystemBus.Register(sio2350, new BusMultiRegistration(SioNS, 0x1000, "NonSecure"));
                }
                else
                {
                    sio2040 = new RP2040_SIO(machine);
                    machine.SystemBus.Register(sio2040, new BusRangeRegistration(Sio, 0x1000));
                }
                this.mirror = mirror;
                Address = mirror ? SioNS : Sio;
                CPU.PerformanceInMips = 100;
                machine.PostCreationActions();
            }

            public void WriteNonSecure(long offset, uint value) => sio2350.WriteDoubleWordNonSecure(offset, value);

            public uint Read(long offset)
            {
                if(sio2040 != null) return sio2040.ReadDoubleWord(offset);
                return mirror ? sio2350.ReadDoubleWordNonSecure(offset) : sio2350.ReadDoubleWord(offset);
            }

            public void Write(long offset, uint value)
            {
                // A debugger without CPU context accesses core0's FIFO bank.
                if(sio2040 != null) sio2040.WriteDoubleWord(offset, value);
                else if(mirror) sio2350.WriteDoubleWordNonSecure(offset, value);
                else sio2350.WriteDoubleWord(offset, value);
            }

            public IBusController Bus { get; }
            public RP2350_AccessControl Access { get; }
            public CortexM CPU { get; }
            public MappedMemory Memory { get; }
            public uint Address { get; }
            private readonly bool mirror;
            private readonly RP2040_SIO sio2040;
            private readonly RP2350_SIO sio2350;
        }

        private sealed class ResultMemory
        {
            public ResultMemory(MappedMemory source) { this.source = source; }
            public uint ReadDoubleWord(long offset) => source.ReadDoubleWord(0x300 + offset);
            private readonly MappedMemory source;
        }

        private const uint Ram = 0x20000000, Sio = 0xd0000000, SioNS = 0xd0020000;
    }
}
