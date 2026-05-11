// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Text;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;

namespace Veldrid.D3D12
{
    /// <summary>
    /// Captures Device Removed Extended Data (DRED) breadcrumb + page-fault
    /// info when the GPU hangs / device-removes. Enabled at device-creation
    /// time via <see cref="ID3D12DeviceRemovedExtendedDataSettings"/> in
    /// <see cref="D3D12GraphicsDevice"/>'s DEBUG-only setup.
    /// </summary>
    /// <remarks>
    /// DRED is the canonical D3D12 mechanism for diagnosing "device
    /// removed" failures — the only HRESULT DXGI emits in that scenario
    /// is the opaque <c>DXGI_ERROR_DEVICE_REMOVED</c>. With DRED on, the
    /// device retains a ring buffer of recent command-list operations
    /// the GPU started but didn't finish, plus the virtual-address +
    /// resource info for any page fault. That tells us WHICH draw call
    /// or resource access caused the hang, which is otherwise impossible
    /// to derive from outside a graphics debugger.
    /// </remarks>
    internal static class D3D12DredDump
    {
        public static string Capture(D3D12GraphicsDevice gd)
        {
            int removedReason = gd.Device.DeviceRemovedReason.Code;
            string reasonName = removedReason switch
            {
                unchecked((int)0x887A0005) => "DXGI_ERROR_DEVICE_REMOVED (generic)",
                unchecked((int)0x887A0006) => "DXGI_ERROR_DEVICE_RESET (driver reset us)",
                unchecked((int)0x887A0007) => "DXGI_ERROR_DRIVER_INTERNAL_ERROR (driver bug)",
                unchecked((int)0x887A0020) => "DXGI_ERROR_DEVICE_HUNG (GPU hang from our command stream — bad PSO/state/OOB)",
                unchecked((int)0x887A0021) => "DXGI_ERROR_DEVICE_REMOVED_OPERATION_IN_PROGRESS",
                0 => "S_OK (none — device still alive??)",
                _ => "unknown",
            };

            var sb = new StringBuilder();
            sb.Append("  RemovedReason: 0x").Append(removedReason.ToString("X8"))
              .Append(' ').Append(reasonName).Append('\n');

            var dredOrNull = gd.Device.QueryInterfaceOrNull<ID3D12DeviceRemovedExtendedData>();
            if (dredOrNull == null)
            {
                sb.Append("  (ID3D12DeviceRemovedExtendedData interface not available — Vortice may have skipped DRED setup despite the SetAutoBreadcrumbsEnablement call)\n");
                return sb.ToString();
            }

            using (dredOrNull)
            {
                try
                {
                    var crumbsResult = dredOrNull.GetAutoBreadcrumbsOutput(out DredAutoBreadcrumbsOutput crumbs);
                    if (crumbsResult.Success)
                    {
                        sb.Append("  Breadcrumbs available (chain head non-null) — full breadcrumb walking pending\n");
                    }
                    else
                    {
                        sb.Append("  GetAutoBreadcrumbsOutput returned 0x").Append(crumbsResult.Code.ToString("X8")).Append('\n');
                    }
                }
                catch (Exception ex)
                {
                    sb.Append("  (GetAutoBreadcrumbsOutput threw: ").Append(ex.Message).Append(")\n");
                }

                try
                {
                    var pfResult = dredOrNull.GetPageFaultAllocationOutput(out DredPageFaultOutput pf);
                    if (pfResult.Success)
                    {
                        if (pf.PageFaultVA != 0)
                            sb.Append("  PageFaultVA=0x").Append(((ulong)pf.PageFaultVA).ToString("X16")).Append('\n');
                        else
                            sb.Append("  (no page fault — hang was not OOB access)\n");
                    }
                    else
                    {
                        sb.Append("  GetPageFaultAllocationOutput returned 0x").Append(pfResult.Code.ToString("X8")).Append('\n');
                    }
                }
                catch (Exception ex)
                {
                    sb.Append("  (GetPageFaultAllocationOutput threw: ").Append(ex.Message).Append(")\n");
                }
            }
            return sb.ToString();
        }
    }
}
