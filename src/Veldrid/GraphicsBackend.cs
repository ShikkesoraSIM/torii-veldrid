namespace Veldrid
{
    /// <summary>
    ///     The specific graphics API used by the <see cref="GraphicsDevice" />.
    /// </summary>
    public enum GraphicsBackend : byte
    {
        /// <summary>
        ///     Direct3D 11.
        /// </summary>
        Direct3D11,

        /// <summary>
        ///     Vulkan.
        /// </summary>
        Vulkan,

        /// <summary>
        ///     OpenGL.
        /// </summary>
        OpenGL,

        /// <summary>
        ///     Metal.
        /// </summary>
        Metal,

        /// <summary>
        ///     OpenGL ES.
        /// </summary>
        OpenGLES,

        /// <summary>
        ///     Direct3D 12.
        /// </summary>
        /// <remarks>
        ///     Torii Veldrid extension. The backend implementation lives under
        ///     <c>Veldrid.D3D12</c> and is consumed by <c>RendererType.Direct3D12</c>
        ///     / <c>RendererType.Deferred_Direct3D12</c> in osu-framework. Added
        ///     at the end of the enum on purpose so existing persisted config
        ///     values for <c>Direct3D11</c>/<c>Vulkan</c>/<c>OpenGL</c>/<c>Metal</c>/<c>OpenGLES</c>
        ///     stay byte-identical (this is a <c>byte</c>-backed enum).
        /// </remarks>
        Direct3D12
    }
}
