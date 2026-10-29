// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        internal void SetExternalResetHold(bool asserted)
        {
            if(externalResetHeld == asserted) return;
            externalResetHeld = asserted;
            // The caller uses a safe reset/synchronization boundary. A physical
            // RUN input never borrows the PSM wait signal or debugger halt bit.
            UpdateRequestedHaltedState();
        }

        internal void SetPowerSequencerResetHold(bool asserted)
        {
            if(powerSequencerResetHeld == asserted) return;
            powerSequencerResetHeld = asserted;
            // PSM transitions are dispatched at a synchronization boundary. Keep
            // this source independent of external RUN, CpuWaitSignal and debug.
            UpdateRequestedHaltedState();
        }

        protected override bool IsHeldInReset => externalResetHeld || powerSequencerResetHeld;

        private volatile bool powerSequencerResetHeld;

        private volatile bool externalResetHeld;
    }
}
