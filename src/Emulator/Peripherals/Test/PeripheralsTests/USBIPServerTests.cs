//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.USB;
using Antmicro.Renode.Extensions.Utilities.USBIP;

using USB = Antmicro.Renode.Extensions.Utilities.USB;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Utilities.Packets;

using NUnit.Framework;

using USBDirection = Antmicro.Renode.Core.USB.Direction;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class USBIPServerTests
    {
        [SetUp]
        public void SetUp()
        {
            inputLock = new object();
            EmulationManager.Instance.CurrentEmulation.StartAll();
            server = new USBIPServer(0);
            device = new TestDevice();
            server.Register(device, new NumberRegistrationPoint<int>(0));
            var provider = GetField<SocketServerProvider>(server, "server");
            replies = GetField<ConcurrentQueue<byte[]>>(provider, "queue");
            CacheDescriptors();
            SetURBState();
            Submit(0, URBDirection.Out, 0, 0, setup: new SetupPacket { Request = 9, Value = 1 });
            AssertReply(0, URBCommand.URBReply);
            device.Connection.Setup.SuccessfulReads = 0;
            device.Connection.Setup.Writes.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            server.Dispose();
            EmulationManager.Instance.Clear();
        }

        [Test]
        public void ShouldProcessOutputWhileAnotherEndpointWaitsForInput()
        {
            var input = Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            var output = Submit(2, URBDirection.Out, 2, 1, new byte[] { 0x42 });
            Assert.That(output.Wait(Timeout), Is.True, "A pending IN must not block the socket parser");
            AssertReply(2, URBCommand.URBReply);
            Assert.That(device.Connection.Output.Writes.TryDequeue(out var data), Is.True);
            Assert.That(data, Is.EqualTo(new byte[] { 0x42 }));
        }

        [Test]
        public void ShouldUnlinkInputWithoutLeavingAReaderForLaterPackets()
        {
            Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            var unlink = Feed(new URBHeader { Command = URBCommand.Unlink, SequenceNumber = 2, FlagsOrStatus = 1 }, default(URBRequest));
            Assert.That(unlink.Wait(Timeout), Is.True);
            var reply = AssertReply(2, URBCommand.UnlinkReply);
            Assert.That(unchecked((int)Packet.Decode<URBHeader>(reply).FlagsOrStatus), Is.EqualTo(-104));
            Submit(3, URBDirection.In, 1, 64);
            device.Connection.Input.Push(new byte[] { 0x71 });
            reply = AssertReply(3, URBCommand.URBReply);
            Assert.That(reply.Skip(48), Is.EqualTo(new byte[] { 0x71 }));
            Assert.That(replies.IsEmpty, Is.True, "Unlinked submissions must not also complete");
        }

        [Test]
        public void ShouldUseInputStatusForHostToDeviceControlWithoutData()
        {
            var setup = new SetupPacket { Direction = USBDirection.HostToDevice, Request = 0x22, Count = 0 };
            Submit(1, URBDirection.Out, 0, 0, setup: setup);
            AssertReply(1, URBCommand.URBReply);
            Assert.That(device.Connection.Setup.Writes, Is.Empty, "No OUT data/status packet belongs to this transfer");
            Assert.That(device.Connection.Setup.SuccessfulReads, Is.EqualTo(1), "The device's IN status ZLP must be consumed");
        }

        [Test]
        public void ShouldKeepInputRequestsInSubmissionOrder()
        {
            Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            Submit(2, URBDirection.In, 1, 64);
            device.Connection.Input.Push(new byte[] { 0x11 });
            device.Connection.Input.Push(new byte[] { 0x22 });
            Assert.That(AssertReply(1, URBCommand.URBReply).Skip(48), Is.EqualTo(new byte[] { 0x11 }));
            Assert.That(AssertReply(2, URBCommand.URBReply).Skip(48), Is.EqualTo(new byte[] { 0x22 }));
        }

        [Test]
        public void ShouldNotReadOrReplyAfterReset()
        {
            Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            server.Reset();
            SetURBState();
            Submit(2, URBDirection.In, 1, 64);
            device.Connection.Input.Push(new byte[] { 0x33 });
            Assert.That(AssertReply(2, URBCommand.URBReply).Skip(48), Is.EqualTo(new byte[] { 0x33 }));
            Assert.That(replies.IsEmpty, Is.True);
        }

        [Test]
        public void ShouldProcessControlWhileNotificationInputIsPending()
        {
            Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            Submit(2, URBDirection.Out, 0, 0,
                setup: new SetupPacket { Direction = USBDirection.HostToDevice, Request = 0x22 });
            AssertReply(2, URBCommand.URBReply);
        }

        [Test]
        public void ShouldStopControlReadAtAShortPacket()
        {
            device.Connection.Setup.SetupResponse = Enumerable.Range(0, 18).Select(i => (byte)i).ToArray();
            Submit(1, URBDirection.In, 0, 64,
                setup: new SetupPacket { Direction = USBDirection.DeviceToHost, Request = 6, Count = 64 });
            var reply = AssertReply(1, URBCommand.URBReply);
            Assert.That(reply.Skip(48), Is.EqualTo(device.Connection.Setup.SetupResponse));
            Assert.That(device.Connection.Setup.Writes.Single(), Is.Empty);
        }

        [Test]
        public void ShouldUnlinkQueuedInputWithoutConsumingItsPacket()
        {
            Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            Assert.That(Submit(2, URBDirection.In, 1, 64).Wait(Timeout), Is.True);
            Feed(new URBHeader { Command = URBCommand.Unlink, SequenceNumber = 3, FlagsOrStatus = 2 }, default(URBRequest));
            AssertReply(3, URBCommand.UnlinkReply);
            device.Connection.Input.Push(new byte[] { 0x11 });
            Assert.That(AssertReply(1, URBCommand.URBReply).Skip(48), Is.EqualTo(new byte[] { 0x11 }));
            Submit(4, URBDirection.In, 1, 64);
            device.Connection.Input.Push(new byte[] { 0x44 });
            Assert.That(AssertReply(4, URBCommand.URBReply).Skip(48), Is.EqualTo(new byte[] { 0x44 }));
        }

        [Test]
        public void ShouldReturnZeroWhenUnlinkArrivesAfterCompletion()
        {
            Submit(1, URBDirection.Out, 2, 1, new byte[] { 0x11 });
            AssertReply(1, URBCommand.URBReply);
            Feed(new URBHeader { Command = URBCommand.Unlink, SequenceNumber = 2, FlagsOrStatus = 1 }, default(URBRequest));
            var reply = AssertReply(2, URBCommand.UnlinkReply);
            Assert.That(Packet.Decode<URBHeader>(reply).FlagsOrStatus, Is.Zero);
        }

        [TestCase(USBIPServer.USBSpeed.Full, 2u)]
        [TestCase(USBIPServer.USBSpeed.High, 3u)]
        public void ShouldReportTheRegisteredDeviceSpeed(USBIPServer.USBSpeed speed, uint expected)
        {
            server.Unregister(device);
            server.Register(device, new NumberRegistrationPoint<int>(0), speed);
            var descriptor = (IEnumerable<byte>)typeof(USBIPServer).GetMethod("GenerateDeviceDescriptor",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server, new object[] { device, 0u, false });
            Assert.That(Packet.Decode<DeviceDescriptor>(descriptor.ToArray()).Speed, Is.EqualTo(expected));
        }

        [Test]
        public void ShouldAccumulateBulkPacketsUntilShortPacket()
        {
            Submit(1, URBDirection.In, 1, 128);
            device.Connection.Input.Push(Enumerable.Repeat((byte)0x11, 64).ToArray());
            device.Connection.Input.Push(Enumerable.Repeat((byte)0x22, 16).ToArray());
            Assert.That(AssertReply(1, URBCommand.URBReply).Skip(48),
                Is.EqualTo(Enumerable.Repeat((byte)0x11, 64).Concat(Enumerable.Repeat((byte)0x22, 16))));
        }

        [Test]
        public void ShouldUseDescriptorPacketSizeForBulkTermination()
        {
            device.Connection.Setup.EndpointPacketSize = 8;
            CacheDescriptors();
            Submit(1, URBDirection.In, 1, 32);
            device.Connection.Input.Push(new byte[8]);
            device.Connection.Input.Push(new byte[3]);
            Assert.That(AssertReply(1, URBCommand.URBReply).Length, Is.EqualTo(48 + 11));
        }

        [Test]
        public void ShouldWaitForZeroLengthPacketAfterFullBulkPacket()
        {
            Submit(1, URBDirection.In, 1, 128);
            device.Connection.Input.Push(new byte[64]);
            Assert.That(device.Connection.Input.WaitingAfterPacket.Wait(Timeout), Is.True);
            Assert.That(replies.IsEmpty, Is.True);
            device.Connection.Input.Push(Array.Empty<byte>());
            Assert.That(AssertReply(1, URBCommand.URBReply).Length, Is.EqualTo(48 + 64));
        }

        [Test]
        public void ShouldReturnShortNotOkStatusWithPartialData()
        {
            Feed(new URBHeader { Command = URBCommand.URBRequest, SequenceNumber = 1, BusId = 1,
                Direction = URBDirection.In, EndpointNumber = 1, FlagsOrStatus = 0x201 },
                new URBRequest { TransferBufferLength = 128 });
            device.Connection.Input.Push(new byte[16]);
            var reply = AssertReply(1, URBCommand.URBReply);
            Assert.That(unchecked((int)Packet.Decode<URBHeader>(reply).FlagsOrStatus), Is.EqualTo(-121));
            Assert.That(Packet.Decode<URBReply>(reply, 24).ActualLength, Is.EqualTo(16));
            Assert.That(reply.Length, Is.EqualTo(48 + 16));
        }

        [Test]
        public void ShouldUnlinkPartiallyReadBulkWithoutStealingTheNextPacket()
        {
            Submit(1, URBDirection.In, 1, 128);
            device.Connection.Input.Push(new byte[64]);
            Assert.That(device.Connection.Input.WaitingAfterPacket.Wait(Timeout), Is.True);
            Feed(new URBHeader { Command = URBCommand.Unlink, SequenceNumber = 2, FlagsOrStatus = 1 }, default(URBRequest));
            AssertReply(2, URBCommand.UnlinkReply);
            Submit(3, URBDirection.In, 1, 64);
            device.Connection.Input.Push(new byte[] { 0x33 });
            Assert.That(AssertReply(3, URBCommand.URBReply).Skip(48), Is.EqualTo(new byte[] { 0x33 }));
        }

        [Test]
        public void ShouldUseInputStatusWhenZeroLengthSetupSaysDeviceToHost()
        {
            Submit(1, URBDirection.In, 0, 0,
                setup: new SetupPacket { Direction = USBDirection.DeviceToHost, Request = 0x22 });
            AssertReply(1, URBCommand.URBReply);
            Assert.That(device.Connection.Setup.SuccessfulReads, Is.EqualTo(1));
            Assert.That(device.Connection.Setup.Writes.IsEmpty, Is.True);
        }

        [Test]
        public void ShouldCancelUnacceptedRp2OutputBeforeFirmwareArmsBuffer()
        {
            using(var machine = new Machine())
            {
                var dpram = new RP2_USB_DPRAM();
                var usb = new RP2_USB(machine, dpram, new TestClocks());
                usb.WriteDoubleWord(0x40, 1);
                usb.WriteDoubleWord(0x4c, 0x20010000);
                dpram.WriteDoubleWord(0x14, 0xa0000180); // EP2 OUT, one 64-byte buffer.
                var recording = new RecordingDevice(usb);
                server.Register(recording, new NumberRegistrationPoint<int>(1));
                Feed(new URBHeader { Command = URBCommand.URBRequest, SequenceNumber = 1, BusId = 1,
                    DeviceId = 1, Direction = URBDirection.Out, EndpointNumber = 2 },
                    new URBRequest { TransferBufferLength = 1 }, new byte[] { 0xa5 });
                Assert.That(recording.WriteStarted.Wait(Timeout), Is.True);
                Feed(new URBHeader { Command = URBCommand.Unlink, SequenceNumber = 2, FlagsOrStatus = 1 }, default(URBRequest));
                AssertReply(2, URBCommand.UnlinkReply);
                dpram.WriteDoubleWord(0x94, 0x440);
                Assert.That(dpram.ReadByte(0x180), Is.Zero);
                Assert.That(dpram.ReadWord(0x94), Is.EqualTo(0x440));
                Assert.That(replies.IsEmpty, Is.True);
                server.Unregister(recording);
            }
        }

        [Test]
        public void ShouldReturnBrokenPipeForPendingInputStall()
        {
            Submit(1, URBDirection.In, 1, 64);
            Assert.That(device.Connection.Input.ReadAttempted.Wait(Timeout), Is.True);
            device.Connection.Input.StallInput();
            var reply = AssertReply(1, URBCommand.URBReply);
            Assert.That(unchecked((int)Packet.Decode<URBHeader>(reply).FlagsOrStatus), Is.EqualTo(-32));
            Assert.That(reply.Length, Is.EqualTo(48));
        }

        [Test]
        public void ShouldReturnBrokenPipeAndDiscardRejectedRp2OutputOnStall()
        {
            using(var machine = new Machine())
            {
                var dpram = new RP2_USB_DPRAM();
                var usb = new RP2_USB(machine, dpram, new TestClocks());
                usb.WriteDoubleWord(0x40, 1);
                usb.WriteDoubleWord(0x4c, 0x20010000);
                dpram.WriteDoubleWord(0x14, 0xa0000180);
                var recording = new RecordingDevice(usb);
                server.Register(recording, new NumberRegistrationPoint<int>(1));
                Feed(new URBHeader { Command = URBCommand.URBRequest, SequenceNumber = 1, BusId = 1,
                    DeviceId = 1, Direction = URBDirection.Out, EndpointNumber = 2 },
                    new URBRequest { TransferBufferLength = 1 }, new byte[] { 0xa5 });
                Assert.That(recording.WriteStarted.Wait(Timeout), Is.True);
                Assert.DoesNotThrow(() => dpram.WriteDoubleWord(0x94, 0x800));
                var reply = AssertReply(1, URBCommand.URBReply);
                Assert.That(unchecked((int)Packet.Decode<URBHeader>(reply).FlagsOrStatus), Is.EqualTo(-32));
                dpram.WriteDoubleWord(0x94, 0x440);
                Assert.That(dpram.ReadByte(0x180), Is.Zero);
                Assert.That(dpram.ReadWord(0x94), Is.EqualTo(0x440));
                server.Unregister(recording);
            }
        }

        private void CacheDescriptors()
        {
            var descriptors = (IEnumerable<byte>)typeof(USBIPServer).GetMethod("GenerateDeviceDescriptor",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server, new object[] { device, 0u, true });
            descriptors.ToArray();
        }

        private sealed class TestClocks : IRP2ClockSource
        {
            public ulong SystemFrequency => 125000000;
            public ulong PeripheralFrequency => SystemFrequency;
            public ulong USBFrequency => 48000000;
            public event Action ClockChanged { add { } remove { } }
        }

        private sealed class RecordingDevice : IUSBDevice
        {
            public RecordingDevice(IUSBDevice device) { this.device = device; }
            public IUSBConnection ConnectUSB() => new RecordingConnection(device.ConnectUSB(), WriteStarted);
            public byte Address => device.Address;
            public void Reset() => device.Reset();
            public readonly ManualResetEventSlim WriteStarted = new ManualResetEventSlim();
            private readonly IUSBDevice device;
        }

        private sealed class RecordingConnection : IUSBConnection
        {
            public RecordingConnection(IUSBConnection connection, ManualResetEventSlim started) { this.connection = connection; this.started = started; }
            public IUSBPipeSetup ConnectEndpointSetup(byte endpoint) => connection.ConnectEndpointSetup(endpoint);
            public IUSBPipeRead ConnectEndpointRead(byte endpoint) => connection.ConnectEndpointRead(endpoint);
            public IUSBPipeWrite ConnectEndpointWrite(byte endpoint) => new RecordingWrite(connection.ConnectEndpointWrite(endpoint), started);
            public void Dispose() => connection.Dispose();
            private readonly IUSBConnection connection;
            private readonly ManualResetEventSlim started;
        }

        private sealed class RecordingWrite : IUSBPipeWrite
        {
            public RecordingWrite(IUSBPipeWrite pipe, ManualResetEventSlim started) { this.pipe = pipe; this.started = started; }
            public void Write(byte[] data) { pipe.Write(data); started.Set(); }
            public Task WriteAsync(byte[] data, CancellationToken token = default)
            {
                var task = pipe.WriteAsync(data, token);
                started.Set();
                return task;
            }
            private readonly IUSBPipeWrite pipe;
            private readonly ManualResetEventSlim started;
        }

        private Task Submit(uint sequence, URBDirection direction, uint endpoint, uint length, byte[] data = null, SetupPacket? setup = null)
        {
            return Feed(new URBHeader { Command = URBCommand.URBRequest, SequenceNumber = sequence, BusId = 1,
                DeviceId = 0, Direction = direction, EndpointNumber = endpoint,
                FlagsOrStatus = direction == URBDirection.In ? 0x200u : 0 },
                new URBRequest { TransferBufferLength = length }, data, setup);
        }

        private Task Feed(URBHeader header, URBRequest request, byte[] data = null, SetupPacket? setup = null)
        {
            var bytes = Packet.Encode(header).Concat(Packet.Encode(request)).ToArray();
            if(setup.HasValue)
            {
                Array.Copy(Packet.Encode(setup.Value), 0, bytes, 40, 8);
            }
            bytes = bytes.Concat(data ?? Array.Empty<byte>()).ToArray();
            return Task.Factory.StartNew(() =>
            {
                lock(inputLock)
                {
                    foreach(var b in bytes)
                    {
                        typeof(USBIPServer).GetMethod("HandleIncomingData", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(server, new object[] { (int)b });
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        private byte[] AssertReply(uint sequence, URBCommand command)
        {
            byte[] reply = null;
            Assert.That(SpinWait.SpinUntil(() => replies.TryDequeue(out reply), Timeout), Is.True, $"No reply for sequence {sequence}");
            var header = Packet.Decode<URBHeader>(reply);
            Assert.That(header.SequenceNumber, Is.EqualTo(sequence));
            Assert.That(header.Command, Is.EqualTo(command));
            return reply;
        }

        private void SetURBState()
        {
            var field = typeof(USBIPServer).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
            field.SetValue(server, Enum.Parse(field.FieldType, "WaitForURBHeader"));
        }

        private static T GetField<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private sealed class TestDevice : IUSBDevice
        {
            public IUSBConnection ConnectUSB() => Connection;
            public void Reset() { }
            public byte Address => 0;
            public readonly TestConnection Connection = new TestConnection();
        }

        private sealed class TestConnection : IUSBConnection
        {
            public IUSBPipeSetup ConnectEndpointSetup(byte endpoint) => endpoint == 0 ? Setup : null;
            public IUSBPipeRead ConnectEndpointRead(byte endpoint) => endpoint == 1 ? Input : null;
            public IUSBPipeWrite ConnectEndpointWrite(byte endpoint) => endpoint == 2 ? Output : null;
            public void Dispose() { }
            public readonly TestPipe Input = new TestPipe();
            public readonly TestPipe Output = new TestPipe();
            public readonly TestPipe Setup = new TestPipe();
        }

        private sealed class TestPipe : IUSBPipeSetup, IUSBPipeStatus
        {
            public bool TryRead(out byte[] data)
            {
                ReadAttempted.Set();
                if(!packets.TryDequeue(out data))
                {
                    if(SuccessfulReads != 0) WaitingAfterPacket.Set();
                    return false;
                }
                Interlocked.Increment(ref SuccessfulReads);
                return true;
            }
            public void Write(byte[] data) => Writes.Enqueue(data);
            public void SetupPacketWrite(SetupPacket setup) => Push(SetupResponse);
            public void ReadDeviceDescriptor(Action<USB.DeviceDescriptor> callback) => callback(new USB.DeviceDescriptor { MaximumPacketSize = 64, NumberOfConfigurations = 1 });
            public void ReadConfigurationDescriptor(byte index, Action<(USB.ConfigurationDescriptor, IEnumerable<USB.IDescriptor>)> callback)
                => callback((new USB.ConfigurationDescriptor { ConfigurationValue = 1 }, new USB.IDescriptor[] {
                    new USB.InterfaceDescriptor { Number = 0 },
                    new USB.EndpointDescriptor { EndpointNumber = 1, Direction = USB.EndpointDirection.In, Attributes = 2, MaxPacketSize = EndpointPacketSize },
                    new USB.EndpointDescriptor { EndpointNumber = 2, Direction = USB.EndpointDirection.Out, Attributes = 2, MaxPacketSize = EndpointPacketSize }
                }));
            public void SetupRead(SetupPacket setup, Action<byte[]> callback)
            {
                SetupPacketWrite(setup);
                // The default zero-count SetupRead writes an OUT status packet.
                Write(Array.Empty<byte>());
                callback(Array.Empty<byte>());
            }
            public void Push(byte[] data)
            {
                packets.Enqueue(data);
                NewPacket?.Invoke();
            }
            public void StallInput() { stalledInput = true; StatusChanged?.Invoke(); }
            public bool IsStalled(USBDirection direction) => direction == USBDirection.DeviceToHost && stalledInput;
            public event Action StatusChanged;
            private bool stalledInput;
            public event Action NewPacket;
            public byte[] SetupResponse = Array.Empty<byte>();
            public ushort EndpointPacketSize = 64;
            public int SuccessfulReads;
            public readonly ManualResetEventSlim WaitingAfterPacket = new ManualResetEventSlim();
            public readonly ManualResetEventSlim ReadAttempted = new ManualResetEventSlim();
            public readonly ConcurrentQueue<byte[]> Writes = new ConcurrentQueue<byte[]>();
            private readonly ConcurrentQueue<byte[]> packets = new ConcurrentQueue<byte[]>();
        }

        private object inputLock;
        private USBIPServer server;
        private TestDevice device;
        private ConcurrentQueue<byte[]> replies;
        private const int Timeout = 2000;
    }
}
