//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Antmicro.Renode.Utilities;
using NUnit.Framework;
namespace Antmicro.Renode.UnitTests
{
    [TestFixture, NonParallelizable]
    public class SocketServerProviderTests
    {
        [SetUp]
        public void SetUp()
        {
            clients = new List<TcpClient>();
            accepted = new ConcurrentQueue<Stream>();
            closed = new ManualResetEventSlim();
            received = new AutoResetEvent(false);
        }
        [TearDown]
        public void TearDown()
        {
            foreach (var client in clients)
                client.Dispose();
            server?.Dispose();
            closed.Dispose();
            received.Dispose();
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ShouldRetainOrDiscardDataQueuedBeforeConnection(bool flushOnConnect)
        {
            CreateServer(flushOnConnect: flushOnConnect);
            server.Send(new byte[] { 1, 2, 3 });
            var client = Connect();
            server.SendByte(0x7a);
            Assert.That(ReadExactly(client, flushOnConnect ? 1 : 4), Is.EqualTo(flushOnConnect ? new byte[] { 0x7a } : new byte[] { 1, 2, 3, 0x7a }));
        }
        [Test]
        public void ShouldPreserveMixedSendFifoAcrossCoalescedBurstNotifications()
        {
            CreateServer();
            var client = Connect();
            var expected = new List<byte>();
            for (var i = 0; i < 257; i++)
            {
                server.SendByte((byte)i);
                expected.Add((byte)i);
                var packet = BitConverter.GetBytes(i);
                server.Send(packet);
                expected.AddRange(packet);
            }
            Assert.That(ReadExactly(client, expected.Count), Is.EqualTo(expected));
        }
        [Test]
        public void ShouldWakeAParkedWriterForRepeatedDelayedSends()
        {
            CreateServer();
            var client = Connect();
            for (var i = 0; i < 12; i++)
            {
                WaitForParkedWriter();
                server.SendByte((byte)i);
                Assert.That(ReadExactly(client, 1), Is.EqualTo(new byte[] { (byte)i }));
            }
        }
        [Test]
        public void ShouldDeliverNewDataWhileAnEarlierLargeWriteIsDraining()
        {
            CreateServer();
            var client = Connect();
            var first = Enumerable.Range(0, 1024 * 1024).Select(i => (byte)i).ToArray();
            server.Send(first);
            server.Send(new byte[] { 0xf1, 0xf2, 0xf3 });
            Assert.That(ReadExactly(client, first.Length + 3), Is.EqualTo(first.Concat(new byte[] { 0xf1, 0xf2, 0xf3 })));
        }
        [Test]
        public void ShouldDeliverConcurrentBurstsWithoutLosingOrReorderingEachProducer()
        {
            CreateServer();
            var client = Connect();
            const int producers = 4, count = 200;
            var tasks = Enumerable.Range(0, producers).Select(id => Task.Run(() => { for (var i = 0; i < count; i++) server.Send(new byte[] { (byte)id, (byte)i }); })).ToArray();
            Assert.That(Task.WaitAll(tasks, TimeSpan.FromSeconds(5)), Is.True);
            var result = ReadExactly(client, producers * count * 2);
            var expected = new int[producers];
            for (var i = 0; i < result.Length; i += 2)
            {
                var id = result[i];
                Assert.Less(id, producers);
                Assert.AreEqual(expected[id]++, result[i + 1]);
            }
            Assert.That(expected, Is.All.EqualTo(count));
        }
        [TestCase(false)]
        [TestCase(true)]
        public void ShouldPreserveQueuedBetweenConnectionsAccordingToFlushMode(bool flushOnConnect)
        {
            CreateServer(flushOnConnect: flushOnConnect);
            var first = Connect();
            first.Dispose();
            Assert.That(closed.Wait(Timeout), Is.True);
            closed.Reset();
            server.Send(new byte[] { 4, 5, 6 });
            var second = Connect();
            server.SendByte(0x7b);
            Assert.That(ReadExactly(second, flushOnConnect ? 1 : 4), Is.EqualTo(flushOnConnect ? new byte[] { 0x7b } : new byte[] { 4, 5, 6, 0x7b }));
        }
        [Test]
        public void ShouldIgnoreADisconnectForAnEarlierConnection()
        {
            CreateServer();
            var first = Connect();
            Assert.That(accepted.TryDequeue(out var oldStream), Is.True);
            server.DisconnectClient(oldStream);
            Assert.That(closed.Wait(Timeout), Is.True);
            closed.Reset();
            var second = Connect();
            server.DisconnectClient(oldStream);
            server.SendByte(0x71);
            Assert.That(ReadExactly(second, 1), Is.EqualTo(new byte[] { 0x71 }));
            Assert.IsFalse(closed.IsSet);
        }
        [Test]
        public void ShouldCloseTheCurrentClientAndAcceptAnotherWhileWriterWasParked()
        {
            CreateServer();
            var first = Connect();
            WaitForParkedWriter();
            Assert.That(accepted.TryDequeue(out var stream), Is.True);
            server.DisconnectClient(stream);
            Assert.That(closed.Wait(Timeout), Is.True);
            var next = Connect();
            server.SendByte(0x72);
            Assert.That(ReadExactly(next, 1), Is.EqualTo(new byte[] { 0x72 }));
        }
        [Test]
        public void ShouldWakeAParkedWriterWhenItsReaderSeesEof()
        {
            CreateServer();
            var client = Connect();
            WaitForParkedWriter();
            client.Client.Shutdown(SocketShutdown.Send);
            Assert.That(closed.Wait(Timeout), Is.True);
            var next = Connect();
            server.SendByte(0x73);
            Assert.That(ReadExactly(next, 1), Is.EqualTo(new byte[] { 0x73 }));
        }
        [Test]
        public void ShouldStopWithTheWriterParked()
        {
            CreateServer();
            var client = Connect();
            WaitForParkedWriter();
            var stop = Task.Run(() => server.Stop());
            Assert.That(stop.Wait(Timeout), Is.True);
            Assert.AreEqual(-1, client.GetStream().ReadByte());
        }
        [Test]
        public void ShouldKeepTelnetNegotiationAndIacEscaping()
        {
            CreateServer(telnet: true);
            var client = Connect(telnet: true);
            server.Send(new byte[] { 1, 255, 2, 255, 255, 3 });
            Assert.That(ReadExactly(client, 9), Is.EqualTo(new byte[] { 1, 255, 255, 2, 255, 255, 255, 255, 3 }));
        }
        private void CreateServer(bool telnet = false, bool flushOnConnect = false)
        {
            server = new SocketServerProvider(telnet, flushOnConnect, "socket_writer_test");
            server.BufferSize = 1024;
            server.ConnectionAccepted += stream => accepted.Enqueue(stream);
            server.ConnectionClosed += () => closed.Set();
            server.DataBlockReceived += _ => received.Set();
            server.Start(0);
        }
        private TcpClient Connect(bool telnet = false)
        {
            var client = new TcpClient() { NoDelay = true, ReceiveTimeout = 10000 };
            clients.Add(client);
            client.Connect(IPAddress.Loopback, server.Port.Value);
            if (telnet)
                Assert.That(ReadExactly(client, 15), Is.EqualTo(new byte[] { 255, 253, 0, 255, 253, 31, 255, 251, 1, 255, 251, 3, 255, 252, 34 }));
            client.GetStream().WriteByte(0xa5);
            Assert.That(received.WaitOne(Timeout), Is.True, "The reader handshake proves connection setup completed");
            return client;
        }
        private void WaitForParkedWriter()
        {
            var thread = (Thread)typeof(SocketServerProvider).GetField("writerThread", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(server);
            Assert.That(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, Timeout), Is.True, "Writer never reached its notification wait");
        }
        private static byte[] ReadExactly(TcpClient client, int count)
        {
            var data = new byte[count];
            var stream = client.GetStream();
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(data, read, count - read);
                if (n == 0)
                    throw new EndOfStreamException();
                read += n;
            }
            return data;
        }
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private SocketServerProvider server; private List<TcpClient> clients; private ConcurrentQueue<Stream> accepted; private ManualResetEventSlim closed; private AutoResetEvent received;
    }
}
