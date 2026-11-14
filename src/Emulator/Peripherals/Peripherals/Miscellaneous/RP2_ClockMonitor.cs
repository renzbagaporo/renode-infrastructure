//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // The registers from CLK_SYS_RESUS_CTRL to INTS, which have the same layout
    // on the RP2040 and RP2350 clock blocks: clk_sys resuscitation, frequency
    // counter 0, the wake/sleep enables and the CLOCKS interrupt.
    internal sealed class RP2_ClockMonitor
    {
        public RP2_ClockMonitor(GPIO irq, Func<uint, ulong> sourceFrequency)
        {
            this.irq = irq;
            this.sourceFrequency = sourceFrequency;
            Reset();
        }

        public void Reset()
        {
            resusControl = 0xff;
            Resussed = false;
            counterReferenceKHz = counterDelay = counterInterval = counterSource = counterStatus = counterResult = 0;
            counterMinimumKHz = 0;
            counterMaximumKHz = 0x1ffffff;
            wakeEnable[0] = wakeEnable[1] = sleepEnable[0] = sleepEnable[1] = 0xffffffff;
            interruptEnable = interruptForce = 0;
            UpdateInterrupt();
        }

        // True while clk_sys is forced to clk_ref after its source stopped.
        public bool Resussed { get; private set; }

        // Called by the clock block after a clock change; returns true when it
        // has just forced clk_sys onto clk_ref.
        public bool CheckResus(ulong systemFrequency, ulong referenceFrequency)
        {
            var enabled = (resusControl & ResusEnable) != 0;
            var forced = (resusControl & ResusForce) != 0;
            if(Resussed || !enabled || referenceFrequency == 0 || (systemFrequency != 0 && !forced))
            {
                return false;
            }
            Resussed = true;
            UpdateInterrupt();
            return true;
        }

        public bool TryRead(long offset, out uint value)
        {
            switch(offset)
            {
            case 0x00: value = resusControl; return true;
            case 0x04: value = Resussed ? 1u : 0; return true;
            case 0x08: value = counterReferenceKHz; return true;
            case 0x0c: value = counterMinimumKHz; return true;
            case 0x10: value = counterMaximumKHz; return true;
            case 0x14: value = counterDelay; return true;
            case 0x18: value = counterInterval; return true;
            case 0x1c: value = counterSource; return true;
            case 0x20: value = counterStatus; return true;
            case 0x24: value = counterResult; return true;
            case 0x28: case 0x2c: value = wakeEnable[(offset - 0x28) / 4]; return true;
            case 0x30: case 0x34: value = sleepEnable[(offset - 0x30) / 4]; return true;
            // Nothing sleeps, so every clock enabled for wake is running.
            case 0x38: case 0x3c: value = wakeEnable[(offset - 0x38) / 4]; return true;
            case 0x40: value = RawInterrupts; return true;
            case 0x44: value = interruptEnable; return true;
            case 0x48: value = interruptForce; return true;
            case 0x4c: value = Interrupts; return true;
            default: value = 0; return false;
            }
        }

        // Returns true when the offset belongs to this block. clocksChanged is set
        // when clk_sys leaves its forced clk_ref selection.
        public bool TryWrite(long offset, uint value, out bool clocksChanged)
        {
            clocksChanged = false;
            switch(offset)
            {
            case 0x00:
                resusControl = value & 0x111ff;
                if((value & ResusClear) != 0 && Resussed)
                {
                    Resussed = false;
                    clocksChanged = true;
                    UpdateInterrupt();
                }
                // A forced resus applies on the next clock evaluation.
                clocksChanged |= (value & ResusForce) != 0;
                return true;
            case 0x04: return true;
            case 0x08: counterReferenceKHz = value & 0xfffff; return true;
            case 0x0c: counterMinimumKHz = value & 0x1ffffff; return true;
            case 0x10: counterMaximumKHz = value & 0x1ffffff; return true;
            case 0x14: counterDelay = value & 7; return true;
            case 0x18: counterInterval = value & 0xf; return true;
            case 0x1c:
                counterSource = value & 0xff;
                Measure();
                return true;
            case 0x20: case 0x24: return true;
            case 0x28: case 0x2c: wakeEnable[(offset - 0x28) / 4] = value; return true;
            case 0x30: case 0x34: sleepEnable[(offset - 0x30) / 4] = value; return true;
            case 0x38: case 0x3c: case 0x40: case 0x4c: return true;
            case 0x44: interruptEnable = value & 1; UpdateInterrupt(); return true;
            case 0x48: interruptForce = value & 1; UpdateInterrupt(); return true;
            default: return false;
            }
        }

        // Writing SRC starts a measurement; it completes at once with the exact
        // frequency, in kHz with a 5-bit fraction, as RESULT reports it.
        private void Measure()
        {
            if(counterSource == 0)
            {
                counterResult = 0;
                counterStatus = Done;
                return;
            }
            var hz = sourceFrequency(counterSource);
            var kHz = hz / 1000;
            var fraction = (hz % 1000) * 32 / 1000;
            counterResult = (uint)(Math.Min(kHz, 0x1ffffffUL) << 5 | fraction);
            if(hz == 0) counterStatus = Done | Fail | Died;
            else if(kHz < counterMinimumKHz) counterStatus = Done | Fail | Slow;
            else if(kHz > counterMaximumKHz) counterStatus = Done | Fail | Fast;
            else counterStatus = Done | Pass;
        }

        private uint RawInterrupts => Resussed ? 1u : 0;

        private uint Interrupts => (RawInterrupts & interruptEnable) | interruptForce;

        private void UpdateInterrupt() => irq.Set(Interrupts != 0);

        private uint resusControl;
        private uint counterReferenceKHz, counterMinimumKHz, counterMaximumKHz, counterDelay, counterInterval;
        private uint counterSource, counterStatus, counterResult;
        private uint interruptEnable, interruptForce;

        private readonly uint[] wakeEnable = new uint[2];
        private readonly uint[] sleepEnable = new uint[2];
        private readonly GPIO irq;
        private readonly Func<uint, ulong> sourceFrequency;

        private const uint ResusEnable = 1u << 8;
        private const uint ResusForce = 1u << 12;
        private const uint ResusClear = 1u << 16;
        private const uint Pass = 1u << 0;
        private const uint Done = 1u << 4;
        private const uint Fail = 1u << 16;
        private const uint Slow = 1u << 20;
        private const uint Fast = 1u << 24;
        private const uint Died = 1u << 28;
    }
}
