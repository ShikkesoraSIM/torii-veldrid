// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 swapchain — DXGI-backed, double-buffered, Win32-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Owns two back-buffers (BackBufferCount = 2 — minimum DXGI accepts
    /// for the FlipDiscard model used by D3D12), wraps each as a
    /// <see cref="D3D12Texture"/>, and exposes the currently-active
    /// back-buffer's <see cref="D3D12Framebuffer"/> via the inherited
    /// <see cref="Framebuffer"/> property.
    /// </para>
    /// <para>
    /// Present flow (called from <c>D3D12GraphicsDevice.SwapBuffersCore</c>):
    /// </para>
    /// <list type="number">
    /// <item>Open a tiny transient command list owned by the swapchain.</item>
    /// <item>Transition the current back-buffer from RENDER_TARGET → PRESENT
    /// (DXGI requires PRESENT state at Present time; the render code left
    /// it in RENDER_TARGET).</item>
    /// <item>Close + execute that list on the device queue.</item>
    /// <item>Call <c>swapChain.Present(syncInterval, flags)</c>.</item>
    /// <item>Update the cached back-buffer index from
    /// <c>swapChain3.GetCurrentBackBufferIndex()</c> — that's now the
    /// buffer we'll render into next frame.</item>
    /// </list>
    /// <para>
    /// What's NOT here (S5 scope cut for shipability):
    /// </para>
    /// <list type="bullet">
    /// <item>Depth attachment. <see cref="SwapchainDescription.DepthFormat"/>
    /// is silently ignored; the swapchain framebuffers are colour-only.
    /// osu-framework typically creates its own depth textures and pipes
    /// them through render targets it manages itself, so this is fine
    /// for the first cut.</item>
    /// <item>Non-Win32 sources (UWP, SDL2, Wayland). Throws on those.
    /// Torii is Windows-first on the D3D12 path anyway — Linux Nova
    /// users go via Vulkan / Deferred_Vulkan.</item>
    /// <item>Allow-tearing flag for variable-refresh-rate displays. Adds
    /// another DXGI factory probe; deferred until users report jitter.</item>
    /// </list>
    /// </remarks>
    internal sealed class D3D12Swapchain : Swapchain
    {
        public const int BACK_BUFFER_COUNT = 2;

        public override Framebuffer Framebuffer => framebuffers[currentBackBufferIndex];

        public override bool IsDisposed => disposed;
        public override string Name { get; set; }
        public override bool SyncToVerticalBlank { get; set; }

        public IDXGISwapChain3 NativeSwapChain => swapChain3;

        private readonly D3D12GraphicsDevice gd;
        private readonly Format colorFormat;
        private IDXGISwapChain3 swapChain3;
        private D3D12Texture[] backBuffers = null!;
        private D3D12Framebuffer[] framebuffers = null!;
        private int currentBackBufferIndex;

        private uint width;
        private uint height;

        // Transient command list for the per-frame RENDER_TARGET → PRESENT
        // barrier. Reset each frame; the swapchain owns it exclusively so
        // there's no contention with user CommandLists.
        private readonly ID3D12CommandAllocator presentAllocator;
        private readonly ID3D12GraphicsCommandList presentCommandList;

        private bool disposed;

        public D3D12Swapchain(D3D12GraphicsDevice gd, ref SwapchainDescription description)
        {
            this.gd = gd;
            Name = string.Empty;
            SyncToVerticalBlank = description.SyncToVerticalBlank;
            width = description.Width;
            height = description.Height;

            colorFormat = description.ColorSrgb
                ? Format.B8G8R8A8_UNorm_SRgb
                : Format.B8G8R8A8_UNorm;

            if (description.Source is not Win32SwapchainSource win32Source)
                throw new VeldridException(
                    "D3D12 swapchain currently only supports Win32 sources. "
                    + "Non-Windows Torii Nova users should use the Vulkan / Deferred_Vulkan renderer instead.");

            var scDesc = new SwapChainDescription1
            {
                Width = (int)width,
                Height = (int)height,
                Format = colorFormat,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = BACK_BUFFER_COUNT,
                Scaling = Scaling.Stretch,
                // FlipDiscard is the only valid swap-effect for D3D12 +
                // flip-model presentation. Sequential / Discard (the older
                // BitBlt models) are D3D11-only paths.
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = SwapChainFlags.None,
            };

            // Fresh DXGI factory — we don't keep one alive on the device
            // (only used once at startup for adapter enumeration). Cheap
            // to recreate at swapchain creation time, no need to cache.
            DXGI.CreateDXGIFactory1(out IDXGIFactory4? factory).CheckError();
            using (factory!)
            {
                // D3D12 swapchains are queue-bound, not device-bound — this
                // is the major API shape difference vs D3D11 here. The
                // queue dictates which command queue's submissions will be
                // synchronised against Present.
                using IDXGISwapChain1 swapChain1 = factory.CreateSwapChainForHwnd(
                    gd.DirectQueue,
                    win32Source.Hwnd,
                    scDesc);

                // Disable the default Alt+Enter fullscreen toggle —
                // osu-framework manages its own fullscreen state via
                // SetFullscreenState calls when the user changes the
                // setting; the auto-toggle would race with that.
                factory.MakeWindowAssociation(win32Source.Hwnd, WindowAssociationFlags.IgnoreAltEnter);

                // SwapChain1 → SwapChain3 for GetCurrentBackBufferIndex
                // (added in DXGI 1.4 alongside the flip-discard model
                // we already opted into above).
                swapChain3 = swapChain1.QueryInterface<IDXGISwapChain3>();
            }

            currentBackBufferIndex = swapChain3.CurrentBackBufferIndex;

            buildBackBufferResources();

            // Present command list — Direct queue type so it can record
            // the barrier and be submitted on the same queue as user work.
            presentAllocator = gd.Device.CreateCommandAllocator(CommandListType.Direct);
            presentCommandList = gd.Device.CreateCommandList<ID3D12GraphicsCommandList>(
                nodeMask: 0,
                CommandListType.Direct,
                presentAllocator,
                initialState: null);
            presentCommandList.Close();
        }

        /// <summary>
        /// Build (or rebuild after resize) the back-buffer D3D12Texture wraps
        /// and their matching D3D12Framebuffer objects. Called from the
        /// constructor and from <see cref="Resize"/>.
        /// </summary>
        private void buildBackBufferResources()
        {
            backBuffers = new D3D12Texture[BACK_BUFFER_COUNT];
            framebuffers = new D3D12Framebuffer[BACK_BUFFER_COUNT];

            // PixelFormat selection mirrors the DXGI format we created the
            // swapchain with. osu-framework's renderer reads
            // Framebuffer.OutputDescription.ColorAttachments[0].Format so
            // this needs to be a valid Veldrid PixelFormat value.
            PixelFormat colorPixelFormat = colorFormat == Format.B8G8R8A8_UNorm_SRgb
                ? PixelFormat.B8G8R8A8UNormSRgb
                : PixelFormat.B8G8R8A8UNorm;

            for (int i = 0; i < BACK_BUFFER_COUNT; i++)
            {
                // GetBuffer<T>(i) returns the i-th back buffer as
                // an ID3D12Resource. DXGI owns these; we wrap as Veldrid
                // textures with ownsResource=false so DisposeCore won't
                // double-free.
                var backBufferResource = swapChain3.GetBuffer<ID3D12Resource>(i);

                // Back-buffers from DXGI are in PRESENT state on first
                // acquisition. The first SetFramebuffer in user code will
                // transition them to RENDER_TARGET.
                backBuffers[i] = new D3D12Texture(
                    backBufferResource,
                    width,
                    height,
                    colorPixelFormat,
                    TextureUsage.RenderTarget,
                    ResourceStates.Present);

                var fbDesc = new FramebufferDescription
                {
                    DepthTarget = null,
                    ColorTargets = new[] { new FramebufferAttachmentDescription(backBuffers[i], 0, 0) },
                };
                framebuffers[i] = new D3D12Framebuffer(gd, ref fbDesc);
            }
        }

        public override void Resize(uint width, uint height)
        {
            if (width == this.width && height == this.height) return;

            // Drain GPU work touching the old back buffers BEFORE releasing
            // them — DXGI ResizeBuffers refuses while frames are in flight.
            // Cheapest correct option: full queue idle wait. A real-game
            // resize is rare (user dragged the window edge), perf doesn't
            // matter; the simpler code wins.
            gd.WaitForIdle();

            // Release the framebuffers + wrapped textures so the back
            // buffers' ref-count drops to zero, which is the DXGI
            // precondition for ResizeBuffers.
            foreach (var fb in framebuffers) fb.Dispose();
            foreach (var bb in backBuffers) bb.Dispose();

            this.width = width;
            this.height = height;

            swapChain3.ResizeBuffers(
                BACK_BUFFER_COUNT,
                (int)width,
                (int)height,
                colorFormat,
                SwapChainFlags.None).CheckError();

            currentBackBufferIndex = swapChain3.CurrentBackBufferIndex;
            buildBackBufferResources();
        }

        /// <summary>
        /// Internal: present the current back buffer + advance to the next
        /// one. Called by <c>D3D12GraphicsDevice.SwapBuffersCore</c>.
        /// </summary>
        public void Present()
        {
            // Step 1: transition the back buffer we just rendered to from
            // RENDER_TARGET back to PRESENT. DXGI requires PRESENT state
            // at Present-call time.
            //
            // CRITICAL FIX: WaitForIdle / allocator reset must happen
            // UNCONDITIONALLY, NOT only when a state transition is needed.
            // The original code had this in an "if (CurrentState != Present)"
            // gate. That gate fires correctly when the renderer DID draw
            // into the back buffer (RenderTarget → Present), but the
            // first frame of a swap (back-buffer slot freshly acquired
            // from DXGI) starts in PRESENT state — if the renderer wrote
            // to it and left state as Present somehow, or wrote to a
            // DIFFERENT slot that previous frames advanced past, the
            // WaitForIdle never runs and we race the GPU on Present.
            // Symptom: completely black on-screen output despite all
            // draw commands being issued correctly (audio + input fine,
            // logs report MainMenu reached, but visual is dead).
            // The fix is one tiny code reorder: always drain.
            var currentBackBuffer = backBuffers[currentBackBufferIndex];

            gd.WaitForIdle();
            presentAllocator.Reset();
            presentCommandList.Reset(presentAllocator, initialState: null);

            if (currentBackBuffer.CurrentState != ResourceStates.Present)
            {
                presentCommandList.ResourceBarrierTransition(
                    currentBackBuffer.NativeResource,
                    currentBackBuffer.CurrentState,
                    ResourceStates.Present);
                currentBackBuffer.CurrentState = ResourceStates.Present;
            }

            presentCommandList.Close();
            gd.DirectQueue.ExecuteCommandLists(new[] { (ID3D12CommandList)presentCommandList });

            // Step 2: DXGI Present.
            int syncInterval = SyncToVerticalBlank ? 1 : 0;
            var presentResult = swapChain3.Present(syncInterval, PresentFlags.None);
            if (presentResult.Failure)
            {
                // DXGI_ERROR_DEVICE_REMOVED + friends — wrap with DRED
                // breadcrumb / page-fault context if available so we know
                // WHICH draw caused the hang.
                string dred = D3D12DredDump.Capture(gd);
                throw new VeldridException(
                    $"D3D12 Present failed: HRESULT=0x{presentResult.Code:X8}. "
                    + $"DeviceRemovedReason=0x{gd.Device.DeviceRemovedReason.Code:X8}.\n"
                    + $"DRED:\n{dred}");
            }

            // Step 3: advance to the buffer the next frame will render to.
            currentBackBufferIndex = swapChain3.CurrentBackBufferIndex;
        }

        public override void Dispose()
        {
            if (disposed) return;

            presentCommandList.Dispose();
            presentAllocator.Dispose();

            foreach (var fb in framebuffers) fb.Dispose();
            foreach (var bb in backBuffers) bb.Dispose();
            swapChain3.Dispose();

            disposed = true;
        }
    }
}
