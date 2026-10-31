//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Utilities.Binding;

namespace Antmicro.Renode.Peripherals.CPU
{
    // RP2350 has 52 external interrupt inputs, followed by the standard local
    // software and timer inputs at 52 and 53. Hart IDs identify physical PEs;
    // the SystemBus engine registration slots are independent identifiers.
    public sealed class RP2350_Hazard3 : RiscV32, ISystemEventReceiver, IPeripheralWithTransactionState
    {
        public RP2350_Hazard3(IMachine machine, uint hartId = 0, RP2350_Clocks clocks = null, bool architectureEnabled = false)
            : base(machine, "rv32imac_zicsr_zifencei_zba_zbb_zbs_zbkb_zcb_zcmp", hartId: hartId,
                privilegedArchitecture: PrivilegedArchitecture.Priv1_11,
                interruptMode: InterruptMode.Direct, privilegeLevels: PrivilegeLevels.MachineUser,
                pmpEntryCount: 8, pmpNumberOfAddrBits: 30, minimalPmpNapotInBytes: 32)
        {
            TlibHazard3Enable();
            ResetVector = 0x7dfc;
            architectureHeld = !architectureEnabled;
            UpdateRequestedHaltedState();
            if(clocks != null)
            {
                clocks.ClockChanged += () => machine.LocalTimeSource.ExecuteInNearestSyncedState(_ =>
                    Clocked = clocks.SystemFrequency != 0);
            }
        }

        public override void OnGPIO(int number, bool value)
        {
            if(number >= 0 && number < 52)
            {
                TlibHazard3SetIrq((uint)number, value ? 1u : 0u);
                InterruptExecutionSleep();
            }
            else if(number == 52 || number == 53)
            {
                if(number == 52) softwareInterruptLevel = value;
                else timerInterruptLevel = value;
                base.OnGPIO(number == 52 ? 3 : 7, value);
            }
        }

        public override void Reset()
        {
            base.Reset();
            // The SIO's local interrupt outputs belong to a separate domain.
            if(softwareInterruptLevel) base.OnGPIO(3, true);
            if(timerInterruptLevel) base.OnGPIO(7, true);
        }

        public void ReceiveSystemEvent()
        {
            TlibHazard3ReceiveEvent();
            RequestReturn();
            InterruptExecutionSleep();
        }

        public IReadOnlyDictionary<string, int> StateBits { get; } = new Dictionary<string, int>
        {
            ["privileged"] = 0,
            ["instructionFetch"] = 1,
        };

        public bool TryConvertUlongToStateObj(ulong? state, out IContextState stateObj)
        {
            stateObj = state.HasValue ? new ContextState {
                Privileged = (state.Value & 1) != 0,
                InstructionFetch = (state.Value & 2) != 0,
            } : null;
            return state.HasValue;
        }

        public bool TryConvertStateObjToUlong(IContextState stateObj, out ulong? state)
        {
            state = stateObj is ContextState context ?
                (context.Privileged ? 1UL : 0) | (context.InstructionFetch ? 2UL : 0) : (ulong?)null;
            return state.HasValue;
        }

        public sealed class ContextState : IContextState
        {
            public bool Privileged;
            public bool InstructionFetch;
        }

        protected override void HandleBusAccessError(ulong address, SysbusAccessWidth width,
            BusAccess.Operation operation, BusAccessError error, ulong cpuState)
        {
            TlibHazard3ReportBusFault(checked((uint)address), (cpuState & 2) != 0 ? 2u :
                operation == BusAccess.Operation.Write ? 1u : 0u);
        }

        public bool ArchitectureEnabled => !architectureHeld;

        // These setters reconcile reset inputs only at a synchronized boundary.
        internal void SetArchitectureResetHold(bool held)
        {
            architectureHeld = held;
            UpdateRequestedHaltedState();
        }

        internal void SetExternalResetHold(bool held)
        {
            externalHeld = held;
            UpdateRequestedHaltedState();
        }

        internal void SetPowerSequencerResetHold(bool held)
        {
            sequencerHeld = held;
            UpdateRequestedHaltedState();
        }

        protected override bool IsHeldInReset => architectureHeld || externalHeld || sequencerHeld;

#pragma warning disable 649
        [Import]
        private readonly Action TlibHazard3Enable;
        [Import]
        private readonly Action TlibHazard3ReceiveEvent;
        [Import]
        private readonly Action<uint, uint> TlibHazard3SetIrq;
        [Import]
        private readonly Action<uint, uint> TlibHazard3ReportBusFault;
#pragma warning restore 649

        private bool softwareInterruptLevel;
        private bool timerInterruptLevel;
        private volatile bool architectureHeld;
        private volatile bool externalHeld;
        private volatile bool sequencerHeld;
    }
}
