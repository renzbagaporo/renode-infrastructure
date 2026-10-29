//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

namespace Antmicro.Renode.Peripherals
{
    public interface IRP2ClockSource
    {
        ulong SystemFrequency { get; }
        ulong PeripheralFrequency { get; }
        ulong USBFrequency { get; }
        event Action ClockChanged;
    }
}
