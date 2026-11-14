//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // One SIO interpolator (INTERP0 or INTERP1 of one core), the same on the
    // RP2040 and RP2350. Each lane rotates its (optionally crossed)
    // accumulator right, masks it and optionally sign-extends it; the lane
    // results add BASE0/BASE1 (or the raw input with ADD_RAW) and FULL adds
    // both lanes to BASE2. INTERP0 lane 0 adds BLEND and INTERP1 lane 0 adds
    // CLAMP. POP writes the lane results back to the accumulators.
    internal sealed class RP2_Interpolator
    {
        public RP2_Interpolator(int index)
        {
            this.index = index;
            Reset();
        }

        public void Reset()
        {
            accumulator[0] = accumulator[1] = 0;
            baseValue[0] = baseValue[1] = baseValue[2] = 0;
            control[0] = control[1] = 0;
        }

        // offset is relative to the interpolator's block (0x00-0x3c).
        public uint Read(long offset)
        {
            switch(offset)
            {
            case 0x00: return accumulator[0];
            case 0x04: return accumulator[1];
            case 0x08: return baseValue[0];
            case 0x0c: return baseValue[1];
            case 0x10: return baseValue[2];
            case 0x14: return Pop(0);
            case 0x18: return Pop(1);
            case 0x1c: return Pop(2);
            case 0x20: return Peek(0);
            case 0x24: return Peek(1);
            case 0x28: return Peek(2);
            case 0x2c:
                var overflow0 = Overflows(0);
                var overflow1 = Overflows(1);
                return control[0] | (overflow0 ? 1u << 23 : 0) | (overflow1 ? 1u << 24 : 0)
                    | (overflow0 || overflow1 ? 1u << 25 : 0);
            case 0x30: return control[1];
            case 0x34: return ShiftMask(0);
            case 0x38: return ShiftMask(1);
            default: return 0;
            }
        }

        public void Write(long offset, uint value)
        {
            switch(offset)
            {
            case 0x00: accumulator[0] = value; break;
            case 0x04: accumulator[1] = value; break;
            case 0x08: baseValue[0] = value; break;
            case 0x0c: baseValue[1] = value; break;
            case 0x10: baseValue[2] = value; break;
            case 0x2c: control[0] = value & (index == 0 ? Lane0Interp0Mask : Lane0Interp1Mask); break;
            case 0x30: control[1] = value & Lane1Mask; break;
            case 0x34: accumulator[0] += value; break;
            case 0x38: accumulator[1] += value; break;
            case 0x3c:
                // In blend mode lane 1's SIGNED flag extends both halves.
                baseValue[0] = (Blend ? Signed(1) : Signed(0)) ? (uint)(short)value : value & 0xffff;
                baseValue[1] = Signed(1) ? (uint)(short)(value >> 16) : value >> 16;
                break;
            }
        }

        private uint Peek(int result)
        {
            Calculate(out var lane0, out var lane1, out var full);
            return result == 0 ? Present(0, lane0) : result == 1 ? Present(1, lane1) : full;
        }

        private uint Pop(int result)
        {
            Calculate(out var lane0, out var lane1, out var full);
            accumulator[0] = CrossResult(0) ? lane1 : lane0;
            accumulator[1] = CrossResult(1) ? lane0 : lane1;
            return result == 0 ? Present(0, lane0) : result == 1 ? Present(1, lane1) : full;
        }

        private void Calculate(out uint lane0, out uint lane1, out uint full)
        {
            var value0 = ShiftMask(0);
            var value1 = ShiftMask(1);
            var raw0 = Input(0) + baseValue[0];
            var raw1 = Input(1) + baseValue[1];
            if(Blend)
            {
                // A fraction of 0-255/256ths between BASE0 and BASE1.
                var alpha = value1 & 0xff;
                lane1 = Signed(1)
                    ? (uint)((int)baseValue[0] + (int)(((long)(int)baseValue[1] - (int)baseValue[0]) * alpha >> 8))
                    : (uint)(baseValue[0] + ((((long)baseValue[1] - baseValue[0]) * alpha) >> 8));
                lane0 = alpha;
                full = baseValue[2] + value0;
                return;
            }
            lane0 = AddRaw(0) ? raw0 : value0 + baseValue[0];
            lane1 = AddRaw(1) ? raw1 : value1 + baseValue[1];
            if(Clamp)
            {
                if(Signed(0)) lane0 = (uint)System.Math.Min(System.Math.Max((int)value0, (int)baseValue[0]), (int)baseValue[1]);
                else lane0 = System.Math.Min(System.Math.Max(value0, baseValue[0]), baseValue[1]);
            }
            full = baseValue[2] + value0 + value1;
        }

        // The lane's rotated, masked and (if SIGNED) sign-extended input.
        private uint ShiftMask(int lane)
        {
            var masked = Rotated(lane) & Mask(lane);
            var msb = MaskMsb(lane);
            if(Signed(lane) && msb < 31 && (masked & (1u << msb)) != 0) masked |= ~0u << (msb + 1);
            return masked;
        }

        private bool Overflows(int lane)
        {
            var msb = MaskMsb(lane);
            return msb < 31 && (Rotated(lane) >> (msb + 1)) != 0;
        }

        private uint Rotated(int lane)
        {
            var input = Input(lane);
            var shift = (int)(control[lane] & 31);
            return shift == 0 ? input : (input >> shift) | (input << (32 - shift));
        }

        private uint Mask(int lane)
        {
            var lsb = (int)((control[lane] >> 5) & 31);
            var msb = MaskMsb(lane);
            if(msb < lsb) return 0;
            var upper = msb == 31 ? ~0u : (1u << (msb + 1)) - 1;
            return upper & ~((1u << lsb) - 1);
        }

        private uint Present(int lane, uint result) => result | (((control[lane] >> 19) & 3) << 28);

        private uint Input(int lane) => accumulator[(control[lane] & CrossInputBit) != 0 ? lane ^ 1 : lane];
        private int MaskMsb(int lane) => (int)((control[lane] >> 10) & 31);
        private bool Signed(int lane) => (control[lane] & (1u << 15)) != 0;
        private bool CrossResult(int lane) => (control[lane] & (1u << 17)) != 0;
        private bool AddRaw(int lane) => (control[lane] & (1u << 18)) != 0;
        private bool Blend => index == 0 && (control[0] & (1u << 21)) != 0;
        private bool Clamp => index == 1 && (control[0] & (1u << 22)) != 0;

        private readonly int index;
        private readonly uint[] accumulator = new uint[2];
        private readonly uint[] baseValue = new uint[3];
        private readonly uint[] control = new uint[2];

        private const uint CrossInputBit = 1u << 16;
        private const uint Lane1Mask = 0x1fffff;
        private const uint Lane0Interp0Mask = 0x3fffff;
        private const uint Lane0Interp1Mask = 0x5fffff;
    }
}
