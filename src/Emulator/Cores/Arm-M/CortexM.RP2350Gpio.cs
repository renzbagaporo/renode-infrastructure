//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Runtime.InteropServices;

using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Utilities.Binding;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        public RP2350_SIO GPIOCoprocessor { get; set; }

        [Export]
        private uint RP2350GpioAccess(uint control, ulong value, ulong mask, IntPtr result)
        {
            var sio = GPIOCoprocessor;
            if(sio == null) return 0;
            SyncTime();
            var data = sio.AccessGPIOCoprocessor(control, value, mask,
                !TrustZoneEnabled || SecureState, (int)(MultiprocessingId & 1));
            Marshal.WriteInt64(result, unchecked((long)data));
            return 1;
        }
    }
}
