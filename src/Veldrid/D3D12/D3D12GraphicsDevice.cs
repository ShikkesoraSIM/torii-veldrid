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
        // construction time, in which case we materialise it eagerly so the
        // first frame can present without a separate factory call. Mirrors
        // D3D11GraphicsDevice's MainSwapchain behaviour.
        public override Swapchain MainSwapchain => mainSwapchain!;

        private readonly D3D12Swapchain? mainSwapchain;

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

            // Enable DRED — when DXGI_ERROR_DEVICE_REMOVED hits on
            // Present (GPU hung mid-frame), DRED gives us a "breadcrumb"
            // trail of the last command-list operations the GPU executed
            // plus the page-fault VA if the hang was an OOB access. The
            // generic DEVICE_REMOVED HRESULT is otherwise opaque.
            //
            // Must be enabled BEFORE D3D12CreateDevice for the device to
            // honour the breadcrumb storage opt-in.
            if (VorticeD3D12.D3D12GetDebugInterface<ID3D12DeviceRemovedExtendedDataSettings>(out var dredSettings).Success)
            {
                dredSettings!.SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
                dredSettings.SetPageFaultEnablement(DredEnablement.ForcedOn);
                dredSettings.Dispose();
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

#if DEBUG
            // Wire up debug layer diagnostics. Two parallel mechanisms so
            // we capture validator output regardless of which one Vortice's
            // binding happens to honour:
            //
            // 1. ID3D12InfoQueue (the storage queue) — drainable on demand
            //    from the PSO-failure catch in D3D12Pipeline. Tries to
            //    register an allow-all filter so messages are retained.
            //
            // 2. ID3D12InfoQueue1.RegisterMessageCallback — the modern
            //    push-mode API that pumps every message to a delegate as
            //    soon as the debug layer produces it. Captured to an
            //    in-process buffer + emitted to Console.Error so DebugView
            //    OR a stderr-redirecting launcher both pick them up
            //    without relying on OutputDebugString (which empirically
            //    does NOT get the messages on this build of Vortice).
            try
            {
                infoQueue = device.QueryInterfaceOrNull<ID3D12InfoQueue>();
                if (infoQueue != null)
                {
                    infoQueue.SetBreakOnSeverity(MessageSeverity.Error, false);
                    infoQueue.SetBreakOnSeverity(MessageSeverity.Corruption, false);
                    infoQueue.SetBreakOnSeverity(MessageSeverity.Warning, false);
                    infoQueue.PushEmptyStorageFilter();
                }
            }
            catch { }

            try
            {
                var infoQueue1 = device.QueryInterfaceOrNull<ID3D12InfoQueue1>();
                if (infoQueue1 != null)
                {
                    // Keep the delegate alive — RegisterMessageCallback's
                    // unmanaged side holds a function pointer that the GC
                    // would otherwise reclaim mid-callback.
                    messageCallback = onD3D12DebugMessage;
                    infoQueue1.RegisterMessageCallback(messageCallback, MessageCallbackFlags.None);
                    debugMessages = new System.Collections.Generic.List<string>();
                }
            }
            catch { }
#endif

            // Materialise the main swapchain eagerly when a description
            // was passed (the GraphicsDevice.CreateD3D12(opts, swapchainDesc)
            // overload, which is what osu-framework's VeldridDevice uses).
            // Without this, MainSwapchain returns null and the deferred
            // renderer NREs on the first Present — that's the failure mode
            // surfacing as "renderer failed to initialise" in the toast.
            if (swapchainDesc.HasValue)
            {
                var desc = swapchainDesc.Value;
                mainSwapchain = new D3D12Swapchain(this, ref desc);
            }

            PostDeviceCreated();
        }

        /// <summary>
        /// D3D12 InfoQueue interface (debug layer's stored-message buffer).
        /// Null when the debug layer is not active (release build or
        /// Graphics Tools optional feature not installed). Used by the
        /// PSO failure path in <see cref="D3D12Pipeline"/> to surface
        /// validator messages alongside opaque HRESULT codes.
        /// </summary>
        internal ID3D12InfoQueue? InfoQueue => infoQueue;
        private readonly ID3D12InfoQueue? infoQueue;

        // Live debug-message buffer populated by onD3D12DebugMessage via
        // ID3D12InfoQueue1.RegisterMessageCallback. The PSO failure path
        // reads this in addition to the polled InfoQueue so we get
        // diagnostic coverage even when Vortice's InfoQueue plumbing is
        // not actually retaining messages.
        internal System.Collections.Generic.List<string>? DebugMessages => debugMessages;
        private readonly System.Collections.Generic.List<string>? debugMessages;
#if DEBUG
        private readonly MessageCallback? messageCallback;

        private void onD3D12DebugMessage(MessageCategory category, MessageSeverity severity, MessageId id, string description)
        {
            string line = $"[{severity}/{category}/#{(int)id}] {description}";
            // Lock — debug layer may invoke from a driver worker thread.
            lock (this)
            {
                debugMessages?.Add(line);
            }
            // Also push to stderr so external launchers (DebugView, our
            // own launcher's stderr-redirected log file, etc.) can see
            // messages live as they happen instead of only on PSO failure.
            try { Console.Error.WriteLine("D3D12: " + line); } catch { /* stderr might be closed */ }
        }
#endif

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
        {
            // Report no MSAA support for now — osu-framework / DeferredRenderer
            // will skip multisampled framebuffer paths and fall back to
            // single-sample rendering. A proper impl probes via
            // device.CheckFeatureSupport(MultisampleQualityLevels) for each
            // requested sample count, but that requires Vortice's pinned-
            // pointer marshalling for the feature-data struct (same blocker
            // as the FeatureLevels probe in the ctor). Punted to a follow-up.
            return TextureSampleCount.Count1;
        }

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
        {
            // Only UPLOAD / READBACK heap resources can be CPU-mapped directly.
            // DEFAULT-heap mapping would require a staging round-trip; Veldrid
            // doesn't ask for that path (callers create Staging-flagged
            // resources when they need CPU access).
            if (resource is D3D12Buffer buffer)
            {
                if (buffer.HeapType != HeapType.Upload && buffer.HeapType != HeapType.Readback)
                    throw new VeldridException("D3D12: cannot map a DEFAULT-heap buffer. Mark it Staging or Dynamic at creation time.");

                // Range(0, 0) means "we don't intend to read" — a driver
                // hint that skips the CPU-cache-coherence flush. For
                // ReadWrite mode we DO read, so pass null = "full range".
                Vortice.Direct3D12.Range? readRange = mode == MapMode.Write
                    ? new Vortice.Direct3D12.Range { Begin = 0, End = 0 }
                    : (Vortice.Direct3D12.Range?)null;

                IntPtr mapped;
                unsafe
                {
                    void* dataPtr;
                    buffer.NativeResource.Map(0, readRange, &dataPtr).CheckError();
                    mapped = (IntPtr)dataPtr;
                }

                return new MappedResource(
                    resource,
                    mode,
                    mapped,
                    sizeInBytes: buffer.SizeInBytes,
                    subresource: 0,
                    rowPitch: 0,
                    depthPitch: 0);
            }

            if (resource is D3D12Texture)
            {
                // Texture mapping is more involved (256-byte row alignment
                // per D3D12 spec — needs GetCopyableFootprints math). Most
                // Veldrid texture workflows go through UpdateTexture instead;
                // Map is mainly used for Staging textures in readback flows.
                // Defer full implementation until a real consumer needs it.
                throw new NotImplementedException("D3D12: texture Map pending — use UpdateTexture instead.");
            }

            throw new VeldridException($"D3D12: unmappable resource type {resource.GetType().Name}.");
        }

        protected override void UnmapCore(IMappableResource resource, uint subresource)
        {
            if (resource is D3D12Buffer buffer)
            {
                buffer.NativeResource.Unmap(0);
                return;
            }
            // No-op for unsupported types — Map would have thrown, so this
            // should be unreachable, but defensive.
        }

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
        {
            // Frame-pacing hook. osu-framework calls this every frame as a
            // pacing-only barrier — it's not a correctness requirement, it
            // exists for CPU-side latency tuning (FrameSync.VSync targets,
            // NVIDIA Reflex hooks, etc).
            //
            // D3D12 backend choice: no-op. The swapchain Present already
            // applies vsync (FlipDiscard with SyncInterval=1 when the user
            // selects FrameSync.VSync), so frame-time output is correct.
            // We just don't add an additional CPU-side wait barrier.
            // Implementing a real wait-for-frame-ready would require a
            // latency-waitable swapchain (DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT)
            // — pending future Nova work on Reflex-equivalent latency
            // tuning for D3D12.
        }

        private protected override void UpdateTextureCore(
            Texture texture,
            IntPtr source,
            uint sizeInBytes,
            uint x, uint y, uint z,
            uint width, uint height, uint depth,
            uint mipLevel, uint arrayLayer)
        {
            var d12Tex = Util.AssertSubtype<Texture, D3D12Texture>(texture);

            // Compute the destination subresource's CopyableFootprint —
            // gives us the row pitch (256-byte aligned per D3D12 spec),
            // total required upload buffer size, and the sub-resource
            // layout the CopyTextureRegion will consume.
            int subresource = (int)(mipLevel + arrayLayer * d12Tex.MipLevels);
            var desc = d12Tex.NativeResource.Description;

            // Vortice's friendly wrapper returns the footprint array +
            // companion arrays. We only need subresource 0 of the slice
            // we're updating, so request 1 entry.
            var footprints = new PlacedSubresourceFootPrint[1];
            var numRowsArr = new int[1];
            var rowSizesArr = new ulong[1];
            device.GetCopyableFootprints(
                desc,
                firstSubresource: subresource,
                numSubresources: 1,
                baseOffset: 0,
                footprints,
                numRowsArr,
                rowSizesArr,
                out ulong totalBytes);

            var footprint = footprints[0];
            int numRows = numRowsArr[0];
            ulong rowSizeInBytes = rowSizesArr[0];

            // Allocate an upload buffer big enough to hold the padded
            // sub-resource. Per-call allocation — a pool would amortise
            // but UpdateTexture is rare (texture init, not per-frame).
            var uploadDesc = ResourceDescription.Buffer(totalBytes);
            var uploadHeap = new HeapProperties(HeapType.Upload);
            var uploadResource = device.CreateCommittedResource(
                uploadHeap, HeapFlags.None,
                uploadDesc, ResourceStates.GenericRead, null);

            // Footprint shenanigans: GetCopyableFootprints returns the
            // layout for the FULL subresource (e.g. 1024×1024 RowPitch
            // 4096), but the caller's `source` only contains data for
            // the SUB-RECT we want uploaded (width × height × depth).
            // CopyTextureRegion(null srcBox) reads the entire source
            // region per the footprint — so if we use the full-subresource
            // footprint, it pulls (NumRows × RowPitch) bytes of garbage
            // out of the upload buffer past our actual data and writes
            // them onto the destination texture, corrupting it.
            //
            // Fix: override the footprint's Width/Height/Depth to match
            // the sub-rect. CopyTextureRegion then only reads the
            // sub-rect-sized chunk of the upload buffer. RowPitch must
            // remain 256-byte aligned per D3D12 spec, so re-compute it
            // for the sub-rect width.
            int subRectRowPitch = (int)D3D12Util.AlignUp(
                (ulong)(sizeInBytes / (height * depth)),
                256);
            footprint.Footprint.Width = (int)width;
            footprint.Footprint.Height = (int)height;
            footprint.Footprint.Depth = (int)depth;
            footprint.Footprint.RowPitch = subRectRowPitch;

            // Recreate the upload buffer at the now-correct sub-rect
            // size — totalBytes from the footprint API was sized for
            // the full subresource (way too big, mostly wasteful).
            ulong subRectBytes = (ulong)subRectRowPitch * height * depth;
            uploadResource.Dispose();
            uploadResource = device.CreateCommittedResource(
                uploadHeap, HeapFlags.None,
                ResourceDescription.Buffer(subRectBytes),
                ResourceStates.GenericRead, null);

            // Copy the caller's tightly-packed source data into the
            // upload buffer, padding each row to subRectRowPitch.
            unsafe
            {
                void* mappedPtr;
                uploadResource.Map(0, null, &mappedPtr).CheckError();
                byte* dst = (byte*)mappedPtr;
                byte* src = (byte*)source;

                int srcStride = (int)(sizeInBytes / (height * depth));

                for (int slice = 0; slice < depth; slice++)
                {
                    for (int row = 0; row < height; row++)
                    {
                        System.Buffer.MemoryCopy(
                            src + (slice * height + row) * srcStride,
                            dst + (slice * subRectRowPitch * height) + (row * subRectRowPitch),
                            subRectRowPitch, srcStride);
                    }
                }
                uploadResource.Unmap(0);
            }

            // Re-anchor the footprint at offset 0 in the new upload buffer.
            footprint.Offset = 0;

            // Open a transient command list, transition dest texture into
            // CopyDest, CopyTextureRegion, transition back, execute, wait.
            // The synchronous wait matches the UpdateTextureCore contract
            // (callers expect the upload to be GPU-visible on return).
            using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
            using var cmdList = device.CreateCommandList<ID3D12GraphicsCommandList>(
                0, CommandListType.Direct, allocator, null);

            var originalState = d12Tex.CurrentState;
            D3D12Util.TransitionTexture(cmdList, d12Tex, ResourceStates.CopyDest);

            var dstLoc = new TextureCopyLocation(d12Tex.NativeResource, subresource);
            var srcLoc = new TextureCopyLocation(uploadResource, footprint);
            cmdList.CopyTextureRegion(dstLoc, (int)x, (int)y, (int)z, srcLoc, null);

            D3D12Util.TransitionTexture(cmdList, d12Tex, originalState);

            cmdList.Close();
            directQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)cmdList });
            WaitForIdleCore();

            uploadResource.Dispose();
        }

        private protected override void UpdateBufferCore(DeviceBuffer buffer, uint bufferOffsetInBytes, IntPtr source, uint sizeInBytes)
        {
            var d12Buf = Util.AssertSubtype<DeviceBuffer, D3D12Buffer>(buffer);

            // UPLOAD-heap buffers can be written directly via Map. No
            // staging round-trip needed. This is the fast path for the
            // Dynamic / Staging buffers.
            if (d12Buf.HeapType == HeapType.Upload)
            {
                unsafe
                {
                    void* mappedPtr;
                    d12Buf.NativeResource.Map(0, null, &mappedPtr).CheckError();
                    System.Buffer.MemoryCopy(
                        (void*)source,
                        (byte*)mappedPtr + bufferOffsetInBytes,
                        d12Buf.SizeInBytes - bufferOffsetInBytes,
                        sizeInBytes);
                    d12Buf.NativeResource.Unmap(0);
                }
                return;
            }

            // DEFAULT-heap buffer: staging round-trip. Allocate a tiny
            // transient upload buffer, memcpy into it, CopyBufferRegion
            // into the dest, wait. Slow — per-frame uniform updates should
            // use a Dynamic buffer to avoid this path.
            var uploadDesc = ResourceDescription.Buffer(sizeInBytes);
            var uploadHeap = new HeapProperties(HeapType.Upload);
            var uploadResource = device.CreateCommittedResource(
                uploadHeap, HeapFlags.None,
                uploadDesc, ResourceStates.GenericRead, null);

            unsafe
            {
                void* mappedPtr;
                uploadResource.Map(0, null, &mappedPtr).CheckError();
                System.Buffer.MemoryCopy(
                    (void*)source, mappedPtr,
                    sizeInBytes, sizeInBytes);
                uploadResource.Unmap(0);
            }

            using var allocator = device.CreateCommandAllocator(CommandListType.Direct);
            using var cmdList = device.CreateCommandList<ID3D12GraphicsCommandList>(
                0, CommandListType.Direct, allocator, null);

            var originalState = d12Buf.CurrentState;
            D3D12Util.TransitionBuffer(cmdList, d12Buf, ResourceStates.CopyDest);

            cmdList.CopyBufferRegion(
                d12Buf.NativeResource, bufferOffsetInBytes,
                uploadResource, 0,
                sizeInBytes);

            D3D12Util.TransitionBuffer(cmdList, d12Buf, originalState);

            cmdList.Close();
            directQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)cmdList });
            WaitForIdleCore();

            uploadResource.Dispose();
        }

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
