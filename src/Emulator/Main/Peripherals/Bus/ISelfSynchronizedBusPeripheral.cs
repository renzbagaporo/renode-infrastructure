//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

namespace Antmicro.Renode.Peripherals.Bus
{
    // A peripheral that serializes its own register accesses. SystemBus does
    // not hold a per-peripheral lock around them, so an access may synchronize
    // CPU time or take the clock source lock without holding a bus lock that a
    // bus master's clock callback (a DMA transfer) could be waiting for.
    public interface ISelfSynchronizedBusPeripheral : IBusPeripheral
    {
    }
}
