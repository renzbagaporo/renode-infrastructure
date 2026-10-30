//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.UART;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_UARTTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            clocks = new TestClocks();
            uart = new RP2_UART(machine, clocks);
            output = new List<byte>();
            uart.CharReceived += output.Add;
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldResetToDisabledEmptyCharacterModeAndExposePrimeCellIdentity()
        {
            Assert.AreEqual(0x90u, uart.ReadDoubleWord(0x18));
            Assert.AreEqual(0x300u, uart.ReadDoubleWord(0x30));
            Assert.AreEqual(0x12u, uart.ReadDoubleWord(0x34));
            Assert.AreEqual(0u, uart.ReadDoubleWord(0x3c));
            Assert.AreEqual(0u, uart.BaudRate);
            var identity = new uint[8];
            for(var i = 0; i < identity.Length; i++) identity[i] = uart.ReadDoubleWord(0xfe0 + i * 4);
            CollectionAssert.AreEqual(new uint[] { 0x11, 0x10, 0x34, 0, 0xd, 0xf0, 5, 0xb1 }, identity);
        }

        [Test]
        public void ShouldShiftEachFrameAtConfiguredBaudAndDistinguishFIFOFromShifter()
        {
            Configure(false);
            uart.WriteDoubleWord(0, 0x41);
            uart.WriteDoubleWord(0, 0x42);
            Assert.AreEqual(0x28u, uart.ReadDoubleWord(0x18) & 0xa8);
            Advance(99);
            Assert.IsEmpty(output);
            Advance(1);
            CollectionAssert.AreEqual(new byte[] { 0x41 }, output);
            Assert.AreEqual(0x88u, uart.ReadDoubleWord(0x18) & 0xa8);
            Advance(100);
            CollectionAssert.AreEqual(new byte[] { 0x41, 0x42 }, output);
            Assert.AreEqual(0x80u, uart.ReadDoubleWord(0x18) & 0xa8);
        }

        [Test]
        public void ShouldLimitTXFIFOTo32AndPreserveOrder()
        {
            Configure(true, enable: false);
            for(uint i = 0; i < 32; i++) uart.WriteDoubleWord(0, i);
            Assert.AreEqual(0x28u, uart.ReadDoubleWord(0x18) & 0xa8);
            uart.WriteDoubleWord(0, 0xff);
            uart.WriteDoubleWord(0x30, 0x301);
            Advance(3200);
            Assert.AreEqual(32, output.Count);
            for(var i = 0; i < output.Count; i++) Assert.AreEqual(i, output[i]);
            Assert.AreEqual(0x80u, uart.ReadDoubleWord(0x18) & 0xa8);
        }

        [Test]
        public void ShouldBufferWhileDisabledAndFinishCurrentFrameBeforeStopping()
        {
            Configure(false, enable: false);
            uart.WriteDoubleWord(0, 0x41);
            Advance(1000);
            Assert.IsEmpty(output);
            Assert.AreEqual(8u, uart.ReadDoubleWord(0x18) & 8);
            uart.WriteDoubleWord(0x30, 0x301);
            Advance(50);
            uart.WriteDoubleWord(0, 0x42);
            uart.WriteDoubleWord(0x3030, 1);
            Advance(50);
            CollectionAssert.AreEqual(new byte[] { 0x41 }, output);
            Advance(1000);
            Assert.AreEqual(1, output.Count);
            uart.WriteDoubleWord(0x2030, 1);
            Advance(100);
            CollectionAssert.AreEqual(new byte[] { 0x41, 0x42 }, output);
        }

        [Test]
        public void ShouldLatchFractionalDivisorOnLineControlWrite()
        {
            Configure(false);
            Assert.AreEqual(100000u, uart.BaudRate);
            uart.WriteDoubleWord(0x24, 2);
            uart.WriteDoubleWord(0x28, 32);
            Assert.AreEqual(100000u, uart.BaudRate);
            uart.WriteDoubleWord(0x2c, 0x60);
            Assert.AreEqual(40000u, uart.BaudRate);
            uart.WriteDoubleWord(0, 0x55);
            Advance(249);
            Assert.IsEmpty(output);
            Advance(1);
            Assert.AreEqual(0x55, output[0]);
        }

        [TestCase(0x00u, 0x1f, 70ul)]
        [TestCase(0x20u, 0x3f, 80ul)]
        [TestCase(0x40u, 0x7f, 90ul)]
        [TestCase(0x60u, 0xff, 100ul)]
        [TestCase(0x6eu, 0xff, 120ul)]
        public void ShouldUseProgrammedWordLengthParityAndStops(uint line, int expectedByte, ulong duration)
        {
            Configure(false);
            uart.WriteDoubleWord(0x2c, line);
            uart.WriteDoubleWord(0, 0xff);
            Advance(duration - 1);
            Assert.IsEmpty(output);
            Advance(1);
            Assert.AreEqual(expectedByte, output[0]);
            Assert.AreEqual((line & 8) != 0 ? Bits.Two : Bits.One, uart.StopBits);
        }

        [TestCase(0u, Parity.None)]
        [TestCase(2u, Parity.Odd)]
        [TestCase(6u, Parity.Even)]
        [TestCase(0x82u, Parity.Forced1)]
        [TestCase(0x86u, Parity.Forced0)]
        public void ShouldReportActualParityConfiguration(uint bits, Parity expected)
        {
            uart.WriteDoubleWord(0x2c, bits | 0x60);
            Assert.AreEqual(expected, uart.ParityBit);
        }

        [Test]
        public void ShouldPauseShifterAndTimeoutWithoutLosingStateWhenPeripheralClockStops()
        {
            Configure(false);
            uart.WriteDoubleWord(0, 0x41);
            uart.WriteChar(0x42);
            uart.WriteDoubleWord(0x38, 0x40);
            Advance(50);
            clocks.SetFrequency(0);
            Advance(1000);
            Assert.IsEmpty(output);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteChar(0x43);
            clocks.SetFrequency(1600000);
            Advance(49);
            Assert.IsEmpty(output);
            Advance(1);
            Assert.AreEqual(0x41, output[0]);
            Advance(220);
            Assert.IsTrue(uart.IRQ.IsSet);
            Assert.AreEqual(0x42u, uart.ReadDoubleWord(0));
            Assert.AreEqual(0x10u, uart.ReadDoubleWord(0x18) & 0x10);
            Assert.IsFalse(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldTrackPeripheralClockIndependentlyOfSystemClock()
        {
            Configure(false);
            uart.WriteDoubleWord(0, 0x41);
            Advance(50);
            clocks.SetFrequency(800000);
            Assert.AreEqual(50000u, uart.BaudRate);
            Advance(99);
            Assert.IsEmpty(output);
            Advance(1);
            Assert.AreEqual(0x41, output[0]);
        }

        [Test]
        public void ShouldDiscardQueuedAndActiveWorkOnResetAndIgnoreWritesAndReceiveWhileHeld()
        {
            Configure(false);
            uart.WriteDoubleWord(0, 0x41);
            uart.WriteChar(0x42);
            Advance(50);
            uart.ResetHeld = true;
            Configure(false);
            uart.WriteDoubleWord(0, 0x43);
            uart.WriteChar(0x44);
            Advance(1000);
            Assert.IsEmpty(output);
            Assert.AreEqual(0x90u, uart.ReadDoubleWord(0x18));
            Assert.AreEqual(0x300u, uart.ReadDoubleWord(0x30));
            uart.ResetHeld = false;
            Configure(false);
            uart.WriteDoubleWord(0, 0x45);
            Advance(100);
            CollectionAssert.AreEqual(new byte[] { 0x45 }, output);
        }

        [Test]
        public void ShouldPreserveLiveModemInputsAcrossReset()
        {
            uart.OnGPIO(0, false);
            Assert.AreEqual(1u, uart.ReadDoubleWord(0x18) & 1);
            uart.ResetHeld = true;
            uart.ResetHeld = false;
            Assert.AreEqual(1u, uart.ReadDoubleWord(0x18) & 1);
            Assert.AreEqual(0u, uart.ReadDoubleWord(0x3c));
        }

        [Test]
        public void ShouldGenerateReceiveThresholdInterruptAndRespectMaskAndClear()
        {
            Configure(true);
            uart.WriteDoubleWord(0x34, 0); // 4 of 32 entries
            for(byte i = 0; i < 3; i++) uart.WriteChar(i);
            Assert.AreEqual(0u, uart.ReadDoubleWord(0x3c) & 0x10);
            uart.WriteChar(3);
            Assert.AreEqual(0x10u, uart.ReadDoubleWord(0x3c) & 0x10);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0x38, 0x10);
            Assert.IsTrue(uart.IRQ.IsSet);
            Assert.AreEqual(0u, uart.ReadDoubleWord(0));
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteChar(4);
            Assert.IsTrue(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0x44, 0x10);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteChar(5);
            Assert.IsTrue(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldSetTransmitInterruptOnlyOnActualDrainAndAllowManualClear()
        {
            Configure(false);
            uart.WriteDoubleWord(0x38, 0x20);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0, 0x41);
            Assert.IsTrue(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0x44, 0x20);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0, 0x42);
            Assert.IsFalse(uart.IRQ.IsSet);
            Advance(100);
            Assert.IsTrue(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldAssertFifoTransmitIRQWhenDrainingThroughWatermark()
        {
            Configure(true, enable: false);
            uart.WriteDoubleWord(0x38, 0x20);
            for(uint i = 0; i < 18; i++) uart.WriteDoubleWord(0, i);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0x30, 0x301);
            Assert.IsFalse(uart.IRQ.IsSet);
            Advance(100);
            Assert.IsTrue(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0, 0xff);
            Assert.IsFalse(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldSetTimeoutAfter32QuietBitPeriodsAndRestartOnNewReceive()
        {
            Configure(true);
            uart.WriteDoubleWord(0x38, 0x40);
            uart.WriteChar(1);
            Advance(319);
            Assert.IsFalse(uart.IRQ.IsSet);
            uart.WriteChar(2);
            Advance(319);
            Assert.IsFalse(uart.IRQ.IsSet);
            Advance(1);
            Assert.IsTrue(uart.IRQ.IsSet);
            Assert.AreEqual(1u, uart.ReadDoubleWord(0));
            Assert.IsTrue(uart.IRQ.IsSet);
            Assert.AreEqual(2u, uart.ReadDoubleWord(0));
            Assert.IsFalse(uart.IRQ.IsSet);
            Advance(1000);
            Assert.IsFalse(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldClearTimeoutExplicitlyAndRearmItWhileDataRemains()
        {
            Configure(true);
            uart.WriteDoubleWord(0x38, 0x40);
            uart.WriteChar(1);
            Advance(320);
            Assert.IsTrue(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0x44, 0x40);
            Assert.IsFalse(uart.IRQ.IsSet);
            Advance(320);
            Assert.IsTrue(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldReportOverrunWithoutOverwritingUnreadDataAndTagNextAcceptedCharacter()
        {
            Configure(false);
            uart.WriteDoubleWord(0x38, 0x400);
            uart.WriteChar(0x41);
            uart.WriteChar(0x42);
            Assert.IsTrue(uart.IRQ.IsSet);
            Assert.AreEqual(8u, uart.ReadDoubleWord(4));
            Assert.AreEqual(0x41u, uart.ReadDoubleWord(0));
            uart.WriteChar(0x43);
            Assert.AreEqual(0x843u, uart.ReadDoubleWord(0));
            uart.WriteDoubleWord(4, 0);
            Assert.AreEqual(0u, uart.ReadDoubleWord(4));
            Assert.IsTrue(uart.IRQ.IsSet);
            uart.WriteDoubleWord(0x44, 0x400);
            Assert.IsFalse(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldLoopBackOnlyAfterFrameCompletesAndWithoutExternalTransmission()
        {
            Configure(false);
            uart.WriteDoubleWord(0x2030, 0x80);
            uart.WriteDoubleWord(0, 0x5a);
            uart.WriteChar(0xff);
            Advance(99);
            Assert.AreEqual(0x10u, uart.ReadDoubleWord(0x18) & 0x10);
            Advance(1);
            Assert.AreEqual(0x5au, uart.ReadDoubleWord(0));
            Assert.IsEmpty(output);
        }

        [Test]
        public void ShouldHonorCTSBeforeStartingButFinishActiveFrameWhenCTSChanges()
        {
            Configure(false);
            uart.WriteDoubleWord(0x2030, 0x8000);
            uart.WriteDoubleWord(0, 0x41);
            Advance(1000);
            Assert.IsEmpty(output);
            uart.OnGPIO(0, false);
            Advance(50);
            uart.WriteDoubleWord(0, 0x42);
            uart.OnGPIO(0, true);
            Advance(50);
            CollectionAssert.AreEqual(new byte[] { 0x41 }, output);
            Advance(1000);
            Assert.AreEqual(1, output.Count);
            uart.OnGPIO(0, false);
            Advance(100);
            CollectionAssert.AreEqual(new byte[] { 0x41, 0x42 }, output);
        }

        [Test]
        public void ShouldDriveRTSAtReceiveWatermarkAndExposeModemChanges()
        {
            Configure(true);
            uart.WriteDoubleWord(0x34, 0);
            uart.WriteDoubleWord(0x2030, 0x4000);
            Assert.IsFalse(uart.RTS.IsSet);
            for(byte i = 0; i < 4; i++) uart.WriteChar(i);
            Assert.IsTrue(uart.RTS.IsSet);
            uart.ReadDoubleWord(0);
            Assert.IsFalse(uart.RTS.IsSet);
            uart.WriteDoubleWord(0x38, 2);
            uart.OnGPIO(0, false);
            Assert.IsTrue(uart.IRQ.IsSet);
            Assert.AreEqual(1u, uart.ReadDoubleWord(0x18) & 1);
            uart.WriteDoubleWord(0x44, 2);
            Assert.IsFalse(uart.IRQ.IsSet);
        }

        [Test]
        public void ShouldGenerateDMARequestsFromActualFifoSpaceAndErrorMask()
        {
            Configure(false, enable: false);
            uart.WriteDoubleWord(0x48, 3);
            Assert.IsFalse(uart.TxDMA.IsSet);
            uart.WriteDoubleWord(0, 0x41);
            uart.WriteDoubleWord(0x30, 0x301);
            Assert.IsTrue(uart.TxDMA.IsSet);
            uart.WriteDoubleWord(0, 0x42);
            Assert.IsFalse(uart.TxDMA.IsSet);
            uart.WriteChar(0x43);
            Assert.IsTrue(uart.RxDMA.IsSet);
            uart.WriteChar(0x44);
            Assert.IsFalse(uart.RxDMA.IsSet);
            uart.WriteDoubleWord(0x2048, 4);
            Assert.IsTrue(uart.RxDMA.IsSet);
            uart.ReadDoubleWord(0);
            Assert.IsFalse(uart.RxDMA.IsSet);
            uart.ResetHeld = true;
            Assert.IsFalse(uart.TxDMA.IsSet);
        }

        [Test]
        public void ShouldApplyAtomicInterposerReadSideEffectToDataRegister()
        {
            Configure(false);
            uart.WriteChar(0x12);
            uart.WriteDoubleWord(0x1000, 0x34);
            Assert.AreEqual(0x10u, uart.ReadDoubleWord(0x18) & 0x10);
            Advance(100);
            CollectionAssert.AreEqual(new byte[] { 0x26 }, output);
        }

        [Test]
        public void ShouldReplicateNarrowWritesAndApplyAliasesToControl()
        {
            uart.WriteByte(0x24, 0x12);
            Assert.AreEqual(0x1212u, uart.ReadDoubleWord(0x24));
            uart.WriteWord(0x2024, 0x4080);
            Assert.AreEqual(0x5292u, uart.ReadDoubleWord(0x24));
            uart.WriteDoubleWord(0x3024, 0xff00);
            Assert.AreEqual(0x92u, uart.ReadDoubleWord(0x24));
            uart.WriteByte(0x2038, 0x10);
            Assert.AreEqual(0x10u, uart.ReadDoubleWord(0x38));
            uart.WriteWord(0x1038, 0x20);
            Assert.AreEqual(0x30u, uart.ReadDoubleWord(0x38));
        }

        [Test]
        public void ShouldAllowSerialPeerToReenterWithoutHoldingUARTState()
        {
            Configure(false);
            uart.CharReceived += value =>
            {
                Assert.IsFalse(Monitor.IsEntered(uart));
                uart.WriteChar(value);
                if(value == 0x41) uart.WriteDoubleWord(0, 0x42);
            };
            uart.WriteDoubleWord(0, 0x41);
            Advance(100);
            Assert.AreEqual(0x41u, uart.ReadDoubleWord(0));
            Advance(100);
            Assert.AreEqual(0x42u, uart.ReadDoubleWord(0));
            CollectionAssert.AreEqual(new byte[] { 0x41, 0x42 }, output);
        }

        [TestCase("receive")]
        [TestCase("modem")]
        [TestCase("read")]
        [TestCase("write")]
        [TestCase("reset")]
        [TestCase("reset_hold")]
        [TestCase("clock")]
        public void ShouldEnterClockBeforeStateForExternalOperations(string operation)
        {
            Configure(false);
            uart.WriteDoubleWord(0, 0x41);
            Action access;
            switch(operation)
            {
            case "receive": access = () => uart.WriteChar(0x42); break;
            case "modem": access = () => uart.OnGPIO(0, false); break;
            case "read": access = () => uart.ReadDoubleWord(0x18); break;
            case "write": access = () => uart.WriteDoubleWord(0x38, 0x10); break;
            case "reset": access = uart.Reset; break;
            case "reset_hold": access = () => uart.ResetHeld = true; break;
            default: access = () => clocks.SetFrequency(800000); break;
            }
            using(var clockHeld = new ManualResetEventSlim())
            using(var releaseClock = new ManualResetEventSlim())
            using(var entering = new ManualResetEventSlim())
            {
                var stateOwned = false;
                var advancing = Task.Factory.StartNew(() => machine.ClockSource.ExecuteInLock(() =>
                {
                    clockHeld.Set();
                    Assert.IsTrue(releaseClock.Wait(TimeSpan.FromSeconds(5)));
                    if(!stateOwned) Advance(100);
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
                        if(!Monitor.TryEnter(uart)) { stateOwned = true; break; }
                        Monitor.Exit(uart);
                        Thread.Yield();
                    }
                    completedWhileClockHeld = accessing.IsCompleted;
                }
                finally
                {
                    releaseClock.Set();
                    Assert.IsTrue(Task.WaitAll(new[] { advancing, accessing }, TimeSpan.FromSeconds(5)));
                }
                Assert.IsFalse(stateOwned, "An external waiter must not own UART state while waiting for its clock");
                Assert.IsFalse(completedWhileClockHeld, "The external operation must observe completed clock callbacks");
            }
        }

        private void Configure(bool fifo, bool enable = true)
        {
            uart.WriteDoubleWord(0x24, 1);
            uart.WriteDoubleWord(0x28, 0);
            uart.WriteDoubleWord(0x2c, fifo ? 0x70u : 0x60u);
            uart.WriteDoubleWord(0x30, enable ? 0x301u : 0x300u);
        }

        private void Advance(ulong microseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));

        private sealed class TestClocks : IRP2ClockSource
        {
            public ulong SystemFrequency => 150000000;
            public ulong PeripheralFrequency { get; private set; } = 1600000;
            public ulong USBFrequency => 48000000;
            public event Action ClockChanged;
            public void SetFrequency(ulong frequency) { PeripheralFrequency = frequency; ClockChanged?.Invoke(); }
        }

        private Machine machine;
        private TestClocks clocks;
        private RP2_UART uart;
        private List<byte> output;
    }
}
