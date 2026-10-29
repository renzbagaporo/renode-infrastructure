//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.USB
{
    // USB DPRAM uses byte lanes, unlike the peripheral APB register bridge.
    // Buffer control writes notify the USB controller when firmware hands a
    // buffer to the serial interface engine.
    public sealed class RP2_USB_DPRAM : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral, IKnownSize, IRP2ResettablePeripheral
    {
        public RP2_USB_DPRAM()
        {
            data = new byte[MemorySize];
        }

        public byte ReadByte(long offset)
        {
            lock(this)
            {
                CheckRange(offset, 1);
                if(UnavailableAccess()) return 0;
                return data[offset];
            }
        }

        public ushort ReadWord(long offset)
        {
            lock(this)
            {
                CheckRange(offset, 2);
                if(UnavailableAccess()) return 0;
                return (ushort)(data[offset] | (data[offset + 1] << 8));
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            lock(this)
            {
                CheckRange(offset, 4);
                if(UnavailableAccess()) return 0;
                return (uint)(data[offset] | (data[offset + 1] << 8)
                    | (data[offset + 2] << 16) | (data[offset + 3] << 24));
            }
        }

        public void WriteByte(long offset, byte value)
        {
            WriteBytes(offset, new byte[] { value });
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteBytes(offset, new byte[] { (byte)value, (byte)(value >> 8) });
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            WriteBytes(offset, new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) });
        }

        public byte[] ReadBytes(long offset, int count)
        {
            lock(this)
            {
                CheckRange(offset, count);
                var result = new byte[count];
                if(UnavailableAccess()) return result;
                Array.Copy(data, offset, result, 0, count);
                return result;
            }
        }

        public void WriteBytes(long offset, byte[] bytes)
        {
            lock(this)
            {
                CheckRange(offset, bytes.Length);
                if(UnavailableAccess()) return;
                Array.Copy(bytes, 0, data, offset, bytes.Length);
                var first = Math.Max(offset, BufferControlStart);
                var last = Math.Min(offset + bytes.Length, BufferControlEnd);
                if(first >= last)
                {
                    return;
                }
                var firstControl = (int)((first - BufferControlStart) / 4);
                var lastControl = (int)((last - 1 - BufferControlStart) / 4);
                for(var control = firstControl; control <= lastControl; ++control)
                {
                    BufferControlWritten?.Invoke(control);
                }
            }
        }

        public void Reset()
        {
            // The model retains backing bytes across reset. Firmware initializes
            // DPRAM after release; hardware retention is not specified here.
        }

        public bool ResetHeld
        {
            get { lock(this) return resetHeld; }
            set { lock(this) resetHeld = value; }
        }

        public long Size => MemorySize;

        public event Action<int> BufferControlWritten;

        // Access requires USBCTRL out of reset (RP2350 section 12.1.2.7).
        // Exact bus responses are unspecified: return zero, ignore writes, and
        // diagnose access rather than inventing an AHB fault or clearing SRAM.
        private bool UnavailableAccess()
        {
            if(!resetHeld) return false;
            this.Log(LogLevel.Warning, "USB DPRAM access while USBCTRL reset is held");
            return true;
        }

        private void CheckRange(long offset, int count)
        {
            if(offset < 0 || count < 0 || offset > MemorySize - count)
            {
                throw new RecoverableException($"USB DPRAM access outside its 4 KiB range: offset 0x{offset:X}, count {count}");
            }
        }

        private readonly byte[] data;
        private bool resetHeld;

        private const int MemorySize = 0x1000;
        private const int BufferControlStart = 0x80;
        private const int BufferControlEnd = 0x100;
    }
}
