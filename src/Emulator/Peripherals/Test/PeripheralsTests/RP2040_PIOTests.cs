//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
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
            pio.WriteDoubleWord(0x38, 1); // explicitly bypass the input synchronizer
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
            pio.WriteDoubleWord(0x38, 1u << 7);
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

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldExecuteMetalclawSPIStreamAndCSMonitor(bool lead)
        {
            const int mosi = 8, miso = 9, sck = 10, cs = 11;
            // The generated words match encode_program in spi_target.c.
            LoadMetalclawSPI(sck, lead);
            gpio.WriteDoubleWord(miso * 8 + 4, 6);
            gpio.OnGPIO(cs, true);
            gpio.OnGPIO(sck, false);
            Advance(2); // let the CS input synchronizer settle before enabling
            pio.WriteDoubleWord(0xdc, (1u << 26) | (1u << 20) | (mosi << 15) | (miso << 5) | miso);
            pio.WriteDoubleWord(0xcc, (cs << 24) | (11u << 12) | (6u << 7));
            pio.WriteDoubleWord(0xd0, (8u << 25) | (8u << 20) | (1u << 19) | (1u << 17) | (1u << 16));
            // Firmware reverses an MSB-first byte, then feeds the LSB-first OSR.
            pio.WriteByte(0x13, 0x65); // wire byte for 0xa6, APB replication
            pio.WriteByte(0x13, 0x5a);
            pio.WriteDoubleWord(0xf4, cs << 15); // SM1 monitor input base
            pio.WriteDoubleWord(0xe4, (31u << 12) | (29u << 7));
            Force(1, 29);
            pio.WriteDoubleWord(0x138, 1u << 9); // IRQ1 from relative monitor flag1
            pio.WriteDoubleWord(0, 3);
            Advance(20);
            Assert.AreEqual(3u, pio.ReadDoubleWord(0xd4));
            Advance(1000000); // no clock callbacks are needed in the CS-high JMP
            Assert.AreEqual(3u, pio.ReadDoubleWord(0xd4));
            GetOutputState(miso, out _, out var enabled);
            Assert.IsFalse(enabled);
            gpio.OnGPIO(cs, false);
            Advance(8);
            uint response = 0;
            const uint request = 0x3c;
            for(var bit = 7; bit >= 0; bit--)
            {
                gpio.OnGPIO(mosi, ((request >> bit) & 1) != 0);
                gpio.OnGPIO(sck, lead);
                Advance(6);
                gpio.OnGPIO(sck, !lead);
                response = (response << 1) | (gpio.GetInput(miso) ? 1u : 0u);
                Advance(6);
            }
            Assert.AreEqual(0xa6u, response);
            Assert.AreEqual(request, pio.ReadDoubleWord(0x20));
            Assert.IsFalse(pio.IRQ1.IsSet);
            gpio.OnGPIO(cs, true);
            Advance(4);
            Assert.IsTrue(pio.IRQ1.IsSet);
            Assert.AreEqual(2u, pio.ReadDoubleWord(0x30));
        }

        [Test]
        public void ShouldKeepForcedInstructionStalledUntilItExecutes()
        {
            Force(0, 0x80a0);
            pio.WriteDoubleWord(0x10, 0x29);
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            Force(0, 0xa0c7);
            Force(0, 0x8020);
            Assert.AreEqual(0x29u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldRetryInitialAutopullAndFlagOnlyEmptyFIFOStalls()
        {
            pio.WriteDoubleWord(0xd0, 0xc0000 | (1u << 17));
            pio.WriteDoubleWord(0x10, 0xb1234567);
            Force(0, 0x6020); // out x,32: first cycle refills OSR
            Assert.AreEqual(0u, pio.ReadDoubleWord(8));
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            Force(0, 0xa0c1);
            Force(0, 0x8020);
            Assert.AreEqual(0xb1234567u, pio.ReadDoubleWord(0x20));
            Force(0, 0x6020);
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(8) & (1u << 24));
        }

        [Test]
        public void ShouldResolveSimultaneousPinWritesWithHigherSMWinning()
        {
            gpio.WriteDoubleWord(4, 6);
            pio.WriteDoubleWord(0xdc, 1u << 26);
            pio.WriteDoubleWord(0xf4, 1u << 26);
            Force(0, 0xe081);
            Load(0xe001, 0xe000, 0x80a0); // SM0 writes one, SM1 writes zero
            Force(1, 1);
            var changes = 0;
            gpio.DriveChanged += (_, __, ___) => changes++;
            pio.WriteDoubleWord(0, 3);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x3c));
            Assert.AreEqual(0, changes, "A losing SM must not create a pin glitch");
        }

        [Test]
        public void ShouldRetainStickyPinPriorityUntilInlineEnableDeasserts()
        {
            gpio.WriteDoubleWord(4, 6);
            pio.WriteDoubleWord(0xdc, 1u << 26);
            pio.WriteDoubleWord(0xf4, 1u << 26);
            Force(0, 0xe081);
            pio.WriteDoubleWord(0xe4, 0x1f000 | (1u << 17) | (1u << 18));
            Force(1, 0xe001);
            Assert.IsTrue(gpio.GetInput(0));
            Force(0, 0xe000);
            Assert.IsTrue(gpio.GetInput(0));
            Force(1, 0xe000); // inline enable zero removes SM1's sticky strobe
            Force(0, 0xe000);
            Assert.IsFalse(gpio.GetInput(0));
            pio.WriteDoubleWord(0xdc, (1u << 29) | (1u << 26));
            Force(0, 0xf000); // set pins,0 side1: side-set wins
            Assert.IsTrue(gpio.GetInput(0));
        }

        [Test]
        public void ShouldDelaySynchronizedInputsByTwoSystemClocksAndHonorBypass()
        {
            gpio.OnGPIO(0, true);
            Force(0, 0x4001);
            Force(0, 0x8020);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x20));
            Advance(1);
            Force(0, 0x4001);
            Force(0, 0x8020);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x20));
            Advance(1);
            Force(0, 0x4001);
            Force(0, 0x8020);
            Assert.AreEqual(0x80000000u, pio.ReadDoubleWord(0x20));
            gpio.OnGPIO(0, false);
            pio.WriteDoubleWord(0x38, 1);
            Force(0, 0x4001);
            Force(0, 0x8020);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x20));
            pio.WriteDoubleWord(0x38, 0);
            Force(0, 0x4001);
            Force(0, 0x8020);
            Assert.AreEqual(0x80000000u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldPauseInstructionDelayAndRetainDividerPhaseWhileDisabled()
        {
            Load(0xe721, 0xe022, 0x80a0); // set x,1 [7]; set x,2; pull
            pio.WriteDoubleWord(0xc8, 3u << 16);
            pio.WriteDoubleWord(0, 1);
            Advance(7); // first instruction at3; one delay cycle at6
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            pio.WriteDoubleWord(0, 0);
            Advance(100); // divider keeps its phase, delay counter is paused
            pio.WriteDoubleWord(0, 1);
            Advance(18); // enables108..123 consume remaining6 delay cycles
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            Advance(1); // next instruction at126
            Assert.AreEqual(2u, pio.ReadDoubleWord(0xd4));
        }

        [Test]
        public void ShouldRestartDelayImmediatelyAndKeepClockPhase()
        {
            Load(0xff21, 0xe022, 0x80a0); // set x,1 [31]; set x,2
            pio.WriteDoubleWord(0xc8, 3u << 16);
            pio.WriteDoubleWord(0, 1);
            Advance(7);
            pio.WriteDoubleWord(0x2000, 1u << 4);
            Advance(1);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            Advance(1); // phase-aligned enable at9, with delay cleared
            Assert.AreEqual(2u, pio.ReadDoubleWord(0xd4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldExecuteMetalclawSPIRegmapAddressAndPayload(bool lead)
        {
            const int mosi = 8, miso = 9, sck = 10, cs = 11;
            const uint table = 0x20004000;
            LoadMetalclawSPI(sck, lead);
            gpio.WriteDoubleWord(miso * 8 + 4, 6);
            gpio.OnGPIO(cs, true);
            gpio.OnGPIO(sck, false);
            Advance(2);
            pio.WriteDoubleWord(0xdc, (1u << 26) | (1u << 20) | (mosi << 15) | (miso << 5) | miso);
            pio.WriteDoubleWord(0xcc, (cs << 24) | (10u << 12) | (12u << 7));
            pio.WriteDoubleWord(0xd0, 0);
            pio.WriteDoubleWord(0x10, table >> 11);
            Force(0, 0x80a0);
            Force(0, 0x6040); // out y,32
            Force(0, 0xa0c3); // mov isr,null
            pio.WriteDoubleWord(0xd0, (8u << 25) | (1u << 17));
            pio.WriteByte(0x13, 0); // command-phase filler; payload arrives after RX pointer
            Force(0, 14);
            pio.WriteDoubleWord(0, 1);
            Advance(20);
            gpio.OnGPIO(cs, false);
            Advance(8);
            Assert.AreEqual(0u, SPIByte(0x83, lead, mosi, miso, sck));
            Advance(4); // IN NULL, PUSH, and the following payload OUT
            Assert.AreEqual(table + 0x83u * 8, pio.ReadDoubleWord(0x20));
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(8) & (1u << 24));
            pio.WriteByte(0x13, 0xa6);
            pio.WriteByte(0x13, 0x5a);
            Advance(8);
            Assert.AreEqual(0xa6u, SPIByte(0x17, lead, mosi, miso, sck));
            Assert.AreEqual(0x17u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldAutopullDuringAnInstructionDelayWithoutEndingTheDelay()
        {
            Load(0xef21, 0x80a0, 0xa0c7, 0x8020, 0x80a0); // set x,1 [15]; pull; mov isr,osr; push
            pio.WriteDoubleWord(0xd0, 0xc0000 | (1u << 17));
            pio.WriteDoubleWord(0, 1);
            Advance(2);
            pio.WriteDoubleWord(0x10, 0x12345678);
            Assert.AreEqual(1u, pio.ReadDoubleWord(12) & 15);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(12) & 15);
            Advance(13);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            Advance(3);
            Assert.AreEqual(0x12345678u, pio.ReadDoubleWord(0x20));
        }

        [Test]
        public void ShouldResumeForcedInstructionWithoutWaitingForDividerOrDelay()
        {
            Load(0xff21, 0x0001); // long delay then a self-JMP
            pio.WriteDoubleWord(0xc8, 10u << 16);
            pio.WriteDoubleWord(0, 1);
            Advance(11);
            Force(0, 0x80a0);
            pio.WriteDoubleWord(0x10, 0x713);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            Force(0, 0xa0c7);
            Force(0, 0x8020);
            Assert.AreEqual(0x713u, pio.ReadDoubleWord(0x20));
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
        }

        [Test]
        public void ShouldExecuteAllJMPConditionsAndWrapDecrementAtZero()
        {
            Force(0, 0xe020); // set x,0
            Force(0, 0x0025); // jmp !x,5
            Assert.AreEqual(5u, pio.ReadDoubleWord(0xd4));
            Force(0, 0xe022);
            Force(0, 0x0047);
            Force(0, 0x0048);
            Force(0, 0x0049); // x-- at zero does not branch, still decrements
            Assert.AreEqual(8u, pio.ReadDoubleWord(0xd4));
            Force(0, 0xa0c1);
            Force(0, 0x8020);
            Assert.AreEqual(uint.MaxValue, pio.ReadDoubleWord(0x20));
            Force(0, 0xe040); // set y,0
            Force(0, 0x006a);
            Assert.AreEqual(10u, pio.ReadDoubleWord(0xd4));
            Force(0, 0xe041);
            Force(0, 0x008b);
            Force(0, 0x008c);
            Assert.AreEqual(11u, pio.ReadDoubleWord(0xd4));
            Force(0, 0xa0c2);
            Force(0, 0x8020);
            Assert.AreEqual(uint.MaxValue, pio.ReadDoubleWord(0x20));
            Force(0, 0xe021);
            Force(0, 0xe041);
            Force(0, 0x00ad); // x==y
            Assert.AreEqual(11u, pio.ReadDoubleWord(0xd4));
            Force(0, 0xe042);
            Force(0, 0x00ae); // x!=y
            Assert.AreEqual(14u, pio.ReadDoubleWord(0xd4));
            pio.WriteDoubleWord(0xcc, 7u << 24);
            pio.WriteDoubleWord(0x38, 1u << 7);
            gpio.OnGPIO(7, true);
            Force(0, 0x00cf);
            Assert.AreEqual(15u, pio.ReadDoubleWord(0xd4));
            Force(0, 0x00f0); // OSR is empty
            Assert.AreEqual(15u, pio.ReadDoubleWord(0xd4));
            Force(0, 0xa0e3); // mov osr,null resets its shift counter
            Force(0, 0x00f0);
            Assert.AreEqual(16u, pio.ReadDoubleWord(0xd4));
        }

        [Test]
        public void ShouldExecuteForcedMOVEXECWhileStateMachineIsDisabled()
        {
            pio.WriteDoubleWord(0xc8, 100u << 16);
            pio.WriteDoubleWord(0x10, 0xe02a);
            Force(0, 0x80a0);
            Force(0, 0xa087); // mov exec,osr
            Advance(1);
            Force(0, 0xa0c1);
            Force(0, 0x8020);
            Assert.AreEqual(10u, pio.ReadDoubleWord(0x20));
            Assert.AreEqual(0u, pio.ReadDoubleWord(0));
        }

        [Test]
        public void ShouldSampleIRQFlagsBeforeSimultaneousStateMachineWrites()
        {
            Load(0xc000, 0x80a0, 0x20c0, 0x80a0); // IRQ0; pull; WAIT1 IRQ0; pull
            Force(1, 2);
            pio.WriteDoubleWord(0, 3);
            Advance(1);
            Assert.AreEqual(2u, pio.ReadDoubleWord(0xec)); // SM1 still waiting
            Assert.AreEqual(1u, pio.ReadDoubleWord(0x30));
            Advance(1);
            Assert.AreEqual(3u, pio.ReadDoubleWord(0xec));
            Assert.AreEqual(0u, pio.ReadDoubleWord(0x30));
        }

        [Test]
        public void ShouldReleaseStickyPriorityOnStateMachineRestart()
        {
            gpio.WriteDoubleWord(4, 6);
            pio.WriteDoubleWord(0xdc, 1u << 26);
            pio.WriteDoubleWord(0xf4, 1u << 26);
            pio.WriteDoubleWord(0xcc, 0x1f000 | (1u << 17));
            pio.WriteDoubleWord(0xe4, 0x1f000 | (1u << 17));
            Force(0, 0xe081);
            Force(0, 0xe000);
            Force(1, 0xe001);
            Assert.IsTrue(gpio.GetInput(0));
            pio.WriteDoubleWord(0x2000, 1u << 5);
            Assert.IsFalse(gpio.GetInput(0));
        }

        [Test]
        public void ShouldFreezeAndResumeOnTheSystemClockAndChangeFrequency()
        {
            var clocks = new TestClocks();
            pio = new RP2040_PIO(machine, gpio, clocks);
            Load(0xe321, 0xe022, 0x80a0); // set x,1 [3]; set x,2
            pio.WriteDoubleWord(0, 1);
            Advance(2);
            clocks.SetSystemFrequency(0);
            Advance(100);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            clocks.SetSystemFrequency(2000000);
            Advance(1);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
            ((BaseClockSource)machine.ClockSource).Advance(TimeInterval.FromNanoseconds(500));
            Assert.AreEqual(2u, pio.ReadDoubleWord(0xd4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldRetainSynchronizedPinWakeAcrossClockGating(bool inputBeforeGate)
        {
            var clocks = new TestClocks();
            pio = new RP2040_PIO(machine, gpio, clocks);
            Load(0x2084, 0xe027, 0x80a0);
            pio.WriteDoubleWord(0, 1);
            Advance(1);
            if(inputBeforeGate) gpio.OnGPIO(4, true);
            clocks.SetSystemFrequency(0);
            if(!inputBeforeGate) gpio.OnGPIO(4, true);
            Advance(100);
            clocks.SetSystemFrequency(1000000);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xd4));
            Advance(1);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
        }

        [Test]
        public void ShouldRetainQueuedPinWakeWhenClockDividerRestarts()
        {
            Load(0x2084, 0xe027, 0x80a0);
            pio.WriteDoubleWord(0, 1);
            Advance(1);
            gpio.OnGPIO(4, true);
            pio.WriteDoubleWord(0xc8, 3u << 16);
            pio.WriteDoubleWord(0x2000, 1u << 8);
            Advance(2);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xd4));
            Advance(1);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
        }

        [Test]
        public void ShouldReevaluateWAITPinWhenInputMappingChanges()
        {
            gpio.OnGPIO(5, true);
            Advance(2);
            pio.WriteDoubleWord(0xdc, 4u << 15);
            Force(0, 0x20a0); // wait1 pin0, currently GPIO4
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            pio.WriteDoubleWord(0xdc, 5u << 15);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
        }

        [Test]
        public void ShouldReevaluateStalledPULLWhenAutopullFenceIsEnabled()
        {
            Force(0, 0xa0e3); // mov osr,null: full OSR
            Force(0, 0x80a0); // unconditional PULL still waits for TX data
            Assert.AreNotEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
            pio.WriteDoubleWord(0xd0, 0xc0000 | (1u << 17));
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xcc) & 0x80000000);
        }

        [Test]
        public void ShouldRetainFractionalDividerPhaseThroughAStalledInput()
        {
            Load(0x2084, 0xe027, 0x80a0);
            pio.WriteDoubleWord(0xc8, (1u << 16) | (128u << 8));
            pio.WriteDoubleWord(0, 1);
            Advance(1000000);
            gpio.OnGPIO(4, true);
            Advance(1);
            Assert.AreEqual(0u, pio.ReadDoubleWord(0xd4));
            Advance(1);
            Assert.AreEqual(1u, pio.ReadDoubleWord(0xd4));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ShouldKeepStalledSideSetPriorityAgainstOtherStateMachines(bool directions)
        {
            gpio.WriteDoubleWord(4, 6);
            pio.WriteDoubleWord(0xdc, 1u << 26);
            Force(0, 0xe081);
            pio.WriteDoubleWord(0xf4, 1u << 29);
            pio.WriteDoubleWord(0xe4, 0x1f000 | (directions ? 1u << 29 : 0));
            Load(directions ? (ushort)0xe080 : (ushort)0xe000, 0x0000, 0x3084);
            Force(1, 2);
            pio.WriteDoubleWord(0, 3);
            Advance(1);
            GetOutputState(0, out var value, out var enabled);
            Assert.IsTrue(directions ? enabled : value);
            var changes = 0;
            gpio.DriveChanged += (_, __, ___) => changes++;
            Advance(8);
            GetOutputState(0, out value, out enabled);
            Assert.IsTrue(directions ? enabled : value);
            Assert.AreEqual(0, changes, "A stalled higher SM still asserts side-set on every clock enable");
        }

        [Test]
        public void ShouldRestoreStalledSideSetAtItsNextDividerEnable()
        {
            gpio.WriteDoubleWord(4, 6);
            pio.WriteDoubleWord(0xdc, 1u << 26);
            Force(0, 0xe081);
            pio.WriteDoubleWord(0xf4, 1u << 29);
            pio.WriteDoubleWord(0xe0, 3u << 16);
            Load(0xe000, 0x0001, 0x3084);
            Force(1, 2);
            pio.WriteDoubleWord(0, 3);
            Advance(3);
            Assert.IsTrue(gpio.GetInput(0));
            Advance(1);
            Force(0, 0xe000); // between SM1's enables at3 and6
            Assert.IsFalse(gpio.GetInput(0));
            Advance(1);
            Assert.IsFalse(gpio.GetInput(0));
            Advance(1);
            Assert.IsTrue(gpio.GetInput(0));
            Advance(1000000); // the correction returns to an event-driven stall
            Assert.IsTrue(gpio.GetInput(0));
        }

        private uint SPIByte(uint value, bool lead, int mosi, int miso, int sck)
        {
            uint result = 0;
            for(var bit = 7; bit >= 0; bit--)
            {
                gpio.OnGPIO(mosi, ((value >> bit) & 1) != 0);
                gpio.OnGPIO(sck, lead);
                Advance(6);
                gpio.OnGPIO(sck, !lead);
                result = (result << 1) | (gpio.GetInput(miso) ? 1u : 0u);
                Advance(6);
            }
            return result;
        }

        private void LoadMetalclawSPI(int sck, bool lead)
        {
            var firstWait = (ushort)(0x2000 | (lead ? 0x80 : 0) | sck);
            var secondWait = (ushort)(0x2000 | (lead ? 0 : 0x80) | sck);
            Load(0x6001, 0x00c3, 0x0001, 0x00c3, 0xe081, 0xe027,
                firstWait, secondWait, 0x4001, 0x6001, 0x0046, 0xe027,
                0x8000, 0x0005, 0x8080, 0x00cf, 0x4055, 0xe081,
                firstWait, secondWait, 0x4001, 0x6001, 0x00f2, 0x4063,
                0x8020, 0xa042, 0x6001, 0x0005, 0xa042, 0x2020,
                0x20a0, 0xc010);
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
        private sealed class TestClocks : IRP2ClockSource
        {
            public ulong SystemFrequency { get; private set; } = 1000000;
            public ulong PeripheralFrequency => SystemFrequency;
            public ulong USBFrequency => 48000000;
            public event Action ClockChanged;
            public void SetSystemFrequency(ulong frequency)
            {
                SystemFrequency = frequency;
                ClockChanged?.Invoke();
            }
        }

        private const int ClockPin = 2;
        private const int DataPin = 3;
        private Machine machine;
        private RP2_GPIOBank0 gpio;
        private RP2040_PIO pio;
    }
}
