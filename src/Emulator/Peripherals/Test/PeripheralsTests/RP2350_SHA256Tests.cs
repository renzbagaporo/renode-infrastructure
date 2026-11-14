//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

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
    public class RP2350_SHA256Tests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 0x1000);
            dma = new RP2040_DMA(machine, frequency: 1000000, rp2350: true);
            sha = new RP2350_SHA256(machine, frequency: 1000000);
            machine.SystemBus.Register(memory, new BusRangeRegistration(Ram, 0x1000));
            machine.SystemBus.Register(dma, new BusRangeRegistration(Dma, 0x4000));
            machine.SystemBus.Register(sha, new BusRangeRegistration(Sha, 0x1000));
            sha.DREQ.Connect(dma, ShaRequest);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldRequestDataUntilABlockIsFullAndAgainAfterCompression()
        {
            sha.WriteDoubleWord(0, 1); // START
            Assert.IsTrue(sha.DREQ.IsSet);
            for(var i = 0; i < 15; i++) sha.WriteDoubleWord(4, 0);
            Assert.IsTrue(sha.DREQ.IsSet, "15 words leave room for one more");
            sha.WriteDoubleWord(4, 0);
            Assert.IsFalse(sha.DREQ.IsSet, "The 16th word starts compression");
            Advance(60);
            Assert.IsTrue(sha.DREQ.IsSet, "Compression took 57 cycles");
        }

        [Test]
        public void ShouldHashTwoBlocksFedByByteDMAPacedByTheRequest()
        {
            // 64 bytes of 'a' and their padding block: the DMA must hold off
            // at the block boundary while the core compresses the first block.
            for(var i = 0; i < 64; i++) memory.WriteByte(i, (byte)'a');
            memory.WriteByte(64, 0x80);
            memory.WriteByte(126, 0x02); // message length 512 bits
            sha.WriteDoubleWord(0, 0x1000 | 1); // BSWAP, START
            Configure(Ram, Sha + 4, 128, Control(0, 0, ShaRequest, true, false));
            Advance(600);
            Assert.AreEqual(0u, dma.ReadDoubleWord(0x10) & Busy, "All 128 bytes were transferred");
            Assert.AreEqual(0u, sha.ReadDoubleWord(0) & 0x10, "No word arrived while the core was busy");
            Assert.AreEqual(SixtyFourA, Digest());
        }

        private uint[] Digest()
        {
            var digest = new uint[8];
            for(var i = 0; i < 8; i++) digest[i] = sha.ReadDoubleWord(8 + 4 * i);
            return digest;
        }

        // RP2350 CTRL: EN 0, DATA_SIZE 3:2, INCR_READ 4, INCR_WRITE 6,
        // CHAIN_TO 16:13, TREQ_SEL 22:17.
        private static uint Control(int chainTo, int size, int request, bool readIncrement, bool writeIncrement) =>
            1u | ((uint)size << 2) | (readIncrement ? 0x10u : 0) | (writeIncrement ? 0x40u : 0)
            | ((uint)chainTo << 13) | ((uint)request << 17);

        private void Configure(uint read, uint write, uint count, uint control)
        {
            dma.WriteDoubleWord(0x10, control);
            dma.WriteDoubleWord(0, read);
            dma.WriteDoubleWord(4, write);
            dma.WriteDoubleWord(8, count);
            dma.WriteDoubleWord(0x450, 1);
        }

        private void Advance(ulong cycles) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(cycles));

        private Machine machine;
        private MappedMemory memory;
        private RP2040_DMA dma;
        private RP2350_SHA256 sha;

        private const int ShaRequest = 54;
        private const uint Ram = 0x20000000, Dma = 0x50000000, Sha = 0x400f8000;
        private const uint Busy = 1u << 26;
        private static readonly uint[] SixtyFourA = { 0xffe054fe, 0x7ae0cb6d, 0xc65c3af9, 0xb61d5209, 0xf439851d, 0xb43d0ba5, 0x997337df, 0x154668eb };
    }
}
