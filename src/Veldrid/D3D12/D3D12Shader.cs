// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using Vortice.D3DCompiler;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Direct3D 12 shader — a thin wrapper around the pre-compiled bytecode
    /// the caller passes in (<see cref="ShaderDescription.ShaderBytes"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// D3D12 accepts shader bytecode in two formats:
    /// </para>
    /// <list type="bullet">
    /// <item>DXBC — output of the legacy <c>fxc</c> compiler. Used by
    /// D3D11 era shaders. D3D12 loads them fine; missing the newer
    /// features (wave intrinsics, mesh shaders, etc.).</item>
    /// <item>DXIL — output of the modern <c>dxc</c> compiler. Required for
    /// shader-model 6.0+ features.</item>
    /// </list>
    /// <para>
    /// We don't compile here — the bytecode is whatever
    /// <see cref="ShaderDescription.ShaderBytes"/> contains. osu-framework
    /// runs SPIRV through Veldrid-SPIRV's cross-compile pipeline upstream
    /// of the GraphicsDevice, so by the time we see the bytes, the
    /// compilation work is done. The D3D12 PSO creator will pass the bytes
    /// directly to <c>device.CreateGraphicsPipelineState</c>.
    /// </para>
    /// <para>
    /// EntryPoint is captured for completeness (PSO creation references it
    /// indirectly via the function signature in the bytecode), but
    /// dxc/fxc both bake the entry-point name into the binary so we don't
    /// need to pass it separately to D3D12.
    /// </para>
    /// </remarks>
    internal sealed class D3D12Shader : Shader
    {
        public override bool IsDisposed => disposed;
        public override string Name { get; set; }

        public ShaderStages Stage { get; }
        public string EntryPoint { get; }
        public byte[] Bytecode { get; }

        private bool disposed;

        public D3D12Shader(ref ShaderDescription description)
            : base(description.Stage, description.EntryPoint)
        {
            Name = string.Empty;
            Stage = description.Stage;
            EntryPoint = description.EntryPoint;

            // The caller might pass either compiled DXBC bytecode OR raw
            // HLSL source text. osu-framework's SPIRV cross-compile path
            // emits HLSL SOURCE, not bytecode — D3D12's PSO creator only
            // accepts DXBC/DXIL though, so handing it source text leads
            // straight to E_FAIL with no diagnostic. Mirror D3D11Shader's
            // detection-and-compile: if the buffer starts with the "DXBC"
            // FourCC, use as-is; otherwise treat as HLSL source and run
            // it through D3DCompiler (the same fxc-equivalent that the
            // D3D11 backend uses for the same input).
            if (description.ShaderBytes.Length > 4
                && description.ShaderBytes[0] == 0x44   // 'D'
                && description.ShaderBytes[1] == 0x58   // 'X'
                && description.ShaderBytes[2] == 0x42   // 'B'
                && description.ShaderBytes[3] == 0x43)  // 'C'
            {
                Bytecode = new byte[description.ShaderBytes.Length];
                Array.Copy(description.ShaderBytes, Bytecode, description.ShaderBytes.Length);
            }
            else
            {
                Bytecode = compileHlslToDxbc(description);
            }
        }

        /// <summary>
        /// Compile HLSL source text (UTF-8 byte buffer) to DXBC bytecode
        /// using the same Vortice.D3DCompiler pathway the D3D11 backend
        /// uses. Picks the shader-model profile based on stage.
        /// </summary>
        private static byte[] compileHlslToDxbc(ShaderDescription description)
        {
            // sm_5_0 — same target the D3D11 backend uses unconditionally
            // (it ships with a feature-level fallback to 4_0 but FL11_0
            // is the D3D12 baseline so we don't need that). 5_0 covers
            // everything SPIRV-Cross emits for the shaders osu-framework
            // ships.
            string profile = description.Stage switch
            {
                ShaderStages.Vertex                     => "vs_5_0",
                ShaderStages.Geometry                   => "gs_5_0",
                ShaderStages.TessellationControl        => "hs_5_0",
                ShaderStages.TessellationEvaluation     => "ds_5_0",
                ShaderStages.Fragment                   => "ps_5_0",
                ShaderStages.Compute                    => "cs_5_0",
                _ => throw new VeldridException($"D3D12Shader: unknown shader stage {description.Stage}")
            };

            var flags = description.Debug ? ShaderFlags.Debug : ShaderFlags.OptimizationLevel3;

            Compiler.Compile(
                description.ShaderBytes,
                null!,
                null!,
                description.EntryPoint,
                null!,
                profile,
                flags,
                out var result,
                out var error);

            if (result == null)
            {
                string errText = error != null
                    ? Encoding.ASCII.GetString(error.AsBytes())
                    : "<no error blob returned>";
                throw new VeldridException(
                    $"D3D12Shader: failed to compile HLSL source for stage {description.Stage} (entry '{description.EntryPoint}'): {errText}");
            }

            return result.AsBytes();
        }

        public override void Dispose() => disposed = true;
    }
}
