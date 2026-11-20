//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.IO;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // RP2350 HSTX (datasheet 12.11): the control registers. The 8-word data
    // FIFO is RP2350_HSTXFifo, a separate bus peripheral that forwards here.
    //
    // The output side runs on an exact clk_hstx cycle grid: the output shift
    // register holds one word for N_SHIFTS cycles, right-rotating by SHIFT each
    // cycle, and refills on the cycle after its last shift when the FIFO (or
    // the command expander) has data, else it stops until a FIFO write. The
    // bit crossbar, DDR halves and clock generator are computed per half
    // cycle. Words are produced lazily, when the registers or FIFO are
    // accessed and when the FIFO is full and its DREQ must rise at a pop.
    //
    // Output is not driven onto the pads: a 300 Mb/s pin stream is far beyond
    // what pin-level emulation sustains. It is available as a recording (see
    // RecordOutput) and through OutputProduced.
    //
    // Simplifications: FIFO pops happen at the refill that needs the word, not
    // up to a word early through the asynchronous crossing; the command
    // expander takes one cycle per command pop, and that delays the refill only
    // when the previous word shifted for fewer cycles than the pops need.
    // PIO coupled mode is not modelled (indices 24-31 read the shift register).
    public sealed class RP2350_HSTX : RP2APBPeripheral
    {
        public RP2350_HSTX(IMachine machine, RP2350_Clocks clocks = null, ulong frequency = 150000000) : base(machine)
        {
            this.clocks = clocks;
            fallbackFrequency = frequency;
            timer = new LimitTimer(machine.ClockSource, TimeInterval.TicksPerSecond, this, "refill",
                limit: 1, direction: Direction.Ascending, workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += OnTimer;
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            cycleAnchorTime = machine.ClockSource.CurrentValue.Ticks;
            cycleFrequency = Frequency;
            Reset();
        }

        public override void Reset()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    Reanchor();
                    timer.Reset();
                    csr = 0x10050600;
                    for(var i = 0; i < bits.Length; i++) bits[i] = 0;
                    expandShift = 0x01000100;
                    expandTmds = 0;
                    fifo.Clear();
                    writeOnFull = false;
                    Stop();
                    QueueRequest();
                }
            });
            PublishPending();
        }

        // Records every output cycle to PATH: the header "HSTXREC1" and the
        // clk_hstx frequency (u64), then runs of contiguous shifting cycles,
        // each a u64 first cycle, a u32 cycle count and two bytes per cycle
        // (first half, second half; bit n is output BITn). A gap between runs
        // is a stall with the shift register empty.
        public void RecordOutput(string path)
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    Advance(CurrentCycle);
                    CloseRecording();
                    recording = new BinaryWriter(new BufferedStream(File.Create(path), 1 << 20));
                    recording.Write(System.Text.Encoding.ASCII.GetBytes("HSTXREC1"));
                    recording.Write(Frequency);
                }
            });
        }

        public void StopRecording()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    Advance(CurrentCycle);
                    CloseRecording();
                }
            });
        }

        public GPIO DREQ { get; } = new GPIO();

        // Raised for each output word, under the model's locks, with the first
        // cycle and the two half-cycle output bytes per cycle. Handlers must
        // not access the HSTX.
        public event Action<ulong, byte[]> OutputProduced;

        internal uint ReadFifoRegister(long offset)
        {
            OnBeforeAccess();
            uint value = 0;
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    if(ResetHeld || offset != 0) return;
                    Advance(CurrentCycle);
                    value = (uint)fifo.Count | (fifo.Count == 0 ? 1u << 9 : 0) | (fifo.Count == FifoDepth ? 1u << 8 : 0) | (writeOnFull ? 1u << 10 : 0);
                }
            });
            PublishPending();
            return value;
        }

        internal void WriteFifoRegister(long offset, uint value)
        {
            OnBeforeAccess();
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    if(ResetHeld) return;
                    var now = CurrentCycle;
                    Advance(now);
                    if(offset == 0)
                    {
                        if((value & (1u << 10)) != 0) writeOnFull = false;
                    }
                    else if(offset == 4)
                    {
                        if(fifo.Count == FifoDepth) writeOnFull = true;
                        else
                        {
                            fifo.Enqueue(value);
                            if(Enabled && !shifting) StartAt(now + 1);
                        }
                    }
                    Schedule();
                    QueueRequest();
                }
            });
            PublishPending();
        }

        protected override IClockSource AccessClock => machine.ClockSource;

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state) && state.DebugAccess) return;
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        protected override void OnAccessCompleted() => PublishPending();

        protected override void OnResetHeldChanged()
        {
            Advance(CurrentCycle);
            if(ResetHeld)
            {
                fifo.Clear();
                writeOnFull = false;
                Stop();
            }
            Schedule();
            QueueRequest();
        }

        protected override uint ReadRegister(long offset)
        {
            switch(offset)
            {
            case 0x00: return csr;
            case 0x24: return expandShift;
            case 0x28: return expandTmds;
            default:
                if(offset >= 0x04 && offset <= 0x20) return bits[(offset - 4) / 4];
                this.Log(LogLevel.Warning, "Unhandled read at 0x{0:X}", offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            var now = CurrentCycle;
            Advance(now);
            switch(offset)
            {
            case 0x00:
                var wasEnabled = Enabled;
                csr = ApplyOperation(csr, value, operation, 0xff1f1f73);
                if(!Enabled) Stop();
                else if(!wasEnabled) StartAt(now + 1);
                break;
            case 0x24: expandShift = ApplyOperation(expandShift, value, operation, 0x1f1f1f1f); break;
            case 0x28: expandTmds = ApplyOperation(expandTmds, value, operation, 0x00ffffff); break;
            default:
                if(offset >= 0x04 && offset <= 0x20)
                {
                    var index = (offset - 4) / 4;
                    bits[index] = ApplyOperation(bits[index], value, operation, 0x31f1f);
                }
                else this.Log(LogLevel.Warning, "Unhandled write of 0x{0:X} at 0x{1:X}", value, offset);
                break;
            }
            Schedule();
            QueueRequest();
        }

        private void OnTimer()
        {
            lock(SyncRoot)
            {
                Advance(CurrentCycle);
                Schedule();
                QueueRequest();
            }
            PublishPending();
        }

        private void OnClockChanged()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                lock(SyncRoot)
                {
                    Advance(CurrentCycle);
                    Reanchor();
                    Schedule();
                }
            });
        }

        // Produces every output word whose first cycle is at or before cycle.
        private void Advance(ulong cycle)
        {
            while(shifting && Enabled && wordStart + wordLength <= cycle)
            {
                var next = wordStart + wordLength;
                if(!TryProduceWord(wordLength, out var word, out var delay))
                {
                    shifting = false;
                    return;
                }
                Emit(next + delay, word);
            }
        }

        // The shift register is empty: refill at cycle if there is data.
        private void StartAt(ulong cycle)
        {
            if(shifting || !Enabled) return;
            if(TryProduceWord(uint.MaxValue, out var word, out var _)) Emit(cycle, word);
        }

        private void Emit(ulong cycle, uint word)
        {
            shifting = true;
            wordStart = cycle;
            wordLength = OutputShifts;
            var shift = (int)((csr >> 8) & 0x1f);
            var period = ClockPeriod * 2;
            var phase = (csr >> 24) & 0xf;
            var output = new byte[wordLength * 2];
            for(var i = 0; i < wordLength; i++)
            {
                var value = Rotate(word, shift * i);
                for(var half = 0; half < 2; half++)
                {
                    var clock = (clockHalfCycles + phase) % period >= period / 2;
                    clockHalfCycles = (clockHalfCycles + 1) % period;
                    byte levels = 0;
                    for(var pin = 0; pin < bits.Length; pin++)
                    {
                        var control = bits[pin];
                        var level = (control & (1u << 17)) != 0 ? clock
                            : ((value >> (int)((control >> (half == 0 ? 0 : 8)) & 0x1f)) & 1) != 0;
                        if((control & (1u << 16)) != 0) level = !level;
                        if(level) levels |= (byte)(1 << pin);
                    }
                    output[i * 2 + half] = levels;
                }
            }
            Record(cycle, output);
            OutputProduced?.Invoke(cycle, output);
        }

        // The next word for the output shift register, and the cycles it is
        // late by when the command expander needs more pops than the previous
        // word's shifts left it.
        private bool TryProduceWord(uint previousShifts, out uint word, out ulong delay)
        {
            delay = 0;
            word = 0;
            if((csr & ExpandEnable) == 0)
            {
                if(fifo.Count == 0) return false;
                word = fifo.Dequeue();
                return true;
            }
            uint commandPops = 0;
            while(true)
            {
                if(remaining == 0)
                {
                    if(fifo.Count == 0) return false;
                    var command = fifo.Dequeue();
                    commandPops++;
                    var opcode = (command >> 12) & 0xf;
                    if(opcode > 3) continue; // NOP, and reserved opcodes
                    this.opcode = opcode;
                    var count = command & 0xfff;
                    remaining = count == 0 ? long.MaxValue : count;
                    loaded = false;
                    continue;
                }
                if(!loaded)
                {
                    if(fifo.Count == 0) return false;
                    expansion = fifo.Dequeue();
                    loaded = true;
                    sinceLoad = 0;
                }
                break;
            }
            var raw = opcode < 2;
            var repeat = (opcode & 1) != 0;
            word = raw ? expansion : EncodeTmds(expansion);
            var fields = expandShift >> (raw ? 0 : 16);
            expansion = Rotate(expansion, (int)(fields & 0x1f));
            sinceLoad++;
            if(remaining != long.MaxValue) remaining--;
            var shifts = (fields >> 8) & 0x1f;
            if(!repeat && sinceLoad == (shifts == 0 ? 32 : shifts)) loaded = false;
            var needed = commandPops + 1;
            if(needed > previousShifts) delay = needed - previousShifts;
            return true;
        }

        private uint EncodeTmds(uint data)
        {
            uint result = 0;
            for(var lane = 0; lane < 3; lane++)
            {
                var field = expandTmds >> (lane * 8);
                var bitsValid = (int)((field >> 5) & 7) + 1;
                var value = Rotate(data, (int)(field & 0x1f)) & 0xff & (0xffu << (8 - bitsValid));
                result |= EncodeTmdsSymbol((byte)value, ref disparity[lane]) << (lane * 10);
            }
            return result;
        }

        // DVI 1.0 TMDS data encoding with running disparity.
        internal static uint EncodeTmdsSymbol(byte data, ref int disparity)
        {
            var ones = CountOnes(data);
            var useXnor = ones > 4 || (ones == 4 && (data & 1) == 0);
            uint qm = (uint)(data & 1);
            for(var i = 1; i < 8; i++)
            {
                var bit = ((qm >> (i - 1)) ^ ((uint)data >> i)) & 1;
                if(useXnor) bit ^= 1;
                qm |= bit << i;
            }
            if(!useXnor) qm |= 0x100;
            var qmOnes = CountOnes((byte)qm);
            var qmZeros = 8 - qmOnes;
            var qm8 = (qm & 0x100) != 0;
            if(disparity == 0 || qmOnes == qmZeros)
            {
                disparity += qm8 ? qmOnes - qmZeros : qmZeros - qmOnes;
                return qm8 ? 0x100 | (qm & 0xff) : 0x200 | (~qm & 0xff);
            }
            if((disparity > 0 && qmOnes > qmZeros) || (disparity < 0 && qmZeros > qmOnes))
            {
                disparity += (qm8 ? 2 : 0) + qmZeros - qmOnes;
                return 0x200 | (qm & 0x100) | (~qm & 0xff);
            }
            disparity += (qm8 ? 0 : -2) + qmOnes - qmZeros;
            return qm & 0x1ff;
        }

        private static int CountOnes(byte value)
        {
            var count = 0;
            for(; value != 0; value &= (byte)(value - 1)) count++;
            return count;
        }

        private static uint Rotate(uint value, int amount)
        {
            amount &= 31;
            return amount == 0 ? value : (value >> amount) | (value << (32 - amount));
        }

        // The timer is needed only to raise DREQ when a pop frees a full FIFO.
        private void Schedule()
        {
            timer.Enabled = false;
            if(!shifting || !Enabled || fifo.Count < FifoDepth || cycleFrequency == 0) return;
            var words = WordsBeforePop();
            if(words == ulong.MaxValue) return;
            var due = CycleStart(wordStart + wordLength + words * OutputShifts);
            var now = machine.ClockSource.CurrentValue.Ticks;
            timer.Restart(due > now ? due - now : 1);
        }

        // Output words the command expander can supply before it pops again.
        private ulong WordsBeforePop()
        {
            if((csr & ExpandEnable) == 0 || remaining == 0 || !loaded) return 0;
            var repeat = (opcode & 1) != 0;
            var raw = opcode < 2;
            var shifts = (expandShift >> (raw ? 8 : 24)) & 0x1f;
            var untilLoad = repeat ? ulong.MaxValue : (shifts == 0 ? 32u : shifts) - sinceLoad;
            var untilCommand = remaining == long.MaxValue ? ulong.MaxValue : (ulong)remaining;
            return Math.Min(untilLoad, untilCommand);
        }

        private void Stop()
        {
            shifting = false;
            clockHalfCycles = 0;
            remaining = 0;
            loaded = false;
            for(var i = 0; i < disparity.Length; i++) disparity[i] = 0;
        }

        private void Record(ulong cycle, byte[] output)
        {
            if(recording == null) return;
            if(runBuffer.Count != 0 && (cycle != runStart + (ulong)runBuffer.Count / 2 || runBuffer.Count > (1 << 20))) FlushRun();
            if(runBuffer.Count == 0) runStart = cycle;
            runBuffer.AddRange(output);
        }

        private void FlushRun()
        {
            if(runBuffer.Count == 0) return;
            recording.Write(runStart);
            recording.Write((uint)(runBuffer.Count / 2));
            recording.Write(runBuffer.ToArray());
            runBuffer.Clear();
        }

        private void CloseRecording()
        {
            if(recording == null) return;
            FlushRun();
            recording.Dispose();
            recording = null;
        }

        private void QueueRequest() => requestPending = !ResetHeld && fifo.Count < FifoDepth;

        // DREQ is set under the clock lock, outside the model's lock, as the
        // timer sets it: the DMA takes the clock lock on every DREQ change, so
        // a bus access setting it without that lock would wait for the clock
        // while holding the line, and a timer setting it under the clock
        // would wait for the line.
        private void PublishPending()
        {
            machine.ClockSource.ExecuteInLock(() =>
            {
                bool level;
                lock(SyncRoot)
                {
                    if(requestPending == publishedRequest) return;
                    level = publishedRequest = requestPending;
                }
                DREQ.Set(level);
            });
        }

        private bool Enabled => (csr & 1) != 0;
        private uint OutputShifts => ((csr >> 16) & 0x1f) == 0 ? 32 : (csr >> 16) & 0x1f;
        private uint ClockPeriod => (csr >> 28) == 0 ? 16 : csr >> 28;
        private ulong Frequency => clocks?.GetFrequency(HstxClock) ?? fallbackFrequency;

        private ulong CurrentCycle => cycleAnchor + (ulong)((System.Numerics.BigInteger)(machine.ClockSource.CurrentValue.Ticks - cycleAnchorTime) * cycleFrequency / TimeInterval.TicksPerSecond);

        private ulong CycleStart(ulong cycle)
        {
            if(cycleFrequency == 0) return ulong.MaxValue;
            var cycles = (System.Numerics.BigInteger)cycle - cycleAnchor;
            return cycleAnchorTime + (ulong)((cycles * TimeInterval.TicksPerSecond + cycleFrequency - 1) / cycleFrequency);
        }

        private void Reanchor()
        {
            cycleAnchor = CurrentCycle;
            cycleAnchorTime = machine.ClockSource.CurrentValue.Ticks;
            cycleFrequency = Frequency;
        }

        private uint csr;
        private uint expandShift;
        private uint expandTmds;
        private bool writeOnFull;
        private bool shifting;
        private ulong wordStart;
        private uint wordLength;
        private uint clockHalfCycles;
        private uint opcode;
        private long remaining;
        private bool loaded;
        private uint expansion;
        private uint sinceLoad;
        private bool requestPending;
        private bool publishedRequest;
        private ulong cycleAnchor;
        private ulong cycleAnchorTime;
        private ulong cycleFrequency;
        private BinaryWriter recording;
        private ulong runStart;

        private readonly uint[] bits = new uint[8];
        private readonly int[] disparity = new int[3];
        private readonly Queue<uint> fifo = new Queue<uint>();
        private readonly List<byte> runBuffer = new List<byte>();
        private readonly LimitTimer timer;
        private readonly RP2350_Clocks clocks;
        private readonly ulong fallbackFrequency;

        private const int FifoDepth = 8;
        private const int HstxClock = 7;
        private const uint ExpandEnable = 2;
    }

    // RP2350 HSTX data FIFO (HSTX_FIFO, 0x50600000): STAT and the write-only
    // FIFO register of the HSTX named by hstx.
    public sealed class RP2350_HSTXFifo : IDoubleWordPeripheral, IKnownSize, ISelfSynchronizedBusPeripheral
    {
        public RP2350_HSTXFifo(RP2350_HSTX hstx)
        {
            this.hstx = hstx;
        }

        public uint ReadDoubleWord(long offset) => hstx.ReadFifoRegister(offset & 0xfff);

        public void WriteDoubleWord(long offset, uint value) => hstx.WriteFifoRegister(offset & 0xfff, value);

        public void Reset()
        {
        }

        public long Size => 0x1000;

        private readonly RP2350_HSTX hstx;
    }
}
