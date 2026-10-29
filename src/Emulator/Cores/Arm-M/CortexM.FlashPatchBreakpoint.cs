// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using Antmicro.Migrant;
using Antmicro.Migrant.Hooks;
using Antmicro.Renode.Utilities.Binding;

namespace Antmicro.Renode.Peripherals.CPU
{
    public partial class CortexM
    {
        internal void UpdateFlashPatchBreakpoints(uint[] addresses)
        {
            // MMIO can run on another CPU while holding a SystemBus access
            // lock. Publish work without waiting for or pausing the target.
            Volatile.Write(ref flashPatchBreakpointAddresses, addresses);
            TlibSetReturnRequest();
        }

        private void ApplyFlashPatchBreakpoints()
        {
            var addresses = Volatile.Read(ref flashPatchBreakpointAddresses);
            if(ReferenceEquals(addresses, appliedFlashPatchBreakpointAddresses)) return;
            var next = new HashSet<uint>(addresses);
            foreach(var address in nativeFlashPatchBreakpoints.Except(next))
            {
                tlibRemoveFpbBreakpoint(address);
            }
            foreach(var address in next.Except(nativeFlashPatchBreakpoints))
            {
                tlibAddFpbBreakpoint(address);
            }
            nativeFlashPatchBreakpoints = next;
            appliedFlashPatchBreakpointAddresses = addresses;
        }

        [Export]
        private uint TryHaltOnFlashPatchBreakpoint()
        {
            if(HaltedByDebugger || !ExternalDebugEnabled) return 0;
            nvic.ReportExternalDebugBreakpoint();
            HaltedByDebugger = true;
            return 1;
        }

        [PostDeserialization]
        private void RestoreFlashPatchBreakpoints()
        {
            // Native breakpoint lists are rebuilt by their managed owners.
            nativeFlashPatchBreakpoints = new HashSet<uint>();
            appliedFlashPatchBreakpointAddresses = null;
        }

        private uint[] flashPatchBreakpointAddresses = Array.Empty<uint>();
        [Transient]
        private uint[] appliedFlashPatchBreakpointAddresses;
        [Transient]
        private HashSet<uint> nativeFlashPatchBreakpoints = new HashSet<uint>();

#pragma warning disable 649
        [Import]
        private readonly Action<uint> tlibAddFpbBreakpoint;
        [Import]
        private readonly Action<uint> tlibRemoveFpbBreakpoint;
#pragma warning restore 649
    }
}
