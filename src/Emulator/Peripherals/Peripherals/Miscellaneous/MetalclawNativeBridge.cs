//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.I2C;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Bridges a DUT's buses to Metalclaw running natively (MicroPython's unix
    // port with the metalclaw module) over a Unix stream socket the native
    // process listens on: one JSON object per line each way, each request
    // answered before the DUT's clock moves on, so the DUT's virtual time
    // alone decides every outcome. Every request carries that time in
    // nanoseconds (t_ns).
    //
    // The connection opens with a hello naming each bridged bus by the DUT
    // pins it is wired to:
    //   {"op":"hello","version":1,"i2c":[{"bus":0,"sda":4,"scl":5}],"uart":[],"spi":[]} -> {"version":1}
    // As a target on a DelayedI2CBus the bridge forwards START (with the
    // 7-bit address and direction), each written byte, each read and STOP,
    // and answers with the peer's ACK or NACK, or with its data byte; the DUT
    // holds SCL until the answer arrives, as for any target on that bus.
    //   {"op":"i2c","bus":B,"ev":"start","addr":A,"read":R,"repeated":S,"t_ns":T} -> {"ack":true}
    //   {"op":"i2c","bus":B,"ev":"write","data":D,"t_ns":T}                      -> {"ack":true}
    //   {"op":"i2c","bus":B,"ev":"read","t_ns":T}                                -> {"data":D}
    //   {"op":"i2c","bus":B,"ev":"stop","t_ns":T}                                -> {}
    // A tick every TickMicroseconds of virtual time ({"op":"tick","t_ns":T}
    // -> {}) keeps the peer's clock moving while the buses are idle; 0 turns
    // it off. Bridges naming the same socket share one connection, so a DUT
    // with several buses has one bridge per bus, one of them ticking.
    // Any reply may carry "actions", each at its own virtual time no earlier
    // than the request's, applied then: {"t_ns":T,"op":"gpio","pin":N,
    // "level":0|1|"z"} drives or releases a DUT pin's input through GPIO.
    public sealed class MetalclawNativeBridge : IDelayedI2CBusEndpoint
    {
        // socketPath defaults to $METALCLAW_BRIDGE, the path the native process
        // listens on.
        public MetalclawNativeBridge(IMachine machine, string socketPath = "", int bus = 0, ulong tickMicroseconds = 0,
            RP2_GPIOBank0 gpio = null, int replyTimeoutMilliseconds = 60000)
        {
            this.machine = machine;
            this.busIndex = bus;
            this.gpio = gpio;
            if(string.IsNullOrEmpty(socketPath)) socketPath = Environment.GetEnvironmentVariable("METALCLAW_BRIDGE");
            if(string.IsNullOrEmpty(socketPath)) throw new Exceptions.ConstructionException("Set socketPath or METALCLAW_BRIDGE to the native peer's socket");
            connection = Connection.For(socketPath, replyTimeoutMilliseconds);
            if(tickMicroseconds != 0)
            {
                tick = new LimitTimer(machine.ClockSource, 1000000, this, "native tick", limit: tickMicroseconds,
                    workMode: WorkMode.Periodic, enabled: true, eventEnabled: true);
                tick.LimitReached += () => Request(new JsonObject { ["op"] = "tick" });
            }
        }

        public void Reset()
        {
        }

        public void AttachToBus(DelayedI2CBus bus, int sdaPin, int sclPin)
        {
            if(this.bus != null) throw new Exceptions.RecoverableException("A native bridge serves one bus; add another bridge for each further bus");
            this.bus = bus;
            connection.Register("i2c", new JsonObject { ["bus"] = busIndex, ["sda"] = sdaPin, ["scl"] = sclPin });
        }

        public void OnBusEvent(I2CBusEvent busEvent)
        {
            var request = new JsonObject { ["op"] = "i2c", ["bus"] = busIndex };
            switch(busEvent.Type)
            {
            case I2CBusEventType.Start:
                request["ev"] = "start";
                request["addr"] = busEvent.Address;
                request["read"] = busEvent.IsRead;
                request["repeated"] = busEvent.Repeated;
                break;
            case I2CBusEventType.Write:
                request["ev"] = "write";
                request["data"] = busEvent.Data;
                break;
            case I2CBusEventType.Read:
                request["ev"] = "read";
                break;
            case I2CBusEventType.Stop:
                request["ev"] = "stop";
                break;
            default:
                return;
            }
            var reply = Request(request);
            I2CBusEvent answer;
            switch(busEvent.Type)
            {
            case I2CBusEventType.Start:
            case I2CBusEventType.Write:
                answer = I2CBusEvent.Acknowledge(busEvent.Transfer, reply?["ack"]?.GetValue<bool>() ?? false);
                break;
            case I2CBusEventType.Read:
                answer = I2CBusEvent.Reply(busEvent.Transfer, (byte)(reply?["data"]?.GetValue<int>() ?? 0xff));
                break;
            default:
                return;
            }
            bus?.Send(this, answer, busEvent.Sender);
        }

        // Requests sent and answered since the bridge was created.
        public ulong Requests => Interlocked.Read(ref requests);

        private JsonObject Request(JsonObject request)
        {
            var now = machine.ClockSource.CurrentValue;
            request["t_ns"] = (long)(now.TotalMicroseconds * 1000);
            Interlocked.Increment(ref requests);
            var reply = connection.Exchange(request, out var error);
            if(error != null && errors++ == 0) this.Log(LogLevel.Error, "Native peer: {0}; answering as an absent target", error);
            if(reply?["actions"] is JsonArray actions)
            {
                foreach(var action in actions)
                {
                    if(action is JsonObject entry) Schedule(entry, now);
                }
            }
            return reply;
        }

        // Applies an action at its virtual time on the DUT machine's clock,
        // or at once if that time has come.
        private void Schedule(JsonObject action, TimeInterval now)
        {
            var at = TimeInterval.FromMicroseconds((ulong)Math.Max(0, (action["t_ns"]?.GetValue<long>() ?? 0) / 1000));
            machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
            {
                var current = machine.ClockSource.CurrentValue;
                if(at <= current)
                {
                    Apply(action);
                    return;
                }
                Action handler = null;
                handler = () =>
                {
                    machine.ClockSource.TryRemoveClockEntry(handler);
                    Apply(action);
                };
                machine.ClockSource.AddClockEntry(new ClockEntry((at - current).Ticks, TimeInterval.TicksPerSecond, handler, this,
                    "native action", workMode: WorkMode.OneShot));
            });
        }

        private void Apply(JsonObject action)
        {
            var op = action["op"]?.GetValue<string>();
            if(op == "gpio" && gpio != null)
            {
                var pin = action["pin"]?.GetValue<int>() ?? -1;
                var level = action["level"];
                if(level is JsonValue value && value.TryGetValue<string>(out var text) && text == "z") gpio.ReleaseInput(pin);
                else gpio.OnGPIO(pin, (level?.GetValue<int>() ?? 0) != 0);
                return;
            }
            if(unsupported++ == 0) this.Log(LogLevel.Warning, "Native peer action {0} is not supported here", action.ToJsonString());
        }

        private readonly IMachine machine;
        private readonly int busIndex;
        private readonly RP2_GPIOBank0 gpio;
        private ulong unsupported;
        private readonly Connection connection;
        private readonly LimitTimer tick;
        private DelayedI2CBus bus;
        private ulong requests;
        private ulong errors;

        // One stream to the native peer per socket path, shared by its bridges.
        private sealed class Connection
        {
            public static Connection For(string path, int timeoutMilliseconds)
            {
                lock(Shared)
                {
                    if(!Shared.TryGetValue(path, out var connection))
                    {
                        connection = new Connection(path, timeoutMilliseconds);
                        Shared.Add(path, connection);
                    }
                    return connection;
                }
            }

            // A bus for the hello; registered as the platform wires the bridges.
            public void Register(string kind, JsonObject entry)
            {
                lock(sync) buses[kind].Add(entry);
            }

            public JsonObject Exchange(JsonObject request, out string error)
            {
                error = null;
                lock(sync)
                {
                    try
                    {
                        if(writer == null)
                        {
                            Open();
                            var hello = new JsonObject { ["op"] = "hello", ["version"] = 1 };
                            foreach(var kind in buses) hello[kind.Key] = JsonNode.Parse(kind.Value.ToJsonString());
                            writer.Write(hello.ToJsonString() + "\n");
                            writer.Flush();
                            if(reader.ReadLine() == null) throw new IOException("the peer closed the connection after the hello");
                        }
                        writer.Write(request.ToJsonString() + "\n");
                        writer.Flush();
                        var line = reader.ReadLine();
                        if(line == null) throw new IOException("the peer closed the connection");
                        return JsonNode.Parse(line) as JsonObject;
                    }
                    catch(Exception e) when(e is IOException || e is SocketException || e is System.Text.Json.JsonException)
                    {
                        error = e.Message;
                        Close();
                        return null;
                    }
                }
            }

            private Connection(string path, int timeoutMilliseconds)
            {
                this.path = path;
                this.timeoutMilliseconds = timeoutMilliseconds;
            }

            // The peer may start after the DUT; wait for it up to the timeout.
            private void Open()
            {
                var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
                while(true)
                {
                    var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try
                    {
                        socket.Connect(new UnixDomainSocketEndPoint(path));
                        socket.ReceiveTimeout = timeoutMilliseconds;
                        var stream = new NetworkStream(socket, ownsSocket: true);
                        reader = new StreamReader(stream, new UTF8Encoding(false));
                        writer = new StreamWriter(stream, new UTF8Encoding(false));
                        return;
                    }
                    catch(SocketException) when(DateTime.UtcNow < deadline)
                    {
                        socket.Dispose();
                        Thread.Sleep(100);
                    }
                }
            }

            private void Close()
            {
                writer?.Dispose();
                reader?.Dispose();
                writer = null;
                reader = null;
            }

            private StreamReader reader;
            private StreamWriter writer;
            private readonly string path;
            private readonly int timeoutMilliseconds;
            private readonly object sync = new object();
            private readonly Dictionary<string, JsonArray> buses = new Dictionary<string, JsonArray>
            {
                ["i2c"] = new JsonArray(), ["uart"] = new JsonArray(), ["spi"] = new JsonArray()
            };

            private static readonly Dictionary<string, Connection> Shared = new Dictionary<string, Connection>();
        }
    }
}
