// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Linear allocator for D3D12 descriptors. Owns one
    /// <see cref="ID3D12DescriptorHeap"/> of a fixed type and capacity;
    /// hands out contiguous descriptor ranges from a monotonically
    /// increasing index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 has four descriptor heap types (CBV/SRV/UAV, Sampler, RTV,
    /// DSV) with different lifetime expectations and visibility:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="DescriptorHeapType.CbvSrvUav"/> — shader-visible
    /// for binding; one big heap per device, allocated frame-by-frame.</item>
    /// <item><see cref="DescriptorHeapType.Sampler"/> — shader-visible;
    /// hard capped at 2048 entries per heap by D3D12 spec.</item>
    /// <item><see cref="DescriptorHeapType.RenderTargetView"/> /
    /// <see cref="DescriptorHeapType.DepthStencilView"/> — NOT shader
    /// visible; consumed by OMSetRenderTargets directly.</item>
    /// </list>
    /// <para>
    /// This allocator is deliberately simple: a single linear pointer
    /// that never frees individual descriptors. Total capacity is sized
    /// generously so we never realistically hit the limit during a Torii
    /// session. A real production-grade allocator would have a free-list
    /// + frame-fence reclamation; that lands in S6 once we have telemetry
    /// on real allocation patterns.
    /// </para>
    /// <para>
    /// Thread-safety: NOT thread-safe. All allocations must happen on
    /// the same thread (osu-framework's update or draw thread, depending
    /// on the resource type). The Veldrid contract aligns with this —
    /// resources are created on a single owning thread.
    /// </para>
    /// </remarks>
    internal sealed class D3D12DescriptorAllocator : IDisposable
    {
        public ID3D12DescriptorHeap Heap => heap;

        /// <summary>
        /// Descriptor handle increment size for this heap type. Cached
        /// because each call to <c>GetDescriptorHandleIncrementSize</c>
        /// is a per-call API trip — gets called on every allocation, so
        /// memoising matters.
        /// </summary>
        public uint DescriptorSize { get; }

        public bool IsShaderVisible { get; }

        private readonly ID3D12DescriptorHeap heap;
        private readonly DescriptorHeapType heapType;
        private readonly int capacity;
        private int nextIndex;
        private bool disposed;

        public D3D12DescriptorAllocator(
            ID3D12Device device,
            DescriptorHeapType type,
            int capacity,
            bool shaderVisible)
        {
            this.heapType = type;
            this.capacity = capacity;
            IsShaderVisible = shaderVisible;

            // Shader-visible heaps can be bound to the command list and
            // referenced from shaders via descriptor tables. RTV/DSV heaps
            // are CPU-only — the GPU dereferences them at OMSetRenderTargets
            // time but they never appear in a descriptor table.
            //
            // Sampler heaps have a hard cap of 2048 entries even when
            // shader-visible, enforced by D3D12 validation.
            heap = device.CreateDescriptorHeap(new DescriptorHeapDescription
            {
                Type = type,
                DescriptorCount = capacity,
                Flags = shaderVisible
                    ? DescriptorHeapFlags.ShaderVisible
                    : DescriptorHeapFlags.None,
                NodeMask = 0,
            });

            // Vortice's GetDescriptorHandleIncrementSize returns int (matches
            // the D3D12 native signature); cast to uint because the field's
            // semantic is "size in bytes ≥ 0".
            DescriptorSize = (uint)device.GetDescriptorHandleIncrementSize(type);
        }

        /// <summary>
        /// Reserve <paramref name="count"/> contiguous descriptor slots.
        /// Returns the starting index. Throws if the heap is full —
        /// callers should size the heap up-front to avoid this in practice.
        /// </summary>
        public int Allocate(int count = 1)
        {
            if (nextIndex + count > capacity)
                throw new VeldridException(
                    $"D3D12 descriptor heap (type={heapType}) exhausted: "
                    + $"requested {count}, used {nextIndex}/{capacity}.");

            int start = nextIndex;
            nextIndex += count;
            return start;
        }

        /// <summary>
        /// CPU-side handle for descriptor index <paramref name="index"/>.
        /// Always available regardless of <see cref="IsShaderVisible"/>;
        /// used for <c>CreateXxxView</c> calls. Computed via the Vortice
        /// <c>+</c> operator overload which the binding exposes for
        /// handle-arithmetic — clearer than manual Ptr math.
        /// </summary>
        public CpuDescriptorHandle GetCpuHandle(int index)
        {
            return heap.GetCPUDescriptorHandleForHeapStart() + (int)(index * DescriptorSize);
        }

        /// <summary>
        /// GPU-side handle for descriptor index <paramref name="index"/>.
        /// Only valid if the heap was created with
        /// <see cref="DescriptorHeapFlags.ShaderVisible"/> — throws
        /// otherwise. Used to point the GPU at a descriptor table.
        /// </summary>
        public GpuDescriptorHandle GetGpuHandle(int index)
        {
            if (!IsShaderVisible)
                throw new VeldridException("Descriptor heap is not shader-visible.");

            return heap.GetGPUDescriptorHandleForHeapStart() + (int)(index * DescriptorSize);
        }

        public void Dispose()
        {
            if (disposed) return;
            heap.Dispose();
            disposed = true;
        }
    }
}
