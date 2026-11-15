//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using Antmicro.Renode.Time;

namespace Antmicro.Renode.Peripherals.GPIOPort
{
    // An endpoint told when the next input change already scheduled on its
    // machine's clock reaches a pin, such as a change from another machine
    // on a delayed wire. A device that runs ahead of its machine clock can
    // stop there, as it cannot know a later level before that change.
    public interface IScheduledInputs
    {
        // `at` is the machine clock time of the pin's next scheduled change, or
        // null when none is scheduled.
        void SetNextInputChange(int pin, TimeInterval? at);
    }
}
