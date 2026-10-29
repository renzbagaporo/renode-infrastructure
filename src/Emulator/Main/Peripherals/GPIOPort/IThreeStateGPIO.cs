//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    // Output drive is separate from the resolved input level on a shared wire.
    public interface IThreeStateGPIO : IGPIOReceiver
    {
        void GetOutputState(int number, out bool level, out bool enabled);

        event Action<int, bool, bool> DriveChanged;
    }
}
