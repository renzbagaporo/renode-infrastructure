//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.IO;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_OTP : RP2APBPeripheral
    {
        public RP2350_OTP(IMachine machine) : base(machine) { Reset(); }

        public override void Reset()
        {
            userControl = 1;
            bootDisable = 0;
            debugEnable = debugLock = 0;
            for(var page = 0; page < locks.Length; page++) locks[page] = MajorityByte(rows[0xf81 + page * 2]) & 0xf;
        }

        // Factory configuration input: each little-endian 32-bit word is one
        // raw 24-bit fuse row. No guest programming completion is synthesized.
        public void LoadRawImage(string path)
        {
            var data = File.ReadAllBytes(path);
            if(data.Length != rows.Length * 4) throw new ArgumentException("OTP raw image must contain 4096 32-bit rows");
            for(var i = 0; i < rows.Length; i++) rows[i] = BitConverter.ToUInt32(data, i * 4) & 0xffffff;
            Reset();
        }

        public bool UserInterfaceEnabled => (userControl & 0x11) == 1;

        public bool Readable(int row, bool secure)
        {
            var state = (locks[row / 64] >> (secure ? 0 : 2)) & 3;
            return (userControl & 0x11) == 1 && state < 2;
        }

        public uint ReadRaw(int row) => rows[row];

        public bool TryReadECC(int row, out ushort data)
        {
            var value = rows[row];
            if((value & 0xc00000) == 0xc00000) value ^= 0xffffff;
            value &= 0x3fffff;
            var syndrome = 0;
            for(var i = 0; i < 5; i++) syndrome |= (int)Parity(value & ParityMasks[i]) << i;
            var odd = Parity(value);
            if(syndrome != 0)
            {
                if(odd == 0) { data = 0; return false; }
                var corrected = false;
                for(var bit = 0; bit < 22; bit++)
                {
                    var code = 0;
                    for(var i = 0; i < 5; i++) code |= (int)((ParityMasks[i] >> bit) & 1) << i;
                    if(code != syndrome) continue;
                    value ^= 1u << bit;
                    corrected = true;
                    break;
                }
                if(!corrected) { data = 0; return false; }
            }
            data = (ushort)value;
            return true;
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset < 0x100 && offset % 4 == 0) return locks[offset / 4];
            switch(offset)
            {
            case 0x124: return 0;
            case 0x128: return userControl;
            case 0x148: return Critical;
            case 0x14c:
                uint valid = 0;
                for(var i = 0; i < 6; i++) if(MajorityByte(rows[0xf79 + i]) != 0) valid |= 1u << (i + 1);
                return valid;
            case 0x150: return debugEnable;
            case 0x154: return debugLock;
            case 0x158: case 0x15c: return 0;
            case 0x160: return bootDisable;
            default: this.LogUnhandledRead(offset); return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(offset < 0x100 && offset % 4 == 0)
            {
                // SW_LOCK may only advance the lock state, even through CLR/XOR.
                locks[offset / 4] |= value & 0xf;
                    return;
            }
            switch(offset)
            {
            case 0x100:
                this.Log(LogLevel.Error, "OTP SBPI programming is not implemented; instruction will not complete");
                return;
            case 0x124: return;
            case 0x128:
                userControl = ApplyOperation(userControl, value, operation, 0x11);
                    return;
            case 0x150:
                debugEnable = ApplyOperation(debugEnable, value, operation, ~debugLock & 0x10f);
                return;
            case 0x154: debugLock |= value & 0x10f; return;
            case 0x158:
                if(value != 0) this.Log(LogLevel.Error, "RISC-V architecture selection is not supported by this Arm platform");
                return;
            case 0x160:
                if((value & 1) != 0) bootDisable &= ~1u;
                bootDisable |= value & 2;
                return;
            default: this.LogUnhandledWrite(offset, value); return;
            }
        }

        private uint Critical
        {
            get
            {
                uint result = 0;
                for(var bit = 0; bit < 8; bit++)
                {
                    var count = 0;
                    for(var row = 0x40; row < 0x48; row++) count += (int)((rows[row] >> bit) & 1);
                    if(count >= 3) result |= 1u << bit;
                }
                return result & 0x7f;
            }
        }

        private static uint MajorityByte(uint value) => ((value & (value >> 8)) | ((value >> 8) & (value >> 16)) | (value & (value >> 16))) & 0xff;
        private static uint Parity(uint value)
        {
            value ^= value >> 16; value ^= value >> 8; value ^= value >> 4;
            return (0x6996u >> (int)(value & 0xf)) & 1;
        }

        private readonly uint[] rows = new uint[4096];
        private readonly uint[] locks = new uint[64];
        private uint userControl;
        private uint bootDisable;
        private uint debugEnable;
        private uint debugLock;
        private static readonly uint[] ParityMasks = { 0x1ad5b, 0x2366d, 0x4c78e, 0x807f0, 0x10f800 };
    }
}
