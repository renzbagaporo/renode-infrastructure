//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Memory
{
    public sealed class RP2350_OTPData : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize
    {
        public RP2350_OTPData(IMachine machine, RP2350_OTP otp, bool raw = false, bool guarded = false)
        {
            this.machine = machine;
            this.otp = otp;
            this.raw = raw;
            this.guarded = guarded;
        }

        public byte ReadByte(long offset) => raw
            ? (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8))
            : (byte)(ReadECC((int)(offset / 2)) >> (int)((offset & 1) * 8));

        public ushort ReadWord(long offset) => raw
            ? (ushort)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 2) * 8))
            : ReadECC((int)(offset / 2));

        // In the ECC window each row is one halfword, corrected on its own: an
        // uncorrectable row reads as all ones (a bus error when guarded)
        // without affecting the row beside it in a word access.
        public uint ReadDoubleWord(long offset)
        {
            if(!raw) return ReadECC((int)(offset / 2)) | (uint)ReadECC((int)(offset / 2) + 1) << 16;
            var row = (int)(offset / 4);
            if(row >= 4096) return 0;
            if(!CheckReadable(row)) return uint.MaxValue;
            return otp.ReadRaw(row);
        }

        private ushort ReadECC(int row)
        {
            if(row >= 4096) return 0;
            if(!CheckReadable(row)) return ushort.MaxValue;
            if(otp.TryReadECC(row, out var data)) return data;
            if(guarded) throw new BusAccessException(BusAccessError.GenericError);
            return ushort.MaxValue;
        }

        private bool CheckReadable(int row)
        {
            var secure = true;
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu) && cpu is CortexM core && core.TrustZoneEnabled) secure = core.SecureState;
            if(otp.Readable(row, secure)) return true;
            if(guarded || !otp.UserInterfaceEnabled) throw new BusAccessException(BusAccessError.AddressError);
            return false;
        }

        public void WriteByte(long offset, byte value) => WriteDoubleWord(offset, value);
        public void WriteWord(long offset, ushort value) => WriteDoubleWord(offset, value);
        public void WriteDoubleWord(long offset, uint value) => throw new BusAccessException(BusAccessError.CommandError);
        public void Reset() { }
        public long Size => 0x4000;
        private readonly IMachine machine;
        private readonly RP2350_OTP otp;
        private readonly bool raw;
        private readonly bool guarded;
    }
}
