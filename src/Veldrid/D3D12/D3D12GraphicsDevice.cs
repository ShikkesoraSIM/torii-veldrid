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

        /// <summary>
        /// Direct command queue — handles graphics + compute + copy work. A
        /// production-grade backend would have separate Compute and Copy
        /// queues to overlap workloads, but session 2 keeps everything on
        /// one queue for simplicity. Splitting comes in a later optimisation
        /// pass once we have real rendering working.
        /// </summary>
        public ID3D12CommandQueue DirectQueue => directQueue;

        // Descriptor allocators — see D3D12DescriptorAllocator for rationale
        // around heap sizing + shader-visibility. Lifetime is the device's;
        // disposal cascades from PlatformDispose.
        public D3D12DescriptorAllocator RtvAllocator => rtvAllocator;
        public D3D12DescriptorAllocator DsvAllocator => dsvAllocator;
        public D3D12DescriptorAllocator CbvSrvUavAllocator => cbvSrvUavAllocator;
        public D3D12DescriptorAllocator SamplerAllocator => samplerAllocator;

        private readonly ID3D12Device device;
        private readonly IDXGIAdapter dxgiAdapter;
        private readonly ID3D12CommandQueue directQueue;
        private readonly D3D12DescriptorAllocator rtvAllocator;
        private readonly D3D12DescriptorAllocator dsvAllocator;
        private readonly D3D12DescriptorAllocator cbvSrvUavAllocator;
        private readonly D3D12DescriptorAllocator samplerAllocator;
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

            // Direct command queue — accepts graphics, compute, and copy work.
            // Priority Normal; no GPU node mask (single-adapter). See the
            // DirectQueue XML doc for the rationale around using one queue.
            directQueue = device.CreateCommandQueue(new CommandQueueDescription(
                CommandListType.Direct,
                CommandQueuePriority.Normal,
                CommandQueueFlags.None,
                nodeMask: 0));

            // Descriptor heap allocators. Capacity numbers are generous —
            // typical Torii sessions allocate hundreds of CBV/SRV/UAV
            // descriptors per frame (one per ResourceSet bind site), not
            // millions. RTV/DSV heaps are smaller because each framebuffer
            // only consumes a handful of entries. Sampler heap is capped
            // at 2048 by the D3D12 spec.
            //
            // The CBV/SRV/UAV and Sampler heaps are shader-visible so the
            // GPU can dereference them via descriptor tables; RTV/DSV are
            // CPU-only because OMSetRenderTargets pokes descriptors
            // directly without going through a shader-visible binding.
            rtvAllocator       = new D3D12DescriptorAllocator(device, DescriptorHeapType.RenderTargetView,  capacity: 256,    shaderVisible: false);
            dsvAllocator       = new D3D12DescriptorAllocator(device, DescriptorHeapType.DepthStencilView,  capacity: 64,     shaderVisible: false);
            cbvSrvUavAllocator = new D3D12DescriptorAllocator(device, DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,         capacity: 65536,  shaderVisible: true);
            samplerAllocator   = new D3D12DescriptorAllocator(device, DescriptorHeapType.Sampler,           capacity: 2048,   shaderVisible: true);

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
        {
            return Util.AssertSubtype<Fence, D3D12Fence>(fence).Wait(nanosecondTimeout);
        }

        public override bool WaitForFences(Fence[] fences, bool waitAll, ulong nanosecondTimeout)
        {
            // Simple serial implementation — wait each in order. For waitAll
            // this is exactly correct: all must signal regardless of order,
            // and the total time is bounded by the slowest.
            //
            // For waitAll=false (wait-any) this is suboptimal — we'd want
            // a single multi-handle WaitForMultipleObjects. Leaving that
            // optimisation for session 6; rendering pipelines mostly use
            // waitAll for frame-pacing so the common case is correct.
            //
            // Time budget is split crudely: each fence gets the full
            // remaining timeout. With unsignaled fences in waitAll mode
            // the practical effect is the same as native MultipleObjects
            // because they all have to signal eventually.
            if (waitAll)
            {
                foreach (var f in fences)
                {
                    if (!Util.AssertSubtype<Fence, D3D12Fence>(f).Wait(nanosecondTimeout))
                        return false;
                }
                return true;
            }

            // waitAny: poll each in turn with zero timeout until one signals
            // or the budget is exhausted. Coarse-grained but functional.
            long start = Environment.TickCount64;
            int budgetMs = nanosecondTimeout == ulong.MaxValue
                ? int.MaxValue
                : (int)Math.Min(int.MaxValue, nanosecondTimeout / 1_000_000UL);

            while (true)
            {
                foreach (var f in fences)
                {
                    if (Util.AssertSubtype<Fence, D3D12Fence>(f).Signaled)
                        return true;
                }

                if (budgetMs == 0) return false;
                int elapsed = (int)(Environment.TickCount64 - start);
                if (elapsed >= budgetMs) return false;

                // Yield briefly to avoid pegging a core spinning on
                // CompletedValue. 1ms granularity is fine for fence-wait.
                System.Threading.Thread.Sleep(1);
            }
        }

        public override void ResetFence(Fence fence)
        {
            Util.AssertSubtype<Fence, D3D12Fence>(fence).Reset();
        }

        protected override MappedResource MapCore(IMappableResource resource, MapMode mode, uint subresource)
            => throw new NotImplementedException("D3D12: resource mapping pending.");

        protected override void UnmapCore(IMappableResource resource, uint subresource)
            => throw new NotImplementedException("D3D12: resource mapping pending.");

        protected override void PlatformDispose()
        {
            // Order matters: drain the queue first so the GPU isn't mid-flight
            // when we tear down the device, then release in reverse-creation
            // order. Adapter LAST so DXGI can still observe the device handle
            // while it's being torn down.
            try
            {
                WaitForIdleCore();
            }
            catch
            {
                // Best-effort — if WaitForIdle throws during dispose we still
                // want to release everything below.
            }

            // Descriptor allocators must be released before the device
            // because they hold ID3D12DescriptorHeap children of it.
            samplerAllocator?.Dispose();
            cbvSrvUavAllocator?.Dispose();
            dsvAllocator?.Dispose();
            rtvAllocator?.Dispose();

            directQueue?.Dispose();
            dxgiAdapter?.Dispose();
            device?.Dispose();
        }

        private protected override void SubmitCommandsCore(CommandList commandList, Fence fence)
        {
            var d3d12List = Util.AssertSubtype<CommandList, D3D12CommandList>(commandList);

            // Submit the closed command list to the GPU. ExecuteCommandLists
            // takes an array because you can batch — for now we always submit
            // one at a time; batching is a future micro-optimisation.
            directQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)d3d12List.NativeList });

            // If the caller passed a fence, signal it after the GPU completes
            // the work above. D3D12 enqueues the Signal on the queue so it
            // happens IN ORDER with the executed lists — no race.
            if (fence != null)
            {
                var d3d12Fence = Util.AssertSubtype<Fence, D3D12Fence>(fence);
                ulong signalValue = d3d12Fence.AllocateNextSignalValue();
                directQueue.Signal(d3d12Fence.Fence, signalValue).CheckError();
            }
        }

        private protected override void SwapBuffersCore(Swapchain swapchain)
        {
            Util.AssertSubtype<Swapchain, D3D12Swapchain>(swapchain).Present();
        }

        private protected override void WaitForIdleCore()
        {
            // Idiomatic D3D12 wait-for-idle: signal a transient fence on the
            // queue, then wait for it to complete on the CPU. Equivalent to
            // D3D11's Flush() + GetData(QUERY_EVENT) loop.
            using var transientFence = device.CreateFence(0, FenceFlags.None);
            const ulong target = 1;

            directQueue.Signal(transientFence, target).CheckError();

            if (transientFence.CompletedValue < target)
            {
                using var evt = new System.Threading.ManualResetEvent(false);
                transientFence.SetEventOnCompletion(target, evt.SafeWaitHandle.DangerousGetHandle()).CheckError();
                evt.WaitOne();
            }
        }

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
