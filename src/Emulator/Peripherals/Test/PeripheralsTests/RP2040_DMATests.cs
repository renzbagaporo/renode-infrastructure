//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_DMATests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 0x1000);
            dma = new RP2040_DMA(machine, frequency: 1000000);
            machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
            machine.SystemBus.Register(dma, new BusRangeRegistration(Dma, 0x4000));
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void ShouldTransferActualBusDataAtRequestedWidth(int size)
        {
            var peripheral = new WidthPeripheral(dma);
            machine.SystemBus.Register(peripheral, new BusRangeRegistration(Device, 0x100));
            Configure(0, Device, Ram, 2, Control(0, size, 63, false, true));
            Assert.AreEqual(0u, memory.ReadDoubleWord(0));
            Advance(1);
            Assert.AreEqual(1, peripheral.Reads.Count);
            Assert.AreEqual(0u, memory.ReadDoubleWord(0));
            Assert.AreEqual(2u, dma.ReadDoubleWord(8));
            Advance(3);
            CollectionAssert.AreEqual(new[] { 1 << size, 1 << size }, peripheral.Reads);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
            Assert.AreEqual(0u, dma.ReadDoubleWord(12) & Busy);
            Assert.AreEqual(1u, dma.ReadDoubleWord(0x400));
            Assert.AreEqual((byte)0x44, memory.ReadByte(0));
            Assert.AreEqual((byte)0x44, memory.ReadByte(1 << size));
        }

        [Test]
        public void ShouldHonorDREQWithoutSynchronousTransferOrOverflowingPIO()
        {
            var pio = CreatePIO();
            for(uint i = 0; i < 8; i++) memory.WriteDoubleWord(i * 4, 0x10203040 + i);
            Configure(0, Ram, Pio + 0x10, 8, Control(0, 2, 0, true, false));
            Assert.AreEqual(0u, pio.ReadDoubleWord(12));
            Advance(100);
            Assert.AreEqual(4u, dma.ReadDoubleWord(8));
            Assert.AreEqual(4u, pio.ReadDoubleWord(12));
            Assert.AreEqual(0u, pio.ReadDoubleWord(8));
            for(uint i = 0; i < 8; i++)
            {
                Force(pio, 0x80a0); // pull block
                Force(pio, 0xa0c7); // mov isr, osr
                Force(pio, 0x8020); // push block
                Assert.AreEqual(0x10203040 + i, pio.ReadDoubleWord(0x20));
                Advance(3);
            }
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
            Assert.AreEqual(0u, pio.ReadDoubleWord(8));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void ShouldReadPIODataOnlyWhenAvailableAndSelectCorrectPIOBlock(int pioId)
        {
            var pio = CreatePIO(pioId);
            Configure(1, Pio + 0x20, Ram, 8, Control(1, 2, pioId * 8 + 4, false, true));
            Advance(50);
            Assert.AreEqual(8u, dma.ReadDoubleWord(0x48));
            for(uint i = 0; i < 8; i++)
            {
                Force(pio, (ushort)(0xe020 | i));
                Force(pio, 0xa0c1); // mov isr, x
                Force(pio, 0x8020);
                Advance(2);
            }
            for(uint i = 0; i < 8; i++) Assert.AreEqual(i, memory.ReadDoubleWord(4 * i));
            Assert.AreEqual(0u, pio.ReadDoubleWord(8));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x48));
        }

        [TestCase(0x0c, 3)]
        [TestCase(0x1c, 2)]
        [TestCase(0x2c, 1)]
        [TestCase(0x3c, 0)]
        public void ShouldImplementAllTriggerAliases(int trigger, int kind)
        {
            memory.WriteDoubleWord(0, 0xab1234ef);
            var control = Control(0, 2, 63, true, true);
            dma.WriteDoubleWord(0x10, control);
            dma.WriteDoubleWord(0x14, Ram);
            dma.WriteDoubleWord(0x18, Ram + 0x100);
            dma.WriteDoubleWord(8, 1);
            Assert.AreEqual(control, dma.ReadDoubleWord(0x20));
            Assert.AreEqual(Ram, dma.ReadDoubleWord(0x28));
            Assert.AreEqual(Ram + 0x100, dma.ReadDoubleWord(0x34));
            var values = new[] { Ram, Ram + 0x100, 1u, control };
            dma.WriteDoubleWord(trigger, values[kind]);
            Advance(2);
            Assert.AreEqual(0xab1234efu, memory.ReadDoubleWord(0x100));
        }

        [Test]
        public void ShouldKeepReloadSeparateFromLiveCountAndIgnoreBusyTriggers()
        {
            Configure(0, Ram, Ram + 0x100, 3, Control(0, 2, 63, true, true));
            Advance(2);
            dma.WriteDoubleWord(8, 5);
            Assert.AreEqual(2u, dma.ReadDoubleWord(8));
            Assert.AreEqual(5u, dma.ReadDoubleWord(0x804));
            dma.WriteDoubleWord(0x430, 1);
            Assert.AreEqual(2u, dma.ReadDoubleWord(8));
            Advance(4);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
            dma.WriteDoubleWord(0x430, 1);
            Assert.AreEqual(5u, dma.ReadDoubleWord(8));
            Advance(10);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldDrainAcceptedReadWhenPausedAndKeepBusyUntilResumed()
        {
            memory.WriteDoubleWord(0, 0xa5);
            memory.WriteDoubleWord(4, 0x5a);
            Configure(0, Ram, Ram + 0x100, 2, Control(0, 2, 63, true, true));
            Advance(1);
            dma.WriteDoubleWord(0x3010, 1); // clear EN without triggering
            Advance(20);
            Assert.AreEqual(0xa5u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(0u, memory.ReadDoubleWord(0x104));
            Assert.AreEqual(1u, dma.ReadDoubleWord(8));
            Assert.AreEqual(Busy, dma.ReadDoubleWord(12) & Busy);
            dma.WriteDoubleWord(0x2010, 1);
            Advance(2);
            Assert.AreEqual(0x5au, memory.ReadDoubleWord(0x104));
            Assert.AreEqual(0u, dma.ReadDoubleWord(12) & Busy);
        }

        [Test]
        public void ShouldCancelPendingDataAndNeverPublishStaleCompletionAfterReset()
        {
            memory.WriteDoubleWord(0, 0xaabbccdd);
            Configure(0, Ram, Ram + 0x100, 2, Control(0, 2, 63, true, true));
            Advance(1);
            Assert.AreEqual(Ram + 4, dma.ReadDoubleWord(0));
            dma.Reset();
            Advance(100);
            Assert.AreEqual(0u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x400));
            Assert.AreEqual(0u, dma.ReadDoubleWord(12) & Busy);
            Configure(0, Ram, Ram + 0x104, 1, Control(0, 2, 63, true, true));
            Advance(2);
            Assert.AreEqual(0xaabbccddu, memory.ReadDoubleWord(0x104));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldAbortNewReadsAndDrainAcceptedDataBeforeReportingQuiescence(bool accepted)
        {
            memory.WriteDoubleWord(0, 0xaabbccdd);
            Configure(0, Ram, Ram + 0x100, 2, Control(0, 2, 63, true, true));
            if(accepted) Advance(1);
            dma.WriteDoubleWord(0x444, 1);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
            Assert.AreEqual(accepted ? Busy : 0u, dma.ReadDoubleWord(12) & Busy);
            Assert.AreEqual(accepted ? 1u : 0u, dma.ReadDoubleWord(0x444));
            Advance(1);
            Assert.AreEqual(accepted ? 0xaabbccddu : 0u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x444));
            Assert.AreEqual(0u, dma.ReadDoubleWord(12) & Busy);
            var afterQuiescence = memory.ReadDoubleWord(0x104);
            Advance(100);
            Assert.AreEqual(afterQuiescence, memory.ReadDoubleWord(0x104));
            Assert.AreEqual(Ram + (accepted ? 4u : 0), dma.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldUseReadAndWriteRingAndBSwapPerTransferWidth()
        {
            memory.WriteDoubleWord(0, 0x01020304);
            memory.WriteDoubleWord(4, 0x11223344);
            Configure(0, Ram, Ram + 0x100, 4, Control(0, 2, 63, true, true) | (3u << 6) | (1u << 22));
            Advance(8);
            CollectionAssert.AreEqual(new uint[] { 0x04030201, 0x44332211, 0x04030201, 0x44332211 },
                Enumerable.Range(0, 4).Select(i => memory.ReadDoubleWord(0x100 + 4 * i)));
            Configure(0, Ram, Ram + 0x200, 4, Control(0, 1, 63, true, true) | (2u << 6) | (1u << 10) | (1u << 22));
            Advance(8);
            Assert.AreEqual(0x22114433u, memory.ReadDoubleWord(0x200));
            Assert.AreEqual(Ram + 0x200, dma.ReadDoubleWord(4));
        }

        [Test]
        public void ShouldRouteRawMaskedForcedAndQuietInterruptsIndependently()
        {
            dma.WriteDoubleWord(0x404, 1);
            dma.WriteDoubleWord(0x414, 1);
            Configure(0, Ram, Ram + 0x100, 1, Control(0, 0, 63, true, true));
            Advance(2);
            Assert.IsTrue(dma.IRQ0.IsSet);
            Assert.IsTrue(dma.IRQ1.IsSet);
            dma.WriteDoubleWord(0x41c, 1);
            Assert.IsFalse(dma.IRQ0.IsSet);
            Assert.IsFalse(dma.IRQ1.IsSet);
            dma.WriteDoubleWord(0x408, 2);
            Assert.IsTrue(dma.IRQ0.IsSet);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x400));
            Assert.AreEqual(2u, dma.ReadDoubleWord(0x40c));
            dma.WriteDoubleWord(0x40c, 2); // raw clear doesn't clear force
            Assert.IsTrue(dma.IRQ0.IsSet);
            dma.WriteDoubleWord(0x3000 + 0x408, 2);
            Assert.IsFalse(dma.IRQ0.IsSet);
            Configure(0, Ram, Ram + 0x100, 1, Control(0, 0, 63, true, true) | Quiet);
            Advance(2);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x400));
            dma.WriteDoubleWord(0x3c, 0);
            Assert.IsTrue(dma.IRQ0.IsSet);
            Assert.IsTrue(dma.IRQ1.IsSet);
        }

        [Test]
        public void ShouldChainChannelsAndExecuteUnmodifiedRegisterLoaderPattern()
        {
            // spi_target.c: RX writes a descriptor address to loader AL3_READ_ADDR_TRIG;
            // loader writes count + source to TX AL3_TRANS_COUNT + AL3_READ_ADDR_TRIG.
            var pio = CreatePIO(1);
            memory.WriteDoubleWord(0, 0xa6);
            memory.WriteDoubleWord(0x200, 1);
            memory.WriteDoubleWord(0x204, Ram);
            Configure(0, Ram, Pio + 0x13, 264, Control(0, 0, 8, true, false), false);
            Configure(2, Ram + 0x200, Dma + 0x38, 2, Control(2, 2, 63, true, true), false);
            Configure(1, Pio + 0x20, Dma + 2 * 0x40 + 0x3c, 1, Control(1, 2, 12, false, false));
            pio.WriteDoubleWord(0x10, Ram + 0x200);
            Force(pio, 0x80a0);
            Force(pio, 0xa0c7);
            Force(pio, 0x8020);
            Advance(20);
            Assert.AreEqual(1u, dma.ReadDoubleWord(0x804));
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
            Assert.AreEqual(1u, pio.ReadDoubleWord(12) & 15);
            Force(pio, 0x80a0);
            Force(pio, 0xa0c7);
            Force(pio, 0x8020);
            Assert.AreEqual(0xa6a6a6a6u, pio.ReadDoubleWord(0x20));
            Assert.AreEqual(0u, pio.ReadDoubleWord(8));
            Assert.AreEqual(7u, dma.ReadDoubleWord(0x400));
            // A completion chain starts the destination only if enabled and idle.
            dma.Reset();
            Configure(1, Ram, Ram + 0x300, 1, Control(1, 2, 63, true, true), false);
            Configure(0, Ram, Ram + 0x304, 1, (Control(0, 2, 63, true, true) & ~(15u << 11)) | (1u << 11));
            Advance(4);
            Assert.AreEqual(0xa6u, memory.ReadDoubleWord(0x300));
            Assert.AreEqual(3u, dma.ReadDoubleWord(0x400));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldReportReadAndWriteErrorsEvenInQuietMode(bool writeError)
        {
            Configure(0, writeError ? Ram : 0xf0000000, writeError ? 0xf0000000 : Ram, 1,
                Control(0, 2, 63, true, true) | Quiet);
            Advance(4);
            var control = dma.ReadDoubleWord(12);
            Assert.AreEqual(0x80000000u | (writeError ? 1u << 29 : 1u << 30), control & 0xe0000000);
            Assert.AreEqual(0u, control & Busy);
            Assert.AreEqual(1u, dma.ReadDoubleWord(0x400));
            dma.WriteDoubleWord(0x2010, 0x60000000);
            Assert.AreEqual(0u, dma.ReadDoubleWord(12) & 0xe0000000);
        }

        [Test]
        public void ShouldPaceUsingFractionalSystemClockTimer()
        {
            dma.WriteDoubleWord(0x420, (2u << 16) | 5);
            Configure(0, Ram, Ram + 0x100, 4, Control(0, 0, 59, true, true));
            Advance(2);
            Assert.AreEqual(Ram, dma.ReadDoubleWord(0));
            Advance(1);
            Assert.AreEqual(Ram + 1, dma.ReadDoubleWord(0));
            Advance(1);
            Assert.AreEqual(3u, dma.ReadDoubleWord(8));
            Advance(7);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
            Assert.AreEqual(4u, dma.ReadDoubleWord(0x804));
        }

        [Test]
        public void ShouldArbitrateHighPriorityRoundThenOneLowPriorityWithoutStarvation()
        {
            var reads = new List<uint>();
            var peripheral = new WidthPeripheral(dma) { OnRead = offset => reads.Add((uint)offset) };
            machine.SystemBus.Register(peripheral, new BusRangeRegistration(Device, 0x100));
            for(var channel = 0; channel < 3; channel++)
                Configure(channel, Device + (uint)(channel * 4), Ram + (uint)(channel * 0x10), 3,
                    Control(channel, 2, 63, false, false) | (channel < 2 ? 2u : 0), false);
            dma.WriteDoubleWord(0x430, 7);
            Advance(18);
            CollectionAssert.AreEqual(new uint[] { 0, 4, 8, 0, 4, 8, 0, 4, 8 }, reads);
        }

        [TestCase(15, 0x46u)]
        [TestCase(14, 1u)]
        public void ShouldSniffTransferredData(int calculation, uint expected)
        {
            memory.WriteDoubleWord(0, 0x12);
            memory.WriteDoubleWord(4, 0x34);
            dma.WriteDoubleWord(0x434, 1u | ((uint)calculation << 5));
            Configure(0, Ram, Ram + 0x100, 2, Control(0, 2, 63, true, true) | (1u << 23));
            Advance(4);
            Assert.AreEqual(expected, dma.ReadDoubleWord(0x438));
        }

        [TestCase(1, 0xffffffffu, 0xc00u, 0xcbf43926u)]
        [TestCase(2, 0xffffu, 0u, 0x29b1u)]
        public void ShouldMatchStandardCRCCheckVectors(int calculation, uint seed, uint outputControl, uint expected)
        {
            // ASCII "123456789": CRC-32/ISO-HDLC uses reflected input/output,
            // all-one seed and inverted output; CRC-16/IBM-3740 is unreflected.
            for(var i = 0; i < 9; i++) memory.WriteByte(i, (byte)('1' + i));
            dma.WriteDoubleWord(0x438, seed);
            dma.WriteDoubleWord(0x434, 1u | ((uint)calculation << 5) | outputControl);
            Configure(0, Ram, Ram + 0x100, 9, Control(0, 0, 63, true, true) | (1u << 23));
            Advance(18);
            Assert.AreEqual(expected, dma.ReadDoubleWord(0x438));
        }

        [Test]
        public void ShouldCancelChannelAndSnifferByteSwapsAndApplyReadOnlyOutputTransforms()
        {
            memory.WriteDoubleWord(0, 0x12345678);
            dma.WriteDoubleWord(0x434, 1u | (15u << 5) | (1u << 9));
            Configure(0, Ram, Ram + 0x100, 1, Control(0, 2, 63, true, true) | (1u << 23) | (1u << 22));
            Advance(2);
            Assert.AreEqual(0x78563412u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(0x12345678u, dma.ReadDoubleWord(0x438));
            dma.WriteDoubleWord(0x2434, 1u << 11);
            Assert.AreEqual(0xedcba987u, dma.ReadDoubleWord(0x438));
            dma.WriteDoubleWord(0x3434, 1u << 11);
            Assert.AreEqual(0x12345678u, dma.ReadDoubleWord(0x438));
        }

        [Test]
        public void ShouldAcceptAsynchronousDREQOnNextSystemClockEdge()
        {
            memory.WriteDoubleWord(0, 0x12345678);
            Configure(0, Ram, Ram + 0x100, 2, Control(0, 2, 5, true, true));
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(250));
            dma.OnGPIO(5, true);
            Assert.AreEqual(Ram, dma.ReadDoubleWord(0));
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(750));
            Assert.AreEqual(Ram + 4, dma.ReadDoubleWord(0));
            dma.OnGPIO(5, false);
            Advance(1);
            Assert.AreEqual(0x12345678u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(1u, dma.ReadDoubleWord(8));
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(250));
            dma.OnGPIO(5, true);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(750));
            Assert.AreEqual(Ram + 8, dma.ReadDoubleWord(0));
            Advance(1);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldWaitForSystemClockAndResumePendingTransferAtNewFrequency()
        {
            var clocks = new MutableClock { SystemFrequency = 1000000 };
            dma = new RP2040_DMA(machine, clocks);
            memory.WriteDoubleWord(0, 0x12345678);
            Configure(0, Ram, Ram + 0x100, 2, Control(0, 2, 63, true, true));
            Advance(1);
            clocks.SetFrequency(0);
            Advance(100);
            Assert.AreEqual(0u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(2u, dma.ReadDoubleWord(8));
            clocks.SetFrequency(2000000);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(500));
            Assert.AreEqual(0x12345678u, memory.ReadDoubleWord(0x100));
            Assert.AreEqual(1u, dma.ReadDoubleWord(8));
            Advance(1);
            Assert.AreEqual(0u, dma.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldKeepPIOFifoDataFreshAcrossOneThousandRearmedWindows()
        {
            var pio = CreatePIO(1);
            for(var window = 0; window < 1000; window++)
            {
                var expected = (byte)(window * 37);
                memory.WriteByte(0, expected);
                Configure(0, Ram, Pio + 0x13, 1, Control(0, 0, 8, false, false));
                Configure(1, Pio + 0x20, Ram + 0x100, 1, Control(1, 0, 12, false, false));
                Advance(2);
                Force(pio, 0x80a0);
                Force(pio, 0xa0c7);
                Force(pio, 0x8020);
                Advance(2);
                Assert.AreEqual(expected, memory.ReadByte(0x100), "window {0}", window);
                Assert.AreEqual(0u, dma.ReadDoubleWord(12) & (Busy | 0xe0000000));
                Assert.AreEqual(0u, dma.ReadDoubleWord(0x4c) & (Busy | 0xe0000000));
                Assert.AreEqual(0u, pio.ReadDoubleWord(12));
                Assert.AreEqual(0u, pio.ReadDoubleWord(8));
                dma.WriteDoubleWord(0x400, 3);
            }
        }

        [Test]
        public void ShouldNeverRunBusAccessUnderDMAMonitorAndDrainAbortFromPeripheralCallback()
        {
            var peripheral = new WidthPeripheral(dma) { OnRead = _ => dma.WriteDoubleWord(0x444, 1) };
            machine.SystemBus.Register(peripheral, new BusRangeRegistration(Device, 0x100));
            Configure(0, Device, Ram, 1, Control(0, 2, 63, false, false));
            Advance(100);
            Assert.AreEqual(1, peripheral.Reads.Count);
            Assert.AreEqual(0x11223344u, memory.ReadDoubleWord(0));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x444));
        }

        private RP2040_PIO CreatePIO(int index = 0)
        {
            var gpio = new RP2_GPIOBank0(machine);
            var pio = new RP2040_PIO(machine, gpio, pioId: index, frequency: 1000000);
            machine.SystemBus.Register(pio, new BusRangeRegistration(Pio, 0x4000));
            for(var request = 0; request < 8; request++) pio.Connections[request].Connect(dma, index * 8 + request);
            return pio;
        }

        private static void Force(RP2040_PIO pio, ushort instruction) => pio.WriteDoubleWord(0xd8, instruction);
        private static uint Control(int channel, int size, int request, bool readIncrement, bool writeIncrement) =>
            1u | ((uint)size << 2) | (readIncrement ? 0x10u : 0) | (writeIncrement ? 0x20u : 0)
            | ((uint)channel << 11) | ((uint)request << 15);

        private void Configure(int channel, uint read, uint write, uint count, uint control, bool trigger = true)
        {
            var offset = channel * 0x40;
            dma.WriteDoubleWord(offset + 0x10, control);
            dma.WriteDoubleWord(offset, read);
            dma.WriteDoubleWord(offset + 4, write);
            dma.WriteDoubleWord(offset + 8, count);
            if(trigger) dma.WriteDoubleWord(0x430, 1u << channel);
        }

        private void Advance(ulong cycles) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(cycles));

        private sealed class WidthPeripheral : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral
        {
            public WidthPeripheral(RP2040_DMA dma) { this.dma = dma; }
            public readonly List<int> Reads = new List<int>();
            public Action<long> OnRead;
            public byte ReadByte(long offset) { Read(offset, 1); return 0x44; }
            public ushort ReadWord(long offset) { Read(offset, 2); return 0x3344; }
            public uint ReadDoubleWord(long offset) { Read(offset, 4); return 0x11223344; }
            public void WriteByte(long offset, byte value) => Assert.IsFalse(Monitor.IsEntered(dma));
            public void WriteWord(long offset, ushort value) => Assert.IsFalse(Monitor.IsEntered(dma));
            public void WriteDoubleWord(long offset, uint value) => Assert.IsFalse(Monitor.IsEntered(dma));
            public void Reset() { }
            private void Read(long offset, int width)
            {
                Assert.IsFalse(Monitor.IsEntered(dma));
                Reads.Add(width);
                OnRead?.Invoke(offset);
            }
            private readonly RP2040_DMA dma;
        }

        private sealed class MutableClock : IRP2ClockSource
        {
            public ulong SystemFrequency { get; set; }
            public ulong PeripheralFrequency => SystemFrequency;
            public ulong USBFrequency => 48000000;
            public event Action ClockChanged;
            public void SetFrequency(ulong value) { SystemFrequency = value; ClockChanged?.Invoke(); }
        }

        private Machine machine;
        private MappedMemory memory;
        private RP2040_DMA dma;
        private const uint Ram = 0x20000000, Dma = 0x50000000, Pio = 0x50200000, Device = 0x40000000;
        private const uint Busy = 1u << 24, Quiet = 1u << 21;
    }
}
