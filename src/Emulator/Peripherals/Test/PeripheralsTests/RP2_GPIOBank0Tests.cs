//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_GPIOBank0Tests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
        }

        [TearDown]
        public void TearDown()
        {
            machine?.Dispose();
        }

        [Test]
        public void ShouldPublishPinEventsOutsideStateMonitor()
        {
            var gpio = new RP2_GPIOBank0(machine);
            var inputChanged = false;
            var driveChanged = false;
            gpio.InputChanged += (_, __) =>
            {
                inputChanged = true;
                Assert.IsFalse(Monitor.IsEntered(gpio));
            };
            gpio.DriveChanged += (_, __, ___) =>
            {
                driveChanged = true;
                Assert.IsFalse(Monitor.IsEntered(gpio));
            };
            gpio.WriteDoubleWord(4, 0x1f | (3u << 8) | (3u << 12));
            Assert.IsTrue(inputChanged);
            Assert.IsTrue(driveChanged);
        }

        [Test]
        public void ShouldDeliverReciprocalTransitionsWithoutLockingTheOtherBank()
        {
            var other = new RP2_GPIOBank0(machine);
            var gpio = new RP2_GPIOBank0(machine);
            gpio.Connections[0].Connect(other, 1);
            other.Connections[0].Connect(gpio, 1);
            using(var simultaneous = new Barrier(2))
            {
                Action<int, bool> alignSources = (pin, value) =>
                {
                    if(pin == 0 && value)
                    {
                        Assert.IsTrue(simultaneous.SignalAndWait(TimeSpan.FromSeconds(5)));
                    }
                };
                gpio.InputChanged += alignSources;
                other.InputChanged += alignSources;
                var left = Task.Run(() => gpio.WriteDoubleWord(4, 0x1f | (3u << 8) | (3u << 12)));
                var right = Task.Run(() => other.WriteDoubleWord(4, 0x1f | (3u << 8) | (3u << 12)));
                Assert.IsTrue(Task.WaitAll(new[] { left, right }, TimeSpan.FromSeconds(10)), "Reciprocal GPIO delivery deadlocked");
            }
            Assert.IsTrue(gpio.GetInput(1));
            Assert.IsTrue(other.GetInput(1));
            gpio.ResetHeld = true;
            other.ResetHeld = true;
            Assert.IsFalse(gpio.GetInput(1));
            Assert.IsFalse(other.GetInput(1));
        }

        [Test]
        public void ShouldRequireInputEnableAndReleasedIsolationOnRP2350()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.OnGPIO(4, true);
            Assert.IsFalse(gpio.GetInput(4));
            gpio.SetPadConfiguration(4, 0x56);
            Assert.IsTrue(gpio.GetInput(4));
            gpio.SetPadConfiguration(4, 0x156);
            Assert.IsFalse(gpio.GetInput(4));
        }

        [Test]
        public void ShouldGatePeripheralOutputByFunctionEnableAndPadControl()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.SetPeripheralOutput(5, 2, true, true);
            gpio.WriteDoubleWord(2 * 8 + 4, 5);
            Assert.IsFalse(gpio.Connections[2].IsSet);
            gpio.SetPadConfiguration(2, 0x56);
            Assert.IsTrue(gpio.Connections[2].IsSet);
            Assert.AreEqual(1u << 13, gpio.ReadDoubleWord(2 * 8) & (1u << 13));
            gpio.SetPadConfiguration(2, 0xd6);
            Assert.IsFalse(gpio.Connections[2].IsSet);
            gpio.SetPadConfiguration(2, 0x56);
            gpio.WriteDoubleWord(2 * 8 + 4, 0x1f);
            Assert.IsFalse(gpio.Connections[2].IsSet);
        }

        [Test]
        public void ShouldApplyInputOutputAndEnableOverridesIndependently()
        {
            var gpio = new RP2_GPIOBank0(machine);
            gpio.WriteDoubleWord(4, 0x1f | (3u << 8) | (3u << 12));
            Assert.IsTrue(gpio.Connections[0].IsSet);
            Assert.IsTrue(gpio.GetInput(0));
            gpio.WriteDoubleWord(0x2004, 2u << 16);
            Assert.IsFalse(gpio.GetInput(0));
            Assert.AreNotEqual(0u, gpio.ReadDoubleWord(0) & (1u << 17));
        }

        [Test]
        public void ShouldResolveReleasedOpenDrainOutputThroughPullUp()
        {
            var gpio = new RP2_GPIOBank0(machine);
            gpio.SetPadConfiguration(4, 0x5a);
            gpio.WriteDoubleWord(4 * 8 + 4, 3);
            gpio.SetPeripheralOutput(3, 4, false, false);
            Assert.IsTrue(gpio.GetInput(4));
            gpio.SetPeripheralOutput(3, 4, false, true);
            Assert.IsFalse(gpio.GetInput(4));
            gpio.SetPeripheralOutput(3, 4, false, false);
            Assert.IsTrue(gpio.GetInput(4));
        }

        [Test]
        public void ShouldLatchAndAcknowledgeEdgeInterruptsIndependentlyOfLevels()
        {
            var gpio = new RP2_GPIOBank0(machine);
            gpio.WriteDoubleWord(0x100, 8);
            gpio.OnGPIO(0, true);
            Assert.IsTrue(gpio.Core0IRQ.IsSet);
            Assert.IsFalse(gpio.Core1IRQ.IsSet);
            Assert.AreEqual(0xau, gpio.ReadDoubleWord(0xf0) & 0xf);
            gpio.WriteDoubleWord(0xf0, 8);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.AreEqual(2u, gpio.ReadDoubleWord(0xf0) & 0xf);
            gpio.WriteDoubleWord(0xf0, 2);
            Assert.AreEqual(2u, gpio.ReadDoubleWord(0xf0) & 0xf);
        }

        [Test]
        public void ShouldRouteRP2350UpperGPIOInterruptToSelectedCore()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.SetPadConfiguration(44, 0x56);
            gpio.WriteDoubleWord(0x290 + 5 * 4, 8u << 16);
            gpio.OnGPIO(44, true);
            Assert.IsTrue(gpio.Core1IRQ.IsSet);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.AreEqual(1u << 12, gpio.ReadDoubleWord(0x214));
            gpio.WriteDoubleWord(0x230 + 5 * 4, 8u << 16);
            Assert.IsFalse(gpio.Core1IRQ.IsSet);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldNotLatchEdgeWhenResetRemovesDrivenHighOutput(bool rp2350)
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: rp2350);
            gpio.SetPadConfiguration(0, 0x56);
            gpio.WriteDoubleWord(4, 0x1f | (3u << (rp2350 ? 12 : 8)) | (3u << (rp2350 ? 14 : 12)));
            Assert.IsTrue(gpio.GetInput(0));
            gpio.Reset();
            var interruptOffset = rp2350 ? 0x230 : 0xf0;
            Assert.AreEqual(1u, gpio.ReadDoubleWord(interruptOffset) & 0xf);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldIgnoreEdgesDuringResetHoldAndDetectEdgesAfterRelease(bool rp2350)
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: rp2350);
            gpio.SetPadConfiguration(0, 0x56);
            var interruptOffset = rp2350 ? 0x230 : 0xf0;
            gpio.ResetHeld = true;
            gpio.OnGPIO(0, true);
            Assert.AreEqual(2u, gpio.ReadDoubleWord(interruptOffset) & 0xf);
            gpio.OnGPIO(0, false);
            Assert.AreEqual(1u, gpio.ReadDoubleWord(interruptOffset) & 0xf);
            gpio.ResetHeld = false;
            gpio.OnGPIO(0, true);
            Assert.AreEqual(0xau, gpio.ReadDoubleWord(interruptOffset) & 0xf);
        }

        [Test]
        public void ShouldApplyPadAtomicAliasesAndVariantResetValues()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true);
            Assert.AreEqual(0x116u, pads.ReadDoubleWord(4));
            gpio.OnGPIO(0, true);
            pads.WriteDoubleWord(0x3004, 0x100);
            Assert.IsFalse(gpio.GetInput(0));
            pads.WriteDoubleWord(0x2004, 0x40);
            Assert.IsTrue(gpio.GetInput(0));
            Assert.AreEqual(0x56u, pads.ReadDoubleWord(4));
        }

        [TestCase(false, 8, 12, 0x3003331fu)]
        [TestCase(true, 12, 14, 0x3003f01fu)]
        public void ShouldUseVariantSpecificOutputOverridesAndWritableFields(bool rp2350, int outputShift, int enableShift, uint mask)
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: rp2350);
            gpio.SetPadConfiguration(0, 0x56);
            gpio.WriteDoubleWord(4, uint.MaxValue);
            Assert.AreEqual(mask, gpio.ReadDoubleWord(4));
            gpio.WriteDoubleWord(4, 0x1f | (3u << outputShift) | (3u << enableShift));
            Assert.IsTrue(gpio.GetInput(0));
            gpio.WriteDoubleWord(4, 0x1f | (2u << outputShift) | (3u << enableShift));
            Assert.IsFalse(gpio.GetInput(0));
        }

        [Test]
        public void ShouldPublishSioWireAndInterruptCallbacksOutsideStateMonitor()
        {
            var sio = new TestSIO();
            var gpio = new RP2_GPIOBank0(machine, sio);
            sio.InputReceived = () => Assert.IsFalse(Monitor.IsEntered(gpio));
            var receiver = new TestReceiver(() => Assert.IsFalse(Monitor.IsEntered(gpio)));
            gpio.Connections[0].Connect(receiver, 0);
            gpio.Core0IRQ.Connect(receiver, 1);
            gpio.WriteDoubleWord(0x100, 8);
            gpio.OnGPIO(0, true);
            gpio.ReleaseInput(0);
            gpio.WriteDoubleWord(4, 5);
            sio.OutputValue = 1;
            sio.OutputEnableValue = 1;
            sio.Changed();
            Assert.IsTrue(gpio.GetInput(0));
            gpio.Reset();
            Assert.IsFalse(gpio.GetInput(0));
            gpio.WriteDoubleWord(4, 0x1f | (3u << 8) | (3u << 12));
            gpio.ResetHeld = true;
            Assert.IsFalse(gpio.GetInput(0));
        }

        [Test]
        public void ShouldPublishPadChangesOutsideTheSharedStateMonitor()
        {
            var gpio = new RP2_GPIOBank0(machine);
            var pads = new RP2_PadsBank0(machine, gpio);
            var notifications = 0;
            gpio.InputChanged += (_, __) =>
            {
                Assert.IsFalse(Monitor.IsEntered(gpio));
                Assert.IsFalse(Monitor.IsEntered(pads));
                notifications++;
            };
            pads.WriteDoubleWord(4, 0x5a);
            Assert.IsTrue(gpio.GetInput(0));
            pads.Reset();
            Assert.IsFalse(gpio.GetInput(0));
            Assert.AreEqual(2, notifications);
        }

        [Test]
        public void ShouldPublishConcurrentUpdatesInOrderWithoutHoldingStateDuringCallbacks()
        {
            var gpio = new RP2_GPIOBank0(machine);
            var values = new List<bool>();
            using(var firstCallback = new ManualResetEventSlim())
            using(var finishCallback = new ManualResetEventSlim())
            {
                var first = 0;
                gpio.InputChanged += (pin, value) =>
                {
                    if(pin != 0) return;
                    values.Add(value);
                    if(Interlocked.Exchange(ref first, 1) == 0)
                    {
                        firstCallback.Set();
                        Assert.IsTrue(finishCallback.Wait(TimeSpan.FromSeconds(5)));
                    }
                };
                var publishing = Task.Run(() => gpio.OnGPIO(0, true));
                try
                {
                    Assert.IsTrue(firstCallback.Wait(TimeSpan.FromSeconds(5)));
                    var updating = Task.Run(() => { gpio.OnGPIO(0, false); gpio.OnGPIO(0, true); });
                    Assert.IsTrue(updating.Wait(TimeSpan.FromSeconds(5)), "State updates waited for an external callback");
                }
                finally
                {
                    finishCallback.Set();
                    Assert.IsTrue(publishing.Wait(TimeSpan.FromSeconds(5)));
                }
            }
            Assert.That(values, Is.EqualTo(new[] { true, false, true }));
            Assert.IsTrue(gpio.Connections[0].IsSet);
        }

        [Test]
        public void ShouldSerializeConcurrentPadPinAndResetChanges()
        {
            var gpio = new RP2_GPIOBank0(machine);
            var pads = new RP2_PadsBank0(machine, gpio);
            gpio.InputChanged += (_, __) => Assert.IsFalse(Monitor.IsEntered(gpio));
            gpio.DriveChanged += (_, __, ___) => Assert.IsFalse(Monitor.IsEntered(gpio));
            using(var start = new Barrier(3))
            {
                var input = Task.Run(() =>
                {
                    Assert.IsTrue(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                    for(var i = 0; i < 200; i++)
                    {
                        gpio.OnGPIO(1, (i & 1) != 0);
                        gpio.ReleaseInput(1);
                    }
                });
                var output = Task.Run(() =>
                {
                    Assert.IsTrue(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                    for(var i = 0; i < 200; i++)
                    {
                        gpio.SetPeripheralOutput(3, 0, (i & 1) != 0, true);
                        gpio.WriteDoubleWord(4, 3);
                        pads.WriteDoubleWord(4, (i & 1) == 0 ? 0x56u : 0xd6u);
                    }
                });
                var reset = Task.Run(() =>
                {
                    Assert.IsTrue(start.SignalAndWait(TimeSpan.FromSeconds(5)));
                    for(var i = 0; i < 200; i++)
                    {
                        gpio.ResetHeld = true;
                        pads.Reset();
                        gpio.ResetHeld = false;
                    }
                });
                Assert.IsTrue(Task.WaitAll(new[] { input, output, reset }, TimeSpan.FromSeconds(10)));
            }
            gpio.ResetHeld = true;
            gpio.ReleaseInput(0);
            gpio.ReleaseInput(1);
            pads.Reset();
            Assert.IsFalse(gpio.GetInput(0));
            Assert.IsFalse(gpio.GetInput(1));
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
        }

        private sealed class TestReceiver : IGPIOReceiver
        {
            public TestReceiver(Action callback) { this.callback = callback; }
            public void OnGPIO(int number, bool value) => callback();
            public void Reset() { }
            private readonly Action callback;
        }

        private sealed class TestSIO : IRP2GPIO
        {
            public void OnGPIO(int number, bool value) => InputReceived?.Invoke();
            public void Reset() { }
            public void Changed() => GPIOChanged?.Invoke();
            public ulong Output => OutputValue;
            public ulong OutputEnable => OutputEnableValue;
            public ulong OutputValue;
            public ulong OutputEnableValue;
            public Action InputReceived;
            public event Action GPIOChanged;
        }

        private Machine machine;
    }
}
