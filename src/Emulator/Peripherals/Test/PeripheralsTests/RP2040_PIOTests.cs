//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.GPIOPort;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;

using NUnit.Framework;

namespace Antmicro.Renode.PeripheralsTests
{
    [TestFixture]
    public class RP2040_PIOTests
    {
        [SetUp]
        public void SetUp()
        {
            machine = new Machine();
            gpio = new RP2_GPIOBank0(machine);
            pio = new RP2040_PIO(machine, gpio, frequency: 1000000);
        }

        [TearDown]
        public void TearDown() => machine.Dispose();

        [Test]
        public void ShouldExecuteUnmodifiedMetalclawSWDWriteProgram()
        {
            ConfigureMetalclawSWD();
            var bits = new List<bool>();
            var times = new List<ulong>();
            gpio.DriveChanged += (pin, value, enabled) =>
            {
                Assert.IsFalse(Monitor.IsEntered(pio));
                if(pin != ClockPin || !value || !enabled) return;
                GetOutputState(DataPin, out var data, out var dataEnabled);
                Assert.IsTrue(dataEnabled);
                bits.Add(data);
                times.Add(machine.ClockSource.CurrentValue.TotalNanoseconds);
            };
            pio.WriteDoubleWord(0x10, Command(8, true, 0));
            pio.WriteDoubleWord(0x10, 0xa6);
            pio.WriteDoubleWord(0, 1);
            Advance(100);
            CollectionAssert.AreEqual(new[] { false, true, true, false, false, true, false, true }, bits);
            CollectionAssert.AreEqual(Enumerable.Repeat(4000ul, 7), times.Skip(1).Zip(times, (a, b) => a - b));
            Assert.AreEqual(3u, pio.ReadDoubleWord(0xd4));
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(8) & (1u << 24));
            Assert.IsFalse(gpio.Connections[ClockPin].IsSet);
        }

        [Test]
        public void ShouldReadActualSWDWireAndReleaseDirection()
        {
            ConfigureMetalclawSWD();
            const uint response = 0xb69d2317;
            var bit = 0;
            var clocks = 0;
            gpio.OnGPIO(DataPin, (response & 1) != 0);
            gpio.DriveChanged += (pin, value, enabled) =>
            {
                if(pin != ClockPin || !enabled) return;
                GetOutputState(DataPin, out _, out var dataEnabled);
                Assert.IsFalse(dataEnabled);
                if(value) clocks++;
                else if(clocks != 0)
                {
                    bit++;
                    if(bit < 32) gpio.OnGPIO(DataPin, ((response >> bit) & 1) != 0);
                }
            };
            pio.WriteDoubleWord(0x10, Command(32, false, 8));
            pio.WriteDoubleWord(0, 1);
            Advance(200);
            Assert.AreEqual(32, clocks);
            Assert.AreEqual(response, pio.ReadDoubleWord(0x20));
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(8) & (1u << 24));
        }

        [Test]
        public void ShouldClockHighImpedanceSWDTurnaroundUsingOUTPINDIRS()
        {
            ConfigureMetalclawSWD();
            var clocks = 0;
            gpio.SetPadConfiguration(DataPin, 0x5a);
            gpio.DriveChanged += (pin, value, enabled) =>
            {
                if(pin != ClockPin || !value || !enabled) return;
                GetOutputState(DataPin, out _, out var dataEnabled);
                Assert.IsFalse(dataEnabled);
                Assert.IsTrue(gpio.GetInput(DataPin));
                clocks++;
            };
            pio.WriteDoubleWord(0x10, Command(3, false, 0));
            pio.WriteDoubleWord(0x10, 0);
            pio.WriteDoubleWord(0, 1);
            Advance(50);
            Assert.AreEqual(3, clocks);
        }

        [Test]
        public void ShouldKeepFourIndependentFifosAndJoinOwnership()
        {
            Assert.AreEqual(0x0f000f00u, pio.ReadDoubleWord(4));
            for(var sm = 0; sm < 4; sm++)
            {
                for(uint i = 0; i < 4; i++) pio.WriteDoubleWord(0x10 + 4 * sm, (uint)(sm * 100) + i);
                Assert.IsFalse(pio.Connections[sm].IsSet);
                pio.WriteDoubleWord(0x10 + 4 * sm, 999);
            }
            Assert.AreEqual(0x000f0000u, pio.ReadDoubleWord(8));
            for(var sm = 0; sm < 4; sm++)
            {
                for(uint i = 0; i < 4; i++)
                {
                    Force(sm, 0x80a0); // pull block
                    Force(sm, 0xa0c7); // mov isr, osr
                    Force(sm, 0x8020); // push block
                    Assert.AreEqual((uint)(sm * 100) + i, pio.ReadDoubleWord(0x20 + 4 * sm));
                }
            }
            pio.WriteDoubleWord(0xd0, (1u << 30) | 0xc0000);
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(4) & 1);
            for(uint i = 0; i < 8; i++) pio.WriteDoubleWord(0x10, i);
            Assert.AreEqual(8u, pio.ReadDoubleWord(12) & 15);
            Assert.IsFalse(pio.Connections[0].IsSet);
            pio.WriteDoubleWord(0xd0, (1u << 31) | 0xc0000);
            Assert.AreEqual(0u, pio.ReadDoubleWord(12) & 255);
            Assert.IsFalse(pio.Connections[0].IsSet);
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(4) & (1u << 16));
        }

        [Test]
        public void ShouldStallAutopushWithoutShiftingInputTwice()
        {
            for(var i = 0; i < 4; i++)
            {
                Force(0, (ushort)(0xe020 | i)); // set x
                Force(0, 0xa0c1); // mov isr, x
                Force(0, 0x8020);
            }
            pio.WriteDoubleWord(0xd0, 0xc0000 | (8u << 20) | (1u << 16));
            gpio.OnGPIO(0, true);
            Force(0, 0x4008); // in pins, 8
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            gpio.OnGPIO(0, false);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x20));
            Advance(1);
            for(uint i = 1; i < 4; i++) Assert.AreEqual(i, pio.ReadDoubleWord(0x20));
            Assert.AreEqual(0x01000000u, pio.ReadDoubleWord(0x20));
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
        }

        [Test]
        public void ShouldWaitForRealPinAndPreserveDividerPhaseAcrossStall()
        {
            // wait 1 gpio 4; set x, 19; mov isr, x; push; pull block
            Load(0x2084, 0xe033, 0xa0c1, 0x8020, 0x80a0);
            pio.WriteDoubleWord(0xc8, 3u << 16);
            pio.WriteDoubleWord(0, 1);
            Advance(1000000);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xd4));
            gpio.OnGPIO(4, true);
            Advance(1); // tick 1000001, next enable at 1000002
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xd4));
            Advance(1);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            Advance(12);
            Assert.AreEqual(19u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldPreserveFractionalDividerAndDelaySideSet()
        {
            gpio.WriteDoubleWord(4, 6);
            pio.WriteDoubleWord(0xdc, 1u << 26);
            Force(0, 0xe081); // set pindirs, 1
            pio.WriteDoubleWord(0xdc, (1u << 29)); // one mandatory side-set pin
            pio.WriteDoubleWord(0xcc, 1u << 12); // wrap 0..1
            pio.WriteDoubleWord(0xc8, (1u << 16) | (128u << 8));
            Load(0xb042, 0xa042); // nop side 1; nop side 0
            var times = new List<ulong>();
            gpio.DriveChanged += (pin, value, enabled) => { if(pin == 0) times.Add(machine.ClockSource.CurrentValue.TotalNanoseconds); };
            pio.WriteDoubleWord(0, 1);
            Advance(12);
            CollectionAssert.AreEqual(new ulong[] { 1000, 3000, 4000, 6000, 7000, 9000, 10000, 12000 }, times);
        }

        [Test]
        public void ShouldExecuteInjectedInstructionAndIgnoreOuterDelay()
        {
            // pull; out exec, 32 [31]; mov isr,x; push; pull block
            Load(0x80a0, 0x7fe0, 0xa0c1, 0x8020, 0x80a0);
            pio.WriteDoubleWord(0x10, 0xe03b); // set x,27
            pio.WriteDoubleWord(0, 1);
            Advance(5);
            Assert.AreEqual(27u, pio.ReadDoubleWord(0x20));
            Assert.AreEqual(4u, pio.ReadDoubleWord(0xd4));
        }

        [Test]
        public void ShouldExecuteMovReverseInvertAndBothShiftDirections()
        {
            pio.WriteDoubleWord(0x10, 0x12345678);
            Force(0, 0x80a0);
            Force(0, 0xa037); // mov x, ::osr
            Force(0, 0xa0c9); // mov isr, ~x
            Force(0, 0x8020);
            Assert.AreEqual(~0x1e6a2c48u, pio.ReadDoubleWord(0x20));
            Force(0, 0x6028); // out x,8 right
            Force(0, 0xa0c1);
            Force(0, 0x8020);
            Assert.AreEqual(0x78u, pio.ReadDoubleWord(0x20));
            pio.WriteDoubleWord(0xd0, 0);
            Force(0, 0x6028); // out x,8 left, residual OSR is 0x00123456
            Force(0, 0xa0c1);
            Force(0, 0x8020);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x20));
            Force(0, 0xe023); // set x,3
            Force(0, 0x4022); // in x,2 left
            Force(0, 0x4022);
            Force(0, 0x8020);
            Assert.AreEqual(15u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldRouteRelativeIrqAndWaitForGenuineAcknowledgement()
        {
            pio.WriteDoubleWord(0x12c, 1u << 10); // processor IRQ0 from flag2
            Force(2, 0xc030); // irq wait 0 rel => flag2
            Assert.IsTrue(pio.IRQ0.IsSet);
            Assert.AreEqual(4u, pio.ReadDoubleWord(0x30));
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(0xfc) & 0x80000000);
            Force(1, 0x20d1); // wait 1 irq 1 rel => flag2, clears it
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x30));
            Assert.IsFalse(pio.IRQ0.IsSet);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xfc) & 0x80000000);
        }

        [Test]
        public void ShouldRestartExecutionWithoutDiscardingFifosOrScratch()
        {
            Force(0, 0xe02d); // set x,13
            pio.WriteDoubleWord(0x10, 42);
            Force(0, 0x2007); // wait 0 gpio7 succeeds
            gpio.OnGPIO(7, true);
            Force(0, 0x2007); // now stalls
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            pio.WriteDoubleWord(0x2000, 1u << 4); // SM_RESTART strobe
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            Assert.AreEqual(1u, pio.ReadDoubleWord(12) & 15);
            Force(0, 0xa0c1);
            Force(0, 0x8020);
            Assert.AreEqual(13u, pio.ReadDoubleWord(0x20));
            Force(0, 0x80a0);
            Force(0, 0xa0c7);
            Force(0, 0x8020);
            Assert.AreEqual(42u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldStopClockAndGPIOWhileResetIsHeld()
        {
            ConfigureMetalclawSWD();
            pio.WriteDoubleWord(0x10, Command(32, true, 0));
            pio.WriteDoubleWord(0x10, uint.MaxValue);
            pio.WriteDoubleWord(0, 1);
            Advance(16);
            pio.ResetHeld = true;
            var changes = 0;
            gpio.DriveChanged += (_, __, ___) => changes++;
            pio.WriteDoubleWord(0, 1);
            Advance(100);
            Assert.AreEqual(0, changes);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0));
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x40));
        }

        private void ConfigureMetalclawSWD()
        {
            // Exact words and SM configuration from Metalclaw swd_pio.c.
            Load(0x80a0, 0x7101, 0x1941, 0x90a0, 0x6028, 0x6081,
                0x60a5, 0xa042, 0x5901, 0x1047, 0x8020);
            gpio.WriteDoubleWord(ClockPin * 8 + 4, 6);
            gpio.WriteDoubleWord(DataPin * 8 + 4, 6);
            pio.WriteDoubleWord(0xdc, (1u << 26) | (ClockPin << 5));
            Force(0, 0xe081);
            pio.WriteDoubleWord(0xdc, (2u << 29) | (1u << 26) | (1u << 20) |
                (DataPin << 15) | (ClockPin << 10) | (DataPin << 5) | DataPin);
            Force(0, 0xe081);
            pio.WriteDoubleWord(0xcc, (1u << 30) | (10u << 12) | (3u << 7));
            pio.WriteDoubleWord(0xd0, 0xc0000);
            Force(0, 3);
        }

        private void GetOutputState(int pin, out bool value, out bool enabled)
        {
            var status = gpio.ReadDoubleWord(pin * 8);
            value = (status & (1u << 9)) != 0;
            enabled = (status & (1u << 13)) != 0;
        }

        private void Force(int sm, ushort instruction) => pio.WriteDoubleWord(0xd8 + sm * 0x18, instruction);
        private static uint Command(int count, bool output, int address) => (uint)((count - 1) | (output ? 256 : 0) | (address << 9));
        private void Load(params ushort[] words)
        {
            for(var index = 0; index < words.Length; index++) pio.WriteDoubleWord(0x48 + 4 * index, words[index]);
        }
        private void Advance(ulong microseconds) => ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromMicroseconds(microseconds));
        private const int ClockPin = 2;
        private const int DataPin = 3;
        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2040_PIO pio;
    }
}
