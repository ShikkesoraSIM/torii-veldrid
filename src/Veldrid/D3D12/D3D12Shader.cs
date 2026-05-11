// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;

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

            // Defensive copy — Veldrid's contract doesn't guarantee the
            // caller keeps ShaderBytes alive after CreateShader returns.
            // The bytecode is referenced for the lifetime of every PSO
            // built from it, which can extend well past the shader's
            // creation call.
            Bytecode = new byte[description.ShaderBytes.Length];
            Array.Copy(description.ShaderBytes, Bytecode, description.ShaderBytes.Length);
        }

        public override void Dispose() => disposed = true;
    }
}
