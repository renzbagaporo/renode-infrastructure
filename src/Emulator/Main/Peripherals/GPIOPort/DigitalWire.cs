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

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    public static class DigitalWireExtensions
    {
        public static void CreateDigitalWire(this Emulation emulation, string name, bool pullUp = true)
        {
            emulation.ExternalsManager.AddExternal(new DigitalWire(pullUp), name);
        }
    }

    // A shared digital net with explicit strong drivers and a configurable weak
    // bias. Opposing strong drivers are diagnosed; their undefined electrical
    // voltage is represented by a deterministic low level in this model.
    public sealed class DigitalWire : IExternal, IDisposable
    {
        public DigitalWire(bool pullUp = true)
        {
            this.pullUp = pullUp;
            level = pullUp;
        }

        // Configure the topology before starting the emulation. Each endpoint
        // receives the resolved input independently from its output latch/OE.
        public void Connect(IThreeStateGPIO endpoint, int pin)
        {
            endpoint.GetOutputState(pin, out var initialLevel, out var initialEnable);
            var connection = new Connection(endpoint, pin, initialLevel, initialEnable);
            connection.Handler = (number, value, enabled) =>
            {
                if(number == pin)
                {
                    UpdateDriver(connection, value, enabled);
                }
            };
            lock(sync)
            {
                if(disposed) throw new ObjectDisposedException(nameof(DigitalWire));
                if(connections.Any(c => ReferenceEquals(c.Endpoint, endpoint) && c.Pin == pin))
                {
                    throw new RecoverableException("Pin is already connected to this digital wire");
                }
                connections.Add(connection);
                topology = connections.ToArray();
                Resolve();
                pending.Enqueue(new Notification(new[] { connection }, level, false));
            }
            endpoint.DriveChanged += connection.Handler;
            // Refresh after subscribing so a setup-time transition is not lost.
            endpoint.GetOutputState(pin, out initialLevel, out initialEnable);
            UpdateDriver(connection, initialLevel, initialEnable);
        }

        public void Dispose()
        {
            Connection[] oldConnections;
            lock(sync)
            {
                if(disposed) return;
                disposed = true;
                oldConnections = topology;
                topology = Array.Empty<Connection>();
                connections.Clear();
                pending.Clear();
            }
            foreach(var connection in oldConnections)
            {
                connection.Endpoint.DriveChanged -= connection.Handler;
            }
        }

        public bool Level { get { lock(sync) return level; } }

        public bool IsContended { get { lock(sync) return contended; } }

        public ulong ContentionCount { get { lock(sync) return contentionCount; } }

        private void UpdateDriver(Connection connection, bool value, bool enabled)
        {
            lock(sync)
            {
                if(disposed) return;
                connection.Level = value;
                connection.Enabled = enabled;
                Resolve();
            }
            Publish();
        }

        private void Resolve()
        {
            var low = false;
            var high = false;
            foreach(var connection in connections)
            {
                if(connection.Enabled)
                {
                    high |= connection.Level;
                    low |= !connection.Level;
                }
            }
            var nextContention = low && high;
            var newContention = nextContention && !contended;
            if(newContention) contentionCount++;
            contended = nextContention;
            var next = !low && (high || pullUp);
            if(next != level || newContention)
            {
                var receivers = next == level ? Array.Empty<Connection>() : topology;
                level = next;
                pending.Enqueue(new Notification(receivers, level, newContention));
            }
        }

        private void Publish()
        {
            lock(sync)
            {
                if(publishing || disposed) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    Notification notification;
                    lock(sync)
                    {
                        if(pending.Count == 0 || disposed)
                        {
                            publishing = false;
                            return;
                        }
                        notification = pending.Dequeue();
                    }
                    // GPIO receivers may change their drive in response. Never
                    // invoke them with the wire's state monitor held.
                    foreach(var receiver in notification.Receivers)
                    {
                        receiver.Endpoint.OnGPIO(receiver.Pin, notification.Level);
                    }
                    if(notification.Contention)
                    {
                        this.Log(LogLevel.Warning, "Opposing active drivers on a digital wire; resolving low");
                    }
                }
            }
            catch
            {
                lock(sync) publishing = false;
                throw;
            }
        }

        private sealed class Connection
        {
            public Connection(IThreeStateGPIO endpoint, int pin, bool level, bool enabled)
            {
                Endpoint = endpoint;
                Pin = pin;
                Level = level;
                Enabled = enabled;
            }

            public readonly IThreeStateGPIO Endpoint;
            public readonly int Pin;
            public bool Level;
            public bool Enabled;
            public Action<int, bool, bool> Handler;
        }

        private readonly struct Notification
        {
            public Notification(Connection[] receivers, bool level, bool contention)
            {
                Receivers = receivers;
                Level = level;
                Contention = contention;
            }

            public readonly Connection[] Receivers;
            public readonly bool Level;
            public readonly bool Contention;
        }

        private readonly object sync = new object();
        private readonly bool pullUp;
        private readonly List<Connection> connections = new List<Connection>();
        private readonly Queue<Notification> pending = new Queue<Notification>();
        private Connection[] topology = Array.Empty<Connection>();
        private bool level;
        private bool contended;
        private ulong contentionCount;
        private bool publishing;
        private bool disposed;
    }
}
