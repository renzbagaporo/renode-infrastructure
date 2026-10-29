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
    public class RP2_TimerTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            timer = new RP2_Timer(machine);
        }

        [TearDown]
        public void TearDown()
        {
            machine?.Dispose();
        }

        [Test]
        public void ShouldRequireEnabledTickSourceAndFollowItsFrequency()
        {
            AdvanceMicroseconds(10);
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x28));
            timer.SetTickClock(1000000, true);
            AdvanceMicroseconds(10);
            Assert.AreEqual(10u, timer.ReadDoubleWord(0x28));
            timer.Clocked = false;
            AdvanceMicroseconds(10);
            Assert.AreEqual(10u, timer.ReadDoubleWord(0x28));
            timer.Frequency = 2000000;
            timer.Clocked = true;
            AdvanceMicroseconds(3);
            Assert.AreEqual(16u, timer.ReadDoubleWord(0x28));
        }

        [Test]
        public void ShouldCommitTimeOnHighWriteAndLatchHighOnLowRead()
        {
            timer.SetTickClock(1000000, true);
            timer.WriteDoubleWord(4, 0xfffffffe);
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x28));
            timer.WriteDoubleWord(0, 3);
            Assert.AreEqual(0xfffffffeu, timer.ReadDoubleWord(0xc));
            AdvanceMicroseconds(3);
            Assert.AreEqual(4u, timer.ReadDoubleWord(0x24));
            Assert.AreEqual(3u, timer.ReadDoubleWord(8));
            Assert.AreEqual(1u, timer.ReadDoubleWord(0xc));
            Assert.AreEqual(4u, timer.ReadDoubleWord(8));
        }

        [Test]
        public void ShouldFireAndDisarmAlarmAcrossLowWordRollover()
        {
            timer.SetTickClock(1000000, true);
            timer.WriteDoubleWord(4, 0xfffffffd);
            timer.WriteDoubleWord(0, 0);
            timer.WriteDoubleWord(0x38, 1);
            timer.WriteDoubleWord(0x10, 2);
            AdvanceMicroseconds(4);
            Assert.AreEqual(1u, timer.ReadDoubleWord(0x20));
            Assert.IsFalse(timer.Connections[0].IsSet);
            AdvanceMicroseconds(1);
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x20));
            Assert.AreEqual(1u, timer.ReadDoubleWord(0x34));
            Assert.IsTrue(timer.Connections[0].IsSet);
            timer.WriteDoubleWord(0x34, 1);
            Assert.IsFalse(timer.Connections[0].IsSet);
        }

        [Test]
        public void ShouldDisarmAlarmWithoutRaisingItsInterrupt()
        {
            timer.SetTickClock(1000000, true);
            timer.WriteDoubleWord(0x38, 1);
            timer.WriteDoubleWord(0x10, 10);
            timer.WriteDoubleWord(0x20, 1);
            AdvanceMicroseconds(20);
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x34));
            Assert.IsFalse(timer.Connections[0].IsSet);
        }

        [Test]
        public void ShouldPauseCounterAndAlarmTogether()
        {
            timer.SetTickClock(1000000, true);
            timer.WriteDoubleWord(0x38, 1);
            timer.WriteDoubleWord(0x10, 10);
            AdvanceMicroseconds(4);
            timer.WriteDoubleWord(0x30, 1);
            AdvanceMicroseconds(100);
            Assert.AreEqual(4u, timer.ReadDoubleWord(0x28));
            Assert.IsFalse(timer.Connections[0].IsSet);
            timer.WriteDoubleWord(0x30, 0);
            AdvanceMicroseconds(6);
            Assert.AreEqual(10u, timer.ReadDoubleWord(0x28));
            Assert.IsTrue(timer.Connections[0].IsSet);
        }

        [Test]
        public void ShouldForceInterruptWithoutInterruptEnable()
        {
            timer.WriteDoubleWord(0x3c, 4);
            Assert.IsTrue(timer.Connections[2].IsSet);
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x34));
            Assert.AreEqual(4u, timer.ReadDoubleWord(0x40));
            timer.WriteDoubleWord(0x303c, 4);
            Assert.IsFalse(timer.Connections[2].IsSet);
        }

        [Test]
        public void ShouldHonorDebugPauseMask()
        {
            timer.SetTickClock(1000000, true);
            AdvanceMicroseconds(3);
            timer.OnGPIO(0, true);
            AdvanceMicroseconds(20);
            Assert.AreEqual(3u, timer.ReadDoubleWord(0x28));
            timer.WriteDoubleWord(0x302c, 2);
            AdvanceMicroseconds(4);
            Assert.AreEqual(7u, timer.ReadDoubleWord(0x28));
        }

        [Test]
        public void ShouldKeepCounterStoppedWhileResetHeldAndResumeOnRelease()
        {
            timer.SetTickClock(1000000, true);
            AdvanceMicroseconds(4);
            timer.ResetHeld = true;
            timer.SetTickClock(1000000, true);
            AdvanceMicroseconds(20);
            Assert.AreEqual(0u, timer.ReadDoubleWord(0x28));
            timer.ResetHeld = false;
            AdvanceMicroseconds(3);
            Assert.AreEqual(3u, timer.ReadDoubleWord(0x28));
        }

        [Test]
        public void ShouldUseRP2350RegisterLayoutAndIrreversibleLock()
        {
            var rp2350Timer = new RP2_Timer(machine, rp2350: true);
            rp2350Timer.SystemFrequency = 150000000;
            rp2350Timer.WriteDoubleWord(0x38, 1);
            AdvanceMicroseconds(1);
            Assert.AreEqual(150u, rp2350Timer.ReadDoubleWord(0x28));
            rp2350Timer.WriteDoubleWord(0x44, 2);
            Assert.IsTrue(rp2350Timer.Connections[1].IsSet);
            rp2350Timer.WriteDoubleWord(0x34, 1);
            rp2350Timer.WriteDoubleWord(0x3034, 1);
            Assert.AreEqual(1u, rp2350Timer.ReadDoubleWord(0x34));
            rp2350Timer.WriteDoubleWord(0x30, 1);
            Assert.AreEqual(0u, rp2350Timer.ReadDoubleWord(0x30));
        }

        private void AdvanceMicroseconds(ulong duration)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(duration));
        }

        private Machine machine;
        private RP2_Timer timer;
    }
}
