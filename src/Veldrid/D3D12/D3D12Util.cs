// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Common helpers used across the D3D12 backend. Kept small and
    /// allocation-free; anything that needs more than a few lines lives
    /// in its own file alongside the type it serves.
    /// </summary>
    internal static class D3D12Util
    {
        /// <summary>
        /// Rounds <paramref name="value"/> up to the next multiple of
        /// <paramref name="alignment"/>. D3D12 has many alignment rules
        /// (constant-buffer = 256 bytes, texture rows = 256 bytes, etc.)
        /// so a single helper avoids reinventing the integer math.
        /// Both args must be non-zero; the helper does not validate.
        /// </summary>
        public static ulong AlignUp(ulong value, ulong alignment)
            => (value + alignment - 1) & ~(alignment - 1);

        /// <summary>Same as <see cref="AlignUp(ulong, ulong)"/> but uint-typed.</summary>
        public static uint AlignUp(uint value, uint alignment)
            => (value + alignment - 1) & ~(alignment - 1);

        /// <summary>
        /// Maps a Veldrid <see cref="BufferUsage"/> to the D3D12 heap type
        /// the resource should live in.
        /// <list type="bullet">
        /// <item>Staging → UPLOAD (CPU-writable, GPU-readable). Always usable
        /// for CopyResource source even without explicit transitions.</item>
        /// <item>Dynamic → UPLOAD too. A more sophisticated backend would
        /// use a ring of per-frame UPLOAD pages; that optimisation can land
        /// later without changing the public surface.</item>
        /// <item>Everything else → DEFAULT (GPU-only). Uploads go through a
        /// staging buffer + CopyResource at submit time.</item>
        /// </list>
        /// </summary>
        public static HeapType ChooseHeapType(BufferUsage usage)
        {
            if ((usage & BufferUsage.Staging) != 0)
                return HeapType.Upload;
            if ((usage & BufferUsage.Dynamic) != 0)
                return HeapType.Upload;
            return HeapType.Default;
        }

        /// <summary>
        /// The initial <see cref="ResourceStates"/> a resource created in
        /// <paramref name="heap"/> MUST be born in — D3D12 enforces these
        /// at <c>CreateCommittedResource</c> time.
        /// </summary>
        public static ResourceStates InitialStateForHeap(HeapType heap)
        {
            return heap switch
            {
                HeapType.Upload   => ResourceStates.GenericRead,    // UPLOAD requires GenericRead
                HeapType.Readback => ResourceStates.CopyDest,       // READBACK requires CopyDest
                _ => ResourceStates.Common,                          // DEFAULT can start in Common
            };
        }
    }
}
