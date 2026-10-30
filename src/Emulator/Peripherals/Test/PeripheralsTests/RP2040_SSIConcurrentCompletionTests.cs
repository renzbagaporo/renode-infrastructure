//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Threading;
using System.Threading.Tasks;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Time;
using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_SSIConcurrentCompletionTests
    {
        [TestCase(0, true)] [TestCase(0, false)]
        [TestCase(1, true)] [TestCase(1, false)]
        [TestCase(2, true)] [TestCase(2, false)]
        public void ShouldSerializeBusControlsAndFifoObservationsWithWireCompletion(int operation, bool concurrent)
        {
            using(var machine = new Machine())
            using(var target = new BlockingTarget())
            using(var actorStarted = new ManualResetEventSlim())
            {
                var ssi = new RP2040_SSI(machine, frequency: 1000000);
                var qspi = new RP2040_QSPI(machine, ssi: ssi);
                machine.SystemBus.Register(ssi, new BusPointRegistration(Ssi));
                machine.SystemBus.Register(qspi, new BusPointRegistration(Qspi));
                ssi.Register(target, 0);
                machine.SystemBus.WriteDoubleWord(Ssi, 7u << 16);
                machine.SystemBus.WriteDoubleWord(Ssi + 0x14, 2);
                machine.SystemBus.WriteDoubleWord(Ssi + 0x10, 1);
                machine.SystemBus.WriteDoubleWord(Ssi + 8, 1);
                machine.SystemBus.WriteDoubleWord(Qspi + 0xc, 2u << 8);
                machine.SystemBus.WriteDoubleWord(Ssi + 0x60, 0xaa);
                machine.SystemBus.WriteDoubleWord(Ssi + 0x60, 0xbb);
                Task clock = null;
                Task<uint> actor = null;
                var completedWhileInFlight = false;
                try
                {
                    clock = Task.Run(() => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(16)));
                    Assert.IsTrue(target.TransmitEntered.Wait(TimeSpan.FromSeconds(3)), "Timer must enter real SPI Transmit before the competing operation");
                    if(!concurrent)
                    {
                        target.AllowTransmitReturn.Set();
                        Assert.IsTrue(clock.Wait(TimeSpan.FromSeconds(3)));
                    }
                    actor = Task.Run(() =>
                    {
                        actorStarted.Set();
                        if(operation == 0)
                        {
                            machine.SystemBus.WriteDoubleWord(Qspi + 0xc, 3u << 8);
                            return uint.MaxValue;
                        }
                        return machine.SystemBus.ReadDoubleWord(Ssi + (operation == 1 ? 0x24ul : 0x20ul));
                    });
                    Assert.IsTrue(actorStarted.Wait(TimeSpan.FromSeconds(3)));
                    if(concurrent) completedWhileInFlight = actor.Wait(TimeSpan.FromMilliseconds(200));
                }
                finally
                {
                    target.AllowTransmitReturn.Set();
                    if(clock != null) Assert.IsTrue(clock.Wait(TimeSpan.FromSeconds(3)), "Clock callback deadlocked");
                    if(actor != null) Assert.IsTrue(actor.Wait(TimeSpan.FromSeconds(3)), "Concurrent bus operation deadlocked");
                }
                TestContext.WriteLine($"operation={operation} concurrent={concurrent} completedBeforeWireCommit={completedWhileInFlight} finishOverlappedTransmit={target.FinishOverlappedTransmit} result={actor.Result}");
                Assert.Multiple(() =>
                {
                    Assert.IsFalse(completedWhileInFlight, "A bus operation must serialize with the already executing timer callback");
                    Assert.IsFalse(target.FinishOverlappedTransmit, "Chip select deassertion must not call FinishTransmission concurrently with Transmit");
                    if(operation == 1) Assert.AreEqual(1u, actor.Result, "RXFLR observes the committed first frame");
                    if(operation == 2) Assert.AreEqual(0u, actor.Result, "TXFLR observes the second frame dequeued after the first commit");
                    Assert.AreEqual(1, target.CommittedBytes);
                });
            }
        }

        private sealed class BlockingTarget : ISPIPeripheral, IDisposable
        {
            public byte Transmit(byte value)
            {
                Interlocked.Exchange(ref insideTransmit, 1);
                TransmitEntered.Set();
                if(!AllowTransmitReturn.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled SPI Transmit was not released");
                Interlocked.Increment(ref committedBytes);
                Interlocked.Exchange(ref insideTransmit, 0);
                return value;
            }
            public void FinishTransmission()
            {
                if(Volatile.Read(ref insideTransmit) != 0) Interlocked.Exchange(ref finishOverlapped, 1);
            }
            public void Reset() { }
            public void Dispose() { TransmitEntered.Dispose(); AllowTransmitReturn.Dispose(); }
            public bool FinishOverlappedTransmit => Volatile.Read(ref finishOverlapped) != 0;
            public int CommittedBytes => Volatile.Read(ref committedBytes);
            public readonly ManualResetEventSlim TransmitEntered = new ManualResetEventSlim();
            public readonly ManualResetEventSlim AllowTransmitReturn = new ManualResetEventSlim();
            private int insideTransmit;
            private int finishOverlapped;
            private int committedBytes;
        }
        private const ulong Ssi = 0x18000000;
        private const ulong Qspi = 0x40018000;
    }
}
