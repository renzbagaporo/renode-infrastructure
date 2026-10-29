//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.CPU
{
    // Cortex-M33 class 1 ROM table at 0xe00ff000. It advertises the processor
    // debug component implemented by NVIC's memory-mapped debug registers.
    public class CortexM_ROMTable : IDoubleWordPeripheral, IKnownSize
    {
        public uint ReadDoubleWord(long offset)
        {
            switch(offset)
            {
            case 0: return 0xfff0f003; // e000e000 - e00ff000, present, 32-bit entry
            case 0xfcc: return 1;
            case 0xfd0: return 4;
            case 0xfe0: return 0xc9;
            case 0xfe4: return 0xb4;
            case 0xfe8: return 0xb;
            case 0xff0: return 0xd;
            case 0xff4: return 0x10;
            case 0xff8: return 5;
            case 0xffc: return 0xb1;
            default: return 0;
            }
        }

        public void WriteDoubleWord(long offset, uint value) { }
        public void Reset() { }
        public long Size => 0x1000;
    }
}
