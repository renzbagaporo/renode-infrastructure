//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class WinbondW25QFlashCompletionTests
    {
        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(false, false)]
        public void ShouldKeepWipSetUntilTheArrayIsCommitted(bool observerFirst, bool erase)
        {
            using(var machine = new Machine())
            {
                var memory = new MappedMemory(machine, 2 * 1024 * 1024);
                WinbondW25QFlash flash = null;
                LimitTimer observer = null;
                var duration = erase ? 150000ul : 400ul;
                if(observerFirst) observer = NewObserver(machine, duration);
                flash = new WinbondW25QFlash(machine, memory);
                if(!observerFirst) observer = NewObserver(machine, duration);
                memory.WriteByte(0, 0xa5);
                byte observedStatus = 0xff;
                byte observedArray = 0xff;
                bool observedReadAvailable = false;
                var callbacks = 0;
                observer.LimitReached += () =>
                {
                    callbacks++;
                    observedArray = memory.ReadByte(0);
                    flash.Transmit(0x05);
                    observedStatus = flash.Transmit(0);
                    flash.FinishTransmission();
                    observedReadAvailable = flash.ReadAvailable;
                };
                Command(flash, 0x06);
                if(erase) Command(flash, 0xd8, 0, 0, 0);
                else Command(flash, 0x02, 0, 0, 0, 0);
                observer.Enabled = true;
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(duration));
                var finalArray = memory.ReadByte(0);
                TestContext.WriteLine($"observerFirst={observerFirst} erase={erase} callbacks={callbacks} observedStatus=0x{observedStatus:x2} observedArray=0x{observedArray:x2} readAvailable={observedReadAvailable} finalArray=0x{finalArray:x2}");
                Assert.Multiple(() =>
                {
                    Assert.AreEqual(1, callbacks);
                    Assert.AreEqual(erase ? 0xff : 0, finalArray, "The operation must ultimately commit in this time advance");
                    Assert.AreEqual(observerFirst ? 0xa5 : (erase ? 0xff : 0), observedArray, "Registration ordering control");
                    Assert.AreEqual(observerFirst ? 1 : 0, observedStatus & 1, "WIP must remain set until the NOR commit, including same-deadline callbacks");
                    Assert.AreEqual(!observerFirst, observedReadAvailable, "XIP readiness must not expose the precommit array as ready");
                });
            }
        }

        [TestCase(true, true)] [TestCase(true, false)]
        [TestCase(false, true)] [TestCase(false, false)]
        public void ShouldPreserveThePendingOperationAcrossControllerReset(bool resetFirst, bool erase)
        {
            using(var machine = new Machine())
            {
                var memory = new MappedMemory(machine, 2 * 1024 * 1024);
                var duration = erase ? 150000ul : 400ul;
                LimitTimer observer = resetFirst ? NewObserver(machine, duration) : null;
                var flash = new WinbondW25QFlash(machine, memory);
                if(!resetFirst) observer = NewObserver(machine, duration);
                memory.WriteByte(0, 0xa5);
                memory.WriteByte(1, 0xa5);
                observer.LimitReached += () =>
                {
                    // A controller reset leaves an external NOR operation running.
                    // New write commands may be accepted only after it commits.
                    flash.Reset();
                    Command(flash, 0x06);
                    Command(flash, 0x02, 0, 0, 1, 0);
                };
                Command(flash, 0x06);
                if(erase) Command(flash, 0xd8, 0, 0, 0);
                else Command(flash, 0x02, 0, 0, 0, 0);
                observer.Enabled = true;
                var clock = (BaseClockSource)machine.ClockSource;
                clock.Advance(TimeInterval.FromMicroseconds(duration));
                var expectedFirst = erase ? 0xff : 0;
                var expectedSecond = erase ? 0xff : 0xa5;
                Assert.AreEqual(expectedFirst, memory.ReadByte(0), "Reset must preserve the original operation");
                Assert.AreEqual(expectedSecond, memory.ReadByte(1), "A replacement must not commit at the old deadline");
                Assert.AreEqual(resetFirst, flash.ReadAvailable);
                clock.Advance(TimeInterval.FromMicroseconds(399));
                Assert.AreEqual(expectedSecond, memory.ReadByte(1), "Page program retains its full 400 us duration");
                clock.Advance(TimeInterval.FromMicroseconds(1));
                Assert.AreEqual(resetFirst ? expectedSecond : 0, memory.ReadByte(1));
                Assert.IsTrue(flash.ReadAvailable);
            }
        }

        private static LimitTimer NewObserver(Machine machine, ulong duration) => new LimitTimer(machine.ClockSource, 1000000, null, "precommit observer", limit: duration, workMode: WorkMode.OneShot, eventEnabled: true);

        private static void Command(WinbondW25QFlash flash, params byte[] data)
        {
            foreach(var value in data) flash.Transmit(value);
            flash.FinishTransmission();
        }
    }
}
