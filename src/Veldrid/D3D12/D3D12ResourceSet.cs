// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 resource set — bakes a Veldrid
    /// <see cref="ResourceSetDescription"/> into descriptor heap slots,
    /// ready for binding as one or two descriptor tables in the root
    /// signature at <c>SetGraphicsRootDescriptorTable</c> time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Allocation strategy: at construction time we reserve a contiguous
    /// run of CBV/SRV/UAV descriptors (count =
    /// <see cref="D3D12ResourceLayout.CbvSrvUavCount"/>) and optionally a
    /// contiguous run of Sampler descriptors. The bound resources are
    /// walked once and each gets its descriptor written into the right
    /// slot via the matching <c>CreateXxxView</c> call.
    /// </para>
    /// <para>
    /// The CommandList retrieves the start GPU handle for each table via
    /// <see cref="CbvSrvUavGpuStart"/> / <see cref="SamplerGpuStart"/> and
    /// binds it to the root parameter index the Pipeline reserved for
    /// this layout. Multiple ResourceSets can share heap content because
    /// the descriptors are heap-allocator-linear: every set gets its own
    /// disjoint range.
    /// </para>
    /// <para>
    /// Held resources (textures + buffers) are exposed via
    /// <see cref="BoundResources"/> so the CommandList's state-transition
    /// pass can walk them at bind time and emit <c>ResourceBarrier</c>s
    /// into the correct shader-readable state before the draw.
    /// </para>
    /// </remarks>
    internal sealed class D3D12ResourceSet : ResourceSet
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        public D3D12ResourceLayout Layout { get; }
        public IBindableResource[] BoundResources { get; }

        /// <summary>Start GPU handle of the CBV/SRV/UAV descriptor table for this set. Default if no non-sampler resources.</summary>
        public GpuDescriptorHandle CbvSrvUavGpuStart { get; }

        /// <summary>Start GPU handle of the Sampler descriptor table for this set. Default if no samplers.</summary>
        public GpuDescriptorHandle SamplerGpuStart { get; }

        public bool HasCbvSrvUavTable => Layout.CbvSrvUavCount > 0;
        public bool HasSamplerTable => Layout.SamplerCount > 0;

        private bool disposed;

        public D3D12ResourceSet(D3D12GraphicsDevice gd, ref ResourceSetDescription description)
            : base(ref description)
        {
            Name = string.Empty;
            Layout = Util.AssertSubtype<ResourceLayout, D3D12ResourceLayout>(description.Layout);
            BoundResources = description.BoundResources;

            int cbvSrvUavStart = HasCbvSrvUavTable ? gd.CbvSrvUavAllocator.Allocate(Layout.CbvSrvUavCount) : 0;
            int samplerStart = HasSamplerTable ? gd.SamplerAllocator.Allocate(Layout.SamplerCount) : 0;

            if (HasCbvSrvUavTable)
                CbvSrvUavGpuStart = gd.CbvSrvUavAllocator.GetGpuHandle(cbvSrvUavStart);
            if (HasSamplerTable)
                SamplerGpuStart = gd.SamplerAllocator.GetGpuHandle(samplerStart);

            // Walk the bound resources in element order, writing each
            // descriptor into the right slot of the right table.
            for (int i = 0; i < BoundResources.Length; i++)
            {
                var element = Layout.Elements[i];
                int tableSlot = Layout.GetTableSlot(i);

                switch (element.Kind)
                {
                    case ResourceKind.UniformBuffer:
                        writeCbv(gd, BoundResources[i], cbvSrvUavStart + tableSlot);
                        break;

                    case ResourceKind.StructuredBufferReadOnly:
                    case ResourceKind.StructuredBufferReadWrite:
                        writeBufferSrvOrUav(gd, BoundResources[i], cbvSrvUavStart + tableSlot, element.Kind);
                        break;

                    case ResourceKind.TextureReadOnly:
                        writeTextureSrv(gd, BoundResources[i], cbvSrvUavStart + tableSlot);
                        break;

                    case ResourceKind.TextureReadWrite:
                        writeTextureUav(gd, BoundResources[i], cbvSrvUavStart + tableSlot);
                        break;

                    case ResourceKind.Sampler:
                        writeSampler(gd, BoundResources[i], samplerStart + tableSlot);
                        break;
                }
            }
        }

        private static void writeCbv(D3D12GraphicsDevice gd, IBindableResource resource, int descriptorIndex)
        {
            var buf = (D3D12Buffer)resource;
            var desc = new ConstantBufferViewDescription
            {
                BufferLocation = buf.NativeResource.GPUVirtualAddress,
                SizeInBytes = (int)buf.PaddedSizeInBytes,
            };
            gd.Device.CreateConstantBufferView(desc, gd.CbvSrvUavAllocator.GetCpuHandle(descriptorIndex));
        }

        private static void writeBufferSrvOrUav(D3D12GraphicsDevice gd, IBindableResource resource, int descriptorIndex, ResourceKind kind)
        {
            var buf = (D3D12Buffer)resource;
            var cpuHandle = gd.CbvSrvUavAllocator.GetCpuHandle(descriptorIndex);

            // Structured buffers in D3D12 need a stride hint — assume 16
            // bytes per element for now (matches the typical R32G32B32A32
            // float vec4 shape osu-framework uses). A proper stride would
            // come from the shader reflection or an explicit Veldrid field;
            // adding that surface is a future enhancement.
            const int default_stride = 16;
            int elementCount = (int)(buf.SizeInBytes / default_stride);

            if (kind == ResourceKind.StructuredBufferReadOnly)
            {
                var srvDesc = new ShaderResourceViewDescription
                {
                    Format = Vortice.DXGI.Format.Unknown,
                    ViewDimension = ShaderResourceViewDimension.Buffer,
                    Shader4ComponentMapping = ShaderComponentMapping.Default,
                    Buffer = new BufferShaderResourceView
                    {
                        FirstElement = 0,
                        NumElements = elementCount,
                        StructureByteStride = default_stride,
                        Flags = BufferShaderResourceViewFlags.None,
                    }
                };
                gd.Device.CreateShaderResourceView(buf.NativeResource, srvDesc, cpuHandle);
            }
            else
            {
                var uavDesc = new UnorderedAccessViewDescription
                {
                    Format = Vortice.DXGI.Format.Unknown,
                    ViewDimension = UnorderedAccessViewDimension.Buffer,
                    Buffer = new BufferUnorderedAccessView
                    {
                        FirstElement = 0,
                        NumElements = elementCount,
                        StructureByteStride = default_stride,
                        Flags = BufferUnorderedAccessViewFlags.None,
                        CounterOffsetInBytes = 0,
                    }
                };
                gd.Device.CreateUnorderedAccessView(buf.NativeResource, null, uavDesc, cpuHandle);
            }
        }

        private static void writeTextureSrv(D3D12GraphicsDevice gd, IBindableResource resource, int descriptorIndex)
        {
            // The bound resource may be a raw Texture (full-coverage view)
            // or a TextureView (subset). Handle both.
            D3D12Texture tex;
            uint baseMip, mipCount, baseArrayLayer, arrayLayers;
            Vortice.DXGI.Format format;

            if (resource is D3D12TextureView view)
            {
                tex = view.Target;
                baseMip = view.BaseMipLevel;
                mipCount = view.MipLevels;
                baseArrayLayer = view.BaseArrayLayer;
                arrayLayers = view.ArrayLayers;
                format = tex.DxgiFormat;
            }
            else
            {
                tex = (D3D12Texture)resource;
                baseMip = 0;
                mipCount = tex.MipLevels;
                baseArrayLayer = 0;
                arrayLayers = tex.ArrayLayers;
                format = tex.DxgiFormat;
            }

            var srvDesc = new ShaderResourceViewDescription
            {
                Format = format,
                ViewDimension = ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = ShaderComponentMapping.Default,
                Texture2D = new Texture2DShaderResourceView
                {
                    MostDetailedMip = (int)baseMip,
                    MipLevels = (int)mipCount,
                    PlaneSlice = 0,
                    ResourceMinLODClamp = 0.0f,
                }
            };
            gd.Device.CreateShaderResourceView(tex.NativeResource, srvDesc, gd.CbvSrvUavAllocator.GetCpuHandle(descriptorIndex));
        }

        private static void writeTextureUav(D3D12GraphicsDevice gd, IBindableResource resource, int descriptorIndex)
        {
            D3D12Texture tex = resource is D3D12TextureView v ? v.Target : (D3D12Texture)resource;
            var uavDesc = new UnorderedAccessViewDescription
            {
                Format = tex.DxgiFormat,
                ViewDimension = UnorderedAccessViewDimension.Texture2D,
                Texture2D = new Texture2DUnorderedAccessView { MipSlice = 0, PlaneSlice = 0 }
            };
            gd.Device.CreateUnorderedAccessView(tex.NativeResource, null, uavDesc, gd.CbvSrvUavAllocator.GetCpuHandle(descriptorIndex));
        }

        private static void writeSampler(D3D12GraphicsDevice gd, IBindableResource resource, int descriptorIndex)
        {
            var sampler = (D3D12Sampler)resource;
            // CreateSampler takes the description by `ref` in Vortice 2.4.2 —
            // local copy so the readonly property can be passed.
            var desc = sampler.D3D12Description;
            gd.Device.CreateSampler(ref desc, gd.SamplerAllocator.GetCpuHandle(descriptorIndex));
        }

        public override void Dispose()
        {
            if (disposed) return;
            // Descriptors are not freed back to the linear allocator — see
            // the allocator's class XML doc. Dispose flips the flag only.
            disposed = true;
        }
    }
}
