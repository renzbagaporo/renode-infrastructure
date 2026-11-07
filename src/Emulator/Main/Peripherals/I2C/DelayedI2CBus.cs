//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.I2C
{
    public static class DelayedI2CBusExtensions
    {
        public static void CreateDelayedI2CBus(this Emulation emulation, string name, ulong latencyMicroseconds = 300)
        {
            emulation.ExternalsManager.AddExternal(new DelayedI2CBus(TimeInterval.FromMicroseconds(latencyMicroseconds)), name);
        }
    }

    // A controller or target on a DelayedI2CBus.
    public interface IDelayedI2CBusEndpoint : IPeripheral
    {
        // Called once by DelayedI2CBus.Connect with the SDA and SCL pins the
        // bus is wired to, or -1 when no pins are given.
        void AttachToBus(DelayedI2CBus bus, int sdaPin, int sclPin);

        // Runs on the endpoint machine's clock, in the order the sender sent.
        void OnBusEvent(I2CBusEvent busEvent);
    }

    public enum I2CBusEventType
    {
        // Controller to every other endpoint: START or repeated START with
        // the address byte. Every receiver answers with Acknowledge.
        Start,
        // Controller to the targets that acknowledged the address: one data
        // byte. Each answers with Acknowledge.
        Write,
        // Controller to the targets that acknowledged the address: clock in
        // one byte, acknowledging the previous one. Each answers with Data.
        Read,
        // Controller to every other endpoint: STOP. No answer.
        Stop,
        // Target to controller: ACK (true) or NACK on the ninth clock.
        Acknowledge,
        // Target to controller: the byte driven for a Read.
        Data,
    }

    public readonly struct I2CBusEvent
    {
        public static I2CBusEvent Start(ulong transfer, int address, bool read, bool repeated) =>
            new I2CBusEvent(I2CBusEventType.Start, transfer, address, read, repeated, 0, false, null);

        public static I2CBusEvent Write(ulong transfer, byte data) =>
            new I2CBusEvent(I2CBusEventType.Write, transfer, 0, false, false, data, false, null);

        public static I2CBusEvent Read(ulong transfer) =>
            new I2CBusEvent(I2CBusEventType.Read, transfer, 0, true, false, 0, false, null);

        public static I2CBusEvent Stop(ulong transfer) =>
            new I2CBusEvent(I2CBusEventType.Stop, transfer, 0, false, false, 0, false, null);

        public static I2CBusEvent Acknowledge(ulong transfer, bool acknowledged) =>
            new I2CBusEvent(I2CBusEventType.Acknowledge, transfer, 0, false, false, 0, acknowledged, null);

        public static I2CBusEvent Reply(ulong transfer, byte data) =>
            new I2CBusEvent(I2CBusEventType.Data, transfer, 0, false, false, data, false, null);

        public I2CBusEvent WithSender(IDelayedI2CBusEndpoint sender) =>
            new I2CBusEvent(Type, Transfer, Address, IsRead, Repeated, Data, Acknowledged, sender);

        public override string ToString() => $"{Type} transfer {Transfer} address 0x{Address:X} read {IsRead} repeated {Repeated} data 0x{Data:X2} ack {Acknowledged}";

        public readonly I2CBusEventType Type;
        // Chosen by the controller for each address phase; answers carry it
        // back so a controller can ignore answers to an abandoned transfer.
        public readonly ulong Transfer;
        public readonly int Address;
        public readonly bool IsRead;
        public readonly bool Repeated;
        public readonly byte Data;
        public readonly bool Acknowledged;
        public readonly IDelayedI2CBusEndpoint Sender;

        private I2CBusEvent(I2CBusEventType type, ulong transfer, int address, bool read, bool repeated, byte data, bool acknowledged, IDelayedI2CBusEndpoint sender)
        {
            Type = type;
            Transfer = transfer;
            Address = address;
            IsRead = read;
            Repeated = repeated;
            Data = data;
            Acknowledged = acknowledged;
            Sender = sender;
        }
    }

    // An I2C bus between machines whose clocks advance up to a
    // synchronization quantum apart, carried at byte level. Each event
    // reaches the other endpoints `latency` after it was sent on the sender's
    // clock, scheduled on the receiver's clock at its next synchronization
    // point, so the latency must exceed two global quanta. A controller waits
    // for every answer before it finishes the clock it belongs to, as if the
    // target stretched SCL, so a byte costs at least one round trip.
    public sealed class DelayedI2CBus : IExternal, IDisposable
    {
        public DelayedI2CBus(TimeInterval latency = default(TimeInterval))
        {
            this.latency = latency;
        }

        // Configure the topology before starting the emulation.
        public void Connect(IDelayedI2CBusEndpoint endpoint, int sdaPin = -1, int sclPin = -1)
        {
            if(!EmulationManager.Instance.CurrentEmulation.TryGetMachineForPeripheral(endpoint, out var machine))
            {
                throw new RecoverableException("The endpoint does not belong to a machine");
            }
            lock(sync)
            {
                if(disposed) throw new ObjectDisposedException(nameof(DelayedI2CBus));
                if(endpoints.ContainsKey(endpoint)) throw new RecoverableException("The endpoint is already connected to this bus");
            }
            endpoint.AttachToBus(this, sdaPin, sclPin);
            lock(sync)
            {
                endpoints.Add(endpoint, machine);
                if(!views.ContainsKey(machine)) views.Add(machine, new MachineView(machine));
            }
        }

        public void Dispose()
        {
            lock(sync)
            {
                disposed = true;
                endpoints.Clear();
                views.Clear();
            }
        }

        // The number of endpoints other than the given one.
        public int PeerCount(IDelayedI2CBusEndpoint endpoint)
        {
            lock(sync) return endpoints.Count(e => e.Key != endpoint);
        }

        // Sends to `to`, or to every other endpoint when it is null.
        public void Send(IDelayedI2CBusEndpoint sender, I2CBusEvent busEvent, IDelayedI2CBusEndpoint to = null)
        {
            var work = new List<Tuple<MachineView, ulong, TimeInterval>>();
            lock(sync)
            {
                if(disposed || !endpoints.TryGetValue(sender, out var source)) return;
                var at = source.ClockSource.CurrentValue + latency;
                var stamped = busEvent.WithSender(sender);
                foreach(var target in endpoints)
                {
                    if(target.Key == sender || (to != null && target.Key != to)) continue;
                    var view = views[target.Value];
                    if(!view.Scheduled.TryGetValue(sender, out var queue))
                    {
                        queue = new Queue<Scheduled>();
                        view.Scheduled.Add(sender, queue);
                    }
                    var number = ++sequence;
                    queue.Enqueue(new Scheduled(number, target.Key, stamped));
                    work.Add(Tuple.Create(view, number, at));
                }
            }
            // The sender may hold its own clock lock; a receiving machine
            // schedules the event on its clock at its next synchronization point.
            foreach(var item in work)
            {
                var view = item.Item1;
                var number = item.Item2;
                var at = item.Item3;
                view.Machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => Schedule(view, sender, number, at));
            }
        }

        public TimeInterval Latency => latency;

        // Events that arrived after their scheduled time because a machine
        // was further ahead than the latency.
        public ulong LateDeliveries { get { lock(sync) return lateDeliveries; } }

        private void Schedule(MachineView view, IDelayedI2CBusEndpoint sender, ulong number, TimeInterval at)
        {
            var now = view.Machine.ClockSource.CurrentValue;
            ulong ticks = 1;
            if(at > now) ticks = (at - now).Ticks;
            else
            {
                lock(sync)
                {
                    if(lateDeliveries++ == 0)
                    {
                        this.Log(LogLevel.Warning, "A machine reached {0} past a delayed I2C event; raise the latency above two quanta", now - at);
                    }
                }
            }
            Action handler = null;
            handler = () =>
            {
                Deliver(view, sender, number);
                view.Machine.ClockSource.TryRemoveClockEntry(handler);
            };
            view.Machine.ClockSource.AddClockEntry(new ClockEntry(ticks, TimeInterval.TicksPerSecond, handler, this,
                "delayed I2C event", workMode: WorkMode.OneShot));
        }

        // Runs on the receiving machine's clock. Delivers every event from
        // the sender up to the given one, in the order it sent them.
        private void Deliver(MachineView view, IDelayedI2CBusEndpoint sender, ulong number)
        {
            var due = new List<Scheduled>();
            lock(sync)
            {
                if(disposed || !view.Scheduled.TryGetValue(sender, out var queue)) return;
                while(queue.Count != 0 && queue.Peek().Number <= number) due.Add(queue.Dequeue());
            }
            foreach(var item in due) item.Receiver.OnBusEvent(item.Event);
        }

        private sealed class MachineView
        {
            public MachineView(IMachine machine)
            {
                Machine = machine;
            }

            public readonly IMachine Machine;
            public readonly Dictionary<IDelayedI2CBusEndpoint, Queue<Scheduled>> Scheduled = new Dictionary<IDelayedI2CBusEndpoint, Queue<Scheduled>>();
        }

        private readonly struct Scheduled
        {
            public Scheduled(ulong number, IDelayedI2CBusEndpoint receiver, I2CBusEvent busEvent)
            {
                Number = number;
                Receiver = receiver;
                Event = busEvent;
            }

            public readonly ulong Number;
            public readonly IDelayedI2CBusEndpoint Receiver;
            public readonly I2CBusEvent Event;
        }

        private readonly object sync = new object();
        private readonly TimeInterval latency;
        private readonly Dictionary<IDelayedI2CBusEndpoint, IMachine> endpoints = new Dictionary<IDelayedI2CBusEndpoint, IMachine>();
        private readonly Dictionary<IMachine, MachineView> views = new Dictionary<IMachine, MachineView>();
        private ulong sequence;
        private ulong lateDeliveries;
        private bool disposed;
    }
}
