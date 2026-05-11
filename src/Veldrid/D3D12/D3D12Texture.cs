// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 texture — wraps an <see cref="ID3D12Resource"/> for 1D /
    /// 2D / 3D / cubemap textures with mipmaps and array slices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 has a single <see cref="ResourceDescription"/> shape for all
    /// texture dimensions; the <see cref="ResourceDimension"/> field picks
    /// the variant and <see cref="ResourceDescription.DepthOrArraySize"/>
    /// doubles as "depth for 3D" or "array layer count for 2D/cube".
    /// Cube maps in D3D12 are just 2D arrays with 6 layers — the cube
    /// semantic appears at the view level (SRV with DimensionTextureCube),
    /// not the resource level.
    /// </para>
    /// <para>
    /// State tracking: every texture has a <see cref="CurrentState"/> that
    /// the transition emitter updates as the resource moves between
    /// RENDER_TARGET / SHADER_RESOURCE / COPY_DEST / DEPTH_WRITE / etc.
    /// Initial state is chosen from the usage flags: render-targets start
    /// in RENDER_TARGET (matches D3D12's expectation when the resource is
    /// first bound as a target), depth start in DEPTH_WRITE, everything
    /// else starts in COMMON.
    /// </para>
    /// <para>
    /// Format handling: the resource holds the DXGI format derived from the
    /// Veldrid PixelFormat. Views may use a *compatible* DXGI format (e.g.
    /// a depth texture's resource is typeless when the texture is also
    /// sampled). Typeless-resource handling is deferred to S4 alongside
    /// TextureView wiring. For S3 the resource format is the typed format
    /// straight from <see cref="D3D12Formats.ToDxgiFormat"/>.
    /// </para>
    /// </remarks>
    internal sealed class D3D12Texture : Texture
    {
        public override PixelFormat Format { get; }
        public override uint Width { get; }
        public override uint Height { get; }
        public override uint Depth { get; }
        public override uint MipLevels { get; }
        public override uint ArrayLayers { get; }
        public override TextureUsage Usage { get; }
        public override TextureType Type { get; }
        public override TextureSampleCount SampleCount { get; }
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>The wrapped D3D12 resource.</summary>
        public ID3D12Resource NativeResource => resource;

        /// <summary>DXGI format the resource was created with.</summary>
        public Format DxgiFormat { get; }

        /// <summary>Heap the texture was committed into. Always DEFAULT for now.</summary>
        public HeapType HeapType { get; }

        /// <summary>
        /// Current resource state. Driven by the transition emitter as the
        /// CommandList records work that needs this texture in a different
        /// state. External code MUST NOT mutate this directly — go through
        /// the transition helper so the corresponding ResourceBarrier
        /// actually fires.
        /// </summary>
        public ResourceStates CurrentState { get; set; }

        private readonly ID3D12Resource resource;
        private readonly bool ownsResource;
        private bool disposed;

        /// <summary>
        /// Wrap an externally-owned <see cref="ID3D12Resource"/> (e.g. a
        /// DXGI swapchain back-buffer) as a Veldrid texture. The wrapped
        /// resource is NOT released by <see cref="DisposeCore"/> — DXGI
        /// owns swapchain back-buffers and would crash if we double-freed.
        /// </summary>
        public D3D12Texture(
            ID3D12Resource externalResource,
            uint width,
            uint height,
            PixelFormat format,
            TextureUsage usage,
            ResourceStates initialState)
        {
            Name = string.Empty;
            resource = externalResource;
            ownsResource = false;

            Format = format;
            Width = width;
            Height = height;
            Depth = 1;
            MipLevels = 1;
            ArrayLayers = 1;
            Usage = usage;
            Type = TextureType.Texture2D;
            SampleCount = TextureSampleCount.Count1;
            DxgiFormat = D3D12Formats.ToDxgiFormat(format, depthFormat: (usage & TextureUsage.DepthStencil) != 0);
            HeapType = HeapType.Default;
            CurrentState = initialState;
        }

        public D3D12Texture(D3D12GraphicsDevice gd, ref TextureDescription description)
        {
            ownsResource = true;
            Name = string.Empty;
            Format = description.Format;
            Width = description.Width;
            Height = description.Height;
            Depth = description.Depth;
            MipLevels = description.MipLevels;
            ArrayLayers = description.ArrayLayers;
            Usage = description.Usage;
            Type = description.Type;
            SampleCount = description.SampleCount;

            bool isDepth = (description.Usage & TextureUsage.DepthStencil) != 0;
            DxgiFormat = D3D12Formats.ToDxgiFormat(description.Format, isDepth);
            HeapType = HeapType.Default;

            // ResourceDescription shape varies by Type. D3D12's enum is one
            // monolithic struct so we set the fields per dimension here.
            var resourceDesc = new ResourceDescription
            {
                Dimension = description.Type switch
                {
                    TextureType.Texture1D => ResourceDimension.Texture1D,
                    TextureType.Texture2D => ResourceDimension.Texture2D,
                    TextureType.Texture3D => ResourceDimension.Texture3D,
                    _ => ResourceDimension.Texture2D
                },
                Width = description.Width,
                // ResourceDescription.Height is int in Vortice's binding even
                // though semantically a positive count — cast explicitly.
                Height = (int)description.Height,
                DepthOrArraySize = description.Type == TextureType.Texture3D
                    ? (ushort)description.Depth
                    : (ushort)Math.Max(1u, description.ArrayLayers),
                MipLevels = (ushort)Math.Max(1u, description.MipLevels),
                Format = DxgiFormat,
                SampleDescription = new SampleDescription(toSampleCount(description.SampleCount), 0),
                Layout = TextureLayout.Unknown,
                Flags = ResourceFlags.None,
                Alignment = 0,
            };

            // Usage-driven flags. These all have to be set at create time;
            // D3D12 won't let you opt in to RenderTarget / DepthStencil /
            // UnorderedAccess later.
            if ((description.Usage & TextureUsage.RenderTarget) != 0)
                resourceDesc.Flags |= ResourceFlags.AllowRenderTarget;
            if ((description.Usage & TextureUsage.DepthStencil) != 0)
                resourceDesc.Flags |= ResourceFlags.AllowDepthStencil;
            if ((description.Usage & TextureUsage.Storage) != 0)
                resourceDesc.Flags |= ResourceFlags.AllowUnorderedAccess;

            // Initial state: born in the role you'll first use them as.
            // Render targets start in RENDER_TARGET (so the first SetFramebuffer
            // bind is a no-op), depth in DEPTH_WRITE, everything else in COMMON
            // (a permissive state that's legal to transition out of into
            // anything in D3D12's barrier matrix).
            if ((description.Usage & TextureUsage.RenderTarget) != 0)
                CurrentState = ResourceStates.RenderTarget;
            else if ((description.Usage & TextureUsage.DepthStencil) != 0)
                CurrentState = ResourceStates.DepthWrite;
            else
                CurrentState = ResourceStates.Common;

            // Render targets and depth-stencil targets benefit from an
            // "optimized clear value" hint at create time — D3D12 uses it to
            // pre-compute a fast-clear value for the resource. We pass black
            // for RTV and (1.0, 0) for DSV which match the most-common
            // application clears.
            ClearValue? clearValue = null;
            if ((description.Usage & TextureUsage.RenderTarget) != 0)
            {
                clearValue = new ClearValue
                {
                    Format = DxgiFormat,
                    Color = new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f),
                };
            }
            else if ((description.Usage & TextureUsage.DepthStencil) != 0)
            {
                clearValue = new ClearValue
                {
                    Format = DxgiFormat,
                    DepthStencil = new DepthStencilValue(1.0f, 0),
                };
            }

            resource = gd.Device.CreateCommittedResource(
                new HeapProperties(HeapType),
                HeapFlags.None,
                resourceDesc,
                CurrentState,
                clearValue);
        }

        private static int toSampleCount(TextureSampleCount samples)
            => samples switch
            {
                TextureSampleCount.Count1 => 1,
                TextureSampleCount.Count2 => 2,
                TextureSampleCount.Count4 => 4,
                TextureSampleCount.Count8 => 8,
                TextureSampleCount.Count16 => 16,
                TextureSampleCount.Count32 => 32,
                _ => 1,
            };

        private protected override void DisposeCore()
        {
            if (disposed) return;
            // Only release the underlying resource when WE allocated it.
            // External wraps (e.g. swapchain back-buffers from DXGI) MUST
            // NOT be released here — DXGI owns those and double-freeing
            // crashes the driver. The swapchain handles their lifetime
            // explicitly via IDXGISwapChain.Dispose().
            if (ownsResource)
                resource.Dispose();
            disposed = true;
        }
    }
}
