//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_RTCTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            rtc = new RP2040_RTC(machine, frequency: 1000);
            rtc.WriteDoubleWord(0, 999);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldSynchronizeEnableAndDisableToRTCClock()
        {
            rtc.WriteDoubleWord(0xc, 1);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0xc));
            AdvanceMilliseconds(1);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0xc));
            AdvanceMilliseconds(1);
            Assert.AreEqual(3u, rtc.ReadDoubleWord(0xc));
            rtc.WriteDoubleWord(0xc, 0);
            Assert.AreEqual(2u, rtc.ReadDoubleWord(0xc));
            AdvanceMilliseconds(2);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0xc));
        }

        [Test]
        public void ShouldLoadCalendarAndCountOnlyWhenEnabled()
        {
            Load(2026, 10, 29, 4, 12, 34, 56);
            AdvanceMilliseconds(2000);
            Assert.AreEqual(0x040c2238u, rtc.ReadDoubleWord(0x1c));
            Start();
            AdvanceMilliseconds(1000);
            Assert.AreEqual(0x040c2239u, rtc.ReadDoubleWord(0x1c));
            Assert.AreEqual((2026u << 12) | (10u << 8) | 29u, rtc.ReadDoubleWord(0x18));
        }

        [TestCase(2024u, 0u, 29u)]
        [TestCase(2025u, 0u, 1u)]
        [TestCase(2024u, 0x100u, 1u)]
        public void ShouldApplyHardwareLeapYearAndOverride(uint year, uint overrideValue, uint expectedDay)
        {
            Load(year, 2, 28, 3, 23, 59, 59);
            rtc.WriteDoubleWord(0xc, overrideValue | 1);
            AdvanceMilliseconds(1002);
            Assert.AreEqual(4u << 24, rtc.ReadDoubleWord(0x1c));
            var expectedMonth = expectedDay == 29 ? 2u : 3u;
            Assert.AreEqual((year << 12) | (expectedMonth << 8) | expectedDay, rtc.ReadDoubleWord(0x18));
        }

        [Test]
        public void ShouldLatchDateWhenReadingTimeAcrossMidnight()
        {
            Load(2026, 12, 31, 4, 23, 59, 59);
            Start();
            rtc.ReadDoubleWord(0x1c);
            AdvanceMilliseconds(1000);
            Assert.AreEqual((2026u << 12) | (12u << 8) | 31u, rtc.ReadDoubleWord(0x18));
            Assert.AreEqual(5u << 24, rtc.ReadDoubleWord(0x1c));
            Assert.AreEqual((2027u << 12) | (1u << 8) | 1u, rtc.ReadDoubleWord(0x18));
        }

        [Test]
        public void ShouldCompareAlarmFieldsAndHonorMaskAndForce()
        {
            Load(2026, 10, 29, 4, 0, 0, 0);
            rtc.WriteDoubleWord(0x14, 0x10000001);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            Start();
            AdvanceMilliseconds(1000);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0x20));
            Assert.IsTrue(rtc.IRQ.IsSet);
            AdvanceMilliseconds(1000);
            Assert.IsFalse(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x28, 1);
            Assert.IsTrue(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x3028, 1);
            Assert.IsFalse(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldHoldRTCAndBlockWritesDuringReset()
        {
            Load(2026, 10, 29, 4, 0, 0, 0);
            Start();
            rtc.ResetHeld = true;
            rtc.WriteDoubleWord(0xc, 1);
            AdvanceMilliseconds(2000);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0xc));
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x1c));
        }

        private void Load(uint year, uint month, uint day, uint weekday, uint hour, uint minute, uint second)
        {
            rtc.WriteDoubleWord(4, (year << 12) | (month << 8) | day);
            rtc.WriteDoubleWord(8, (weekday << 24) | (hour << 16) | (minute << 8) | second);
            rtc.WriteDoubleWord(0xc, 0x10);
        }

        private void Start()
        {
            rtc.WriteDoubleWord(0xc, 1);
            AdvanceMilliseconds(2);
        }

        private void AdvanceMilliseconds(ulong duration)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(duration));
        }

        private Machine machine;
        private RP2040_RTC rtc;
    }
}
