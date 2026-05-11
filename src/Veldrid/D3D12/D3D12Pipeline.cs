// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;
using VorticeBlend = Vortice.Direct3D12.Blend;
using VorticeBlendOperation = Vortice.Direct3D12.BlendOperation;
using VorticeStencilOperation = Vortice.Direct3D12.StencilOperation;
using VorticeComparisonFunction = Vortice.Direct3D12.ComparisonFunction;
using VorticeCullMode = Vortice.Direct3D12.CullMode;
using VorticeFillMode = Vortice.Direct3D12.FillMode;
using VorticePrimitiveTopologyType = Vortice.Direct3D12.PrimitiveTopologyType;
using VorticeD3D12 = Vortice.Direct3D12.D3D12;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 graphics pipeline — wraps an
    /// <see cref="ID3D12PipelineState"/> (immutable PSO) and an
    /// <see cref="ID3D12RootSignature"/> built from the Veldrid
    /// resource-layout array.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 PSOs are immutable bundles of (shaders + blend + rasterizer +
    /// depth/stencil + render-target formats + input layout + primitive
    /// topology + root signature). Creating one is expensive (driver compile
    /// of the shader → PSO + state validation), so osu-framework caches
    /// them — every distinct combination of those fields gets a different
    /// Pipeline object. We just translate Veldrid's
    /// <see cref="GraphicsPipelineDescription"/> to D3D12's equivalent
    /// struct once at construction time.
    /// </para>
    /// <para>
    /// Root signature layout: for each <see cref="ResourceLayout"/> in
    /// <see cref="GraphicsPipelineDescription.ResourceLayouts"/>, we add
    /// up to two descriptor-table root parameters — one for CBV/SRV/UAV
    /// descriptors and one for Samplers (omitted if empty). The mapping
    /// from layout index → root parameter index is stored in
    /// <see cref="CbvSrvUavRootParamPerLayout"/> /
    /// <see cref="SamplerRootParamPerLayout"/> so the CommandList's
    /// SetGraphicsResourceSet can find the right slot in one lookup.
    /// </para>
    /// </remarks>
    internal sealed class D3D12Pipeline : Pipeline
    {
        public override bool IsComputePipeline => false;
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        public ID3D12PipelineState NativePipelineState => pso;
        public ID3D12RootSignature NativeRootSignature => rootSignature;

        /// <summary>D3D12 primitive topology for IASetPrimitiveTopology — finer-grained than the PSO's topology type.</summary>
        public Vortice.Direct3D.PrimitiveTopology NativePrimitiveTopology { get; }

        /// <summary>Root parameter index of the CBV/SRV/UAV descriptor table for ResourceLayout #i. -1 if no non-sampler resources.</summary>
        public int[] CbvSrvUavRootParamPerLayout { get; }

        /// <summary>Root parameter index of the Sampler descriptor table for ResourceLayout #i. -1 if no samplers.</summary>
        public int[] SamplerRootParamPerLayout { get; }

        /// <summary>
        /// Stride in bytes per VertexLayoutDescription slot. CommandList.SetVertexBuffer
        /// needs this to populate VertexBufferView.StrideInBytes — D3D12 uses that
        /// value literally to step through the vertex buffer per vertex, it does NOT
        /// derive it from the PSO's InputLayout at draw time. Without correct strides
        /// every vertex reads byte 0 of the buffer, every triangle is degenerate, and
        /// the rasterizer drops the entire draw → black screen.
        /// </summary>
        public int[] VertexStridePerSlot { get; }

        private readonly ID3D12PipelineState pso;
        private readonly ID3D12RootSignature rootSignature;
        private bool disposed;

        public D3D12Pipeline(D3D12GraphicsDevice gd, ref GraphicsPipelineDescription description)
            : base(ref description)
        {
            Name = string.Empty;

            // --- Vertex stride cache --------------------------------
            // Use the declared Stride if non-zero, otherwise sum the
            // element sizes. Some framework codepaths construct
            // VertexLayoutDescription via the no-stride ctor expecting
            // the backend to compute it from elements at PSO build
            // time. Always compute as a safety net even if the field
            // looks populated — cheap, deterministic.
            var vertexLayouts = description.ShaderSet.VertexLayouts;
            VertexStridePerSlot = new int[vertexLayouts?.Length ?? 0];
            if (vertexLayouts != null)
            {
                for (int i = 0; i < vertexLayouts.Length; i++)
                {
                    int declared = (int)vertexLayouts[i].Stride;
                    int computed = 0;
                    foreach (var elem in vertexLayouts[i].Elements)
                        computed += vertexFormatStride(elem.Format);
                    // Prefer the larger of declared / computed — declared
                    // can over-pad (caller's deliberate alignment); computed
                    // is the minimum that fits all elements.
                    VertexStridePerSlot[i] = declared > computed ? declared : computed;
                }
            }

            // --- Root signature --------------------------------------
            int layoutCount = description.ResourceLayouts?.Length ?? 0;
            CbvSrvUavRootParamPerLayout = new int[layoutCount];
            SamplerRootParamPerLayout = new int[layoutCount];

            var rootParams = new List<RootParameter1>();
            // Keep the DescriptorRange1 arrays alive in managed memory until
            // CreateRootSignature returns — Vortice's RootParameter1 holds a
            // span pointing into them, and if they get GC'd mid-call we'd
            // pass a dangling pointer to the driver.
            var rangeArraysToKeepAlive = new List<DescriptorRange1[]>();

            // Running per-resource-type base shader registers across all
            // layouts. SPIRV-Cross's HLSL emitter flattens Vulkan
            // descriptor sets into a single register space (space0),
            // numbering registers SEQUENTIALLY by resource type:
            //
            //   set 0 / binding 0 (UBO) → b0
            //   set 1 / binding 0 (SRV) → t0
            //   set 1 / binding 1 (Sampler) → s0
            //   set 2 / binding 0 (UBO) → b1   (next CBV after set 0's b0)
            //
            // The scaffold's space-per-layout scheme (registerSpace=i)
            // does NOT match what the shader bytecode expects, so PSO
            // creation failed with E_INVALIDARG. Use space=0 universally
            // and walk the base registers per type as we iterate layouts.
            int cbvBase = 0, srvBase = 0, uavBase = 0, samplerBase = 0;

            for (int i = 0; i < layoutCount; i++)
            {
                var layout = Util.AssertSubtype<ResourceLayout, D3D12ResourceLayout>(description.ResourceLayouts![i]);

                if (layout.CbvSrvUavCount > 0)
                {
                    var ranges = buildCbvSrvUavRanges(layout, ref cbvBase, ref srvBase, ref uavBase);
                    rangeArraysToKeepAlive.Add(ranges);

                    CbvSrvUavRootParamPerLayout[i] = rootParams.Count;
                    rootParams.Add(new RootParameter1(
                        new RootDescriptorTable1(ranges),
                        ShaderVisibility.All));
                }
                else
                {
                    CbvSrvUavRootParamPerLayout[i] = -1;
                }

                if (layout.SamplerCount > 0)
                {
                    var samplerRange = new[]
                    {
                        new DescriptorRange1(
                            DescriptorRangeType.Sampler,
                            layout.SamplerCount,
                            baseShaderRegister: samplerBase,
                            registerSpace: 0,
                            offsetInDescriptorsFromTableStart: 0)
                    };
                    samplerBase += layout.SamplerCount;
                    rangeArraysToKeepAlive.Add(samplerRange);

                    SamplerRootParamPerLayout[i] = rootParams.Count;
                    rootParams.Add(new RootParameter1(
                        new RootDescriptorTable1(samplerRange),
                        ShaderVisibility.All));
                }
                else
                {
                    SamplerRootParamPerLayout[i] = -1;
                }
            }

            var rsDesc = new RootSignatureDescription1(
                RootSignatureFlags.AllowInputAssemblerInputLayout,
                rootParams.ToArray());

            // Vortice exposes a combined "create from description" overload
            // on ID3D12Device that does the serialize + native create in
            // one call — much cleaner than the manual two-step path. Pass
            // the description by `in` (it's marked `in RootSignatureDescription1`
            // in Vortice's signature for zero-copy struct binding).
            rootSignature = gd.Device.CreateRootSignature(in rsDesc);

            // --- PSO ----------------------------------------------------
            NativePrimitiveTopology = toNativePrimitiveTopology(description.PrimitiveTopology);

            var psoDesc = new GraphicsPipelineStateDescription
            {
                RootSignature = rootSignature,
                VertexShader = findShaderBytes(description.ShaderSet, ShaderStages.Vertex),
                PixelShader = findShaderBytes(description.ShaderSet, ShaderStages.Fragment),
                GeometryShader = findShaderBytes(description.ShaderSet, ShaderStages.Geometry),
                HullShader = findShaderBytes(description.ShaderSet, ShaderStages.TessellationControl),
                DomainShader = findShaderBytes(description.ShaderSet, ShaderStages.TessellationEvaluation),
                BlendState = toBlendDescription(description.BlendState, description.Outputs),
                SampleMask = uint.MaxValue,
                RasterizerState = toRasterizerDescription(description.RasterizerState),
                // Match DepthStencilState to whether the PSO actually has
                // a depth-stencil attachment. D3D12 validates that if
                // DepthEnable is true, DepthStencilFormat must NOT be
                // Unknown — otherwise E_INVALIDARG. Most osu-framework
                // pipelines run with no depth target on the main back
                // buffer (it's a 2D ImGui-style renderer), so we have to
                // force-disable depth here regardless of what the upstream
                // DepthStencilStateDescription says when no attachment
                // is present.
                DepthStencilState = description.Outputs.DepthAttachment != null
                    ? toDepthStencilDescription(description.DepthStencilState)
                    : new DepthStencilDescription
                    {
                        DepthEnable = false,
                        DepthWriteMask = DepthWriteMask.Zero,
                        DepthFunc = VorticeComparisonFunction.Always,
                        StencilEnable = false,
                    },
                InputLayout = toInputLayoutDescription(description.ShaderSet.VertexLayouts),
                // Explicitly set IBStripCutValue to Disabled. Vortice's
                // struct default in 2.4.2 ends up zero-initialised but the
                // underlying D3D12 enum's "Disabled" is 0xFFFFFFFF (i.e.
                // `D3D12_INDEX_BUFFER_STRIP_CUT_VALUE_DISABLED`); 0 maps to
                // `_0xFFFF` which is only valid for TriangleStrip topology
                // and triggers E_INVALIDARG on TriangleList.
                IndexBufferStripCutValue = IndexBufferStripCutValue.Disabled,
                PrimitiveTopologyType = toPrimitiveTopologyType(description.PrimitiveTopology),
                // Vortice 2.4.2 derives NumRenderTargets from
                // RenderTargetFormats.Length (Math.Min with 8) — there is
                // no separate public NumRenderTargets field. So the array
                // length MUST equal the actual attachment count;
                // returning a fixed 8-element array with 7 Format.Unknown
                // trailers tells D3D12 we want 8 render targets, only the
                // first of which has a real format. The validator catches
                // that as a render-target-format mismatch against the
                // pixel shader's SV_TARGET signature and rejects with
                // E_INVALIDARG.
                RenderTargetFormats = collectRtvFormats(description.Outputs),
                DepthStencilFormat = description.Outputs.DepthAttachment != null
                    ? D3D12Formats.ToDxgiFormat(description.Outputs.DepthAttachment.Value.Format, depthFormat: true)
                    : Format.Unknown,
                SampleDescription = new SampleDescription(
                    sampleCountToInt(description.Outputs.SampleCount),
                    quality: 0),
                NodeMask = 0,
                CachedPSO = default,
                Flags = PipelineStateFlags.None,
            };

            try
            {
                pso = gd.Device.CreateGraphicsPipelineState(psoDesc);
            }
            catch (SharpGen.Runtime.SharpGenException ex)
            {
                // Always dump InfoQueue + PSO summary + bytecode reflection
                // + InputLayout side-by-side so we can spot semantic
                // mismatches without the debug layer's help.
                string iqDump = drainInfoQueue(gd);
                string callbackDump = drainCallbackMessages(gd);
                string summary =
                    $"VS={psoDesc.VertexShader.Length}B PS={psoDesc.PixelShader.Length}B "
                    + $"RTs={psoDesc.RenderTargetFormats?.Length ?? 0} "
                    + $"firstRT={(psoDesc.RenderTargetFormats?.Length > 0 ? psoDesc.RenderTargetFormats[0].ToString() : "n/a")} "
                    + $"DSV={psoDesc.DepthStencilFormat} "
                    + $"InputElements={psoDesc.InputLayout?.Elements?.Length ?? 0} "
                    + $"Topology={psoDesc.PrimitiveTopologyType} "
                    + $"Samples={psoDesc.SampleDescription.Count}";

                string shaderInputDump = reflectVertexShaderInputs(psoDesc.VertexShader);
                string layoutDump = dumpInputLayout(psoDesc.InputLayout);
                string vsBindingsDump = reflectShaderBindings(psoDesc.VertexShader, "VS");
                string psBindingsDump = reflectShaderBindings(psoDesc.PixelShader, "PS");
                string blendDump = dumpBlendState(psoDesc.BlendState, psoDesc.RenderTargetFormats?.Length ?? 0);
                string rasterDump = dumpRasterizerState(psoDesc.RasterizerState);
                string rootSigDump = dumpRootSignatureLayout(description);

                throw new VeldridException(
                    $"D3D12 CreateGraphicsPipelineState failed (HRESULT {ex.HResult:X8}).\n"
                    + $"PSO summary: {summary}\n"
                    + $"VS reflected inputs:\n{shaderInputDump}"
                    + $"Our InputLayout:\n{layoutDump}"
                    + $"VS reflected bindings:\n{vsBindingsDump}"
                    + $"PS reflected bindings:\n{psBindingsDump}"
                    + $"Our root signature (from Veldrid ResourceLayouts):\n{rootSigDump}"
                    + $"Blend state:\n{blendDump}"
                    + $"Rasterizer state:\n{rasterDump}"
                    + $"InfoQueue (polled):\n{iqDump}"
                    + $"DebugMessages (callback):\n{callbackDump}",
                    ex);
            }

            // rangeArraysToKeepAlive can fall off the stack now — the root
            // signature has been created and the driver has its own copy.
            GC.KeepAlive(rangeArraysToKeepAlive);
        }

        private static string reflectVertexShaderInputs(ReadOnlyMemory<byte> vsBytecode)
        {
            // Read the canonical input signature directly from the
            // compiled VS bytecode via D3DReflect. This is the EXACT
            // semantic set that D3D12's PSO validator compares against
            // our InputLayout. If they don't match name-for-name +
            // index-for-index, PSO creation fails with E_INVALIDARG
            // (which is what we keep hitting on Vortice 2.4.2 where
            // the debug layer's actual error message is unreachable).
            if (vsBytecode.IsEmpty)
                return "  (no VS bytecode)\n";

            try
            {
                using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(vsBytecode.Span);
                if (reflection == null)
                    return "  (D3DReflect returned null — bytecode is not valid DXBC)\n";

                int inputCount = reflection.Description.InputParameters;
                if (inputCount == 0)
                    return "  (shader declares no input parameters)\n";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < inputCount; i++)
                {
                    var p = reflection.GetInputParameterDescription(i);
                    // UsageMask: 0x1=X, 0x3=XY, 0x7=XYZ, 0xF=XYZW.
                    // This is the bit that lets us spot width mismatches
                    // (e.g. shader reads .xyzw but InputLayout only
                    // provides .x → PSO E_INVALIDARG).
                    sb.Append($"  [{i}] {p.SemanticName}{p.SemanticIndex} reg=v{p.Register} ")
                      .Append($"usageMask=0x{(int)p.UsageMask:X} ")
                      .Append($"readMask=0x{(int)p.ReadWriteMask:X} ")
                      .Append($"type={p.ComponentType} sysVal={p.SystemValueType}\n");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"  (reflection threw: {ex.GetType().Name}: {ex.Message})\n";
            }
        }

        private static string reflectShaderBindings(ReadOnlyMemory<byte> bytecode, string stageLabel)
        {
            if (bytecode.IsEmpty)
                return $"  ({stageLabel}: no bytecode)\n";

            try
            {
                using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(bytecode.Span);
                if (reflection == null)
                    return $"  ({stageLabel}: reflect returned null)\n";

                int boundCount = reflection.Description.BoundResources;
                if (boundCount == 0)
                    return $"  ({stageLabel}: no bound resources)\n";

                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < boundCount; i++)
                {
                    var b = reflection.GetResourceBindingDescription(i);
                    sb.Append($"  [{i}] {b.Type} '{b.Name}' bindPoint={b.BindPoint} bindCount={b.BindCount}\n");
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"  ({stageLabel} reflect threw: {ex.GetType().Name}: {ex.Message})\n";
            }
        }

        private static string dumpBlendState(BlendDescription b, int rtCount)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"  AlphaToCoverage={b.AlphaToCoverageEnable} IndependentBlend={b.IndependentBlendEnable} (rtCount={rtCount})\n");
            for (int i = 0; i < (rtCount > 0 ? rtCount : 1) && i < 8; i++)
            {
                var rt = b.RenderTarget[i];
                sb.Append($"  RT[{i}]: BlendEnable={rt.BlendEnable} LogicOpEnable={rt.LogicOpEnable} ")
                  .Append($"src={rt.SourceBlend} dst={rt.DestinationBlend} op={rt.BlendOperation} ")
                  .Append($"srcA={rt.SourceBlendAlpha} dstA={rt.DestinationBlendAlpha} opA={rt.BlendOperationAlpha} ")
                  .Append($"writeMask={rt.RenderTargetWriteMask}\n");
            }
            return sb.ToString();
        }

        private static string dumpRasterizerState(RasterizerDescription r)
        {
            return $"  FillMode={r.FillMode} CullMode={r.CullMode} FrontCCW={r.FrontCounterClockwise} "
                + $"DepthBias={r.DepthBias} DepthBiasClamp={r.DepthBiasClamp:F3} SlopeScaledBias={r.SlopeScaledDepthBias:F3} "
                + $"DepthClip={r.DepthClipEnable} MultisampleEnable={r.MultisampleEnable} "
                + $"AntialiasedLine={r.AntialiasedLineEnable} ForcedSampleCount={r.ForcedSampleCount}\n";
        }

        private static string dumpRootSignatureLayout(GraphicsPipelineDescription d)
        {
            var layouts = d.ResourceLayouts;
            if (layouts == null || layouts.Length == 0)
                return "  (no ResourceLayouts in pipeline description)\n";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < layouts.Length; i++)
            {
                if (layouts[i] is not D3D12ResourceLayout d12layout)
                {
                    sb.Append($"  [{i}] (non-D3D12 layout?? {layouts[i]?.GetType().Name})\n");
                    continue;
                }
                sb.Append($"  ResourceLayout[{i}] (space={i}): {d12layout.Elements.Length} elements, CBV/SRV/UAV={d12layout.CbvSrvUavCount} Sampler={d12layout.SamplerCount}\n");
                for (int j = 0; j < d12layout.Elements.Length; j++)
                {
                    var el = d12layout.Elements[j];
                    sb.Append($"    [{j}] kind={el.Kind} stages={el.Stages} name='{el.Name}'\n");
                }
            }
            return sb.ToString();
        }

        private static string dumpInputLayout(InputLayoutDescription? layoutMaybe)
        {
            if (layoutMaybe == null)
                return "  (InputLayout is null)\n";

            var elems = layoutMaybe.Elements;
            if (elems == null || elems.Length == 0)
                return "  (InputLayout is empty)\n";

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < elems.Length; i++)
            {
                var e = elems[i];
                sb.Append($"  [{i}] {e.SemanticName}{e.SemanticIndex} slot={e.Slot} ")
                  .Append($"offset={e.AlignedByteOffset} format={e.Format} ")
                  .Append($"class={e.Classification} stepRate={e.InstanceDataStepRate}\n");
            }
            return sb.ToString();
        }

        private static string drainCallbackMessages(D3D12GraphicsDevice gd)
        {
            // The push-mode counterpart to drainInfoQueue. Reads any
            // messages that ID3D12InfoQueue1.RegisterMessageCallback
            // captured into D3D12GraphicsDevice.DebugMessages since this
            // device was created.
            var list = gd.DebugMessages;
            if (list == null)
                return "  (callback unavailable — ID3D12InfoQueue1 not exposed by Vortice on this device)\n";

            lock (gd)
            {
                if (list.Count == 0)
                    return "  (callback registered but no messages received — debug layer is silent for this draw path)\n";

                var sb = new System.Text.StringBuilder();
                int count = list.Count < 50 ? list.Count : 50;
                for (int i = 0; i < count; i++)
                    sb.Append("  ").Append(list[i]).Append('\n');
                list.Clear();
                return sb.ToString();
            }
        }

        private static string drainInfoQueue(D3D12GraphicsDevice gd)
        {
            // Use the device's pre-configured InfoQueue (set up during
            // device construction with an allow-all storage filter +
            // break-on-severity disabled). Falls back to a fresh QI if
            // for some reason the device didn't cache one.
            var iq = gd.InfoQueue;
            if (iq == null)
            {
                try { iq = gd.Device.QueryInterfaceOrNull<ID3D12InfoQueue>(); }
                catch { return "  (InfoQueue not available — debug layer inactive or Graphics Tools not installed)\n"; }
                if (iq == null) return "  (InfoQueue not available — debug layer inactive or Graphics Tools not installed)\n";
            }

            try
            {
                ulong count = iq.NumStoredMessages;
                if (count == 0)
                    return "  (InfoQueue present but empty — debug layer attached but no validator messages recorded)\n";

                var sb = new System.Text.StringBuilder();
                ulong limit = count < 50UL ? count : 50UL;
                for (ulong i = 0; i < limit; i++)
                {
                    try
                    {
                        var msg = iq.GetMessage(i);
                        sb.Append("  [").Append(msg.Severity).Append("] ")
                          .Append(msg.Description).Append('\n');
                    }
                    catch (Exception ex)
                    {
                        sb.Append("  (GetMessage(").Append(i).Append(") threw: ").Append(ex.Message).Append(")\n");
                        break;
                    }
                }
                iq.ClearStoredMessages();
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"  (InfoQueue read threw: {ex.GetType().Name}: {ex.Message})\n";
            }
        }

        // ---- RootSignature helpers --------------------------------------

        private static DescriptorRange1[] buildCbvSrvUavRanges(D3D12ResourceLayout layout, ref int cbvBase, ref int srvBase, ref int uavBase)
        {
            // Walk the layout's non-sampler elements once and bucket them
            // by descriptor range type. CBV/SRV/UAV each need their own
            // range entry in the table — they're different namespaces on
            // the shader side (b#, t#, u#).
            //
            // Register-space convention: SPIRV-Cross's HLSL emitter places
            // EVERYTHING in space=0, with running per-type base registers
            // (b0, b1, …) across descriptor sets. Use the caller-supplied
            // ref-counters to allocate the next available slot per type
            // and advance them in lockstep with the shader's view.
            var ranges = new List<DescriptorRange1>();
            int cbvCount = 0, srvCount = 0, uavCount = 0;

            foreach (var e in layout.Elements)
            {
                switch (e.Kind)
                {
                    case ResourceKind.UniformBuffer:                cbvCount++; break;
                    case ResourceKind.TextureReadOnly:              srvCount++; break;
                    case ResourceKind.StructuredBufferReadOnly:     srvCount++; break;
                    case ResourceKind.TextureReadWrite:             uavCount++; break;
                    case ResourceKind.StructuredBufferReadWrite:    uavCount++; break;
                    // Samplers handled in the parallel sampler table.
                }
            }

            int offset = 0;
            if (cbvCount > 0)
            {
                ranges.Add(new DescriptorRange1(DescriptorRangeType.ConstantBufferView, cbvCount,
                    baseShaderRegister: cbvBase, registerSpace: 0,
                    offsetInDescriptorsFromTableStart: offset));
                cbvBase += cbvCount;
                offset += cbvCount;
            }
            if (srvCount > 0)
            {
                ranges.Add(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, srvCount,
                    baseShaderRegister: srvBase, registerSpace: 0,
                    offsetInDescriptorsFromTableStart: offset));
                srvBase += srvCount;
                offset += srvCount;
            }
            if (uavCount > 0)
            {
                ranges.Add(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, uavCount,
                    baseShaderRegister: uavBase, registerSpace: 0,
                    offsetInDescriptorsFromTableStart: offset));
                uavBase += uavCount;
            }

            return ranges.ToArray();
        }

        // ---- Shader helpers ---------------------------------------------

        private static ReadOnlyMemory<byte> findShaderBytes(ShaderSetDescription shaderSet, ShaderStages stage)
        {
            if (shaderSet.Shaders == null) return default;
            foreach (var s in shaderSet.Shaders)
            {
                if (s is D3D12Shader d12 && d12.Stage == stage)
                    return d12.Bytecode;
            }
            return default;
        }

        // ---- Veldrid → D3D12 enum maps (bulk mapping section) -----------

        private static int sampleCountToInt(TextureSampleCount c) => c switch
        {
            TextureSampleCount.Count1 => 1,
            TextureSampleCount.Count2 => 2,
            TextureSampleCount.Count4 => 4,
            TextureSampleCount.Count8 => 8,
            TextureSampleCount.Count16 => 16,
            TextureSampleCount.Count32 => 32,
            _ => 1,
        };

        private static Vortice.Direct3D.PrimitiveTopology toNativePrimitiveTopology(PrimitiveTopology topology) => topology switch
        {
            PrimitiveTopology.PointList     => Vortice.Direct3D.PrimitiveTopology.PointList,
            PrimitiveTopology.LineList      => Vortice.Direct3D.PrimitiveTopology.LineList,
            PrimitiveTopology.LineStrip     => Vortice.Direct3D.PrimitiveTopology.LineStrip,
            PrimitiveTopology.TriangleList  => Vortice.Direct3D.PrimitiveTopology.TriangleList,
            PrimitiveTopology.TriangleStrip => Vortice.Direct3D.PrimitiveTopology.TriangleStrip,
            _ => Vortice.Direct3D.PrimitiveTopology.TriangleList,
        };

        private static VorticePrimitiveTopologyType toPrimitiveTopologyType(PrimitiveTopology topology) => topology switch
        {
            PrimitiveTopology.PointList     => VorticePrimitiveTopologyType.Point,
            PrimitiveTopology.LineList      => VorticePrimitiveTopologyType.Line,
            PrimitiveTopology.LineStrip     => VorticePrimitiveTopologyType.Line,
            PrimitiveTopology.TriangleList  => VorticePrimitiveTopologyType.Triangle,
            PrimitiveTopology.TriangleStrip => VorticePrimitiveTopologyType.Triangle,
            _ => VorticePrimitiveTopologyType.Triangle,
        };

        private static BlendDescription toBlendDescription(BlendStateDescription veldridBlend, OutputDescription outputs)
        {
            int rtCount = outputs.ColorAttachments?.Length ?? 0;

            // BlendDescription.RenderTarget is a Vortice "fixed buffer"
            // wrapper (inline array of 8 entries — D3D12 spec hardcap on
            // simultaneous render targets). It exposes indexer access
            // rather than array assignment, so we initialise the parent
            // struct first then fill the entries one by one.
            var desc = new BlendDescription
            {
                AlphaToCoverageEnable = veldridBlend.AlphaToCoverageEnabled,
                // Setting IndependentBlendEnable = true forces D3D12 to
                // validate EVERY one of the 8 RenderTarget blend slots
                // against the pixel-shader output signature, including
                // slots beyond the actual NumRenderTargets — Vortice
                // marshals all 8 entries regardless. With osu-framework's
                // single-RT pipelines that translates to "your slot-1..7
                // blend state references an RT that isn't bound" →
                // E_INVALIDARG. Flip it false so only slot 0 matters; we
                // are not currently doing per-RT blending anyway.
                IndependentBlendEnable = false,
            };

            for (int i = 0; i < 8; i++)
            {
                if (i < rtCount && i < veldridBlend.AttachmentStates.Length)
                {
                    var att = veldridBlend.AttachmentStates[i];
                    desc.RenderTarget[i] = new RenderTargetBlendDescription
                    {
                        BlendEnable = att.BlendEnabled,
                        LogicOpEnable = false,
                        SourceBlend = toBlend(att.SourceColorFactor),
                        DestinationBlend = toBlend(att.DestinationColorFactor),
                        BlendOperation = toBlendOp(att.ColorFunction),
                        SourceBlendAlpha = toBlend(att.SourceAlphaFactor),
                        DestinationBlendAlpha = toBlend(att.DestinationAlphaFactor),
                        BlendOperationAlpha = toBlendOp(att.AlphaFunction),
                        LogicOp = LogicOp.Noop,
                        RenderTargetWriteMask = toColorWriteEnable(att.ColorWriteMask ?? ColorWriteMask.All),
                    };
                }
                else
                {
                    desc.RenderTarget[i] = new RenderTargetBlendDescription { RenderTargetWriteMask = ColorWriteEnable.All };
                }
            }

            return desc;
        }

        private static VorticeBlend toBlend(BlendFactor f) => f switch
        {
            BlendFactor.Zero => VorticeBlend.Zero,
            BlendFactor.One => VorticeBlend.One,
            BlendFactor.SourceAlpha => VorticeBlend.SourceAlpha,
            BlendFactor.InverseSourceAlpha => VorticeBlend.InverseSourceAlpha,
            BlendFactor.DestinationAlpha => VorticeBlend.DestinationAlpha,
            BlendFactor.InverseDestinationAlpha => VorticeBlend.InverseDestinationAlpha,
            BlendFactor.SourceColor => VorticeBlend.SourceColor,
            BlendFactor.InverseSourceColor => VorticeBlend.InverseSourceColor,
            BlendFactor.DestinationColor => VorticeBlend.DestinationColor,
            BlendFactor.InverseDestinationColor => VorticeBlend.InverseDestinationColor,
            BlendFactor.BlendFactor => VorticeBlend.BlendFactor,
            BlendFactor.InverseBlendFactor => VorticeBlend.InverseBlendFactor,
            _ => VorticeBlend.One,
        };

        private static VorticeBlendOperation toBlendOp(BlendFunction f) => f switch
        {
            BlendFunction.Add => VorticeBlendOperation.Add,
            BlendFunction.Subtract => VorticeBlendOperation.Subtract,
            BlendFunction.ReverseSubtract => VorticeBlendOperation.RevSubtract,
            BlendFunction.Minimum => VorticeBlendOperation.Min,
            BlendFunction.Maximum => VorticeBlendOperation.Max,
            _ => VorticeBlendOperation.Add,
        };

        private static ColorWriteEnable toColorWriteEnable(ColorWriteMask mask)
        {
            ColorWriteEnable r = 0;
            if ((mask & ColorWriteMask.Red) != 0) r |= ColorWriteEnable.Red;
            if ((mask & ColorWriteMask.Green) != 0) r |= ColorWriteEnable.Green;
            if ((mask & ColorWriteMask.Blue) != 0) r |= ColorWriteEnable.Blue;
            if ((mask & ColorWriteMask.Alpha) != 0) r |= ColorWriteEnable.Alpha;
            return r;
        }

        private static RasterizerDescription toRasterizerDescription(RasterizerStateDescription r)
        {
            return new RasterizerDescription
            {
                FillMode = r.FillMode == PolygonFillMode.Wireframe ? VorticeFillMode.Wireframe : VorticeFillMode.Solid,
                CullMode = r.CullMode switch
                {
                    FaceCullMode.None => VorticeCullMode.None,
                    FaceCullMode.Front => VorticeCullMode.Front,
                    FaceCullMode.Back => VorticeCullMode.Back,
                    _ => VorticeCullMode.Back,
                },
                FrontCounterClockwise = r.FrontFace == FrontFace.CounterClockwise,
                DepthBias = 0,
                DepthBiasClamp = 0,
                SlopeScaledDepthBias = 0,
                DepthClipEnable = !r.DepthClipEnabled ? false : true,
                MultisampleEnable = false,
                AntialiasedLineEnable = false,
                ForcedSampleCount = 0,
                ConservativeRaster = ConservativeRasterizationMode.Off,
            };
        }

        private static DepthStencilDescription toDepthStencilDescription(DepthStencilStateDescription d)
        {
            return new DepthStencilDescription
            {
                DepthEnable = d.DepthTestEnabled,
                DepthWriteMask = d.DepthWriteEnabled ? DepthWriteMask.All : DepthWriteMask.Zero,
                DepthFunc = toComparison(d.DepthComparison),
                StencilEnable = d.StencilTestEnabled,
                StencilReadMask = d.StencilReadMask,
                StencilWriteMask = d.StencilWriteMask,
                FrontFace = toStencilOp(d.StencilFront),
                BackFace = toStencilOp(d.StencilBack),
            };
        }

        private static VorticeComparisonFunction toComparison(ComparisonKind k) => k switch
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

        private static DepthStencilOperationDescription toStencilOp(StencilBehaviorDescription s)
        {
            return new DepthStencilOperationDescription
            {
                StencilFailOp = toStencilOpEnum(s.Fail),
                StencilDepthFailOp = toStencilOpEnum(s.DepthFail),
                StencilPassOp = toStencilOpEnum(s.Pass),
                StencilFunc = toComparison(s.Comparison),
            };
        }

        private static VorticeStencilOperation toStencilOpEnum(StencilOperation o) => o switch
        {
            StencilOperation.Keep => VorticeStencilOperation.Keep,
            StencilOperation.Zero => VorticeStencilOperation.Zero,
            StencilOperation.Replace => VorticeStencilOperation.Replace,
            StencilOperation.IncrementAndClamp => VorticeStencilOperation.IncrementSaturate,
            StencilOperation.DecrementAndClamp => VorticeStencilOperation.DecrementSaturate,
            StencilOperation.Invert => VorticeStencilOperation.Invert,
            StencilOperation.IncrementAndWrap => VorticeStencilOperation.Increment,
            StencilOperation.DecrementAndWrap => VorticeStencilOperation.Decrement,
            _ => VorticeStencilOperation.Keep,
        };

        private static InputLayoutDescription toInputLayoutDescription(VertexLayoutDescription[] vertexLayouts)
        {
            // Two corrections vs the original scaffold:
            //
            // 1. Semantic name mapping must match what the HLSL shader
            //    bytecode actually declares — TextureCoordinate → "TEXCOORD"
            //    (NOT "TEXTURECOORDINATE" from .ToString().ToUpperInvariant()).
            //    SPIRV-Cross + the D3D11 backend (D3D11ResourceCache.
            //    getSemanticString) both produce the short forms, so the
            //    D3D12 PSO validator was rejecting every pipeline as
            //    "input signature mismatch" → E_FAIL.
            //
            // 2. Repeated semantics need incrementing indices ("TEXCOORD0",
            //    "TEXCOORD1", ...) per the HLSL signature emitted by SPIRV-
            //    Cross. The scaffold was hardcoding semanticIndex:0 for
            //    every element, so multi-attribute vertex layouts (osu!'s
            //    TexturedVertex2D — Position + Color + TexCoord(0) +
            //    TexRect/Custom(TexCoord1) + …) collided on the same slot.
            //    Mirror D3D11's SemanticIndices counter pattern.
            var elements = new List<InputElementDescription>();
            int positionIdx = 0, texCoordIdx = 0, normalIdx = 0, colorIdx = 0;

            for (int slot = 0; slot < vertexLayouts.Length; slot++)
            {
                var layout = vertexLayouts[slot];
                int offset = 0;
                foreach (var e in layout.Elements)
                {
                    int useOffset = e.Offset != 0 ? (int)e.Offset : offset;

                    string semanticName;
                    int semanticIndex;
                    switch (e.Semantic)
                    {
                        case VertexElementSemantic.Position:
                            semanticName = "POSITION";
                            semanticIndex = positionIdx++;
                            break;
                        case VertexElementSemantic.Normal:
                            semanticName = "NORMAL";
                            semanticIndex = normalIdx++;
                            break;
                        case VertexElementSemantic.TextureCoordinate:
                            semanticName = "TEXCOORD";
                            semanticIndex = texCoordIdx++;
                            break;
                        case VertexElementSemantic.Color:
                            semanticName = "COLOR";
                            semanticIndex = colorIdx++;
                            break;
                        default:
                            // Fallback for any future-added semantic — use
                            // the raw enum name uppercased so the error is
                            // at least debuggable rather than throwing.
                            semanticName = e.Semantic.ToString().ToUpperInvariant();
                            semanticIndex = 0;
                            break;
                    }

                    elements.Add(new InputElementDescription(
                        semanticName: semanticName,
                        semanticIndex: semanticIndex,
                        format: vertexFormatToDxgi(e.Format),
                        offset: useOffset,
                        slot: slot,
                        slotClass: layout.InstanceStepRate == 0 ? InputClassification.PerVertexData : InputClassification.PerInstanceData,
                        stepRate: (int)layout.InstanceStepRate));
                    offset = useOffset + vertexFormatStride(e.Format);
                }
            }
            return new InputLayoutDescription(elements.ToArray());
        }

        private static Format vertexFormatToDxgi(VertexElementFormat f) => f switch
        {
            VertexElementFormat.Float1 => Format.R32_Float,
            VertexElementFormat.Float2 => Format.R32G32_Float,
            VertexElementFormat.Float3 => Format.R32G32B32_Float,
            VertexElementFormat.Float4 => Format.R32G32B32A32_Float,
            VertexElementFormat.Byte2Norm => Format.R8G8_UNorm,
            VertexElementFormat.Byte2 => Format.R8G8_UInt,
            VertexElementFormat.Byte4Norm => Format.R8G8B8A8_UNorm,
            VertexElementFormat.Byte4 => Format.R8G8B8A8_UInt,
            VertexElementFormat.SByte2Norm => Format.R8G8_SNorm,
            VertexElementFormat.SByte2 => Format.R8G8_SInt,
            VertexElementFormat.SByte4Norm => Format.R8G8B8A8_SNorm,
            VertexElementFormat.SByte4 => Format.R8G8B8A8_SInt,
            VertexElementFormat.UShort2Norm => Format.R16G16_UNorm,
            VertexElementFormat.UShort2 => Format.R16G16_UInt,
            VertexElementFormat.UShort4Norm => Format.R16G16B16A16_UNorm,
            VertexElementFormat.UShort4 => Format.R16G16B16A16_UInt,
            VertexElementFormat.Short2Norm => Format.R16G16_SNorm,
            VertexElementFormat.Short2 => Format.R16G16_SInt,
            VertexElementFormat.Short4Norm => Format.R16G16B16A16_SNorm,
            VertexElementFormat.Short4 => Format.R16G16B16A16_SInt,
            VertexElementFormat.UInt1 => Format.R32_UInt,
            VertexElementFormat.UInt2 => Format.R32G32_UInt,
            VertexElementFormat.UInt3 => Format.R32G32B32_UInt,
            VertexElementFormat.UInt4 => Format.R32G32B32A32_UInt,
            VertexElementFormat.Int1 => Format.R32_SInt,
            VertexElementFormat.Int2 => Format.R32G32_SInt,
            VertexElementFormat.Int3 => Format.R32G32B32_SInt,
            VertexElementFormat.Int4 => Format.R32G32B32A32_SInt,
            VertexElementFormat.Half1 => Format.R16_Float,
            VertexElementFormat.Half2 => Format.R16G16_Float,
            VertexElementFormat.Half4 => Format.R16G16B16A16_Float,
            _ => Format.R32G32B32A32_Float,
        };

        private static int vertexFormatStride(VertexElementFormat f) => f switch
        {
            VertexElementFormat.Float1 or VertexElementFormat.UInt1 or VertexElementFormat.Int1 => 4,
            VertexElementFormat.Float2 or VertexElementFormat.UInt2 or VertexElementFormat.Int2 => 8,
            VertexElementFormat.Float3 or VertexElementFormat.UInt3 or VertexElementFormat.Int3 => 12,
            VertexElementFormat.Float4 or VertexElementFormat.UInt4 or VertexElementFormat.Int4 => 16,
            VertexElementFormat.Byte2 or VertexElementFormat.Byte2Norm or VertexElementFormat.SByte2 or VertexElementFormat.SByte2Norm => 2,
            VertexElementFormat.Byte4 or VertexElementFormat.Byte4Norm or VertexElementFormat.SByte4 or VertexElementFormat.SByte4Norm => 4,
            VertexElementFormat.UShort2 or VertexElementFormat.UShort2Norm or VertexElementFormat.Short2 or VertexElementFormat.Short2Norm or VertexElementFormat.Half2 => 4,
            VertexElementFormat.UShort4 or VertexElementFormat.UShort4Norm or VertexElementFormat.Short4 or VertexElementFormat.Short4Norm or VertexElementFormat.Half4 => 8,
            VertexElementFormat.Half1 => 2,
            _ => 16,
        };

        private static Format[] collectRtvFormats(OutputDescription outputs)
        {
            // Size to actual attachment count, NOT padded to 8.
            // Vortice 2.4.2 marshals NumRenderTargets =
            // Math.Min(RenderTargetFormats.Length, 8), and D3D12's
            // PSO validator compares that count against the pixel
            // shader's SV_TARGETn output signature byte-for-byte —
            // padding with Format.Unknown trailers makes D3D12 think
            // we want 8 RTs and rejects the whole PSO.
            int count = outputs.ColorAttachments?.Length ?? 0;
            if (count > 8) count = 8;

            var arr = new Format[count];
            for (int i = 0; i < count; i++)
                arr[i] = D3D12Formats.ToDxgiFormat(outputs.ColorAttachments![i].Format, depthFormat: false);
            return arr;
        }

        public override void Dispose()
        {
            if (disposed) return;
            pso.Dispose();
            rootSignature.Dispose();
            disposed = true;
        }
    }
}
