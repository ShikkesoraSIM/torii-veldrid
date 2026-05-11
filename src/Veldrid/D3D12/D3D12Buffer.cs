// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 buffer — wraps an <see cref="ID3D12Resource"/> backed by
    /// a committed allocation in the appropriate heap (DEFAULT / UPLOAD /
    /// READBACK depending on the requested <see cref="BufferUsage"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 buffers are 1-dimensional resources without an inherent format.
    /// The view (CBV/SRV/UAV/IBV/VBV) wraps the buffer with format + stride
    /// info at bind time — those views are created lazily in S4 alongside
    /// ResourceSet wiring. This file is purely about the underlying memory.
    /// </para>
    /// <para>
    /// Constant-buffer ranges (Uniform usage) have a 256-byte alignment
    /// requirement in D3D12 — the buffer's <see cref="SizeInBytes"/> property
    /// reports the user-requested size; the actual allocation is rounded up
    /// internally via <see cref="D3D12Util.AlignUp(ulong,ulong)"/> so
    /// CreateConstantBufferView calls don't trip the validation layer.
    /// </para>
    /// <para>
    /// <see cref="CurrentState"/> is mutated by the CommandList layer (S2's
    /// scaffold; transitions land in S4 when SetPipeline/Draw paths are
    /// wired). External code should not write this directly — only the
    /// transition emitter is allowed to.
    /// </para>
    /// </remarks>
    internal sealed class D3D12Buffer : DeviceBuffer
    {
        public override uint SizeInBytes { get; }
        public override BufferUsage Usage { get; }
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>The underlying D3D12 resource. Lifetime tied to this object.</summary>
        public ID3D12Resource NativeResource => resource;

        /// <summary>
        /// Heap the buffer was committed into. Drives transition rules
        /// (UPLOAD/READBACK heaps reject many state transitions).
        /// </summary>
        public HeapType HeapType { get; }

        /// <summary>
        /// Current resource state, mutated by the transition emitter as the
        /// CommandList records work that needs this buffer in a different
        /// state. Initialised from the heap-mandated initial state.
        /// </summary>
        public ResourceStates CurrentState { get; set; }

        /// <summary>
        /// Real allocated size (≥ <see cref="SizeInBytes"/>) after alignment
        /// padding. CreateConstantBufferView et al. read from this; user code
        /// reads the unrounded value via <see cref="SizeInBytes"/>.
        /// </summary>
        public ulong PaddedSizeInBytes { get; }

        private readonly ID3D12Resource resource;
        private bool disposed;

        public D3D12Buffer(D3D12GraphicsDevice gd, ref BufferDescription description)
        {
            Name = string.Empty;
            SizeInBytes = description.SizeInBytes;
            Usage = description.Usage;

            HeapType = D3D12Util.ChooseHeapType(description.Usage);

            // 256-byte alignment for anything that might be bound as a
            // constant buffer (CBV). It's cheap to apply unconditionally for
            // a few wasted bytes per buffer, and avoids the headache of
            // tracking which usage flags trigger the alignment requirement.
            const ulong cbv_alignment = 256;
            PaddedSizeInBytes = D3D12Util.AlignUp((ulong)description.SizeInBytes, cbv_alignment);

            var heapProps = new HeapProperties(HeapType);
            var resourceDesc = ResourceDescription.Buffer(PaddedSizeInBytes);

            // Buffers in DEFAULT heap that will be UAV-bound need the
            // AllowUnorderedAccess flag at creation time — you can't turn it
            // on later. Check the Veldrid usage bits to decide.
            if ((description.Usage & BufferUsage.StructuredBufferReadWrite) != 0
                || (description.Usage & BufferUsage.IndirectBuffer) != 0)
            {
                resourceDesc.Flags |= ResourceFlags.AllowUnorderedAccess;
            }

            CurrentState = D3D12Util.InitialStateForHeap(HeapType);

            // CreateCommittedResource: one-shot heap allocation + resource
            // creation. Faster path is CreatePlacedResource against a
            // pre-allocated heap (less driver overhead for many small
            // resources), but the placed-heap allocator is a session-6
            // optimisation. Committed resources are correct and simpler.
            resource = gd.Device.CreateCommittedResource(
                heapProps,
                HeapFlags.None,
                resourceDesc,
                CurrentState,
                optimizedClearValue: null);
        }

        public override void Dispose()
        {
            if (disposed) return;
            resource.Dispose();
            disposed = true;
        }
    }
}
