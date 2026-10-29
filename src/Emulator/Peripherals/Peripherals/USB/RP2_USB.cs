//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.USB;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities.Packets;

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
            };
            dpram.BufferControlWritten += BufferControlWritten;
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
        }

        public IUSBConnection ConnectUSB()
        {
            lock(dpram)
            {
                connectionCount++;
                UpdateConnection();
                return new Connection(this);
            }
        }

        public byte Address => (byte)(registers[(int)Registers.Address / 4] & 0x7f);

        public GPIO IRQ { get; }

        protected override object SyncRoot => dpram;

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
            lock(dpram)
            {
                if(!CanProcessPackets)
                {
                    this.Log(LogLevel.Warning, "USB SETUP token received while the serial interface is inactive");
                    return;
                }
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

        private sealed class Endpoint : IUSBPipeSetup
        {
            public Endpoint(RP2_USB parent, int number)
            {
                this.parent = parent;
                this.number = number;
            }

            public void Reset()
            {
                pendingWrites.Clear();
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

            public void Write(byte[] data)
            {
                lock(parent.dpram)
                {
                    if(parent.ResetHeld || !parent.PhysicalConnection) return;
                    // IUSBPipeWrite has no NAK result. Queue host-side pending
                    // transfers while clocks are stopped; the SIE accepts them
                    // only when clocking and buffer ownership allow completion.
                    // Non-isochronous full-speed endpoints carry at most 64 bytes
                    // per token. Preserve the zero-length status packet as well.
                    var offset = 0;
                    do
                    {
                        var count = Math.Min(64, data.Length - offset);
                        var packet = new byte[count];
                        Array.Copy(data, offset, packet, 0, count);
                        pendingWrites.Enqueue(packet);
                        offset += count;
                    }
                    while(offset < data.Length);
                    BufferReady();
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
                        while(pendingWrites.Count != 0 && parent.TryGetBuffer(number, false, out var buffer))
                        {
                            var data = pendingWrites.Dequeue();
                            var capacity = buffer.Control & BufferLengthMask;
                            if(data.Length > capacity)
                            {
                                parent.sieStatus |= 1u << 26;
                                parent.UpdateInterrupts();
                                parent.Log(LogLevel.Warning, "USB OUT packet exceeds endpoint {0} buffer capacity", number);
                                continue;
                            }
                            parent.dpram.WriteBytes(buffer.Address, data);
                            parent.CompleteBuffer(buffer, false, data.Length);
                        }
                        if(parent.TryGetBuffer(number, true, out var input) && (input.Control & BufferFull) != 0)
                        {
                            NewPacket?.Invoke();
                        }
                    }
                    finally
                    {
                        processingBuffers = false;
                    }
                }
            }

            public event Action NewPacket;

            private readonly RP2_USB parent;
            private readonly int number;
            private readonly Queue<byte[]> pendingWrites = new Queue<byte[]>();
            private bool processingBuffers;
        }

        private sealed class Connection : IUSBConnection
        {
            public Connection(RP2_USB parent)
            {
                this.parent = parent;
            }

            public IUSBPipeSetup ConnectEndpointSetup(byte endpoint) => GetEndpoint(endpoint);

            public IUSBPipeRead ConnectEndpointRead(byte endpoint) => GetEndpoint(endpoint);

            public IUSBPipeWrite ConnectEndpointWrite(byte endpoint) => GetEndpoint(endpoint);

            public void Dispose()
            {
                lock(parent.dpram)
                {
                    if(disposed)
                    {
                        return;
                    }
                    disposed = true;
                    parent.connectionCount--;
                    parent.UpdateConnection();
                }
            }

            private Endpoint GetEndpoint(byte endpoint)
            {
                return !disposed && (endpoint & 0x7f) < EndpointCount ? parent.endpoints[endpoint & 0x7f] : null;
            }

            private readonly RP2_USB parent;
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
