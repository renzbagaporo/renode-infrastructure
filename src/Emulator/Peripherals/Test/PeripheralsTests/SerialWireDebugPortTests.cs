//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Antmicro.Renode.Peripherals.CPU;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class SerialWireDebugPortTests
    {
        [SetUp]
        public void SetUp() { target = new TestTarget(); }

        [Test]
        public void ShouldIgnoreRequestsUntilDormantActivationAndLineReset()
        {
            Send(0xa5, 8);
            Assert.IsFalse(IsDriving);
            Assert.AreEqual(0, target.RequestCount);
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
            Assert.AreEqual(1, target.RequestCount);
        }

        [Test]
        public void ShouldDriveAckDataAndParityOnlyAfterTurnaround()
        {
            Wake();
            Send(0xa5, 8);
            Assert.IsFalse(IsDriving);
            Cycle(null);
            Assert.IsTrue(IsDriving);
            Assert.AreEqual(1, Receive(3));
            Assert.AreEqual(0x4c013477, Receive(32));
            Assert.AreEqual(Parity(0x4c013477), Cycle(null));
            Assert.IsFalse(IsDriving);
            Cycle(null);
            Assert.IsFalse(IsDriving);
        }

        [TestCase(0xa7)] // request parity
        [TestCase(0xe5)] // stop
        [TestCase(0x25)] // park
        public void ShouldLockOutMalformedRequestsAndRecoverAfterLineReset(int header)
        {
            Wake();
            Send((uint)header, 8);
            Send(0xa5, 8);
            Assert.AreEqual(0, target.RequestCount);
            Assert.IsFalse(IsDriving);
            LineReset();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [Test]
        public void ShouldRejectInvalidWriteParityWithoutCommittingData()
        {
            Wake();
            Read(0xa5);
            Write(0x81, 0x12345678, !Parity(0x12345678));
            Assert.AreEqual(0, target.WriteCount);
            Assert.AreEqual(1, target.ParityErrors);
            Write(0x81, 0xabcdef00, Parity(0xabcdef00));
            Assert.AreEqual(1, target.WriteCount);
            Assert.AreEqual(0xabcdef00, target.LastWrite);
        }

        [Test]
        public void ShouldRecognizeTheArchitecturalSWDToDormantSequence()
        {
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
            for(var i = 0; i < 56; i++) Cycle(true);
            Send(0xe3bc, 16);
            LineReset();
            Send(0xa5, 8);
            Assert.IsFalse(IsDriving);
            Assert.AreEqual(1, target.RequestCount);
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [Test]
        public void ShouldAcceptOnlyIdentificationAfterLineReset()
        {
            Wake();
            Send(0x81, 8);
            Assert.AreEqual(0, target.RequestCount);
            Assert.IsFalse(IsDriving);
            LineReset();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [TestCase(0x01002927u, true)]
        [TestCase(0x11002927u, false)]
        public void ShouldKeepTargetSelectionResponseUndrivenAndSelectByIdentity(uint identity, bool selected)
        {
            Wake();
            Send(0x99, 8);
            for(var i = 0; i < 5; i++)
            {
                Assert.IsFalse(IsDriving);
                Cycle(null);
            }
            Send(identity, 32);
            Cycle(Parity(identity));
            Send(0, 2);
            if(selected) Assert.AreEqual(0x4c013477, Read(0xa5));
            else
            {
                Send(0xa5, 8);
                for(var i = 0; i < 40; i++) { Assert.IsFalse(IsDriving); Cycle(null); }
                Assert.AreEqual(0, target.RequestCount);
            }
        }

        [Test]
        public void ShouldTreatTargetSelectionParityFailureAsProtocolError()
        {
            Wake();
            Send(0x99, 8);
            for(var i = 0; i < 5; i++) Cycle(null);
            Send(0x01002927, 32);
            Cycle(!Parity(0x01002927));
            Send(0xa5, 8);
            Assert.AreEqual(0, target.RequestCount);
            Assert.AreEqual(0, target.ParityErrors);
            Assert.IsFalse(IsDriving);
            LineReset();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [TestCase(2)]
        [TestCase(4)]
        public void ShouldReleaseTheLineAfterWaitAndFaultWithoutTransferringData(int ack)
        {
            Wake();
            target.Ack = ack;
            Send(0xa5, 8);
            Cycle(null);
            Assert.AreEqual(ack, Receive(3));
            Assert.IsFalse(IsDriving);
            Cycle(null);
            target.Ack = 1;
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [Test]
        public void ShouldRequireTheCompleteAlertAndCorrectActivationCode()
        {
            SendAlert();
            Send(0, 4);
            Send(0x19, 8);
            LineReset();
            Send(0xa5, 8);
            Assert.AreEqual(0, target.RequestCount);
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [Test]
        public void ShouldReturnToDormantWhenResetIsAsserted()
        {
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
            target.OnGPIO(2, false);
            Assert.IsFalse(IsDriving);
            target.OnGPIO(2, true);
            LineReset();
            Send(0xa5, 8);
            Assert.AreEqual(1, target.RequestCount);
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [Test]
        public void ShouldPreserveThePhysicalResetPinAcrossPeripheralReset()
        {
            Wake();
            target.OnGPIO(2, false);
            target.Reset();
            Wake();
            Send(0xa5, 8);
            Assert.AreEqual(0, target.RequestCount);
            Assert.IsFalse(IsDriving);
            target.OnGPIO(2, true);
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldDiscardQueuedOldTransfersWhenResetRacesABlockedBackend(bool hardwareReset)
        {
            Wake();
            using(var entered = new ManualResetEventSlim())
            using(var release = new ManualResetEventSlim())
            {
                target.OnBegin = () =>
                {
                    entered.Set();
                    Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)), "backend was not released");
                };
                var header = Task.Run(() => Send(0xa5, 8));
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "read did not reach backend");
                    // These edges arrive while the accepted read is blocked. If
                    // replayed, they finish that read and commit a subsequent write.
                    target.OnGPIO(0, false);
                    for(var i = 0; i < 38; i++) RawCycle(true);
                    RawSend(0x81, 8);
                    for(var i = 0; i < 5; i++) RawCycle(false);
                    RawSend(0x12345678, 32);
                    RawCycle(Parity(0x12345678));
                    if(hardwareReset)
                    {
                        target.OnGPIO(2, false);
                        // A machine/peripheral reset may coalesce with the pin
                        // assertion while the old transaction is unwinding.
                        target.Reset();
                    }
                    else target.Reset();
                    Assert.IsFalse(IsDriving);
                    Assert.AreEqual(0, target.BackendOverlap);
                    // New input cannot bypass the serialized backend reset.
                    RawSend(0xa5, 8);
                    Assert.AreEqual(1, target.RequestCount);
                }
                finally { release.Set(); }
                Assert.IsTrue(header.Wait(TimeSpan.FromSeconds(5)), "protocol owner did not finish");
                Assert.AreEqual(0, target.WriteCount, "stale write reached backend after reset");
                Assert.AreEqual(0, target.BackendOverlap, "reset overlapped an in-flight backend operation");
                Assert.AreEqual(hardwareReset ? 1 : 0, target.TargetResetCount, "physical reset request was lost when resets coalesced");
                Assert.IsFalse(IsDriving, "old ACK/data drive was restored after reset");
                target.OnBegin = null;
                if(hardwareReset) target.OnGPIO(2, true);
                Wake();
                Assert.AreEqual(0x4c013477, Read(0xa5));
            }
        }

        [Test]
        public void ShouldNotHoldTheProtocolMonitorAcrossBackendOrDriveCallbacks()
        {
            var backendChecks = 0;
            var driveChecks = 0;
            target.OnBegin = () =>
            {
                Assert.IsTrue(Task.Run(() => target.GetOutputState(1, out var _, out var __)).Wait(TimeSpan.FromSeconds(5)),
                    "backend callback holds protocol monitor");
                target.OnGPIO(1, true);
                backendChecks++;
            };
            target.DriveChanged += (_, level, enabled) =>
            {
                Assert.IsTrue(Task.Run(() => target.GetOutputState(1, out var __, out var ___)).Wait(TimeSpan.FromSeconds(5)),
                    "drive callback holds protocol monitor");
                target.OnGPIO(1, enabled ? level : true);
                driveChecks++;
            };
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
            Assert.AreEqual(1, backendChecks);
            Assert.Greater(driveChecks, 3);
        }

        [Test]
        public void ShouldDiscardOldDriveSubscribersAfterAReentrantReset()
        {
            var reset = false;
            var observed = new List<bool>();
            target.DriveChanged += (_, level, enabled) =>
            {
                if(enabled && !reset)
                {
                    reset = true;
                    target.Reset();
                }
            };
            target.DriveChanged += (_, level, enabled) => observed.Add(enabled);
            Wake();
            Send(0xa5, 8);
            Cycle(null);
            Assert.IsTrue(reset);
            CollectionAssert.AreEqual(new[] { false }, observed, "old-epoch enable was replayed after reset");
            Assert.IsFalse(IsDriving);
            Assert.AreEqual(0, target.BackendOverlap);
        }

        [Test]
        public void ShouldInvalidateABlockedDrivePublicationWhenAnotherThreadResets()
        {
            using(var entered = new ManualResetEventSlim())
            using(var release = new ManualResetEventSlim())
            {
                var firstEnable = true;
                var observed = new List<bool>();
                target.DriveChanged += (_, level, enabled) =>
                {
                    if(enabled && firstEnable)
                    {
                        firstEnable = false;
                        entered.Set();
                        Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)), "drive callback was not released");
                    }
                };
                target.DriveChanged += (_, level, enabled) => observed.Add(enabled);
                Wake();
                Send(0xa5, 8);
                var turnaround = Task.Run(() => Cycle(null));
                try
                {
                    Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)), "ACK publication did not start");
                    Assert.IsTrue(Task.Run(() => target.Reset()).Wait(TimeSpan.FromSeconds(5)), "reset waited for an external drive callback");
                    Assert.IsFalse(IsDriving);
                }
                finally { release.Set(); }
                Assert.IsTrue(turnaround.Wait(TimeSpan.FromSeconds(5)));
                CollectionAssert.AreEqual(new[] { false }, observed, "old ACK notification reached a later subscriber after reset");
                Assert.IsFalse(IsDriving);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldDeferABackendCallbackResetUntilTheAcceptedOperationUnwinds(bool foreignThread)
        {
            target.OnBegin = () =>
            {
                if(foreignThread)
                    Assert.IsTrue(Task.Run(() => target.Reset()).Wait(TimeSpan.FromSeconds(5)), "foreign reset callback blocked on backend owner");
                else target.Reset();
            };
            Wake();
            Send(0xa5, 8);
            Cycle(null);
            Assert.IsFalse(IsDriving);
            Assert.AreEqual(0, target.BackendOverlap);
            Assert.AreEqual(0, target.WriteCount);
            target.OnBegin = null;
            Wake();
            Assert.AreEqual(0x4c013477, Read(0xa5));
        }

        private void RawSend(uint value, int count)
        {
            for(var i = 0; i < count; i++) RawCycle(((value >> i) & 1) != 0);
        }

        private void RawCycle(bool value)
        {
            target.OnGPIO(1, value);
            target.OnGPIO(0, true);
            target.OnGPIO(0, false);
        }

        private void Write(uint header, uint value, bool parity)
        {
            Send(header, 8);
            Cycle(null);
            Assert.AreEqual(1, Receive(3));
            Assert.IsFalse(IsDriving);
            Cycle(null);
            Send(value, 32);
            Cycle(parity);
            Send(0, 2);
        }

        private uint Read(uint header)
        {
            Send(header, 8);
            Cycle(null);
            Assert.AreEqual(1, Receive(3));
            var value = Receive(32);
            Assert.AreEqual(Parity(value), Cycle(null));
            Cycle(null);
            Send(0, 2);
            return value;
        }

        private void Wake()
        {
            SendAlert();
            Send(0, 4);
            Send(0x1a, 8);
            LineReset();
        }

        private void SendAlert()
        {
            Send(0xff, 8);
            foreach(var word in new uint[] { 0x19bc0ea2, 0xe3ddafe9, 0x86852d95, 0x6209f392 }) Send(word, 32);
        }

        private void LineReset()
        {
            for(var i = 0; i < 56; i++) Cycle(true);
            Send(0, 2);
        }

        private void Send(uint value, int count)
        {
            for(var i = 0; i < count; i++) Cycle(((value >> i) & 1) != 0);
        }

        private uint Receive(int count)
        {
            uint value = 0;
            for(var i = 0; i < count; i++) if(Cycle(null)) value |= 1u << i;
            return value;
        }

        private bool Cycle(bool? host)
        {
            target.GetOutputState(1, out var output, out var enabled);
            // Both drivers see the same wire. Driving opposite levels is an error.
            Assert.IsFalse(host.HasValue && enabled && host.Value != output, "SWDIO contention");
            var resolved = host ?? (enabled ? output : true);
            target.OnGPIO(1, resolved);
            target.OnGPIO(0, true);
            target.OnGPIO(0, false);
            return resolved;
        }

        private bool IsDriving
        {
            get { target.GetOutputState(1, out var _, out var enabled); return enabled; }
        }

        private static bool Parity(uint value)
        {
            var parity = false;
            for(var i = 0; i < 32; i++) parity ^= ((value >> i) & 1) != 0;
            return parity;
        }

        private sealed class TestTarget : SerialWireDebugPort
        {
            public int RequestCount, WriteCount, ParityErrors, Ack = 1;
            public int BackendOverlap;
            public int TargetResetCount;
            public Action OnBegin;
            public uint LastWrite;
            protected override Acknowledgement BeginTransfer(bool ap, bool read, byte address, out uint value)
            {
                EnterBackend();
                try
                {
                    Interlocked.Increment(ref RequestCount);
                    OnBegin?.Invoke();
                    value = 0x4c013477;
                    return (Acknowledgement)Ack;
                }
                finally { Interlocked.Decrement(ref activeBackend); }
            }
            protected override void CompleteWrite(bool ap, byte address, uint value)
            {
                EnterBackend();
                try { Interlocked.Increment(ref WriteCount); LastWrite = value; }
                finally { Interlocked.Decrement(ref activeBackend); }
            }
            protected override void ResetBackend()
            {
                EnterBackend();
                Interlocked.Decrement(ref activeBackend);
            }
            private void EnterBackend()
            {
                if(Interlocked.Increment(ref activeBackend) > 1) Interlocked.Increment(ref BackendOverlap);
            }
            protected override void WriteParityError() { ParityErrors++; }
            protected override void ResetTarget() { Interlocked.Increment(ref TargetResetCount); }
            protected override bool SelectTarget(uint value) { return value == 0x01002927; }
            private int activeBackend;
        }

        private TestTarget target;
    }
}
