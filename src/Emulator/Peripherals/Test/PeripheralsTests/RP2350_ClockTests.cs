//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2350_ClockTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            xosc = new RP2_XOSC(machine, rp2350: true);
            rosc = new RP2350_ROSC(machine);
            pllSys = new RP2_PLL(machine, xosc, rp2350: true);
            pllUsb = new RP2_PLL(machine, xosc, rp2350: true);
            clocks = new RP2350_Clocks(machine, xosc, rosc, pllSys, pllUsb);
        }

        [TearDown]
        public void TearDown()
        {
            machine?.Dispose();
        }

        [Test]
        public void ShouldNotifyTickConsumersWhenClocksReset()
        {
            var ticks = new RP2350_Ticks(machine, clocks);
            ticks.WriteDoubleWord(0x4, 12);
            ticks.WriteDoubleWord(0x0, 1);
            StartSystemPLL();
            clocks.WriteDoubleWord(0x30, 2);
            clocks.WriteDoubleWord(0x3c, 1);
            Assert.AreEqual(1000000ul, ticks.GetFrequency(0));
            ulong notifiedFrequency = 0;
            clocks.ClockChanged += () => notifiedFrequency = clocks.GetFrequency(5);

            clocks.Reset();

            Assert.AreEqual(rosc.Frequency / 12, ticks.GetFrequency(0));
            Assert.AreEqual(rosc.Frequency, notifiedFrequency);
        }

        [Test]
        public void ShouldUseVariantSpecificResetLineMask()
        {
            var rp2040Resets = new RP2_Resets(machine);
            var rp2350Resets = new RP2_Resets(machine, rp2350: true);
            Assert.AreEqual(0x01ffffffu, rp2040Resets.ReadDoubleWord(0));
            Assert.AreEqual(0x1fffffffu, rp2350Resets.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldUseVariantSpecificCrystalCounterOffsetAndWidth()
        {
            var rp2040Xosc = new RP2_XOSC(machine);
            rp2040Xosc.WriteDoubleWord(0, 0xfabaa0);
            rp2040Xosc.WriteDoubleWord(0x1c, 0x101);
            Assert.AreEqual(1u, rp2040Xosc.ReadDoubleWord(0x1c));
            xosc.WriteDoubleWord(0, 0xfabaa0);
            xosc.WriteDoubleWord(0x10, 0x101);
            Assert.AreEqual(0x101u, xosc.ReadDoubleWord(0x10));
        }

        [Test]
        public void ShouldApplyVariantSpecificPLLMinimumVCOFrequency()
        {
            var rp2040PLL = new RP2_PLL(machine, xosc);
            xosc.WriteDoubleWord(0, 0xfabaa0);
            foreach(var pll in new[] { rp2040PLL, pllSys })
            {
                pll.WriteDoubleWord(8, 40);
                pll.WriteDoubleWord(0xc, 0x52000);
                pll.WriteDoubleWord(0x3004, 0x29);
            }
            Assert.AreEqual(48000000ul, rp2040PLL.Frequency);
            Assert.AreEqual(0ul, pllSys.Frequency);
        }

        [Test]
        public void ShouldWaitForProgrammedCrystalStartupDelay()
        {
            xosc.WriteDoubleWord(0xc, 1);
            xosc.WriteDoubleWord(0x0, 0xfabaa0);
            Assert.AreEqual(0ul, xosc.Frequency);
            AdvanceMicroseconds(21);
            Assert.AreEqual(0ul, xosc.Frequency);
            AdvanceMicroseconds(1);
            Assert.AreEqual(12000000ul, xosc.Frequency);
            xosc.WriteDoubleWord(0x0, 0xd1eaa0);
            Assert.AreEqual(0ul, xosc.Frequency);
        }

        [Test]
        public void ShouldCancelCrystalStartupWhenDisabled()
        {
            xosc.WriteDoubleWord(0xc, 1);
            xosc.WriteDoubleWord(0x0, 0xfabaa0);
            xosc.WriteDoubleWord(0x0, 0xd1eaa0);
            AdvanceMicroseconds(100);
            Assert.AreEqual(0ul, xosc.Frequency);
            Assert.AreEqual(0u, xosc.ReadDoubleWord(0x4) & 0x80001000);
        }

        [Test]
        public void ShouldRequireCrystalAndValidPoweredPLL()
        {
            pllSys.WriteDoubleWord(0x8, 125);
            pllSys.WriteDoubleWord(0x3004, 0x21);
            pllSys.WriteDoubleWord(0xc, 0x52000);
            pllSys.WriteDoubleWord(0x3004, 0x8);
            Assert.AreEqual(0ul, pllSys.Frequency);
            xosc.WriteDoubleWord(0x0, 0xfabaa0);
            Assert.AreEqual(150000000ul, pllSys.Frequency);
            Assert.AreEqual(0x80000000u, pllSys.ReadDoubleWord(0x0) & 0xc0000000);
            pllSys.WriteDoubleWord(0x2004, 0x20);
            Assert.AreEqual(0ul, pllSys.Frequency);
            Assert.AreEqual(0x40000000u, pllSys.ReadDoubleWord(0x0) & 0xc0000000);
        }

        [Test]
        public void ShouldRejectInvalidPLLFeedbackDivider()
        {
            StartSystemPLL();
            pllSys.WriteDoubleWord(0x8, 1);
            Assert.AreEqual(0ul, pllSys.Frequency);
            Assert.AreEqual(0u, pllSys.ReadDoubleWord(0x0) & 0x80000000);
        }

        [Test]
        public void ShouldSelectAndDivideRunningClockSources()
        {
            StartSystemPLL();
            clocks.WriteDoubleWord(0x30, 2);
            clocks.WriteDoubleWord(0x3c, 1);
            Assert.AreEqual(12000000ul, clocks.GetFrequency(4));
            Assert.AreEqual(150000000ul, clocks.GetFrequency(5));
            Assert.AreEqual(4u, clocks.ReadDoubleWord(0x38));
            Assert.AreEqual(2u, clocks.ReadDoubleWord(0x44));
            clocks.WriteDoubleWord(0x40, 0x20000);
            Assert.AreEqual(75000000ul, clocks.GetFrequency(5));
        }

        [Test]
        public void ShouldRetainReferenceSourceUntilRequestedClockIsRunning()
        {
            xosc.WriteDoubleWord(0x0, 0xfabaa0);
            clocks.WriteDoubleWord(0x30, 2);
            clocks.WriteDoubleWord(0x30, 1);
            Assert.AreEqual(4u, clocks.ReadDoubleWord(0x38));
            Assert.AreEqual(12000000ul, clocks.GetFrequency(4));
        }

        [Test]
        public void ShouldRequirePasswordForRingOscillatorDriveStrength()
        {
            var previous = rosc.ReadDoubleWord(0x4);
            rosc.WriteDoubleWord(0x4, 0x1234);
            Assert.AreEqual(previous, rosc.ReadDoubleWord(0x4));
            Assert.AreNotEqual(0u, rosc.ReadDoubleWord(0x1c) & (1u << 24));
            rosc.WriteDoubleWord(0x1c, 1u << 24);
            rosc.WriteDoubleWord(0x4, 0x96961234);
            Assert.AreEqual(0x1234u, rosc.ReadDoubleWord(0x4));
            Assert.AreEqual(0u, rosc.ReadDoubleWord(0x1c) & (1u << 24));
        }

        [Test]
        public void ShouldGenerateTicksOnlyWithEnabledNonzeroDivider()
        {
            xosc.WriteDoubleWord(0x0, 0xfabaa0);
            clocks.WriteDoubleWord(0x30, 2);
            var ticks = new RP2350_Ticks(machine, clocks);
            ticks.WriteDoubleWord(0x4, 12);
            Assert.AreEqual(0ul, ticks.GetFrequency(0));
            ticks.WriteDoubleWord(0x0, 1);
            Assert.AreEqual(1000000ul, ticks.GetFrequency(0));
            Assert.AreEqual(3u, ticks.ReadDoubleWord(0x0));
            ticks.WriteDoubleWord(0x3000, 1);
            Assert.AreEqual(0ul, ticks.GetFrequency(0));
        }

        [Test]
        public void ShouldRejectPeripheralWithoutPersistentResetHold()
        {
            var resets = new RP2_Resets(machine);
            machine.SystemBus.Register(resets, new BusRangeRegistration(0x4000c000, 0x4000));
            var registration = new NumberRegistrationPoint<int>(12);
            var unsupported = new MappedMemory(machine, 0x1000);
            Assert.Throws<RegistrationException>(() => resets.Register(unsupported, registration));
            Assert.That(resets.Children, Is.Empty);
            resets.Register(pllSys, registration);
            Assert.IsTrue(pllSys.ResetHeld);
        }

        [Test]
        public void ShouldResetAndHoldRegisteredPeripheral()
        {
            var resets = new RP2_Resets(machine, rp2350: true);
            machine.SystemBus.Register(resets, new BusRangeRegistration(0x40020000, 0x4000));
            resets.Register(pllSys, new NumberRegistrationPoint<int>(14));
            Assert.IsTrue(pllSys.ResetHeld);
            resets.WriteDoubleWord(0x3000, 1u << 14);
            StartSystemPLL();
            Assert.AreEqual(150000000ul, pllSys.Frequency);
            resets.WriteDoubleWord(0x2000, 1u << 14);
            Assert.AreEqual(0ul, pllSys.Frequency);
            Assert.IsTrue(pllSys.ResetHeld);
            pllSys.WriteDoubleWord(0x8, 125);
            Assert.AreEqual(0u, pllSys.ReadDoubleWord(0x8));
        }

        private void StartSystemPLL()
        {
            xosc.WriteDoubleWord(0x0, 0xfabaa0);
            pllSys.WriteDoubleWord(0x8, 125);
            pllSys.WriteDoubleWord(0x3004, 0x21);
            pllSys.WriteDoubleWord(0xc, 0x52000);
            pllSys.WriteDoubleWord(0x3004, 0x8);
        }

        private void AdvanceMicroseconds(ulong duration)
        {
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(duration));
        }

        private Machine machine;
        private RP2_XOSC xosc;
        private RP2350_ROSC rosc;
        private RP2_PLL pllSys;
        private RP2_PLL pllUsb;
        private RP2350_Clocks clocks;
    }
}
