//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    // A three-state endpoint whose pins can also pull an undriven net weakly,
    // as pad pull-up and pull-down resistors do.
    public interface IPulledThreeStateGPIO : IThreeStateGPIO
    {
        // The pin's weak pull: true up, false down, null none.
        bool? GetPull(int number);

        event Action<int, bool?> PullChanged;
    }
}
