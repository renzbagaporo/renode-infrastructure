//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.IRQControllers;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Each physical PE has two modeled execution engines. An independent reset
    // hold keeps the deselected engine quiescent even when clocks or host halt
    // controls change. OTP selection is sampled at processor reset, not write.
    public sealed class RP2350_ArchitectureSelector : IPeripheral
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
                sequencerHeld[core] = false;
                arms[core].SetPowerSequencerResetHold(false);
                riscv[core].SetPowerSequencerResetHold(false);
                SelectArchitecture(core);
            }
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
            if(!sequencerHeld[core]) SelectArchitecture(core);
        }

        public void SetPowerSequencerResetHold(int core, bool held)
        {
            if(!held && sequencerHeld[core]) SelectArchitecture(core);
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
            arms[core].SetArchitectureResetHold(selectedRiscV[core]);
            riscv[core].SetArchitectureResetHold(!selectedRiscV[core]);
        }

        private readonly CortexM[] arms;
        private readonly RP2350_Hazard3[] riscv;
        private readonly NVIC[] nvics;
        private readonly RP2350_OTP otp;
        private readonly bool[] selectedRiscV = new bool[2];
        private readonly bool[] sequencerHeld = new bool[2];
        private bool runHeld;
        private bool rescueHeld;
    }
}
