//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class RP2040_PSM : RP2APBPeripheral
    {
        public RP2040_PSM(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1) : base(machine)
        {
            cores = new[] { core0, core1 };
            nvics = new[] { nvic0, nvic1 };
            Reset();
        }

        public override void Reset()
        {
            forceOn = 0;
            forceOff = 0;
            watchdogSelect = 0;
            foreach(var core in cores)
            {
                core.CpuWaitSignal.Unset();
            }
        }

        protected override uint ReadRegister(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.ForceOn:
                return forceOn;
            case Registers.ForceOff:
                return forceOff;
            case Registers.WatchdogSelect:
                return watchdogSelect;
            case Registers.Done:
                // The modeled chip has completed power-on sequencing. Forced-off
                // domains remain unavailable until firmware releases them.
                return DomainMask & ~forceOff;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch((Registers)offset)
            {
            case Registers.ForceOn:
                forceOn = ApplyOperation(forceOn, value, operation, DomainMask);
                break;
            case Registers.ForceOff:
                var previous = forceOff;
                forceOff = ApplyOperation(forceOff, value, operation, DomainMask);
                UpdateProcessorReset(previous);
                break;
            case Registers.WatchdogSelect:
                watchdogSelect = ApplyOperation(watchdogSelect, value, operation, DomainMask);
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private void UpdateProcessorReset(uint previous)
        {
            var unsupportedDomains = (previous ^ forceOff) & ~ProcessorMask;
            if(unsupportedDomains != 0)
            {
                this.Log(LogLevel.Warning, "Power gating for PSM domains 0x{0:x} is not implemented", unsupportedDomains);
            }
            for(var core = 0; core < cores.Length; core++)
            {
                var mask = 1u << (Processor0Bit + core);
                if(((previous ^ forceOff) & mask) == 0)
                {
                    continue;
                }
                var held = (forceOff & mask) != 0;
                cores[core].CpuWaitSignal.Set(held);
                if(held)
                {
                    nvics[core].Reset();
                    cores[core].Reset();
                }
            }
        }

        private readonly CortexM[] cores;
        private readonly NVIC[] nvics;
        private uint forceOn;
        private uint forceOff;
        private uint watchdogSelect;

        private const int Processor0Bit = 15;
        private const uint ProcessorMask = 3u << Processor0Bit;
        private const uint DomainMask = 0x1ffff;

        private enum Registers : long
        {
            ForceOn = 0x0,
            ForceOff = 0x4,
            WatchdogSelect = 0x8,
            Done = 0xc,
        }
    }
}
