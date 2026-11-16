//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SPI
{
    // An SPI controller on four pins of an RP2 GPIO bank, for a board that is
    // the target: it drives CS, SCK and MOSI as the pads' external inputs and
    // samples the level on MISO, in the same machine with no latency, which a
    // target's answer needs when the real controller would be on another
    // machine. Mode 0, one byte per chip-select window: CS falls with MOSI's
    // first bit, eight clocks follow (MOSI changes while SCK is low, MISO is
    // sampled as SCK rises), CS rises half a clock after the last fall and
    // stays high for one clock before the next byte.
    //
    // Start N sends the MOSI page N times, the first one interval after
    // Start and the rest one interval after the page before ends. Before any
    // Start it drives the lines idle (CS high, SCK and MOSI low). Declared
    // `@ sysbus` (no address), it is reachable by name from the monitor.
    public sealed class RP2_PinSPIController : IPeripheral
    {
        public RP2_PinSPIController(IMachine machine, RP2_GPIOBank0 gpio, int csPin, int sckPin, int mosiPin, int misoPin,
            ulong frequency = 1000000, ulong intervalMilliseconds = 500)
        {
            if(gpio == null) throw new ConstructionException("A GPIO bank is required");
            if(frequency == 0) throw new ConstructionException("The frequency must not be 0");
            this.gpio = gpio;
            this.csPin = csPin;
            this.sckPin = sckPin;
            this.mosiPin = mosiPin;
            this.misoPin = misoPin;
            IntervalMilliseconds = intervalMilliseconds;
            halfPeriods = 2 * frequency;
            timer = new LimitTimer(machine.ClockSource, halfPeriods, this, "spi controller", limit: 1,
                workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += OnTimer;
            mosiPage = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            Reset();
        }

        public void Reset()
        {
            lock(sync)
            {
                timer.Reset();
                remaining = 0;
                PagesSent = 0;
                step = 0;
                misoLog.Clear();
            }
            Drive(true, false, false);
        }

        // Sends the MOSI page `pages` times.
        public void Start(int pages)
        {
            if(pages <= 0) throw new RecoverableException("The page count must be positive");
            lock(sync)
            {
                remaining = pages;
                step = 0;
                byteIndex = 0;
                page = new List<byte>();
                Wait(IntervalTicks);
            }
        }

        // The bytes to send, as hex.
        public void SetMosi(string hex)
        {
            var digits = new string(hex.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if(digits.Length == 0 || digits.Length % 2 != 0) throw new RecoverableException("Expected whole bytes of hex");
            var bytes = new byte[digits.Length / 2];
            for(var i = 0; i < bytes.Length; i++)
            {
                if(!byte.TryParse(digits.Substring(2 * i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                    throw new RecoverableException($"Not hex: {digits.Substring(2 * i, 2)}");
            }
            lock(sync) mosiPage = bytes;
        }

        // What came back on MISO for each page sent since the last call, as
        // hex, one line per page.
        public string TakeMisoLog()
        {
            lock(sync)
            {
                var text = string.Join("\n", misoLog.Select(page => string.Join(" ", page.Select(b => b.ToString("x2")))));
                misoLog.Clear();
                return text;
            }
        }

        public int PagesSent { get; private set; }

        public ulong IntervalMilliseconds { get; set; }

        // Steps of one byte window, in half SCK periods: 0 lowers CS with bit
        // 7 on MOSI; odd steps 1..15 raise SCK and sample MISO; even steps
        // 2..16 lower SCK and put the next bit out; 17 raises CS; 18, one
        // clock later, starts the next byte or ends the page.
        private void OnTimer()
        {
            bool cs = true, sck = false, mosi = false;
            lock(sync)
            {
                if(remaining == 0) return;
                if(step == 18)
                {
                    if(++byteIndex == mosiPage.Length)
                    {
                        byteIndex = 0;
                        step = 0;
                        misoLog.Add(page);
                        page = new List<byte>();
                        PagesSent++;
                        if(--remaining != 0) Wait(IntervalTicks);
                        Drive(true, false, false);
                        return;
                    }
                    step = 0;
                }
                if(step == 0)
                {
                    current = mosiPage[byteIndex];
                    received = 0;
                }
                if(step <= 16)
                {
                    cs = false;
                    sck = step % 2 == 1;
                    // Bit 7 goes out at step 0, bit 7 - k/2 at the fall of step k.
                    var outBit = step == 16 ? 0 : 7 - step / 2;
                    mosi = ((current >> outBit) & 1) != 0;
                    if(sck) received = (byte)((received << 1) | ((gpio.GetPadLevel(misoPin) ?? false) ? 1 : 0));
                    step++;
                    Wait(1);
                }
                else
                {
                    // Step 17: CS rises, and stays high for one clock.
                    page.Add(received);
                    step = 18;
                    Wait(2);
                }
            }
            Drive(cs, sck, mosi);
        }

        private void Wait(ulong halves)
        {
            timer.Enabled = false;
            timer.Limit = Math.Max(1, halves);
            timer.ResetValue();
            timer.Enabled = true;
        }

        private void Drive(bool cs, bool sck, bool mosi)
        {
            gpio.OnGPIO(mosiPin, mosi);
            gpio.OnGPIO(sckPin, sck);
            gpio.OnGPIO(csPin, cs);
        }

        private ulong IntervalTicks => Math.Max(1, IntervalMilliseconds * halfPeriods / 1000);

        private int remaining, step, byteIndex;
        private byte current, received;
        private List<byte> page = new List<byte>();
        private byte[] mosiPage;

        private readonly RP2_GPIOBank0 gpio;
        private readonly int csPin, sckPin, mosiPin, misoPin;
        private readonly ulong halfPeriods;
        private readonly LimitTimer timer;
        private readonly List<List<byte>> misoLog = new List<List<byte>>();
        private readonly object sync = new object();
    }
}
