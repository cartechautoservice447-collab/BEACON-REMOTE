using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Capture;

/// <summary>
/// Well-known DXGI and Win32 HRESULT constants encountered during Desktop Duplication.
/// </summary>
public static class DxgiHResult
{
    public const int SOk = 0;
    public const int DxgiErrorInvalidCall = unchecked((int)0x887A0001);
    public const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    public const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    public const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    public const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    public const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    public const int DxgiErrorDriverInternalError = unchecked((int)0x887A0020);
    public const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);
    public const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    public const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    public const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);
    public const int DxgiErrorAccessDenied = unchecked((int)0x887A002B);
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int EOutOfMemory = unchecked((int)0x8007000E);
}

/// <summary>
/// Normalized classification of a DXGI Desktop Duplication operation or frame acquisition attempt.
/// </summary>
public enum DxgiCaptureStatus
{
    Ok = 0,
    WaitTimeout = 1,
    AccessLost = 2,
    DeviceRemovedOrReset = 3,
    InvalidCall = 4,
    AccessDeniedOrProtectedDesktop = 5,
    SessionDisconnected = 6,
    UnsupportedOrUnavailable = 7,
    MaxDuplicationClientsReached = 8,
    MonitorNotFound = 9,
    PoolExhausted = 10,
    UnknownError = 11,
}

/// <summary>
/// Represents the current availability of the interactive user desktop surface for capture.
/// </summary>
public enum DesktopSurfaceAvailability
{
    Available = 0,

    /// <summary>
    /// Windows switched to a protected desktop (UAC elevation prompt, Winlogon lock screen, or Secure Desktop).
    /// Capture pauses cleanly without attempting to bypass OS security boundaries.
    /// </summary>
    ProtectedOrLockedDesktop = 1,

    /// <summary>
    /// The interactive terminal/console session is disconnected.
    /// </summary>
    SessionDisconnected = 2,

    /// <summary>
    /// Display mode, rotation, or GPU driver is transitioning and duplication is being recreated.
    /// </summary>
    DeviceRecovering = 3,

    /// <summary>
    /// Capture source is stopped or target output is not available.
    /// </summary>
    Unavailable = 4,
}

/// <summary>
/// Maps raw DXGI and Win32 HRESULTs into actionable <see cref="DxgiCaptureStatus"/> values.
/// </summary>
public static class DxgiHResultClassifier
{
    public static DxgiCaptureStatus Classify(int hResult) =>
        hResult switch
        {
            DxgiHResult.SOk => DxgiCaptureStatus.Ok,
            DxgiHResult.DxgiErrorWaitTimeout => DxgiCaptureStatus.WaitTimeout,
            DxgiHResult.DxgiErrorAccessLost => DxgiCaptureStatus.AccessLost,
            DxgiHResult.DxgiErrorDeviceRemoved
                or DxgiHResult.DxgiErrorDeviceHung
                or DxgiHResult.DxgiErrorDeviceReset
                or DxgiHResult.DxgiErrorDriverInternalError => DxgiCaptureStatus.DeviceRemovedOrReset,
            DxgiHResult.DxgiErrorInvalidCall => DxgiCaptureStatus.InvalidCall,
            DxgiHResult.DxgiErrorAccessDenied
                or DxgiHResult.EAccessDenied => DxgiCaptureStatus.AccessDeniedOrProtectedDesktop,
            DxgiHResult.DxgiErrorSessionDisconnected => DxgiCaptureStatus.SessionDisconnected,
            DxgiHResult.DxgiErrorNotCurrentlyAvailable => DxgiCaptureStatus.MaxDuplicationClientsReached,
            DxgiHResult.DxgiErrorUnsupported => DxgiCaptureStatus.UnsupportedOrUnavailable,
            DxgiHResult.DxgiErrorNotFound => DxgiCaptureStatus.MonitorNotFound,
            _ => DxgiCaptureStatus.UnknownError,
        };

    public static bool IsTransientRecoverable(DxgiCaptureStatus status) =>
        status is DxgiCaptureStatus.AccessLost
            or DxgiCaptureStatus.DeviceRemovedOrReset
            or DxgiCaptureStatus.InvalidCall;

    /// <summary>
    /// Returns true for contention/transition statuses (such as <c>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE</c>)
    /// that merit a strictly bounded delayed retry before failing closed.
    /// </summary>
    public static bool IsBoundedAvailabilityRetry(DxgiCaptureStatus status) =>
        status is DxgiCaptureStatus.MaxDuplicationClientsReached;
}

/// <summary>
/// DXGI pointer shape format matching <c>DXGI_OUTDUPL_POINTER_SHAPE_TYPE</c>.
/// </summary>
public enum PointerShapeKind
{
    Monochrome = 1,
    Color = 2,
    MaskedColor = 4,
}

/// <summary>
/// Immutable snapshot of the hardware/OS cursor glyph when <c>PointerShapeBufferSize &gt; 0</c>.
/// Correlated with <see cref="CursorSnapshot.ShapeVersion"/>.
/// </summary>
public sealed record CursorShapeSnapshot(
    long ShapeVersion,
    PointerShapeKind Kind,
    PixelSize Size,
    int PitchBytes,
    int HotSpotX,
    int HotSpotY,
    ReadOnlyMemory<byte> PixelData);

/// <summary>
/// Represents a <c>DXGI_OUTDUPL_MOVE_RECT</c> desktop scroll/move region.
/// </summary>
public readonly record struct DxgiMoveRect(
    int SourceX,
    int SourceY,
    PixelRect DestinationRect);

/// <summary>
/// Raw pointer state reported by <c>DXGI_OUTDUPL_FRAME_INFO</c> on a frame acquisition tick.
/// </summary>
public sealed record DxgiRawPointerUpdate(
    bool PositionUpdated,
    bool IsVisible,
    int X,
    int Y,
    PointerShapeKind? NewShapeKind = null,
    PixelSize NewShapeSize = default,
    int NewShapePitchBytes = 0,
    int HotSpotX = 0,
    int HotSpotY = 0,
    ReadOnlyMemory<byte> NewShapeBytes = default);

/// <summary>
/// Raw metadata and pixel-copy callback for an acquired DXGI desktop frame prior to <c>ReleaseFrame</c>.
/// </summary>
public sealed record DxgiAcquiredFrameData(
    long PresentTimestampQpcTicks,
    bool HasDesktopImageUpdate,
    PixelSize SurfaceSize,
    FramePixelFormat PixelFormat,
    DisplayRotation SurfaceRotation,
    ReadOnlyMemory<PixelRect> DirtyRects,
    ReadOnlyMemory<DxgiMoveRect> MoveRects,
    DxgiRawPointerUpdate PointerUpdate,
    Func<Memory<byte>, int, CancellationToken, ValueTask>? CopyPixelsDelegate = null,
    FrameBackingKind BackingKind = FrameBackingKind.GpuTexture,
    bool RectsInUnrotatedSpace = false);

/// <summary>
/// Outcome of a single <see cref="IDxgiDuplicationSession.TryAcquireNextFrame"/> call.
/// </summary>
public sealed record DxgiAcquireAttemptResult(
    DxgiCaptureStatus Status,
    int HResult,
    DxgiAcquiredFrameData? FrameData = null,
    string? DiagnosticDetail = null)
{
    public static DxgiAcquireAttemptResult Success(DxgiAcquiredFrameData frameData)
    {
        ArgumentNullException.ThrowIfNull(frameData);
        return new(DxgiCaptureStatus.Ok, DxgiHResult.SOk, frameData);
    }

    public static DxgiAcquireAttemptResult WaitTimeout() =>
        new(DxgiCaptureStatus.WaitTimeout, DxgiHResult.DxgiErrorWaitTimeout);

    public static DxgiAcquireAttemptResult FromHResult(int hResult, string? detail = null) =>
        new(DxgiHResultClassifier.Classify(hResult), hResult, null, detail);
}

/// <summary>
/// Encapsulates an active <c>IDXGIOutputDuplication</c> session and its associated D3D11 staging resources.
/// Guarantees that <see cref="ReleaseFrame"/> is called before the next acquisition or disposal.
/// </summary>
public interface IDxgiDuplicationSession : IDisposable
{
    MonitorDescriptor Monitor { get; }
    PixelSize SurfaceSize { get; }
    FramePixelFormat PixelFormat { get; }
    DisplayRotation SurfaceRotation { get; }
    FrameBackingKind BackingKind { get; }
    bool IsFrameAcquired { get; }

    DxgiAcquireAttemptResult TryAcquireNextFrame(int timeoutMilliseconds);

    /// <summary>
    /// Copies or latches the currently acquired desktop texture into <paramref name="slot"/>
    /// before <see cref="ReleaseFrame"/> is called on the underlying DXGI output duplication.
    /// </summary>
    void CopyAcquiredFrameToSlot(DxgiAcquiredFrameData frameData, DxgiPooledFrameSlot slot);

    /// <summary>
    /// Releases the currently acquired DXGI desktop frame back to the OS compositor.
    /// Must be idempotent if no frame is currently held.
    /// </summary>
    void ReleaseFrame();
}

/// <summary>
/// Abstraction over native Windows DXGI 1.2+ adapter/output enumeration and D3D11 duplication initialization.
/// Enables deterministic unit testing of recovery, rotation, frame lifecycle, and multi-monitor switching
/// while binding directly to <c>dxgi.dll</c> and <c>d3d11.dll</c> in <c>Beacon.Companion.Windows</c>.
/// </summary>
public interface IDxgiAdapterBackend : IAsyncDisposable
{
    Task<IReadOnlyList<MonitorDescriptor>> EnumerateMonitorsAsync(CancellationToken ct = default);

    Task<IDxgiDuplicationSession> OpenDuplicationSessionAsync(
        MonitorDescriptor monitor,
        CancellationToken ct = default);

    Task ResetAdapterResourcesAsync(CancellationToken ct = default);

    long GetTimestampQpcTicks();
}

/// <summary>
/// Configuration and resource-protection limits for <see cref="DxgiScreenCaptureSource"/>.
/// </summary>
public sealed record DxgiScreenCaptureOptions
{
    public const string PrimaryMonitorAlias = "primary";

    /// <summary>
    /// Maximum number of concurrent frame leases in the bounded pool (1..16).
    /// Prevents unbounded GPU/CPU memory growth if a downstream consumer stalls.
    /// </summary>
    public int MaxPooledFrames { get; init; } = 4;

    /// <summary>
    /// Maximum consecutive transient recovery attempts (<c>ACCESS_LOST</c>, <c>DEVICE_RESET</c>, <c>INVALID_CALL</c>)
    /// within a single <see cref="IScreenCaptureSource.AcquireNextFrameAsync"/> call before returning null or throwing.
    /// </summary>
    public int MaxConsecutiveRecoveryAttempts { get; init; } = 4;

    /// <summary>
    /// Bounded non-busy delay before recreating output duplication after <c>DXGI_ERROR_ACCESS_LOST</c>.
    /// </summary>
    public TimeSpan AccessLostRetryDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Bounded non-busy delay before recreating D3D11 device after <c>DXGI_ERROR_DEVICE_REMOVED</c> or <c>DXGI_ERROR_DEVICE_RESET</c>.
    /// </summary>
    public TimeSpan DeviceResetRetryDelay { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>
    /// Bounded non-busy backoff delay when Windows is on a Protected / Secure Desktop (<c>DXGI_ERROR_ACCESS_DENIED</c>).
    /// Prevents high-CPU busy loops while UAC or the Windows lock screen is active.
    /// </summary>
    public TimeSpan ProtectedDesktopBackoffDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Upper bound on a single <c>AcquireNextFrameAsync</c> wait timeout.
    /// </summary>
    public TimeSpan MaxAcquireTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Bounded retry count when DXGI reports <c>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE</c> (<c>0x887A0022</c>),
    /// which can happen transiently during DWM desktop transitions or duplication client teardown before
    /// indicating persistent 4-client quota exhaustion.
    /// </summary>
    public int MaxNotCurrentlyAvailableRetries { get; init; } = 3;

    /// <summary>
    /// Bounded non-busy delay between <c>DXGI_ERROR_NOT_CURRENTLY_AVAILABLE</c> recovery retries.
    /// </summary>
    public TimeSpan NotCurrentlyAvailableRetryDelay { get; init; } = TimeSpan.FromMilliseconds(75);

    /// <summary>
    /// When false (default), cursor-only updates without desktop pixel changes update <c>LatestCursor</c>
    /// and <c>LatestCursorShape</c> and immediately release the DXGI frame without emitting a duplicate video frame.
    /// When true, cursor-only updates emit a frame if a valid desktop surface is present.
    /// </summary>
    public bool EmitCursorOnlyFrames { get; init; }

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxPooledFrames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxPooledFrames, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxConsecutiveRecoveryAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxConsecutiveRecoveryAttempts, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxNotCurrentlyAvailableRetries, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxNotCurrentlyAvailableRetries, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(AccessLostRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(DeviceResetRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(ProtectedDesktopBackoffDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(NotCurrentlyAvailableRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaxAcquireTimeout, TimeSpan.Zero);
    }
}

/// <summary>
/// Immutable telemetry and lifecycle snapshot for <see cref="DxgiScreenCaptureSource"/>.
/// </summary>
public sealed record DxgiCaptureDiagnostics(
    long CaptureEpoch,
    long TotalFramesPublished,
    long TotalCursorOnlyUpdates,
    long TotalTimeouts,
    long TotalAccessLostRecoveries,
    long TotalDeviceResetRecoveries,
    long TotalProtectedDesktopEvents,
    long TotalPoolExhaustedDrops,
    int ActiveLeasedFrames,
    int TotalPooledSlots,
    DesktopSurfaceAvailability SurfaceAvailability,
    DxgiCaptureStatus LastStatus,
    int LastHResult,
    long TotalNotCurrentlyAvailableRetries = 0);

/// <summary>
/// Exception thrown when DXGI Desktop Duplication encounters a non-recoverable or unsupported error.
/// </summary>
public sealed class DxgiCaptureException : Exception
{
    public DxgiCaptureException()
        : base("DXGI desktop capture failed.")
    {
        Status = DxgiCaptureStatus.UnknownError;
    }

    public DxgiCaptureException(string message)
        : base(message)
    {
        Status = DxgiCaptureStatus.UnknownError;
    }

    public DxgiCaptureException(string message, Exception innerException)
        : base(message, innerException)
    {
        Status = DxgiCaptureStatus.UnknownError;
    }

    public DxgiCaptureException(DxgiCaptureStatus status, int hResult, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        HResult = hResult;
    }

    public DxgiCaptureStatus Status { get; }
}

/// <summary>
/// Outcome of an interactive desktop identity verification check prior to and immediately after
/// initial idle desktop fallback capture.
/// </summary>
public readonly record struct DxgiDesktopIdentityCheckResult(
    bool IsAllowed,
    DxgiCaptureStatus FailureStatus,
    int FailureHResult,
    string DiagnosticDetail);

/// <summary>
/// Fail-closed security policy verifying that initial idle desktop fallback capture executes strictly
/// on the logged-in user's interactive <c>WinSta0\Default</c> desktop and never on <c>Winlogon</c>,
/// UAC Secure Desktop, <c>Disconnect</c>, Session 0, or across a mid-capture desktop switch.
/// </summary>
public static class DxgiDesktopIdentityPolicy
{
    public const string InteractiveWindowStationName = "WinSta0";
    public const string InteractiveDefaultDesktopName = "Default";

    public static DxgiDesktopIdentityCheckResult EvaluateInitialFallbackDesktop(
        bool isUserInteractive,
        bool isWindowStationVisible,
        string? windowStationName,
        string? threadDesktopName,
        string? preflightInputDesktopName,
        bool verifyPostCapture = false,
        string? postCaptureThreadDesktopName = null,
        string? postCaptureInputDesktopName = null)
    {
        if (!isUserInteractive || !isWindowStationVisible)
        {
            return new DxgiDesktopIdentityCheckResult(
                IsAllowed: false,
                FailureStatus: DxgiCaptureStatus.SessionDisconnected,
                FailureHResult: DxgiHResult.DxgiErrorSessionDisconnected,
                DiagnosticDetail: "Initial idle fallback blocked: process or window station is non-interactive.");
        }

        if (!MatchesObjectName(windowStationName, InteractiveWindowStationName))
        {
            return new DxgiDesktopIdentityCheckResult(
                IsAllowed: false,
                FailureStatus: DxgiCaptureStatus.SessionDisconnected,
                FailureHResult: DxgiHResult.DxgiErrorSessionDisconnected,
                DiagnosticDetail: $"Initial idle fallback blocked: window station '{windowStationName ?? "<null>"}' is not '{InteractiveWindowStationName}'.");
        }

        if (!MatchesObjectName(threadDesktopName, InteractiveDefaultDesktopName))
        {
            return new DxgiDesktopIdentityCheckResult(
                IsAllowed: false,
                FailureStatus: DxgiCaptureStatus.AccessDeniedOrProtectedDesktop,
                FailureHResult: DxgiHResult.DxgiErrorAccessDenied,
                DiagnosticDetail: $"Initial idle fallback blocked: thread desktop '{threadDesktopName ?? "<null>"}' is not '{InteractiveDefaultDesktopName}'.");
        }

        if (!MatchesObjectName(preflightInputDesktopName, InteractiveDefaultDesktopName))
        {
            var isDisconnected = MatchesObjectName(preflightInputDesktopName, "Disconnect");
            return new DxgiDesktopIdentityCheckResult(
                IsAllowed: false,
                FailureStatus: isDisconnected
                    ? DxgiCaptureStatus.SessionDisconnected
                    : DxgiCaptureStatus.AccessDeniedOrProtectedDesktop,
                FailureHResult: isDisconnected
                    ? DxgiHResult.DxgiErrorSessionDisconnected
                    : DxgiHResult.DxgiErrorAccessDenied,
                DiagnosticDetail: $"Initial idle fallback blocked: active input desktop '{preflightInputDesktopName ?? "<inaccessible>"}' is not '{InteractiveDefaultDesktopName}'.");
        }

        if (verifyPostCapture)
        {
            if (!MatchesObjectName(postCaptureThreadDesktopName, InteractiveDefaultDesktopName) ||
                !MatchesObjectName(postCaptureInputDesktopName, InteractiveDefaultDesktopName))
            {
                var isDisconnected = MatchesObjectName(postCaptureInputDesktopName, "Disconnect");
                return new DxgiDesktopIdentityCheckResult(
                    IsAllowed: false,
                    FailureStatus: isDisconnected
                        ? DxgiCaptureStatus.SessionDisconnected
                        : DxgiCaptureStatus.AccessDeniedOrProtectedDesktop,
                    FailureHResult: isDisconnected
                        ? DxgiHResult.DxgiErrorSessionDisconnected
                        : DxgiHResult.DxgiErrorAccessDenied,
                    DiagnosticDetail: $"Initial idle fallback aborted: desktop changed during capture to '{postCaptureInputDesktopName ?? "<inaccessible>"}' (thread='{postCaptureThreadDesktopName ?? "<null>"}').");
            }
        }

        return new DxgiDesktopIdentityCheckResult(
            IsAllowed: true,
            FailureStatus: DxgiCaptureStatus.Ok,
            FailureHResult: DxgiHResult.SOk,
            DiagnosticDetail: string.Empty);
    }

    private static bool MatchesObjectName(string? actual, string expected)
    {
        if (string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        var trimmed = actual.AsSpan().Trim().Trim('\0');
        return trimmed.Equals(expected.AsSpan(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Validates native DXGI surface formats for Step 3.5 (<c>IDXGIOutput1::DuplicateOutput</c>).
/// Standard <c>IDXGIOutput1::DuplicateOutput</c> provides 32-bit SDR <c>DXGI_FORMAT_B8G8R8A8_UNORM</c> (87)
/// or <c>DXGI_FORMAT_B8G8R8A8_UNORM_SRGB</c> (91) surfaces (<see cref="FramePixelFormat.Bgra8Unorm"/>).
/// Higher-bit-depth HDR duplication (<c>IDXGIOutput5::DuplicateOutput1</c> / <c>DXGI_FORMAT_R16G16B16A16_FLOAT</c>)
/// is not implemented in Step 3.5 and is explicitly rejected rather than simulated via a format label.
/// </summary>
public static class DxgiFormatValidator
{
    public const uint DxgiFormatUnknown = 0;
    public const uint DxgiFormatR16G16B16A16Float = 10;
    public const uint DxgiFormatB8G8R8A8Unorm = 87;
    public const uint DxgiFormatB8G8R8A8UnormSrgb = 91;

    public static FramePixelFormat ValidateDuplicateOutputFormat(uint dxgiFormat) =>
        dxgiFormat switch
        {
            DxgiFormatB8G8R8A8Unorm or DxgiFormatB8G8R8A8UnormSrgb => FramePixelFormat.Bgra8Unorm,
            _ => throw new DxgiCaptureException(
                DxgiCaptureStatus.UnsupportedOrUnavailable,
                DxgiHResult.DxgiErrorUnsupported,
                $"IDXGIOutput1::DuplicateOutput supports only 32-bit BGRA8 (DXGI_FORMAT_B8G8R8A8_UNORM=87 or _SRGB=91); native format {dxgiFormat} (including unimplemented IDXGIOutput5::DuplicateOutput1 HDR Rgba16Float) is unsupported in Step 3.5."),
        };

    public static void EnsureSupportedSdrPixelFormat(FramePixelFormat pixelFormat)
    {
        if (pixelFormat != FramePixelFormat.Bgra8Unorm)
        {
            throw new DxgiCaptureException(
                DxgiCaptureStatus.UnsupportedOrUnavailable,
                DxgiHResult.DxgiErrorUnsupported,
                $"Step 3.5 IDXGIOutput1::DuplicateOutput supports only FramePixelFormat.Bgra8Unorm (32-bit SDR BGRA8); format '{pixelFormat}' is not implemented.");
        }
    }
}

