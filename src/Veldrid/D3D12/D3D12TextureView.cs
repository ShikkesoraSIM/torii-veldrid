// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 texture view — stores the view parameters (sub-resource
    /// range, format override) but does NOT allocate a descriptor heap slot
    /// upfront. Descriptors are baked at <see cref="ResourceSet"/> bind time
    /// in S4, where we know the consuming shader stage + visibility.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In D3D12, an "SRV" (or RTV / DSV / UAV) is a piece of memory in a
    /// descriptor heap that the GPU reads to know "how do I view this
    /// resource?". The view metadata (base mip, mip count, base array layer,
    /// layer count, format) determines how the resource bytes are
    /// interpreted at shader read time. The same underlying texture can
    /// have many different views — different mip ranges, different array
    /// slices, different formats (within DXGI compatibility).
    /// </para>
    /// <para>
    /// We don't allocate descriptors here because:
    /// </para>
    /// <list type="bullet">
    /// <item>Descriptor heaps are a finite per-frame resource — keeping
    /// unused descriptors live just because a TextureView exists wastes them.</item>
    /// <item>The descriptor needs to live in a specific heap (e.g. the
    /// CBV/SRV/UAV heap for shader resources, the RTV heap for render
    /// targets). That decision is made by the ResourceSet at bind time,
    /// not by us.</item>
    /// </list>
    /// <para>
    /// Disposal is trivial — nothing to release.
    /// </para>
    /// </remarks>
    internal sealed class D3D12TextureView : TextureView
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>The texture this view targets.</summary>
        public new D3D12Texture Target { get; }

        private bool disposed;

        public D3D12TextureView(D3D12GraphicsDevice gd, ref TextureViewDescription description)
            : base(ref description)
        {
            Name = string.Empty;
            Target = Util.AssertSubtype<Texture, D3D12Texture>(description.Target);
            // Base class stores BaseMipLevel / MipLevels / BaseArrayLayer /
            // ArrayLayers / Format from the description. We just keep a
            // strongly-typed reference to the target for the bind path.
        }

        public override void Dispose() => disposed = true;
    }
}
