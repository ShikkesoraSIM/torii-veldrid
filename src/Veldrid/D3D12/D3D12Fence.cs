// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Threading;
using Vortice.Direct3D12;
using SharpGen.Runtime;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 fence — used by the device to synchronise CPU-side waits
    /// against GPU command-buffer completion. Wraps a Vortice
    /// <see cref="ID3D12Fence"/> and a Win32 event handle that fires when
    /// the fence reaches its target value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 fences differ from D3D11's `ManualResetEvent`-backed model:
    /// the fence has a monotonically-increasing 64-bit counter, and the
    /// CPU asks "wake me when the value reaches N". A `signaled` fence in
    /// Veldrid terms is one whose target value has been reached; "reset"
    /// bumps the target so the wait blocks again.
    /// </para>
    /// <para>
    /// Internal counter convention:
    /// <list type="bullet">
    /// <item><c>nextValue</c> = the value the NEXT signal will move the
    /// fence to. Starts at 1 (or 0 if constructed as already-signaled).</item>
    /// <item><see cref="Signaled"/> reads
    /// <c>fence.CompletedValue >= targetValue</c>. The current target is
    /// tracked separately because Veldrid's <see cref="Reset"/> contract
    /// is "make this fence unsignaled" — D3D12 fences don't go backwards,
    /// so Reset bumps the target above CompletedValue instead.</item>
    /// </list>
    /// </para>
    /// </remarks>
    internal sealed class D3D12Fence : Fence
    {
        public override bool Signaled => fence.CompletedValue >= targetValue;
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>The wrapped D3D12 fence handle.</summary>
        public ID3D12Fence Fence => fence;

        /// <summary>
        /// Value the next <see cref="Signal"/> should move the fence to.
        /// Incremented on each call so successive submits chain correctly.
        /// </summary>
        public ulong NextValue => nextValue;

        /// <summary>
        /// Target value that the CPU is waiting for. The fence is "signaled"
        /// (from Veldrid's perspective) once <c>fence.CompletedValue >=</c>
        /// this. Updated by <see cref="Signal"/> after the GPU has been
        /// asked to bump the fence.
        /// </summary>
        public ulong TargetValue => targetValue;

        private readonly ID3D12Fence fence;
        private readonly ManualResetEvent waitEvent;

        private ulong nextValue;
        private ulong targetValue;
        private bool disposed;

        public D3D12Fence(ID3D12Device device, bool signaled)
        {
            Name = string.Empty;

            // Starting value: if the caller asked for a pre-signaled fence,
            // start at 1 with target 1 so Signaled returns true immediately.
            // Otherwise start at 0 with target 1 so the fence is unsignaled
            // until somebody calls Signal.
            ulong initial = signaled ? 1UL : 0UL;
            fence = device.CreateFence(initial, FenceFlags.None);
            targetValue = 1;
            nextValue = signaled ? 2UL : 1UL;

            waitEvent = new ManualResetEvent(initialState: false);
        }

        /// <summary>
        /// Reserve a target value for the next GPU signal. Returns the value
        /// the queue should be asked to signal to. Caller is responsible for
        /// invoking <c>queue.Signal(fence, value)</c>.
        /// </summary>
        public ulong AllocateNextSignalValue()
        {
            ulong v = nextValue++;
            targetValue = v;
            return v;
        }

        /// <summary>
        /// Block the calling thread until the fence reaches its current
        /// <see cref="TargetValue"/>, with a nanosecond timeout. Returns
        /// true if the fence signalled within the timeout.
        /// </summary>
        public bool Wait(ulong nanosecondTimeout)
        {
            // Fast path: already there.
            if (fence.CompletedValue >= targetValue)
                return true;

            // SetEventOnCompletion ties the Win32 event to a specific target
            // value. Re-arm each wait because TargetValue may have changed
            // since the last call (e.g. caller did Reset → Submit → Wait).
            waitEvent.Reset();
            fence.SetEventOnCompletion(targetValue, waitEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();

            int msTimeout = nanosecondTimeout == ulong.MaxValue
                ? -1
                : (int)Math.Min(int.MaxValue, nanosecondTimeout / 1_000_000UL);

            return waitEvent.WaitOne(msTimeout);
        }

        /// <summary>
        /// Make the fence unsignaled from Veldrid's perspective. D3D12
        /// fences can't decrement, so we bump the target value above the
        /// current completed value — subsequent <see cref="Signaled"/>
        /// checks will read false until a new signal lands.
        /// </summary>
        public override void Reset()
        {
            // Push target one past whatever's currently completed, so the
            // monotonic counter rule is respected and Signaled flips back
            // to false.
            targetValue = fence.CompletedValue + 1;
            if (nextValue <= targetValue)
                nextValue = targetValue + 1;
        }

        public override void Dispose()
        {
            if (disposed) return;
            waitEvent.Dispose();
            fence.Dispose();
            disposed = true;
        }
    }
}
