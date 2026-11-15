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
            var changes = new List<Tuple<MachineView, RemoteChange, TimeInterval>>();
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
                    changes.Add(Tuple.Create(view, change, at));
                }
            }
            // The driver may hold its own clock lock; a receiving machine
            // schedules the change on its clock at its next synchronization point.
            foreach(var change in changes)
            {
                var view = change.Item1;
                var remote = change.Item2;
                var at = change.Item3;
                view.Machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => Schedule(view, remote, at));
            }
            Publish(own);
        }

        private void Schedule(MachineView view, RemoteChange change, TimeInterval at)
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
                        this.Log(LogLevel.Warning, "A machine reached {0} past a delayed wire change; raise the latency above two quanta", now - at);
                    }
                }
            }
            Action handler = null;
            handler = () =>
            {
                Apply(view, change.Sequence);
                view.Machine.ClockSource.TryRemoveClockEntry(handler);
            };
            view.Machine.ClockSource.AddClockEntry(new ClockEntry(ticks, TimeInterval.TicksPerSecond, handler, this,
                "delayed wire", workMode: WorkMode.OneShot));
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
