// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 resource factory. **Scaffold (session 1)** — every Create*
    /// override throws <see cref="NotImplementedException"/>. Resource backing
    /// (Buffer, Texture, Sampler, Pipeline, Shader, etc.) lands in follow-up
    /// commits as we work down through the rendering hot path.
    /// </summary>
    /// <remarks>
    /// Mirrors the layout of <see cref="Veldrid.D3D11.D3D11ResourceFactory"/>.
    /// Holds a back-reference to the owning <see cref="D3D12GraphicsDevice"/>
    /// because most D3D12 resource creation needs the underlying
    /// <c>ID3D12Device</c> handle and the device's heap allocators (which
    /// don't exist yet in session 1 — see the device's
    /// <see cref="D3D12GraphicsDevice.PlatformDispose"/> scaffold notes).
    /// </remarks>
    internal class D3D12ResourceFactory : ResourceFactory
    {
        public override GraphicsBackend BackendType => GraphicsBackend.Direct3D12;

        private readonly D3D12GraphicsDevice gd;

        public D3D12ResourceFactory(D3D12GraphicsDevice gd)
            : base(gd.Features)
        {
            this.gd = gd;
        }

        public override Pipeline CreateComputePipeline(ref ComputePipelineDescription description)
            => throw new NotImplementedException("D3D12: compute pipeline pending.");

        public override Framebuffer CreateFramebuffer(ref FramebufferDescription description)
            => new D3D12Framebuffer(gd, ref description);

        public override CommandList CreateCommandList(ref CommandListDescription description)
            => new D3D12CommandList(gd, ref description);

        public override ResourceLayout CreateResourceLayout(ref ResourceLayoutDescription description)
            => new D3D12ResourceLayout(ref description);

        public override ResourceSet CreateResourceSet(ref ResourceSetDescription description)
            => new D3D12ResourceSet(gd, ref description);

        public override Fence CreateFence(bool signaled)
            => new D3D12Fence(gd.Device, signaled);

        public override Swapchain CreateSwapchain(ref SwapchainDescription description)
            => throw new NotImplementedException("D3D12: swapchain pending.");

        protected override Pipeline CreateGraphicsPipelineCore(ref GraphicsPipelineDescription description)
            => new D3D12Pipeline(gd, ref description);

        protected override Texture CreateTextureCore(ulong nativeTexture, ref TextureDescription description)
            => throw new NotImplementedException("D3D12: native-texture import pending (S5 — needed for swapchain back-buffer wrap).");

        protected override Texture CreateTextureCore(ref TextureDescription description)
            => new D3D12Texture(gd, ref description);

        protected override TextureView CreateTextureViewCore(ref TextureViewDescription description)
            => new D3D12TextureView(gd, ref description);

        protected override DeviceBuffer CreateBufferCore(ref BufferDescription description)
            => new D3D12Buffer(gd, ref description);

        protected override Sampler CreateSamplerCore(ref SamplerDescription description)
            => new D3D12Sampler(ref description);

        protected override Shader CreateShaderCore(ref ShaderDescription description)
            => new D3D12Shader(ref description);
    }
}
