using System.Runtime.Versioning;
using Beacon.Companion.Core.Capture;
using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Windows.Capture;

/// <summary>
/// Windows production composition factory for <see cref="DxgiScreenCaptureSource"/>.
/// Wires <see cref="WindowsDxgiAdapterBackend"/> (native DXGI 1.2 <c>IDXGIOutput1::DuplicateOutput</c> + D3D11)
/// into the platform-independent <see cref="IScreenCaptureSource"/> contract.
/// </summary>
/// <remarks>
/// Known Windows OS Capture Boundaries and Security Constraints (Documented for Step 3.5):
/// <list type="bullet">
///   <item>
///     <description><b>Interactive User Session Required:</b> DXGI Desktop Duplication must run inside the logged-in user's interactive desktop session (never from a non-interactive Session 0 Windows Service or headless CI VM without an active display output).</description>
///   </item>
///   <item>
///     <description><b>Secure Desktop / UAC / Winlogon Lock Screen:</b> When Windows switches to <c>Winlogon</c> or a UAC elevation prompt on the Secure Desktop, DXGI returns <c>DXGI_ERROR_ACCESS_DENIED</c> (<c>0x887A002B</c>) or <c>DXGI_ERROR_ACCESS_LOST</c> (<c>0x887A0026</c>). The companion does not attempt to bypass Windows security boundaries; it transitions cleanly to <see cref="DesktopSurfaceAvailability.ProtectedOrLockedDesktop"/>, notifies <see cref="SessionStateMachine.NotifyDesktopLockedOrUnavailable"/>, waits with a bounded backoff, and resumes automatically when the user returns to the interactive desktop.</description>
///   </item>
///   <item>
///     <description><b>Protected &amp; Excluded Windows:</b> Windows marked with <c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> or DRM Hardware Protected Video Path (PVP) are automatically masked out by the Windows DWM compositor.</description>
///   </item>
///   <item>
///     <description><b>Maximum Concurrent Duplication Clients &amp; Hybrid GPUs:</b> Windows limits Desktop Duplication to 4 concurrent system-wide duplication clients (<c>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE</c>, failing fast on cold start when external quota is full and applying a single non-nested bounded retry only during active in-session duplication recreation) and requires the D3D11 device to be created on the adapter that owns the target output (<c>DXGI_ERROR_UNSUPPORTED</c> on cross-adapter calls).</description>
///   </item>
///   <item>
///     <description><b>32-bit SDR BGRA8 Format Contract (<c>IDXGIOutput1::DuplicateOutput</c>):</b> Step 3.5 uses standard <c>IDXGIOutput1::DuplicateOutput</c>, which provides 32-bit SDR desktop surfaces (<c>DXGI_FORMAT_B8G8R8A8_UNORM</c> = 87 or <c>DXGI_FORMAT_B8G8R8A8_UNORM_SRGB</c> = 91, mapped to <see cref="FramePixelFormat.Bgra8Unorm"/> at 4 bytes/pixel). Higher-bit-depth HDR desktop duplication requires <c>IDXGIOutput5::DuplicateOutput1</c>, which is not implemented in Step 3.5; non-BGRA8 formats (including <c>DXGI_FORMAT_R16G16B16A16_FLOAT</c>) fail closed with <see cref="DxgiCaptureStatus.UnsupportedOrUnavailable"/>.</description>
///   </item>
/// </list>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class WindowsScreenCaptureFactory
{
    public static DxgiScreenCaptureSource Create(
        DxgiScreenCaptureOptions? options = null,
        SessionStateMachine? sessionStateMachine = null,
        TimeProvider? timeProvider = null)
    {
        var backend = new WindowsDxgiAdapterBackend();
        return new DxgiScreenCaptureSource(
            backend,
            options,
            sessionStateMachine,
            timeProvider);
    }
}
