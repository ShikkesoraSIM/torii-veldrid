// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 framebuffer — pre-allocates render-target views (RTVs)
    /// and an optional depth-stencil view (DSV) for the bound textures, so
    /// SetFramebuffer at command-recording time becomes a cheap descriptor
    /// table lookup + <c>OMSetRenderTargets</c> call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// RTV / DSV descriptors live in non-shader-visible heaps owned by the
    /// device (<see cref="D3D12GraphicsDevice.RtvAllocator"/> /
    /// <see cref="D3D12GraphicsDevice.DsvAllocator"/>). Each framebuffer
    /// reserves a contiguous run of descriptor slots at construction time
    /// — one per color attachment + one for the depth target if any.
    /// </para>
    /// <para>
    /// The descriptors are never freed back to the heap; for a Torii
    /// session, framebuffer counts stay in the low hundreds (one per
    /// swapchain back-buffer, one per FBO osu-framework creates for
    /// effects), well below the 256-slot RTV / 64-slot DSV caps the
    /// device allocator was sized with. If a long-running session DOES
    /// hit the cap, <see cref="D3D12DescriptorAllocator.Allocate"/>
    /// throws cleanly — easier to debug than a silent driver crash.
    /// </para>
    /// </remarks>
    internal sealed class D3D12Framebuffer : Framebuffer
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>
        /// First RTV descriptor slot index. The N color targets occupy
        /// slots <c>[RtvStart .. RtvStart + ColorTargets.Count)</c> as a
        /// contiguous run, ready to feed
        /// <c>OMSetRenderTargets(numRts, &amp;rtvStart, ...)</c>.
        /// </summary>
        public int RtvStartIndex { get; }

        /// <summary>
        /// CPU handle to the first RTV. Convenience accessor — the
        /// CommandList only needs this + count for OMSetRenderTargets.
        /// </summary>
        public CpuDescriptorHandle RtvHandle { get; }

        /// <summary>
        /// DSV CPU handle, or default-initialised if there's no depth
        /// target on this framebuffer. Check <see cref="HasDepth"/>
        /// before reading.
        /// </summary>
        public CpuDescriptorHandle DsvHandle { get; }

        public bool HasDepth { get; }

        private readonly D3D12GraphicsDevice gd;
        private bool disposed;

        public D3D12Framebuffer(D3D12GraphicsDevice gd, ref FramebufferDescription description)
            : base(description.DepthTarget, description.ColorTargets)
        {
            this.gd = gd;
            Name = string.Empty;

            int colorCount = ColorTargets.Count;

            if (colorCount > 0)
            {
                RtvStartIndex = gd.RtvAllocator.Allocate(colorCount);
                RtvHandle = gd.RtvAllocator.GetCpuHandle(RtvStartIndex);

                // Create one RTV per color attachment. The CPU handles are
                // contiguous, so iterating with descriptorSize advances by
                // one slot each step.
                for (int i = 0; i < colorCount; i++)
                {
                    var attachment = ColorTargets[i];
                    var tex = Util.AssertSubtype<Texture, D3D12Texture>(attachment.Target);
                    var handle = gd.RtvAllocator.GetCpuHandle(RtvStartIndex + i);

                    var rtvDesc = new RenderTargetViewDescription
                    {
                        Format = tex.DxgiFormat,
                        // Default-init by dimension — the union members for
                        // mip / array slice need explicit fill-in via the
                        // Texture2D / Texture3D / TextureArray sub-structs
                        // depending on the resource type. For now we assume
                        // 2D, single mip, single array slice — covers the
                        // common framebuffer case. Multi-slice render
                        // targets land in S6 alongside cubemap framebuffers.
                        ViewDimension = RenderTargetViewDimension.Texture2D,
                        Texture2D = new Texture2DRenderTargetView
                        {
                            MipSlice = (int)attachment.MipLevel,
                            PlaneSlice = 0,
                        }
                    };

                    gd.Device.CreateRenderTargetView(tex.NativeResource, rtvDesc, handle);
                }
            }

            if (description.DepthTarget != null)
            {
                HasDepth = true;
                int dsvIdx = gd.DsvAllocator.Allocate(1);
                DsvHandle = gd.DsvAllocator.GetCpuHandle(dsvIdx);

                var depthAttachment = description.DepthTarget.Value;
                var depthTex = Util.AssertSubtype<Texture, D3D12Texture>(depthAttachment.Target);

                var dsvDesc = new DepthStencilViewDescription
                {
                    Format = depthTex.DxgiFormat,
                    ViewDimension = DepthStencilViewDimension.Texture2D,
                    Flags = DepthStencilViewFlags.None,
                    Texture2D = new Texture2DDepthStencilView
                    {
                        MipSlice = (int)depthAttachment.MipLevel,
                    }
                };

                gd.Device.CreateDepthStencilView(depthTex.NativeResource, dsvDesc, DsvHandle);
            }
        }

        public override void Dispose()
        {
            if (disposed) return;
            // RTV/DSV descriptors are not freed back to the linear
            // allocator — see class XML doc. Dispose is just a flag flip
            // so IsDisposed reports correctly.
            disposed = true;
        }
    }
}
