//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // A bus context for a non-CPU bus master (an RP2 DMA's busContext): bus
    // registrations made for it (`cpu: <this>`) are what that master sees,
    // so two chips in one machine each give their DMA its own map.
    public sealed class RP2_BusMaster : IPeripheral
    {
        public void Reset()
        {
        }
    }
}
