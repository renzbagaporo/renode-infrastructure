//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.CPU;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    // Marks the peripherals of one RP2 chip when a machine holds several:
    // every peripheral whose local name starts with Prefix belongs to it. A
    // chip's models find their siblings, cores and resets through it, so a
    // reset of one chip leaves the others running. A machine with fewer than
    // two chips needs no marker: every lookup then spans the machine.
    public sealed class RP2_Chip : IPeripheral
    {
        public RP2_Chip(string prefix)
        {
            Prefix = prefix;
        }

        public void Reset()
        {
        }

        public string Prefix { get; }

        // The peripherals of every other chip in the machine; none with fewer
        // than two chips, or for a peripheral no chip claims.
        public static List<IPeripheral> OtherChips(IMachine machine, IPeripheral member)
        {
            var others = OtherPrefixes(machine, member);
            if(others.Length == 0) return new List<IPeripheral>();
            return machine.GetRegisteredPeripherals().Select(entry => entry.Peripheral).Distinct()
                .Where(peripheral => machine.TryGetLocalName(peripheral, out var name) && others.Any(prefix => name.StartsWith(prefix)))
                .ToList();
        }

        // The peripherals of type T in the member's own chip.
        public static IEnumerable<T> Own<T>(IMachine machine, IPeripheral member) where T : class, IPeripheral
        {
            var others = OtherPrefixes(machine, member);
            return machine.GetPeripheralsOfType<T>()
                .Where(peripheral => others.Length == 0 || !(machine.TryGetLocalName(peripheral, out var name) && others.Any(prefix => name.StartsWith(prefix))));
        }

        // The items, peripherals of any kind, that do not belong to another chip.
        public static IEnumerable<T> Of<T>(IMachine machine, IPeripheral member, IEnumerable<T> items)
        {
            var others = OtherPrefixes(machine, member);
            return others.Length == 0 ? items : items.Where(item => !(item is IPeripheral peripheral
                && machine.TryGetLocalName(peripheral, out var name) && others.Any(prefix => name.StartsWith(prefix))));
        }

        public static IEnumerable<BaseCPU> Cores(IMachine machine, IPeripheral member) =>
            Own<BaseCPU>(machine, member).Distinct();

        private static string[] OtherPrefixes(IMachine machine, IPeripheral member)
        {
            var chips = machine.GetPeripheralsOfType<RP2_Chip>().ToArray();
            if(chips.Length < 2 || !machine.TryGetLocalName(member, out var name)) return new string[0];
            var own = chips.FirstOrDefault(chip => name.StartsWith(chip.Prefix));
            return own == null ? new string[0] : chips.Where(chip => chip != own).Select(chip => chip.Prefix).ToArray();
        }
    }
}
