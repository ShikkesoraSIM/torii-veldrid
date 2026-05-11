// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using Vortice.Direct3D;
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

        private readonly ID3D12PipelineState pso;
        private readonly ID3D12RootSignature rootSignature;
        private bool disposed;

        public D3D12Pipeline(D3D12GraphicsDevice gd, ref GraphicsPipelineDescription description)
            : base(ref description)
        {
            Name = string.Empty;

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

            for (int i = 0; i < layoutCount; i++)
            {
                var layout = Util.AssertSubtype<ResourceLayout, D3D12ResourceLayout>(description.ResourceLayouts![i]);

                if (layout.CbvSrvUavCount > 0)
                {
                    // One descriptor table per layout for non-sampler
                    // resources. The slot inside the table maps 1:1 to
                    // the layout's CBV/SRV/UAV element order (see
                    // D3D12ResourceLayout.GetTableSlot).
                    var ranges = buildCbvSrvUavRanges(layout, registerSpace: i);
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
                            baseShaderRegister: 0,
                            registerSpace: i,
                            offsetInDescriptorsFromTableStart: 0)
                    };
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
                // Always dump InfoQueue (with status if empty) + PSO summary
                // together so the failure mode is observable in one log entry.
                string iqDump = drainInfoQueue(gd);
                string summary =
                    $"VS={psoDesc.VertexShader.Length}B PS={psoDesc.PixelShader.Length}B "
                    + $"RTs={psoDesc.RenderTargetFormats?.Length ?? 0} "
                    + $"firstRT={(psoDesc.RenderTargetFormats?.Length > 0 ? psoDesc.RenderTargetFormats[0].ToString() : "n/a")} "
                    + $"DSV={psoDesc.DepthStencilFormat} "
                    + $"InputElements={psoDesc.InputLayout?.Elements?.Length ?? 0} "
                    + $"Topology={psoDesc.PrimitiveTopologyType} "
                    + $"Samples={psoDesc.SampleDescription.Count}";

                throw new VeldridException(
                    $"D3D12 CreateGraphicsPipelineState failed (HRESULT {ex.HResult:X8}).\nPSO summary: {summary}\nInfoQueue:\n{iqDump}",
                    ex);
            }

            // rangeArraysToKeepAlive can fall off the stack now — the root
            // signature has been created and the driver has its own copy.
            GC.KeepAlive(rangeArraysToKeepAlive);
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

        private static DescriptorRange1[] buildCbvSrvUavRanges(D3D12ResourceLayout layout, int registerSpace)
        {
            // Walk the layout's non-sampler elements once and bucket them
            // by descriptor range type. CBV/SRV/UAV each need their own
            // range entry in the table — we DON'T fold them into a single
            // range because the descriptor types are different on the
            // shader side (b#, t#, u# register namespaces).
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
                    baseShaderRegister: 0, registerSpace: registerSpace,
                    offsetInDescriptorsFromTableStart: offset));
                offset += cbvCount;
            }
            if (srvCount > 0)
            {
                ranges.Add(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, srvCount,
                    baseShaderRegister: 0, registerSpace: registerSpace,
                    offsetInDescriptorsFromTableStart: offset));
                offset += srvCount;
            }
            if (uavCount > 0)
            {
                ranges.Add(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, uavCount,
                    baseShaderRegister: 0, registerSpace: registerSpace,
                    offsetInDescriptorsFromTableStart: offset));
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
                IndependentBlendEnable = true,
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
