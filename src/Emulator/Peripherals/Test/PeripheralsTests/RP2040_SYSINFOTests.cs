//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_SYSINFOTests
    {
        [Test]
        public void ShouldReportB2IdentityAndASICPlatform()
        {
            using(var machine = new Machine())
            {
                var info = new RP2040_SYSINFO(machine);
                Assert.AreEqual(0x20002927u, info.ReadDoubleWord(0));
                Assert.AreEqual(2u, info.ReadDoubleWord(4));
                info.WriteDoubleWord(0x2000, 0xffffffff);
                Assert.AreEqual(0x20002927u, info.ReadDoubleWord(0));
            }
        }
    }
}
