//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;

using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Peripherals.USB;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2_USB_DPRAMTests
    {
        [Test]
        public void ShouldPreserveOtherByteLanesDuringNarrowWrites()
        {
            var memory = new RP2_USB_DPRAM();
            memory.WriteDoubleWord(0x100, 0x44332211);
            memory.WriteByte(0x101, 0xaa);
            memory.WriteWord(0x102, 0xbbcc);
            Assert.That(memory.ReadDoubleWord(0x100), Is.EqualTo(0xbbccaa11));
            Assert.That(memory.ReadBytes(0x100, 4), Is.EqualTo(new byte[] { 0x11, 0xaa, 0xcc, 0xbb }));
        }

        [Test]
        public void ShouldNotifyForBothHalvesOfEachEndpointBufferControl()
        {
            var memory = new RP2_USB_DPRAM();
            var controls = new List<int>();
            memory.BufferControlWritten += controls.Add;
            memory.WriteWord(0x82, 0x8400);
            memory.WriteWord(0x84, 0x0400);
            memory.WriteByte(0xff, 0x84);
            Assert.That(controls, Is.EqualTo(new int[] { 0, 1, 31 }));
        }

        [Test]
        public void ShouldNotNotifyForSetupOrPayloadWrites()
        {
            var memory = new RP2_USB_DPRAM();
            var controls = new List<int>();
            memory.BufferControlWritten += controls.Add;
            memory.WriteBytes(0, new byte[8]);
            memory.WriteDoubleWord(0x7c, 0x80000000);
            memory.WriteBytes(0x100, new byte[64]);
            memory.WriteByte(0xfff, 0x42);
            Assert.That(controls, Is.Empty);
        }

        [Test]
        public void ShouldExposeCompletedWriteToCallback()
        {
            var memory = new RP2_USB_DPRAM();
            uint observed = 0;
            memory.BufferControlWritten += control => observed = memory.ReadDoubleWord(0x80 + control * 4);
            memory.WriteDoubleWord(0x80, 0x12345678);
            Assert.That(observed, Is.EqualTo(0x12345678));
        }

        [Test]
        public void ShouldNotifyEveryControlInBoundaryCrossingBulkWrite()
        {
            var memory = new RP2_USB_DPRAM();
            var controls = new List<int>();
            memory.BufferControlWritten += controls.Add;
            memory.WriteBytes(0x7f, new byte[130]);
            Assert.That(controls.Count, Is.EqualTo(32));
            Assert.That(controls[0], Is.Zero);
            Assert.That(controls[31], Is.EqualTo(31));
        }

        [Test]
        public void ShouldRejectAccessOutsideDPRAM()
        {
            var memory = new RP2_USB_DPRAM();
            Assert.Throws<RecoverableException>(() => memory.ReadByte(-1));
            Assert.Throws<RecoverableException>(() => memory.ReadDoubleWord(0xffe));
            Assert.Throws<RecoverableException>(() => memory.WriteBytes(0xfff, new byte[2]));
            Assert.That(memory.ReadBytes(0x1000, 0), Is.Empty);
        }
    }
}
