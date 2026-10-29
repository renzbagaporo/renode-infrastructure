//
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

// uncomment the line below to get more detailed logs
// note: dumping packets may severely lower performance
// #define DEBUG_PACKETS

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Debugging;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Utilities;
using Antmicro.Renode.Utilities.Packets;

namespace Antmicro.Renode.Extensions.Utilities.USBIP
{
    public static class USBIPServerExtensions
    {
        public static void CreateUSBIPServer(this Emulation emulation, int port = 3240, string name = "usb")
        {
            var server = new USBIPServer(port);
            server.Run();

            emulation.HostMachine.AddHostMachineElement(server, name);
        }

        // this is just a simple wrapper method allowing to register devices from monitor
        public static void Register(this USBIPServer @this, IUSBDevice device, int? port = null, USBIPServer.USBSpeed speed = USBIPServer.USBSpeed.High)
        {
            if(!port.HasValue)
            {
                port = @this.Children.Any()
                    ? @this.Children.Max(x => x.RegistrationPoint.Address) + 1
                    : 0;
            }

            @this.Register(device, new NumberRegistrationPoint<int>(port.Value), speed);
        }
    }

    public class USBIPServer : SimpleContainerBase<IUSBDevice>, IHostMachineElement, IDisposable
    {
        public USBIPServer(int port)
        {
            this.port = port;

            server = new SocketServerProvider(false, flushOnConnect: true, serverName: "USBIP");
            server.DataReceived += HandleIncomingData;
            server.ConnectionClosed += Reset;

            // setting initial size is just an optimization
            buffer = new List<byte>(Packet.CalculateLength<URBRequest>());
            cancellationToken = new CancellationTokenSource();
        }

        public override void Dispose()
        {
            Shutdown();
            foreach(var (_, conn) in deviceToConnection)
            {
                conn.Dispose();
            }
            base.Dispose();
        }

        public void Run()
        {
            server.Start(port);
        }

        public void Reset()
        {
            PendingTransfer[] transfers;
            CancellationTokenSource previousToken;
            lock(transferLock)
            {
                generation++;
                transfers = pendingTransfers.Values.ToArray();
                pendingTransfers.Clear();
                endpointTasks.Clear();
                previousToken = cancellationToken;
                cancellationToken = new CancellationTokenSource();
            }
            previousToken.Cancel();
            foreach(var transfer in transfers)
            {
                transfer.Cancel();
            }
            state = State.WaitForCommand;
            urbHeader = default(URBHeader);
            additionalDataCount = 0;
            buffer.Clear();
        }

        public override void Register(IUSBDevice device, NumberRegistrationPoint<int> registrationPoint)
        {
            Register(device, registrationPoint, USBSpeed.High);
        }

        public void Register(IUSBDevice device, NumberRegistrationPoint<int> registrationPoint, USBSpeed speed)
        {
            base.Register(device, registrationPoint);
            deviceToConnection[device] = device.ConnectUSB();
            deviceSpeeds[device] = speed;
        }

        public override void Unregister(IUSBDevice device)
        {
            if(deviceToConnection.Remove(device, out var conn))
            {
                PendingTransfer[] transfers;
                lock(transferLock)
                {
                    transfers = pendingTransfers.Values.Where(t => t.Connection == conn).ToArray();
                    deviceMetadata.Remove(conn);
                    endpointZeroPacketSizes.Remove(conn);
                    foreach(var transfer in transfers)
                    {
                        pendingTransfers.Remove(transfer.Header.SequenceNumber);
                    }
                }
                foreach(var transfer in transfers)
                {
                    transfer.Cancel();
                }
                conn.Dispose();
            }
            deviceSpeeds.Remove(device);
            base.Unregister(device);
        }

        private void SendResponse(IEnumerable<byte> bytes)
        {
            server.Send(bytes);

#if DEBUG_PACKETS
            this.Log(LogLevel.Noisy, "Count {0}: {1}", bytes.Count(), Misc.PrettyPrintCollectionHex(bytes));
#endif
        }

        private void Shutdown()
        {
            this.Log(LogLevel.Info, "Shutting down the server...");

            Reset();
            cancellationToken.Cancel();
            server.Stop();
        }

        private void HandleIncomingData(int b)
        {
#if DEBUG_PACKETS
            this.Log(LogLevel.Noisy, "Incoming byte: 0x{0:X}; state = {1}; buffer size = {2}", b, state, buffer.Count);
#endif

            if(b < 0)
            {
                // the current connection is closed - reset the state and prepare for the next one
                Reset();
                return;
            }

            buffer.Add((byte)b);

            switch(state)
            {
            case State.WaitForCommand:
            {
                DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIP.Header>());
                if(buffer.Count == Packet.CalculateLength<USBIP.Header>())
                {
                    var header = Packet.Decode<USBIP.Header>(buffer);
                    buffer.Clear();
                    this.Log(LogLevel.Debug, "Received USB/IP header: {0}", header.ToString());

                    switch(header.Command)
                    {
                    case USBIP.Command.ListDevices:
                    {
                        SendResponse(HandleListDevicesCommand());

                        state = State.WaitForCommand;
                        break;
                    }

                    case USBIP.Command.AttachDevice:
                    {
                        state = State.WaitForBusId;
                        break;
                    }

                    default:
                    {
                        this.Log(LogLevel.Error, "Unexpected packet command: 0x{0:X}", header.Command);
                        Shutdown();
                        break;
                    }
                    }
                }
                break;
            }

            case State.WaitForBusId:
            {
                DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<USBIP.AttachDeviceCommandDescriptor>());
                if(buffer.Count == Packet.CalculateLength<USBIP.AttachDeviceCommandDescriptor>())
                {
                    var busId = System.Text.Encoding.ASCII.GetString(buffer.ToArray());
                    buffer.Clear();

                    var (success, response) = HandleDeviceAttachCommand(busId);
                    state = success
                        ? State.WaitForURBHeader
                        : State.WaitForCommand;

                    SendResponse(response);
                }
                break;
            }

            case State.WaitForURBHeader:
            {
                DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<URBHeader>());
                if(buffer.Count == Packet.CalculateLength<URBHeader>())
                {
                    urbHeader = Packet.Decode<URBHeader>(buffer);
                    this.Log(LogLevel.Debug, "Received URB header: {0}", urbHeader.ToString());
                    buffer.Clear();

                    switch(urbHeader.Command)
                    {
                    case URBCommand.URBRequest:
                        state = State.WaitForURBRequest;
                        break;
                    case URBCommand.Unlink:
                        state = State.HandleUnlinkCommand;
                        break;
                    default:
                        this.Log(LogLevel.Error, "Unexpected URB command: 0x{0:X}", urbHeader.Command);
                        Shutdown();
                        break;
                    }
                }
                break;
            }

            case State.WaitForURBRequest:
            {
                if(buffer.Count < Packet.CalculateLength<URBRequest>() + additionalDataCount)
                {
                    break;
                }

                var packet = Packet.Decode<URBRequest>(buffer);
                if(additionalDataCount == 0)
                {
                    this.Log(LogLevel.Debug, "Received URB request: {0}", packet.ToString());

                    if(urbHeader.Direction == URBDirection.Out && packet.TransferBufferLength > 0)
                    {
                        additionalDataCount = (int)packet.TransferBufferLength;
                        this.Log(LogLevel.Debug, "Packet comes with {0} bytes of additional data. Waiting for it", additionalDataCount);
                        break;
                    }
                }
                else
                {
                    if(urbHeader.Direction == URBDirection.Out && packet.TransferBufferLength > 0)
                    {
                        this.Log(LogLevel.Debug, "Collected {0} bytes of additional data - moving forward", additionalDataCount);
                    }
                }

                // The parser must release its mutable state before a worker can run.
                var header = urbHeader;
                var setup = Packet.Decode<SetupPacket>(buffer, Packet.CalculateOffset<URBRequest>(nameof(URBRequest.Setup)));
                var payload = buffer.Skip(Packet.CalculateLength<URBRequest>()).ToArray();
                additionalDataCount = 0;
                state = State.WaitForURBHeader;
                buffer.Clear();
                DispatchRequest(header, packet, setup, payload);

                break;
            }

            case State.HandleUnlinkCommand:
            {
                DebugHelper.Assert(buffer.Count <= Packet.CalculateLength<URBRequest>());
                if(buffer.Count == Packet.CalculateLength<URBRequest>())
                {
                    var header = urbHeader;
                    buffer.Clear();
                    state = State.WaitForURBHeader;
                    HandleUnlink(header);
                }
                break;
            }

            default:
                throw new ArgumentException(string.Format("Unexpected state: {0}", state));
            }
        }

        private void DispatchRequest(URBHeader header, URBRequest request, SetupPacket setup, byte[] payload)
        {
            if(header.BusId != ExportedBusId || !TryGetByAddress((int)header.DeviceId, out var device))
            {
                SendResponse(GenerateURBReply(header, request, status: NoDevice));
                return;
            }

            var conn = deviceToConnection[device];
            lock(transferLock)
            {
                if(pendingTransfers.ContainsKey(header.SequenceNumber))
                {
                    SendResponse(GenerateURBReply(header, request, status: InvalidArgument));
                    return;
                }
                var transfer = new PendingTransfer(header, request, setup, payload, conn, generation);
                pendingTransfers.Add(header.SequenceNumber, transfer);
                // EP0 shares one queue for both directions. Other endpoints have
                // independent IN/OUT queues, preserving submission order per pipe.
                var key = (conn, header.EndpointNumber, header.EndpointNumber == 0 ? URBDirection.Out : header.Direction);
                endpointTasks.TryGetValue(key, out var previous);
                endpointTasks[key] = (previous ?? Task.CompletedTask).ContinueWith(_ => ExecuteTransfer(transfer),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
        }

        private void ExecuteTransfer(PendingTransfer transfer)
        {
            try
            {
                byte[] data = null;
                var header = transfer.Header;
                if(header.EndpointNumber == 0)
                {
                    var ep = transfer.Invoke(() => transfer.Connection.ConnectEndpointSetup(0));
                    if(ep == null)
                    {
                        CompleteTransfer(transfer, status: NoEntry);
                        return;
                    }
                    transfer.Invoke(() => ep.SetupPacketWrite(transfer.Setup));
                    if(transfer.Setup.Count == 0 || transfer.Setup.Direction == Direction.HostToDevice)
                    {
                        // A zero-count host-to-device setup has no OUT data stage;
                        // its status stage is an IN zero-length packet.
                        if(transfer.Setup.Count != 0)
                        {
                            WritePacket(transfer, ep, transfer.Payload);
                        }
                        ReadPacket(transfer, ep);
                    }
                    else
                    {
                        var length = Math.Min(transfer.Setup.Count, transfer.Request.TransferBufferLength);
                        endpointZeroPacketSizes.TryGetValue(transfer.Connection, out var packetSize);
                        if(packetSize == 0) packetSize = 64;
                        var (response, status) = ReadData(transfer, ep, (int)length, packetSize);
                        WritePacket(transfer, ep, Array.Empty<byte>());
                        CompleteTransfer(transfer, response, status);
                        return;
                    }
                }
                else if(header.Direction == URBDirection.In)
                {
                    var ep = transfer.Invoke(() => transfer.Connection.ConnectEndpointRead((byte)header.EndpointNumber));
                    if(ep == null)
                    {
                        CompleteTransfer(transfer, status: NoEntry);
                        return;
                    }
                    var descriptor = GetEndpointDescriptor(transfer.Connection, (byte)header.EndpointNumber, USB.EndpointDirection.In);
                    if(!descriptor.HasValue || (descriptor.Value.MaxPacketSize & 0x7ff) == 0)
                    {
                        CompleteTransfer(transfer, status: NoEntry);
                        return;
                    }
                    var (response, status) = ReadData(transfer, ep, checked((int)transfer.Request.TransferBufferLength), descriptor.Value.MaxPacketSize & 0x7ff);
                    CompleteTransfer(transfer, response, status);
                    return;
                }
                else
                {
                    var ep = transfer.Invoke(() => transfer.Connection.ConnectEndpointWrite((byte)header.EndpointNumber));
                    if(ep == null)
                    {
                        CompleteTransfer(transfer, status: NoEntry);
                        return;
                    }
                    WritePacket(transfer, ep, transfer.Payload);
                }
                transfer.Invoke(() => UpdateControlState(transfer));
                CompleteTransfer(transfer, data);
            }
            catch(OperationCanceledException)
            {
                // UNLINK/reset removes the request first. Device-side cancellation
                // still completes a submission that remains outstanding.
                CompleteTransfer(transfer, status: ConnectionReset);
            }
            catch(Exception e)
            {
                this.Log(LogLevel.Warning, "USB/IP transfer failed: {0}", e.Message);
                CompleteTransfer(transfer, status: InputOutputError);
            }
            finally
            {
                transfer.Dispose();
            }
        }

        private void WritePacket(PendingTransfer transfer, IUSBPipeWrite pipe, byte[] data)
        {
            var completion = transfer.Invoke(() => pipe.WriteAsync(data, transfer.Token));
            completion.GetAwaiter().GetResult();
            transfer.Token.ThrowIfCancellationRequested();
        }

        private (byte[], int) ReadData(PendingTransfer transfer, IUSBPipeRead pipe, int length, int maximumPacketSize)
        {
            var response = new List<byte>();
            do
            {
                var packet = ReadPacket(transfer, pipe);
                if(packet.Length > maximumPacketSize || packet.Length > length - response.Count)
                {
                    response.AddRange(packet.Take(length - response.Count));
                    return (response.ToArray(), Overflow);
                }
                response.AddRange(packet);
                if(packet.Length < maximumPacketSize)
                {
                    var status = response.Count < length && (transfer.Header.FlagsOrStatus & ShortNotOkFlag) != 0 ? RemoteInputOutputError : 0;
                    return (response.ToArray(), status);
                }
            }
            while(response.Count < length);
            return (response.ToArray(), 0);
        }

        private void UpdateControlState(PendingTransfer transfer)
        {
            if(transfer.Header.EndpointNumber != 0 || transfer.Setup.Type != PacketType.Standard) return;
            lock(transferLock)
            {
                if(!deviceMetadata.TryGetValue(transfer.Connection, out var metadata)) return;
                if(transfer.Setup.Recipient == PacketRecipient.Device && transfer.Setup.Request == (byte)StandardRequest.SetConfiguration)
                {
                    metadata.Configuration = (byte)transfer.Setup.Value;
                    metadata.AlternateSettings.Clear();
                }
                else if(transfer.Setup.Recipient == PacketRecipient.Interface && transfer.Setup.Request == (byte)StandardRequest.SetInterface)
                {
                    metadata.AlternateSettings[(byte)transfer.Setup.Index] = (byte)transfer.Setup.Value;
                }
            }
        }

        private USB.EndpointDescriptor? GetEndpointDescriptor(IUSBConnection connection, byte endpoint, USB.EndpointDirection direction)
        {
            lock(transferLock)
            {
                if(!deviceMetadata.TryGetValue(connection, out var metadata)) return null;
                foreach(var entry in metadata.Endpoints)
                {
                    metadata.AlternateSettings.TryGetValue(entry.Interface, out var alternate);
                    if(entry.Configuration == metadata.Configuration && entry.Alternate == alternate
                        && entry.Descriptor.EndpointNumber == endpoint && entry.Descriptor.Direction == direction)
                    {
                        return entry.Descriptor;
                    }
                }
                return null;
            }
        }

        private byte[] ReadPacket(PendingTransfer transfer, IUSBPipeRead pipe)
        {
            using(var available = new AutoResetEvent(false))
            {
                var finished = false;
                Action notify = () =>
                {
                    lock(available)
                    {
                        if(!finished) available.Set();
                    }
                };
                pipe.NewPacket += notify;
                try
                {
                    while(true)
                    {
                        byte[] data = null;
                        // Cancellation and TryRead share a gate: after Cancel returns,
                        // this reader can never consume a future packet.
                        if(transfer.Invoke(() => pipe.TryRead(out data)))
                        {
                            Misc.WaitForCallback(onDone => IUSBPipeRead.WrapReadCallbackInDelay(_ => onDone())(data), transfer.Token);
                            return data;
                        }
                        WaitHandle.WaitAny(new WaitHandle[] { available, transfer.Token.WaitHandle });
                    }
                }
                finally
                {
                    pipe.NewPacket -= notify;
                    lock(available)
                    {
                        finished = true;
                    }
                }
            }
        }

        private void CompleteTransfer(PendingTransfer transfer, byte[] data = null, int status = 0)
        {
            lock(transferLock)
            {
                if(transfer.Generation != generation
                    || !pendingTransfers.TryGetValue(transfer.Header.SequenceNumber, out var current)
                    || current != transfer)
                {
                    return;
                }
                pendingTransfers.Remove(transfer.Header.SequenceNumber);
                SendResponse(GenerateURBReply(transfer.Header, transfer.Request, data, status));
            }
        }

        private void HandleUnlink(URBHeader header)
        {
            PendingTransfer transfer;
            int currentGeneration;
            lock(transferLock)
            {
                pendingTransfers.Remove(header.FlagsOrStatus, out transfer);
                currentGeneration = generation;
            }
            transfer?.Cancel();
            lock(transferLock)
            {
                if(currentGeneration != generation) return;
                var reply = new URBHeader
                {
                    Command = URBCommand.UnlinkReply,
                    SequenceNumber = header.SequenceNumber,
                    FlagsOrStatus = unchecked((uint)(transfer == null ? 0 : ConnectionReset))
                };
                SendResponse(Packet.Encode(reply).Concat(new byte[Packet.CalculateLength<URBRequest>()]));
            }
        }

        private IEnumerable<byte> GenerateURBReply(URBHeader hdr, URBRequest req, IEnumerable<byte> data = null, int status = 0)
        {
            var header = new URBHeader
            {
                Command = URBCommand.URBReply,
                SequenceNumber = hdr.SequenceNumber,
                FlagsOrStatus = unchecked((uint)status)
            };
            var reply = new URBReply
            {
                ActualLength = data != null ? (uint)data.Count() : status == 0 ? req.TransferBufferLength : 0,
                NumberOfPackets = uint.MaxValue
            };
            var result = Packet.Encode(header).Concat(Packet.Encode(reply));
            if(data != null && hdr.Direction == URBDirection.In)
            {
                result = result.Concat(data);
            }
            return result;
        }

        private IEnumerable<byte> GenerateDeviceDescriptor(IUSBDevice device, uint deviceNumber, bool includeInterfaces)
        {
            var conn = deviceToConnection[device];
            var ep0 = conn.ConnectEndpointSetup(0);
            var deviceDescriptor = ep0.ReadDeviceDescriptorBlocking(cancellationToken.Token);
            endpointZeroPacketSizes[conn] = deviceDescriptor.MaximumPacketSize;
            var interfaceDescriptors = new List<USB.InterfaceDescriptor>();

            var metadata = new DeviceMetadata();
            for(var i = 0; i < deviceDescriptor.NumberOfConfigurations; i++)
            {
                var (configuration, descs) = ep0.ReadConfigurationDescriptorBlocking((byte)i, cancellationToken.Token);
                foreach(var (iface, children) in USB.IDescriptor.EnumerateInterfaceDescriptors(descs))
                {
                    if(i == 0 && iface.AlternateSetting == 0) interfaceDescriptors.Add(iface);
                    foreach(var endpoint in children.OfType<USB.EndpointDescriptor>())
                    {
                        metadata.Endpoints.Add((configuration.ConfigurationValue, iface.Number, iface.AlternateSetting, endpoint));
                    }
                }
            }
            lock(transferLock)
            {
                if(deviceMetadata.TryGetValue(conn, out var previous))
                {
                    metadata.Configuration = previous.Configuration;
                    foreach(var setting in previous.AlternateSettings) metadata.AlternateSettings.Add(setting.Key, setting.Value);
                }
                deviceMetadata[conn] = metadata;
            }

            var devDescriptor = new USBIP.DeviceDescriptor
            {
                Path = new byte[256],
                BusId = new byte[32],

                BusNumber = ExportedBusId,
                DeviceNumber = deviceNumber,
                Speed = (uint)deviceSpeeds[device],

                IdVendor = deviceDescriptor.VendorId,
                IdProduct = deviceDescriptor.ProductId,

                DeviceClass = deviceDescriptor.Class,
                DeviceSubClass = deviceDescriptor.Subclass,
                DeviceProtocol = deviceDescriptor.Protocol,

                NumberOfConfigurations = deviceDescriptor.NumberOfConfigurations,
                NumberOfInterfaces = (byte)interfaceDescriptors.Count
            };

            SetText(devDescriptor.BusId, "{0}-{1}", ExportedBusId, deviceNumber);
            SetText(devDescriptor.Path, "/renode/virtual/{0}-{1}", ExportedBusId, deviceNumber);

            var result = Packet.Encode(devDescriptor).AsEnumerable();

            foreach(var iface in interfaceDescriptors.Where(_ => includeInterfaces))
            {
                var intDescriptor = new USBIP.InterfaceDescriptor
                {
                    InterfaceClass = iface.Class,
                    InterfaceSubClass = iface.Subclass,
                    InterfaceProtocol = iface.Protocol
                };

                result = result.Concat(Packet.Encode(intDescriptor));
            }

            return result;
        }

        private void SetText(byte[] destination, string format, params object[] param)
        {
            var textBuffer = System.Text.Encoding.ASCII.GetBytes(string.Format(format, param));
            Array.Copy(textBuffer, destination, textBuffer.Length);
        }

        private IEnumerable<byte> HandleListDevicesCommand()
        {
            var header = new USBIP.Header
            {
                Version = ProtocolVersion,
                Command = USBIP.Command.ListDevicesReply,
            };

            var regCount = new USBIP.DeviceListCount
            {
                NumberOfExportedDevices = (uint)ChildCollection.Count
            };

            var result = Packet.Encode(header).Concat(Packet.Encode(regCount));

            foreach(var child in ChildCollection)
            {
                result = result.Concat(GenerateDeviceDescriptor(child.Value, (uint)child.Key, true));
            }

            return result;
        }

        private (bool, IEnumerable<byte>) HandleDeviceAttachCommand(string deviceIdString)
        {
            var success = true;
            var deviceId = 0;
            IUSBDevice device = null;

            var m = Regex.Match(deviceIdString, "([0-9]+)-([0-9]+)");
            if(!m.Success)
            {
                this.Log(LogLevel.Warning, "Unexpected device string when handling attach command: {0}. It should be in format '1-4'", deviceIdString);
                success = false;
            }
            else
            {
                var busId = int.Parse(m.Groups[1].Value);
                deviceId = int.Parse(m.Groups[2].Value);

                if(busId != ExportedBusId)
                {
                    this.Log(LogLevel.Warning, "Trying to attach to a non-existing bus 0x{0:X}", busId);
                    success = false;
                }
                else if(!TryGetByAddress(deviceId, out device))
                {
                    this.Log(LogLevel.Warning, "Trying to attach to a non-existing device 0x{0:X}", deviceId);
                    success = false;
                }
            }

            var header = new USBIP.Header
            {
                Version = ProtocolVersion,
                Command = USBIP.Command.AttachDeviceReply,
                Status = success ? 0 : 1u
            };

            var response = Packet.Encode(header).AsEnumerable();
            if(success)
            {
                var conn = deviceToConnection[device];
                response = response.Concat(GenerateDeviceDescriptor(device, (uint)deviceId, false));
            }

            return (success, response);
        }

        private sealed class DeviceMetadata
        {
            public byte Configuration;
            public readonly Dictionary<byte, byte> AlternateSettings = new Dictionary<byte, byte>();
            public readonly List<(byte Configuration, byte Interface, byte Alternate, USB.EndpointDescriptor Descriptor)> Endpoints
                = new List<(byte, byte, byte, USB.EndpointDescriptor)>();
        }

        private sealed class PendingTransfer : IDisposable
        {
            public PendingTransfer(URBHeader header, URBRequest request, SetupPacket setup, byte[] payload, IUSBConnection connection, int generation)
            {
                Header = header;
                Request = request;
                Setup = setup;
                Payload = payload;
                Connection = connection;
                Generation = generation;
            }

            public T Invoke<T>(Func<T> action)
            {
                lock(gate)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    return action();
                }
            }

            public void Invoke(Action action) => Invoke(() => { action(); return true; });

            public void Cancel()
            {
                lock(gate)
                {
                    if(!disposed) cancellation.Cancel();
                }
            }

            public void Dispose()
            {
                lock(gate)
                {
                    disposed = true;
                    cancellation.Dispose();
                }
            }

            public CancellationToken Token => cancellation.Token;
            public readonly URBHeader Header;
            public readonly URBRequest Request;
            public readonly SetupPacket Setup;
            public readonly byte[] Payload;
            public readonly IUSBConnection Connection;
            public readonly int Generation;
            private readonly object gate = new object();
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
            private bool disposed;
        }

        private readonly object transferLock = new object();
        private readonly Dictionary<uint, PendingTransfer> pendingTransfers = new Dictionary<uint, PendingTransfer>();
        private readonly Dictionary<(IUSBConnection, uint, URBDirection), Task> endpointTasks = new Dictionary<(IUSBConnection, uint, URBDirection), Task>();
        private readonly Dictionary<IUSBDevice, USBSpeed> deviceSpeeds = new Dictionary<IUSBDevice, USBSpeed>();
        private readonly Dictionary<IUSBConnection, byte> endpointZeroPacketSizes = new Dictionary<IUSBConnection, byte>();
        private readonly Dictionary<IUSBConnection, DeviceMetadata> deviceMetadata = new Dictionary<IUSBConnection, DeviceMetadata>();
        private int generation;
        private const uint ShortNotOkFlag = 1;
        private const int RemoteInputOutputError = -121;
        private const int ConnectionReset = -104;
        private const int NoEntry = -2;
        private const int NoDevice = -19;
        private const int InvalidArgument = -22;
        private const int InputOutputError = -5;
        private const int Overflow = -75;

        private State state;
        private URBHeader urbHeader;
        private int additionalDataCount;
        private CancellationTokenSource cancellationToken;

        private readonly int port;
        private readonly List<byte> buffer;
        private readonly SocketServerProvider server;
        private readonly IDictionary<IUSBDevice, IUSBConnection> deviceToConnection = new Dictionary<IUSBDevice,IUSBConnection>();

        private const uint ExportedBusId = 1;
        private const ushort ProtocolVersion = 0x0111;

        public enum USBSpeed
        {
            Unknown = 0,
            Low = 1,
            Full = 2,
            High = 3,
            Wireless = 4,
            Super = 5,
            SuperPlus = 6,
        }

        private enum State
        {
            WaitForCommand,
            WaitForBusId,
            WaitForURBHeader,
            HandleUnlinkCommand,
            WaitForURBRequest,
        }
    }
}
