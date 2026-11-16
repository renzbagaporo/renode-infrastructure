//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // RP2040 VREG_AND_CHIP_RESET (datasheet 2.10.7, 2.12.8): the regulator and
    // brown-out controls, and CHIP_RESET, which records what caused the last
    // chip-level reset. Only a power-on reset (a machine reset) resets this
    // block: it keeps its state across RUN, the rescue DP's restart,
    // SYSRESETREQ and the watchdog, and the PSM and the debug port record
    // their resets here. The rescue restart sets PSM_RESTART_FLAG, which the
    // boot ROM's check_rescue acknowledges before it halts the core.
    // The regulator reports regulation (ROK) whenever it is enabled; voltage
    // and brown-out thresholds have no effect.
    public sealed class RP2040_VregAndChipReset : RP2APBPeripheral
    {
        public RP2040_VregAndChipReset(IMachine machine) : base(machine)
        {
            Reset();
        }

        public override void Reset()
        {
            lock(SyncRoot)
            {
                vreg = 0xb1;
                bod = 0x91;
                chipReset = HadPowerOnReset;
            }
        }

        // A RUN pin reset.
        public void RecordRunReset()
        {
            lock(SyncRoot) chipReset = (chipReset & RestartFlag) | HadRun;
        }

        // The rescue DP's psm_restart.
        public void RecordPsmRestart()
        {
            lock(SyncRoot) chipReset = HadPsmRestart | RestartFlag;
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0x0: return vreg | ((vreg & 1) != 0 ? RegulationOk : 0);
            case 0x4: return bod;
            case 0x8: return chipReset;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0x0:
                vreg = ApplyOperation(vreg, value, operation, 0xf3);
                break;
            case 0x4:
                bod = ApplyOperation(bod, value, operation, 0xf1);
                break;
            case 0x8:
                // PSM_RESTART_FLAG is write-1-to-clear; the other fields are read-only.
                if((value & RestartFlag) != 0 && operation != AtomicOperation.Clear) chipReset &= ~RestartFlag;
                break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
        }

        private uint vreg;
        private uint bod;
        private uint chipReset;

        private const uint RegulationOk = 1u << 12;
        private const uint HadPowerOnReset = 1u << 8;
        private const uint HadRun = 1u << 16;
        private const uint HadPsmRestart = 1u << 20;
        private const uint RestartFlag = 1u << 24;
    }
}
