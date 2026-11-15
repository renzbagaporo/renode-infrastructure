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

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public static class DelayedDigitalWireExtensions
    {
        public static void CreateDelayedDigitalWire(this Emulation emulation, string name, bool pullUp = true, ulong latencyMicroseconds = 300)
        {
            emulation.ExternalsManager.AddExternal(new DelayedDigitalWire(pullUp, TimeInterval.FromMicroseconds(latencyMicroseconds)), name);
        }
    }

    // A digital net between machines whose clocks advance up to a
    // synchronization quantum apart. A machine sees each driver of another
    // machine as it was `latency` earlier on that driver's clock, so edge
    // spacing is kept exactly and delivery does not depend on host thread
    // timing. A change reaches the receiving machine at its next
    // synchronization point, where that machine schedules it on its own
    // clock, so the latency must exceed two global quanta. Endpoints on the driving
    // machine see a change at once. Pad pull changes travel the same way. Each
    // machine resolves its strong drivers, the external pull-up and the pad
    // pulls as DigitalWire does.
    public sealed class DelayedDigitalWire : IExternal, IDisposable
    {
        public DelayedDigitalWire(bool pullUp = true, TimeInterval latency = default(TimeInterval))
        {
            this.pullUp = pullUp;
            this.latency = latency;
        }

        // Configure the topology before starting the emulation.
        public void Connect(IThreeStateGPIO endpoint, int pin)
        {
            if(!EmulationManager.Instance.CurrentEmulation.TryGetMachineForPeripheral(endpoint, out var machine))
            {
                throw new RecoverableException("The endpoint does not belong to a machine");
            }
            endpoint.GetOutputState(pin, out var level, out var enabled);
            var pulled = endpoint as IPulledThreeStateGPIO;
            var connection = new Connection(endpoint, pin, machine, level, enabled);
            connection.Pull = pulled?.GetPull(pin);
            connection.Handler = (number, value, drive) =>
            {
                if(number == pin) UpdateDriver(connection, value, drive, connection.Pull);
            };
            connection.PullHandler = (number, pull) =>
            {
                if(number == pin) UpdateDriver(connection, connection.Level, connection.Enabled, pull);
            };
            lock(sync)
            {
                if(disposed) throw new ObjectDisposedException(nameof(DelayedDigitalWire));
                if(connections.Any(c => ReferenceEquals(c.Endpoint, endpoint) && c.Pin == pin))
                {
                    throw new RecoverableException("Pin is already connected to this digital wire");
                }
                connections.Add(connection);
                foreach(var view in views.Values) view.Remote[connection] = Driver.Released;
                if(!views.TryGetValue(machine, out var own))
                {
                    own = new MachineView(machine);
                    var created = own;
                    own.Timer = () => OnTimer(created);
                    foreach(var other in connections.Where(c => c.Machine != machine)) own.Remote[other] = new Driver(other.Level, other.Enabled, other.Pull);
                    views.Add(machine, own);
                }
                // Setup-time driver states apply to every machine immediately.
                foreach(var view in views.Values.Where(v => v.Machine != machine)) view.Remote[connection] = new Driver(level, enabled, connection.Pull);
                own.Level = Resolve(own);
                own.Pending.Enqueue(new Delivery(connection, own.Level));
                foreach(var view in views.Values) Refresh(view);
            }
            endpoint.DriveChanged += connection.Handler;
            if(pulled != null) pulled.PullChanged += connection.PullHandler;
            endpoint.GetOutputState(pin, out level, out enabled);
            UpdateDriver(connection, level, enabled, pulled?.GetPull(pin));
            foreach(var view in views.Values.ToArray()) Publish(view);
        }

        public void Dispose()
        {
            Connection[] old;
            lock(sync)
            {
                if(disposed) return;
                disposed = true;
                old = connections.ToArray();
                connections.Clear();
                views.Clear();
            }
            foreach(var connection in old)
            {
                connection.Endpoint.DriveChanged -= connection.Handler;
                if(connection.Endpoint is IPulledThreeStateGPIO pulled) pulled.PullChanged -= connection.PullHandler;
            }
        }

        public TimeInterval Latency => latency;

        // Deliveries that arrived after their scheduled time because a
        // machine was further ahead than the latency.
        public ulong LateDeliveries { get { lock(sync) return lateDeliveries; } }

        private void UpdateDriver(Connection connection, bool value, bool enabled, bool? pull)
        {
            MachineView own;
            var post = new List<MachineView>();
            lock(sync)
            {
                if(disposed || (connection.Level == value && connection.Enabled == enabled && connection.Pull == pull)) return;
                connection.Level = value;
                connection.Enabled = enabled;
                connection.Pull = pull;
                own = views[connection.Machine];
                Refresh(own);
                // A change delivered after its driver released its clock lock carries
                // the time it happened; see SignalTime.
                var at = SignalTime.Of(connection.Machine) + latency;
                foreach(var view in views.Values.Where(v => v.Machine != connection.Machine))
                {
                    var change = new RemoteChange(++sequence, connection, new Driver(value, enabled, pull));
                    view.Scheduled.Enqueue(change);
                    view.Incoming.Enqueue(new Arrival(change.Sequence, at));
                    if(view.IncomingPosted) continue;
                    view.IncomingPosted = true;
                    post.Add(view);
                }
            }
            // The driver may hold its own clock lock; a receiving machine
            // schedules the changes on its clock at its next synchronization point.
            foreach(var view in post)
            {
                view.Machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => ScheduleIncoming(view));
            }
            Publish(own);
        }

        // Puts the changes that arrived since the last synchronization point on
        // the receiving machine's clock. One clock entry for each machine fires
        // at the earliest due change: a fast signal keeps thousands of changes
        // in flight, and a clock entry for each would slow every operation of
        // that machine's clock source.
        private void ScheduleIncoming(MachineView view)
        {
            var now = view.Machine.ClockSource.CurrentValue;
            lock(sync)
            {
                if(disposed) return;
                view.IncomingPosted = false;
                while(view.Incoming.Count != 0)
                {
                    var arrival = view.Incoming.Dequeue();
                    var due = arrival.At.Ticks;
                    if(arrival.At <= now)
                    {
                        due = now.Ticks + 1;
                        if(lateDeliveries++ == 0)
                        {
                            this.Log(LogLevel.Warning, "A machine reached {0} past a delayed wire change; raise the latency above two quanta", now - arrival.At);
                        }
                    }
                    view.Due.Add((due, arrival.Sequence));
                }
            }
            Arm(view);
        }

        // (Re)arms the machine's clock entry for its earliest due change. Under
        // the clock lock, then the wire lock, as deliveries take them.
        private void Arm(MachineView view)
        {
            var clock = view.Machine.ClockSource;
            clock.ExecuteInLock(() =>
            {
                ulong due;
                lock(sync)
                {
                    if(disposed || view.Due.Count == 0 || (view.TimerArmed && view.TimerDue <= view.Due.Min.Ticks)) return;
                    due = view.Due.Min.Ticks;
                    if(view.TimerArmed) clock.TryRemoveClockEntry(view.Timer);
                    view.TimerArmed = true;
                    view.TimerDue = due;
                }
                var now = clock.CurrentValue.Ticks;
                clock.AddClockEntry(new ClockEntry(due > now ? due - now : 1, TimeInterval.TicksPerSecond, view.Timer, this,
                    "delayed wire", workMode: WorkMode.OneShot));
            });
        }

        // Runs on the receiving machine's clock. A due change applies with every
        // change the drivers made before it.
        private void OnTimer(MachineView view)
        {
            var clock = view.Machine.ClockSource;
            var now = clock.CurrentValue.Ticks;
            ulong last = 0;
            var any = false;
            lock(sync)
            {
                clock.TryRemoveClockEntry(view.Timer);
                view.TimerArmed = false;
                while(view.Due.Count != 0 && view.Due.Min.Ticks <= now)
                {
                    var next = view.Due.Min;
                    view.Due.Remove(next);
                    if(next.Sequence >= last) last = next.Sequence;
                    any = true;
                }
            }
            if(any) Apply(view, last);
            Arm(view);
        }

        // Runs on the receiving machine's clock. Applies every change up to
        // the given one in the order the drivers made them.
        private void Apply(MachineView view, ulong sequence)
        {
            lock(sync)
            {
                if(disposed) return;
                while(view.Scheduled.Count != 0 && view.Scheduled.Peek().Sequence <= sequence)
                {
                    var change = view.Scheduled.Dequeue();
                    view.Remote[change.Connection] = change.Driver;
                    Refresh(view);
                }
            }
            Publish(view);
        }

        private void Refresh(MachineView view)
        {
            var level = Resolve(view);
            if(level == view.Level) return;
            view.Level = level;
            foreach(var receiver in connections.Where(c => c.Machine == view.Machine)) view.Pending.Enqueue(new Delivery(receiver, level));
        }

        private bool Resolve(MachineView view)
        {
            var low = false;
            var high = false;
            var pullUps = false;
            var pullDowns = false;
            foreach(var connection in connections)
            {
                var driver = connection.Machine == view.Machine
                    ? new Driver(connection.Level, connection.Enabled, connection.Pull)
                    : view.Remote.TryGetValue(connection, out var remote) ? remote : Driver.Released;
                pullUps |= driver.Pull == true;
                pullDowns |= driver.Pull == false;
                if(!driver.Enabled) continue;
                high |= driver.Level;
                low |= !driver.Level;
            }
            if(low && high && !view.Contended)
            {
                view.Contended = true;
                this.Log(LogLevel.Warning, "Opposing active drivers on a delayed digital wire; resolving low");
            }
            else if(!(low && high)) view.Contended = false;
            return !low && (high || pullUp || (pullUps && (!pullDowns || view.Level)));
        }

        // Receivers may change their own drive in response; deliver without
        // the wire lock and let an outer delivery on this view drain the queue.
        private void Publish(MachineView view)
        {
            lock(sync)
            {
                if(view.Publishing || disposed) return;
                view.Publishing = true;
            }
            try
            {
                while(true)
                {
                    Delivery delivery;
                    lock(sync)
                    {
                        if(view.Pending.Count == 0 || disposed)
                        {
                            view.Publishing = false;
                            return;
                        }
                        delivery = view.Pending.Dequeue();
                    }
                    delivery.Receiver.Endpoint.OnGPIO(delivery.Receiver.Pin, delivery.Level);
                }
            }
            catch
            {
                lock(sync) view.Publishing = false;
                throw;
            }
        }

        private sealed class Connection
        {
            public Connection(IThreeStateGPIO endpoint, int pin, IMachine machine, bool level, bool enabled)
            {
                Endpoint = endpoint;
                Pin = pin;
                Machine = machine;
                Level = level;
                Enabled = enabled;
            }

            public readonly IThreeStateGPIO Endpoint;
            public readonly int Pin;
            public readonly IMachine Machine;
            public bool Level;
            public bool Enabled;
            public bool? Pull;
            public Action<int, bool, bool> Handler;
            public Action<int, bool?> PullHandler;
        }

        private sealed class MachineView
        {
            public MachineView(IMachine machine)
            {
                Machine = machine;
            }

            public readonly IMachine Machine;
            public readonly Dictionary<Connection, Driver> Remote = new Dictionary<Connection, Driver>();
            public readonly Queue<RemoteChange> Scheduled = new Queue<RemoteChange>();
            // Changes not yet on this machine's clock, and those on it by due tick.
            public readonly Queue<Arrival> Incoming = new Queue<Arrival>();
            public readonly SortedSet<(ulong Ticks, ulong Sequence)> Due = new SortedSet<(ulong Ticks, ulong Sequence)>();
            public bool IncomingPosted;
            public Action Timer;
            public bool TimerArmed;
            public ulong TimerDue;
            public readonly Queue<Delivery> Pending = new Queue<Delivery>();
            public bool Level;
            public bool Contended;
            public bool Publishing;
        }

        private readonly struct Driver
        {
            public Driver(bool level, bool enabled, bool? pull)
            {
                Level = level;
                Enabled = enabled;
                Pull = pull;
            }

            public static Driver Released => new Driver(false, false, null);

            public readonly bool Level;
            public readonly bool Enabled;
            public readonly bool? Pull;
        }

        private readonly struct RemoteChange
        {
            public RemoteChange(ulong sequence, Connection connection, Driver driver)
            {
                Sequence = sequence;
                Connection = connection;
                Driver = driver;
            }

            public readonly ulong Sequence;
            public readonly Connection Connection;
            public readonly Driver Driver;
        }

        private readonly struct Arrival
        {
            public Arrival(ulong sequence, TimeInterval at)
            {
                Sequence = sequence;
                At = at;
            }

            public readonly ulong Sequence;
            public readonly TimeInterval At;
        }

        private readonly struct Delivery
        {
            public Delivery(Connection receiver, bool level)
            {
                Receiver = receiver;
                Level = level;
            }

            public readonly Connection Receiver;
            public readonly bool Level;
        }

        private readonly object sync = new object();
        private readonly bool pullUp;
        private readonly TimeInterval latency;
        private readonly List<Connection> connections = new List<Connection>();
        private readonly Dictionary<IMachine, MachineView> views = new Dictionary<IMachine, MachineView>();
        private ulong sequence;
        private ulong lateDeliveries;
        private bool disposed;
    }
}
