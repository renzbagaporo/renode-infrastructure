// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System.Collections.Generic;

namespace Antmicro.Renode.Peripherals.CPU
{
    // A physical processor can select between alternative execution engines.
    // Reset owners preserve physical pauses while every affected engine is held.
    public interface ICPUResetController
    {
        bool Contains(ICPU cpu);
        bool IsActive(ICPU cpu);
        bool SameProcessor(ICPU first, ICPU second);
        ICPU GetResetTarget(ICPU previousCPU);
        void HoldInReset(IEnumerable<ICPU> affectedCPUs);
        void ReleaseReset();
    }
}
