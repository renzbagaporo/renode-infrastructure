//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_DMATests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 0x1000);
            dma = new RP2040_DMA(machine, frequency: 1000000, rp2350: true);
            machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
            machine.SystemBus.Register(dma, new BusRangeRegistration(Dma, 0x4000));
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldExposeSixteenChannelsAndTheRP2350RegisterMap()
        {
            Assert.AreEqual(16u, dma.ReadDoubleWord(0x468));
            dma.WriteDoubleWord(15 * 0x40, 0x12345678);
            Assert.AreEqual(0x12345678u, dma.ReadDoubleWord(15 * 0x40));
            dma.WriteDoubleWord(0x454, 0xfff);
            Assert.AreEqual(0xfffu, dma.ReadDoubleWord(0x454));
            Assert.AreEqual(3u, dma.ReadDoubleWord(0x480));
            Assert.AreEqual(3u, dma.ReadDoubleWord(0x4cc));
            Assert.AreEqual(0x3ffu, dma.ReadDoubleWord(0x4d0));
            dma.WriteDoubleWord(0x480 + 15 * 4, 1);
            Assert.AreEqual(1u, dma.ReadDoubleWord(0x480 + 15 * 4));
        }

        [Test]
        public void ShouldDecodeTheRP2350ControlLayoutAndChain()
        {
            memory.WriteDoubleWord(0, 0xa5a5a5a5);
            memory.WriteDoubleWord(0x100, 0x5a5a5a5a);
            Configure(1, Ram + 0x100, Ram + 0x104, 1, Control(1, 2, 63, false, false), trigger: false);
            Configure(0, Ram, Ram + 4, 1, Control(1, 2, 63, false, false));
            Assert.AreNotEqual(0u, dma.ReadDoubleWord(0x10) & Busy);
            Advance(10);
            Assert.AreEqual(0xa5a5a5a5u, memory.ReadDoubleWord(4));
            Assert.AreEqual(0x5a5a5a5au, memory.ReadDoubleWord(0x104));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x10) & Busy);
            Assert.AreEqual(3u, dma.ReadDoubleWord(0x400));
        }

        [Test]
        public void ShouldDecrementAddressesWithReverseIncrements()
        {
            for(uint i = 0; i < 4; i++) memory.WriteDoubleWord(0x10 + 4 * i, 0x100 + i);
            Configure(0, Ram + 0x1c, Ram + 0x80, 4, Control(0, 2, 63, true, true) | IncrementReadReverse);
            Advance(20);
            for(uint i = 0; i < 4; i++) Assert.AreEqual(0x103 - i, memory.ReadDoubleWord(0x80 + 4 * i));
            Assert.AreEqual(Ram + 0xc, dma.ReadDoubleWord(0));
            Assert.AreEqual(Ram + 0x90, dma.ReadDoubleWord(4));
        }

        [Test]
        public void ShouldKeepTransferringInEndlessModeUntilAborted()
        {
            Configure(0, Ram, Ram + 4, 0xf0000002, Control(0, 2, 63, false, false));
            Advance(40);
            Assert.AreEqual(0xf0000002u, dma.ReadDoubleWord(8));
            Assert.AreNotEqual(0u, dma.ReadDoubleWord(0x10) & Busy);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x400));
            dma.WriteDoubleWord(0x464, 1);
            Advance(10);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x464));
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x10) & Busy);
        }

        [Test]
        public void ShouldRetriggerItselfInTriggerSelfMode()
        {
            memory.WriteDoubleWord(0, 7);
            Configure(0, Ram, Ram + 0x40, 0x10000001, Control(0, 2, 63, false, true));
            Advance(3);
            Assert.AreEqual(1u, dma.ReadDoubleWord(0x400));
            Assert.AreNotEqual(0u, dma.ReadDoubleWord(0x10) & Busy);
            Advance(3);
            Assert.AreEqual(7u, memory.ReadDoubleWord(0x40));
            Assert.AreEqual(7u, memory.ReadDoubleWord(0x44));
            dma.WriteDoubleWord(0x464, 1);
        }

        [Test]
        public void ShouldDriveAllFourInterruptOutputs()
        {
            var outputs = new[] { dma.IRQ0, dma.IRQ1, dma.IRQ2, dma.IRQ3 };
            Configure(3, Ram, Ram + 4, 1, Control(3, 2, 63, false, false));
            Advance(5);
            Assert.AreEqual(1u << 3, dma.ReadDoubleWord(0x420));
            for(var i = 0; i < 4; i++) Assert.IsFalse(outputs[i].IsSet);
            dma.WriteDoubleWord(0x424, 1u << 3);
            Assert.IsTrue(dma.IRQ2.IsSet);
            Assert.AreEqual(1u << 3, dma.ReadDoubleWord(0x42c));
            dma.WriteDoubleWord(0x438, 1u << 5);
            Assert.IsTrue(dma.IRQ3.IsSet);
            Assert.AreEqual(1u << 5, dma.ReadDoubleWord(0x43c));
            dma.WriteDoubleWord(0x42c, 1u << 3);
            Assert.IsFalse(dma.IRQ2.IsSet);
            Assert.IsFalse(dma.IRQ0.IsSet);
            Assert.IsFalse(dma.IRQ1.IsSet);
        }

        // RP2350 CTRL: EN 0, DATA_SIZE 3:2, INCR_READ 4, INCR_READ_REV 5,
        // INCR_WRITE 6, CHAIN_TO 16:13, TREQ_SEL 22:17.
        private static uint Control(int chainTo, int size, int request, bool readIncrement, bool writeIncrement) =>
            1u | ((uint)size << 2) | (readIncrement ? 0x10u : 0) | (writeIncrement ? 0x40u : 0)
            | ((uint)chainTo << 13) | ((uint)request << 17);

        private void Configure(int channel, uint read, uint write, uint count, uint control, bool trigger = true)
        {
            var offset = channel * 0x40;
            dma.WriteDoubleWord(offset + 0x10, control);
            dma.WriteDoubleWord(offset, read);
            dma.WriteDoubleWord(offset + 4, write);
            dma.WriteDoubleWord(offset + 8, count);
            if(trigger) dma.WriteDoubleWord(0x450, 1u << channel);
        }

        private void Advance(ulong cycles) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(cycles));

        private Machine machine;
        private MappedMemory memory;
        private RP2040_DMA dma;

        private const uint Ram = 0x20000000, Dma = 0x50000000;
        private const uint Busy = 1u << 26, IncrementReadReverse = 1u << 5;
    }
}
