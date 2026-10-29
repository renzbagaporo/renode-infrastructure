//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.USB;
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
            usb = new RP2_USB(machine, dpram);
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

        private Machine machine;
        private RP2_USB_DPRAM dpram;
        private RP2_USB usb;
        private IUSBConnection connection;
    }
}
