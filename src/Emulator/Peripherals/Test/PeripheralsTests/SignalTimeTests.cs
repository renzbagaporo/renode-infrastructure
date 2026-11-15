//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class SignalTimeTests
    {
        [Test]
        public void ShouldKeepEachMachinesTimeAcrossNestedDeliveries()
        {
            // A synchronous wire's answer from machine B arrives inside A's
            // delivery, and A's time still holds there.
            using(var a = new Machine())
            using(var b = new Machine())
            {
                var atA = TimeInterval.FromMicroseconds(10);
                var atB = TimeInterval.FromMicroseconds(20);
                Assert.AreEqual(a.ClockSource.CurrentValue, SignalTime.Of(a));
                using(SignalTime.At(a, atA))
                {
                    using(SignalTime.At(b, atB))
                    {
                        Assert.AreEqual(atA, SignalTime.Of(a));
                        Assert.AreEqual(atB, SignalTime.Of(b));
                        using(SignalTime.At(a, atB)) Assert.AreEqual(atB, SignalTime.Of(a));
                        Assert.AreEqual(atA, SignalTime.Of(a));
                    }
                    Assert.AreEqual(b.ClockSource.CurrentValue, SignalTime.Of(b));
                    Assert.AreEqual(atA, SignalTime.Of(a));
                }
                Assert.AreEqual(a.ClockSource.CurrentValue, SignalTime.Of(a));
            }
        }
    }
}
