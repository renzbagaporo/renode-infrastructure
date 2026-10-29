//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Sampling timing and the entropy holding register are modeled separately.
    // Entropy uses Renode's seeded PRNG, for reproducible emulation; this is not
    // a cryptographically secure source for guest applications.
    public sealed class RP2350_TRNG : RP2APBPeripheral
    {
        public RP2350_TRNG(IMachine machine, RP2350_Clocks clocks = null, ulong frequency = 6000000) : base(machine)
        {
            this.clocks = clocks;
            timer = new LimitTimer(machine.ClockSource, frequency, this, "entropy collection", limit: 192,
                workMode: WorkMode.OneShot, eventEnabled: true);
            timer.LimitReached += CollectEntropy;
            if(clocks != null) clocks.ClockChanged += UpdateClock;
            Reset();
        }

        public override void Reset()
        {
            timer.Reset();
            mask = 0xf;
            status = 0;
            configuration = 0;
            debugControl = 0;
            sampleCount = 0xffff;
            enabled = valid = false;
            Array.Clear(entropy, 0, entropy.Length);
            IRQ.Unset();
            UpdateClock();
        }

        public GPIO IRQ { get; } = new GPIO();

        protected override void OnBeforeAccess()
        {
            if(machine.SystemBus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state) && state.DebugAccess) return;
            if(machine.SystemBus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        protected override uint ReadRegister(long offset)
        {
            if(offset >= 0x114 && offset <= 0x128 && offset % 4 == 0)
            {
                var result = entropy[(offset - 0x114) / 4];
                if(offset == 0x128 && valid) StartSampling();
                return result;
            }
            switch(offset)
            {
            case 0x100: return mask;
            case 0x104: return status;
            case 0x10c: return configuration;
            case 0x110: return valid ? 1u : 0;
            case 0x12c: return enabled ? 1u : 0;
            case 0x130: return sampleCount;
            case 0x134: return 0;
            case 0x138: return debugControl;
            case 0x140: return 0;
            case 0x1b4: return 0;
            case 0x1b8: return timer.Enabled ? 1u : 0;
            case 0x1c0: return 0;
            default:
                this.LogUnhandledRead(offset);
                return 0;
            }
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            switch(offset)
            {
            case 0x100: mask = ApplyOperation(mask, value, operation, 0xf); break;
            case 0x108: status &= ~(value & 0xd); break;
            case 0x10c: configuration = ApplyOperation(configuration, value, operation, 3); break;
            case 0x12c:
                var wasEnabled = enabled;
                enabled = (ApplyOperation(enabled ? 1u : 0, value, operation, 1) & 1) != 0;
                if(enabled && !wasEnabled) StartSampling();
                if(!enabled) timer.Enabled = false;
                break;
            case 0x130:
                sampleCount = ApplyOperation(sampleCount, value, operation);
                if(enabled) StartSampling();
                break;
            case 0x134: break;
            case 0x138: debugControl = ApplyOperation(debugControl, value, operation, 0xe); break;
            case 0x140: if((value & 1) != 0) Reset(); break;
            case 0x1bc:
                if(!enabled) { valid = false; status &= ~1u; }
                break;
            default: this.LogUnhandledWrite(offset, value); break;
            }
            IRQ.Set((status & ~mask) != 0);
        }

        private void StartSampling()
        {
            valid = false;
            timer.Enabled = false;
            if(!enabled) return;
            // One raw bit per sampling interval, or at least two source samples
            // per Von Neumann output bit when the decorrelator is enabled.
            var samples = (debugControl & 2) != 0 ? 192UL : 768UL;
            timer.Limit = samples * ((ulong)sampleCount + 1);
            timer.ResetValue();
            timer.Enabled = clocks == null || clocks.SystemFrequency != 0;
        }

        private void CollectEntropy()
        {
            var rng = EmulationManager.Instance.CurrentEmulation.RandomGenerator;
            for(var i = 0; i < entropy.Length; i++) entropy[i] = (uint)rng.Next() | ((uint)(rng.Next() & 1) << 31);
            valid = true;
            status |= 1;
            IRQ.Set((status & ~mask) != 0);
        }

        private void UpdateClock()
        {
            if(clocks == null) return;
            if(clocks.SystemFrequency != 0) timer.Frequency = clocks.SystemFrequency;
            timer.Enabled = enabled && !valid && clocks.SystemFrequency != 0;
        }

        private readonly LimitTimer timer;
        private readonly RP2350_Clocks clocks;
        private readonly uint[] entropy = new uint[6];
        private uint mask;
        private uint status;
        private uint configuration;
        private uint sampleCount;
        private uint debugControl;
        private bool enabled;
        private bool valid;
    }
}
