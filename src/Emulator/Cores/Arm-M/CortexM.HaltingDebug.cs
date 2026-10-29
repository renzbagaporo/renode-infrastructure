//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Utilities.Binding;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        public override ExecutionResult ExecuteInstructions(ulong numberOfInstructionsToExecute, out ulong numberOfExecutedInstructions)
        {
            ApplyFlashPatchBreakpoints();
            if(nvic.ExternalDebugHaltRequested)
            {
                RequestExternalDebugControl();
                numberOfExecutedInstructions = 0;
                return ExecutionResult.Interrupted;
            }
            var step = nvic.TryGetExternalDebugStepRequest(out var generation);
            if(step && numberOfInstructionsToExecute != 0)
            {
                // Debug execution can resume a sleeping core. The ordinary
                // instruction budget still controls the real native CPU.
                TlibCleanWfiProcState();
                numberOfInstructionsToExecute = 1;
            }
            var result = base.ExecuteInstructions(numberOfInstructionsToExecute, out numberOfExecutedInstructions);
            if(step && numberOfExecutedInstructions != 0 && !HaltedByDebugger && !nvic.Lockup.IsSet
                && result != ExecutionResult.Aborted && nvic.ReportExternalDebugStep(generation))
            {
                RequestExternalDebugControl();
            }
            return result;
        }

        [Export]
        private uint TryHaltOnDebugBreakpoint()
        {
            if(!nvic.ReportExternalDebugBreakpoint())
            {
                return 0;
            }
            RequestExternalDebugControl();
            return 1;
        }
    }
}
