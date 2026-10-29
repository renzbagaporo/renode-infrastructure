//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Numerics;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.DMA
{
    // One shared read/data slot models the bus pipeline. Requests accept reads;
    // writes already accepted into that slot drain independently of DREQ and EN.
    // This conservative bus pipeline completes at most one transfer per two
    // system cycles; it does not model the RP2040 crossbar's parallel masters.
    public sealed class RP2040_DMA : RP2APBPeripheral, IGPIOReceiver
    {
        public RP2040_DMA(IMachine machine, IRP2ClockSource clocks = null, ulong frequency = 125000000) : base(machine)
        {
            this.clocks = clocks;
            this.frequency = clocks == null ? frequency : clocks.SystemFrequency;
            IRQ0 = new GPIO();
            IRQ1 = new GPIO();
            cycleCounter = new PhasedTimer(machine.ClockSource, Math.Max(1ul, this.frequency), this, "cycles",
                direction: Direction.Ascending, enabled: this.frequency != 0);
            timer = new PhasedTimer(machine.ClockSource, Math.Max(1ul, this.frequency), this, "bus",
                limit: 1, workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += Tick;
            for(var i = 0; i < ChannelCount; i++) channels[i] = new Channel(i);
            if(clocks != null) clocks.ClockChanged += OnClockChanged;
            Reset();
        }

        public GPIO IRQ0 { get; }
        public GPIO IRQ1 { get; }

        // Hardware request numbers match the RP2040 TREQ_SEL encoding. Inputs
        // are FIFO-ready levels (as provided by RP2040_PIO), not pulse credits.
        public void OnGPIO(int number, bool value)
        {
            if(number < 0 || number >= requests.Length) return;
            lock(SyncRoot)
            {
                requests[number] = value;
                Schedule();
            }
        }

        public override void Reset()
        {
            lock(SyncRoot)
            {
                timer.Enabled = false;
                scheduled = false;
                pending = null;
                highServed = 0;
                lowCursor = highCursor = 0;
                rawInterrupts = sniffControl = sniffData = 0;
                Array.Clear(irqEnable, 0, irqEnable.Length);
                Array.Clear(irqForce, 0, irqForce.Length);
                Array.Clear(pacing, 0, pacing.Length);
                Array.Clear(pacingPhase, 0, pacingPhase.Length);
                lastTimerCycle = cycleCounter.Value;
                foreach(var channel in channels)
                {
                    channel.Generation++;
                    channel.ReadAddress = channel.WriteAddress = channel.Reload = channel.Remaining = channel.Control = channel.Errors = 0;
                    channel.Busy = channel.Aborting = false;
                    channel.TimerCredit = 0;
                }
                UpdateInterrupts();
            }
            PublishPending();
        }

        protected override void OnBeforeAccess()
        {
            // Report the CPU's retired instruction progress before sampling
            // registers. Descriptor writes have this DMA as their bus initiator
            // and must not recursively synchronize a CPU clock callback.
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        protected override void OnResetHeldChanged() => Schedule();
        protected override void OnAccessCompleted() => PublishPending();

        protected override uint ReadRegister(long offset)
        {
            UpdatePacing();
            if(offset < ChannelCount * 0x40)
            {
                var channel = channels[offset / 0x40];
                switch(RegisterKind(offset))
                {
                case 0: return channel.ReadAddress;
                case 1: return channel.WriteAddress;
                case 2: return channel.Remaining;
                case 3: return channel.Control | (channel.Busy ? Busy : 0) | channel.Errors | (channel.Errors != 0 ? 0x80000000u : 0);
                }
            }
            if(offset >= 0x800 && offset < 0x800 + ChannelCount * 0x40)
            {
                var channel = channels[(offset - 0x800) / 0x40];
                if((offset & 0x3f) == 4) return channel.Reload;
                if((offset & 0x3f) == 0) return (uint)(channel.Treq >= 59 && channel.Treq < 63 ? channel.TimerCredit : RequestAvailable(channel) ? 1 : 0);
                return 0;
            }
            if(offset >= 0x420 && offset < 0x430) return pacing[(offset - 0x420) / 4];
            switch(offset)
            {
            case 0x400: return rawInterrupts;
            case 0x404: return irqEnable[0];
            case 0x408: return irqForce[0];
            case 0x40c: return InterruptStatus(0);
            case 0x414: return irqEnable[1];
            case 0x418: return irqForce[1];
            case 0x41c: return InterruptStatus(1);
            case 0x434: return sniffControl;
            case 0x438:
                var value = (sniffControl & (1u << 10)) != 0 ? Reverse(sniffData) : sniffData;
                return (sniffControl & (1u << 11)) != 0 ? ~value : value;
            case 0x440: return pending == null ? 0 : 0x101u;
            case 0x444:
                uint aborts = 0;
                foreach(var channel in channels) if(channel.Aborting) aborts |= 1u << channel.Index;
                return aborts;
            case 0x448: return ChannelCount;
            default: return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            UpdatePacing();
            if(offset < ChannelCount * 0x40)
            {
                var channel = channels[offset / 0x40];
                switch(RegisterKind(offset))
                {
                case 0: channel.ReadAddress = ApplyOperation(channel.ReadAddress, value, operation); break;
                case 1: channel.WriteAddress = ApplyOperation(channel.WriteAddress, value, operation); break;
                case 2: channel.Reload = ApplyOperation(channel.Reload, value, operation); break;
                case 3:
                    channel.Control = ApplyOperation(channel.Control, value, operation, ControlMask);
                    channel.Errors &= ~(value & ErrorMask);
                    break;
                }
                if((offset & 0xf) == 0xc)
                {
                    if(value == 0)
                    {
                        if((channel.Control & Quiet) != 0) rawInterrupts |= 1u << channel.Index;
                    }
                    else Trigger(channel);
                }
            }
            else if(offset >= 0x420 && offset < 0x430)
            {
                var index = (int)((offset - 0x420) / 4);
                pacing[index] = ApplyOperation(pacing[index], value, operation);
                pacingPhase[index] = 0;
            }
            else if(offset >= 0x800 && offset < 0x800 + ChannelCount * 0x40 && (offset & 0x3f) == 0)
            {
                channels[(offset - 0x800) / 0x40].TimerCredit = 0;
            }
            else
            {
                switch(offset)
                {
                case 0x400: rawInterrupts &= ~(value & ChannelMask); break;
                case 0x404: irqEnable[0] = ApplyOperation(irqEnable[0], value, operation, ChannelMask); break;
                case 0x408: irqForce[0] = ApplyOperation(irqForce[0], value, operation, ChannelMask); break;
                case 0x40c: rawInterrupts &= ~(value & ChannelMask); break;
                case 0x414: irqEnable[1] = ApplyOperation(irqEnable[1], value, operation, ChannelMask); break;
                case 0x418: irqForce[1] = ApplyOperation(irqForce[1], value, operation, ChannelMask); break;
                case 0x41c: rawInterrupts &= ~(value & ChannelMask); break;
                case 0x430:
                    foreach(var channel in channels) if((value & (1u << channel.Index)) != 0) Trigger(channel);
                    break;
                case 0x434: sniffControl = ApplyOperation(sniffControl, value, operation, 0xfff); break;
                case 0x438: sniffData = ApplyOperation(sniffData, value, operation); break;
                case 0x444:
                    foreach(var channel in channels)
                    {
                        if((value & (1u << channel.Index)) == 0) continue;
                        // Abort cannot revoke a read accepted by the bus. Drain
                        // that data before acknowledging quiescence. Model the
                        // documented handshake, without the early-clear E13 erratum.
                        channel.Remaining = 0;
                        channel.TimerCredit = 0;
                        channel.Aborting = pending != null && pending.Channel == channel;
                        channel.Busy = channel.Aborting;
                        if(!channel.Aborting) channel.Generation++;
                    }
                    break;
                }
            }
            UpdateInterrupts();
            Schedule();
        }

        private void Trigger(Channel channel)
        {
            if(channel.Busy || (channel.Control & 1) == 0) return;
            channel.Remaining = channel.Reload;
            channel.TimerCredit = 0;
            channel.Busy = channel.Remaining != 0;
            channel.Generation++;
        }

        private void Tick()
        {
            Transfer transfer;
            bool write;
            lock(SyncRoot)
            {
                scheduled = false;
                UpdatePacing();
                if(ResetHeld || frequency == 0) return;
                write = pending != null;
                if(write) transfer = pending;
                else
                {
                    var channel = Arbitrate();
                    if(channel == null) { Schedule(); return; }
                    var size = (int)((channel.Control >> 2) & 3);
                    transfer = new Transfer { Channel = channel, Generation = channel.Generation,
                        ReadAddress = channel.ReadAddress, WriteAddress = channel.WriteAddress,
                        Control = channel.Control, Bytes = size == 3 ? 0 : 1 << size };
                    if(channel.Treq >= 59 && channel.Treq < 63) channel.TimerCredit--;
                    pending = transfer;
                }
                executing = true;
            }
            // Bus accesses can synchronously change GPIO/DREQ, trigger or abort
            // other channels. Never hold the register monitor across the bus.
            var error = false;
            try
            {
                if(transfer.Bytes == 0 || !Mapped(write ? transfer.WriteAddress : transfer.ReadAddress, transfer.Bytes)) error = true;
                else if(write) WriteBus(transfer);
                else transfer.Data = ReadBus(transfer);
            }
            finally
            {
                lock(SyncRoot)
                {
                    executing = false;
                    var channel = transfer.Channel;
                    if(channel.Generation == transfer.Generation && pending == transfer)
                    {
                        if(error)
                        {
                            channel.Errors |= write ? 1u << 29 : 1u << 30;
                            channel.Busy = channel.Aborting = false;
                            rawInterrupts |= 1u << channel.Index;
                            pending = null;
                        }
                        else if(write)
                        {
                            channel.WriteAddress = Increment(transfer.WriteAddress, transfer.Control, transfer.Bytes, true);
                            if(!channel.Aborting) channel.Remaining--;
                            pending = null;
                            if(channel.Aborting)
                            {
                                channel.Busy = channel.Aborting = false;
                                if((channel.Control & Quiet) == 0) rawInterrupts |= 1u << channel.Index;
                            }
                            else if(channel.Remaining == 0)
                            {
                                channel.Busy = false;
                                if((channel.Control & Quiet) == 0) rawInterrupts |= 1u << channel.Index;
                                var chain = (int)((channel.Control >> 11) & 15);
                                if(chain < ChannelCount && chain != channel.Index) Trigger(channels[chain]);
                            }
                        }
                        else
                        {
                            channel.ReadAddress = Increment(transfer.ReadAddress, transfer.Control, transfer.Bytes, false);
                            if((transfer.Control & (1u << 22)) != 0) transfer.Data = ByteSwap(transfer.Data, transfer.Bytes);
                            Sniff(transfer);
                        }
                    }
                    UpdateInterrupts();
                    Schedule();
                }
                PublishPending();
            }
        }

        private uint ReadBus(Transfer transfer)
        {
            switch(transfer.Bytes)
            {
            case 1: return machine.SystemBus.ReadByte(transfer.ReadAddress, context: this);
            case 2: return machine.SystemBus.ReadWord(transfer.ReadAddress, context: this);
            default: return machine.SystemBus.ReadDoubleWord(transfer.ReadAddress, context: this);
            }
        }

        private void WriteBus(Transfer transfer)
        {
            switch(transfer.Bytes)
            {
            case 1: machine.SystemBus.WriteByte(transfer.WriteAddress, (byte)transfer.Data, context: this); break;
            case 2: machine.SystemBus.WriteWord(transfer.WriteAddress, (ushort)transfer.Data, context: this); break;
            default: machine.SystemBus.WriteDoubleWord(transfer.WriteAddress, transfer.Data, context: this); break;
            }
        }

        private bool Mapped(uint address, int bytes) => (address & (uint)(bytes - 1)) == 0
            && machine.SystemBus.WhatIsAt(address, this) != null && machine.SystemBus.WhatIsAt((ulong)address + (uint)bytes - 1, this) != null;

        private Channel Arbitrate()
        {
            // Each high priority channel gets one turn per round, then one low
            // priority channel gets a turn. Both groups rotate independently.
            for(var pass = 0; pass < 2; pass++)
            {
                for(var i = 0; i < ChannelCount; i++)
                {
                    var index = (highCursor + i) % ChannelCount;
                    var channel = channels[index];
                    if((highServed & (1u << index)) != 0 || (channel.Control & 2) == 0 || !Ready(channel)) continue;
                    highServed |= 1u << index;
                    highCursor = (index + 1) % ChannelCount;
                    return channel;
                }
                highServed = 0;
                for(var i = 0; i < ChannelCount; i++)
                {
                    var index = (lowCursor + i) % ChannelCount;
                    var channel = channels[index];
                    if((channel.Control & 2) != 0 || !Ready(channel)) continue;
                    lowCursor = (index + 1) % ChannelCount;
                    return channel;
                }
            }
            return null;
        }

        private bool Ready(Channel channel) => channel.Busy && !channel.Aborting && (channel.Control & 1) != 0 && RequestAvailable(channel);
        private bool RequestAvailable(Channel channel) => channel.Treq == 63 || (channel.Treq < 59 ? requests[channel.Treq] : channel.TimerCredit != 0);

        private void Schedule()
        {
            if(executing || ResetHeld || frequency == 0) return;
            UpdatePacing();
            ulong delay = pending != null ? 1ul : ulong.MaxValue;
            foreach(var channel in channels)
            {
                if(!channel.Busy || (channel.Control & 1) == 0) continue;
                if(RequestAvailable(channel)) { delay = 1; break; }
                if(channel.Treq < 59 || channel.Treq >= 63) continue;
                var index = channel.Treq - 59;
                var x = pacing[index] >> 16;
                var y = pacing[index] & 0xffff;
                if(x == 0 || y == 0) continue;
                var next = x >= y ? 1 : (y - pacingPhase[index] + x - 1) / x;
                delay = Math.Min(delay, next);
            }
            if(delay == ulong.MaxValue)
            {
                timer.Enabled = false;
                scheduled = false;
                return;
            }
            var due = cycleCounter.Value + delay;
            if(scheduled && due >= dueCycle) return;
            timer.Enabled = false;
            timer.Limit = delay;
            timer.ResetValue();
            // A peripheral may assert DREQ between system clock edges. The
            // dormant bus timer must resume the running clock's exact phase.
            timer.Phase = cycleCounter.Phase;
            dueCycle = due;
            scheduled = true;
            timer.Enabled = true;
        }

        private void UpdatePacing()
        {
            var now = cycleCounter.Value;
            var elapsed = now - lastTimerCycle;
            lastTimerCycle = now;
            if(elapsed == 0) return;
            for(var index = 0; index < pacing.Length; index++)
            {
                var x = pacing[index] >> 16;
                var y = pacing[index] & 0xffff;
                if(x == 0 || y == 0) continue;
                var total = (BigInteger)elapsed * Math.Min(x, y) + pacingPhase[index];
                var pulses = total / y;
                pacingPhase[index] = (ulong)(total % y);
                foreach(var channel in channels)
                {
                    if(channel.Busy && (channel.Control & 1) != 0 && channel.Treq == index + 59)
                        channel.TimerCredit = (int)BigInteger.Min(63, channel.TimerCredit + pulses);
                }
            }
        }

        private void OnClockChanged()
        {
            lock(SyncRoot)
            {
                UpdatePacing();
                timer.Enabled = false;
                scheduled = false;
                frequency = clocks.SystemFrequency;
                cycleCounter.Enabled = frequency != 0;
                if(frequency != 0)
                {
                    cycleCounter.Frequency = frequency;
                    timer.Frequency = frequency;
                }
                Schedule();
            }
        }

        private void Sniff(Transfer transfer)
        {
            if((sniffControl & 1) == 0 || ((sniffControl >> 1) & 15) != transfer.Channel.Index || (transfer.Control & (1u << 23)) == 0) return;
            var data = (sniffControl & (1u << 9)) != 0 ? ByteSwap(transfer.Data, transfer.Bytes) : transfer.Data;
            var calculation = (sniffControl >> 5) & 15;
            if(calculation == 15) { sniffData = unchecked(sniffData + data); return; }
            if(calculation == 14)
            {
                data ^= data >> 16; data ^= data >> 8; data ^= data >> 4; data ^= data >> 2; data ^= data >> 1;
                sniffData ^= data & 1;
                return;
            }
            if(calculation > 3) return;
            var width = calculation < 2 ? 32 : 16;
            var polynomial = width == 32 ? 0x04c11db7u : 0x1021u;
            for(var b = 0; b < transfer.Bytes; b++)
            {
                var octet = (data >> (b * 8)) & 255;
                if((calculation & 1) != 0) octet = Reverse(octet) >> 24;
                sniffData ^= octet << (width - 8);
                for(var bit = 0; bit < 8; bit++) sniffData = (sniffData << 1) ^ ((sniffData & (1u << (width - 1))) != 0 ? polynomial : 0);
                if(width == 16) sniffData &= 0xffff;
            }
        }

        private static uint Increment(uint address, uint control, int bytes, bool write)
        {
            if((control & (write ? 0x20u : 0x10u)) == 0) return address;
            var next = unchecked(address + (uint)bytes);
            var ring = (int)((control >> 6) & 15);
            if(ring == 0 || ((control & (1u << 10)) != 0) != write) return next;
            var mask = (1u << ring) - 1;
            return (address & ~mask) | (next & mask);
        }

        private static int RegisterKind(long offset)
        {
            switch(offset & 0x3f)
            {
            case 0: case 0x14: case 0x28: case 0x3c: return 0;
            case 4: case 0x18: case 0x2c: case 0x34: return 1;
            case 8: case 0x1c: case 0x24: case 0x38: return 2;
            default: return 3;
            }
        }

        private static uint ByteSwap(uint value, int bytes) => bytes == 1 ? value : bytes == 2
            ? ((value & 255) << 8) | ((value >> 8) & 255)
            : (value << 24) | ((value & 0xff00) << 8) | ((value >> 8) & 0xff00) | (value >> 24);

        private static uint Reverse(uint value)
        {
            value = ((value & 0x55555555) << 1) | ((value >> 1) & 0x55555555);
            value = ((value & 0x33333333) << 2) | ((value >> 2) & 0x33333333);
            value = ((value & 0x0f0f0f0f) << 4) | ((value >> 4) & 0x0f0f0f0f);
            return ByteSwap(value, 4);
        }

        private uint InterruptStatus(int index) => (rawInterrupts & irqEnable[index]) | irqForce[index];

        private void UpdateInterrupts()
        {
            for(var i = 0; i < 2; i++)
            {
                var state = InterruptStatus(i) != 0;
                if(state == irqState[i]) continue;
                irqState[i] = state;
                notifications.Enqueue(new Notification { Index = i, State = state });
            }
        }

        private void PublishPending()
        {
            if(System.Threading.Monitor.IsEntered(SyncRoot)) return;
            lock(SyncRoot)
            {
                if(publishing) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    Notification notification;
                    lock(SyncRoot)
                    {
                        if(notifications.Count == 0) { publishing = false; return; }
                        notification = notifications.Dequeue();
                    }
                    (notification.Index == 0 ? IRQ0 : IRQ1).Set(notification.State);
                }
            }
            catch
            {
                lock(SyncRoot) publishing = false;
                throw;
            }
        }

        private sealed class PhasedTimer : LimitTimer
        {
            public PhasedTimer(IClockSource clockSource, ulong frequency, IPeripheral owner, string name,
                ulong limit = ulong.MaxValue, Direction direction = Direction.Descending, bool enabled = false,
                WorkMode workMode = WorkMode.Periodic, bool eventEnabled = false)
                : base(clockSource, frequency, owner, name, limit, direction, enabled, workMode, eventEnabled)
            {
                this.clockSource = clockSource;
            }

            public Fraction Phase
            {
                get => clockSource.GetClockEntry(OnLimitReached).ValueResiduum;
                set => clockSource.ExchangeClockEntryWith(OnLimitReached, entry =>
                {
                    entry.ValueResiduum = value;
                    return entry;
                });
            }

            private readonly IClockSource clockSource;
        }

        private sealed class Channel
        {
            public Channel(int index) { Index = index; }
            public int Treq => (int)((Control >> 15) & 63);
            public readonly int Index;
            public uint ReadAddress, WriteAddress, Reload, Remaining, Control, Errors;
            public ulong Generation;
            public bool Busy, Aborting;
            public int TimerCredit;
        }

        private sealed class Transfer
        {
            public Channel Channel;
            public ulong Generation;
            public uint ReadAddress, WriteAddress, Control, Data;
            public int Bytes;
        }

        private struct Notification { public int Index; public bool State; }

        private readonly Channel[] channels = new Channel[ChannelCount];
        private readonly bool[] requests = new bool[59];
        private readonly uint[] irqEnable = new uint[2], irqForce = new uint[2], pacing = new uint[4];
        private readonly ulong[] pacingPhase = new ulong[4];
        private readonly bool[] irqState = new bool[2];
        private readonly Queue<Notification> notifications = new Queue<Notification>();
        private readonly PhasedTimer timer, cycleCounter;
        private readonly IRP2ClockSource clocks;
        private Transfer pending;
        private ulong frequency, dueCycle, lastTimerCycle;
        private uint rawInterrupts, sniffControl, sniffData, highServed;
        private int lowCursor, highCursor;
        private bool scheduled, executing, publishing;

        private const int ChannelCount = 12;
        private const uint ChannelMask = (1u << ChannelCount) - 1;
        private const uint ControlMask = 0xffffff;
        private const uint ErrorMask = 0x60000000;
        private const uint Busy = 1u << 24;
        private const uint Quiet = 1u << 21;
    }
}
