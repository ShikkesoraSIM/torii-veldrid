// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Vortice.DXGI;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Veldrid <see cref="PixelFormat"/> → DXGI <see cref="Format"/> mapping
    /// for the D3D12 backend. D3D12 itself consumes DXGI formats identically
    /// to D3D11; this helper exists separate from <c>D3D11Formats</c> only to
    /// keep the D3D12 backend self-contained.
    /// </summary>
    /// <remarks>
    /// Specialty formats (ETC2 — Android / mobile-targeted block compression,
    /// ASTC, multi-planar YCbCr) throw <see cref="VeldridException"/> rather
    /// than silently mapping to <see cref="Format.Unknown"/>. A wrong format
    /// upload produces visual corruption that's harder to debug than a clean
    /// resource-creation crash.
    /// </remarks>
    internal static class D3D12Formats
    {
        public static Format ToDxgiFormat(PixelFormat format, bool depthFormat)
        {
            if (depthFormat)
            {
                switch (format)
                {
                    case PixelFormat.R16UNorm: return Format.D16_UNorm;
                    case PixelFormat.R32Float: return Format.D32_Float;
                    case PixelFormat.D24UNormS8UInt: return Format.D24_UNorm_S8_UInt;
                    case PixelFormat.D32FloatS8UInt: return Format.D32_Float_S8X24_UInt;
                    default:
                        throw new VeldridException($"D3D12: unsupported depth format '{format}'.");
                }
            }

            switch (format)
            {
                // 8-bit per channel
                case PixelFormat.R8UNorm: return Format.R8_UNorm;
                case PixelFormat.R8SNorm: return Format.R8_SNorm;
                case PixelFormat.R8UInt: return Format.R8_UInt;
                case PixelFormat.R8SInt: return Format.R8_SInt;

                case PixelFormat.R8G8UNorm: return Format.R8G8_UNorm;
                case PixelFormat.R8G8SNorm: return Format.R8G8_SNorm;
                case PixelFormat.R8G8UInt: return Format.R8G8_UInt;
                case PixelFormat.R8G8SInt: return Format.R8G8_SInt;

                case PixelFormat.R8G8B8A8UNorm: return Format.R8G8B8A8_UNorm;
                case PixelFormat.R8G8B8A8UNormSRgb: return Format.R8G8B8A8_UNorm_SRgb;
                case PixelFormat.R8G8B8A8SNorm: return Format.R8G8B8A8_SNorm;
                case PixelFormat.R8G8B8A8UInt: return Format.R8G8B8A8_UInt;
                case PixelFormat.R8G8B8A8SInt: return Format.R8G8B8A8_SInt;

                case PixelFormat.B8G8R8A8UNorm: return Format.B8G8R8A8_UNorm;
                case PixelFormat.B8G8R8A8UNormSRgb: return Format.B8G8R8A8_UNorm_SRgb;

                // 16-bit per channel
                case PixelFormat.R16UNorm: return Format.R16_UNorm;
                case PixelFormat.R16SNorm: return Format.R16_SNorm;
                case PixelFormat.R16UInt: return Format.R16_UInt;
                case PixelFormat.R16SInt: return Format.R16_SInt;
                case PixelFormat.R16Float: return Format.R16_Float;

                case PixelFormat.R16G16UNorm: return Format.R16G16_UNorm;
                case PixelFormat.R16G16SNorm: return Format.R16G16_SNorm;
                case PixelFormat.R16G16UInt: return Format.R16G16_UInt;
                case PixelFormat.R16G16SInt: return Format.R16G16_SInt;
                case PixelFormat.R16G16Float: return Format.R16G16_Float;

                case PixelFormat.R16G16B16A16UNorm: return Format.R16G16B16A16_UNorm;
                case PixelFormat.R16G16B16A16SNorm: return Format.R16G16B16A16_SNorm;
                case PixelFormat.R16G16B16A16UInt: return Format.R16G16B16A16_UInt;
                case PixelFormat.R16G16B16A16SInt: return Format.R16G16B16A16_SInt;
                case PixelFormat.R16G16B16A16Float: return Format.R16G16B16A16_Float;

                // 32-bit per channel
                case PixelFormat.R32UInt: return Format.R32_UInt;
                case PixelFormat.R32SInt: return Format.R32_SInt;
                case PixelFormat.R32Float: return Format.R32_Float;

                case PixelFormat.R32G32UInt: return Format.R32G32_UInt;
                case PixelFormat.R32G32SInt: return Format.R32G32_SInt;
                case PixelFormat.R32G32Float: return Format.R32G32_Float;

                case PixelFormat.R32G32B32A32UInt: return Format.R32G32B32A32_UInt;
                case PixelFormat.R32G32B32A32SInt: return Format.R32G32B32A32_SInt;
                case PixelFormat.R32G32B32A32Float: return Format.R32G32B32A32_Float;

                // Packed
                case PixelFormat.R10G10B10A2UNorm: return Format.R10G10B10A2_UNorm;
                case PixelFormat.R10G10B10A2UInt: return Format.R10G10B10A2_UInt;
                case PixelFormat.R11G11B10Float: return Format.R11G11B10_Float;

                // Block compression
                case PixelFormat.Bc1RgbUNorm: return Format.BC1_UNorm;
                case PixelFormat.Bc1RgbUNormSRgb: return Format.BC1_UNorm_SRgb;
                case PixelFormat.Bc1RgbaUNorm: return Format.BC1_UNorm;
                case PixelFormat.Bc1RgbaUNormSRgb: return Format.BC1_UNorm_SRgb;
                case PixelFormat.Bc2UNorm: return Format.BC2_UNorm;
                case PixelFormat.Bc2UNormSRgb: return Format.BC2_UNorm_SRgb;
                case PixelFormat.Bc3UNorm: return Format.BC3_UNorm;
                case PixelFormat.Bc3UNormSRgb: return Format.BC3_UNorm_SRgb;
                case PixelFormat.Bc4UNorm: return Format.BC4_UNorm;
                case PixelFormat.Bc4SNorm: return Format.BC4_SNorm;
                case PixelFormat.Bc5UNorm: return Format.BC5_UNorm;
                case PixelFormat.Bc5SNorm: return Format.BC5_SNorm;
                case PixelFormat.Bc7UNorm: return Format.BC7_UNorm;
                case PixelFormat.Bc7UNormSRgb: return Format.BC7_UNorm_SRgb;

                // Depth — handled at the top via the depthFormat flag. If
                // called with depthFormat=false for a depth PixelFormat,
                // throw rather than silently swap to a non-depth equivalent.
                case PixelFormat.D24UNormS8UInt:
                case PixelFormat.D32FloatS8UInt:
                    throw new VeldridException(
                        $"D3D12: format '{format}' is depth-only — pass depthFormat=true.");

                default:
                    throw new VeldridException($"D3D12: PixelFormat '{format}' is not yet mapped.");
            }
        }
    }
}
