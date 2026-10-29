//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public interface IRP2GPIO : IGPIOReceiver
    {
        ulong Output { get; }

        ulong OutputEnable { get; }

        event Action GPIOChanged;
    }
}
