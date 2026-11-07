//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.DMA;

using NUnit.Framework;

namespace Antmicro.Renode.UnitTests
{
    [TestFixture]
    public class RP2040_DMATests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            EmulationManager.Instance.CurrentEmulation.AddMachine(machine);
            dma = new RP2040_DMA(machine);
        }

        [TearDown]
        public void TearDown()
        {
            EmulationManager.Instance.Clear();
        }

        // A PIO timer callback raises DREQ while its CPU thread holds the clock
        // lock; the other core reads a DMA register at the same moment. The
        // read must wait for the clock lock before taking the register monitor.
        [Test]
        public void ShouldAcceptRequestsFromClockCallbacksDuringRegisterAccess()
        {
            Task<uint> read = null;
            var callback = Task.Run(() => machine.ClockSource.ExecuteInLock(() =>
            {
                read = Task.Run(() => dma.ReadDoubleWord(ChannelCountOffset));
                Thread.Sleep(200);
                dma.OnGPIO(0, true);
            }));
            Assert.IsTrue(callback.Wait(TimeSpan.FromSeconds(5)), "the request from the clock callback did not complete");
            Assert.IsTrue(read.Wait(TimeSpan.FromSeconds(5)), "the register read did not complete");
            Assert.AreEqual(ChannelCount, read.Result);
        }

        private Machine machine;
        private RP2040_DMA dma;

        private const long ChannelCountOffset = 0x448;
        private const uint ChannelCount = 12;
    }
}
