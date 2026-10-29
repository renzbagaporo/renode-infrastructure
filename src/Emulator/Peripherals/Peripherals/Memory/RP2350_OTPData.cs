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

        public byte ReadByte(long offset) => (byte)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 3) * 8));
        public ushort ReadWord(long offset) => (ushort)(ReadDoubleWord(offset & ~3L) >> (int)((offset & 2) * 8));
        public uint ReadDoubleWord(long offset)
        {
            var row = (int)(offset / (raw ? 4 : 2));
            if(row >= 4096) return 0;
            var secure = true;
            if(machine.SystemBus.TryGetCurrentCPU(out var cpu) && cpu is CortexM core && core.TrustZoneEnabled) secure = core.SecureState;
            if(!otp.Readable(row, secure))
            {
                if(guarded || !otp.UserInterfaceEnabled) throw new BusAccessException(BusAccessError.AddressError);
                return uint.MaxValue;
            }
            if(raw) return otp.ReadRaw(row);
            if(!otp.TryReadECC(row, out var low) || !otp.TryReadECC(row + 1, out var high))
            {
                if(guarded) throw new BusAccessException(BusAccessError.GenericError);
                return uint.MaxValue;
            }
            return low | (uint)high << 16;
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
