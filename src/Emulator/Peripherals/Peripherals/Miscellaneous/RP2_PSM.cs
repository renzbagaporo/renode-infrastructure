//
// Copyright (c) 2026 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public abstract class RP2_PSM : RP2APBPeripheral
    {
        public override void Reset()
        {
            lock(SyncRoot)
            {
                Interlocked.Increment(ref resetGeneration);
                forceOn = forceOff = watchdogSelect = 0;
                for(var core = 0; core < cores.Length; core++)
                {
                    requestedReset[core] = completedReset[core] = 0;
                }
                QueueProcessorUpdate();
            }
        }

        protected RP2_PSM(IMachine machine, CortexM core0, CortexM core1, NVIC nvic0, NVIC nvic1,
            int processor0Bit, uint domainMask) : base(machine)
        {
            cores = new[] { core0, core1 };
            nvics = new[] { nvic0, nvic1 };
            this.processor0Bit = processor0Bit;
            this.domainMask = domainMask;
            ProcessorMask = 3u << processor0Bit;
            // Board construction may park a secondary PE until the power
            // sequencer is attached. Runtime holds belong to their own sources.
            foreach(var core in cores) core.CpuWaitSignal.Unset();
            Reset();
        }

        protected override uint ReadRegister(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.ForceOn:
                return forceOn;
            case Registers.ForceOff:
                // The SDK polls FRCE_OFF itself to confirm that the PE is in
                // reset. A queued request cannot acknowledge that transition.
                return (forceOff & ~ProcessorMask) | appliedProcessorReset;
            case Registers.WatchdogSelect:
                return watchdogSelect;
            case Registers.Done:
                var unavailable = forceOff | appliedProcessorReset | releasingProcessorReset;
                for(var core = 0; core < cores.Length; core++)
                {
                    if(requestedReset[core] != completedReset[core]) unavailable |= ProcessorBit(core);
                }
                return domainMask & ~unavailable;
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
                forceOn = ApplyOperation(forceOn, value, operation, domainMask);
                break;
            case Registers.ForceOff:
                var previous = forceOff;
                forceOff = ApplyOperation(forceOff, value, operation, domainMask);
                var unsupported = (previous ^ forceOff) & ~ProcessorMask;
                if(unsupported != 0)
                {
                    this.Log(LogLevel.Warning, "Power gating for PSM domains 0x{0:x} is not implemented", unsupported);
                }
                for(var core = 0; core < cores.Length; core++)
                {
                    // Retain an assertion edge even if firmware clears the
                    // request before the synchronization callback can run.
                    if((~previous & forceOff & ProcessorBit(core)) != 0) requestedReset[core]++;
                }
                QueueProcessorUpdate();
                break;
            case Registers.WatchdogSelect:
                watchdogSelect = ApplyOperation(watchdogSelect, value, operation, domainMask);
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private void QueueProcessorUpdate()
        {
            if(processorUpdateQueued) return;
            processorUpdateQueued = true;
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => ApplyProcessorUpdates());
        }

        private void ApplyProcessorUpdates()
        {
            while(true)
            {
                int core;
                int generation;
                ulong resetSequence;
                bool needsReset;
                bool held;
                lock(SyncRoot)
                {
                    for(core = 0; core < cores.Length; core++)
                    {
                        var mask = ProcessorBit(core);
                        if(requestedReset[core] != completedReset[core]
                            || ((forceOff ^ appliedProcessorReset) & mask) != 0) break;
                    }
                    if(core == cores.Length)
                    {
                        processorUpdateQueued = false;
                        return;
                    }
                    generation = resetGeneration;
                    needsReset = requestedReset[core] != completedReset[core];
                    // Each accepted assertion edge receives a real reset, even
                    // when several pulses arrive before this callback runs.
                    resetSequence = completedReset[core] + (needsReset ? 1UL : 0UL);
                    held = (forceOff & ProcessorBit(core)) != 0;
                }

                // No clock, bus, PSM or CPU state monitor is owned here. A peer
                // blocked in MMIO can finish before Pause/Reset joins it.
                if(needsReset)
                {
                    cores[core].SetPowerSequencerResetHold(true);
                    nvics[core].Reset();
                    cores[core].Reset();
                    // Reset requests a pause. Normalize it through Resume while
                    // our independent reset source still prevents PE execution;
                    // the time handle must be disabled rather than waiting for
                    // a thread that the reset hold intentionally does not start.
                    cores[core].Resume();
                    lock(SyncRoot)
                    {
                        appliedProcessorReset |= ProcessorBit(core);
                        if(generation == resetGeneration) completedReset[core] = resetSequence;
                    }
                    // Re-read the desired level and generation before release.
                    continue;
                }
                if(!held)
                {
                    lock(SyncRoot)
                    {
                        // Withdraw the held acknowledgement before the PE can
                        // execute. DONE remains low until release completes.
                        releasingProcessorReset |= ProcessorBit(core);
                        appliedProcessorReset &= ~ProcessorBit(core);
                    }
                }
                cores[core].SetPowerSequencerResetHold(held);
                if(!held) cores[core].Resume();
                lock(SyncRoot)
                {
                    // Record the physical result even if Reset invalidated the
                    // request while it was dispatched; the next pass reconciles
                    // that result against the new generation.
                    if(held) appliedProcessorReset |= ProcessorBit(core);
                    else appliedProcessorReset &= ~ProcessorBit(core);
                    releasingProcessorReset &= ~ProcessorBit(core);
                }
            }
        }

        private uint ProcessorBit(int core) => 1u << (processor0Bit + core);

        protected int resetGeneration;
        protected readonly CortexM[] cores;
        protected readonly NVIC[] nvics;
        protected readonly uint ProcessorMask;
        protected uint watchdogSelect;

        private readonly int processor0Bit;
        private readonly uint domainMask;
        private readonly ulong[] requestedReset = new ulong[2];
        private readonly ulong[] completedReset = new ulong[2];
        private uint forceOn;
        private uint forceOff;
        private uint appliedProcessorReset;
        private uint releasingProcessorReset;
        private bool processorUpdateQueued;

        private enum Registers : long
        {
            ForceOn = 0x0,
            ForceOff = 0x4,
            WatchdogSelect = 0x8,
            Done = 0xc,
        }
    }
}
