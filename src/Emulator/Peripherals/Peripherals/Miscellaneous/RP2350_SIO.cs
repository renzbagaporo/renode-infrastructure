//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.GPIOPort;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public sealed class RP2350_SIO : BaseGPIOPort, IDoubleWordPeripheral, IKnownSize, IRP2GPIO
    {
        public RP2350_SIO(IMachine machine, RP2350_AccessControl accessControl = null, bool emulateErratumE2 = true) : base(machine, 64)
        {
            this.machine = machine;
            this.accessControl = accessControl;
            this.emulateErratumE2 = emulateErratumE2;
            for(var bank = 0; bank < 2; bank++) for(var core = 0; core < 2; core++) receive[bank, core] = new Queue<uint>();
            Reset();
        }

        public uint ReadDoubleWord(long offset) => Read(offset, false);
        public void WriteDoubleWord(long offset, uint value) => Write(offset, value, false);
        [ConnectionRegion("NonSecure")]
        public uint ReadDoubleWordNonSecure(long offset) => Read(offset, true);
        [ConnectionRegion("NonSecure")]
        public void WriteDoubleWordNonSecure(long offset, uint value) => Write(offset, value, true);

        public override void OnGPIO(int number, bool value)
        {
            lock(sync) base.OnGPIO(number, value);
        }

        public override void Reset()
        {
            lock(sync)
            {
                Array.Clear(State, 0, State.Length);
                gpioOutput = gpioEnable = 0;
                Array.Clear(spinlocks, 0, spinlocks.Length);
                Array.Clear(errors, 0, errors.Length);
                Array.Clear(doorbells, 0, doorbells.Length);
                Array.Clear(peripheralNonSecure, 0, peripheralNonSecure.Length);
                for(var bank = 0; bank < 2; bank++) for(var core = 0; core < 2; core++) receive[bank, core].Clear();
                pending = gpioChanged = true;
            }
            Publish();
        }

        public ulong Output { get { lock(sync) return gpioOutput; } }
        public ulong OutputEnable { get { lock(sync) return gpioEnable; } }
        public long Size => 0x1000;
        public GPIO Core0IRQ { get; } = new GPIO();
        public GPIO Core1IRQ { get; } = new GPIO();
        public GPIO Core0NonSecureIRQ { get; } = new GPIO();
        public GPIO Core1NonSecureIRQ { get; } = new GPIO();
        public GPIO Core0DoorbellIRQ { get; } = new GPIO();
        public GPIO Core1DoorbellIRQ { get; } = new GPIO();
        public GPIO Core0NonSecureDoorbellIRQ { get; } = new GPIO();
        public GPIO Core1NonSecureDoorbellIRQ { get; } = new GPIO();
        public event Action GPIOChanged;

        private uint Read(long offset, bool mirror)
        {
            var bank = Bank(mirror, out var core);
            try
            {
                lock(sync)
                {
                    if(offset >= 0x100 && offset < 0x180 && offset % 4 == 0)
                    {
                        var mask = 1u << (int)((offset - 0x100) / 4);
                        if((spinlocks[bank] & mask) != 0) return 0;
                        spinlocks[bank] |= mask;
                        return mask;
                    }
                    var gpioMask = GPIOMask(bank);
                    switch(offset)
                    {
                    case 0: return (uint)core;
                    case 4: return (uint)(Inputs & gpioMask);
                    case 8: return (uint)((Inputs & gpioMask) >> 32);
                    case 0x10: return (uint)(gpioOutput & gpioMask);
                    case 0x14: return (uint)((gpioOutput & gpioMask) >> 32);
                    case 0x30: return (uint)(gpioEnable & gpioMask);
                    case 0x34: return (uint)((gpioEnable & gpioMask) >> 32);
                    case 0x50:
                        return errors[bank, core] | (receive[bank, core].Count != 0 ? 1u : 0)
                            | (receive[bank, core ^ 1].Count < 4 ? 2u : 0);
                    case 0x58:
                        pending = true;
                        if(receive[bank, core].Count != 0) return receive[bank, core].Dequeue();
                        errors[bank, core] |= 8;
                        return 0;
                    case 0x5c: return spinlocks[bank];
                    case 0x180: case 0x184: return doorbells[bank, core ^ 1];
                    case 0x188: case 0x18c: return doorbells[bank, core];
                    case 0x190:
                        if(bank != 0) throw new BusAccessException(BusAccessError.CommandError);
                        return peripheralNonSecure[core];
                    default:
                        if(offset >= 0x18 && offset <= 0x4c) return 0; // GPIO atomic write-only aliases
                        this.LogUnhandledRead(offset); return 0;
                    }
                }
            }
            finally { Publish(); }
        }

        private void Write(long offset, uint value, bool mirror)
        {
            var bank = Bank(mirror, out var core);
            try
            {
                lock(sync)
                {
                    if(offset >= 0x100 && offset < 0x180 && offset % 4 == 0)
                    {
                        spinlocks[bank] &= ~(1u << (int)((offset - 0x100) / 4));
                        return;
                    }
                    if(emulateErratumE2 && offset >= 0x180 && offset < 0x200 && offset % 4 == 0)
                        spinlocks[bank] &= ~(1u << (int)((offset - 0x180) / 4));
                    if(offset >= 0x10 && offset <= 0x4c && offset % 4 == 0)
                    {
                        var shift = (offset & 4) != 0 ? 32 : 0;
                        var mask = GPIOMask(bank) & (0xffffffffUL << shift);
                        var data = (ulong)value << shift;
                        var enable = offset >= 0x30;
                        var previous = enable ? gpioEnable : gpioOutput;
                        switch((offset & 0x1f) >> 3)
                        {
                        case 2: previous = (previous & ~mask) | (data & mask); break;
                        case 3: previous |= data & mask; break;
                        case 0: previous &= ~(data & mask); break;
                        case 1: previous ^= data & mask; break;
                        }
                        if(enable) gpioEnable = previous; else gpioOutput = previous;
                        gpioChanged = pending = true;
                        return;
                    }
                    switch(offset)
                    {
                    case 0x50: errors[bank, core] &= ~(value & 0xc); pending = true; return;
                    case 0x54:
                        if(receive[bank, core ^ 1].Count < 4) receive[bank, core ^ 1].Enqueue(value);
                        else errors[bank, core] |= 4;
                        pending = true; return;
                    case 0x180: doorbells[bank, core ^ 1] |= value & 0xff; pending = true; return;
                    case 0x184: doorbells[bank, core ^ 1] &= ~(value & 0xff); pending = true; return;
                    case 0x188: doorbells[bank, core] |= value & 0xff; pending = true; return;
                    case 0x18c: doorbells[bank, core] &= ~(value & 0xff); pending = true; return;
                    case 0x190:
                        if(bank != 0) throw new BusAccessException(BusAccessError.CommandError);
                        peripheralNonSecure[core] = value & 0x23;
                        return;
                    default: this.LogUnhandledWrite(offset, value); return;
                    }
                }
            }
            finally { Publish(); }
        }

        private int Bank(bool mirror, out int core)
        {
            core = 0;
            var bus = machine.GetSystemBus(this);
            var secure = true;
            if(bus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu)
            {
                core = (int)(cpu.MultiprocessingId & 1);
                cpu.SyncTime();
                if(cpu is CortexM cortex && cortex.TrustZoneEnabled) secure = cortex.SecureState;
            }
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)) secure = state.AttributionSecure;
            if(accessControl != null && accessControl.CoreIsForcedNonSecure(core)) secure = false;
            if(mirror && !secure) throw new BusAccessException(BusAccessError.CommandError);
            return mirror || !secure ? 1 : 0;
        }

        private ulong GPIOMask(int bank) => bank == 0 ? ImplementedGPIOs
            : accessControl == null ? 0 : (accessControl.GPIONonSecureMask0 | ((ulong)accessControl.GPIONonSecureMask1 << 32)) & ImplementedGPIOs;
        private ulong Inputs
        {
            get
            {
                ulong result = 0;
                for(var i = 0; i < 64; i++) if(State[i]) result |= 1UL << i;
                return result & ImplementedGPIOs;
            }
        }

        private void Publish()
        {
            lock(sync)
            {
                if(publishing) return;
                publishing = true;
            }
            try
            {
                while(true)
                {
                    bool change;
                    ulong output;
                    var fifo = new bool[4]; var bell = new bool[4];
                    lock(sync)
                    {
                        if(!pending) { publishing = false; return; }
                        pending = false;
                        change = gpioChanged; gpioChanged = false;
                        output = gpioOutput;
                        for(var bank = 0; bank < 2; bank++) for(var core = 0; core < 2; core++)
                        {
                            fifo[bank * 2 + core] = receive[bank, core].Count != 0 || errors[bank, core] != 0;
                            bell[bank * 2 + core] = doorbells[bank, core] != 0;
                        }
                    }
                    Core0IRQ.Set(fifo[0]); Core1IRQ.Set(fifo[1]);
                    Core0NonSecureIRQ.Set(fifo[2]); Core1NonSecureIRQ.Set(fifo[3]);
                    Core0DoorbellIRQ.Set(bell[0]); Core1DoorbellIRQ.Set(bell[1]);
                    Core0NonSecureDoorbellIRQ.Set(bell[2]); Core1NonSecureDoorbellIRQ.Set(bell[3]);
                    if(change)
                    {
                        for(var pin = 0; pin < 64; pin++) Connections[pin].Set((output & (1UL << pin)) != 0);
                        GPIOChanged?.Invoke();
                    }
                }
            }
            catch { lock(sync) publishing = false; throw; }
        }

        private readonly IMachine machine;
        private readonly RP2350_AccessControl accessControl;
        private readonly bool emulateErratumE2;
        private readonly object sync = new object();
        private readonly Queue<uint>[,] receive = new Queue<uint>[2, 2];
        private readonly uint[,] errors = new uint[2, 2];
        private readonly uint[,] doorbells = new uint[2, 2];
        private readonly uint[] spinlocks = new uint[2];
        private readonly uint[] peripheralNonSecure = new uint[2];
        private ulong gpioOutput;
        private ulong gpioEnable;
        private bool pending;
        private bool gpioChanged;
        private bool publishing;
        private const ulong ImplementedGPIOs = 0xff00ffffffffffffUL;
    }
}
