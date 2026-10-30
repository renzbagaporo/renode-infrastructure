//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_WatchdogTests
    {
        [SetUp]
        public void SetUp()
        {
            resets = 0;
            machine = new Machine();
            var xosc = new RP2_XOSC(machine);
            clocks = new RP2040_Clocks(machine, xosc, new RP2040_ROSC(machine),
                new RP2_PLL(machine, xosc), new RP2_PLL(machine, xosc));
            watchdog = new RP2040_Watchdog(machine, clocks);
            watchdog.ResetRequested += () => resets++;
            watchdog.WriteDoubleWord(0x2c, 0x206); // 6 MHz ROSC / 6 = 1 MHz tick.
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldApplyTheTwoDecrementsPerTickErratum()
        {
            Arm();
            Advance(9);
            Assert.AreEqual(0u, watchdog.ReadDoubleWord(8));
            Assert.AreEqual(0, resets);
            Advance(1);
            Assert.AreEqual(1u, watchdog.ReadDoubleWord(8));
            Assert.AreEqual(1, resets);
            Assert.AreEqual(0u, watchdog.ReadDoubleWord(0) & 0xffffff);
            Assert.AreNotEqual(0u, watchdog.ReadDoubleWord(0) & 0x40000000);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldNotRearmAnExpiredWatchdogOnClockChanges(bool force)
        {
            Arm();
            if(force) watchdog.WriteDoubleWord(0x2000, 0x80000000);
            else Advance(10);
            Assert.AreEqual(1, resets);
            clocks.WriteDoubleWord(0x48, 0x800);
            Advance(100);
            Assert.AreEqual(1, resets, "A clock update must not reload an expired watchdog");
            Assert.AreEqual(force ? 2u : 1u, watchdog.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldRetainCauseAndScratchUntilTheRunReset()
        {
            watchdog.WriteDoubleWord(0x1c, 0x6ab73121);
            watchdog.WriteDoubleWord(0x2000, 0x80000000);
            Assert.AreEqual(2u, watchdog.ReadDoubleWord(8));
            Assert.AreEqual(0x6ab73121u, watchdog.ReadDoubleWord(0x1c));
            watchdog.Reset();
            Assert.AreEqual(0u, watchdog.ReadDoubleWord(8));
            for(var offset = 0xc; offset <= 0x28; offset += 4) Assert.AreEqual(0u, watchdog.ReadDoubleWord(offset));
        }

        [Test]
        public void ShouldStopWhenTheReferenceTickIsDisabled()
        {
            Arm();
            Advance(4);
            watchdog.WriteDoubleWord(0x2c, 6);
            Advance(100);
            Assert.AreEqual(0, resets);
            watchdog.WriteDoubleWord(0x2c, 0x206);
            Advance(6);
            Assert.AreEqual(1, resets);
        }

        [Test]
        public void ShouldPauseWithoutReloadingWhenDisabled()
        {
            Arm();
            Advance(4);
            watchdog.WriteDoubleWord(0x3000, 0x40000000);
            Advance(100);
            Assert.AreEqual(0, resets);
            watchdog.WriteDoubleWord(0x2000, 0x40000000);
            Advance(6);
            Assert.AreEqual(1, resets);
        }

        [Test]
        public void ShouldReloadTheDeadlineFromTheNewWrite()
        {
            Arm();
            Advance(8);
            watchdog.WriteDoubleWord(4, 20);
            Advance(8);
            Assert.AreEqual(0, resets);
            Advance(2);
            Assert.AreEqual(1, resets);
        }

        private void Arm()
        {
            watchdog.WriteDoubleWord(4, 20);
            watchdog.WriteDoubleWord(0, 0x47000000);
        }

        private void Advance(ulong microseconds) =>
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));

        private Machine machine;
        private RP2040_Clocks clocks;
        private RP2040_Watchdog watchdog;
        private int resets;
    }
}
