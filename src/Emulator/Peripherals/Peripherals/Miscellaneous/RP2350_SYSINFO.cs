//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_SYSINFO : RP2APBPeripheral
    {
        public RP2350_SYSINFO(IMachine machine, uint revision = 2, bool packageQfn60 = false, uint gitReference = 0) : base(machine)
        {
            chipId = ((revision & 0xf) << 28) | 0x4927;
            package = packageQfn60 ? 1u : 0;
            this.gitReference = gitReference;
        }
        public override void Reset() { }
        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0: return chipId;
            case 4: return package;
            case 8: return 2;
            case 0x14: return gitReference;
            default: this.LogUnhandledRead(offset); return 0;
            }
        }
        protected override void WriteRegister(long offset, uint value, AtomicOperation operation) => this.LogUnhandledWrite(offset, value);
        private readonly uint chipId;
        private readonly uint package;
        private readonly uint gitReference;
    }
}
