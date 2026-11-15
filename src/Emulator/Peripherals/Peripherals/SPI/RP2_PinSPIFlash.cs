//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SPI
{
    // A W25Q-style SPI NOR flash on four pins of an RP2 GPIO bank, in SPI mode
    // 0: it follows the CS, SCK and MOSI levels the board drives, samples MOSI
    // on each rising SCK edge and drives MISO as the pad's external input,
    // changing it on falling edges and releasing it while deselected. It
    // answers in the same machine with no latency, which a controller that
    // samples its own MISO (a PIO program) needs when the real part would be
    // on another machine.
    //
    // Commands: 03 read, 0B fast read, 02 page program, 20 sector (4 KiB),
    // 52 block (32 KiB) and D8 block (64 KiB) erase, 60/C7 chip erase, 05/35
    // status registers 1 and 2, 06 write enable, 04 write disable, 9F JEDEC
    // ID, 90 manufacturer and device ID, AB release power-down. Programming
    // and erasing complete at once, so BUSY reads 0. Erased bytes are FF.
    public sealed class RP2_PinSPIFlash : IPeripheral
    {
        public RP2_PinSPIFlash(IMachine machine, RP2_GPIOBank0 gpio, int csPin, int sckPin, int mosiPin, int misoPin,
            int size = 2 * 1024 * 1024, uint jedecId = 0xEF4015)
        {
            this.machine = machine;
            if(gpio == null) throw new ConstructionException("A GPIO bank is required");
            if(size <= 0 || (size & (size - 1)) != 0) throw new ConstructionException("The size must be a power of two");
            this.gpio = gpio;
            this.csPin = csPin;
            this.sckPin = sckPin;
            this.mosiPin = mosiPin;
            this.misoPin = misoPin;
            this.jedecId = jedecId;
            memory = new byte[size];
            Erase(0, size);
            gpio.DriveChanged += OnDriveChanged;
            Reset();
        }

        public void Reset()
        {
            lock(sync)
            {
                selected = false;
                writeEnabled = false;
                EndCommand();
            }
            gpio.ReleaseInput(misoPin);
        }

        // Fills the flash from a file, starting at offset.
        public void LoadBinary(ReadFilePath path, int offset = 0)
        {
            var data = File.ReadAllBytes(path);
            if(offset < 0 || offset + data.Length > memory.Length) throw new RecoverableException("The file does not fit the flash at that offset");
            lock(sync) Array.Copy(data, 0, memory, offset, data.Length);
        }

        public byte ReadByte(int address) => memory[address & (memory.Length - 1)];

        // The bytes the board has sent while selected since the last call,
        // as hex, one CS window per line.
        public string TakeMosiLog()
        {
            lock(sync)
            {
                var text = string.Join("\n", mosiLog.Select(window => string.Join(" ", window.Select(b => b.ToString("x2")))));
                mosiLog.Clear();
                if(selected) mosiLog.Add(new List<byte>());
                return text;
            }
        }

        public int Size => memory.Length;

        private void OnDriveChanged(int pin, bool value, bool enabled)
        {
            if(pin != csPin && pin != sckPin && pin != mosiPin) return;
            bool? miso;
            // The time the change happened. Changes from different sources
            // (a CPU's SIO write, a PIO a CPU access ran ahead of the machine
            // clock) can be reported out of their time order.
            var time = SignalTime.Of(machine);
            lock(sync)
            {
                var level = !enabled || value;
                if(pin == mosiPin)
                {
                    mosi = enabled && value;
                    return;
                }
                if(pin == csPin)
                {
                    if(level == !selected) return;
                    selected = !level;
                    csTime = time;
                    if(selected)
                    {
                        EndCommand();
                        mosiLog.Add(new List<byte>());
                        if(mosiLog.Count > MaxLoggedWindows) mosiLog.RemoveAt(0);
                        // SCK low at select is mode 0: the first bit goes out now.
                        // SCK high is mode 3: it goes out on the first falling edge.
                        awaitingFirstFall = sck;
                        if(awaitingFirstFall) return;
                        miso = OutputBit();
                    }
                    else
                    {
                        EndCommand();
                        miso = null;
                    }
                }
                else
                {
                    var rising = level && !sck;
                    var falling = !level && sck;
                    sck = level;
                    if(!selected || (!rising && !falling)) return;
                    if(time < csTime)
                    {
                        // An edge made before the latest select, reported after it:
                        // it only sets the SCK level the select found.
                        if(!falling || !awaitingFirstFall) return;
                        awaitingFirstFall = false;
                        miso = OutputBit();
                    }
                    else if(falling && awaitingFirstFall)
                    {
                        awaitingFirstFall = false;
                        miso = OutputBit();
                    }
                    else if(rising)
                    {
                        shiftIn = (byte)((shiftIn << 1) | (mosi ? 1 : 0));
                        if(++bitsIn == 8)
                        {
                            bitsIn = 0;
                            ReceiveByte(shiftIn);
                        }
                        return;
                    }
                    else miso = OutputBit();
                }
            }
            if(miso.HasValue) gpio.OnGPIO(misoPin, miso.Value);
            else gpio.ReleaseInput(misoPin);
        }

        // The output bit for the clock that follows: bit 7 of the byte the
        // flash sends in the current byte slot first.
        private bool OutputBit()
        {
            if(bitsOut == 0) outputByte = NextOutput();
            var bit = (outputByte & (0x80 >> bitsOut)) != 0;
            bitsOut = (bitsOut + 1) & 7;
            return bit;
        }

        private void ReceiveByte(byte value)
        {
            if(mosiLog.Count != 0) mosiLog[mosiLog.Count - 1].Add(value);
            received++;
            if(received == 1)
            {
                command = value;
                switch(command)
                {
                case 0x06: writeEnabled = true; break;
                case 0x04: writeEnabled = false; break;
                case 0x60:
                case 0xC7:
                    if(writeEnabled) Erase(0, memory.Length);
                    writeEnabled = false;
                    break;
                case 0x03: case 0x0B: case 0x02: case 0x20: case 0x52: case 0xD8:
                case 0x05: case 0x35: case 0x9F: case 0x90: case 0xAB:
                    break;
                default:
                    this.Log(LogLevel.Warning, "Unhandled command 0x{0:X2}", command);
                    break;
                }
                return;
            }
            if(received <= 4 && HasAddress(command))
            {
                address = ((address << 8) | value) & 0xFFFFFF;
                if(received == 4)
                {
                    switch(command)
                    {
                    case 0x20: EraseAligned(4096); break;
                    case 0x52: EraseAligned(32 * 1024); break;
                    case 0xD8: EraseAligned(64 * 1024); break;
                    }
                }
                return;
            }
            if(command == 0x02 && writeEnabled)
            {
                // Programming clears bits, and wraps within the 256-byte page.
                var target = (address & ~0xFF) | ((address + programmed) & 0xFF);
                memory[target & (memory.Length - 1)] &= value;
                programmed++;
            }
        }

        // The byte the flash sends in the slot now starting, from the bytes
        // received before it.
        private byte NextOutput()
        {
            var slot = received;
            switch(command)
            {
            case 0x03:
                return slot >= 4 ? memory[(address + slot - 4) & (memory.Length - 1)] : (byte)0xFF;
            case 0x0B:
                return slot >= 5 ? memory[(address + slot - 5) & (memory.Length - 1)] : (byte)0xFF;
            case 0x05:
                return slot >= 1 ? (byte)(writeEnabled ? 2 : 0) : (byte)0xFF;
            case 0x35:
                return slot >= 1 ? (byte)0 : (byte)0xFF;
            case 0x9F:
                return slot >= 1 && slot <= 3 ? (byte)(jedecId >> (8 * (3 - slot))) : (byte)0xFF;
            case 0x90:
                return slot >= 4 ? (byte)((slot - 4 + (int)(address & 1)) % 2 == 0 ? jedecId >> 16 : (jedecId & 0xFF) - 1) : (byte)0xFF;
            case 0xAB:
                return slot >= 4 ? (byte)((jedecId & 0xFF) - 1) : (byte)0xFF;
            default:
                return 0xFF;
            }
        }

        private void EndCommand()
        {
            if(received > 0 && (command == 0x02 || command == 0x20 || command == 0x52 || command == 0xD8) && received >= 4) writeEnabled = false;
            command = 0;
            received = 0;
            address = 0;
            programmed = 0;
            bitsIn = 0;
            bitsOut = 0;
            shiftIn = 0;
        }

        private static bool HasAddress(byte command)
        {
            switch(command)
            {
            case 0x03: case 0x0B: case 0x02: case 0x20: case 0x52: case 0xD8: case 0x90: case 0xAB: return true;
            default: return false;
            }
        }

        private void EraseAligned(int blockSize)
        {
            if(!writeEnabled) return;
            Erase(address & ~(blockSize - 1) & (memory.Length - 1), blockSize);
        }

        private void Erase(int start, int length)
        {
            for(var i = 0; i < length && start + i < memory.Length; i++) memory[start + i] = 0xFF;
        }

        private bool selected, sck, mosi, writeEnabled, awaitingFirstFall;
        private byte command, shiftIn, outputByte;
        private int received, bitsIn, bitsOut, address, programmed;

        private TimeInterval csTime;

        private readonly IMachine machine;
        private readonly RP2_GPIOBank0 gpio;
        private readonly int csPin, sckPin, mosiPin, misoPin;
        private readonly uint jedecId;
        private readonly byte[] memory;
        private readonly List<List<byte>> mosiLog = new List<List<byte>>();
        private readonly object sync = new object();

        private const int MaxLoggedWindows = 256;
    }
}
