//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Miscellaneous;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2ClockSourceTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void ShouldExposeVariantSpecificUsbClockAndSemanticSystemAndPeripheralClocks(bool rp2350)
        {
            using(var machine = new Machine())
            {
                var xosc = new RP2_XOSC(machine, rp2350: rp2350);
                var pllSys = new RP2_PLL(machine, xosc, rp2350: rp2350);
                var pllUsb = new RP2_PLL(machine, xosc, rp2350: rp2350);
                RP2APBPeripheral registers;
                IRP2ClockSource source;
                if(rp2350)
                {
                    var clocks = new RP2350_Clocks(machine, xosc, new RP2350_ROSC(machine), pllSys, pllUsb);
                    registers = clocks;
                    source = clocks;
                }
                else
                {
                    var clocks = new RP2040_Clocks(machine, xosc, new RP2040_ROSC(machine), pllSys, pllUsb);
                    registers = clocks;
                    source = clocks;
                }
                var notifications = 0;
                source.ClockChanged += () => notifications++;
                Assert.That(source.USBFrequency, Is.Zero);
                xosc.WriteDoubleWord(0, 0xfabaa0);
                pllUsb.WriteDoubleWord(8, 120);
                pllUsb.WriteDoubleWord(0xc, 0x65000);
                pllUsb.WriteDoubleWord(0x3004, 0x29);
                Assert.That(pllUsb.Frequency, Is.EqualTo(48000000ul));
                // USB is generator 7 on RP2040 and generator 8 on RP2350.
                registers.WriteDoubleWord((rp2350 ? 8 : 7) * 12, 0x800);
                Assert.That(source.USBFrequency, Is.EqualTo(48000000ul));
                // Select XOSC for clk_ref, clk_sys follows clk_ref; clk_peri follows clk_sys.
                registers.WriteDoubleWord(0x30, 2);
                registers.WriteDoubleWord(0x3c, 0);
                registers.WriteDoubleWord(0x48, 0x800);
                Assert.That(source.SystemFrequency, Is.EqualTo(12000000ul));
                Assert.That(source.PeripheralFrequency, Is.EqualTo(12000000ul));
                // Disable only the USB generator, leaving the system/peripheral clocks running.
                registers.WriteDoubleWord(0x3000 + (rp2350 ? 8 : 7) * 12, 0x800);
                Assert.That(source.USBFrequency, Is.Zero);
                Assert.That(source.SystemFrequency, Is.EqualTo(12000000ul));
                Assert.That(notifications, Is.GreaterThan(0));
            }
        }
    }
}
