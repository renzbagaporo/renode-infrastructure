//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Miscellaneous;
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

        [Test]
        public void ShouldSynchronizeAlarmEnableAndDisableToRTCClock()
        {
            rtc.WriteDoubleWord(0x10, 0x10000000);
            Assert.AreEqual(0x10000000u, rtc.ReadDoubleWord(0x10));
            AdvanceMilliseconds(1);
            Assert.AreEqual(0x10000000u, rtc.ReadDoubleWord(0x10));
            AdvanceMilliseconds(1);
            Assert.AreEqual(0x30000000u, rtc.ReadDoubleWord(0x10));
            rtc.WriteDoubleWord(0x3010, 0x10000000);
            Assert.AreEqual(0x20000000u, rtc.ReadDoubleWord(0x10));
            AdvanceMilliseconds(2);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x10));
        }

        [Test]
        public void ShouldWaitForCalendarUpdateWhenArmedAtMatchingSecond()
        {
            Load(2020, 6, 5, 3, 15, 45, 0);
            Start();
            rtc.WriteDoubleWord(0x14, 0x10000000);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            AdvanceMilliseconds(2);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x20));
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(1000);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0x1c) & 0x3f);
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(59000);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x1c) & 0x3f);
            Assert.IsTrue(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldRearmSecondMatchWithoutRefiringWithinSameSecond()
        {
            Load(2020, 6, 5, 3, 15, 45, 0);
            rtc.WriteDoubleWord(0x14, 0x10000001);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            Start();
            AdvanceMilliseconds(1000);
            Assert.IsTrue(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x3010, 0x10000000);
            AdvanceMilliseconds(2);
            Assert.IsFalse(rtc.IRQ.IsSet);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x20));
            rtc.WriteDoubleWord(0x2010, 0x10000000);
            AdvanceMilliseconds(2);
            Assert.AreEqual(0x30000000u, rtc.ReadDoubleWord(0x10));
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(59000);
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(1000);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0x1c) & 0x3f);
            Assert.IsTrue(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldEvaluateWildcardAlarmAtEachCalendarUpdate()
        {
            Load(2020, 6, 5, 3, 15, 45, 0);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            Start();
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(1000);
            Assert.IsTrue(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x3010, 0x10000000);
            AdvanceMilliseconds(2);
            rtc.WriteDoubleWord(0x2010, 0x10000000);
            AdvanceMilliseconds(2);
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(1000);
            Assert.IsTrue(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldKeepRawAlarmIndependentOfMaskAndForce()
        {
            Load(2020, 6, 5, 3, 15, 45, 0);
            rtc.WriteDoubleWord(0x14, 0x10000001);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            Start();
            AdvanceMilliseconds(1000);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0x20));
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x2c));
            Assert.IsFalse(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x24, 1);
            Assert.IsTrue(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x24, 0);
            rtc.WriteDoubleWord(0x28, 1);
            Assert.IsTrue(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x3010, 0x10000000);
            AdvanceMilliseconds(2);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x20));
            Assert.IsTrue(rtc.IRQ.IsSet);
            rtc.WriteDoubleWord(0x28, 0);
            Assert.IsFalse(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldResetPendingAlarmSynchronizationAndInterrupt()
        {
            Load(2020, 6, 5, 3, 15, 45, 0);
            Start();
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            rtc.WriteDoubleWord(0x28, 1);
            AdvanceMilliseconds(1);
            rtc.Reset();
            AdvanceMilliseconds(2000);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x10));
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x20));
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x2c));
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x1c));
            Assert.IsFalse(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldPauseCalendarAndAlarmSynchronizationWhenClockIsGated()
        {
            var xosc = new RP2_XOSC(machine);
            var rosc = new RP2040_ROSC(machine);
            var clocks = new RP2040_Clocks(machine, xosc, rosc,
                new RP2_PLL(machine, xosc), new RP2_PLL(machine, xosc));
            clocks.WriteDoubleWord(0x70, 6000u << 8);
            clocks.WriteDoubleWord(0x6c, 0x840);
            Assert.AreEqual(1000ul, clocks.GetFrequency(9));
            rtc = new RP2040_RTC(machine, clocks);
            rtc.WriteDoubleWord(0, 999);
            Load(2020, 6, 5, 3, 15, 45, 0);
            Start();
            clocks.WriteDoubleWord(0x6c, 0x40);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            AdvanceMilliseconds(2000);
            Assert.AreEqual(0x10000000u, rtc.ReadDoubleWord(0x10));
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x1c) & 0x3f);
            Assert.IsFalse(rtc.IRQ.IsSet);
            clocks.WriteDoubleWord(0x6c, 0x840);
            AdvanceMilliseconds(2);
            Assert.AreEqual(0x30000000u, rtc.ReadDoubleWord(0x10));
            Assert.IsFalse(rtc.IRQ.IsSet);
            AdvanceMilliseconds(1000);
            Assert.AreEqual(1u, rtc.ReadDoubleWord(0x1c) & 0x3f);
            Assert.IsTrue(rtc.IRQ.IsSet);
        }

        [Test]
        public void ShouldPublishIRQAfterReleasingRegisterStateMonitor()
        {
            var receiver = new InterruptReceiver(rtc);
            rtc.IRQ.Connect(receiver, 0);
            rtc.WriteDoubleWord(0x28, 1);
            rtc.WriteDoubleWord(0x28, 0);
            Load(2020, 6, 5, 3, 15, 45, 0);
            rtc.WriteDoubleWord(0x14, 0x10000001);
            rtc.WriteDoubleWord(0x10, 0x10000000);
            rtc.WriteDoubleWord(0x24, 1);
            Start();
            AdvanceMilliseconds(1000);
            rtc.Reset();
            Assert.That(receiver.States, Does.Contain(true));
            Assert.That(receiver.States, Does.Contain(false));
            Assert.That(receiver.CalledUnderStateMonitor, Is.All.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldDrainIRQChangesFromReentrantAcknowledgeOrHeldReset(bool reset)
        {
            var receiver = new InterruptReceiver(rtc);
            receiver.Callback = value =>
            {
                if(!value) return;
                if(reset) rtc.ResetHeld = true;
                else rtc.WriteDoubleWord(0x28, 0);
            };
            rtc.IRQ.Connect(receiver, 0);
            rtc.WriteDoubleWord(0x28, 1);
            Assert.AreEqual(1, receiver.States.FindAll(value => value).Count);
            Assert.IsFalse(receiver.States[receiver.States.Count - 1]);
            Assert.That(receiver.CalledUnderStateMonitor, Is.All.False);
            Assert.AreEqual(0u, rtc.ReadDoubleWord(0x2c));
            Assert.IsFalse(rtc.IRQ.IsSet);
            if(reset)
            {
                rtc.WriteDoubleWord(0x28, 1);
                AdvanceMilliseconds(2000);
                Assert.IsFalse(rtc.IRQ.IsSet);
                rtc.ResetHeld = false;
                Assert.AreEqual(0u, rtc.ReadDoubleWord(0x2c));
            }
        }

        private sealed class InterruptReceiver : IGPIOReceiver
        {
            public InterruptReceiver(RP2040_RTC rtc) => this.rtc = rtc;

            public void Reset()
            {
                States.Clear();
                CalledUnderStateMonitor.Clear();
            }

            public void OnGPIO(int number, bool value)
            {
                States.Add(value);
                CalledUnderStateMonitor.Add(Monitor.IsEntered(rtc));
                rtc.ReadDoubleWord(0x2c);
                Callback?.Invoke(value);
            }

            public readonly List<bool> States = new List<bool>();
            public readonly List<bool> CalledUnderStateMonitor = new List<bool>();

            public System.Action<bool> Callback;

            private readonly RP2040_RTC rtc;
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
