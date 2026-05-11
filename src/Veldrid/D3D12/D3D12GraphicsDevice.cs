// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using VorticeD3D12 = Vortice.Direct3D12.D3D12;
using VorticeDXGI = Vortice.DXGI.DXGI;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 backend for Veldrid. **Scaffold (session 1)** — the device
    /// instantiates and disposes cleanly, but every rendering / resource-creation
    /// path throws <see cref="NotImplementedException"/>. Subsequent commits fill
    /// these in incrementally (CommandList, Pipeline, ResourceFactory, etc.).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reference: <see cref="Veldrid.D3D11.D3D11GraphicsDevice"/>. The D3D12
    /// implementation mirrors the same constructor / dispose / feature-flag
    /// shape, with the differences D3D12 enforces over D3D11:
    /// </para>
    /// <list type="bullet">
    /// <item>No immediate context — work goes through one or more
    /// <see cref="ID3D12CommandQueue"/>s explicitly created by the device.</item>
    /// <item>Resource state transitions are manual via
    /// <see cref="ResourceBarrier"/>; the Veldrid layer hides this from
    /// callers by tracking state on each <see cref="Texture"/> / <see cref="DeviceBuffer"/>.</item>
    /// <item>Descriptor heaps replace D3D11's flat resource binding — the
    /// Veldrid <see cref="ResourceSet"/> maps onto a contiguous range in a
    /// CBV/SRV/UAV heap.</item>
    /// <item>Shaders compile to DXIL via the new DirectX Shader Compiler
    /// (dxc) rather than the legacy D3DCompile path used by D3D11.</item>
    /// </list>
    /// <para>
    /// All of these are deferred to follow-up commits. This file establishes
    /// the device-creation handshake (Vortice → D3D12.D3D12CreateDevice →
    /// IDXGIAdapter walk) and the dispose path, so build / link / load /
    /// initialise round-trips cleanly end-to-end before any rendering code
    /// is written.
    /// </para>
    /// </remarks>
    internal class D3D12GraphicsDevice : GraphicsDevice
    {
        public override string DeviceName { get; }
        public override string VendorName { get; }
        public override GraphicsApiVersion ApiVersion { get; }
        public override GraphicsBackend BackendType => GraphicsBackend.Direct3D12;

        // D3D12 maps to the same UV / depth / clip-space conventions as D3D11
        // (left-handed coordinate system, [0, 1] depth, top-left UV origin).
        // These propagate up to osu-framework's renderer feature flags and
        // ensure mods/shaders authored against D3D11 work unchanged.
        public override bool IsUvOriginTopLeft => true;
        public override bool IsDepthRangeZeroToOne => true;
        public override bool IsClipSpaceYInverted => false;

        public override ResourceFactory ResourceFactory => d3d12ResourceFactory;

        // Main swapchain is created on demand via CreateSwapchain; the device
        // itself doesn't own one unless a SwapchainDescription was passed at
        // construction time. Scaffold returns null — implementation comes when
        // we wire D3D12Swapchain.
        public override Swapchain MainSwapchain => null!;

        public override GraphicsDeviceFeatures Features { get; }

        public ID3D12Device Device => device;
        public IDXGIAdapter Adapter => dxgiAdapter;
        public bool IsDebugEnabled { get; }

        private readonly ID3D12Device device;
        private readonly IDXGIAdapter dxgiAdapter;
        private readonly D3D12ResourceFactory d3d12ResourceFactory;

        public D3D12GraphicsDevice(GraphicsDeviceOptions options, SwapchainDescription? swapchainDesc)
        {
            // Enable the D3D12 debug layer in DEBUG builds when the SDK
            // layers are installed locally. Mirrors D3D11GraphicsDevice's
            // pattern — release builds never pay the validation cost.
            // Explicit `<ID3D12Debug>` type arg because the generic helper's
            // ComObject constraint can't be satisfied from a nullable target
            // via inference alone.
#if DEBUG
            if (VorticeD3D12.D3D12GetDebugInterface<ID3D12Debug>(out var debugInterface).Success)
            {
                debugInterface!.EnableDebugLayer();
                debugInterface.Dispose();
                IsDebugEnabled = true;
            }
#endif

            // Walk DXGI adapters and create the device on the first one that
            // supports feature level 11_0 minimum (D3D12's effective floor —
            // anything older predates the API). EnumAdapters1 takes int (not
            // uint) for the adapter index in Vortice's binding.
            VorticeDXGI.CreateDXGIFactory1(out IDXGIFactory4? factory).CheckError();

            using (factory!)
            {
                for (int adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Success; adapterIndex++)
                {
                    using (adapter)
                    {
                        var adapterDesc = adapter.Description1;

                        // Skip the software / WARP adapter unless every other
                        // option failed — caller can fall through to it via
                        // factory.EnumWarpAdapter later if desired.
                        if ((adapterDesc.Flags & AdapterFlags.Software) != 0)
                            continue;

                        if (VorticeD3D12.D3D12CreateDevice(adapter, FeatureLevel.Level_11_0, out ID3D12Device? createdDevice).Success)
                        {
                            device = createdDevice!;
                            dxgiAdapter = adapter.QueryInterface<IDXGIAdapter>();
                            DeviceName = adapterDesc.Description;
                            VendorName = "id:" + ((uint)adapterDesc.VendorId).ToString("x8");
                            break;
                        }
                    }
                }
            }

            if (device == null)
                throw new VeldridException("No DXGI adapter supports Direct3D 12 feature level 11_0.");

            // Scaffold: ApiVersion is hardcoded to 12.0 for now. A proper
            // probe through device.CheckFeatureSupport(FeatureLevels) needs
            // pinned-pointer marshalling for the FeatureLevelsRequested
            // field (it's exposed as raw `nint` in Vortice 2.4.2, not as a
            // managed array). Punted to a later session — the ApiVersion
            // value is informational, doesn't gate rendering.
            ApiVersion = new GraphicsApiVersion(12, 0, 0, 0);

            // Feature flags — D3D12 broadly subsumes D3D11's capability surface
            // (it's strictly newer hardware), but the scaffold sets a
            // conservative-but-D3D11-equivalent baseline. Specialised features
            // (mesh shaders, raytracing, sampler feedback, etc.) are exposed
            // through BackendInfo-level APIs in future commits, not here.
            Features = new GraphicsDeviceFeatures(
                computeShader: true,
                geometryShader: true,
                tessellationShaders: true,
                multipleViewports: true,
                samplerLodBias: true,
                drawBaseVertex: true,
                drawBaseInstance: true,
                drawIndirect: true,
                drawIndirectBaseInstance: true,
                fillModeWireframe: true,
                samplerAnisotropy: true,
                depthClipDisable: true,
                texture1D: true,
                independentBlend: true,
                structuredBuffer: true,
                subsetTextureView: true,
                commandListDebugMarkers: true,
                bufferRangeBinding: true,
                shaderFloat64: false);

            d3d12ResourceFactory = new D3D12ResourceFactory(this);

            PostDeviceCreated();
        }

        // ---- Abstract overrides — scaffold stubs ------------------------
        //
        // The following methods throw NotImplementedException. Each will be
        // filled in by a follow-up commit dedicated to that subsystem:
        //
        //   Session 2 → Command queue + CommandList + SubmitCommandsCore
        //   Session 3 → Resource creation (Buffer, Texture, Sampler) + state
        //               transitions
        //   Session 4 → Pipeline + RootSignature + Shader (DXIL compile)
        //   Session 5 → Swapchain + frame presentation
        //   Session 6+ → Fence sync, format-support probes, mapping,
        //                BackendInfoD3D12, debug-marker plumbing
        //
        // Until then, anything that tries to actually USE a D3D12 device
        // for rendering will throw — but the device CAN be constructed
        // and disposed, which is what session 1 is proving end-to-end.

        public override TextureSampleCount GetSampleCountLimit(PixelFormat format, bool depthFormat)
            => throw new NotImplementedException("D3D12: GetSampleCountLimit pending.");

        public override bool WaitForFence(Fence fence, ulong nanosecondTimeout)
            => throw new NotImplementedException("D3D12: fence sync pending.");

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
            => throw new NotImplementedException("D3D12: fence sync pending.");

        public override void ResetFence(Fence fence)
            => throw new NotImplementedException("D3D12: fence sync pending.");

        protected override MappedResource MapCore(IMappableResource resource, MapMode mode, uint subresource)
            => throw new NotImplementedException("D3D12: resource mapping pending.");

        protected override void UnmapCore(IMappableResource resource, uint subresource)
            => throw new NotImplementedException("D3D12: resource mapping pending.");

        protected override void PlatformDispose()
        {
            // Order matters: release adapter LAST so DXGI can still observe
            // the device handle while it's being torn down.
            dxgiAdapter?.Dispose();
            device?.Dispose();
        }

        private protected override void SubmitCommandsCore(CommandList commandList, Fence fence)
            => throw new NotImplementedException("D3D12: command submission pending.");

        private protected override void SwapBuffersCore(Swapchain swapchain)
            => throw new NotImplementedException("D3D12: swapchain present pending.");

        private protected override void WaitForIdleCore()
            => throw new NotImplementedException("D3D12: queue wait-idle pending.");

        private protected override void WaitForNextFrameReadyCore()
            => throw new NotImplementedException("D3D12: frame pacing pending.");

        private protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x, uint y, uint z,
            uint width, uint height, uint depth,
            uint mipLevel, uint arrayLayer)
            => throw new NotImplementedException("D3D12: texture upload pending.");

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
            => throw new NotImplementedException("D3D12: buffer upload pending.");

        private protected override bool GetPixelFormatSupportCore(
            PixelFormat format,
            TextureType type,
            TextureUsage usage,
            out PixelFormatProperties properties)
        {
            // Return a permissive default for now so callers querying support
            // before any real rendering happens don't crash. Real probe via
            // CheckFeatureSupport(Format) lands in a follow-up commit.
            properties = new PixelFormatProperties(
                maxWidth: 16384,
                maxHeight: 16384,
                maxDepth: 2048,
                maxMipLevels: 15,
                maxArrayLayers: 2048,
                sampleCounts: 0xF);
            return true;
        }

        // D3D12 constant-buffer view (CBV) alignment is 256 bytes — same as
        // D3D11's UniformBuffer alignment. Structured-buffer SRVs have no
        // intrinsic offset alignment requirement in the API itself, but the
        // safest cross-driver floor is 16 bytes (matches D3D11's choice).
        // Constants here rather than property gymnastics — these never
        // change per-device on D3D12.
        internal override uint GetUniformBufferMinOffsetAlignmentCore() => 256;
        internal override uint GetStructuredBufferMinOffsetAlignmentCore() => 16;
    }
}
