//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2ClockAccessTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            clocks = new TestClocks();
            gpio = new RP2_GPIOBank0(machine);
            pio = new RP2040_PIO(machine, gpio, clocks);
            clock = (BaseClockSource)machine.ClockSource;
            pio.WriteDoubleWord(0x48, 0x80a0); // pull block
            pio.WriteDoubleWord(0, 1);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [TestCase("gpio")]
        [TestCase("read")]
        [TestCase("write")]
        [TestCase("reset")]
        [TestCase("reset_hold")]
        [TestCase("clock")]
        public void ShouldEnterClockBeforePIOStateForExternalAccess(string operation)
        {
            Action access;
            switch(operation)
            {
            case "gpio": access = () => gpio.OnGPIO(4, true); break;
            case "read": access = () => pio.ReadDoubleWord(4); break;
            case "write": access = () => pio.WriteDoubleWord(0xc8, 0x10000); break;
            case "reset": access = pio.Reset; break;
            case "reset_hold": access = () => pio.ResetHeld = true; break;
            default: access = () => clocks.SetFrequency(2000000); break;
            }
            using(var clockHeld = new ManualResetEventSlim())
            using(var releaseClock = new ManualResetEventSlim())
            using(var entering = new ManualResetEventSlim())
            {
                var stateOwned = false;
                var advancing = Task.Factory.StartNew(() => clock.ExecuteInLock(() =>
                {
                    clockHeld.Set();
                    Assert.IsTrue(releaseClock.Wait(TimeSpan.FromSeconds(5)));
                    // A broken implementation is released without invoking a
                    // callback that would hang this entire test process.
                    if(!stateOwned) clock.Advance(TimeInterval.FromMicroseconds(1));
                }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Assert.IsTrue(clockHeld.Wait(TimeSpan.FromSeconds(5)));
                var accessing = Task.Factory.StartNew(() => { entering.Set(); access(); },
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                var completedWhileClockHeld = false;
                try
                {
                    Assert.IsTrue(entering.Wait(TimeSpan.FromSeconds(5)));
                    var deadline = Stopwatch.StartNew();
                    while(deadline.ElapsedMilliseconds < 100 && !accessing.IsCompleted)
                    {
                        if(!Monitor.TryEnter(pio)) { stateOwned = true; break; }
                        Monitor.Exit(pio);
                        Thread.Yield();
                    }
                    completedWhileClockHeld = accessing.IsCompleted;
                }
                finally
                {
                    releaseClock.Set();
                    Assert.IsTrue(Task.WaitAll(new[] { advancing, accessing }, TimeSpan.FromSeconds(5)));
                }
                Assert.IsFalse(stateOwned, "An input/register/reset waiter must not own PIO state while waiting for its clock");
                Assert.IsFalse(completedWhileClockHeld, "The external access must observe its clock's completed callbacks");
            }
        }

        private sealed class TestClocks : IRP2ClockSource
        {
            public ulong SystemFrequency { get; private set; } = 1000000;
            public ulong PeripheralFrequency => SystemFrequency;
            public ulong USBFrequency => 48000000;
            public event Action ClockChanged;
            public void SetFrequency(ulong frequency) { SystemFrequency = frequency; ClockChanged?.Invoke(); }
        }

        private Machine machine;
        private TestClocks clocks;
        private RP2_GPIOBank0 gpio;
        private RP2040_PIO pio;
        private BaseClockSource clock;
    }
}
