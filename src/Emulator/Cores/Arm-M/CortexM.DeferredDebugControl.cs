// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.Threading.Tasks;

using Antmicro.Migrant;
using Antmicro.Renode.Peripherals.IRQControllers;
using Antmicro.Renode.Utilities.Binding;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        internal void RequestExternalDebugControl()
        {
            // Called from MMIO and native callbacks. Neither may join the
            // CPU: a bus access or an external Pause can be awaiting it.
            TlibSetReturnRequest();
            lock(debugWorkerSync)
            {
                if(debugWorkerDisposed) return;
                debugWorkerRevision++;
                if(debugWorker == null)
                {
                    debugWorker = Task.Run((System.Action)ApplyExternalDebugControl);
                }
            }
        }

        public override void Dispose()
        {
            Task worker;
            lock(debugWorkerSync)
            {
                debugWorkerDisposed = true;
                worker = debugWorker;
            }
            // The worker never owns its scheduler monitor while joining a PE.
            worker?.GetAwaiter().GetResult();
            base.Dispose();
        }

        private void ApplyExternalDebugControl()
        {
            while(true)
            {
                ulong revision;
                lock(debugWorkerSync)
                {
                    if(debugWorkerDisposed) { debugWorker = null; return; }
                    revision = debugWorkerRevision;
                }
                if(nvic.GetExternalDebugControlRequest(out var generation, out var halt))
                {
                    if(halt)
                    {
                        // Joining is outside reset: an in-flight native RCP
                        // callback may need that monitor before it can return.
                        if(nvic.BeginExternalDebugControl(generation, true))
                        {
                            base.HaltedByDebugger = true;
                            UpdateRequestedHaltedState();
                            uint previousState;
                            lock(reset) previousState = tlibEnterHaltingDebug();
                            // Hooks can call external code. Publish the actual
                            // wake/Lockup exit outside all reset/debug monitors.
                            if((previousState & 1) != 0) NotifySleepExitFromDebugStop();
                            if((previousState & 2) != 0) nvic.SetLockupState(false);
                            lock(reset) nvic.CompleteExternalDebugControl(generation, true);
                        }
                    }
                    else
                    {
                        // Releasing a PE and register transfers serialize with
                        // reset. This path only resumes, and never joins it.
                        lock(reset)
                        {
                            if(nvic.BeginExternalDebugControl(generation, false))
                            {
                                // Restore interrupt delivery before releasing
                                // the PE when C_MASKINTS has been cleared.
                                nvic.FindPendingInterrupt();
                                base.HaltedByDebugger = false;
                                nvic.CompleteExternalDebugControl(generation, false);
                            }
                        }
                    }
                    while(nvic.TryDequeueExternalDebugRegisterTransfer(out var transfer))
                    {
                        ApplyExternalDebugRegisterTransfer(transfer);
                    }
                    nvic.FindPendingInterrupt();
                }
                lock(debugWorkerSync)
                {
                    if(debugWorkerRevision != revision) continue;
                    debugWorker = null;
                    return;
                }
            }
        }

        private void ApplyExternalDebugRegisterTransfer(NVIC.DebugRegisterTransfer transfer)
        {
            // Reset uses the same monitor. Native registers are accessed only
            // after a completed halt, and stale reset/resume requests are ignored.
            lock(reset)
            {
                if(!nvic.CanApplyExternalDebugRegisterTransfer(transfer.Generation)) return;
                var value = transfer.Value;
                if(transfer.Selector == 20)
                {
                    if(transfer.Write)
                    {
                        SetRegister((int)CortexMRegisters.PRIMASK, value & 1);
                        SetRegister((int)CortexMRegisters.BasePri, (value >> 8) & 0xff);
                        SetRegister((int)CortexMRegisters.FAULTMASK, (value >> 16) & 1);
                        SetRegister((int)CortexMRegisters.Control, (value >> 24) & 7);
                    }
                    else value = (uint)GetRegister((int)CortexMRegisters.PRIMASK)
                        | ((uint)GetRegister((int)CortexMRegisters.BasePri) << 8)
                        | ((uint)GetRegister((int)CortexMRegisters.FAULTMASK) << 16)
                        | ((uint)GetRegister((int)CortexMRegisters.Control) << 24);
                }
                else
                {
                    var register = NVIC.DebugRegisterIndex(transfer.Selector);
                    if(transfer.Write) SetRegister(register, value);
                    else value = (uint)GetRegister(register);
                }
                nvic.CompleteExternalDebugRegisterTransfer(transfer.Generation, value, transfer.Write);
            }
        }

#pragma warning disable 649
        [Import]
        private readonly Func<uint> tlibEnterHaltingDebug;
#pragma warning restore 649

        private readonly object debugWorkerSync = new object();
        private ulong debugWorkerRevision;
        [Transient]
        private Task debugWorker;
        [Transient]
        private bool debugWorkerDisposed;
    }
}
