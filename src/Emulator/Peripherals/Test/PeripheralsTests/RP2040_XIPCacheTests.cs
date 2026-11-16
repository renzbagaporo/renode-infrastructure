//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_XIPCacheTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            memory = new MappedMemory(machine, 0x200000);
            var ssi = new RP2040_SSI(machine);
            machine.SystemBus.Register(ssi, new BusRangeRegistration(0x18000000UL, (ulong)ssi.Size));
            flash = new WinbondW25QFlash(machine, memory);
            ssi.Register(flash, new NumberRegistrationPoint<int>(0));
            xip = new RP2040_XIP(machine, ssi, flash);
            machine.SystemBus.Register(xip, new BusRangeRegistration(0x14000000UL, 0x4000));
            Register(0x10000000, cached: true, allocate: true);
            Register(0x11000000, cached: true, allocate: false);
            Register(0x12000000, cached: false, allocate: true);
            Register(0x13000000, cached: false, allocate: false);
            // Standard SPI read (03h) through the SSI's XIP configuration.
            ssi.WriteDoubleWord(8, 0);
            ssi.WriteDoubleWord(0, 0x001f0300u);
            ssi.WriteDoubleWord(0x14, 2);
            ssi.WriteDoubleWord(0xf4, 0x03000218);
            ssi.WriteDoubleWord(0x10, 1);
            ssi.WriteDoubleWord(8, 1);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldCountHitsMissesAndInvalidationLikeFlashCachePerfctr()
        {
            Flush();
            xip.WriteDoubleWord(Accesses, 1);
            xip.WriteDoubleWord(Hits, 1);
            Read(0x10000100);
            AssertCounters(0, 1, "First access to data should miss");
            Read(0x10000100);
            AssertCounters(1, 2, "Second access to data should hit");
            machine.SystemBus.WriteDoubleWord(0x10000100, 0);
            Read(0x10000100);
            AssertCounters(1, 3, "Should miss after invalidation");
            Read(0x10000100);
            AssertCounters(2, 4, "Second access after invalidation should hit again");
            Read(0x10000104);
            AssertCounters(3, 5, "The same 8-byte line hits");
        }

        [Test]
        public void ShouldLookUpWithoutAllocatingThroughTheNonAllocatingAlias()
        {
            Flush();
            xip.WriteDoubleWord(Accesses, 1);
            Read(0x11000200);
            Read(0x11000200);
            AssertCounters(0, 2, "Non-allocating misses do not fill the line");
            Read(0x10000200);
            Read(0x11000200);
            AssertCounters(1, 4, "A line the allocating alias filled hits through either cached alias");
        }

        [Test]
        public void ShouldNotCountUncachedAliasesOrReadsWithTheCacheDisabled()
        {
            Flush();
            xip.WriteDoubleWord(Accesses, 1);
            Read(0x12000300);
            Read(0x13000300);
            AssertCounters(0, 0, "Uncached aliases bypass the cache");
            xip.WriteDoubleWord(0x3000, 1); // CTRL.EN clear
            Read(0x10000300);
            AssertCounters(0, 0, "A disabled cache counts nothing");
        }

        [Test]
        public void ShouldReplaceTheLeastRecentlyUsedOfTwoWays()
        {
            Flush();
            xip.WriteDoubleWord(Accesses, 1);
            // Lines 8 KiB apart share a set.
            Read(0x10000000);
            Read(0x10002000);
            Read(0x10000000);
            Read(0x10004000);
            AssertCounters(1, 4, "Two fills, a hit, then the third line evicts the older way");
            Read(0x10000000);
            Read(0x10002000);
            AssertCounters(2, 6, "The recently used line stayed; the evicted one misses");
        }

        [Test]
        public void ShouldCountTheBootsReadsBeforeAnyCounterAccess()
        {
            Read(0x10000100);
            Read(0x10000100);
            AssertCounters(1, 2, "flash_cache_perfctr's \"At boot\" counts");
        }

        [Test]
        public void ShouldStopCountingAfterTheBootUntilACounterAccess()
        {
            for(var i = 0; i < 65536 + 10; i++) Read(0x10000200);
            AssertCounters(65535, 65536, "Counting stops after the boot's reads");
            Read(0x10000200);
            AssertCounters(65536, 65537, "A counter access restarts it");
        }

        [Test]
        public void ShouldClearEveryLineOnFlush()
        {
            xip.WriteDoubleWord(Accesses, 1);
            Read(0x10000400);
            Flush();
            Read(0x10000400);
            AssertCounters(0, 2);
        }

        private void Register(ulong address, bool cached, bool allocate)
        {
            var window = new RP2040_XIPMemory(machine, flash, xip, cached, allocate);
            machine.SystemBus.Register(window, new BusRangeRegistration(address, (ulong)memory.Size));
        }

        private void Read(ulong address) => machine.SystemBus.ReadDoubleWord(address);

        private void Flush()
        {
            xip.WriteDoubleWord(4, 1);
            Assert.AreEqual(1u, xip.ReadDoubleWord(8) & 1, "FLUSH_READY");
            xip.WriteDoubleWord(Hits, 1);
        }

        private void AssertCounters(uint hits, uint accesses, string message = null)
        {
            Assert.AreEqual(hits, xip.ReadDoubleWord(Hits), message);
            Assert.AreEqual(accesses, xip.ReadDoubleWord(Accesses), message);
        }

        private Machine machine;
        private MappedMemory memory;
        private WinbondW25QFlash flash;
        private RP2040_XIP xip;

        private const long Hits = 0xc, Accesses = 0x10;
    }
}
