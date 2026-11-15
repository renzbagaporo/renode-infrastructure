//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;

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
        // machine, or that machine's current clock value.
        public static TimeInterval Of(IMachine machine)
        {
            return scopeMachine != null && scopeMachine == machine ? scopeTime : machine.ClockSource.CurrentValue;
        }

        public static Scope At(IMachine machine, TimeInterval time)
        {
            var scope = new Scope(scopeMachine, scopeTime);
            scopeMachine = machine;
            scopeTime = time;
            return scope;
        }

        public readonly struct Scope : IDisposable
        {
            public Scope(IMachine machine, TimeInterval time)
            {
                previousMachine = machine;
                previousTime = time;
            }

            public void Dispose()
            {
                scopeMachine = previousMachine;
                scopeTime = previousTime;
            }

            private readonly IMachine previousMachine;
            private readonly TimeInterval previousTime;
        }

        [ThreadStatic]
        private static IMachine scopeMachine;
        [ThreadStatic]
        private static TimeInterval scopeTime;
    }
}
