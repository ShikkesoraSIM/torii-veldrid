// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 resource layout — stores the
    /// <see cref="ResourceLayoutElementDescription"/> array as-is, plus a
    /// pre-computed split between CBV/SRV/UAV and Sampler counts so the
    /// root-signature builder doesn't have to re-walk the list each time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In D3D12 root-signature land, samplers and non-sampler resources
    /// MUST live in separate descriptor tables (because they live in
    /// separate descriptor heap TYPES). For each Veldrid ResourceLayout
    /// we therefore generate two descriptor tables in the root signature:
    /// one CBV/SRV/UAV table and one Sampler table (omitted if empty).
    /// This class precomputes the counts so the pipeline-builder can size
    /// the tables and the ResourceSet can allocate the matching descriptor
    /// ranges without re-counting.
    /// </para>
    /// <para>
    /// The element order is preserved exactly — Veldrid's contract is that
    /// the i-th BoundResource in a ResourceSet matches the i-th element
    /// here. The descriptor-table slot a resource lands in is
    /// determined by walking the elements in order and skipping over the
    /// "wrong type" entries (samplers when building the CBV/SRV/UAV table,
    /// and vice versa). The "TableIndexFor" helpers below codify that walk
    /// so callers don't reinvent it.
    /// </para>
    /// </remarks>
    internal sealed class D3D12ResourceLayout : ResourceLayout
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        public ResourceLayoutElementDescription[] Elements { get; }

        /// <summary>Number of CBV/SRV/UAV slots in this layout.</summary>
        public int CbvSrvUavCount { get; }

        /// <summary>Number of Sampler slots in this layout.</summary>
        public int SamplerCount { get; }

        private bool disposed;

        public D3D12ResourceLayout(ref ResourceLayoutDescription description)
            : base(ref description)
        {
            Name = string.Empty;
            Elements = description.Elements;

            int cbvSrvUav = 0;
            int samplers = 0;
            for (int i = 0; i < Elements.Length; i++)
            {
                if (IsSamplerKind(Elements[i].Kind)) samplers++;
                else cbvSrvUav++;
            }
            CbvSrvUavCount = cbvSrvUav;
            SamplerCount = samplers;
        }

        /// <summary>
        /// True for resource kinds that live in the Sampler descriptor heap.
        /// Used by the layout / pipeline / resourceset code to fork between
        /// the two parallel descriptor tables.
        /// </summary>
        public static bool IsSamplerKind(ResourceKind kind) => kind == ResourceKind.Sampler;

        /// <summary>
        /// Index of the i-th element in its corresponding descriptor table.
        /// E.g. a layout of [CBV, Sampler, SRV, Sampler] maps to:
        ///   element 0 → CbvSrvUav table slot 0
        ///   element 1 → Sampler table slot 0
        ///   element 2 → CbvSrvUav table slot 1
        ///   element 3 → Sampler table slot 1
        /// </summary>
        public int GetTableSlot(int elementIndex)
        {
            bool wantSampler = IsSamplerKind(Elements[elementIndex].Kind);
            int slot = 0;
            for (int i = 0; i < elementIndex; i++)
            {
                if (IsSamplerKind(Elements[i].Kind) == wantSampler) slot++;
            }
            return slot;
        }

        public override void Dispose() => disposed = true;
    }
}
