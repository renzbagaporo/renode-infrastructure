//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.CPU
{
    // RP2350 datasheet 3.5: a DPv3 with two AHB5 MEM-APv2s and an always-on RP-AP.
    public class RP2350_DebugPort : SerialWireDebugPort
    {
        public RP2350_DebugPort(IMachine machine, CortexM cpu0, CortexM cpu1)
        {
            this.machine = machine;
            aps = new[] { new MemoryAccessPort(machine.SystemBus, cpu0), new MemoryAccessPort(machine.SystemBus, cpu1) };
            if(cpu0 != null) cpu0.EnableMemoryMappedDebug = true;
            if(cpu1 != null) cpu1.EnableMemoryMappedDebug = true;
            Reset();
        }

        protected override void ResetBackend()
        {
            select = select1 = readBuffer = rpControl = 0;
            control = 0x40;
            if(aps != null) foreach(var ap in aps) ap.Reset();
        }

        protected override void LineReset()
        {
            select = 0;
        }

        protected override void ResetTarget()
        {
            machine.RequestReset();
        }

        protected override Acknowledgement BeginTransfer(bool accessPort, bool read, byte address, out uint value)
        {
            value = 0;
            if(!accessPort)
            {
                if(read) value = ReadDP(address);
                return Acknowledgement.Ok;
            }
            if((control & 0xa2) != 0) return Acknowledgement.Fault;
            var apAddress = ((ulong)select1 << 32) | (select & 0xfffffff0u) | address;
            // The RP-AP is accessible while the switched core debug domain is powered off.
            if(apAddress < 0x80000 && (control & 0x20000000) == 0) return Acknowledgement.Wait;
            if(!IsAPAddress(apAddress))
            {
                control |= 0x20;
                return Acknowledgement.Fault;
            }
            if(!read) return Acknowledgement.Ok;
            value = readBuffer;
            try
            {
                readBuffer = ReadAP(apAddress);
            }
            catch(AccessFault)
            {
                control |= 0x20;
                return Acknowledgement.Fault;
            }
            return Acknowledgement.Ok;
        }

        protected override void CompleteWrite(bool accessPort, byte address, uint value)
        {
            if(!accessPort)
            {
                if(address == 0)
                {
                    if((value & 2) != 0) control &= ~0x10u;
                    if((value & 4) != 0) control &= ~0x20u;
                    if((value & 8) != 0) control &= ~0x80u;
                    if((value & 16) != 0) control &= ~2u;
                }
                else if(address == 8) select = value;
                else if(address == 4 && (select & 0xf) == 5) select1 = value;
                else if(address == 4 && (select & 0xf) == 0)
                {
                    control = (control & 0xf2) | (value & 0x54000001);
                    if((value & 0x10000000) != 0) control |= 0x20000000;
                    if((value & 0x40000000) != 0) control |= 0x80000000;
                    if((value & 0x04000000) != 0) control |= 0x08000000;
                }
                return;
            }
            try { WriteAP(((ulong)select1 << 32) | (select & 0xfffffff0u) | address, value); }
            catch(AccessFault) { control |= 0x20; }
        }

        protected override void WriteParityError()
        {
            control |= 0x80;
        }

        protected override bool SelectTarget(uint value)
        {
            return value == 0x01002927;
        }

        private uint ReadDP(byte address)
        {
            var bank = select & 0xf;
            if(address == 0)
            {
                switch(bank)
                {
                case 0: return 0x4c013477;
                case 1: return 32;
                case 2: return 1;
                default: return 0;
                }
            }
            if(address == 4)
            {
                switch(bank)
                {
                case 0: return control;
                case 1: return 0; // DLCR: one turnaround cycle
                case 2: return 0x01002927;
                case 3: return 1; // SWD protocol version 2 is encoded as 1.
                case 5: return select1;
                default: return 0;
                }
            }
            if(address == 12) return readBuffer;
            return 0;
        }

        private static bool IsAPAddress(ulong address)
        {
            return address < 0x1000 || (address >= 0x2000 && address < 0x3000)
                || (address >= 0x4000 && address < 0x5000) || (address >= 0x80000 && address < 0x81000);
        }

        private uint ReadAP(ulong address)
        {
            if(address < 0x1000)
            {
                if(address == 0) return 0x2003;
                if(address == 4) return 0x4003;
                return ComponentRegister((uint)address, 0x09093004, 9, 0x47700af7);
            }
            if(address >= 0x80000)
            {
                if(address == 0x80000) return rpControl;
                // The RP-AP is not a CoreSight component. The datasheet specifies
                // an implementation-defined IDR at 0xdfc, with an undefined reset.
                return 0;
            }
            return aps[address >= 0x4000 ? 1 : 0].Read((uint)(address & 0xfff));
        }

        private void WriteAP(ulong address, uint value)
        {
            if(address == 0x80000)
            {
                var rescueChanged = ((rpControl ^ value) & 0x80000000) != 0;
                rpControl = value & 0xc000007f;
                if(rescueChanged) RescueResetChanged?.Invoke((value & 0x80000000) != 0);
            }
            else if(address >= 0x2000 && address < 0x5000) aps[address >= 0x4000 ? 1 : 0].Write((uint)(address & 0xfff), value);
        }

        // This reports physical reset assertion/release to the SoC power/reset model.
        public event Action<bool> RescueResetChanged;

        private static uint ComponentRegister(uint offset, uint peripheralId, uint componentClass, uint architecture)
        {
            if(offset == 0xfbc) return architecture;
            if(offset == 0xfd0) return (peripheralId >> 24) & 0xf;
            if(offset >= 0xfe0 && offset <= 0xfe8) return (peripheralId >> (int)((offset - 0xfe0) * 2)) & 0xff;
            switch(offset)
            {
            case 0xff0: return 0xd;
            case 0xff4: return componentClass << 4;
            case 0xff8: return 5;
            case 0xffc: return 0xb1;
            default: return 0;
            }
        }

        private sealed class MemoryAccessPort
        {
            public MemoryAccessPort(IBusController bus, CortexM cpu)
            {
                this.bus = bus;
                this.cpu = cpu;
            }

            public void Reset() { csw = 0x0b800052; tar = 0; }

            public uint Read(uint offset)
            {
                switch(offset)
                {
                case 0xd00: return csw;
                case 0xd04: return tar;
                case 0xd0c: return Transfer(false, 0);
                // Ten-bit TAR incrementer. Optional DAR and extended error
                // control registers are not implemented by this endpoint.
                case 0xdf4: return 0x00010000;
                case 0xdf8: return 0xe00ff003;
                // RP2350's AHB5 MEM-AP with enhanced HPROT, revision 3.
                case 0xdfc: return 0x34770008;
                default:
                    if(offset >= 0xd10 && offset <= 0xd1c) return ReadMemory((tar & ~0xfu) | (offset & 0xc), 2);
                    return ComponentRegister(offset, 0x043bb9e3, 9, 0x47700a17);
                }
            }

            public void Write(uint offset, uint value)
            {
                switch(offset)
                {
                case 0xd00:
                    // Single address increment and 8/16/32-bit accesses are supported.
                    if((value & 7) <= 2 && ((value >> 4) & 3) <= 1) csw = (value & 0xff000037) | 0x00800040;
                    return;
                case 0xd04: tar = value; return;
                case 0xd0c: Transfer(true, value); return;
                default:
                    if(offset >= 0xd10 && offset <= 0xd1c) WriteMemory((tar & ~0xfu) | (offset & 0xc), 2, value);
                    return;
                }
            }

            private uint Transfer(bool write, uint value)
            {
                var size = (int)(csw & 7);
                if((tar & ((1u << size) - 1)) != 0) throw new AccessFault();
                if(write) WriteMemory(tar, size, value);
                else value = ReadMemory(tar, size);
                if((csw & 0x30) == 0x10) tar = (tar & ~0x3ffu) | ((tar + (1u << size)) & 0x3ff);
                return value;
            }

            private uint ReadMemory(uint address, int size)
            {
                if(bus.WhatPeripheralIsAt(address, cpu, TransactionState) == null) throw new AccessFault();
                switch(size)
                {
                case 0: return (uint)bus.ReadByte(address, cpu, TransactionState) << (int)((address & 3) * 8);
                case 1: return (uint)bus.ReadWord(address, cpu, TransactionState) << (int)((address & 2) * 8);
                default: return bus.ReadDoubleWord(address, cpu, TransactionState);
                }
            }

            private void WriteMemory(uint address, int size, uint value)
            {
                if(bus.WhatPeripheralIsAt(address, cpu, TransactionState) == null) throw new AccessFault();
                switch(size)
                {
                case 0: bus.WriteByte(address, (byte)(value >> (int)((address & 3) * 8)), cpu, TransactionState); return;
                case 1: bus.WriteWord(address, (ushort)(value >> (int)((address & 2) * 8)), cpu, TransactionState); return;
                default: bus.WriteDoubleWord(address, value, cpu, TransactionState); return;
                }
            }

            private ulong? TransactionState => cpu == null ? (ulong?)null : (ulong)new CortexM.ContextState
            {
                Privileged = (csw & (1u << 25)) != 0,
                CpuSecure = (csw & (1u << 30)) == 0,
                AttributionSecure = (csw & (1u << 30)) == 0,
                DebugAccess = true,
            };

            private readonly IBusController bus;
            private readonly CortexM cpu;
            private uint csw, tar;
        }

        private sealed class AccessFault : Exception { }
        private readonly IMachine machine;
        private readonly MemoryAccessPort[] aps;
        private uint select, select1, control, readBuffer, rpControl;
    }
}
