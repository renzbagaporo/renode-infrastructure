//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Each physical PE has two modeled execution engines. An independent reset
    // hold keeps the deselected engine quiescent even when clocks or host halt
    // controls change. OTP selection is sampled at processor reset, not write.
    public sealed class RP2350_ArchitectureSelector : IPeripheral, ICPUResetController
    {
        public RP2350_ArchitectureSelector(IMachine machine, CortexM arm0, CortexM arm1,
            RP2350_Hazard3 riscv0, RP2350_Hazard3 riscv1, NVIC nvic0, NVIC nvic1,
            RP2350_OTP otp, RP2350_PSM psm = null)
        {
            arms = new[] { arm0, arm1 };
            riscv = new[] { riscv0, riscv1 };
            nvics = new[] { nvic0, nvic1 };
            this.otp = otp;
            if(psm != null) psm.ArchitectureSelector = this;
            Reset();
        }

        public void Reset()
        {
            otp.ResetArchitectureSelection();
            for(var core = 0; core < 2; core++)
            {
                if(resetInProgress && !boundaryHeld[core]) continue;
                sequencerHeld[core] = false;
                arms[core].SetPowerSequencerResetHold(false);
                riscv[core].SetPowerSequencerResetHold(false);
                if(!boundaryHeld[core]) SelectArchitecture(core);
            }
        }

        public bool Contains(ICPU cpu) => FindCore(cpu) >= 0;

        public bool SameProcessor(ICPU first, ICPU second) => FindCore(first) >= 0 && FindCore(first) == FindCore(second);

        public bool IsActive(ICPU cpu)
        {
            var core = FindCore(cpu);
            return core >= 0 && ReferenceEquals(cpu, ActiveProcessor(core));
        }

        public ICPU GetResetTarget(ICPU previousCPU)
        {
            var core = FindCore(previousCPU);
            if(core < 0) throw new ArgumentException("CPU is not part of this physical processor complex", nameof(previousCPU));
            return otp.RequestedRiscV(core) ? (ICPU)riscv[core] : arms[core];
        }

        public void HoldInReset(IEnumerable<ICPU> affectedCPUs)
        {
            resetInProgress = true;
            foreach(var cpu in affectedCPUs)
            {
                var core = FindCore(cpu);
                if(core < 0) continue;
                boundaryHeld[core] = true;
                UpdateEngineHolds(core);
            }
        }

        public void ReleaseReset()
        {
            resetInProgress = false;
            for(var core = 0; core < 2; core++)
            {
                if(!boundaryHeld[core]) continue;
                boundaryHeld[core] = false;
                if(!sequencerHeld[core]) SelectArchitecture(core);
                else UpdateEngineHolds(core);
            }
        }

        private int FindCore(ICPU cpu)
        {
            for(var core = 0; core < 2; core++)
                if(ReferenceEquals(cpu, arms[core]) || ReferenceEquals(cpu, riscv[core])) return core;
            return -1;
        }

        public BaseCPU ActiveProcessor(int core) => selectedRiscV[core] ? (BaseCPU)riscv[core] : arms[core];

        public void ResetProcessor(int core)
        {
            // Caller has reached a synchronized reset boundary. Both engines
            // remain held until the new architecture and reset state agree.
            arms[core].SetArchitectureResetHold(true);
            riscv[core].SetArchitectureResetHold(true);
            nvics[core].Reset();
            arms[core].Reset();
            riscv[core].Reset();
            // Reset requests host pauses even for a deselected engine. Normalize
            // both time handles while the architecture holds still prevent any
            // instruction from executing.
            arms[core].Resume();
            riscv[core].Resume();
            if(!sequencerHeld[core] && !boundaryHeld[core]) SelectArchitecture(core);
        }

        public void SetPowerSequencerResetHold(int core, bool held)
        {
            if(!held && sequencerHeld[core] && !boundaryHeld[core]) SelectArchitecture(core);
            sequencerHeld[core] = held;
            arms[core].SetPowerSequencerResetHold(held);
            riscv[core].SetPowerSequencerResetHold(held);
        }

        public void SetRunResetHold(bool held)
        {
            runHeld = held;
            ApplyExternalHold();
        }

        public void SetRescueResetHold(bool held)
        {
            rescueHeld = held;
            ApplyExternalHold();
        }

        private void ApplyExternalHold()
        {
            for(var core = 0; core < 2; core++)
            {
                arms[core].SetExternalResetHold(runHeld || rescueHeld);
                riscv[core].SetExternalResetHold(runHeld || rescueHeld);
            }
        }

        private void SelectArchitecture(int core)
        {
            selectedRiscV[core] = otp.LatchArchitecture(core);
            UpdateEngineHolds(core);
        }

        private void UpdateEngineHolds(int core)
        {
            arms[core].SetArchitectureResetHold(boundaryHeld[core] || selectedRiscV[core]);
            riscv[core].SetArchitectureResetHold(boundaryHeld[core] || !selectedRiscV[core]);
        }

        private readonly CortexM[] arms;
        private readonly RP2350_Hazard3[] riscv;
        private readonly NVIC[] nvics;
        private readonly RP2350_OTP otp;
        private readonly bool[] boundaryHeld = new bool[2];
        private readonly bool[] selectedRiscV = new bool[2];
        private readonly bool[] sequencerHeld = new bool[2];
        private bool resetInProgress;
        private bool runHeld;
        private bool rescueHeld;
    }
}
