//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_SSICompletionTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void ShouldKeepTheInFlightFrameWhenAnotherBusMasterWritesAtItsDeadline(bool writerFirst)
        {
            using(var machine = new Machine())
            {
                LimitTimer writer = null;
                if(writerFirst) writer = NewWriter(machine);
                var ssi = new RP2040_SSI(machine, frequency: 1000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                if(!writerFirst) writer = NewWriter(machine);
                var callbacks = 0;
                uint observedBusy = 0;
                writer.LimitReached += () =>
                {
                    callbacks++;
                    observedBusy = ssi.ReadDoubleWord(0x28) & 1;
                    machine.SystemBus.WriteDoubleWord(SsiAddress + 0x60, 0xbb);
                };
                ssi.WriteDoubleWord(0, 7u << 16);
                ssi.WriteDoubleWord(0x14, 2);
                ssi.WriteDoubleWord(0x10, 1);
                ssi.WriteDoubleWord(8, 1);
                machine.SystemBus.WriteDoubleWord(SsiAddress + 0x60, 0xaa);
                writer.Enabled = true;
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(16));
                var firstBoundary = target.Bytes.ToArray();
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(16));
                TestContext.WriteLine($"writerFirst={writerFirst} callbacks={callbacks} firstBoundary={System.BitConverter.ToString(firstBoundary)} final={System.BitConverter.ToString(target.Bytes.ToArray())}");
                Assert.Multiple(() =>
                {
                    Assert.AreEqual(1, callbacks);
                    Assert.AreEqual(writerFirst ? 1u : 0u, observedBusy, "BUSY remains set through the frame commit");
                    CollectionAssert.AreEqual(new byte[] { 0xaa }, firstBoundary, "The frame already being shifted must retain its original payload");
                    CollectionAssert.AreEqual(new byte[] { 0xaa, 0xbb }, target.Bytes, "The newly queued frame must follow exactly once");
                });
            }
        }

        [Test]
        public void ShouldCancelAQueuedCompletionWithoutCompletingTheReplacementEarly(
            [Values(false, true)] bool reset, [Values(false, true)] bool rearm,
            [Values(false, true)] bool writerFirst)
        {
            using(var machine = new Machine())
            {
                LimitTimer writer = writerFirst ? NewWriter(machine) : null;
                var ssi = new RP2040_SSI(machine, frequency: 1000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                if(!writerFirst) writer = NewWriter(machine);
                writer.LimitReached += () =>
                {
                    if(reset) ssi.Reset();
                    else ssi.WriteDoubleWord(8, 0);
                    if(rearm)
                    {
                        Configure(ssi);
                        ssi.WriteDoubleWord(0x60, 0xbb);
                    }
                };
                Configure(ssi);
                ssi.WriteDoubleWord(0x60, 0xaa);
                writer.Enabled = true;
                var clock = (BaseClockSource)machine.ClockSource;
                clock.Advance(TimeInterval.FromMicroseconds(16));
                var prefix = writerFirst ? new byte[0] : new byte[] { 0xaa };
                CollectionAssert.AreEqual(prefix, target.Bytes, "A queued old callback must not send replacement data");
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x24), "Canceled transfer must not leak receive data");
                Assert.AreEqual(rearm ? 1u : 0u, ssi.ReadDoubleWord(0x28) & 1);
                clock.Advance(TimeInterval.FromMicroseconds(15));
                CollectionAssert.AreEqual(prefix, target.Bytes, "A replacement frame retains its full serial duration");
                clock.Advance(TimeInterval.FromMicroseconds(1));
                var expected = new List<byte>(prefix);
                if(rearm) expected.Add(0xbb);
                CollectionAssert.AreEqual(expected, target.Bytes);
                Assert.AreEqual(rearm ? 1u : 0u, ssi.ReadDoubleWord(0x24));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            }
        }

        [TestCase(2000000ul, 12000ul, 8000ul)]
        [TestCase(500000ul, 24000ul, 32000ul)]
        [TestCase(3000000ul, 10667ul, 5334ul)]
        public void ShouldRetainFrameTimingWhenFrequencyChanges(ulong frequency, ulong firstDeadline, ulong secondDuration)
        {
            using(var machine = new Machine())
            {
                var ssi = new RP2040_SSI(machine, frequency: 1000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                Configure(ssi);
                ssi.WriteDoubleWord(0x60, 0xaa);
                ssi.WriteDoubleWord(0x60, 0xbb);
                var clock = (BaseClockSource)machine.ClockSource;
                clock.Advance(TimeInterval.FromNanoseconds(8000));
                ssi.Frequency = frequency;
                clock.Advance(TimeInterval.FromNanoseconds(firstDeadline - 8001));
                Assert.AreEqual(1u, ssi.ReadDoubleWord(0x28) & 1);
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x24));
                CollectionAssert.IsEmpty(target.Bytes);
                clock.Advance(TimeInterval.FromNanoseconds(1));
                CollectionAssert.AreEqual(new byte[] { 0xaa }, target.Bytes);
                Assert.AreEqual(0xaau, ssi.ReadDoubleWord(0x60));
                clock.Advance(TimeInterval.FromNanoseconds(secondDuration - 1));
                CollectionAssert.AreEqual(new byte[] { 0xaa }, target.Bytes);
                Assert.AreEqual(1u, ssi.ReadDoubleWord(0x28) & 1);
                clock.Advance(TimeInterval.FromNanoseconds(1));
                CollectionAssert.AreEqual(new byte[] { 0xaa, 0xbb }, target.Bytes);
                Assert.AreEqual(0xbbu, ssi.ReadDoubleWord(0x60));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            }
        }

        [Test]
        public void ShouldKeepQueuedFramesAcrossFractionalReadPolls()
        {
            using(var machine = new Machine())
            {
                var ssi = new RP2040_SSI(machine, frequency: 3000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                Configure(ssi);
                ssi.WriteDoubleWord(0x60, 0xaa);
                ssi.WriteDoubleWord(0x60, 0xbb);
                var clock = (BaseClockSource)machine.ClockSource;
                clock.Advance(TimeInterval.FromNanoseconds(5320));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x24));
                Assert.AreEqual(1u, ssi.ReadDoubleWord(0x28) & 1);
                clock.Advance(TimeInterval.FromNanoseconds(13));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x24));
                clock.Advance(TimeInterval.FromNanoseconds(1));
                CollectionAssert.AreEqual(new byte[] { 0xaa }, target.Bytes);
                Assert.AreEqual(0xaau, ssi.ReadDoubleWord(0x60));
                clock.Advance(TimeInterval.FromNanoseconds(5333));
                CollectionAssert.AreEqual(new byte[] { 0xaa }, target.Bytes);
                clock.Advance(TimeInterval.FromNanoseconds(1));
                CollectionAssert.AreEqual(new byte[] { 0xaa, 0xbb }, target.Bytes);
                Assert.AreEqual(0xbbu, ssi.ReadDoubleWord(0x60));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            }
        }

        [Test]
        public void ShouldRetainFractionalTimerProgressAfterDisableAndRearm()
        {
            using(var machine = new Machine())
            {
                var ssi = new RP2040_SSI(machine, frequency: 3000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                Configure(ssi);
                ssi.WriteDoubleWord(0x60, 0xaa);
                var clock = (BaseClockSource)machine.ClockSource;
                clock.Advance(TimeInterval.FromNanoseconds(500));
                ssi.WriteDoubleWord(8, 0);
                ssi.WriteDoubleWord(8, 1);
                ssi.WriteDoubleWord(0x60, 0xbb);
                // LimitTimer.ResetValue preserves the half-cycle already
                // accumulated at 500 ns, so 15.5 cycles take 5166.667 ns.
                clock.Advance(TimeInterval.FromNanoseconds(5166));
                CollectionAssert.IsEmpty(target.Bytes);
                clock.Advance(TimeInterval.FromNanoseconds(1));
                CollectionAssert.AreEqual(new byte[] { 0xbb }, target.Bytes);
                Assert.AreEqual(0xbbu, ssi.ReadDoubleWord(0x60));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            }
        }

        [TestCase(true)] [TestCase(false)]
        public void ShouldReleaseForcedChipSelectAfterTheFrameCommit(bool writerFirst)
        {
            using(var machine = new Machine())
            {
                var writer = writerFirst ? NewWriter(machine) : null;
                var ssi = new RP2040_SSI(machine, frequency: 1000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                if(!writerFirst) writer = NewWriter(machine);
                writer.LimitReached += () => ssi.ChipSelectOverride = 0;
                Configure(ssi);
                ssi.ChipSelectOverride = 2;
                ssi.WriteDoubleWord(0x60, 0xaa);
                writer.Enabled = true;
                ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(16));
                CollectionAssert.AreEqual(new byte[] { 0xaa }, target.Bytes);
                CollectionAssert.AreEqual(new[] { 1 }, target.FinishPositions,
                    "Chip select must not finish the transaction before its byte reaches the wire");
            }
        }

        [Test]
        public void ShouldDiscardDummyWaitStateWhenDisabledAndRearmed()
        {
            using(var machine = new Machine())
            {
                var ssi = new RP2040_SSI(machine, frequency: 1000000);
                machine.SystemBus.Register(ssi, new BusPointRegistration(SsiAddress));
                var target = new RecordingTarget();
                ssi.Register(target, 0);
                ssi.WriteDoubleWord(0, (2u << 21) | (31u << 16) | (3u << 8));
                ssi.WriteDoubleWord(0x14, 2);
                ssi.WriteDoubleWord(0xf4, 1u | (8u << 2) | (2u << 8) | (4u << 11));
                ssi.WriteDoubleWord(0x10, 1);
                ssi.WriteDoubleWord(8, 1);
                ssi.WriteDoubleWord(0x60, 0xeb);
                ssi.WriteDoubleWord(0x60, 0x000100a0);
                var clock = (BaseClockSource)machine.ClockSource;
                clock.Advance(TimeInterval.FromMicroseconds(36));
                Assert.AreEqual(1u, ssi.ReadDoubleWord(0x28) & 1, "Enhanced read is waiting through dummy clocks");
                var prefix = target.Bytes.ToArray();
                ssi.WriteDoubleWord(8, 0);
                Configure(ssi);
                ssi.WriteDoubleWord(0x60, 0xbb);
                clock.Advance(TimeInterval.FromMicroseconds(15));
                CollectionAssert.AreEqual(prefix, target.Bytes);
                clock.Advance(TimeInterval.FromMicroseconds(1));
                var expected = new List<byte>(prefix) { 0xbb };
                CollectionAssert.AreEqual(expected, target.Bytes);
                Assert.AreEqual(1u, ssi.ReadDoubleWord(0x24));
                Assert.AreEqual(0xbbu, ssi.ReadDoubleWord(0x60));
                Assert.AreEqual(0u, ssi.ReadDoubleWord(0x28) & 1);
            }
        }

        private static void Configure(RP2040_SSI ssi)
        {
            ssi.WriteDoubleWord(0, 7u << 16);
            ssi.WriteDoubleWord(0x14, 2);
            ssi.WriteDoubleWord(0x10, 1);
            ssi.WriteDoubleWord(8, 1);
        }

        private static LimitTimer NewWriter(Machine machine) => new LimitTimer(machine.ClockSource, 1000000, null, "clocked bus master", limit: 16, workMode: WorkMode.OneShot, eventEnabled: true);
        private sealed class RecordingTarget : ISPIPeripheral
        {
            public byte Transmit(byte value) { Bytes.Add(value); return value; }
            public void FinishTransmission() { FinishPositions.Add(Bytes.Count); }
            public void Reset() { Bytes.Clear(); FinishPositions.Clear(); }
            public readonly List<int> FinishPositions = new List<int>();
            public readonly List<byte> Bytes = new List<byte>();
        }
        private const ulong SsiAddress = 0x18000000;
    }
}
