//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;

namespace Antmicro.Renode.Time
{
    // The virtual time at which a signal change being delivered happened.
    // A device that queues a change under its clock lock and delivers it after
    // releasing that lock can deliver it inside At, so a receiver that
    // timestamps changes, such as a delayed wire, sees the time of the change
    // and not a later clock value another thread advanced to meanwhile.
    public static class SignalTime
    {
        // The time of the change being delivered on this thread for the
        // machine, or that machine's current clock value. Deliveries nest
        // across machines (a synchronous wire's answer from another machine
        // arrives inside this one's delivery), so each machine keeps the time
        // of its innermost delivery.
        public static TimeInterval Of(IMachine machine)
        {
            if(scopes != null)
            {
                for(var i = scopes.Count - 1; i >= 0; i--)
                {
                    if(scopes[i].Machine == machine) return scopes[i].Time;
                }
            }
            return machine.ClockSource.CurrentValue;
        }

        public static Scope At(IMachine machine, TimeInterval time)
        {
            if(scopes == null) scopes = new List<(IMachine Machine, TimeInterval Time)>();
            scopes.Add((machine, time));
            return new Scope(scopes.Count - 1);
        }

        public readonly struct Scope : IDisposable
        {
            public Scope(int depth)
            {
                this.depth = depth;
            }

            public void Dispose()
            {
                if(scopes != null && scopes.Count > depth) scopes.RemoveRange(depth, scopes.Count - depth);
            }

            private readonly int depth;
        }

        [ThreadStatic]
        private static List<(IMachine Machine, TimeInterval Time)> scopes;
    }
}
