//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        public bool ExternalDebugEnabled => nvic.ExternalDebugEnabled;

        public bool EnableMemoryMappedDebug
        {
            get => nvic.EnableMemoryMappedDebug;
            set => nvic.EnableMemoryMappedDebug = value;
        }
    }
}
