//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.USB;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities.Packets;

using NUnit.Framework;

using USBDirection = Antmicro.Renode.Core.USB.Direction;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_USBTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            dpram = new RP2_USB_DPRAM();
            clocks = new ControlledClockSource();
            usb = new RP2_USB(machine, dpram, clocks);
            usb.WriteDoubleWord(0x40, 1);
            usb.WriteDoubleWord(0x4c, 0x20010000);
            connection = usb.ConnectUSB();
            usb.WriteDoubleWord(0x50, 1u << 19);
        }

        [TearDown]
        public void TearDown()
        {
            connection.Dispose();
            machine.Dispose();
        }

        [Test]
        public void ShouldDeliverSetupPacketToFirmwareWithMaskedInterrupt()
        {
            var setup = new SetupPacket { Direction = USBDirection.DeviceToHost, Request = 6, Value = 0x100, Count = 18 };
            connection.ConnectEndpointSetup(0).SetupPacketWrite(setup);
            Assert.That(dpram.ReadBytes(0, 8), Is.EqualTo(Packet.Encode(setup)));
            Assert.That(usb.ReadDoubleWord(0x8c), Is.EqualTo(1u << 16));
            Assert.That(usb.IRQ.IsSet, Is.False);
            usb.WriteDoubleWord(0x90, 1u << 16);
            Assert.That(usb.IRQ.IsSet, Is.True);
            usb.WriteDoubleWord(0x3050, 1u << 17);
            Assert.That(usb.IRQ.IsSet, Is.False);
        }

        [Test]
        public void ShouldForceInterruptWithoutEnableMask()
        {
            Assert.That(usb.ReadDoubleWord(0x90), Is.Zero);
            usb.WriteDoubleWord(0x2094, 1u << 16);
            Assert.That(usb.ReadDoubleWord(0x8c), Is.Zero);
            Assert.That(usb.ReadDoubleWord(0x98), Is.EqualTo(1u << 16));
            Assert.That(usb.IRQ.IsSet, Is.True);
            usb.WriteDoubleWord(0x3094, 1u << 16);
            Assert.That(usb.IRQ.IsSet, Is.False);
        }

        [Test]
        public void ShouldWaitForFirmwareToMakeAnInputBufferAvailable()
        {
            var input = connection.ConnectEndpointRead(0);
            dpram.WriteBytes(0x100, new byte[] { 0x12, 0x34, 0x56 });
            dpram.WriteDoubleWord(0x80, 0x8003);
            Assert.That(input.TryRead(out _), Is.False);
            dpram.WriteDoubleWord(0x80, 0x8403);
            Assert.That(input.TryRead(out var bytes), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x12, 0x34, 0x56 }));
            Assert.That(dpram.ReadWord(0x80), Is.EqualTo(3));
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(1));
            Assert.That(input.TryRead(out _), Is.False);
        }

        [Test]
        public void ShouldCompleteOutputOnlyAfterFirmwareArmsBuffer()
        {
            var output = connection.ConnectEndpointWrite(0);
            output.Write(new byte[] { 0xa5, 0x5a });
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            dpram.WriteDoubleWord(0x84, 0x0440);
            Assert.That(dpram.ReadBytes(0x100, 2), Is.EqualTo(new byte[] { 0xa5, 0x5a }));
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x8002));
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(2));
        }

        [Test]
        public void ShouldNotOverwriteFullOutputBuffer()
        {
            dpram.WriteBytes(0x100, new byte[] { 0x42 });
            dpram.WriteDoubleWord(0x84, 0x8440);
            connection.ConnectEndpointWrite(0).Write(new byte[] { 0xa5 });
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(0x42));
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            dpram.WriteWord(0x84, 0x0440);
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(0xa5));
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(2));
        }

        [Test]
        public void ShouldRequireStallArmForEndpointZero()
        {
            var input = connection.ConnectEndpointRead(0);
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8c01);
            Assert.That(input.TryRead(out _), Is.True);
            dpram.WriteDoubleWord(0x80, 0x8c01);
            usb.WriteDoubleWord(0x68, 1);
            Assert.That(input.TryRead(out _), Is.False);
            usb.WriteDoubleWord(0x3068, 1);
            Assert.That(input.TryRead(out var bytes), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x42 }));
        }

        [Test]
        public void ShouldInterruptAfterBothInputBuffersWhenConfiguredPerPair()
        {
            // EP1 IN, double buffered, interrupt once per pair, payload at 0x180.
            dpram.WriteDoubleWord(8, 0xd8000180);
            dpram.WriteBytes(0x180, new byte[] { 1 });
            dpram.WriteBytes(0x1c0, new byte[] { 2 });
            dpram.WriteDoubleWord(0x88, 0x84018401);
            var input = connection.ConnectEndpointRead(1);
            Assert.That(input.TryRead(out var first), Is.True);
            Assert.That(first, Is.EqualTo(new byte[] { 1 }));
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            Assert.That(input.TryRead(out var second), Is.True);
            Assert.That(second, Is.EqualTo(new byte[] { 2 }));
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(4));
        }

        [Test]
        public void ShouldHandleZeroLengthStatusPackets()
        {
            connection.ConnectEndpointWrite(0).Write(new byte[0]);
            dpram.WriteDoubleWord(0x84, 0x0440);
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x8000));
            dpram.WriteDoubleWord(0x80, 0x8400);
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out var bytes), Is.True);
            Assert.That(bytes, Is.Empty);
        }

        [Test]
        public void ShouldDiscardPendingInputOnNewSetup()
        {
            dpram.WriteBytes(0x100, new byte[] { 0x42 });
            dpram.WriteDoubleWord(0x80, 0x8401);
            connection.ConnectEndpointSetup(0).SetupPacketWrite(new SetupPacket { Request = 6, Count = 18 });
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out _), Is.False);
        }

        [Test]
        public void ShouldHoldAbortedEndpointUntilAbortClears()
        {
            usb.WriteDoubleWord(0x2060, 1);
            Assert.That(usb.ReadDoubleWord(0x64), Is.EqualTo(1));
            dpram.WriteBytes(0x100, new byte[] { 0x42 });
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out _), Is.False);
            usb.WriteDoubleWord(0x3064, 1);
            usb.WriteDoubleWord(0x3060, 1);
            Assert.That(usb.ReadDoubleWord(0x64), Is.Zero);
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out var bytes), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x42 }));
        }

        [Test]
        public void ShouldGenerateFramesOnlyWhileConnected()
        {
            usb.WriteDoubleWord(0x90, 1u << 17);
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(3));
            Assert.That(usb.IRQ.IsSet, Is.True);
            Assert.That(usb.ReadDoubleWord(0x48), Is.EqualTo(3));
            Assert.That(usb.IRQ.IsSet, Is.False);
            connection.Dispose();
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMilliseconds(3));
            Assert.That(usb.ReadDoubleWord(0x48), Is.EqualTo(3));
        }

        [Test]
        public void ShouldFreezeFramesAndPreserveFramePhaseWhileClockStopped()
        {
            AdvanceMicroseconds(500);
            clocks.Set(125000000, 0);
            AdvanceMicroseconds(5000);
            Assert.That(usb.ReadDoubleWord(0x48), Is.Zero);
            clocks.Set(125000000, 48000000);
            AdvanceMicroseconds(499);
            Assert.That(usb.ReadDoubleWord(0x48), Is.Zero);
            AdvanceMicroseconds(1);
            Assert.That(usb.ReadDoubleWord(0x48), Is.EqualTo(1));
        }

        [TestCase(0ul, 48000000ul)]
        [TestCase(125000000ul, 0ul)]
        [TestCase(125000000ul, 24000000ul)]
        public void ShouldFreezePacketOwnershipAndPendingStatusWhenClocksUnavailable(ulong system, ulong usbFrequency)
        {
            usb.WriteDoubleWord(0, 37);
            usb.WriteDoubleWord(0x2094, 1u << 16);
            dpram.WriteBytes(0x100, new byte[] { 0x42 });
            dpram.WriteDoubleWord(0x80, 0x8401);
            var control = dpram.ReadDoubleWord(0x80);
            clocks.Set(system, usbFrequency);
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out _), Is.False);
            connection.ConnectEndpointSetup(0).SetupPacketWrite(new SetupPacket { Request = 6 });
            connection.ConnectEndpointWrite(0).Write(new byte[] { 0xa5 });
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadDoubleWord(0x80), Is.EqualTo(control));
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(0x42));
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            Assert.That(usb.ReadDoubleWord(0x8c) & (1u << 16), Is.Zero);
            Assert.That(usb.IRQ.IsSet, Is.True);
            usb.WriteDoubleWord(0, 38);
            Assert.That(usb.Address, Is.EqualTo(38));
            clocks.Set(125000000, 48000000);
            Assert.That(usb.Address, Is.EqualTo(38));
            Assert.That(usb.ReadDoubleWord(0x50) & (1u << 19), Is.Zero);
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(2));
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(0xa5));
        }

        [Test]
        public void ShouldRetainCompletedBufferInterruptWhileClockStopped()
        {
            dpram.WriteBytes(0x100, new byte[] { 0x42 });
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.IsTrue(connection.ConnectEndpointRead(0).TryRead(out _));
            usb.WriteDoubleWord(0x90, 1u << 4);
            clocks.Set(125000000, 0);
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(1));
            Assert.That(usb.IRQ.IsSet, Is.True);
            clocks.Set(125000000, 48000000);
            Assert.That(usb.ReadDoubleWord(0x58), Is.EqualTo(1));
            Assert.That(usb.IRQ.IsSet, Is.True);
        }

        [Test]
        public void ShouldObserveHostAttachOnlyAfterAValidClockBecomesAvailable()
        {
            connection.Dispose();
            clocks.Set(125000000, 0);
            usb.WriteDoubleWord(0x50, 1u << 19);
            usb.WriteDoubleWord(0, 23);
            connection = usb.ConnectUSB();
            Assert.That(usb.Address, Is.EqualTo(23));
            Assert.That(usb.ReadDoubleWord(0x50) & (1u << 19), Is.Zero);
            clocks.Set(125000000, 48000000);
            Assert.That(usb.Address, Is.Zero);
            Assert.That(usb.ReadDoubleWord(0x50) & (1u << 19), Is.Not.Zero);
            usb.WriteDoubleWord(0x50, 1u << 19);
            usb.WriteDoubleWord(0, 24);
            clocks.Set(125000000, 0);
            clocks.Set(125000000, 48000000);
            Assert.That(usb.Address, Is.EqualTo(24));
            Assert.That(usb.ReadDoubleWord(0x50) & (1u << 19), Is.Zero);
        }

        [Test]
        public void ShouldDiscardTrafficReceivedDuringResetAndPreserveHostAttachment()
        {
            connection.ConnectEndpointWrite(0).Write(new byte[] { 0xa5 });
            usb.ResetHeld = true;
            connection.ConnectEndpointWrite(0).Write(new byte[] { 0x5a });
            connection.ConnectEndpointSetup(0).SetupPacketWrite(new SetupPacket { Request = 6 });
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out _), Is.False);
            Assert.That(usb.IRQ.IsSet, Is.False);
            usb.ResetHeld = false;
            usb.WriteDoubleWord(0x40, 1);
            usb.WriteDoubleWord(0x4c, 0x20010000);
            Assert.That(usb.ReadDoubleWord(0x50) & (1u << 19), Is.Not.Zero);
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            connection.ConnectEndpointWrite(0).Write(new byte[] { 0x33 });
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(0x33));
        }

        [Test]
        public void ShouldNotNotifyInputAvailabilityUntilClocksResume()
        {
            var input = connection.ConnectEndpointRead(0);
            var notifications = 0;
            input.NewPacket += () => notifications++;
            clocks.Set(125000000, 0);
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.That(notifications, Is.Zero);
            clocks.Set(125000000, 48000000);
            Assert.That(notifications, Is.EqualTo(1));
            Assert.That(input.TryRead(out var bytes), Is.True);
            Assert.That(bytes, Is.EqualTo(new byte[] { 0x42 }));
            Assert.That(input.TryRead(out _), Is.False);
            Assert.That(notifications, Is.EqualTo(1));
        }

        [Test]
        public void ShouldPreserveHostPendingOutputOrderAcrossClockStopExactlyOnce()
        {
            clocks.Set(125000000, 0);
            var output = connection.ConnectEndpointWrite(0);
            output.Write(new byte[] { 1 });
            output.Write(new byte[] { 2 });
            dpram.WriteDoubleWord(0x84, 0x440);
            clocks.Set(125000000, 48000000);
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(1));
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x8001));
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(2));
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x8001));
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
        }

        [Test]
        public void ShouldCancelHostPendingOutputOnPhysicalDisconnect()
        {
            clocks.Set(125000000, 0);
            connection.ConnectEndpointWrite(0).Write(new byte[] { 0xa5 });
            connection.Dispose();
            dpram.WriteDoubleWord(0x84, 0x440);
            clocks.Set(125000000, 48000000);
            connection = usb.ConnectUSB();
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
        }

        [Test]
        public void ShouldNotObserveHostAttachWhileResetIsHeld()
        {
            connection.Dispose();
            usb.ResetHeld = true;
            connection = usb.ConnectUSB();
            AdvanceMicroseconds(2000);
            Assert.That(usb.ReadDoubleWord(0x50), Is.Zero);
            Assert.That(usb.ReadDoubleWord(0x48), Is.Zero);
            usb.ResetHeld = false;
            Assert.That(usb.ReadDoubleWord(0x50), Is.Zero);
            usb.WriteDoubleWord(0x40, 1);
            usb.WriteDoubleWord(0x4c, 0x20010000);
            Assert.That(usb.ReadDoubleWord(0x50) & (1u << 19), Is.Not.Zero);
        }

        [Test]
        public void ShouldRunWithALowNonzeroSystemClockWithoutInventingDeterministicEventLoss()
        {
            clocks.Set(12000000, 48000000);
            AdvanceMicroseconds(1000);
            Assert.That(usb.ReadDoubleWord(0x48), Is.EqualTo(1));
            dpram.WriteBytes(0x100, new byte[] { 0x42 });
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.That(connection.ConnectEndpointRead(0).TryRead(out _), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldPublishInputNotificationsOutsideDpramMonitor(bool restoreClock)
        {
            var input = connection.ConnectEndpointRead(0);
            var notifications = 0;
            input.NewPacket += () =>
            {
                Assert.IsFalse(Monitor.IsEntered(dpram));
                notifications++;
            };
            if(restoreClock) clocks.Set(125000000, 0);
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8401);
            if(restoreClock)
            {
                Assert.AreEqual(0, notifications);
                clocks.Set(125000000, 48000000);
            }
            Assert.AreEqual(1, notifications);
        }

        [Test]
        public void ShouldAllowAnotherThreadToReadDpramDuringInputNotification()
        {
            connection.ConnectEndpointRead(0).NewPacket += () =>
            {
                var read = Task.Run(() => dpram.ReadByte(0x100));
                Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(1)), "Input notification retained the DPRAM state monitor");
                Assert.AreEqual(0x42, read.Result);
            };
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8401);
        }

        [Test]
        public void ShouldDeliverRearmedInputWithoutNestedCallbacksOrLosingTheNotification()
        {
            var input = connection.ConnectEndpointRead(0);
            var packets = new List<byte>();
            var depth = 0;
            var maximumDepth = 0;
            input.NewPacket += () =>
            {
                depth++;
                maximumDepth = Math.Max(maximumDepth, depth);
                try
                {
                    Assert.IsTrue(input.TryRead(out var packet));
                    packets.Add(packet[0]);
                    if(packets.Count == 1)
                    {
                        dpram.WriteByte(0x100, 2);
                        dpram.WriteDoubleWord(0x80, 0x8401);
                    }
                }
                finally
                {
                    depth--;
                }
            };
            dpram.WriteByte(0x100, 1);
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.That(packets, Is.EqualTo(new byte[] { 1, 2 }));
            Assert.AreEqual(1, maximumDepth);
            Assert.IsFalse(input.TryRead(out _));
        }

        [Test]
        public void ShouldInvalidateOldInputHandlesWhenAConnectionIsDisposed()
        {
            var oldInput = connection.ConnectEndpointRead(0);
            connection.Dispose();
            connection = usb.ConnectUSB();
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.IsFalse(oldInput.TryRead(out _));
            Assert.IsTrue(connection.ConnectEndpointRead(0).TryRead(out var packet));
            Assert.That(packet, Is.EqualTo(new byte[] { 0x42 }));
        }

        [Test]
        public void ShouldDetachOldPendingReadsBeforeAnotherConnectionUsesTheEndpoint()
        {
            var oldInput = connection.ConnectEndpointRead(0);
            var oldNotifications = 0;
            oldInput.NewPacket += () => oldNotifications++;
            oldInput.ReadPacket(_ => { });
            connection.Dispose();
            connection = usb.ConnectUSB();
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.AreEqual(0, oldNotifications);
            Assert.IsTrue(connection.ConnectEndpointRead(0).TryRead(out var packet));
            Assert.That(packet, Is.EqualTo(new byte[] { 0x42 }));
        }

        [Test]
        public void ShouldIgnoreOldOutputHandlesAfterReconnect()
        {
            var oldOutput = connection.ConnectEndpointWrite(0);
            connection.Dispose();
            connection = usb.ConnectUSB();
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x84, 0x440);
            usb.WriteDoubleWord(0x50, 1u << 19);
            oldOutput.Write(new byte[] { 0xa5 });
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(0x42));
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
            Assert.That(usb.ReadDoubleWord(0x58), Is.Zero);
            Assert.That(usb.ReadDoubleWord(0x8c), Is.Zero);
        }

        [Test]
        public void ShouldIgnoreOldSetupHandlesAfterReconnect()
        {
            var oldSetup = connection.ConnectEndpointSetup(0);
            connection.Dispose();
            connection = usb.ConnectUSB();
            usb.WriteDoubleWord(0x50, 1u << 19);
            var before = dpram.ReadBytes(0, 8);
            oldSetup.SetupPacketWrite(new SetupPacket { Request = 6, Count = 18 });
            Assert.That(dpram.ReadBytes(0, 8), Is.EqualTo(before));
            Assert.That(usb.ReadDoubleWord(0x8c), Is.Zero);
        }

        [Test]
        public void ShouldCancelOnlyTheDisposedConnectionsHostPendingOutput()
        {
            using(var other = usb.ConnectUSB())
            {
                clocks.Set(125000000, 0);
                connection.ConnectEndpointWrite(0).Write(new byte[] { 1 });
                other.ConnectEndpointWrite(0).Write(new byte[] { 2 });
                connection.Dispose();
                dpram.WriteDoubleWord(0x84, 0x440);
                clocks.Set(125000000, 48000000);
                Assert.That(dpram.ReadByte(0x100), Is.EqualTo(2));
                Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x8001));
                dpram.WriteDoubleWord(0x84, 0x440);
                Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
            }
        }

        [Test]
        public void ShouldPreserveActiveInputSubscriptionsAcrossControllerReset()
        {
            var input = connection.ConnectEndpointRead(0);
            var notifications = 0;
            input.NewPacket += () => notifications++;
            usb.ResetHeld = true;
            usb.ResetHeld = false;
            usb.WriteDoubleWord(0x40, 1);
            usb.WriteDoubleWord(0x4c, 0x20010000);
            dpram.WriteByte(0x100, 0x42);
            dpram.WriteDoubleWord(0x80, 0x8401);
            Assert.AreEqual(1, notifications);
            Assert.IsTrue(input.TryRead(out var packet));
            Assert.That(packet, Is.EqualTo(new byte[] { 0x42 }));
        }

        [Test]
        public void ShouldCompleteAsyncOutputOnlyAfterFirmwareAcceptsEveryPacket()
        {
            var data = new byte[128];
            for(var i = 0; i < data.Length; i++) data[i] = (byte)i;
            var write = connection.ConnectEndpointWrite(0).WriteAsync(data, CancellationToken.None);
            Assert.That(write.IsCompleted, Is.False);
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(write.IsCompleted, Is.False);
            Assert.That(dpram.ReadByte(0x100), Is.Zero);
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(write.Wait(TimeSpan.FromSeconds(1)), Is.True);
            Assert.That(dpram.ReadByte(0x100), Is.EqualTo(64));
        }

        [Test]
        public void ShouldCancelAsyncOutputBeforeAnUnavailableBufferIsArmed()
        {
            using(var cancellation = new CancellationTokenSource())
            {
                var write = connection.ConnectEndpointWrite(0).WriteAsync(new byte[] { 0xa5 }, cancellation.Token);
                Assert.That(write.IsCompleted, Is.False);
                cancellation.Cancel();
                Assert.That(SpinWait.SpinUntil(() => write.IsCanceled, 1000), Is.True);
                dpram.WriteDoubleWord(0x84, 0x440);
                Assert.That(dpram.ReadByte(0x100), Is.Zero);
                Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
            }
        }

        [Test]
        public void ShouldCancelOnlyTheUnacceptedSuffixOfAsyncOutput()
        {
            using(var cancellation = new CancellationTokenSource())
            {
                var data = new byte[128];
                for(var i = 0; i < data.Length; i++) data[i] = (byte)i;
                var write = connection.ConnectEndpointWrite(0).WriteAsync(data, cancellation.Token);
                dpram.WriteDoubleWord(0x84, 0x440);
                Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x8040));
                cancellation.Cancel();
                Assert.That(SpinWait.SpinUntil(() => write.IsCanceled, 1000), Is.True);
                dpram.WriteDoubleWord(0x84, 0x440);
                Assert.That(dpram.ReadByte(0x100), Is.Zero);
                Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
            }
        }

        [Test]
        public void ShouldCancelAsyncOutputWhenItsConnectionIsDisposed()
        {
            var write = connection.ConnectEndpointWrite(0).WriteAsync(new byte[] { 0xa5 });
            Assert.That(write.IsCompleted, Is.False);
            connection.Dispose();
            Assert.That(SpinWait.SpinUntil(() => write.IsCanceled, 1000), Is.True);
            connection = usb.ConnectUSB();
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadByte(0x100), Is.Zero);
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
        }

        [Test]
        public void ShouldNotReportSuccessfulAsyncOutputWhileResetHeld()
        {
            usb.ResetHeld = true;
            var write = connection.ConnectEndpointWrite(0).WriteAsync(new byte[] { 0xa5 }, CancellationToken.None);
            Assert.That(write.IsCompletedSuccessfully, Is.False);
        }

        [Test]
        public void ShouldClearEndpointZeroStallArmOnNewSetup()
        {
            usb.WriteDoubleWord(0x68, 3);
            connection.ConnectEndpointSetup(0).SetupPacketWrite(new SetupPacket { Request = 6, Count = 18 });
            Assert.That(usb.ReadDoubleWord(0x68), Is.Zero);
        }

        [Test]
        public void ShouldFailPendingAsyncOutputOnStallWithoutNotifyingLegacyReaders()
        {
            var input = connection.ConnectEndpointRead(0);
            var notifications = 0;
            input.NewPacket += () => notifications++;
            var write = connection.ConnectEndpointWrite(0).WriteAsync(new byte[] { 0xa5 });
            usb.WriteDoubleWord(0x68, 2);
            Assert.DoesNotThrow(() => dpram.WriteDoubleWord(0x84, 0x800));
            Assert.That(SpinWait.SpinUntil(() => write.IsFaulted, 1000), Is.True);
            Assert.That(notifications, Is.Zero);
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadByte(0x100), Is.Zero);
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
        }

        [Test]
        public void ShouldPublishEndpointZeroStallSeparatelyAndOnlyWhenArmed()
        {
            var input = connection.ConnectEndpointRead(0);
            var status = (IUSBPipeStatus)input;
            var statusNotifications = 0;
            var legacyNotifications = 0;
            input.NewPacket += () => legacyNotifications++;
            input.ReadPacket(_ => legacyNotifications++);
            status.StatusChanged += () =>
            {
                Assert.That(Monitor.IsEntered(dpram), Is.False);
                statusNotifications++;
            };
            Assert.DoesNotThrow(() => dpram.WriteDoubleWord(0x80, 0x800));
            Assert.That(status.IsStalled(USBDirection.DeviceToHost), Is.False);
            Assert.That(statusNotifications, Is.Zero);
            Assert.DoesNotThrow(() => usb.WriteDoubleWord(0x68, 1));
            Assert.That(status.IsStalled(USBDirection.DeviceToHost), Is.True);
            Assert.That(input.TryRead(out _), Is.False);
            Assert.That(statusNotifications, Is.EqualTo(1));
            connection.ConnectEndpointSetup(0).SetupPacketWrite(new SetupPacket { Request = 6, Count = 18 });
            Assert.That(status.IsStalled(USBDirection.DeviceToHost), Is.False);
            Assert.That(statusNotifications, Is.EqualTo(2));
            Assert.That(legacyNotifications, Is.Zero);
        }

        [Test]
        public void ShouldRejectOnlyTheUnacceptedOutputSuffixOnStall()
        {
            var data = new byte[128];
            for(var i = 0; i < data.Length; i++) data[i] = (byte)i;
            var write = connection.ConnectEndpointWrite(0).WriteAsync(data);
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadByte(0x100), Is.Zero);
            usb.WriteDoubleWord(0x68, 2);
            Assert.DoesNotThrow(() => dpram.WriteDoubleWord(0x84, 0x800));
            Assert.That(SpinWait.SpinUntil(() => write.IsFaulted, 1000), Is.True);
            Assert.That(write.Exception.InnerException, Is.TypeOf<USBStallException>());
            dpram.WriteDoubleWord(0x84, 0x440);
            Assert.That(dpram.ReadByte(0x100), Is.Zero);
            Assert.That(dpram.ReadWord(0x84), Is.EqualTo(0x440));
        }

        private void AdvanceMicroseconds(ulong duration) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(duration));

        private sealed class ControlledClockSource : IRP2ClockSource
        {
            public ulong SystemFrequency { get; private set; } = 125000000;
            public ulong PeripheralFrequency => SystemFrequency;
            public ulong USBFrequency { get; private set; } = 48000000;
            public event Action ClockChanged;
            public void Set(ulong system, ulong usb)
            {
                SystemFrequency = system;
                USBFrequency = usb;
                ClockChanged?.Invoke();
            }
        }

        private ControlledClockSource clocks;
        private Machine machine;
        private RP2_USB_DPRAM dpram;
        private RP2_USB usb;
        private IUSBConnection connection;
    }
}
