//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities.Packets;

using USBDirection = Antmicro.Renode.Core.USB.Direction;

namespace Antmicro.Renode.Peripherals.USB
{
    // Full-speed device controller. USB host requests exchange packets with
    // the buffers owned by the SIE; firmware services the corresponding IRQs.
    public sealed class RP2_USB : RP2APBPeripheral, IUSBDevice
    {
        public RP2_USB(IMachine machine, RP2_USB_DPRAM dpram, IRP2ClockSource clockSource) : base(machine)
        {
            this.dpram = dpram;
            this.clockSource = clockSource ?? throw new ArgumentNullException(nameof(clockSource));
            endpoints = new Endpoint[EndpointCount];
            bufferIndex = new int[EndpointCount * 2];
            for(var endpoint = 0; endpoint < EndpointCount; ++endpoint)
            {
                endpoints[endpoint] = new Endpoint(this, endpoint);
            }
            IRQ = new GPIO();
            frameTimer = new LimitTimer(machine.ClockSource, 1000, this, nameof(frameTimer), limit: 1,
                workMode: WorkMode.Periodic, eventEnabled: true);
            frameTimer.LimitReached += () =>
            {
                lock(dpram)
                {
                    if(!CanProcessPackets) return;
                    frameNumber = (frameNumber + 1) & 0x7ff;
                    startOfFramePending = true;
                    UpdateInterrupts();
                }
                PublishPending();
            };
            dpram.BufferControlWritten += BufferControlWritten;
            dpram.AccessCompleted += PublishPending;
            clockSource.ClockChanged += ClockChanged;
            Reset();
            ClockChanged();
        }

        public override void Reset()
        {
            lock(dpram)
            {
                Array.Clear(registers, 0, registers.Length);
                Array.Clear(bufferIndex, 0, bufferIndex.Length);
                foreach(var endpoint in endpoints)
                {
                    endpoint.Reset();
                }
                frameTimer.Reset();
                frameNumber = 0;
                bufferStatus = 0;
                bufferCPU = 0;
                sieStatus = 0;
                startOfFramePending = false;
                connectionObserved = false;
                UpdateConnection();
                UpdateInterrupts();
            }
            PublishPending();
        }

        public IUSBConnection ConnectUSB()
        {
            IUSBConnection connection;
            lock(dpram)
            {
                connectionCount++;
                UpdateConnection();
                connection = new Connection(this);
            }
            PublishPending();
            return connection;
        }

        public byte Address => (byte)(registers[(int)Registers.Address / 4] & 0x7f);

        public GPIO IRQ { get; }

        protected override object SyncRoot => dpram;

        protected override void OnAccessCompleted() => PublishPending();

        protected override void OnResetHeldChanged()
        {
            dpram.ResetHeld = ResetHeld;
            UpdateConnection();
            UpdateInterrupts();
        }

        protected override uint ReadRegister(long offset)
        {
            lock(dpram)
            {
                switch((Registers)offset)
                {
                case Registers.StartOfFrame:
                    startOfFramePending = false;
                    UpdateInterrupts();
                    return frameNumber;
                case Registers.SIEStatus:
                    return sieStatus | (connectionObserved ? 1u << 16 : 0);
                case Registers.BufferStatus:
                    return bufferStatus;
                case Registers.BufferCPU:
                    return bufferCPU;
                case Registers.InterruptRaw:
                    return RawInterrupts;
                case Registers.InterruptStatus:
                    return EnabledInterrupts;
                default:
                    if(offset >= 0 && offset < RegisterBytes && (offset & 3) == 0)
                    {
                        return registers[offset / 4];
                    }
                    this.LogUnhandledRead(offset);
                    return 0;
                }
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            lock(dpram)
            {
                switch((Registers)offset)
                {
                case Registers.SIEStatus:
                    ClearStatus(ref sieStatus, value, operation);
                    break;
                case Registers.BufferStatus:
                    ClearStatus(ref bufferStatus, value, operation);
                    break;
                case Registers.AbortDone:
                    var abortDone = registers[offset / 4];
                    ClearStatus(ref abortDone, value, operation);
                    registers[offset / 4] = abortDone;
                    break;
                case Registers.BufferCPU:
                case Registers.InterruptRaw:
                case Registers.InterruptStatus:
                case Registers.StartOfFrame:
                    this.Log(LogLevel.Warning, "Write to read-only USB register 0x{0:X}", offset);
                    break;
                default:
                    if(offset < 0 || offset >= RegisterBytes || (offset & 3) != 0)
                    {
                        this.LogUnhandledWrite(offset, value);
                        return;
                    }
                    registers[offset / 4] = ApplyOperation(registers[offset / 4], value, operation);
                    if((Registers)offset == Registers.Abort)
                    {
                        // Token transfers complete atomically in this model, so an
                        // endpoint can acknowledge an abort between any two tokens.
                        registers[(int)Registers.AbortDone / 4] |= registers[offset / 4];
                    }
                    if((Registers)offset == Registers.StallArm)
                    {
                        endpoints[0].BufferReady();
                    }
                    if((Registers)offset == Registers.MainControl || (Registers)offset == Registers.SIEControl)
                    {
                        UpdateConnection();
                    }
                    break;
                }
                UpdateInterrupts();
            }
        }

        private void BusReset()
        {
            registers[(int)Registers.Address / 4] = 0;
            bufferStatus = 0;
            bufferCPU = 0;
            Array.Clear(bufferIndex, 0, bufferIndex.Length);
            foreach(var endpoint in endpoints)
            {
                endpoint.Reset();
            }
            sieStatus = BusResetStatus;
            UpdateInterrupts();
        }

        private void UpdateConnection()
        {
            if(!PhysicalConnection)
            {
                connectionObserved = false;
                foreach(var endpoint in endpoints) endpoint.Reset();
            }
            if(!ResetHeld && ClocksAvailable && PhysicalConnection && !connectionObserved)
            {
                connectionObserved = true;
                BusReset();
            }
            frameTimer.Enabled = connectionObserved;
            frameTimer.Clocked = CanProcessPackets;
            if(CanProcessPackets)
            {
                foreach(var endpoint in endpoints)
                {
                    endpoint.BufferReady();
                }
            }
        }

        private void ClockChanged()
        {
            lock(dpram)
            {
                if(clockSource.USBFrequency == 48000000 && clockSource.SystemFrequency != 0
                    && clockSource.SystemFrequency < 52800000 && !timingWarningEmitted)
                {
                    timingWarningEmitted = true;
                    this.Log(LogLevel.Warning, "USB clk_sys below 52.8 MHz: RP2040-E16/RP2350-E12 event loss is not emulated");
                }
                UpdateConnection();
                UpdateInterrupts();
            }
            PublishPending();
        }

        private void BufferControlWritten(int control)
        {
            lock(dpram)
            {
                if(ResetHeld) return;
                // The RESET bit resets the SIE's buffer selector independently for
                // each endpoint direction. Clear it before notifying consumers.
                var offset = BufferControlOffset + control * 4;
                var value = dpram.ReadDoubleWord(offset);
                if((value & BufferReset) != 0)
                {
                    bufferIndex[control] = 0;
                    dpram.WriteDoubleWord(offset, value & ~(uint)BufferReset);
                    return;
                }
                endpoints[control / 2].BufferReady();
            }
        }

        private bool TryGetBuffer(int endpoint, bool input, out Buffer buffer)
        {
            var control = endpoint * 2 + (input ? 0 : 1);
            var endpointControl = endpoint == 0 ? 0 : dpram.ReadDoubleWord(8 + (endpoint - 1) * 8 + (input ? 0 : 4));
            var doubleBuffered = endpoint == 0
                ? (registers[(int)Registers.SIEControl / 4] & (1u << 30)) != 0
                : (endpointControl & (1u << 30)) != 0;
            var index = doubleBuffered ? bufferIndex[control] : 0;
            var offset = BufferControlOffset + control * 4;
            var controlWord = dpram.ReadDoubleWord(offset);
            var half = (ushort)(controlWord >> (index * 16));
            var enabled = endpoint == 0 || (endpointControl & (1u << 31)) != 0;
            var aborted = (registers[(int)Registers.Abort / 4] & (1u << control)) != 0;
            var stallArmed = endpoint != 0
                || (registers[(int)Registers.StallArm / 4] & (input ? 1u : 2u)) != 0;
            var stalled = (controlWord & BufferStall) != 0 && stallArmed;
            var address = endpoint == 0 ? 0x100 : (int)(endpointControl & 0xffff);
            if(index != 0)
            {
                var isochronous = endpoint != 0 && ((endpointControl >> 26) & 3) == 1;
                address += isochronous ? 128 << (int)((controlWord >> 27) & 3) : 64;
            }
            buffer = new Buffer(control, index, offset, address, half, doubleBuffered);
            return CanProcessPackets && enabled && !aborted && !stalled && (half & BufferAvailable) != 0
                && (input || (half & BufferFull) == 0);
        }

        private bool IsEndpointStalled(int endpoint, bool input)
        {
            if(!CanProcessPackets) return false;
            var control = endpoint * 2 + (input ? 0 : 1);
            var enabled = endpoint == 0 || (dpram.ReadDoubleWord(8 + (endpoint - 1) * 8 + (input ? 0 : 4)) & (1u << 31)) != 0;
            var armed = endpoint != 0 || (registers[(int)Registers.StallArm / 4] & (input ? 1u : 2u)) != 0;
            return enabled && armed && (registers[(int)Registers.Abort / 4] & (1u << control)) == 0
                && (dpram.ReadDoubleWord(BufferControlOffset + control * 4) & BufferStall) != 0;
        }

        private void CompleteBuffer(Buffer buffer, bool input, int length)
        {
            var value = dpram.ReadDoubleWord(buffer.ControlOffset);
            var half = (ushort)((buffer.Control & ~(BufferAvailable | BufferFull | BufferLengthMask))
                | (input ? 0 : BufferFull) | length);
            var shift = buffer.Index * 16;
            value = (value & ~(0xffffu << shift)) | ((uint)half << shift);
            // Update selector/status before the DPRAM notification can let
            // firmware or a waiting host callback observe the completion.
            bufferIndex[buffer.Direction] = buffer.DoubleBuffered ? buffer.Index ^ 1 : 0;
            var bit = 1u << buffer.Direction;
            bufferCPU = buffer.Index != 0 ? bufferCPU | bit : bufferCPU & ~bit;
            var endpoint = buffer.Direction / 2;
            var control = endpoint == 0 ? registers[(int)Registers.SIEControl / 4]
                : dpram.ReadDoubleWord(8 + (endpoint - 1) * 8 + (input ? 0 : 4));
            var interruptPerBuffer = (control & (1u << 29)) != 0;
            var interruptPerPair = (control & (1u << 28)) != 0;
            if(interruptPerBuffer || (interruptPerPair && buffer.Index == 1))
            {
                bufferStatus |= bit;
            }
            dpram.WriteDoubleWord(buffer.ControlOffset, value);
            UpdateInterrupts();
        }

        private void Setup(SetupPacket packet)
        {
            try
            {
                lock(dpram)
                {
                    if(!CanProcessPackets)
                    {
                        this.Log(LogLevel.Warning, "USB SETUP token received while the serial interface is inactive");
                        return;
                    }
                    // Hardware clears EP0_STALL_ARM on SETUP, independently of
                    // when the firmware services SETUP_REC and rewrites buffers.
                    registers[(int)Registers.StallArm / 4] = 0;
                    endpoints[0].Reset();
                    bufferIndex[0] = 0;
                    bufferIndex[1] = 0;
                    // A new SETUP terminates the previous control transfer. Its
                    // response must come from firmware handling this setup packet.
                    dpram.WriteDoubleWord(0x80, dpram.ReadDoubleWord(0x80) & ~0x04000400u);
                    dpram.WriteDoubleWord(0x84, dpram.ReadDoubleWord(0x84) & ~0x04000400u);
                    dpram.WriteBytes(0, Packet.Encode(packet));
                    sieStatus |= SetupReceivedStatus;
                    UpdateInterrupts();
                }
            }
            finally
            {
                PublishPending();
            }
        }

        private void QueueInputNotification(int endpoint)
        {
            pendingInputNotifications |= 1u << endpoint;
        }

        // Default USB read helpers hold their packet monitor while calling
        // TryRead. Publish outside the DPRAM monitor to avoid reversing that
        // lock order; reentrant buffer rearming appends another notification.
        private void PublishPending()
        {
            if(Monitor.IsEntered(dpram)) return;
            lock(dpram)
            {
                if(publishingInputNotifications) return;
                publishingInputNotifications = true;
            }
            try
            {
                while(true)
                {
                    int endpoint;
                    bool ready;
                    bool statusChanged;
                    lock(dpram)
                    {
                        if((pendingInputNotifications | pendingStatusNotifications) == 0)
                        {
                            publishingInputNotifications = false;
                            return;
                        }
                        endpoint = 0;
                        while(((pendingInputNotifications | pendingStatusNotifications) & (1u << endpoint)) == 0) endpoint++;
                        statusChanged = (pendingStatusNotifications & (1u << endpoint)) != 0;
                        ready = (pendingInputNotifications & (1u << endpoint)) != 0
                            && TryGetBuffer(endpoint, true, out var buffer) && (buffer.Control & BufferFull) != 0;
                        pendingInputNotifications &= ~(1u << endpoint);
                        pendingStatusNotifications &= ~(1u << endpoint);
                    }
                    if(statusChanged) endpoints[endpoint].PublishStatusChanged();
                    if(ready) endpoints[endpoint].PublishAvailable();
                }
            }
            catch
            {
                lock(dpram) publishingInputNotifications = false;
                throw;
            }
        }

        private void UpdateInterrupts()
        {
            IRQ.Set(!ResetHeld && EnabledInterrupts != 0);
        }

        private static void ClearStatus(ref uint status, uint value, AtomicOperation operation)
        {
            switch(operation)
            {
            case AtomicOperation.Set:
                status |= value;
                break;
            case AtomicOperation.Xor:
                status ^= value;
                break;
            default:
                status &= ~value;
                break;
            }
        }

        private uint RawInterrupts => (bufferStatus != 0 ? 1u << 4 : 0)
            | ((sieStatus & (1u << 26)) != 0 ? 1u << 7 : 0)
            | ((sieStatus & BusResetStatus) != 0 ? 1u << 12 : 0)
            | ((sieStatus & SetupReceivedStatus) != 0 ? 1u << 16 : 0)
            | (startOfFramePending ? 1u << 17 : 0);

        private uint EnabledInterrupts => ((RawInterrupts & registers[(int)Registers.InterruptEnable / 4])
            | registers[(int)Registers.InterruptForce / 4]) & 0xfffff;

        private bool CanProcessPackets => !ResetHeld && ClocksAvailable && connectionObserved && PhysicalConnection;

        private bool ClocksAvailable => clockSource.USBFrequency == 48000000 && clockSource.SystemFrequency != 0;

        private bool PhysicalConnection => connectionCount != 0
            && (registers[(int)Registers.MainControl / 4] & 3) == 1
            && (registers[(int)Registers.SIEControl / 4] & (1u << 16)) != 0;

        private readonly IRP2ClockSource clockSource;
        private readonly RP2_USB_DPRAM dpram;
        private readonly Endpoint[] endpoints;
        private readonly int[] bufferIndex;
        private readonly LimitTimer frameTimer;
        private readonly uint[] registers = new uint[RegisterBytes / 4];
        private uint sieStatus;
        private uint bufferStatus;
        private uint bufferCPU;
        private uint frameNumber;
        private int connectionCount;
        private bool startOfFramePending;
        private bool connectionObserved;
        private bool timingWarningEmitted;
        private uint pendingInputNotifications;
        private uint pendingStatusNotifications;
        private bool publishingInputNotifications;

        private const int EndpointCount = 16;
        private const int RegisterBytes = 0x100;
        private const int BufferControlOffset = 0x80;
        private const ushort BufferLengthMask = 0x3ff;
        private const ushort BufferAvailable = 1 << 10;
        private const ushort BufferStall = 1 << 11;
        private const ushort BufferReset = 1 << 12;
        private const ushort BufferFull = 1 << 15;
        private const uint SetupReceivedStatus = 1u << 17;
        private const uint BusResetStatus = 1u << 19;

        private readonly struct Buffer
        {
            public Buffer(int direction, int index, long controlOffset, long address, ushort control, bool doubleBuffered)
            {
                Direction = direction;
                Index = index;
                ControlOffset = controlOffset;
                Address = address;
                Control = control;
                DoubleBuffered = doubleBuffered;
            }

            public readonly int Direction;
            public readonly int Index;
            public readonly long ControlOffset;
            public readonly long Address;
            public readonly ushort Control;
            public readonly bool DoubleBuffered;
        }

        private sealed class Endpoint
        {
            public Endpoint(RP2_USB parent, int number)
            {
                this.parent = parent;
                this.number = number;
            }

            public void Reset()
            {
                foreach(var pending in pendingWrites) pending.Completion.TrySetCanceled();
                pendingWrites.Clear();
                parent.pendingInputNotifications &= ~(1u << number);
                if(inputStalled || outputStalled) parent.pendingStatusNotifications |= 1u << number;
                else parent.pendingStatusNotifications &= ~(1u << number);
                inputStalled = false;
                outputStalled = false;
            }

            public void SetupPacketWrite(SetupPacket packet)
            {
                if(number != 0)
                {
                    parent.Log(LogLevel.Warning, "SETUP token on non-control endpoint {0}", number);
                    return;
                }
                parent.Setup(packet);
            }

            public bool TryRead(out byte[] data)
            {
                try
                {
                    lock(parent.dpram)
                    {
                        if(!parent.TryGetBuffer(number, true, out var buffer) || (buffer.Control & BufferFull) == 0)
                        {
                            data = Array.Empty<byte>();
                            return false;
                        }
                        var length = buffer.Control & BufferLengthMask;
                        data = parent.dpram.ReadBytes(buffer.Address, length);
                        parent.CompleteBuffer(buffer, true, length);
                        return true;
                    }
                }
                finally
                {
                    parent.PublishPending();
                }
            }

            public void Write(byte[] data, Connection owner) => _ = WriteAsync(data, owner, CancellationToken.None);

            public async Task WriteAsync(byte[] data, Connection owner, CancellationToken token)
            {
                var pending = new PendingWrite(owner, (byte[])data.Clone());
                using(token.Register(() =>
                {
                    try
                    {
                        lock(parent.dpram)
                        {
                            pending.Completion.TrySetCanceled(token);
                            BufferReady();
                        }
                    }
                    finally
                    {
                        parent.PublishPending();
                    }
                }))
                {
                    try
                    {
                        lock(parent.dpram)
                        {
                            if(owner.Disposed || parent.ResetHeld || !parent.PhysicalConnection)
                            {
                                pending.Completion.TrySetCanceled();
                            }
                            else if(!pending.Completion.Task.IsCompleted)
                            {
                                pendingWrites.Enqueue(pending);
                                BufferReady();
                            }
                        }
                    }
                    finally
                    {
                        parent.PublishPending();
                    }
                    await pending.Completion.Task.ConfigureAwait(false);
                }
            }

            public void BufferReady()
            {
                lock(parent.dpram)
                {
                    if(processingBuffers)
                    {
                        return;
                    }
                    processingBuffers = true;
                    try
                    {
                        var newInputStalled = parent.IsEndpointStalled(number, true);
                        var newOutputStalled = parent.IsEndpointStalled(number, false);
                        if(newInputStalled != inputStalled || newOutputStalled != outputStalled)
                        {
                            inputStalled = newInputStalled;
                            outputStalled = newOutputStalled;
                            parent.pendingStatusNotifications |= 1u << number;
                        }
                        while(pendingWrites.Count != 0)
                        {
                            var pending = pendingWrites.Peek();
                            if(pending.Owner.Disposed || pending.Completion.Task.IsCompleted)
                            {
                                pending.Completion.TrySetCanceled();
                                pendingWrites.Dequeue();
                                continue;
                            }
                            if(outputStalled)
                            {
                                pendingWrites.Dequeue();
                                pending.Completion.TrySetException(new USBStallException());
                                continue;
                            }
                            if(!parent.TryGetBuffer(number, false, out var buffer)) break;
                            var count = Math.Min(64, pending.Data.Length - pending.Offset);
                            var capacity = buffer.Control & BufferLengthMask;
                            if(count > capacity)
                            {
                                parent.sieStatus |= 1u << 26;
                                parent.UpdateInterrupts();
                                parent.Log(LogLevel.Warning, "USB OUT packet exceeds endpoint {0} buffer capacity", number);
                                pending.Completion.TrySetException(new InvalidOperationException("USB OUT packet exceeds buffer capacity"));
                                pendingWrites.Dequeue();
                                continue;
                            }
                            parent.dpram.WriteBytes(buffer.Address, pending.Data.Skip(pending.Offset).Take(count).ToArray());
                            parent.CompleteBuffer(buffer, false, count);
                            pending.Offset += count;
                            if(pending.Offset == pending.Data.Length)
                            {
                                pendingWrites.Dequeue();
                                pending.Completion.TrySetResult(true);
                            }
                        }
                        if(parent.TryGetBuffer(number, true, out var input) && (input.Control & BufferFull) != 0)
                        {
                            parent.QueueInputNotification(number);
                        }
                    }
                    finally
                    {
                        processingBuffers = false;
                    }
                }
            }

            public void CancelPendingWrites(Connection owner)
            {
                var count = pendingWrites.Count;
                for(var i = 0; i < count; i++)
                {
                    var pending = pendingWrites.Dequeue();
                    if(pending.Owner != owner) pendingWrites.Enqueue(pending);
                    else pending.Completion.TrySetCanceled();
                }
            }

            public bool IsStalled(bool input) => parent.IsEndpointStalled(number, input);

            public void PublishAvailable() => NewPacket?.Invoke();

            public void PublishStatusChanged() => StatusChanged?.Invoke();

            public event Action NewPacket;
            public event Action StatusChanged;

            private readonly RP2_USB parent;
            private readonly int number;
            private readonly Queue<PendingWrite> pendingWrites = new Queue<PendingWrite>();

            private sealed class PendingWrite
            {
                public PendingWrite(Connection owner, byte[] data)
                {
                    Owner = owner;
                    Data = data;
                }

                public readonly Connection Owner;
                public readonly byte[] Data;
                public int Offset;
                public readonly TaskCompletionSource<bool> Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            private bool processingBuffers;
            private bool inputStalled;
            private bool outputStalled;
        }

        private sealed class Connection : IUSBConnection
        {
            public Connection(RP2_USB parent)
            {
                this.parent = parent;
                leases = new EndpointLease[EndpointCount];
            }

            public bool Disposed => disposed;

            public IUSBPipeSetup ConnectEndpointSetup(byte endpoint) => GetEndpoint(endpoint);

            public IUSBPipeRead ConnectEndpointRead(byte endpoint) => GetEndpoint(endpoint);

            public IUSBPipeWrite ConnectEndpointWrite(byte endpoint) => GetEndpoint(endpoint);

            public void Dispose()
            {
                try
                {
                    lock(parent.dpram)
                    {
                        if(disposed)
                        {
                            return;
                        }
                        disposed = true;
                        foreach(var lease in leases) lease?.Detach();
                        foreach(var endpoint in parent.endpoints) endpoint.CancelPendingWrites(this);
                        parent.connectionCount--;
                        parent.UpdateConnection();
                    }
                }
                finally
                {
                    parent.PublishPending();
                }
            }

            private EndpointLease GetEndpoint(byte endpoint)
            {
                lock(parent.dpram)
                {
                    var number = endpoint & 0x7f;
                    if(disposed || number >= EndpointCount) return null;
                    if(leases[number] == null) leases[number] = new EndpointLease(this, parent.endpoints[number]);
                    return leases[number];
                }
            }

            // Handles and pending host transfers belong to this attachment.
            // SIE/bus resets retain its subscriptions; disposal invalidates them.
            private sealed class EndpointLease : IUSBPipeSetup, IUSBPipeStatus
            {
                public EndpointLease(Connection owner, Endpoint endpoint)
                {
                    this.owner = owner;
                    this.endpoint = endpoint;
                    endpoint.NewPacket += PublishAvailable;
                    endpoint.StatusChanged += PublishStatusChanged;
                }

                public bool TryRead(out byte[] data)
                {
                    try
                    {
                        lock(owner.parent.dpram)
                        {
                            if(owner.disposed)
                            {
                                data = Array.Empty<byte>();
                                return false;
                            }
                            return endpoint.TryRead(out data);
                        }
                    }
                    finally
                    {
                        owner.parent.PublishPending();
                    }
                }

                public void Write(byte[] data)
                {
                    try
                    {
                        lock(owner.parent.dpram)
                        {
                            if(!owner.disposed) endpoint.Write(data, owner);
                        }
                    }
                    finally
                    {
                        owner.parent.PublishPending();
                    }
                }

                public Task WriteAsync(byte[] data, CancellationToken token = default)
                {
                    // The endpoint owns cancellation and completion under the DPRAM
                    // monitor; no transfer can complete just by entering its queue.
                    return endpoint.WriteAsync(data, owner, token);
                }

                public void SetupPacketWrite(SetupPacket packet)
                {
                    try
                    {
                        lock(owner.parent.dpram)
                        {
                            if(!owner.disposed) endpoint.SetupPacketWrite(packet);
                        }
                    }
                    finally
                    {
                        owner.parent.PublishPending();
                    }
                }

                public event Action NewPacket
                {
                    add
                    {
                        lock(owner.parent.dpram)
                        {
                            if(!owner.disposed) handlers += value;
                        }
                    }
                    remove
                    {
                        lock(owner.parent.dpram) handlers -= value;
                    }
                }

                public bool IsStalled(USBDirection direction)
                {
                    lock(owner.parent.dpram)
                    {
                        return !owner.disposed && endpoint.IsStalled(direction == USBDirection.DeviceToHost);
                    }
                }

                public event Action StatusChanged
                {
                    add
                    {
                        lock(owner.parent.dpram)
                        {
                            if(!owner.disposed) statusHandlers += value;
                        }
                    }
                    remove
                    {
                        lock(owner.parent.dpram) statusHandlers -= value;
                    }
                }

                private void PublishStatusChanged()
                {
                    Action callback;
                    lock(owner.parent.dpram)
                    {
                        if(owner.disposed) return;
                        callback = statusHandlers;
                    }
                    callback?.Invoke();
                }

                public void Detach()
                {
                    endpoint.NewPacket -= PublishAvailable;
                    endpoint.StatusChanged -= PublishStatusChanged;
                    handlers = null;
                    statusHandlers = null;
                }

                private void PublishAvailable()
                {
                    Action callback;
                    lock(owner.parent.dpram)
                    {
                        if(owner.disposed) return;
                        callback = handlers;
                    }
                    callback?.Invoke();
                }

                private readonly Connection owner;
                private readonly Endpoint endpoint;
                private Action handlers;
                private Action statusHandlers;
            }

            private readonly RP2_USB parent;
            private readonly EndpointLease[] leases;
            private bool disposed;
        }

        private enum Registers : long
        {
            Address = 0x00,
            MainControl = 0x40,
            StartOfFrame = 0x48,
            SIEControl = 0x4c,
            SIEStatus = 0x50,
            BufferStatus = 0x58,
            BufferCPU = 0x5c,
            Abort = 0x60,
            AbortDone = 0x64,
            StallArm = 0x68,
            InterruptRaw = 0x8c,
            InterruptEnable = 0x90,
            InterruptForce = 0x94,
            InterruptStatus = 0x98,
        }
    }
}
