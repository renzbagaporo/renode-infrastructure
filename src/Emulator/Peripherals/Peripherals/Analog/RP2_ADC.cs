//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Numerics;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Analog
{
    // RP2040/RP2350 12-bit SAR ADC. A conversion takes 96 clk_adc cycles;
    // START_MANY repeats them back to back, or on the fractional DIV pacer,
    // whose triggers during a conversion are ignored. RROBIN steps AINSEL
    // after each conversion. The sample FIFO (4 entries on the RP2040, 8 on
    // the RP2350) raises IRQ and, with DREQ_EN, DREQ while it holds at least
    // THRESH samples. Inputs are voltages against a 3.3 V reference set with
    // SetChannelVoltage; the last channel is the temperature sensor, whose
    // Vbe is 0.706 V at 27 C with a -1.721 mV/C slope. Conversions never err.
    public sealed class RP2_ADC : RP2APBPeripheral
    {
        public RP2_ADC(IMachine machine, bool rp2350 = false, int channels = 5, ulong frequency = 48000000) : base(machine)
        {
            if(channels != 5 && !(rp2350 && channels == 9)) throw new ConstructionException("The ADC has 5 channels (9 on the RP2350 QFN-80)");
            this.rp2350 = rp2350;
            this.frequency = frequency;
            voltages = new double[channels];
            timer = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, "conversion",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += OnTimer;
            Reset();
        }

        public override void Reset()
        {
            timer.Reset();
            control = 0;
            result = 0;
            fifoControl = 0;
            divider = 0;
            interruptEnable = interruptForce = 0;
            fifo.Clear();
            converting = false;
            anchorTime = machine.ClockSource.CurrentValue.Ticks;
            UpdateSignals();
        }

        public void SetChannelVoltage(int channel, double volts)
        {
            if(channel < 0 || channel >= voltages.Length - 1) throw new RecoverableException($"Channel {channel} is not an external input");
            lock(SyncRoot) voltages[channel] = volts;
        }

        public double TemperatureCelsius { get; set; } = 27;

        public GPIO IRQ { get; } = new GPIO();
        public GPIO DREQ { get; } = new GPIO();

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        protected override void OnResetHeldChanged()
        {
            if(ResetHeld) timer.Enabled = false;
            UpdateSignals();
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0x00: return control | (converting ? 0 : Ready);
            case 0x04: return result;
            case 0x08:
                return fifoControl | (fifo.Count == 0 ? FifoEmpty : 0) | (fifo.Count == Depth ? FifoFull : 0)
                    | (uint)fifo.Count << 16;
            case 0x0c:
                uint value = 0;
                if(fifo.Count != 0) value = fifo.Dequeue();
                else fifoControl |= FifoUnder;
                UpdateSignals();
                return value;
            case 0x10: return divider;
            case 0x14: return RawInterrupt;
            case 0x18: return interruptEnable;
            case 0x1c: return interruptForce;
            case 0x20: return (RawInterrupt & interruptEnable) | interruptForce;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0x00:
                var wasMany = (control & StartMany) != 0;
                // ERR_STICKY never sets: conversions do not fail.
                control = ApplyOperation(control, value & ~StartOnce, operation, rp2350 ? 0x01fff00bu : 0x001f700bu);
                var startOnce = operation != AtomicOperation.Clear && (value & StartOnce) != 0;
                if((control & Enable) == 0) control &= ~StartMany;
                var many = (control & StartMany) != 0;
                if(!converting && (control & Enable) != 0 && (startOnce || (many && !wasMany))) StartConversion(CurrentUnit);
                break;
            case 0x08:
                var clear = operation == AtomicOperation.Clear ? 0 : value & (FifoOver | FifoUnder);
                fifoControl = (ApplyOperation(fifoControl, value, operation, 0x0f00000f) & 0x0f00000f)
                    | (fifoControl & (FifoOver | FifoUnder) & ~clear);
                break;
            case 0x10: divider = ApplyOperation(divider, value, operation, 0x00ffffff); break;
            case 0x18: interruptEnable = ApplyOperation(interruptEnable, value, operation, 1); break;
            case 0x1c: interruptForce = ApplyOperation(interruptForce, value, operation, 1); break;
            default:
                this.LogUnhandledWrite(offset, value);
                break;
            }
            UpdateSignals();
        }

        private void StartConversion(BigInteger start)
        {
            converting = true;
            conversionStart = start;
            Schedule(start + ConversionCycles * 256);
        }

        // The timer fires at the end of a conversion, or at the next pacer
        // trigger of a free-running sequence.
        private void OnTimer()
        {
            lock(SyncRoot)
            {
                if(ResetHeld) return;
                if(!converting)
                {
                    if((control & (Enable | StartMany)) == (Enable | StartMany)) StartConversion(dueUnit);
                    return;
                }
                converting = false;
                Complete();
                if((control & (Enable | StartMany)) == (Enable | StartMany))
                {
                    var end = dueUnit;
                    var period = Period;
                    if(period == 0) StartConversion(end);
                    else
                    {
                        // The first pacer trigger after this conversion ends.
                        var triggers = (end - conversionStart + period - 1) / period;
                        var next = conversionStart + triggers * period;
                        if(next == end) StartConversion(end);
                        else Schedule(next);
                    }
                }
                UpdateSignals();
            }
        }

        private void Complete()
        {
            var channel = (int)((control >> 12) & (rp2350 ? 0xfu : 7u));
            result = Sample(channel);
            if((fifoControl & FifoEnable) != 0)
            {
                if(fifo.Count >= Depth) fifoControl |= FifoOver;
                else fifo.Enqueue((ushort)((fifoControl & FifoShift) != 0 ? result >> 4 : result));
            }
            var roundRobin = (control >> 16) & ((1u << voltages.Length) - 1);
            if(roundRobin == 0) return;
            for(var step = 1; step <= voltages.Length; step++)
            {
                var next = (channel + step) % voltages.Length;
                if((roundRobin & (1u << next)) == 0) continue;
                control = (control & ~(0xfu << 12)) | (uint)next << 12;
                return;
            }
        }

        private ushort Sample(int channel)
        {
            if(channel >= voltages.Length) return 0;
            var volts = channel == voltages.Length - 1
                ? ((control & TemperatureEnable) != 0 ? 0.706 - 0.001721 * (TemperatureCelsius - 27) : 0)
                : voltages[channel];
            return (ushort)Math.Max(0, Math.Min(4095, Math.Round(volts / Reference * 4096)));
        }

        private void Schedule(BigInteger unit)
        {
            dueUnit = unit;
            var now = machine.ClockSource.CurrentValue.Ticks;
            var start = UnitStart(unit);
            timer.Restart(start > now ? start - now : 1);
        }

        private void UpdateSignals()
        {
            var level = (uint)fifo.Count >= ((fifoControl >> 24) & 0xf);
            IRQ.Set(!ResetHeld && ((level ? interruptEnable : 0) | interruptForce) != 0);
            DREQ.Set(!ResetHeld && (fifoControl & FifoDreqEnable) != 0 && level);
        }

        // Time is counted in 1/256 clk_adc cycles from the last reset, so the
        // fractional pacer and 48 MHz cycles stay exact against virtual time.
        private BigInteger CurrentUnit => (BigInteger)(machine.ClockSource.CurrentValue.Ticks - anchorTime) * frequency * 256 / TimeInterval.TicksPerSecond;

        private ulong UnitStart(BigInteger unit) => anchorTime + (ulong)((unit * TimeInterval.TicksPerSecond + frequency * 256 - 1) / (frequency * 256));

        private BigInteger Period => ((divider >> 8) & 0xffff) == 0 && (divider & 0xff) == 0 ? 0
            : (((divider >> 8) & 0xffff) + 1) * 256 + (divider & 0xff);

        private uint RawInterrupt => (uint)fifo.Count >= ((fifoControl >> 24) & 0xf) ? 1u : 0;

        private int Depth => rp2350 ? 8 : 4;

        private readonly bool rp2350;
        private readonly ulong frequency;
        private readonly double[] voltages;
        private readonly LimitTimer timer;
        private readonly Queue<ushort> fifo = new Queue<ushort>();
        private uint control, result, fifoControl, divider, interruptEnable, interruptForce;
        private bool converting;
        private BigInteger conversionStart, dueUnit;
        private ulong anchorTime;

        private const double Reference = 3.3;
        private const int ConversionCycles = 96;
        private const uint Enable = 1, TemperatureEnable = 2, StartOnce = 4, StartMany = 8, Ready = 1u << 8;
        private const uint FifoEnable = 1, FifoShift = 2, FifoDreqEnable = 8, FifoEmpty = 1u << 8, FifoFull = 1u << 9;
        private const uint FifoUnder = 1u << 10, FifoOver = 1u << 11;
    }
}
