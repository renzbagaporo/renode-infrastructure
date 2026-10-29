//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

namespace Antmicro.Renode.Peripherals
{
    // A reset line remains asserted until software releases it. Implementations
    // must inhibit register writes, protocol callbacks and scheduled device work
    // while held, and discard work invalidated by assertion of the reset line.
    public interface IRP2ResettablePeripheral : IPeripheral
    {
        bool ResetHeld { get; set; }
    }
}
