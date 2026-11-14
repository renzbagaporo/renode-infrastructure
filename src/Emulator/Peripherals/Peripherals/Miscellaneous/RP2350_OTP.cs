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
        public RP2350_OTP(IMachine machine) : base(machine) { Reset(); ResetArchitectureSelection(); }

        public override void Reset()
        {
            userControl = 1;
            bootDisable = 0;
            debugEnable = debugLock = 0;
            for(var page = 0; page < locks.Length; page++) locks[page] = MajorityByte(rows[0xf81 + page * 2]) & 0xf;
            sbpiInstruction = sbpiStatus = 0;
            Array.Clear(sbpiWriteData, 0, sbpiWriteData.Length);
            Array.Clear(sbpiReadData, 0, sbpiReadData.Length);
            Array.Clear(dapRegisters, 0, dapRegisters.Length);
            Array.Clear(pmcRegisters, 0, pmcRegisters.Length);
        }

        // Factory configuration input: each little-endian 32-bit word is one
        // raw 24-bit fuse row.
        public void LoadRawImage(string path)
        {
            var data = File.ReadAllBytes(path);
            if(data.Length != rows.Length * 4) throw new ArgumentException("OTP raw image must contain 4096 32-bit rows");
            for(var i = 0; i < rows.Length; i++) rows[i] = BitConverter.ToUInt32(data, i * 4) & 0xffffff;
            Reset();
            ResetArchitectureSelection();
        }

        // ARCHSEL belongs to the earliest switched-domain reset, ahead of the
        // PSM processor-cold stage. Resetting the OTP controller stage alone
        // must preserve the request that the boot ROM prepared for a watchdog.
        public void ResetArchitectureSelection()
        {
            architectureRequest = ConstrainArchitecture((Critical & 8) != 0 ? 3u : 0u);
        }

        public bool RequestedRiscV(int core)
        {
            if(core < 0 || core > 1) throw new ArgumentOutOfRangeException(nameof(core));
            return (architectureRequest & (1u << core)) != 0;
        }

        public bool LatchArchitecture(int core)
        {
            if(core < 0 || core > 1) throw new ArgumentOutOfRangeException(nameof(core));
            var bit = 1u << core;
            architectureStatus = (architectureStatus & ~bit) | (architectureRequest & bit);
            return (architectureStatus & bit) != 0;
        }

        // Factory configuration input: writes CHIPID0-3 (rows 0-3) as ECC rows,
        // CHIPID0 holding bits 15:0. The boot ROM reports bits 31:0 as the
        // device ID and bits 63:32 as the wafer ID.
        public void SetChipId(ulong id)
        {
            for(var i = 0; i < 4; i++) rows[i] = EncodeECC((ushort)(id >> (16 * i)));
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
            if(offset >= 0x104 && offset <= 0x110) return sbpiWriteData[(offset - 0x104) / 4];
            if(offset >= 0x114 && offset <= 0x120) return sbpiReadData[(offset - 0x114) / 4];
            switch(offset)
            {
            case 0x100: return sbpiInstruction;
            case 0x124: return sbpiStatus;
            case 0x128: return userControl;
            case 0x148: return Critical;
            case 0x14c:
                uint valid = 0;
                for(var i = 0; i < 6; i++) if(MajorityByte(rows[0xf79 + i]) != 0) valid |= 1u << (i + 1);
                return valid;
            case 0x150: return debugEnable;
            case 0x154: return debugLock;
            case 0x158: return architectureRequest;
            case 0x15c: return architectureStatus;
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
            if(offset >= 0x104 && offset <= 0x110)
            {
                sbpiWriteData[(offset - 0x104) / 4] = ApplyOperation(sbpiWriteData[(offset - 0x104) / 4], value, operation);
                return;
            }
            switch(offset)
            {
            case 0x100:
                sbpiInstruction = ApplyOperation(sbpiInstruction, value, operation, 0x7fffffff);
                if((sbpiInstruction & SbpiExecute) != 0) ExecuteSbpi();
                return;
            case 0x124:
                // INSTR_MISS, INSTR_DONE and RDATA_VLD clear on a write of 1.
                if(operation != AtomicOperation.Clear) sbpiStatus &= ~(value & 0x111);
                return;
            case 0x128:
                userControl = ApplyOperation(userControl, value, operation, 0x11);
                    return;
            case 0x150:
                debugEnable = ApplyOperation(debugEnable, value, operation, ~debugLock & 0x10f);
                return;
            case 0x154: debugLock |= value & 0x10f; return;
            case 0x158:
                architectureRequest = ConstrainArchitecture(ApplyOperation(architectureRequest, value, operation, 3));
                return;
            case 0x160:
                if((value & 1) != 0) bootDisable &= ~1u;
                bootDisable |= value & 2;
                return;
            default: this.LogUnhandledWrite(offset, value); return;
            }
        }

        // The SBPI bridge to the fuse macro, as the boot ROM drives it: register
        // writes and reads on the DAP (row address, data) and PMC (control)
        // targets, then a PMC START command programs the addressed row. Each
        // instruction completes at once; fuses only change from 0 to 1.
        private void ExecuteSbpi()
        {
            var instruction = sbpiInstruction;
            sbpiInstruction &= ~SbpiExecute;
            var isWrite = (instruction & (1u << 29)) != 0;
            var hasPayload = (instruction & (1u << 28)) != 0;
            var size = (int)((instruction >> 24) & 0xf) + 1;
            var target = (instruction >> 16) & 0xff;
            var command = (instruction >> 8) & 0xff;
            var registers = target == DapTarget ? dapRegisters : target == PmcTarget ? pmcRegisters : null;
            if(registers == null)
            {
                this.Log(LogLevel.Warning, "SBPI instruction 0x{0:x8} for unknown target 0x{1:x2} ignored", instruction, target);
            }
            else if(!hasPayload)
            {
                if(isWrite && target == PmcTarget && command == PmcStart) ProgramRow();
            }
            else if(isWrite && (command & 0xc0) == 0xc0)
            {
                for(var i = 0; i < size; i++)
                {
                    var data = size == 1 ? instruction & 0xff : (sbpiWriteData[i / 4] >> (8 * (i % 4))) & 0xff;
                    registers[((command & 0x3f) + i) & 0x3f] = (byte)data;
                }
            }
            else if(!isWrite && (command & 0xc0) == 0x80)
            {
                Array.Clear(sbpiReadData, 0, sbpiReadData.Length);
                for(var i = 0; i < size; i++)
                {
                    // The PMC status reads 0: programming is never busy.
                    var data = target == PmcTarget && ((command & 0x3f) + i) == PmcControlStatus ? 0u : registers[((command & 0x3f) + i) & 0x3f];
                    sbpiReadData[i / 4] |= data << (8 * (i % 4));
                }
                sbpiStatus |= SbpiReadDataValid;
            }
            sbpiStatus |= SbpiDone;
        }

        private void ProgramRow()
        {
            var row = (dapRegisters[DapAddress0] | (dapRegisters[DapAddress1] << 8)) & (rows.Length - 1);
            var data = (uint)(dapRegisters[DapData0] | (dapRegisters[DapData1] << 8) | (dapRegisters[DapDataECC] << 16));
            rows[row] |= data;
            this.Log(LogLevel.Debug, "Programmed OTP row 0x{0:x3} with 0x{1:x6}: now 0x{2:x6}", row, data, rows[row]);
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
                for(var bit = 0; bit < 2; bit++)
                {
                    var count = 0;
                    for(var row = 0x38; row < 0x40; row++) count += (int)((rows[row] >> bit) & 1);
                    if(count >= 3) result |= 1u << (16 + bit);
                }
                return result & 0x3007f;
            }
        }

        private uint ConstrainArchitecture(uint request)
        {
            var critical = Critical;
            if((critical & 0x10000) != 0) return 3;
            if((critical & 0x20001) != 0) return 0;
            return request & 3;
        }

        // 16 data bits, Hamming bits 20:16 and an even overall parity bit 21.
        private static uint EncodeECC(ushort data)
        {
            uint value = data;
            for(var i = 0; i < 5; i++) value |= Parity(data & ParityMasks[i]) << (16 + i);
            return value | (Parity(value) << 21);
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
        private uint architectureRequest;
        private uint architectureStatus;
        private uint sbpiInstruction;
        private uint sbpiStatus;
        private readonly uint[] sbpiWriteData = new uint[4];
        private readonly uint[] sbpiReadData = new uint[4];
        private readonly byte[] dapRegisters = new byte[64];
        private readonly byte[] pmcRegisters = new byte[64];
        private const uint SbpiExecute = 1u << 30, SbpiDone = 1u << 4, SbpiReadDataValid = 1u << 0;
        private const uint DapTarget = 0x02, PmcTarget = 0x3a, PmcStart = 0x01;
        private const int PmcControlStatus = 0x3f;
        private const int DapData0 = 0x00, DapData1 = 0x01, DapDataECC = 0x20, DapAddress0 = 0x3c, DapAddress1 = 0x3d;
        private static readonly uint[] ParityMasks = { 0x1ad5b, 0x2366d, 0x4c78e, 0x807f0, 0x10f800 };
    }
}
