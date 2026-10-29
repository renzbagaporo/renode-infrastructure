//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    [GPIO(NumberOfInputs = 6)]
    public sealed class RP2040_QSPI : RP2APBPeripheral, IGPIOReceiver, INumberedGPIOOutput
    {
        public RP2040_QSPI(IMachine machine, IRP2GPIO sio = null, RP2040_SSI ssi = null) : base(machine)
        {
            this.ssi = ssi;
            bank = new RP2_GPIOBank0(machine, sio == null ? null : new QSPISIO(sio), numberOfPins: 6);
            var connections = new Dictionary<int, IGPIO>();
            for(var pin = 0; pin < 6; pin++)
            {
                connections.Add(pin, bank.Connections[pin]);
            }
            Connections = new ReadOnlyDictionary<int, IGPIO>(connections);
            Reset();
        }

        public void OnGPIO(int number, bool value) => bank.OnGPIO(number, value);

        public void ReleaseInput(int number) => bank.ReleaseInput(number);

        public void SetPadConfiguration(int pin, uint value) => bank.SetPadConfiguration(pin, value);

        public void SetPeripheralOutput(int pin, bool value, bool enabled) => bank.SetPeripheralOutput(0, pin, value, enabled);

        public bool GetInput(int pin) => bank.GetInput(pin);

        public override void Reset()
        {
            lock(SyncRoot)
            {
                ResetBank();
            }
            PublishPending();
        }

        private void ResetBank()
        {
            bank.Reset();
            for(var pin = 0; pin < 6; pin++)
            {
                bank.WriteDoubleWord(pin * 8 + 4, 0);
            }
            bank.WriteDoubleWord(0xf0, 0x00cccccc);
            UpdateChipSelect();
        }

        public IReadOnlyDictionary<int, IGPIO> Connections { get; }

        public GPIO Core0IRQ => bank.Core0IRQ;

        public GPIO Core1IRQ => bank.Core1IRQ;

        public GPIO DormantWakeIRQ => bank.DormantWakeIRQ;

        protected override uint ReadRegister(long offset)
        {
            if(TryTranslate(offset, out var bankOffset))
            {
                var value = bank.ReadDoubleWord(bankOffset);
                return ResetHeld && offset == 0x30 ? value & 0x00333333 : value;
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        protected override void WriteRegister(long offset, uint value, AtomicOperation operation)
        {
            if(TryTranslate(offset, out var bankOffset))
            {
                bank.WriteDoubleWord(bankOffset | ((long)operation << 12), value);
                if(offset == 0xc)
                {
                    UpdateChipSelect();
                }
                return;
            }
            this.LogUnhandledWrite(offset, value);
        }

        protected override void OnResetHeldChanged()
        {
            if(!ResetHeld)
            {
                bank.WriteDoubleWord(0xf0, 0x00cccccc);
            }
        }

        protected override object SyncRoot => bank?.SynchronizationRoot ?? base.SyncRoot;

        protected override void OnAccessCompleted() => PublishPending();

        private void UpdateChipSelect()
        {
            if(ssi != null)
            {
                pendingChipSelectOverrides.Enqueue((bank.ReadDoubleWord(0xc) >> 8) & 3);
            }
        }

        internal object SynchronizationRoot => SyncRoot;

        internal void PublishPending()
        {
            if(Monitor.IsEntered(SyncRoot))
            {
                return;
            }
            bank.PublishPending();
            lock(SyncRoot)
            {
                if(publishing)
                {
                    return;
                }
                publishing = true;
            }
            try
            {
                while(true)
                {
                    uint value;
                    lock(SyncRoot)
                    {
                        if(pendingChipSelectOverrides.Count == 0)
                        {
                            publishing = false;
                            return;
                        }
                        value = pendingChipSelectOverrides.Dequeue();
                    }
                    ssi.ChipSelectOverride = value;
                }
            }
            catch
            {
                lock(SyncRoot)
                {
                    publishing = false;
                }
                throw;
            }
        }

        private static bool TryTranslate(long offset, out long bankOffset)
        {
            bankOffset = offset;
            if(offset < 0x30)
            {
                return true;
            }
            // One raw interrupt word followed by enable, force and status
            // words for each core and the dormant wake destination.
            if(offset <= 0x54 && (offset & 3) == 0)
            {
                bankOffset = 0xf0 + (offset - 0x30) * 4;
                return true;
            }
            return false;
        }

        private readonly RP2_GPIOBank0 bank;
        private readonly RP2040_SSI ssi;
        private readonly Queue<uint> pendingChipSelectOverrides = new Queue<uint>();
        private bool publishing;

        private sealed class QSPISIO : IRP2GPIO
        {
            public QSPISIO(IRP2GPIO sio)
            {
                this.sio = sio;
            }

            public void Reset()
            {
                // SIO has its own reset domain.
            }

            public ulong Output => sio.Output >> 30;

            public ulong OutputEnable => sio.OutputEnable >> 30;

            public event Action GPIOChanged
            {
                add => sio.GPIOChanged += value;
                remove => sio.GPIOChanged -= value;
            }

            public void OnGPIO(int number, bool value) => sio.OnGPIO(number + 30, value);

            private readonly IRP2GPIO sio;
        }
    }
}
