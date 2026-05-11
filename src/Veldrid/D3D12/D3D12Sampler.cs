// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using VorticeFilter = Vortice.Direct3D12.Filter;
using VorticeComparisonFunction = Vortice.Direct3D12.ComparisonFunction;
using VorticeAddressMode = Vortice.Direct3D12.TextureAddressMode;
using VorticeSamplerDescription = Vortice.Direct3D12.SamplerDescription;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 sampler — stores the immutable Vortice sampler description
    /// (ready to be written into a sampler descriptor heap slot) until a
    /// <see cref="ResourceSet"/> binds it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unlike <see cref="D3D12Buffer"/> / <see cref="D3D12Texture"/>, a
    /// sampler is NOT a resource in D3D12 — it has no
    /// <c>ID3D12Resource</c> backing. It's a small immutable struct that
    /// the GPU reads when the shader does a texture sample. Veldrid asks
    /// us to create one up-front; we precompute the Vortice description
    /// here and the ResourceSet writes it into a real descriptor heap slot
    /// lazily in S4.
    /// </para>
    /// <para>
    /// Disposal is trivial — there's no native handle to release.
    /// </para>
    /// <para>
    /// Type-aliases at the top of the file pin every D3D12-side enum /
    /// struct to its <c>Vortice.Direct3D12.*</c> namespace so they don't
    /// collide with the Veldrid types of the same name
    /// (<c>SamplerDescription</c>, <c>Filter</c>, etc. exist in both).
    /// </para>
    /// </remarks>
    internal sealed class D3D12Sampler : Sampler
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        /// <summary>
        /// Precomputed Vortice sampler description, ready to be written into
        /// a sampler descriptor heap slot via
        /// <c>device.CreateSampler(VorticeSamplerDescription, CpuDescriptorHandle)</c>.
        /// </summary>
        public VorticeSamplerDescription D3D12Description { get; }

        private bool disposed;

        public D3D12Sampler(ref SamplerDescription description)
        {
            Name = string.Empty;
            D3D12Description = new VorticeSamplerDescription
            {
                Filter = toD3D12Filter(description.Filter, description.ComparisonKind.HasValue),
                AddressU = toD3D12AddressMode(description.AddressModeU),
                AddressV = toD3D12AddressMode(description.AddressModeV),
                AddressW = toD3D12AddressMode(description.AddressModeW),
                MipLODBias = description.LodBias,
                MaxAnisotropy = (int)description.MaximumAnisotropy,
                ComparisonFunction = description.ComparisonKind.HasValue
                    ? toD3D12ComparisonFunction(description.ComparisonKind.Value)
                    : VorticeComparisonFunction.Never,
                BorderColor = toBorderColor(description.BorderColor),
                MinLOD = description.MinimumLod,
                MaxLOD = description.MaximumLod,
            };
        }

        public override void Dispose() => disposed = true;

        // ---- Veldrid → D3D12 enum maps ----------------------------------
        //
        // Local static helpers because they're only used by Sampler. The
        // shared D3D12Util.cs is reserved for cross-resource helpers.

        private static VorticeFilter toD3D12Filter(SamplerFilter filter, bool comparison)
        {
            // D3D12 has separate Filter enum values for "regular" filters and
            // "comparison" filters (the latter perform a hardware-side
            // depth-compare and return a 0/1 result instead of the sampled
            // value). Veldrid models comparison as a flag on the description;
            // we fork the lookup here.
            if (comparison)
            {
                return filter switch
                {
                    SamplerFilter.MinPointMagPointMipPoint => VorticeFilter.ComparisonMinMagMipPoint,
                    SamplerFilter.MinPointMagPointMipLinear => VorticeFilter.ComparisonMinMagPointMipLinear,
                    SamplerFilter.MinPointMagLinearMipPoint => VorticeFilter.ComparisonMinPointMagLinearMipPoint,
                    SamplerFilter.MinPointMagLinearMipLinear => VorticeFilter.ComparisonMinPointMagMipLinear,
                    SamplerFilter.MinLinearMagPointMipPoint => VorticeFilter.ComparisonMinLinearMagMipPoint,
                    SamplerFilter.MinLinearMagPointMipLinear => VorticeFilter.ComparisonMinLinearMagPointMipLinear,
                    SamplerFilter.MinLinearMagLinearMipPoint => VorticeFilter.ComparisonMinMagLinearMipPoint,
                    SamplerFilter.MinLinearMagLinearMipLinear => VorticeFilter.ComparisonMinMagMipLinear,
                    SamplerFilter.Anisotropic => VorticeFilter.ComparisonAnisotropic,
                    _ => VorticeFilter.ComparisonMinMagMipLinear,
                };
            }

            return filter switch
            {
                SamplerFilter.MinPointMagPointMipPoint => VorticeFilter.MinMagMipPoint,
                SamplerFilter.MinPointMagPointMipLinear => VorticeFilter.MinMagPointMipLinear,
                SamplerFilter.MinPointMagLinearMipPoint => VorticeFilter.MinPointMagLinearMipPoint,
                SamplerFilter.MinPointMagLinearMipLinear => VorticeFilter.MinPointMagMipLinear,
                SamplerFilter.MinLinearMagPointMipPoint => VorticeFilter.MinLinearMagMipPoint,
                SamplerFilter.MinLinearMagPointMipLinear => VorticeFilter.MinLinearMagPointMipLinear,
                SamplerFilter.MinLinearMagLinearMipPoint => VorticeFilter.MinMagLinearMipPoint,
                SamplerFilter.MinLinearMagLinearMipLinear => VorticeFilter.MinMagMipLinear,
                SamplerFilter.Anisotropic => VorticeFilter.Anisotropic,
                _ => VorticeFilter.MinMagMipLinear,
            };
        }

        private static VorticeAddressMode toD3D12AddressMode(SamplerAddressMode mode)
            => mode switch
            {
                SamplerAddressMode.Wrap => VorticeAddressMode.Wrap,
                SamplerAddressMode.Mirror => VorticeAddressMode.Mirror,
                SamplerAddressMode.Clamp => VorticeAddressMode.Clamp,
                SamplerAddressMode.Border => VorticeAddressMode.Border,
                _ => VorticeAddressMode.Wrap,
            };

        private static VorticeComparisonFunction toD3D12ComparisonFunction(ComparisonKind kind)
            => kind switch
            {
                ComparisonKind.Never => VorticeComparisonFunction.Never,
                ComparisonKind.Less => VorticeComparisonFunction.Less,
                ComparisonKind.Equal => VorticeComparisonFunction.Equal,
                ComparisonKind.LessEqual => VorticeComparisonFunction.LessEqual,
                ComparisonKind.Greater => VorticeComparisonFunction.Greater,
                ComparisonKind.NotEqual => VorticeComparisonFunction.NotEqual,
                ComparisonKind.GreaterEqual => VorticeComparisonFunction.GreaterEqual,
                ComparisonKind.Always => VorticeComparisonFunction.Always,
                _ => VorticeComparisonFunction.Always,
            };

        private static Vortice.Mathematics.Color4 toBorderColor(SamplerBorderColor color)
            => color switch
            {
                SamplerBorderColor.TransparentBlack => new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f),
                SamplerBorderColor.OpaqueBlack => new Vortice.Mathematics.Color4(0f, 0f, 0f, 1f),
                SamplerBorderColor.OpaqueWhite => new Vortice.Mathematics.Color4(1f, 1f, 1f, 1f),
                _ => new Vortice.Mathematics.Color4(0f, 0f, 0f, 0f),
            };
    }
}
