//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//

using System;
using System.IO;
using System.Linq;
using System.Threading;

using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.CPU;
using Antmicro.Renode.Peripherals.MemoryControllers;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Memory
{
    // Reads and instruction fetches use the bus so SSI/NOR availability is
    // observed without changing mapped memory while a clock callback runs.
    // Programming remains available only through the flash serial interface.
    public sealed class RP2040_XIPMemory : IBytePeripheral, IWordPeripheral, IDoubleWordPeripheral,
        IQuadWordPeripheral, IMultibyteWritePeripheral, IExecutableIO, IKnownSize, IDisposable
    {
        public RP2040_XIPMemory(IMachine machine, WinbondW25QFlash flash, RP2040_XIP xip)
        {
            this.machine = machine;
            this.flash = flash;
            this.xip = xip;
            flash.StateChanged += InvalidateInstructions;
            xip.AvailabilityChanged += InvalidateInstructions;
            machine.PeripheralsChanged += OnPeripheralsChanged;
        }

        public byte ReadByte(long offset) => (byte)ReadTransaction(offset, 1);
        public ushort ReadWord(long offset) => (ushort)ReadTransaction(offset, 2);
        public uint ReadDoubleWord(long offset) => (uint)ReadTransaction(offset, 4);
        public ulong ReadQuadWord(long offset) => ReadTransaction(offset, 8);

        private ulong ReadTransaction(long offset, int count)
        {
            if(offset < 0 || offset > Size - count) throw new BusAccessException(BusAccessError.AddressError);
            SynchronizeDataAccess();
            if(machine.GetSystemBus(this).TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                && state.InstructionFetch) Volatile.Write(ref hasFetchedInstructions, 1);
            ulong value = 0;
            machine.ClockSource.ExecuteInLock(() =>
            {
                if(!CanRead()) return;
                for(var i = 0; i < count; i++) value |= (ulong)flash.UnderlyingMemory.ReadByte(offset + i) << (8 * i);
            });
            return value;
        }

        private void SynchronizeDataAccess()
        {
            var bus = machine.GetSystemBus(this);
            if(bus.TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                && (state.InstructionFetch || state.DebugAccess)) return;
            if(bus.TryGetTransactionInitiator(out var initiator) && initiator is ICPU cpu) cpu.SyncTime();
        }

        private bool CanRead()
        {
            if(xip.Available) return true;
            if(machine.GetSystemBus(this).TryGetCurrentContextState<CortexM.ContextState>(out var _, out var state)
                && state.InstructionFetch) throw new BusAccessException(BusAccessError.GenericError);
            return false;
        }

        public void WriteByte(long offset, byte value)
        {
        }

        public void WriteWord(long offset, ushort value)
        {
        }

        public void WriteDoubleWord(long offset, uint value)
        {
        }

        public void WriteQuadWord(long offset, ulong value)
        {
        }

        public byte[] ReadBytes(long offset, int count, IPeripheral context = null)
        {
            SynchronizeDataAccess();
            var result = new byte[count];
            machine.ClockSource.ExecuteInLock(() =>
            {
                if(CanRead()) result = flash.UnderlyingMemory.ReadBytes(offset, count);
            });
            return result;
        }

        public void WriteBytes(long offset, byte[] array, int startingIndex, int count, IPeripheral context = null)
        {
        }

        public void LoadBinary(string fileName, long offset = 0)
        {
            var data = File.ReadAllBytes(fileName);
            machine.ClockSource.ExecuteInLock(() =>
            {
                flash.UnderlyingMemory.WriteBytes(offset, data, 0, data.Length);
                InvalidateInstructions();
            });
        }

        public void Dispose()
        {
            machine.PeripheralsChanged -= OnPeripheralsChanged;
            flash.StateChanged -= InvalidateInstructions;
            xip.AvailabilityChanged -= InvalidateInstructions;
            flash.UnderlyingMemory.Dispose();
        }

        public void Reset()
        {
            RouteDataAccessesThroughBus();
        }

        public long Size => flash.UnderlyingMemory.Size;

        private void OnPeripheralsChanged(IMachine _, PeripheralsChangedEventArgs change)
        {
            if(change.Peripheral == this || change.Peripheral is ICPUWithMappedMemory)
            {
                RouteDataAccessesThroughBus();
            }
        }

        private void RouteDataAccessesThroughBus()
        {
            var bus = machine.GetSystemBus(this);
            foreach(var registration in bus.GetRegistrationPoints(this))
            {
                foreach(var cpu in bus.GetCPUs().OfType<ICPUWithMappedMemory>())
                {
                    cpu.RegisterAccessFlags(registration.Range.StartAddress, registration.Range.Size, isIoMemory: true);
                }
            }
        }

        private void InvalidateInstructions()
        {
            if(Volatile.Read(ref hasFetchedInstructions) == 0) return;
            foreach(var cpu in machine.GetSystemBus(this).GetCPUs().OfType<TranslationCPU>())
            {
                // Request clearing on the CPU thread after a concurrent code
                // translation returns, including any newly published block.
                cpu.RequestTranslationCacheClearing();
            }
        }

        private int hasFetchedInstructions;
        private readonly IMachine machine;
        private readonly WinbondW25QFlash flash;
        private readonly RP2040_XIP xip;
    }
}
