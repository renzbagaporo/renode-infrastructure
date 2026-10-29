//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;

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
        public void ShouldLatchInputEnableWithoutIsolatingTheInputOnRP2350()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.OnGPIO(4, true);
            Assert.IsFalse(gpio.GetInput(4));
            gpio.SetPadConfiguration(4, 0x56);
            Assert.IsTrue(gpio.GetInput(4));
            gpio.SetPadConfiguration(4, 0x156);
            Assert.IsTrue(gpio.GetInput(4));
            gpio.OnGPIO(4, false);
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

        [Test]
        public void ShouldLatchOutputAndPadControlsWhileIsolated()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            gpio.SetPadConfiguration(47, 0x56);
            gpio.WriteDoubleWord(47 * 8 + 4, 5);
            gpio.SetPeripheralOutput(5, 47, true, true);
            gpio.SetPadConfiguration(47, 0x156);
            gpio.SetPeripheralOutput(5, 47, false, false);
            gpio.SetPadConfiguration(47, 0x180);
            gpio.GetOutputState(47, out var level, out var enabled);
            Assert.IsTrue(level);
            Assert.IsTrue(enabled);
            Assert.IsTrue(gpio.GetInput(47));
            gpio.OnGPIO(47, false);
            Assert.IsFalse(gpio.GetInput(47));
            gpio.ReleaseInput(47);
            Assert.IsTrue(gpio.GetInput(47));
            gpio.SetPadConfiguration(47, 0x80);
            gpio.GetOutputState(47, out level, out enabled);
            Assert.IsFalse(level);
            Assert.IsFalse(enabled);
            Assert.IsFalse(gpio.GetInput(47));
        }

        [Test]
        public void ShouldReadColdResetPullUpThroughAnIsolatedInput()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, numberOfPins: 8);
            gpio.ResetPadConfiguration(2, 0x15a);
            Assert.IsTrue(gpio.GetInput(2));
            gpio.SetPadConfiguration(2, 0x156);
            Assert.IsTrue(gpio.GetInput(2));
            gpio.OnGPIO(2, false);
            Assert.IsFalse(gpio.GetInput(2));
            gpio.ReleaseInput(2);
            Assert.IsTrue(gpio.GetInput(2));
            gpio.SetPadConfiguration(2, 0x56);
            Assert.IsFalse(gpio.GetInput(2));
        }

        [Test]
        public void ShouldPreserveIsolationAndLatchedDriveAcrossPadBlockReset()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true);
            pads.WriteDoubleWord(4, 0x56);
            gpio.WriteDoubleWord(4, 0xf01f);
            pads.WriteDoubleWord(4, 0x156);
            gpio.WriteDoubleWord(4, 0x1f);
            pads.ResetHeld = true;
            Assert.AreEqual(0x116u, pads.ReadDoubleWord(4));
            gpio.GetOutputState(0, out var level, out var enabled);
            Assert.IsTrue(level);
            Assert.IsTrue(enabled);
            Assert.IsTrue(gpio.GetInput(0));
            pads.ResetHeld = false;
            pads.WriteDoubleWord(4, 0x56);
            gpio.GetOutputState(0, out level, out enabled);
            Assert.IsFalse(enabled);
            Assert.IsFalse(gpio.GetInput(0));
        }

        [Test]
        public void ShouldRetainReleasedIsolationAcrossPadBlockReset()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true);
            pads.WriteDoubleWord(4, 0x56);
            pads.ResetHeld = true;
            Assert.AreEqual(0x16u, pads.ReadDoubleWord(4));
            pads.ResetHeld = false;
            pads.Reset();
            Assert.AreEqual(0x116u, pads.ReadDoubleWord(4));
        }

        [Test]
        public void ShouldExposeRP2350DebugPadRegisters()
        {
            var gpio = new RP2_GPIOBank0(machine, rp2350: true);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true);
            Assert.AreEqual(0x5au, pads.ReadDoubleWord(0xc4));
            Assert.AreEqual(0x5au, pads.ReadDoubleWord(0xc8));
            pads.WriteDoubleWord(0xc4, uint.MaxValue);
            pads.WriteDoubleWord(0xc8, uint.MaxValue);
            Assert.AreEqual(0x1ffu, pads.ReadDoubleWord(0xc4));
            Assert.AreEqual(0x1ffu, pads.ReadDoubleWord(0xc8));
            pads.WriteDoubleWord(0x30c8, 0x100);
            Assert.AreEqual(0xffu, pads.ReadDoubleWord(0xc8));
        }

        [Test]
        public void ShouldRerouteUpperPinInterruptWhenItsSecurityMaskChanges()
        {
            var access = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, accessControl: access);
            gpio.SetPadConfiguration(47, 0x56);
            gpio.WriteDoubleWord(0x25c, 0x80000000);
            gpio.OnGPIO(47, true);
            Assert.IsTrue(gpio.Core0IRQ.IsSet);
            Assert.IsFalse(gpio.Core0NonSecureIRQ.IsSet);
            access.WriteDoubleWord(0x10, 1u << 15);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.IsTrue(gpio.Core0NonSecureIRQ.IsSet);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(0x204));
            Assert.AreEqual(1u << 15, gpio.ReadDoubleWord(0x20c));
        }

        [Test]
        public void ShouldMapQSPIInterruptSecurityToItsSioBitPosition()
        {
            var access = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, numberOfPins: 8, accessControl: access, securityPinOffset: 56);
            gpio.ResetPadConfiguration(2, 0x156);
            gpio.WriteDoubleWord(0x248, 8u << 8);
            gpio.OnGPIO(2, true);
            access.WriteDoubleWord(0x10, 1u << 2);
            Assert.IsTrue(gpio.Core0IRQ.IsSet);
            Assert.IsFalse(gpio.Core0NonSecureIRQ.IsSet);
            access.WriteDoubleWord(0x10, 1u << 26);
            Assert.IsFalse(gpio.Core0IRQ.IsSet);
            Assert.IsTrue(gpio.Core0NonSecureIRQ.IsSet);
        }

        [Test]
        public void ShouldFilterNonSecurePinPadAndInterruptAccessesByTransactionAttributes()
        {
            var access = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, accessControl: access);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true);
            var master = new TestBusMaster();
            var state = new CortexM.ContextState { CpuSecure = true, AttributionSecure = false, Privileged = true };
            machine.SystemBus.Register(gpio, new BusRangeRegistration(0x40028000, 0x4000));
            machine.SystemBus.Register(pads, new BusRangeRegistration(0x40038000, 0x4000));
            access.WriteDoubleWord(0xc, 1u << 3);
            pads.WriteDoubleWord(4, 0x56);
            pads.WriteDoubleWord(16, 0x56);
            Assert.AreEqual(0u, machine.SystemBus.ReadDoubleWordWithState(0x40028004, master, state));
            Assert.AreEqual(0x1fu, machine.SystemBus.ReadDoubleWordWithState(0x4002801c, master, state));
            machine.SystemBus.WriteDoubleWordWithState(0x40028004, 5, master, state);
            machine.SystemBus.WriteDoubleWordWithState(0x4002801c, 5, master, state);
            Assert.AreEqual(0x1fu, gpio.ReadDoubleWord(4));
            Assert.AreEqual(5u, gpio.ReadDoubleWord(28));
            Assert.AreEqual(0u, machine.SystemBus.ReadDoubleWordWithState(0x40038004, master, state));
            Assert.AreEqual(0x56u, machine.SystemBus.ReadDoubleWordWithState(0x40038010, master, state));
            machine.SystemBus.WriteDoubleWordWithState(0x40038004, 0x5a, master, state);
            machine.SystemBus.WriteDoubleWordWithState(0x40038010, 0x5a, master, state);
            Assert.AreEqual(0x56u, pads.ReadDoubleWord(4));
            Assert.AreEqual(0x5au, pads.ReadDoubleWord(16));
            machine.SystemBus.WriteDoubleWordWithState(0x40028248, uint.MaxValue, master, state);
            Assert.AreEqual(0xf000u, gpio.ReadDoubleWord(0x248));
            gpio.OnGPIO(0, true);
            gpio.OnGPIO(3, true);
            Assert.AreEqual(0xa000u, machine.SystemBus.ReadDoubleWordWithState(0x40028230, master, state));
            machine.SystemBus.WriteDoubleWordWithState(0x40028230, uint.MaxValue, master, state);
            Assert.AreEqual(0x200au, gpio.ReadDoubleWord(0x230) & 0xf00f);
            Assert.AreEqual(0u, machine.SystemBus.ReadDoubleWordWithState(0x40028200, master, state));
            Assert.AreEqual(8u, machine.SystemBus.ReadDoubleWordWithState(0x40028208, master, state));
        }

        [TestCase(false, 0u)]
        [TestCase(true, 0x116u)]
        public void ShouldApplyOptionalQfn60A2PadSecurityRemapping(bool a2Remap, uint expected)
        {
            var access = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, numberOfPins: 30, accessControl: access);
            var pads = new RP2_PadsBank0(machine, gpio, rp2350: true, rp2350A2PadRemap: a2Remap);
            var master = new TestBusMaster();
            var state = new CortexM.ContextState { AttributionSecure = false, Privileged = true };
            machine.SystemBus.Register(pads, new BusRangeRegistration(0x40038000, 0x4000));
            access.WriteDoubleWord(0xc, 1u << 8);
            Assert.AreEqual(expected, machine.SystemBus.ReadDoubleWordWithState(0x40038004, master, state));
        }

        [Test]
        public void ShouldDisconnectSecurePeripheralWhenGpioOrPeripheralPermissionsChange()
        {
            var access = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, accessControl: access);
            gpio.WriteDoubleWord(12 * 8 + 4, 0); // HSTX, Secure-only at reset
            Assert.AreEqual(0u, gpio.ReadDoubleWord(12 * 8 + 4));
            access.WriteDoubleWord(0xc, 1u << 12);
            Assert.AreEqual(31u, gpio.ReadDoubleWord(12 * 8 + 4));
            gpio.WriteDoubleWord(12 * 8 + 4, 0);
            Assert.AreEqual(31u, gpio.ReadDoubleWord(12 * 8 + 4));
            access.WriteDoubleWord(0x80, 0xacce00ff);
            gpio.WriteDoubleWord(12 * 8 + 4, 0);
            Assert.AreEqual(0u, gpio.ReadDoubleWord(12 * 8 + 4));
            access.WriteDoubleWord(0x80, 0xacce00fc);
            Assert.AreEqual(31u, gpio.ReadDoubleWord(12 * 8 + 4));
            gpio.WriteDoubleWord(12 * 8 + 4, 5);
            Assert.AreEqual(5u, gpio.ReadDoubleWord(12 * 8 + 4));
        }

        [TestCase(47, 1u, 0x94)]
        [TestCase(47, 2u, 0xa0)]
        [TestCase(47, 3u, 0x88)]
        [TestCase(47, 4u, 0x8c)]
        [TestCase(47, 6u, 0x4c)]
        [TestCase(47, 7u, 0x50)]
        [TestCase(47, 8u, 0x54)]
        [TestCase(47, 9u, 0xe4)]
        [TestCase(47, 10u, 0x48)]
        [TestCase(47, 11u, 0xa0)]
        [TestCase(8, 2u, 0xa4)]
        [TestCase(12, 2u, 0xa0)]
        [TestCase(26, 11u, 0xa4)]
        [TestCase(30, 11u, 0xa0)]
        public void ShouldUsePeripheralSecurityForMuxFunction(int pin, uint function, long permission)
        {
            var access = new RP2350_AccessControl(machine);
            var gpio = new RP2_GPIOBank0(machine, rp2350: true, accessControl: access);
            access.WriteDoubleWord(pin < 32 ? 0xc : 0x10, 1u << (pin & 31));
            access.WriteDoubleWord(permission, 0xacce00fc);
            gpio.WriteDoubleWord(pin * 8 + 4, function);
            Assert.AreEqual(31u, gpio.ReadDoubleWord(pin * 8 + 4));
            access.WriteDoubleWord(permission, 0xacce00ff);
            gpio.WriteDoubleWord(pin * 8 + 4, function);
            Assert.AreEqual(function, gpio.ReadDoubleWord(pin * 8 + 4));
        }

        private sealed class TestBusMaster : IPeripheralWithTransactionState, IBusPeripheral
        {
            public void Reset() { }
            public IReadOnlyDictionary<string, int> StateBits { get; } = new Dictionary<string, int>();
            public bool TryConvertStateObjToUlong(IContextState stateObj, out ulong? state)
            {
                state = stateObj is CortexM.ContextState value ? (ulong?)value : null;
                return state.HasValue;
            }
            public bool TryConvertUlongToStateObj(ulong? state, out IContextState stateObj)
            {
                stateObj = state.HasValue ? (CortexM.ContextState)state.Value : null;
                return state.HasValue;
            }
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
