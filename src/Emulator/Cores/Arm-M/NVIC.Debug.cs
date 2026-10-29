//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.IRQControllers
{
    public partial class NVIC
    {
        // Enable only on platforms providing the external debug hardware.
        public bool EnableMemoryMappedDebug { get; set; }

        public bool ExternalDebugEnabled
        {
            get { lock(debugSync) return EnableMemoryMappedDebug && (debugControl & 1) != 0; }
        }

        public bool ExternalDebugMaskInterrupts
        {
            get { lock(debugSync) return EnableMemoryMappedDebug && (debugControl & 9) == 9; }
        }

        public bool ExternalDebugStepRequested
        {
            get { return TryGetExternalDebugStepRequest(out var _); }
        }

        public uint ExternalDebugRequestGeneration
        {
            get { lock(debugSync) return debugRequestGeneration; }
        }

        public bool ExternalDebugHaltRequested
        {
            get { lock(debugSync) return EnableMemoryMappedDebug && (debugControl & 3) == 3; }
        }

        public bool TryGetExternalDebugStepRequest(out uint generation)
        {
            lock(debugSync)
            {
                generation = debugRequestGeneration;
                return EnableMemoryMappedDebug && (debugControl & 7) == 5;
            }
        }

        public bool ReportExternalDebugStep(uint generation)
        {
            lock(debugSync)
            {
                if(generation != debugRequestGeneration || !EnableMemoryMappedDebug || (debugControl & 7) != 5) return false;
                debugFaultStatus |= 1;
                debugControl |= 2;
                return true;
            }
        }

        public bool CatchCoreReset
        {
            get { lock(debugSync) return EnableMemoryMappedDebug && (debugControl & debugExceptionControl & 1) != 0; }
        }

        public bool HandleExternalDebugReset()
        {
            lock(debugSync)
            {
                var catchReset = CatchCoreReset;
                debugRequestGeneration++;
                debugResetStatus = true;
                debugRestartStatus = false;
                debugData = 0;
                debugFaultStatus = catchReset ? 8u : 0;
                debugControl = (debugControl & ~2u) | (catchReset ? 2u : 0);
                return catchReset;
            }
        }

        public void ReportExternalDebugBreakpoint()
        {
            lock(debugSync)
            {
                debugRequestGeneration++;
                debugFaultStatus |= 2;
                debugControl |= 2;
            }
        }

        private bool ReadDebugRegister(long offset, out uint value)
        {
            value = 0;
            switch(offset)
            {
            case 0xd30:
                lock(debugSync) value = debugFaultStatus;
                return true;
            case 0xdf0:
                lock(debugSync)
                {
                    value = debugControl;
                    if(cpu.HaltedByDebugger) value |= 0x30000;
                    if(InSleep.IsSet) value |= 0x40000;
                    if(Lockup.IsSet) value |= 0x80000;
                    if(cpu.TrustZoneEnabled) value |= 0x100000;
                    if(debugResetStatus) value |= 0x2000000;
                    if(debugRestartStatus) value |= 0x4000000;
                    debugResetStatus = debugRestartStatus = false;
                }
                return true;
            case 0xdf4: return true; // DCRSR is write-only.
            case 0xdf8: value = debugData; return true;
            case 0xdfc:
                lock(debugSync) value = debugExceptionControl;
                return true;
            case 0xfbc: value = 0x47702a04; return true; // Armv8-M processor debug
            case 0xfd0: value = 4; return true;
            case 0xfe0: value = 0x21; return true;
            case 0xfe4: value = 0xbd; return true;
            case 0xfe8: value = 0xb; return true;
            case 0xfec: return true;
            case 0xff0: value = 0xd; return true;
            case 0xff4: value = 0x90; return true;
            case 0xff8: value = 5; return true;
            case 0xffc: value = 0xb1; return true;
            default: return false;
            }
        }

        private bool WriteDebugRegister(long offset, uint value)
        {
            switch(offset)
            {
            case 0xd30:
                lock(debugSync) debugFaultStatus &= ~(value & 0x1f);
                return true;
            case 0xdf0:
                if((value >> 16) != 0xa05f) return true;
                var wasHalted = cpu.HaltedByDebugger;
                lock(debugSync)
                {
                    debugRequestGeneration++;
                    debugControl = value & 0x2f;
                    if(!wasHalted && (value & 3) == 3) debugFaultStatus |= 1;
                    if(wasHalted && (value & 3) != 3) debugRestartStatus = true;
                }
                // Re-evaluate IRQ eligibility before releasing the PE. No debug
                // or IRQ lock is held while the CPU acknowledges halt/resume.
                FindPendingInterrupt();
                cpu.HaltedByDebugger = (value & 3) == 3;
                return true;
            case 0xdf4:
                if((debugControl & 1) == 0 || !cpu.HaltedByDebugger) return true;
                var selector = (int)(value & 0x7f);
                var write = (value & 0x10000) != 0;
                if(selector == 20)
                {
                    // CONTROL, FAULTMASK, BASEPRI, PRIMASK share the CFBP transfer.
                    if(write)
                    {
                        cpu.SetRegister((int)CortexMRegisters.PRIMASK, debugData & 1);
                        cpu.SetRegister((int)CortexMRegisters.BasePri, (debugData >> 8) & 0xff);
                        cpu.SetRegister((int)CortexMRegisters.FAULTMASK, (debugData >> 16) & 1);
                        cpu.SetRegister((int)CortexMRegisters.Control, (debugData >> 24) & 7);
                    }
                    else debugData = (uint)cpu.GetRegister((int)CortexMRegisters.PRIMASK)
                        | ((uint)cpu.GetRegister((int)CortexMRegisters.BasePri) << 8)
                        | ((uint)cpu.GetRegister((int)CortexMRegisters.FAULTMASK) << 16)
                        | ((uint)cpu.GetRegister((int)CortexMRegisters.Control) << 24);
                    return true;
                }
                var register = DebugRegisterIndex(selector);
                if(register < 0) return true;
                if(write) cpu.SetRegister(register, debugData);
                else debugData = (uint)cpu.GetRegister(register);
                return true;
            case 0xdf8: debugData = value; return true;
            case 0xdfc:
                lock(debugSync) debugExceptionControl = value & 0x01000001;
                return true;
            default: return false;
            }
        }

        private static int DebugRegisterIndex(int selector)
        {
            if(selector <= 15) return selector;
            if(selector >= 64 && selector <= 95) return (int)CortexMRegisters.S0 + selector - 64;
            switch(selector)
            {
            case 16: return (int)CortexMRegisters.CPSR;
            case 17: return (int)CortexMRegisters.MSPCURR;
            case 18: return (int)CortexMRegisters.PSPCURR;
            case 24: return (int)CortexMRegisters.MSPNS;
            case 25: return (int)CortexMRegisters.PSPNS;
            case 26: return (int)CortexMRegisters.MSPS;
            case 27: return (int)CortexMRegisters.PSPS;
            case 33: return (int)CortexMRegisters.FPSCR;
            default: return -1;
            }
        }

        private uint debugControl, debugData, debugFaultStatus, debugExceptionControl;
        private uint debugRequestGeneration;
        private readonly object debugSync = new object();
        private bool debugResetStatus, debugRestartStatus;
    }
}
