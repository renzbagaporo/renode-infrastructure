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
                HaltedByDebugger = true;
                // A newer debugger command wins over this step's completion.
                if(nvic.ExternalDebugRequestGeneration != generation)
                {
                    HaltedByDebugger = nvic.ExternalDebugHaltRequested;
                }
            }
            return result;
        }

        [Export]
        private uint TryHaltOnDebugBreakpoint()
        {
            if(!ExternalDebugEnabled)
            {
                return 0;
            }
            nvic.ReportExternalDebugBreakpoint();
            HaltedByDebugger = true;
            return 1;
        }
    }
}
