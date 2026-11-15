//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Utilities.RESD;

namespace Antmicro.Renode.Peripherals.Analog
{
    // A one- or two-stage RC low-pass filter between a digital driver and an
    // analog input: input -> R1 -> node A (C1 to GND) -> R2 -> output (C2 to
    // GND). GPIO input 0 drives the input to 0 V or HighVolts; with R2 = 0
    // the filter is the single stage R1/C1. Between input edges the network
    // is integrated exactly, so a PWM input gives its mean level with the
    // real ripple. Sample is the output voltage at the current virtual time,
    // unloaded by the input that reads it. As a wire endpoint it only listens.
    public sealed class RCLowPassFilter : IRESDSampleSource<VoltageSample>, IThreeStateGPIO
    {
        public RCLowPassFilter(IMachine machine, double r1Ohms, double c1Nanofarads,
            double r2Ohms = 0, double c2Nanofarads = 0, double highVolts = 3.3)
        {
            if(r1Ohms <= 0 || c1Nanofarads <= 0) throw new ConstructionException("R1 and C1 must be positive");
            if(r2Ohms < 0 || c2Nanofarads < 0 || (r2Ohms > 0) != (c2Nanofarads > 0)) throw new ConstructionException("R2 and C2 are both positive (two stages) or both zero (one stage)");
            this.machine = machine;
            this.highVolts = highVolts;
            var c1 = c1Nanofarads * 1e-9;
            twoStage = r2Ohms > 0;
            if(twoStage)
            {
                var c2 = c2Nanofarads * 1e-9;
                // x' = A x + b u with x = (vA, vOut); both nodes settle at u.
                a11 = -(1 / r1Ohms + 1 / r2Ohms) / c1;
                a12 = 1 / (r2Ohms * c1);
                a21 = 1 / (r2Ohms * c2);
                a22 = -1 / (r2Ohms * c2);
                var trace = a11 + a22;
                var determinant = a11 * a22 - a12 * a21;
                var root = Math.Sqrt(trace * trace / 4 - determinant);
                lambda1 = trace / 2 + root;
                lambda2 = trace / 2 - root;
            }
            else
            {
                lambda1 = -1 / (r1Ohms * c1);
            }
            Reset();
        }

        public void Reset()
        {
            lock(sync)
            {
                input = 0;
                nodeA = output = 0;
                lastTicks = machine.ClockSource.CurrentValue.Ticks;
            }
        }

        public void OnGPIO(int number, bool value)
        {
            if(number != 0) return;
            lock(sync)
            {
                Advance();
                input = value ? highVolts : 0;
            }
        }

        public void GetOutputState(int number, out bool level, out bool enabled)
        {
            level = false;
            enabled = false;
        }

        public event Action<int, bool, bool> DriveChanged
        {
            add { }
            remove { }
        }

        public double Volts
        {
            get
            {
                lock(sync)
                {
                    Advance();
                    return output;
                }
            }
        }

        public VoltageSample Sample => new VoltageSample((uint)Math.Max(0, Math.Round(Volts * 1e6)));

        private void Advance()
        {
            var now = machine.ClockSource.CurrentValue.Ticks;
            if(now <= lastTicks) return;
            var seconds = (now - lastTicks) / (double)Time.TimeInterval.TicksPerSecond;
            lastTicks = now;
            if(!twoStage)
            {
                output = input + (output - input) * Math.Exp(lambda1 * seconds);
                nodeA = output;
                return;
            }
            // x(t) = u + e^(A t) (x(0) - u), with e^(A t) from Sylvester's
            // formula for the two distinct real eigenvalues.
            var e1 = Math.Exp(lambda1 * seconds);
            var e2 = Math.Exp(lambda2 * seconds);
            var difference = lambda1 - lambda2;
            var p = (e1 - e2) / difference;
            var q = (lambda1 * e2 - lambda2 * e1) / difference;
            var dA = nodeA - input;
            var dOut = output - input;
            nodeA = input + (a11 * p + q) * dA + a12 * p * dOut;
            output = input + a21 * p * dA + (a22 * p + q) * dOut;
        }

        private double input;
        private double nodeA;
        private double output;
        private ulong lastTicks;

        private readonly IMachine machine;
        private readonly double highVolts;
        private readonly bool twoStage;
        private readonly double a11, a12, a21, a22;
        private readonly double lambda1, lambda2;
        private readonly object sync = new object();
    }
}
