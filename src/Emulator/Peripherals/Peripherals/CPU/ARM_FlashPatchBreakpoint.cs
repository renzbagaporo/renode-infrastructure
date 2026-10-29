// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.CPU
{
    // Arm DDI0553B.y B14.5 and D1.2.108-122; eight code comparators as
    // implemented on RP2350 Cortex-M33. Armv8-M does not support flash remap.
    public sealed class ARM_FlashPatchBreakpoint : IDoubleWordPeripheral, IWordPeripheral, IBytePeripheral, IKnownSize
    {
        public ARM_FlashPatchBreakpoint(IMachine machine, CortexM cpu)
        {
            if(cpu.ArchitectureVersion < 8)
            {
                throw new ConstructionException("FPBv2 requires an Armv8-M CPU");
            }
            this.machine = machine;
            this.cpu = cpu;
            Reset();
        }

        public uint ReadDoubleWord(long offset)
        {
            ValidateAccess(offset);
            lock(sync)
            {
                if(offset >= 8 && offset < 0x200)
                {
                    var index = (int)((offset - 8) / 4);
                    return index < comparators.Length ? comparators[index] : 0;
                }
                switch(offset)
                {
                case 0: return 0x10000080u | (enabled ? 1u : 0);
                case 4: return 0; // FP_REMAP: no remapping in Armv8-M.
                case 0xfb0: case 0xfb4: return 0; // No software lock.
                case 0xfbc: return 0x47701a03; // FPBv2 CoreSight architecture.
                case 0xfcc: return 0;
                case 0xfd0: return 4;
                case 0xfd4: case 0xfd8: case 0xfdc: return 0;
                case 0xfe0: return 0x21;
                case 0xfe4: return 0xbd;
                case 0xfe8: return 0xb;
                case 0xfec: return 0;
                case 0xff0: return 0xd;
                case 0xff4: return 0x90;
                case 0xff8: return 5;
                case 0xffc: return 0xb1;
                default: return 0;
                }
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            ValidateAccess(offset);
            lock(sync)
            {
                if(offset == 0)
                {
                    if((value & 2) == 0) return;
                    enabled = (value & 1) != 0;
                }
                else if(offset >= 8 && offset < 8 + ComparatorCount * 4)
                {
                    comparators[(offset - 8) / 4] = value;
                }
                else
                {
                    return;
                }
                PublishConfiguration();
            }
        }

        public void Reset()
        {
            lock(sync)
            {
                // The architectural reset fields specify Cold reset. A CPU's
                // Warm reset retains this separately instantiated debug block.
                enabled = false;
                Array.Clear(comparators, 0, comparators.Length);
                PublishConfiguration();
            }
        }

        // FPB registers are word-only. Faulting narrow accesses is a supported
        // choice for their architecturally UNPREDICTABLE behavior.
        public byte ReadByte(long offset) => throw new BusAccessException(BusAccessError.CommandError);
        public ushort ReadWord(long offset) => throw new BusAccessException(BusAccessError.CommandError);
        public void WriteByte(long offset, byte value) => throw new BusAccessException(BusAccessError.CommandError);
        public void WriteWord(long offset, ushort value) => throw new BusAccessException(BusAccessError.CommandError);

        public long Size => 0x1000;

        private void PublishConfiguration()
        {
            var addresses = new HashSet<uint>();
            if(enabled)
            {
                foreach(var comparator in comparators)
                {
                    if((comparator & 1) != 0) addresses.Add(comparator & ~1u);
                }
            }
            var snapshot = new uint[addresses.Count];
            addresses.CopyTo(snapshot);
            cpu.UpdateFlashPatchBreakpoints(snapshot);
        }

        private void ValidateAccess(long offset)
        {
            if((offset & 3) != 0) throw new BusAccessException(BusAccessError.CommandError);
            var bus = machine.SystemBus;
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state))
            {
                if(!state.Privileged) throw new BusAccessException(BusAccessError.CommandError);
            }
            else if(bus.TryGetCurrentCPU(out var origin) && origin is CortexM core
                && (core.XProgramStatusRegister & 0x1ff) == 0 && (core.Control & 1) != 0)
            {
                throw new BusAccessException(BusAccessError.CommandError);
            }
        }

        private bool enabled;
        private readonly uint[] comparators = new uint[ComparatorCount];
        private readonly object sync = new object();
        private readonly IMachine machine;
        private readonly CortexM cpu;
        private const int ComparatorCount = 8;
    }
}
