//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.GPIOPort;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class DigitalWireTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ShouldResolveReleasedPinsUsingWireBias(bool pullUp)
        {
            using(var wire = new DigitalWire(pullUp))
            {
                var left = new Pin();
                var right = new Pin();
                wire.Connect(left, 0);
                wire.Connect(right, 0);
                Assert.AreEqual(pullUp, left.Input);
                Assert.AreEqual(pullUp, right.Input);
                left.Drive(!pullUp, true);
                Assert.AreEqual(!pullUp, right.Input);
                left.Drive(!pullUp, false);
                Assert.AreEqual(pullUp, right.Input);
            }
        }

        [Test]
        public void ShouldHandOverSWDDataBetweenHostAndTargetWithoutContention()
        {
            using(var wire = new DigitalWire())
            {
                var host = new Pin();
                var target = new Pin();
                wire.Connect(host, 0);
                wire.Connect(target, 0);
                var levels = new List<bool>();
                host.InputChanged = levels.Add;
                host.Drive(false, true);
                host.Drive(false, false);
                target.Drive(false, true);
                target.Drive(true, true);
                target.Drive(true, false);
                Assert.That(levels, Is.EqualTo(new[] { false, true, false, true }));
                Assert.AreEqual(0ul, wire.ContentionCount);
                Assert.IsFalse(wire.IsContended);
            }
        }

        [Test]
        public void ShouldReportOpposingDriversAndResolveLowUntilTheyRelease()
        {
            using(var wire = new DigitalWire())
            {
                var left = new Pin();
                var right = new Pin();
                wire.Connect(left, 0);
                wire.Connect(right, 0);
                left.Drive(true, true);
                right.Drive(false, true);
                Assert.IsTrue(wire.IsContended);
                Assert.IsFalse(left.Input);
                Assert.IsFalse(right.Input);
                Assert.AreEqual(1ul, wire.ContentionCount);
                left.Drive(true, true);
                Assert.AreEqual(1ul, wire.ContentionCount);
                right.Drive(false, false);
                Assert.IsFalse(wire.IsContended);
                Assert.IsTrue(right.Input);
            }
        }

        [Test]
        public void ShouldKeepRP2OutputLatchSeparateFromResolvedWireInput()
        {
            using(var machine = new Machine())
            using(var wire = new DigitalWire())
            {
                var bank = new RP2_GPIOBank0(machine);
                bank.SetPeripheralOutput(5, 0, true, false);
                bank.WriteDoubleWord(4, 5);
                var target = new Pin();
                wire.Connect(bank, 0);
                wire.Connect(target, 0);
                target.Drive(false, true);
                Assert.IsFalse(bank.GetInput(0));
                bank.GetOutputState(0, out var level, out var enabled);
                Assert.IsTrue(level);
                Assert.IsFalse(enabled);
                target.Drive(false, false);
                Assert.IsTrue(bank.GetInput(0));
                bank.ResetHeld = true;
                bank.GetOutputState(0, out _, out enabled);
                Assert.IsFalse(enabled);
            }
        }

        [Test]
        public void ShouldPublishReentrantDriveChangesInOrderWithoutRecursiveCallbacks()
        {
            using(var wire = new DigitalWire())
            {
                var host = new Pin();
                var target = new Pin();
                wire.Connect(host, 0);
                wire.Connect(target, 0);
                var observed = new List<bool>();
                var armed = true;
                var depth = 0;
                target.InputChanged = value =>
                {
                    Assert.AreEqual(0, depth++);
                    observed.Add(value);
                    // Another thread can read state while a receiver is active.
                    Assert.IsTrue(Task.Run(() => wire.Level).Wait(TimeSpan.FromSeconds(1)));
                    if(!value && armed)
                    {
                        armed = false;
                        host.Drive(false, false);
                        target.Drive(false, true);
                    }
                    depth--;
                };
                host.Drive(false, true);
                Assert.That(observed, Is.EqualTo(new[] { false, true, false }));
                Assert.IsFalse(wire.IsContended);
            }
        }

        [Test]
        public void ShouldSerializeConcurrentOpenDrainChanges()
        {
            using(var wire = new DigitalWire())
            {
                var left = new Pin();
                var right = new Pin();
                wire.Connect(left, 0);
                wire.Connect(right, 0);
                Action<Pin> toggle = pin =>
                {
                    for(var i = 0; i < 500; i++)
                    {
                        pin.Drive(false, true);
                        pin.Drive(false, false);
                    }
                };
                Assert.IsTrue(Task.WaitAll(new[] { Task.Run(() => toggle(left)), Task.Run(() => toggle(right)) }, TimeSpan.FromSeconds(10)));
                Assert.IsTrue(wire.Level);
                Assert.IsTrue(left.Input);
                Assert.IsTrue(right.Input);
                Assert.AreEqual(0ul, wire.ContentionCount);
            }
        }

        [Test]
        public void ShouldRejectDuplicatePinAndStopObservingAfterDisposal()
        {
            var pin = new Pin();
            var wire = new DigitalWire();
            wire.Connect(pin, 0);
            Assert.Throws<RecoverableException>(() => wire.Connect(pin, 0));
            wire.Dispose();
            pin.Drive(false, true);
            Assert.IsTrue(wire.Level);
        }

        [Test]
        public void ShouldResolveAnUndrivenNetFromPadPulls()
        {
            using(var wire = new DigitalWire(false))
            {
                var left = new PulledPin(false);
                var right = new PulledPin(null);
                wire.Connect(left, 0);
                wire.Connect(right, 0);
                Assert.IsFalse(right.Input, "A pull-down holds the net low");
                left.SetPull(true);
                Assert.IsTrue(right.Input, "A pull-up raises it");
                right.SetPull(false);
                Assert.IsTrue(left.Input, "Opposing pulls keep the net high");
                right.Drive(false, true);
                Assert.IsFalse(left.Input, "A strong driver wins");
                right.Drive(false, false);
                Assert.IsFalse(left.Input, "Released between opposing pulls, the net keeps the driven low");
                left.SetPull(null);
                Assert.IsFalse(left.Input, "A pull-down alone holds it low");
                left.SetPull(true);
                right.SetPull(null);
                Assert.IsTrue(left.Input, "A pull-up alone raises it");
            }
        }

        [Test]
        public void ShouldLetAnExternalPullUpOverridePadPullDowns()
        {
            using(var wire = new DigitalWire(true))
            {
                var pad = new PulledPin(false);
                var other = new Pin();
                wire.Connect(pad, 0);
                wire.Connect(other, 0);
                Assert.IsTrue(other.Input);
            }
        }

        [Test]
        public void ShouldFollowAnRP2PadPull()
        {
            var machine = new Machine();
            try
            {
                var bank = new RP2_GPIOBank0(machine);
                using(var wire = new DigitalWire(false))
                {
                    var other = new Pin();
                    wire.Connect(bank, 6);
                    wire.Connect(other, 0);
                    Assert.IsFalse(other.Input, "The reset pad pull-down holds an undriven net low");
                    bank.SetPadConfiguration(6, 0x5a); // IE, PUE
                    Assert.IsTrue(other.Input);
                    bank.SetPadConfiguration(6, 0x52); // IE, no pulls
                    Assert.IsFalse(other.Input);
                }
            }
            finally
            {
                machine.Dispose();
            }
        }

        private sealed class PulledPin : IPulledThreeStateGPIO
        {
            public PulledPin(bool? pull)
            {
                this.pull = pull;
            }

            public void Reset() { }

            public void GetOutputState(int number, out bool value, out bool enabled)
            {
                value = output;
                enabled = outputEnabled;
            }

            public bool? GetPull(int number) => pull;

            public void SetPull(bool? value)
            {
                pull = value;
                PullChanged?.Invoke(0, value);
            }

            public void Drive(bool value, bool enabled)
            {
                output = value;
                outputEnabled = enabled;
                DriveChanged?.Invoke(0, value, enabled);
            }

            public void OnGPIO(int number, bool value) => Input = value;

            public bool Input { get; private set; }
            public event Action<int, bool, bool> DriveChanged;
            public event Action<int, bool?> PullChanged;

            private bool output;
            private bool outputEnabled;
            private bool? pull;
        }

        private sealed class Pin : IThreeStateGPIO
        {
            public void Reset()
            {
                Drive(false, false);
            }

            public void GetOutputState(int number, out bool value, out bool enabled)
            {
                value = output;
                enabled = outputEnabled;
            }

            public void Drive(bool value, bool enabled)
            {
                output = value;
                outputEnabled = enabled;
                DriveChanged?.Invoke(0, value, enabled);
            }

            public void OnGPIO(int number, bool value)
            {
                Input = value;
                InputChanged?.Invoke(value);
            }

            public bool Input { get; private set; }
            public Action<bool> InputChanged;
            public event Action<int, bool, bool> DriveChanged;

            private bool output;
            private bool outputEnabled;
        }
    }
}
