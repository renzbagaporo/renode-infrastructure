//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_SHA256 : RP2APBPeripheral, IBytePeripheral, IWordPeripheral
    {
        public RP2350_SHA256(IMachine machine, RP2350_Clocks clocks = null, ulong frequency = 6000000) : base(machine)
        {
            this.clocks = clocks;
            timer = new LimitTimer(machine.ClockSource, frequency, this, "compression", limit: 57,
                workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += () => { Compress(); count = 0; sumValid = true; };
            if(clocks != null) clocks.ClockChanged += UpdateClock;
            Reset();
        }

        public override void Reset()
        {
            timer.Reset();
            control = 0x1200;
            count = 0;
            partial = 0;
            partialBytes = 0;
            error = false;
            sumValid = true;
            Array.Clear(sum, 0, sum.Length);
            Array.Clear(words, 0, words.Length);
            UpdateClock();
        }

        public new void WriteByte(long offset, byte value)
        {
            if((offset & 0xffc) == 4) WriteData(value, 1);
            else base.WriteByte(offset, value);
        }

        public new void WriteWord(long offset, ushort value)
        {
            if((offset & 0xffc) == 4) WriteData(value, 2);
            else base.WriteWord(offset, value);
        }

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu)) cpu.SyncTime();
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset == 0) return control | (sumValid ? 4u : 0) | (!timer.Enabled ? 2u : 0) | (error ? 0x10u : 0);
            if(offset >= 8 && offset <= 0x24 && offset % 4 == 0) return sum[(offset - 8) / 4];
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset == 4) { WriteData(value, 4); return; }
            if(offset != 0) { this.LogUnhandledWrite(offset, value); return; }
            control = ApplyOperation(control, value, operation, 0x1300);
            if((value & 0x10) != 0) error = false;
            if((value & 1) != 0 && operation != AtomicOperation.Clear)
            {
                timer.Enabled = false;
                count = 0;
                partial = 0;
                partialBytes = 0;
                Array.Copy(Initial, sum, sum.Length);
                sumValid = true;
            }
        }

        private void WriteData(uint value, int bytes)
        {
            OnBeforeAccess();
            if(ResetHeld) return;
            if(timer.Enabled) { error = true; return; }
            sumValid = false;
            for(var i = 0; i < bytes; i++)
            {
                partial |= ((value >> (i * 8)) & 0xff) << (partialBytes * 8);
                if(++partialBytes != 4) continue;
                words[count++] = (control & 0x1000) != 0 ? Swap(partial) : partial;
                partial = 0;
                partialBytes = 0;
                if(count == 16)
                {
                    timer.ResetValue();
                    timer.Enabled = clocks == null || clocks.SystemFrequency != 0;
                }
            }
        }

        private void Compress()
        {
            unchecked
            {
                for(var i = 16; i < 64; i++)
                {
                    var x = words[i - 15]; var y = words[i - 2];
                    words[i] = words[i - 16] + (Ror(x, 7) ^ Ror(x, 18) ^ (x >> 3))
                        + words[i - 7] + (Ror(y, 17) ^ Ror(y, 19) ^ (y >> 10));
                }
                var a = sum[0]; var b = sum[1]; var c = sum[2]; var d = sum[3];
                var e = sum[4]; var f = sum[5]; var g = sum[6]; var h = sum[7];
                for(var i = 0; i < 64; i++)
                {
                    var t1 = h + (Ror(e, 6) ^ Ror(e, 11) ^ Ror(e, 25)) + ((e & f) ^ (~e & g)) + K[i] + words[i];
                    var t2 = (Ror(a, 2) ^ Ror(a, 13) ^ Ror(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
                    h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
                }
                sum[0] += a; sum[1] += b; sum[2] += c; sum[3] += d;
                sum[4] += e; sum[5] += f; sum[6] += g; sum[7] += h;
            }
        }

        private void UpdateClock()
        {
            if(clocks == null) return;
            if(clocks.SystemFrequency != 0) timer.Frequency = clocks.SystemFrequency;
            timer.Enabled = count == 16 && clocks.SystemFrequency != 0;
        }

        private static uint Ror(uint x, int n) => (x >> n) | (x << (32 - n));
        private static uint Swap(uint x) => (x >> 24) | ((x >> 8) & 0xff00) | ((x << 8) & 0xff0000) | (x << 24);
        private readonly LimitTimer timer;
        private readonly RP2350_Clocks clocks;
        private readonly uint[] words = new uint[64];
        private readonly uint[] sum = new uint[8];
        private int count;
        private int partialBytes;
        private uint partial;
        private uint control;
        private bool error;
        private bool sumValid;
        private static readonly uint[] Initial = { 0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19 };
        private static readonly uint[] K = {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };
    }
}
