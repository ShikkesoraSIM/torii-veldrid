// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 command list. **Scaffold (session 2)** — owns a real
    /// <see cref="ID3D12CommandAllocator"/> + <see cref="ID3D12GraphicsCommandList"/>
    /// pair, supports <see cref="Begin"/> / <see cref="End"/> for recording
    /// state-transitions, and is submittable through
    /// <c>D3D12GraphicsDevice.SubmitCommandsCore</c>. All actual rendering
    /// commands (<see cref="DrawCore"/>, <see cref="SetPipelineCore"/>,
    /// <see cref="SetFramebufferCore"/>, etc.) throw
    /// <see cref="NotImplementedException"/> for now.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 separates the memory backing (<see cref="ID3D12CommandAllocator"/>)
    /// from the recording context (<see cref="ID3D12GraphicsCommandList"/>).
    /// This first cut keeps it simple: one allocator per command list, reset
    /// together at <see cref="Begin"/>. In production this should grow into a
    /// pool of frame-in-flight allocators so back-to-back Begin/End/Submit
    /// cycles don't have to wait for the previous submission to complete on
    /// the GPU. That optimisation is deferred — correctness first, perf later.
    /// </para>
    /// <para>
    /// Begin/End semantics:
    /// </para>
    /// <list type="bullet">
    /// <item><see cref="Begin"/>: reset the allocator, then reset the
    /// command list to point at the (just-reset) allocator. After this
    /// call the list is in "recording" state and ready to accept commands.
    /// Important: it is the CALLER'S responsibility to ensure the GPU is
    /// done with the previous submission of this allocator before calling
    /// Begin — the device fences handle this in SubmitCommandsCore.</item>
    /// <item><see cref="End"/>: closes the list. After this it can be
    /// passed to <c>queue.ExecuteCommandLists</c> but not modified.</item>
    /// </list>
    /// <para>
    /// All the rendering-state methods are scaffold stubs because they
    /// each need a real Veldrid→D3D12 mapping (PipelineState, RootSignature,
    /// resource descriptors, viewports, etc.) which lands in sessions 3
    /// and 4 alongside the corresponding resource backings.
    /// </para>
    /// </remarks>
    internal class D3D12CommandList : CommandList
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>The native D3D12 command list. Valid between Begin and submission.</summary>
        public ID3D12GraphicsCommandList NativeList => commandList;

        private readonly ID3D12CommandAllocator allocator;
        private readonly ID3D12GraphicsCommandList commandList;

        // D3D12 command lists are born OPEN (Reset implicit on creation),
        // but Veldrid's contract is that commands only land between
        // Begin/End. We close immediately after creation and rely on the
        // user calling Begin to put us back in the recording state.
        private bool isRecording;
        private bool disposed;

        public D3D12CommandList(D3D12GraphicsDevice gd, ref CommandListDescription description)
            : base(ref description, gd.Features, gd.UniformBufferMinOffsetAlignment, gd.StructuredBufferMinOffsetAlignment)
        {
            Name = string.Empty;

            allocator = gd.Device.CreateCommandAllocator(CommandListType.Direct);
            commandList = gd.Device.CreateCommandList<ID3D12GraphicsCommandList>(
                nodeMask: 0,
                CommandListType.Direct,
                allocator,
                initialState: null);

            // Lists are created in the open state. Close immediately so the
            // first Begin can Reset() cleanly.
            commandList.Close();
            isRecording = false;
        }

        public override void Begin()
        {
            if (isRecording)
                throw new VeldridException("D3D12CommandList.Begin called while already recording.");

            // Resetting the allocator while the GPU is still consuming the
            // previous submission is undefined behaviour. The device's
            // SubmitCommandsCore fences a signal after each submission and
            // the caller is expected to wait on it before reusing this
            // command list. Veldrid doesn't expose per-list ownership
            // tracking — that contract sits at the caller (osu-framework's
            // renderer) which holds one list per frame and waits the
            // frame fence before reuse.
            allocator.Reset();
            commandList.Reset(allocator, initialState: null);
            isRecording = true;
        }

        public override void End()
        {
            if (!isRecording)
                throw new VeldridException("D3D12CommandList.End called while not recording.");

            commandList.Close();
            isRecording = false;
        }

        public override void Dispose()
        {
            if (disposed) return;
            commandList.Dispose();
            allocator.Dispose();
            disposed = true;
        }

        // ---- Rendering-state methods — scaffold stubs ---------------------
        //
        // Each of these maps a Veldrid command onto one or more D3D12
        // ID3D12GraphicsCommandList calls. The translations need the
        // corresponding D3D12 resource wrappers (Pipeline, ResourceSet,
        // Framebuffer, etc.) which arrive in sessions 3-4. Until then,
        // any attempt to actually record render work into this list throws.

        public override void SetViewport(uint index, ref Viewport viewport)
            => throw new NotImplementedException("D3D12: SetViewport pending session 3.");

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
            => throw new NotImplementedException("D3D12: SetScissorRect pending session 3.");

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => throw new NotImplementedException("D3D12: Dispatch pending compute-pipeline work (session 4).");

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetsCount, ref uint dynamicOffsets)
            => throw new NotImplementedException("D3D12: ResourceSet binding pending session 3.");

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet set, uint dynamicOffsetsCount, ref uint dynamicOffsets)
            => throw new NotImplementedException("D3D12: ResourceSet binding pending session 3.");

        protected override void SetFramebufferCore(Framebuffer fb)
            => throw new NotImplementedException("D3D12: Framebuffer binding pending session 5 (swapchain).");

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
            => throw new NotImplementedException("D3D12: indirect draw pending session 4.");

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
            => throw new NotImplementedException("D3D12: indirect draw pending session 4.");

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
            => throw new NotImplementedException("D3D12: indirect dispatch pending session 4.");

        protected override void ResolveTextureCore(Texture source, Texture destination)
            => throw new NotImplementedException("D3D12: ResolveSubresource pending session 3.");

        protected override void CopyBufferCore(DeviceBuffer source, uint sourceOffset, DeviceBuffer destination, uint destinationOffset, uint sizeInBytes)
            => throw new NotImplementedException("D3D12: buffer copy pending session 3.");

        protected override void CopyTextureCore(
            Texture source, uint srcX, uint srcY, uint srcZ, uint srcMipLevel, uint srcBaseArrayLayer,
            Texture destination, uint dstX, uint dstY, uint dstZ, uint dstMipLevel, uint dstBaseArrayLayer,
            uint width, uint height, uint depth, uint layerCount)
            => throw new NotImplementedException("D3D12: texture copy pending session 3.");

        private protected override void SetPipelineCore(Pipeline pipeline)
            => throw new NotImplementedException("D3D12: SetPipeline pending session 4.");

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
            => throw new NotImplementedException("D3D12: vertex-buffer binding pending session 3.");

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
            => throw new NotImplementedException("D3D12: index-buffer binding pending session 3.");

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
            => throw new NotImplementedException("D3D12: ClearRenderTargetView pending session 5.");

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
            => throw new NotImplementedException("D3D12: ClearDepthStencilView pending session 5.");

        private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => throw new NotImplementedException("D3D12: Draw pending session 4.");

        private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
            => throw new NotImplementedException("D3D12: DrawIndexed pending session 4.");

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
            => throw new NotImplementedException("D3D12: UpdateBuffer (in-list) pending session 3.");

        private protected override void GenerateMipmapsCore(Texture texture)
            => throw new NotImplementedException("D3D12: mipmap generation pending session 3.");

        // Debug markers — these are CHEAP D3D12 native calls so we could
        // wire them now even without other rendering state, but the
        // surrounding command list isn't accepting state changes yet so a
        // debug marker on its own would be inert. Keep as stubs and wire
        // alongside the rest of the rendering commands.
        private protected override void PushDebugGroupCore(string name)
            => throw new NotImplementedException("D3D12: debug-marker push pending session 6.");

        private protected override void PopDebugGroupCore()
            => throw new NotImplementedException("D3D12: debug-marker pop pending session 6.");

        private protected override void InsertDebugMarkerCore(string name)
            => throw new NotImplementedException("D3D12: debug-marker insert pending session 6.");
    }
}
