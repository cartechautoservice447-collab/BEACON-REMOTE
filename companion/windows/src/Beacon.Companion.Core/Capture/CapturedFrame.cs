namespace Beacon.Companion.Core.Capture;

public enum FramePixelFormat
{
    Bgra8Unorm = 0,
    Rgba16Float = 1,
}

public enum FrameBackingKind
{
    /// <summary>Frame resides in GPU memory and can be passed directly to a hardware video encoder in Step 4.</summary>
    GpuTexture = 0,

    /// <summary>Frame resides in CPU-accessible staging memory.</summary>
    CpuStaging = 1,
}

public readonly record struct CursorSnapshot(
    bool IsVisible,
    int X,
    int Y,
    long ShapeVersion);

/// <summary>
/// Ownership lease for the backing surface of a captured desktop frame.
/// Implementations in Windows capture modules (Step 3.5) hold pooled D3D11/DXGI resources
/// without exposing raw native pointers to platform-independent Core callers.
/// Disposing this lease returns the texture/buffer to its capture pool.
/// </summary>
public interface IFrameResourceLease : IDisposable
{
    FrameBackingKind BackingKind { get; }
    PixelSize Size { get; }
    FramePixelFormat PixelFormat { get; }

    /// <summary>
    /// Copies pixels into a caller-supplied CPU buffer only when software fallback or diagnostics require it.
    /// Zero-copy GPU pipelines in Step 4 should adapt the underlying Windows-specific lease interface directly.
    /// </summary>
    ValueTask CopyToCpuBufferAsync(Memory<byte> destination, int strideBytes, CancellationToken ct = default);
}

/// <summary>
/// Single captured display frame with explicit deterministic ownership via <see cref="Dispose"/>.
/// Callers must dispose every non-null frame promptly so the underlying bounded GPU texture pool is not starved.
/// </summary>
public sealed class CapturedFrame : IDisposable
{
    private readonly IFrameResourceLease _lease;
    private int _disposed;

    public CapturedFrame(
        long frameSequence,
        long timestampQpcTicks,
        MonitorDescriptor monitor,
        ReadOnlyMemory<PixelRect> dirtyRects,
        CursorSnapshot cursor,
        IFrameResourceLease lease)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(frameSequence);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(lease);

        FrameSequence = frameSequence;
        TimestampQpcTicks = timestampQpcTicks;
        Monitor = monitor;
        DirtyRects = dirtyRects;
        Cursor = cursor;
        _lease = lease;
    }

    public long FrameSequence { get; }
    public long TimestampQpcTicks { get; }
    public MonitorDescriptor Monitor { get; }
    public PixelSize Size => _lease.Size;
    public FramePixelFormat PixelFormat => _lease.PixelFormat;
    public FrameBackingKind BackingKind => _lease.BackingKind;
    public ReadOnlyMemory<PixelRect> DirtyRects { get; }
    public CursorSnapshot Cursor { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public IFrameResourceLease ResourceLease
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _lease;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lease.Dispose();
        }
    }
}

/// <summary>
/// Platform-independent contract for interactive desktop screen capture (DXGI / WGC in Step 3.5).
/// Operates inside the logged-in user's interactive session, never from a Session 0 service.
/// </summary>
public interface IScreenCaptureSource : IAsyncDisposable
{
    bool IsCapturing { get; }
    MonitorDescriptor? ActiveMonitor { get; }

    Task<IReadOnlyList<MonitorDescriptor>> GetMonitorsAsync(CancellationToken ct = default);
    Task StartCaptureAsync(string monitorId, CancellationToken ct = default);

    /// <summary>
    /// Acquires the next updated desktop frame, or returns null if no new visual frame arrived before <paramref name="timeout"/>.
    /// Ownership of the returned <see cref="CapturedFrame"/> transfers to the caller until disposed.
    /// </summary>
    ValueTask<CapturedFrame?> AcquireNextFrameAsync(TimeSpan timeout, CancellationToken ct = default);

    Task StopCaptureAsync(CancellationToken ct = default);
}
