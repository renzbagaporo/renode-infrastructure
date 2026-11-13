//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.CPU
{
    // RP2040 datasheet 2.3.4: SWD multidrop with three ADIv5 DPv2 debug ports on
    // one wire, chosen by TARGETSEL after a line reset: core 0 (0x01002927),
    // core 1 (0x11002927) and the rescue DP (0xf1002927). Each core DP has one
    // AHB-AP at APSEL 0 that accesses memory as its core. The rescue DP's
    // power-up request resets the chip; the boot ROM's rescue halt is not
    // modelled. RUN low holds both cores in reset.
    public class RP2040_DebugPort : SerialWireDebugPort
    {
        public RP2040_DebugPort(IMachine machine, CortexM cpu0, CortexM cpu1)
        {
            this.machine = machine;
            cores = new[] { cpu0, cpu1 };
            ports = new[]
            {
                new Port(0x0, new MemoryAccessPort(machine.SystemBus, cpu0)),
                new Port(0x1, new MemoryAccessPort(machine.SystemBus, cpu1)),
                new Port(0xf, null),
            };
            // MachineReset runs after peripheral reset while cores are paused.
            // Reapply the physical RUN level before they can resume.
            machine.MachineReset += _ => ApplyRunHold();
            if(cpu0 != null) cpu0.EnableMemoryMappedDebug = true;
            if(cpu1 != null) cpu1.EnableMemoryMappedDebug = true;
            Reset();
        }

        protected override void ResetBackend()
        {
            if(ports == null) return;
            foreach(var port in ports) port.Reset();
            selected = ports[0];
        }

        protected override void LineReset()
        {
            foreach(var port in ports) port.Select = 0;
        }

        protected override bool SelectTarget(uint value)
        {
            foreach(var port in ports)
            {
                if(value != (TargetId | ((uint)port.Instance << 28))) continue;
                selected = port;
                return true;
            }
            return false;
        }

        protected override void ResetTarget() => machine.RequestReset();

        protected override void ReleaseTarget()
        {
            // A release can overtake a queued reset, or be followed by another
            // assertion. Reconcile the current physical level at the boundary.
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => ApplyRunHold());
        }

        private void ApplyRunHold()
        {
            var hold = ResetPinAsserted;
            foreach(var core in cores) core?.SetExternalResetHold(hold);
        }

        protected override Acknowledgement BeginTransfer(bool accessPort, bool read, byte address, out uint value)
        {
            var port = selected;
            value = 0;
            if(!accessPort)
            {
                if(read) value = ReadDP(port, address);
                return Acknowledgement.Ok;
            }
            if((port.Control & 0xa2) != 0) return Acknowledgement.Fault;
            if((port.Control & 0x20000000) == 0) return Acknowledgement.Wait;
            if(!read) return Acknowledgement.Ok;
            value = port.ReadBuffer;
            try
            {
                port.ReadBuffer = ReadAP(port, address);
            }
            catch(AccessFault fault)
            {
                this.Log(LogLevel.Debug, "Core {0} AP read of 0x{1:X2} faulted: {2}", port.Instance, (port.Select & 0xf0) | address, fault.Message);
                port.Control |= 0x20;
                return Acknowledgement.Fault;
            }
            return Acknowledgement.Ok;
        }

        protected override void CompleteWrite(bool accessPort, byte address, uint value)
        {
            var port = selected;
            if(!accessPort)
            {
                if(address == 0)
                {
                    if((value & 2) != 0) port.Control &= ~0x10u;
                    if((value & 4) != 0) port.Control &= ~0x20u;
                    if((value & 8) != 0) port.Control &= ~0x80u;
                    if((value & 16) != 0) port.Control &= ~2u;
                }
                else if(address == 8) port.Select = value;
                else if(address == 4 && (port.Select & 0xf) == 0) WriteControl(port, value);
                return;
            }
            try { WriteAP(port, address, value); }
            catch(AccessFault fault)
            {
                this.Log(LogLevel.Debug, "Core {0} AP write of 0x{1:X2} faulted: {2}", port.Instance, (port.Select & 0xf0) | address, fault.Message);
                port.Control |= 0x20;
            }
        }

        protected override void WriteParityError()
        {
            this.Log(LogLevel.Warning, "SWD write data parity error on core {0}'s DP", selected.Instance);
            selected.Control |= 0x80;
        }

        private void WriteControl(Port port, uint value)
        {
            var powerUpRequested = (port.Control & 0x10000000) == 0 && (value & 0x10000000) != 0;
            port.Control = (port.Control & 0xf2) | (value & 0x54000001);
            if((value & 0x10000000) != 0) port.Control |= 0x20000000;
            if((value & 0x40000000) != 0) port.Control |= 0x80000000;
            if((value & 0x04000000) != 0) port.Control |= 0x08000000;
            if(powerUpRequested && port.AccessPort == null)
            {
                this.Log(LogLevel.Warning, "Rescue DP power-up request: resetting the chip; the boot ROM rescue halt is not modelled");
                machine.RequestReset();
            }
        }

        private static uint ReadDP(Port port, byte address)
        {
            var bank = port.Select & 0xf;
            switch(address)
            {
            case 0: return DebugPortId;
            case 4:
                switch(bank)
                {
                case 0: return port.Control;
                case 1: return 0; // DLCR: one turnaround cycle
                case 2: return TargetId;
                case 3: return ((uint)port.Instance << 28) | 1; // DLPIDR: TINSTANCE, SWD protocol version 2
                default: return 0;
                }
            case 12: return port.ReadBuffer;
            default: return 0;
            }
        }

        private static uint ReadAP(Port port, byte address)
        {
            // Only APSEL 0 exists, and only on the core DPs.
            if(port.AccessPort == null || (port.Select >> 24) != 0) return 0;
            return port.AccessPort.Read((port.Select & 0xf0) | address);
        }

        private static void WriteAP(Port port, byte address, uint value)
        {
            if(port.AccessPort == null || (port.Select >> 24) != 0) return;
            port.AccessPort.Write((port.Select & 0xf0) | address, value);
        }

        private sealed class Port
        {
            public Port(int instance, MemoryAccessPort accessPort)
            {
                Instance = instance;
                AccessPort = accessPort;
            }

            public void Reset()
            {
                Select = ReadBuffer = 0;
                Control = 0;
                AccessPort?.Reset();
            }

            public readonly int Instance;
            public readonly MemoryAccessPort AccessPort;
            public uint Select, Control, ReadBuffer;
        }

        // The Cortex-M0+ AHB-AP: CSW, TAR, DRW, banked data, BASE and IDR.
        private sealed class MemoryAccessPort
        {
            public MemoryAccessPort(IBusController bus, CortexM cpu)
            {
                this.bus = bus;
                this.cpu = cpu;
            }

            public void Reset() { csw = 0x03000052; tar = 0; }

            public uint Read(uint offset)
            {
                switch(offset)
                {
                case 0x00: return csw;
                case 0x04: return tar;
                case 0x0c: return Transfer(false, 0);
                case 0xf4: return 0;
                case 0xf8: return 0xe00ff003;
                case 0xfc: return 0x04770031;
                default:
                    if(offset >= 0x10 && offset <= 0x1c) return ReadMemory((tar & ~0xfu) | (offset & 0xc), 2);
                    return 0;
                }
            }

            public void Write(uint offset, uint value)
            {
                switch(offset)
                {
                case 0x00:
                    // Single address increment and 8/16/32-bit accesses are supported.
                    if((value & 7) <= 2 && ((value >> 4) & 3) <= 1) csw = (value & 0x7f000037) | 0x40;
                    return;
                case 0x04: tar = value; return;
                case 0x0c: Transfer(true, value); return;
                default:
                    if(offset >= 0x10 && offset <= 0x1c) WriteMemory((tar & ~0xfu) | (offset & 0xc), 2, value);
                    return;
                }
            }

            private uint Transfer(bool write, uint value)
            {
                var size = (int)(csw & 7);
                if((tar & ((1u << size) - 1)) != 0) throw new AccessFault($"misaligned TAR 0x{tar:X8} for CSW 0x{csw:X8}");
                if(write) WriteMemory(tar, size, value);
                else value = ReadMemory(tar, size);
                if((csw & 0x30) == 0x10) tar = (tar & ~0x3ffu) | ((tar + (1u << size)) & 0x3ff);
                return value;
            }

            private uint ReadMemory(uint address, int size)
            {
                try
                {
                    if(bus.WhatPeripheralIsAt(address, cpu, TransactionState) == null) throw new AccessFault($"nothing mapped at 0x{address:X8}");
                    switch(size)
                    {
                    case 0: return (uint)bus.ReadByte(address, cpu, TransactionState) << (int)((address & 3) * 8);
                    case 1: return (uint)bus.ReadWord(address, cpu, TransactionState) << (int)((address & 2) * 8);
                    default: return bus.ReadDoubleWord(address, cpu, TransactionState);
                    }
                }
                catch(BusAccessException error)
                {
                    // An AHB error response becomes the sticky fault.
                    throw new AccessFault($"bus error at 0x{address:X8}: {error.Message}");
                }
            }

            private void WriteMemory(uint address, int size, uint value)
            {
                try
                {
                    if(bus.WhatPeripheralIsAt(address, cpu, TransactionState) == null) throw new AccessFault($"nothing mapped at 0x{address:X8}");
                    switch(size)
                    {
                    case 0: bus.WriteByte(address, (byte)(value >> (int)((address & 3) * 8)), cpu, TransactionState); return;
                    case 1: bus.WriteWord(address, (ushort)(value >> (int)((address & 2) * 8)), cpu, TransactionState); return;
                    default: bus.WriteDoubleWord(address, value, cpu, TransactionState); return;
                    }
                }
                catch(BusAccessException error)
                {
                    // An AHB error response becomes the sticky fault.
                    throw new AccessFault($"bus error at 0x{address:X8}: {error.Message}");
                }
            }

            private ulong? TransactionState => cpu == null ? (ulong?)null : (ulong)new CortexM.ContextState
            {
                Privileged = true,
                CpuSecure = true,
                AttributionSecure = true,
                DebugAccess = true,
            };

            private readonly IBusController bus;
            private readonly CortexM cpu;
            private uint csw, tar;
        }

        private sealed class AccessFault : Exception
        {
            public AccessFault(string message) : base(message) { }
        }

        private const uint DebugPortId = 0x0bc12477;
        private const uint TargetId = 0x01002927;

        private readonly IMachine machine;
        private readonly CortexM[] cores;
        private readonly Port[] ports;
        private Port selected;
    }
}
