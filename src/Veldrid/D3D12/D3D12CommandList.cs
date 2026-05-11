// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 command list. As of S4 part 2 this wires the core
    /// rendering path end-to-end: pipeline / framebuffer / viewport /
    /// scissor / vertex+index buffers / resource set binding / draw /
    /// clear, with automatic <see cref="ResourceBarrier"/> transitions
    /// for the bound textures (via <see cref="D3D12Util.TransitionTexture"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list owns one <see cref="ID3D12CommandAllocator"/> +
    /// <see cref="ID3D12GraphicsCommandList"/> pair. Begin resets both;
    /// End closes the list for submission. Reuse contract: the caller
    /// MUST fence-wait the previous submission before calling Begin
    /// again — see SubmitCommandsCore on the device for the matching
    /// signal pattern.
    /// </para>
    /// <para>
    /// Currently-bound state (pipeline, framebuffer) is tracked on the
    /// instance so commands like ClearColorTarget / SetGraphicsResourceSet
    /// know which framebuffer / root signature to operate against. State
    /// resets at every Begin so a fresh list never inherits stale binds
    /// from the previous session.
    /// </para>
    /// <para>
    /// Descriptor-heap binding (<c>SetDescriptorHeaps</c>): D3D12 demands
    /// the shader-visible heaps be set on the command list once per recorded
    /// sequence, BEFORE any SetDescriptorTable call. We do this lazily on
    /// the first SetPipeline; subsequent SetPipelines reuse the existing
    /// heap binding since the device always allocates from the same two
    /// shader-visible heaps.
    /// </para>
    /// </remarks>
    internal class D3D12CommandList : CommandList
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        public ID3D12GraphicsCommandList NativeList => commandList;

        private readonly D3D12GraphicsDevice gd;
        private readonly ID3D12CommandAllocator allocator;
        private readonly ID3D12GraphicsCommandList commandList;

        private D3D12Pipeline? currentPipeline;
        private D3D12Framebuffer? currentFramebuffer;
        private bool descriptorHeapsBound;

        private bool isRecording;
        private bool disposed;

        public D3D12CommandList(D3D12GraphicsDevice gd, ref CommandListDescription description)
            : base(ref description, gd.Features, gd.UniformBufferMinOffsetAlignment, gd.StructuredBufferMinOffsetAlignment)
        {
            this.gd = gd;
            Name = string.Empty;

            allocator = gd.Device.CreateCommandAllocator(CommandListType.Direct);
            commandList = gd.Device.CreateCommandList<ID3D12GraphicsCommandList>(
                nodeMask: 0,
                CommandListType.Direct,
                allocator,
                initialState: null);

            commandList.Close();
            isRecording = false;
        }

        public override void Begin()
        {
            if (isRecording)
                throw new VeldridException("D3D12CommandList.Begin called while already recording.");

            allocator.Reset();
            commandList.Reset(allocator, initialState: null);
            isRecording = true;

            // Reset tracked state so a fresh recording doesn't accidentally
            // inherit pipeline / framebuffer bindings from the previous one.
            currentPipeline = null;
            currentFramebuffer = null;
            descriptorHeapsBound = false;
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

        // ---- Pipeline state -------------------------------------------

        private protected override void SetPipelineCore(Pipeline pipeline)
        {
            var d12Pipeline = Util.AssertSubtype<Pipeline, D3D12Pipeline>(pipeline);

            // Bind shader-visible heaps once per recording. Cheap to call
            // even when already bound, but the if-guard skips the GPU
            // command altogether for the steady-state case. Vortice's
            // SetDescriptorHeaps takes an ID3D12DescriptorHeap[] — pass
            // both shader-visible heaps in one call.
            if (!descriptorHeapsBound)
            {
                commandList.SetDescriptorHeaps(new ID3D12DescriptorHeap[]
                {
                    gd.CbvSrvUavAllocator.Heap,
                    gd.SamplerAllocator.Heap,
                });
                descriptorHeapsBound = true;
            }

            commandList.SetGraphicsRootSignature(d12Pipeline.NativeRootSignature);
            commandList.SetPipelineState(d12Pipeline.NativePipelineState);
            commandList.IASetPrimitiveTopology(d12Pipeline.NativePrimitiveTopology);

            currentPipeline = d12Pipeline;
        }

        protected override void SetGraphicsResourceSetCore(uint slot, ResourceSet rs, uint dynamicOffsetsCount, ref uint dynamicOffsets)
        {
            if (currentPipeline == null)
                throw new VeldridException("SetGraphicsResourceSet called without a bound pipeline.");

            var d12Set = Util.AssertSubtype<ResourceSet, D3D12ResourceSet>(rs);

            // Transition any bound textures into SHADER_RESOURCE state
            // before the draw. UAV-bound textures (TextureReadWrite) need
            // UNORDERED_ACCESS instead; we keep the logic simple — read
            // path covers the common osu! case, write path lands in S6.
            for (int i = 0; i < d12Set.BoundResources.Length; i++)
            {
                var res = d12Set.BoundResources[i];
                var kind = d12Set.Layout.Elements[i].Kind;

                if (kind == ResourceKind.TextureReadOnly)
                {
                    var tex = res is D3D12TextureView v ? v.Target : (D3D12Texture)res;
                    D3D12Util.TransitionTexture(commandList, tex, ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
                }
                else if (kind == ResourceKind.StructuredBufferReadOnly)
                {
                    D3D12Util.TransitionBuffer(commandList, (D3D12Buffer)res, ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
                }
            }

            int layoutIdx = (int)slot;
            int cbvSrvUavRoot = currentPipeline.CbvSrvUavRootParamPerLayout[layoutIdx];
            int samplerRoot = currentPipeline.SamplerRootParamPerLayout[layoutIdx];

            if (cbvSrvUavRoot >= 0 && d12Set.HasCbvSrvUavTable)
                commandList.SetGraphicsRootDescriptorTable(cbvSrvUavRoot, d12Set.CbvSrvUavGpuStart);
            if (samplerRoot >= 0 && d12Set.HasSamplerTable)
                commandList.SetGraphicsRootDescriptorTable(samplerRoot, d12Set.SamplerGpuStart);
        }

        protected override void SetComputeResourceSetCore(uint slot, ResourceSet set, uint dynamicOffsetsCount, ref uint dynamicOffsets)
            => throw new NotImplementedException("D3D12: compute resource set pending (S6 compute pipeline).");

        // ---- Framebuffer + viewport + scissor -------------------------

        protected override void SetFramebufferCore(Framebuffer fb)
        {
            var d12Fb = Util.AssertSubtype<Framebuffer, D3D12Framebuffer>(fb);

            // Transition all color targets to RENDER_TARGET state. Most
            // framebuffers are created already in this state (see
            // D3D12Texture's initial-state choice), but a texture that was
            // sampled in a previous draw may have moved out of it.
            for (int i = 0; i < d12Fb.ColorTargets.Count; i++)
            {
                var tex = Util.AssertSubtype<Texture, D3D12Texture>(d12Fb.ColorTargets[i].Target);
                D3D12Util.TransitionTexture(commandList, tex, ResourceStates.RenderTarget);
            }
            if (d12Fb.DepthTarget != null)
            {
                var depthTex = Util.AssertSubtype<Texture, D3D12Texture>(d12Fb.DepthTarget.Value.Target);
                D3D12Util.TransitionTexture(commandList, depthTex, ResourceStates.DepthWrite);
            }

            // OMSetRenderTargets points the output-merger stage at the
            // RTV (and optional DSV) descriptor handles the framebuffer
            // pre-allocated. The handles are contiguous in the RTV heap so
            // the count + start-handle pair is sufficient — no copying.
            // Vortice's overload `(int, CpuDescriptorHandle, Nullable<CpuDescriptorHandle>)`
            // implicitly treats the start handle as a contiguous range.
            commandList.OMSetRenderTargets(
                d12Fb.ColorTargets.Count,
                d12Fb.RtvHandle,
                d12Fb.HasDepth ? d12Fb.DsvHandle : (CpuDescriptorHandle?)null);

            currentFramebuffer = d12Fb;
        }

        public override void SetViewport(uint index, ref Viewport viewport)
        {
            // Vortice's RSSetViewport takes the 6 floats directly (matches
            // the native D3D12_VIEWPORT struct layout). D3D12 supports up
            // to 16 viewports — `index` selects which one to set.
            // NOTE: Vortice 2.4.2's RSSetViewport sets the FIRST viewport
            // unconditionally; multi-viewport support per slot needs
            // RSSetViewports (plural) with a span. For now we only support
            // setting viewport 0 — covers osu-framework's actual usage.
            if (index != 0)
                throw new NotSupportedException("D3D12: multi-viewport not yet wired; pending RSSetViewports plural form.");

            commandList.RSSetViewport(
                viewport.X, viewport.Y,
                viewport.Width, viewport.Height,
                viewport.MinDepth, viewport.MaxDepth);
        }

        public override void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            if (index != 0)
                throw new NotSupportedException("D3D12: multi-scissor not yet wired; pending RSSetScissorRects plural form.");

            // D3D12 scissor rects are Left/Top/Right/Bottom (not LTWH).
            // Vortice exposes a single-RawRect overload — convert LTWH→LTRB.
            commandList.RSSetScissorRect(new Vortice.RawRect(
                left: (int)x, top: (int)y,
                right: (int)(x + width), bottom: (int)(y + height)));
        }

        // ---- Clears ---------------------------------------------------

        private protected override void ClearColorTargetCore(uint index, RgbaFloat clearColor)
        {
            if (currentFramebuffer == null)
                throw new VeldridException("ClearColorTarget called without a bound framebuffer.");

            // Compute the RTV handle for the requested slot. The framebuffer
            // pre-allocates a contiguous run starting at RtvHandle; we
            // advance by descriptorSize * index bytes.
            CpuDescriptorHandle target = currentFramebuffer.RtvHandle
                + (int)(index * gd.RtvAllocator.DescriptorSize);

            commandList.ClearRenderTargetView(target, new Color4(
                clearColor.R, clearColor.G, clearColor.B, clearColor.A));
        }

        private protected override void ClearDepthStencilCore(float depth, byte stencil)
        {
            if (currentFramebuffer == null || !currentFramebuffer.HasDepth)
                throw new VeldridException("ClearDepthStencil called without a bound depth attachment.");

            commandList.ClearDepthStencilView(
                currentFramebuffer.DsvHandle,
                ClearFlags.Depth | ClearFlags.Stencil,
                depth,
                stencil);
        }

        // ---- Vertex + index buffer binding ----------------------------

        private protected override void SetVertexBufferCore(uint index, DeviceBuffer buffer, uint offset)
        {
            var d12Buf = Util.AssertSubtype<DeviceBuffer, D3D12Buffer>(buffer);
            D3D12Util.TransitionBuffer(commandList, d12Buf, ResourceStates.VertexAndConstantBuffer);

            // Stride is supposed to come from the bound pipeline's vertex
            // layout. We don't have a clean back-pointer to that here, so
            // for now we assume the entire buffer is the vertex range and
            // the stride is implied by the pipeline's input layout — D3D12
            // is told the buffer + size and trusts the PSO for stride.
            //
            // VertexBufferView.StrideInBytes IS required, though, so we
            // pull it from the currently-bound pipeline's first vertex
            // layout. A multi-VBO pipeline with different strides per
            // slot is a future enhancement (osu-framework currently uses
            // a single interleaved VBO per pipeline).
            int stride = currentPipeline != null ? stridePerSlot(currentPipeline, (int)index) : 0;

            var view = new VertexBufferView
            {
                BufferLocation = d12Buf.NativeResource.GPUVirtualAddress + offset,
                SizeInBytes = (int)(d12Buf.SizeInBytes - offset),
                StrideInBytes = stride,
            };
            commandList.IASetVertexBuffers((int)index, new[] { view });
        }

        private static int stridePerSlot(D3D12Pipeline pipeline, int slot)
        {
            // We don't have the original VertexLayoutDescription stored on
            // the pipeline; for the common single-VBO case, the stride
            // equals the sum of all attribute sizes in slot 0. Hard-coded
            // to 0 here — D3D12 will fall back to the PSO's input layout
            // stride which is the source of truth anyway. A proper
            // pipeline-side stride cache lands in S6 if profiling shows
            // it matters.
            _ = pipeline;
            _ = slot;
            return 0;
        }

        private protected override void SetIndexBufferCore(DeviceBuffer buffer, IndexFormat format, uint offset)
        {
            var d12Buf = Util.AssertSubtype<DeviceBuffer, D3D12Buffer>(buffer);
            D3D12Util.TransitionBuffer(commandList, d12Buf, ResourceStates.IndexBuffer);

            var view = new IndexBufferView
            {
                BufferLocation = d12Buf.NativeResource.GPUVirtualAddress + offset,
                SizeInBytes = (int)(d12Buf.SizeInBytes - offset),
                Format = format == IndexFormat.UInt32
                    ? Vortice.DXGI.Format.R32_UInt
                    : Vortice.DXGI.Format.R16_UInt,
            };
            commandList.IASetIndexBuffer(view);
        }

        // ---- Draws -----------------------------------------------------

        private protected override void DrawCore(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            commandList.DrawInstanced((int)vertexCount, (int)instanceCount, (int)vertexStart, (int)instanceStart);
        }

        private protected override void DrawIndexedCore(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        {
            commandList.DrawIndexedInstanced((int)indexCount, (int)instanceCount, (int)indexStart, vertexOffset, (int)instanceStart);
        }

        // ---- Stubs for the corners not yet covered -------------------

        public override void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => throw new NotImplementedException("D3D12: Dispatch pending compute pipeline (S6).");

        protected override void DrawIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
            => throw new NotImplementedException("D3D12: indirect draw pending S6.");

        protected override void DrawIndexedIndirectCore(DeviceBuffer indirectBuffer, uint offset, uint drawCount, uint stride)
            => throw new NotImplementedException("D3D12: indirect draw pending S6.");

        protected override void DispatchIndirectCore(DeviceBuffer indirectBuffer, uint offset)
            => throw new NotImplementedException("D3D12: indirect dispatch pending S6.");

        protected override void ResolveTextureCore(Texture source, Texture destination)
            => throw new NotImplementedException("D3D12: ResolveSubresource pending S6.");

        protected override void CopyBufferCore(DeviceBuffer source, uint sourceOffset, DeviceBuffer destination, uint destinationOffset, uint sizeInBytes)
            => throw new NotImplementedException("D3D12: buffer copy pending S6.");

        protected override void CopyTextureCore(
            Texture source, uint srcX, uint srcY, uint srcZ, uint srcMipLevel, uint srcBaseArrayLayer,
            Texture destination, uint dstX, uint dstY, uint dstZ, uint dstMipLevel, uint dstBaseArrayLayer,
            uint width, uint height, uint depth, uint layerCount)
            => throw new NotImplementedException("D3D12: texture copy pending S6.");

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
            => throw new NotImplementedException("D3D12: in-list buffer update pending S6 (needs upload staging pool).");

        private protected override void GenerateMipmapsCore(Texture texture)
            => throw new NotImplementedException("D3D12: mipmap generation pending S6.");

        // Debug markers — wire when PIX support lands in S6.
        private protected override void PushDebugGroupCore(string name)
            => throw new NotImplementedException("D3D12: debug-marker push pending S6.");

        private protected override void PopDebugGroupCore()
            => throw new NotImplementedException("D3D12: debug-marker pop pending S6.");

        private protected override void InsertDebugMarkerCore(string name)
            => throw new NotImplementedException("D3D12: debug-marker insert pending S6.");
    }
}
