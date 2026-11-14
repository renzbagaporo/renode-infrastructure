//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.DMA;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_HSTXTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            // 100 MHz clk_hstx: one cycle every 10 ns.
            hstx = new RP2350_HSTX(machine, frequency: 100000000);
            fifo = new RP2350_HSTXFifo(hstx);
            output = new List<(ulong Cycle, byte[] Halves)>();
            hstx.OutputProduced += (cycle, halves) => output.Add((cycle, halves));
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldShiftOutMsbFirstWithAClockAndPackedControlBits()
        {
            // hstx/spi_lcd: BIT1 the clock, BIT0 data bit 7, BIT2 bit 27 (CSn x8)
            // and BIT3 bit 17 inverted (DC x8); shift left one bit a cycle, 8 times.
            hstx.WriteDoubleWord(0x08, 1u << 17);
            hstx.WriteDoubleWord(0x04, 7u | (7u << 8));
            hstx.WriteDoubleWord(0x0c, 27u | (27u << 8));
            hstx.WriteDoubleWord(0x10, 17u | (17u << 8) | (1u << 16));
            hstx.WriteDoubleWord(0x00, 1u | (31u << 8) | (8u << 16) | (1u << 28));
            fifo.WriteDoubleWord(4, 0xa5); // CSn low, DC high (field clear), data 0xa5
            Advance(200);
            Assert.AreEqual(1, output.Count);
            var halves = output[0].Halves;
            Assert.AreEqual(16, halves.Length);
            var data = 0;
            for(var cycle = 0; cycle < 8; cycle++)
            {
                Assert.AreEqual(0, halves[cycle * 2] & 2, "SCK low in the first half");
                Assert.AreEqual(2, halves[cycle * 2 + 1] & 2, "SCK high in the second half");
                Assert.AreEqual(halves[cycle * 2] & 1, halves[cycle * 2 + 1] & 1, "Data is stable across the cycle");
                Assert.AreEqual(0, halves[cycle * 2] & 4, "CSn low");
                Assert.AreEqual(8, halves[cycle * 2] & 8, "DC high");
                data = (data << 1) | (halves[cycle * 2] & 1);
            }
            Assert.AreEqual(0xa5, data);
        }

        [Test]
        public void ShouldRefillWithoutAGapAndStallWhenTheFifoRunsDry()
        {
            hstx.WriteDoubleWord(0x04, 0u | (1u << 8)); // two bits a cycle, LSB first
            hstx.WriteDoubleWord(0x00, 1u | (2u << 8) | (16u << 16));
            fifo.WriteDoubleWord(4, 0x11111111);
            fifo.WriteDoubleWord(4, 0x22222222);
            Advance(1000);
            Assert.AreEqual(2, output.Count);
            Assert.AreEqual(output[0].Cycle + 16, output[1].Cycle, "The second word follows on the cycle after the first's last shift");
            var start = output[1].Cycle;
            fifo.WriteDoubleWord(4, 0x33333333);
            Advance(100);
            Assert.AreEqual(3, output.Count);
            Assert.Greater(output[2].Cycle, start + 16, "The register emptied and waited for data");
            Assert.AreEqual(0x11111111u, Word(output[0].Halves));
            Assert.AreEqual(0x33333333u, Word(output[2].Halves));
        }

        [Test]
        public void ShouldReportFifoLevelsAndRequestDataUntilFull()
        {
            var dreq = new GPIOReceiver();
            hstx.DREQ.Connect(dreq, 0);
            hstx.WriteDoubleWord(0x00, 0x10050600); // configured but not enabled
            Assert.AreEqual(1u << 9, fifo.ReadDoubleWord(0), "Empty");
            Assert.IsTrue(dreq.State);
            for(uint i = 0; i < 8; i++) fifo.WriteDoubleWord(4, i);
            Assert.AreEqual(8u | (1u << 8), fifo.ReadDoubleWord(0), "Full");
            Assert.IsFalse(dreq.State);
            fifo.WriteDoubleWord(4, 8);
            Assert.AreNotEqual(0u, fifo.ReadDoubleWord(0) & (1u << 10), "Written when full");
            fifo.WriteDoubleWord(0, 1u << 10);
            Assert.AreEqual(0u, fifo.ReadDoubleWord(0) & (1u << 10));
            // Enable with 5 shifts a word: the first pop is one cycle later, the
            // second at the refill 5 cycles after that.
            // The first word is popped when EN is set and shifts from the next
            // cycle; the next pop is at its refill 5 cycles later.
            hstx.WriteDoubleWord(0x00, 0x10050601);
            Advance(15);
            Assert.AreEqual(7u, fifo.ReadDoubleWord(0) & 0xff);
            Assert.IsTrue(dreq.State, "The pop freed a slot");
            fifo.WriteDoubleWord(4, 9);
            Assert.IsFalse(dreq.State);
            Advance(30);
            Assert.IsFalse(dreq.State, "Still full before the refill");
            Advance(20);
            Assert.IsTrue(dreq.State, "The timer raised DREQ at the next pop without a bus access");
        }

        [Test]
        public void ShouldRepeatAndRotateRawDataAndSkipNops()
        {
            hstx.WriteDoubleWord(0x04, 0u | (1u << 8)); // two bits a cycle, LSB first
            hstx.WriteDoubleWord(0x24, (8u << 0) | (1u << 8)); // RAW: rotate by 8 each output
            hstx.WriteDoubleWord(0x00, 1u | 2u | (2u << 8) | (16u << 16));
            fifo.WriteDoubleWord(4, 0xf000); // NOP
            fifo.WriteDoubleWord(4, 0x1000 | 3); // RAW_REPEAT, 3 words
            fifo.WriteDoubleWord(4, 0x44332211);
            fifo.WriteDoubleWord(4, 0x0000 | 2); // RAW, 2 words
            fifo.WriteDoubleWord(4, 0xaaaaaaaa);
            fifo.WriteDoubleWord(4, 0xbbbbbbbb);
            Advance(1000);
            var words = output.Select(o => Word(o.Halves)).ToArray();
            CollectionAssert.AreEqual(new uint[] { 0x44332211, 0x11443322, 0x22114433, 0xaaaaaaaa, 0xbbbbbbbb }, words);
            Assert.AreEqual(0u, fifo.ReadDoubleWord(0) & 0xff);
        }

        [Test]
        public void ShouldTmdsEncodeEachLaneWithRunningDisparity()
        {
            var disparity = 0;
            var symbols = Enumerable.Range(0, 4).Select(_ => RP2350_HSTX.EncodeTmdsSymbol(0, ref disparity)).ToArray();
            CollectionAssert.AreEqual(new uint[] { 0x100, 0x3ff, 0x100, 0x3ff }, symbols, "DVI 1.0: black alternates to stay DC balanced");
            disparity = 0;
            for(var value = 0; value < 256; value++)
            {
                for(var repeat = 0; repeat < 3; repeat++)
                {
                    var symbol = RP2350_HSTX.EncodeTmdsSymbol((byte)value, ref disparity);
                    Assert.AreEqual(value, DecodeTmds(symbol), $"Round trip of 0x{value:X2}");
                    Assert.LessOrEqual(System.Math.Abs(disparity), 10);
                }
            }
        }

        [Test]
        public void ShouldTmdsEncodeRgb332PixelsLikeTheDviExample()
        {
            // hstx/dvi_out_hstx_encoder: lane 2 takes bits 7:5, lane 1 bits 4:2
            // and lane 0 bits 1:0 of each byte; 4 pixels a word, LSB first.
            hstx.WriteDoubleWord(0x28, (2u << 21) | (0u << 16) | (2u << 13) | (29u << 8) | (1u << 5) | 26u);
            hstx.WriteDoubleWord(0x24, (4u << 24) | (8u << 16) | (1u << 8));
            hstx.WriteDoubleWord(0x00, 1u | 2u | (5u << 28) | (5u << 16) | (2u << 8));
            for(var lane = 0; lane < 3; lane++)
            {
                hstx.WriteDoubleWord(0x04 + 8 * lane, (uint)(lane * 10) | ((uint)(lane * 10 + 1) << 8));
            }
            fifo.WriteDoubleWord(4, 0x2000 | 4); // TMDS, 4 pixels
            fifo.WriteDoubleWord(4, 0xe01c03ff);
            Advance(1000);
            Assert.AreEqual(4, output.Count);
            var expected = new byte[] { 0xff, 0x03, 0x1c, 0xe0 };
            for(var pixel = 0; pixel < 4; pixel++)
            {
                var halves = output[pixel].Halves;
                Assert.AreEqual(expected[pixel] & 0xe0, DecodeTmds(Lane(halves, 4)), "Lane 2");
                Assert.AreEqual((expected[pixel] << 3) & 0xe0, DecodeTmds(Lane(halves, 2)), "Lane 1");
                Assert.AreEqual((expected[pixel] << 6) & 0xc0, DecodeTmds(Lane(halves, 0)), "Lane 0");
            }
        }

        [Test]
        public void ShouldPaceADMAChannelThroughTheFifoRequest()
        {
            var memory = new MappedMemory(machine, 0x1000);
            var dma = new RP2040_DMA(machine, frequency: 100000000);
            machine.SystemBus.Register(memory, new BusRangeRegistration(0x20000000, 0x1000));
            machine.SystemBus.Register(dma, new BusRangeRegistration(0x50000000, 0x1000));
            machine.SystemBus.Register(fifo, new BusRangeRegistration(0x50600000, 0x1000));
            hstx.DREQ.Connect(dma, 52);
            for(uint i = 0; i < 32; i++) memory.WriteDoubleWord(4 * i, 0x01010101u * i);
            hstx.WriteDoubleWord(0x04, 0u | (8u << 8));
            hstx.WriteDoubleWord(0x00, 1u | (1u << 8) | (8u << 16));
            // CTRL_TRIG (RP2040 layout): EN, word size, INCR_READ, CHAIN_TO itself, TREQ 52.
            dma.WriteDoubleWord(0x00, 0x20000000);
            dma.WriteDoubleWord(0x04, 0x50600004);
            dma.WriteDoubleWord(0x08, 32);
            dma.WriteDoubleWord(0x0c, 1u | (2u << 2) | 0x10u | (52u << 15));
            Advance(5000);
            Assert.AreEqual(32, output.Count, "Every word reached the shift register");
            for(var i = 1; i < output.Count; i++) Assert.AreEqual(output[i - 1].Cycle + 8, output[i].Cycle, $"No stall before word {i}");
            Assert.AreEqual(0u, fifo.ReadDoubleWord(0) & (1u << 10), "The DMA never overran the FIFO");
        }

        // The word shifted out LSB first on BIT0, two bits a cycle.
        private static uint Word(byte[] halves) => Lane(halves, 0, 32);

        // The bits output by pin, first half then second half of each cycle.
        private static uint Lane(byte[] halves, int pin, int count = 10)
        {
            uint value = 0;
            for(var i = 0; i < count && i < halves.Length; i++) value |= (uint)((halves[i] >> pin) & 1) << i;
            return value;
        }

        private static int DecodeTmds(uint symbol)
        {
            var data = (int)(symbol & 0xff);
            if((symbol & 0x200) != 0) data = ~data & 0xff;
            var result = data & 1;
            for(var i = 1; i < 8; i++)
            {
                var bit = ((data >> i) ^ (data >> (i - 1))) & 1;
                if((symbol & 0x100) == 0) bit ^= 1;
                result |= bit << i;
            }
            return result;
        }

        // Output words are produced lazily; a STAT read brings the model up to
        // the current time, as any access does.
        private void Advance(ulong nanoseconds)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(nanoseconds));
            fifo.ReadDoubleWord(0);
        }

        private Machine machine;
        private RP2350_HSTX hstx;
        private RP2350_HSTXFifo fifo;
        private List<(ulong Cycle, byte[] Halves)> output;

        private sealed class GPIOReceiver : IGPIOReceiver
        {
            public void OnGPIO(int number, bool value) => State = value;
            public void Reset() => State = false;
            public bool State { get; private set; }
        }
    }
}
