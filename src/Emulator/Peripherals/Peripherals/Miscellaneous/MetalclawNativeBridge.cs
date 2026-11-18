//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    // it off. A ticking bridge with the DUT's GPIO bank also reports where
    // the DUT's I2C, SPI and UART blocks are routed, from GPIO_CTRL.FUNCSEL,
    // first and whenever it changes, so a part that speaks first (a GPS on a
    // UART) is found on its pins:
    //   {"op":"route","i2c":[{"bus":0,"sda":4,"scl":5}],"spi":[{"bus":0,
    //    "sck":18,"mosi":19,"miso":16}],"uart":[{"port":1,"tx":4,"rx":5}],"t_ns":T} -> {}
    // Bridges naming the same socket share one connection, so a DUT with
    // several buses has one bridge per bus, one of them ticking. An action
    // in any reply goes to the bridge on that connection that owns its UART
    // port or the GPIO bank.
    // With a UART the bridge forwards each byte the DUT transmits,
    //   {"op":"uart","port":P,"data":[D],"t_ns":T} -> {}
    // and is named in the hello by its port and the DUT's own TX and RX pins.
    // With an SPI master the bridge answers each frame as it starts,
    //   {"op":"spi","bus":B,"mosi":W,"bits":N,"t_ns":T} -> {"miso":W}
    // and is named in the hello by the DUT's SCK, MOSI, MISO and CS pins.
    // With the DUT's GPIO bank given, each frame and each I2C START also
    // names the pins the block is routed to now, as "pins":{"sck":N,
    // "mosi":N,"miso":N} or "pins":{"sda":N,"scl":N}, and each UART byte as
    // "pins":{"tx":N,"rx":N}, read from
    // GPIO_CTRL.FUNCSEL, since a program may route a block to other pins
    // than the hello's.
    // Given pinSda and pinScl instead of a bus, the bridge is an I2C target on
    // those DUT pins, for a bus a program drives itself (a PIO I2C master):
    // it follows the DUT's own drive on them, released lines high through
    // the pads' pull-ups, sends the same i2c requests as on a bus, and drives
    // SDA low through GPIO for an ACK and for the 0 bits it sends; it never
    // stretches SCL, since each answer comes before the DUT's clock moves.
    // Watched pins (an SPI bridge's CS, and any a {"op":"watch","pin":N} or
    // {"op":"watch","pins":[...]} action adds) report every change of the
    // DUT's own drive on them, never the level the peer's actions put on the
    // line, with "z" when the DUT releases the pin:
    //   {"op":"gpio","pin":N,"level":0|1|"z","t_ns":T} -> {}
    // Any reply may carry "actions", each at its own virtual time no earlier
    // than the request's, applied then; one at the request's own time is in
    // place before the DUT executes another instruction: {"t_ns":T,"op":"gpio","pin":N,
    // "level":0|1|"z"} drives or releases a DUT pin's input through GPIO, and
    // {"t_ns":T,"op":"uart","port":P,"data":D} puts a byte in that UART's
    // receive FIFO (the peer paces bytes at the baud rate), and
    // {"t_ns":T,"op":"spi_clock","bus":B,"mosi":W} clocks one frame into an
    // SPI block the DUT runs as a slave, CSn framing it, for a controller
    // played by the peer. The frame the block shifted out comes back at once
    // as {"op":"spi_clocked","bus":B,"mosi":W,"miso":W|null,"t_ns":T} -> {},
    // null when the block is not an enabled slave or keeps its output off.
    // {"t_ns":T,"op":"join","pins":[A,B]} wires two DUT pins together, as a
    // jumper, until {"t_ns":T,"op":"unjoin","pins":[A,B]}: the DUT's drive
    // on either becomes the other's input at once ("z" releases it), and two
    // I2C blocks whose routed SDA and SCL are joined talk to each other
    // directly, the DUT answering its own target's requests, without
    // requests to the peer.
    public sealed class MetalclawNativeBridge : IDelayedI2CBusEndpoint
    {
        // socketPath defaults to $METALCLAW_BRIDGE, the path the native process
        // listens on.
        public MetalclawNativeBridge(IMachine machine, string socketPath = "", int bus = 0, ulong tickMicroseconds = 0,
            RP2_GPIOBank0 gpio = null, UART.IUART uart = null, int uartTx = -1, int uartRx = -1,
            SPI.RP2_SPI spi = null, int spiSck = -1, int spiMosi = -1, int spiMiso = -1, int spiCs = -1,
            int pinSda = -1, int pinScl = -1, int replyTimeoutMilliseconds = 60000)
        {
            this.machine = machine;
            this.busIndex = bus;
            this.gpio = gpio;
            this.uart = uart;
            this.spi = spi;
            if(string.IsNullOrEmpty(socketPath)) socketPath = Environment.GetEnvironmentVariable("METALCLAW_BRIDGE");
            if(string.IsNullOrEmpty(socketPath)) throw new Exceptions.ConstructionException("Set socketPath or METALCLAW_BRIDGE to the native peer's socket");
            connection = Connection.For(socketPath, replyTimeoutMilliseconds);
            connection.Join(this);
            if(uart != null)
            {
                connection.Register("uart", new JsonObject { ["port"] = bus, ["tx"] = uartTx, ["rx"] = uartRx });
                uart.CharReceived += value =>
                {
                    var request = new JsonObject { ["op"] = "uart", ["port"] = busIndex, ["data"] = new JsonArray(value) };
                    // UART pins come in groups of four that alternate UART0,
                    // UART1, UART1, UART0 (UARTRole).
                    AddRoutedPins(request, UARTFunctions, pin => ((pin + 4) / 8) % 2 == busIndex, UARTRole);
                    Request(request);
                };
            }
            if(spi != null)
            {
                connection.Register("spi", new JsonObject { ["bus"] = bus, ["sck"] = spiSck, ["mosi"] = spiMosi, ["miso"] = spiMiso, ["cs"] = spiCs });
                spi.FrameTarget = (mosi, bits, at) =>
                {
                    var request = new JsonObject { ["op"] = "spi", ["bus"] = busIndex, ["mosi"] = mosi, ["bits"] = bits };
                    AddRoutedPins(request, new[] { SPIFunction }, pin => (pin / 8) % 2 == busIndex,
                        pin => pin % 4 == 0 ? "miso" : pin % 4 == 2 ? "sck" : pin % 4 == 3 ? "mosi" : null);
                    var reply = Request(request, at);
                    return (ushort)(reply?["miso"]?.GetValue<int>() ?? 0xffff);
                };
                if(spiCs >= 0) connection.Watch(spiCs);
            }
            if(pinSda >= 0 && pinScl >= 0)
            {
                if(gpio == null) throw new Exceptions.ConstructionException("A pin-level I2C target needs the DUT's gpio");
                this.pinSda = pinSda;
                this.pinScl = pinScl;
                connection.Register("i2c", new JsonObject { ["bus"] = bus, ["sda"] = pinSda, ["scl"] = pinScl });
                gpio.DriveChanged += OnPinI2CDrive;
            }
            // One bridge per connection reports its watched pins.
            if(gpio != null && connection.ClaimPinReports()) gpio.DriveChanged += OnDriveChanged;
            if(tickMicroseconds != 0)
            {
                tick = new LimitTimer(machine.ClockSource, 1000000, this, "native tick", limit: tickMicroseconds,
                    workMode: WorkMode.Periodic, enabled: true, eventEnabled: true);
                tick.LimitReached += () =>
                {
                    ReportRouting();
                    Request(new JsonObject { ["op"] = "tick" });
                };
            }
        }

        public void Reset()
        {
        }

        // Puts a controller's event from a joined bus on this bridge's bus,
        // to this bus's other blocks, and remembers where its answers go.
        private void Relay(MetalclawNativeBridge origin, I2CBusEvent busEvent)
        {
            lock(relays)
            {
                if(busEvent.Type == I2CBusEventType.Stop) relays.Remove(busEvent.Transfer);
                else relays[busEvent.Transfer] = Tuple.Create(origin, busEvent.Sender);
            }
            bus?.Send(this, busEvent);
        }

        private void ReturnRelayedAnswer(I2CBusEvent answer)
        {
            Tuple<MetalclawNativeBridge, IDelayedI2CBusEndpoint> origin;
            lock(relays)
            {
                if(!relays.TryGetValue(answer.Transfer, out origin)) return;
            }
            origin.Item1.bus?.Send(origin.Item1, answer, origin.Item2);
        }

        // The pins this I2C block is routed to now: SDA on even pins, SCL on odd.
        private List<int> RoutedI2CPins(bool sda)
        {
            var pins = new List<int>();
            if(gpio == null || bus == null) return pins;
            for(var pin = 0; pin < MaximumPins; pin++)
            {
                if(gpio.GetFunction(pin) == I2CFunction && (pin / 2) % 2 == busIndex && (pin % 2 == 0) == sda) pins.Add(pin);
            }
            return pins;
        }

        public void AttachToBus(DelayedI2CBus bus, int sdaPin, int sclPin)
        {
            if(this.bus != null) throw new Exceptions.RecoverableException("A native bridge serves one bus; add another bridge for each further bus");
            this.bus = bus;
            connection.Register("i2c", new JsonObject { ["bus"] = busIndex, ["sda"] = sdaPin, ["scl"] = sclPin });
        }

        public void OnBusEvent(I2CBusEvent busEvent)
        {
            if(busEvent.Type == I2CBusEventType.Acknowledge || busEvent.Type == I2CBusEventType.Data)
            {
                ReturnRelayedAnswer(busEvent);
                return;
            }
            var peer = connection.JoinedI2CPeer(this);
            if(peer != null)
            {
                peer.Relay(this, busEvent);
                return;
            }
            var request = new JsonObject { ["op"] = "i2c", ["bus"] = busIndex };
            switch(busEvent.Type)
            {
            case I2CBusEventType.Start:
                request["ev"] = "start";
                request["addr"] = busEvent.Address;
                request["read"] = busEvent.IsRead;
                request["repeated"] = busEvent.Repeated;
                AddRoutedPins(request, new[] { I2CFunction }, pin => (pin / 2) % 2 == busIndex, pin => pin % 2 == 0 ? "sda" : "scl");
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

        // Pin-level I2C target ----------------------------------------------

        private void OnPinI2CDrive(int pin, bool value, bool enabled)
        {
            if(pin != pinSda && pin != pinScl) return;
            var low = enabled && !value;
            if(pin == pinSda)
            {
                dutSdaLow = low;
                var sda = SdaLevel;
                if(sda == lastSda) return;
                lastSda = sda;
                // Only the controller changes SDA while SCL is high.
                if(lastScl)
                {
                    if(!sda) PinStart();
                    else PinStop();
                }
                return;
            }
            var scl = !low;
            if(scl == lastScl) return;
            lastScl = scl;
            if(scl) PinClockRises();
            else PinClockFalls();
        }

        private bool SdaLevel => !(dutSdaLow || ownSdaLow);

        private void DriveSda(bool levelLow)
        {
            if(ownSdaLow == levelLow) return;
            ownSdaLow = levelLow;
            lastSda = SdaLevel;
            if(levelLow) gpio.OnGPIO(pinSda, false);
            else gpio.ReleaseInput(pinSda);
        }

        private JsonObject PinRequest(string ev)
        {
            return new JsonObject { ["op"] = "i2c", ["bus"] = busIndex, ["ev"] = ev, ["pins"] = new JsonObject { ["sda"] = pinSda, ["scl"] = pinScl } };
        }

        private void PinStart()
        {
            pinRepeated = pinState != PinState.Idle;
            pinState = PinState.Address;
            pinBits = 0;
            pinShift = 0;
            pinAcknowledging = false;
            DriveSda(false);
        }

        private void PinStop()
        {
            DriveSda(false);
            if(pinState == PinState.Idle) return;
            pinState = PinState.Idle;
            Request(PinRequest("stop"));
        }

        private void PinClockRises()
        {
            switch(pinState)
            {
            case PinState.Address:
            case PinState.Write:
                if(pinAcknowledging) return;
                pinShift = (byte)((pinShift << 1) | (SdaLevel ? 1 : 0));
                pinBits++;
                break;
            case PinState.Read:
                if(pinBits == 9) pinControllerAcknowledged = !SdaLevel;
                break;
            }
        }

        private void PinClockFalls()
        {
            switch(pinState)
            {
            case PinState.Address:
            case PinState.Write:
                if(pinAcknowledging)
                {
                    // The ninth clock ends: release the ACK.
                    pinAcknowledging = false;
                    DriveSda(false);
                    pinBits = 0;
                    pinShift = 0;
                    if(pinReadNext)
                    {
                        pinReadNext = false;
                        pinState = PinState.Read;
                        PinSendNextByte();
                    }
                    else pinState = PinState.Write;
                    return;
                }
                if(pinBits < 8) return;
                bool acknowledged;
                if(pinState == PinState.Address)
                {
                    var request = PinRequest("start");
                    request["addr"] = pinShift >> 1;
                    request["read"] = (pinShift & 1) != 0;
                    request["repeated"] = pinRepeated;
                    acknowledged = Request(request)?["ack"]?.GetValue<bool>() ?? false;
                    pinReadNext = acknowledged && (pinShift & 1) != 0;
                }
                else
                {
                    var request = PinRequest("write");
                    request["data"] = pinShift;
                    acknowledged = Request(request)?["ack"]?.GetValue<bool>() ?? false;
                }
                if(!acknowledged)
                {
                    pinState = PinState.Ignore;
                    return;
                }
                pinAcknowledging = true;
                DriveSda(true);
                break;
            case PinState.Read:
                if(pinBits < 8)
                {
                    DriveSda(((pinOut >> (7 - pinBits)) & 1) == 0);
                    pinBits++;
                }
                else if(pinBits == 8)
                {
                    // The controller's ACK or NACK clock.
                    DriveSda(false);
                    pinBits = 9;
                }
                else if(pinControllerAcknowledged) PinSendNextByte();
                else
                {
                    DriveSda(false);
                    pinState = PinState.Ignore;
                }
                break;
            }
        }

        // The next byte to send: its first bit goes out now, on SCL low.
        private void PinSendNextByte()
        {
            pinOut = (byte)(Request(PinRequest("read"))?["data"]?.GetValue<int>() ?? 0xff);
            DriveSda(((pinOut >> 7) & 1) == 0);
            pinBits = 1;
        }

        private enum PinState { Idle, Address, Write, Read, Ignore }

        private string lastRouting;
        private PinState pinState;
        private int pinSda = -1, pinScl = -1, pinBits;
        private byte pinShift, pinOut;
        private bool dutSdaLow, ownSdaLow, lastSda = true, lastScl = true;
        private bool pinRepeated, pinAcknowledging, pinControllerAcknowledged, pinReadNext;

        // Sends the blocks' routing when it differs from the last sent.
        private void ReportRouting()
        {
            if(gpio == null) return;
            var route = new JsonObject
            {
                ["op"] = "route",
                ["i2c"] = Routes(new[] { I2CFunction }, 2, "bus", pin => (pin / 2) % 2, pin => pin % 2 == 0 ? "sda" : "scl"),
                ["spi"] = Routes(new[] { SPIFunction }, 2, "bus", pin => (pin / 8) % 2,
                    pin => pin % 4 == 0 ? "miso" : pin % 4 == 2 ? "sck" : pin % 4 == 3 ? "mosi" : null),
                ["uart"] = Routes(UARTFunctions, 2, "port", pin => ((pin + 4) / 8) % 2, UARTRole),
            };
            var text = route.ToJsonString();
            if(text == lastRouting) return;
            lastRouting = text;
            Request(route);
        }

        // Each routed block of a function with its pins, the first in each role.
        private JsonArray Routes(int[] functions, int blocks, string key, Func<int, int> blockOf, Func<int, string> role)
        {
            var routes = new JsonArray();
            for(var block = 0; block < blocks; block++)
            {
                var entry = new JsonObject { [key] = block };
                for(var pin = 0; pin < MaximumPins; pin++)
                {
                    if(Array.IndexOf(functions, gpio.GetFunction(pin)) < 0 || blockOf(pin) != block) continue;
                    var name = role(pin);
                    if(name != null && !entry.ContainsKey(name)) entry[name] = pin;
                }
                if(entry.Count > 1) routes.Add(entry);
            }
            return routes;
        }

        // The first pin routed to this block in each role, from FUNCSEL.
        private void AddRoutedPins(JsonObject request, int[] functions, Func<int, bool> ofThisBlock, Func<int, string> role)
        {
            if(gpio == null) return;
            var pins = new JsonObject();
            for(var pin = 0; pin < MaximumPins; pin++)
            {
                if(Array.IndexOf(functions, gpio.GetFunction(pin)) < 0 || !ofThisBlock(pin)) continue;
                var name = role(pin);
                if(name != null && !pins.ContainsKey(name)) pins[name] = pin;
            }
            request["pins"] = pins;
        }

        // A change of the DUT's drive on a watched pin, at the time the GPIO
        // bank delivers it.
        private void OnDriveChanged(int pin, bool value, bool enabled)
        {
            foreach(var partner in connection.JoinedTo(pin))
            {
                if(enabled) gpio.OnGPIO(partner, value);
                else gpio.ReleaseInput(partner);
            }
            if(!connection.Watched(pin)) return;
            JsonNode level = enabled ? (JsonNode)(value ? 1 : 0) : "z";
            Request(new JsonObject { ["op"] = "gpio", ["pin"] = pin, ["level"] = level }, SignalTime.Of(machine));
        }

        private JsonObject Request(JsonObject request, TimeInterval? at = null)
        {
            var now = at ?? machine.ClockSource.CurrentValue;
            var requestTime = (long)(now.TotalMicroseconds * 1000);
            request["t_ns"] = requestTime;
            Interlocked.Increment(ref requests);
            var reply = connection.Exchange(request, out var error);
            if(error != null && errors++ == 0) this.Log(LogLevel.Error, "Native peer: {0}; answering as an absent target", error);
            if(reply?["actions"] is JsonArray actions)
            {
                foreach(var action in actions)
                {
                    if(!(action is JsonObject entry)) continue;
                    // Due by the request's own time: in place before the
                    // request returns, so the DUT's next instruction sees it
                    // (a MISO level answering the MOSI edge it just drove).
                    if((entry["t_ns"]?.GetValue<long>() ?? 0) <= requestTime) Apply(entry);
                    else Schedule(entry, now);
                }
            }
            return reply;
        }

        // Applies a later action at its virtual time on the DUT machine's
        // clock, or at the next synchronized point if that time has come.
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

        // An action may ride on any bridge's reply (the ticking bridge gets
        // most); the bridge that owns its port or pin applies it.
        private void Apply(JsonObject action)
        {
            if(TryApply(action) || connection.TryApplyElsewhere(this, action)) return;
            if(unsupported++ == 0) this.Log(LogLevel.Warning, "Native peer action {0} has no bridge to apply it", action.ToJsonString());
        }

        private bool TryApply(JsonObject action)
        {
            var op = action["op"]?.GetValue<string>();
            if(op == "gpio" && gpio != null)
            {
                var pin = action["pin"]?.GetValue<int>() ?? -1;
                var level = action["level"];
                if(level is JsonValue value && value.TryGetValue<string>(out var text) && text == "z") gpio.ReleaseInput(pin);
                else gpio.OnGPIO(pin, (level?.GetValue<int>() ?? 0) != 0);
                return true;
            }
            if(op == "watch")
            {
                if(action["pin"] is JsonValue single) connection.Watch(single.GetValue<int>());
                if(action["pins"] is JsonArray pins)
                {
                    foreach(var entry in pins) connection.Watch(entry.GetValue<int>());
                }
                return true;
            }
            if(op == "join" || op == "unjoin")
            {
                if(!(action["pins"] is JsonArray pair) || pair.Count != 2) return false;
                var a = pair[0].GetValue<int>();
                var b = pair[1].GetValue<int>();
                connection.SetJoined(a, b, op == "join");
                if(gpio == null) return true;
                // A new jumper carries each pin's present drive to the other;
                // a removed one releases the inputs it was driving.
                foreach(var (from, to) in new[] { (a, b), (b, a) })
                {
                    gpio.GetOutputState(from, out var level, out var enabled);
                    if(op == "join" && enabled) gpio.OnGPIO(to, level);
                    else gpio.ReleaseInput(to);
                }
                return true;
            }
            if(op == "spi_clock" && spi != null && (action["bus"]?.GetValue<int>() ?? -1) == busIndex)
            {
                var mosi = action["mosi"]?.GetValue<int>() ?? 0;
                var miso = spi.ClockSlaveFrame((ushort)mosi);
                Request(new JsonObject
                {
                    ["op"] = "spi_clocked", ["bus"] = busIndex, ["mosi"] = mosi,
                    ["miso"] = miso is ushort word ? JsonValue.Create((int)word) : null
                });
                return true;
            }
            if(op == "uart" && uart != null && (action["port"]?.GetValue<int>() ?? -1) == busIndex)
            {
                uart.WriteChar((byte)(action["data"]?.GetValue<int>() ?? 0));
                return true;
            }
            return false;
        }

        private const int SPIFunction = 1;
        // UART's own function (2) puts TX on pin % 4 == 0 and RX on 1; the
        // RP2350's UART auxiliary function (11) puts them on 2 and 3, the
        // pins where function 2 has CTS and RTS.
        private static readonly int[] UARTFunctions = { 2, 11 };

        private string UARTRole(int pin)
        {
            var function = gpio.GetFunction(pin);
            if(function == 2) return pin % 4 == 0 ? "tx" : pin % 4 == 1 ? "rx" : null;
            if(function == 11) return pin % 4 == 2 ? "tx" : pin % 4 == 3 ? "rx" : null;
            return null;
        }
        private const int I2CFunction = 3;
        private const int MaximumPins = 48;

        private readonly IMachine machine;
        private readonly int busIndex;
        private readonly RP2_GPIOBank0 gpio;
        private readonly UART.IUART uart;
        private readonly SPI.RP2_SPI spi;
        private readonly Dictionary<ulong, Tuple<MetalclawNativeBridge, IDelayedI2CBusEndpoint>> relays =
            new Dictionary<ulong, Tuple<MetalclawNativeBridge, IDelayedI2CBusEndpoint>>();
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

            public void Join(MetalclawNativeBridge bridge)
            {
                lock(members) members.Add(bridge);
            }

            public bool TryApplyElsewhere(MetalclawNativeBridge from, JsonObject action)
            {
                MetalclawNativeBridge[] others;
                lock(members) others = members.ToArray();
                foreach(var bridge in others)
                {
                    if(bridge != from && bridge.TryApply(action)) return true;
                }
                return false;
            }

            public void SetJoined(int a, int b, bool joined)
            {
                lock(joins)
                {
                    var key = Tuple.Create(Math.Min(a, b), Math.Max(a, b));
                    if(joined) joins.Add(key);
                    else joins.Remove(key);
                }
            }

            public List<int> JoinedTo(int pin)
            {
                lock(joins)
                {
                    return joins.Where(j => j.Item1 == pin || j.Item2 == pin)
                        .Select(j => j.Item1 == pin ? j.Item2 : j.Item1).ToList();
                }
            }

            // The other I2C bridge whose block's routed SDA and SCL are joined
            // to this one's, if any.
            public MetalclawNativeBridge JoinedI2CPeer(MetalclawNativeBridge from)
            {
                lock(joins)
                {
                    if(joins.Count == 0) return null;
                }
                MetalclawNativeBridge[] others;
                lock(members) others = members.ToArray();
                var sda = from.RoutedI2CPins(true);
                var scl = from.RoutedI2CPins(false);
                if(sda.Count == 0 || scl.Count == 0) return null;
                foreach(var other in others)
                {
                    if(other == from || other.bus == null) continue;
                    if(Linked(sda, other.RoutedI2CPins(true)) && Linked(scl, other.RoutedI2CPins(false))) return other;
                }
                return null;
            }

            private bool Linked(List<int> ours, List<int> theirs) => ours.Any(pin => JoinedTo(pin).Any(theirs.Contains));

            public void Watch(int pin)
            {
                lock(watchedPins) watchedPins.Add(pin);
            }

            public bool Watched(int pin)
            {
                lock(watchedPins) return watchedPins.Contains(pin);
            }

            public bool ClaimPinReports()
            {
                lock(watchedPins)
                {
                    if(pinReportsClaimed) return false;
                    pinReportsClaimed = true;
                    return true;
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
                    // Any failure to reach the peer (a refused or over-long socket
                    // path among them) leaves it absent rather than ending Renode.
                    catch(Exception e) when(!(e is OutOfMemoryException))
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
            private readonly HashSet<int> watchedPins = new HashSet<int>();
            private readonly List<MetalclawNativeBridge> members = new List<MetalclawNativeBridge>();
            private readonly HashSet<Tuple<int, int>> joins = new HashSet<Tuple<int, int>>();
            private bool pinReportsClaimed;
            private readonly Dictionary<string, JsonArray> buses = new Dictionary<string, JsonArray>
            {
                ["i2c"] = new JsonArray(), ["uart"] = new JsonArray(), ["spi"] = new JsonArray()
            };

            private static readonly Dictionary<string, Connection> Shared = new Dictionary<string, Connection>();
        }
    }
}
