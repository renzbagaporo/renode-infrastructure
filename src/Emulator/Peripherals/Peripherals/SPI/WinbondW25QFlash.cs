//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Memory;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.SPI
{
    // W25Q16JV serial command set. ISPIPeripheral exchanges bytes in wire order;
    // the controller handles the serial/dual/quad lane widths and dummy clocks.
    public sealed class WinbondW25QFlash : ISPIPeripheral, IGPIOReceiver
    {
        public WinbondW25QFlash(IMachine machine, MappedMemory underlyingMemory, ulong uniqueId = 0x0123456789abcdef,
            byte memoryType = 0x40, bool quadEnabled = true)
        {
            if(underlyingMemory.Size != 2 * 1024 * 1024)
            {
                throw new ConstructionException("W25Q16JV requires a 2 MiB underlying memory");
            }
            this.underlyingMemory = underlyingMemory;
            this.uniqueId = uniqueId;
            this.memoryType = memoryType;
            underlyingMemory.ResetByte = 0xff;
            status2 = quadEnabled ? (byte)2 : (byte)0;
            transaction = new List<byte>();
            operationTimer = new LimitTimer(machine.ClockSource, 1000000, this, "operation",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            operationTimer.LimitReached += CompleteOperation;
        }

        public byte Transmit(byte data)
        {
            if(transaction.Count == 0)
            {
                command = data;
                if(continuousRead && data != 0xff)
                {
                    command = 0xeb;
                    transaction.Add(0xeb);
                }
                else
                {
                    transaction.Add(data);
                    if(Busy && command != 0x05 && command != 0x35 && command != 0x15)
                    {
                        command = 0;
                    }
                    if(!IsSupported(command))
                    {
                        this.Log(LogLevel.Warning, "Ignoring unsupported flash command 0x{0:X2}", data);
                    }
                    return 0xff;
                }
            }

            var index = transaction.Count - 1;
            transaction.Add(data);
            switch(command)
            {
            case 0x05:
                return (byte)(status1 | (Busy ? 1 : 0) | (writeEnabled ? 2 : 0));
            case 0x35:
                return status2;
            case 0x15:
                return status3;
            case 0x9f:
                return index % 3 == 0 ? (byte)0xef : index % 3 == 1 ? memoryType : (byte)0x15;
            case 0x4b:
                return index < 4 ? (byte)0xff : (byte)(uniqueId >> (56 - ((index - 4) % 8) * 8));
            case 0x03:
            case 0x0b:
            case 0x6b:
            case 0xeb:
                var dummyBytes = command == 0xeb ? 3 : command == 0x03 ? 0 : 1;
                if(command == 0x6b || command == 0xeb)
                {
                    if((status2 & 2) == 0)
                    {
                        return 0xff;
                    }
                }
                if(index < 3 + dummyBytes)
                {
                    return 0xff;
                }
                return underlyingMemory.ReadByte((Address + index - 3 - dummyBytes) & AddressMask);
            case 0x90:
                return index < 3 ? (byte)0xff : (index & 1) == 1 ? (byte)0xef : (byte)0x14;
            default:
                return 0xff;
            }
        }

        public void FinishTransmission()
        {
            if(transaction.Count == 0)
            {
                return;
            }
            if(!Busy)
            {
                switch(command)
                {
                case 0x06:
                    if(transaction.Count == 1) writeEnabled = true;
                    break;
                case 0x04:
                    if(transaction.Count == 1) writeEnabled = false;
                    break;
                case 0x01:
                case 0x31:
                case 0x11:
                    if(writeEnabled && transaction.Count >= 2 && transaction.Count <= (command == 0x01 ? 3 : 2))
                    {
                        BeginOperation(10000);
                    }
                    break;
                case 0x02:
                case 0x32:
                    if(writeEnabled && transaction.Count >= 5 && (command != 0x32 || (status2 & 2) != 0))
                    {
                        BeginOperation(400);
                    }
                    break;
                case 0x20:
                case 0x52:
                case 0xd8:
                    if(writeEnabled && transaction.Count == 4) BeginOperation(command == 0x20 ? 45000u : command == 0x52 ? 120000u : 150000u);
                    break;
                case 0xc7:
                case 0x60:
                    if(writeEnabled && transaction.Count == 1) BeginOperation(5000000);
                    break;
                case 0xeb:
                    if(transaction.Count >= 5 && (status2 & 2) != 0)
                    {
                        continuousRead = (transaction[4] & 0xf0) == 0xa0;
                    }
                    break;
                case 0xff:
                    continuousRead = false;
                    break;
                }
            }
            transaction.Clear();
            command = 0;
            StateChanged?.Invoke();
        }

        public void OnGPIO(int number, bool value)
        {
            if(number == 0 && value) FinishTransmission();
        }

        public void Reset()
        {
            // Controller reset does not erase the nonvolatile array or status registers.
            transaction.Clear();
            continuousRead = false;
            writeEnabled = false;
            command = 0;
            StateChanged?.Invoke();
        }

        public MappedMemory UnderlyingMemory => underlyingMemory;

        public event Action StateChanged;

        public bool ReadAvailable => !Busy;

        public bool QuadEnabled => (status2 & 2) != 0;

        public bool ContinuousReadEnabled => continuousRead;

        // Durations use the typical values in W25Q16JV Rev G, AC characteristics.
        private void BeginOperation(uint durationMicroseconds)
        {
            if(command == 0x02 || command == 0x32)
            {
                if(IsProtected(Address)) return;
            }
            else if(command == 0x20 || command == 0x52 || command == 0xd8 || command == 0xc7 || command == 0x60)
            {
                var length = command == 0x20 ? 4096 : command == 0x52 ? 32768 : command == 0xd8 ? 65536 : AddressMask + 1;
                var start = Address & ~(length - 1);
                for(var i = start; i < start + length; i++)
                {
                    if(IsProtected(i)) return;
                }
            }
            else if((status2 & 1) != 0)
            {
                this.Log(LogLevel.Warning, "Ignoring status-register write while SRL is set");
                return;
            }
            pendingCommand = command;
            pendingData = transaction.ToArray();
            operationTimer.Limit = durationMicroseconds;
            operationTimer.ResetValue();
            operationTimer.Enabled = true;
            StateChanged?.Invoke();
        }

        private void CompleteOperation()
        {
            var address = pendingData.Length >= 4 ? ((pendingData[1] << 16) | (pendingData[2] << 8) | pendingData[3]) & AddressMask : 0;
            switch(pendingCommand)
            {
            case 0x01:
                status1 = (byte)(pendingData[1] & 0xfc);
                if(pendingData.Length == 3) status2 = (byte)((status2 & 0x38) | (pendingData[2] & 0x43) | (pendingData[2] & 0x38));
                break;
            case 0x31:
                status2 = (byte)((status2 & 0x38) | (pendingData[1] & 0x7b));
                break;
            case 0x11:
                status3 = (byte)(pendingData[1] & 0x64);
                break;
            case 0x02:
            case 0x32:
                // The last received byte wins when the 256-byte page buffer wraps.
                var page = new Dictionary<int, byte>();
                for(var i = 4; i < pendingData.Length; i++) page[(address & ~255) | ((address + i - 4) & 255)] = pendingData[i];
                foreach(var entry in page)
                {
                    if(!IsProtected(entry.Key)) underlyingMemory.WriteByte(entry.Key, (byte)(underlyingMemory.ReadByte(entry.Key) & entry.Value));
                }
                break;
            default:
                var length = pendingCommand == 0x20 ? 4096 : pendingCommand == 0x52 ? 32768 : pendingCommand == 0xd8 ? 65536 : AddressMask + 1;
                address &= ~(length - 1);
                for(var i = address; i < address + length; i++)
                {
                    if(!IsProtected(i)) underlyingMemory.WriteByte(i, 0xff);
                }
                break;
            }
            writeEnabled = false;
            pendingData = null;
            StateChanged?.Invoke();
        }

        private bool IsProtected(int address)
        {
            var bp = (status1 >> 2) & 7;
            var length = bp == 0 ? 0 : bp >= 6 ? AddressMask + 1
                : (status1 & 0x40) != 0 ? 4096 << (bp - 1) : (AddressMask + 1) >> (6 - bp);
            var protectedByBP = (status1 & 0x20) != 0 ? address < length : address >= AddressMask + 1 - length;
            return (status2 & 0x40) != 0 ? !protectedByBP : protectedByBP;
        }

        private static bool IsSupported(byte value)
        {
            switch(value)
            {
            case 0x01: case 0x02: case 0x03: case 0x04: case 0x05: case 0x06: case 0x0b:
            case 0x11: case 0x15: case 0x20: case 0x31: case 0x32: case 0x35: case 0x4b:
            case 0x52: case 0x60: case 0x6b: case 0x90: case 0x9f: case 0xc7: case 0xd8: case 0xeb: case 0xff:
                return true;
            default:
                return false;
            }
        }

        private int Address => transaction.Count >= 4 ? ((transaction[1] << 16) | (transaction[2] << 8) | transaction[3]) & AddressMask : 0;
        private bool Busy => operationTimer.Enabled;
        private readonly MappedMemory underlyingMemory;
        private readonly ulong uniqueId;
        private readonly byte memoryType;
        private readonly LimitTimer operationTimer;
        private readonly List<byte> transaction;
        private byte command;
        private byte status1;
        private byte status2;
        private byte status3;
        private bool writeEnabled;
        private bool continuousRead;
        private byte pendingCommand;
        private byte[] pendingData;
        private const int AddressMask = 0x1fffff;
    }
}
