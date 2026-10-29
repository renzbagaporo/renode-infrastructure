//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
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
            Write(0x81, 0x12345678, !Parity(0x12345678));
            Assert.AreEqual(0, target.WriteCount);
            Assert.AreEqual(1, target.ParityErrors);
            Write(0x81, 0xabcdef00, Parity(0xabcdef00));
            Assert.AreEqual(1, target.WriteCount);
            Assert.AreEqual(0xabcdef00, target.LastWrite);
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
            public uint LastWrite;
            protected override Acknowledgement BeginTransfer(bool ap, bool read, byte address, out uint value)
            {
                RequestCount++;
                value = 0x4c013477;
                return (Acknowledgement)Ack;
            }
            protected override void CompleteWrite(bool ap, byte address, uint value) { WriteCount++; LastWrite = value; }
            protected override void WriteParityError() { ParityErrors++; }
        }

        private TestTarget target;
    }
}
