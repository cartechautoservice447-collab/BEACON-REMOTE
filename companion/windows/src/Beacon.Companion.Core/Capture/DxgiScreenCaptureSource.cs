using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Capture;

/// <summary>
/// Production DXGI Desktop Duplication engine implementing <see cref="IScreenCaptureSource"/>.
/// Manages output enumeration, monitor selection and runtime switching, bounded frame pooling,
/// guaranteed <c>ReleaseFrame</c> lifecycle, rotation and dirty-rectangle normalization,
/// cursor position and shape versioning, automatic recovery from <c>DXGI_ERROR_ACCESS_LOST</c>
/// and <c>DXGI_ERROR_DEVICE_REMOVED</c>/<c>RESET</c>, and fail-closed handling of Windows Secure Desktop.
/// </summary>
public sealed class DxgiScreenCaptureSource : IScreenCaptureSource, IDisposable
{
    private readonly IDxgiAdapterBackend _backend;
    private readonly DxgiScreenCaptureOptions _options;
    private readonly SessionStateMachine? _sessionStateMachine;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly TaskCompletionSource _allExecutionsDrainedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private CancellationTokenSource _activeCaptureCts = new();
    private IDxgiDuplicationSession? _activeSession;
    private DxgiFramePool? _framePool;
    private MonitorDescriptor? _activeMonitor;
    private string? _requestedMonitorKey;
    private bool _isCapturing;
    private bool _wasLockedOrProtected;
    private bool _hasPublishedInitialFrameInEpoch;

    private long _captureEpoch;
    private long _frameSequence;
    private long _lastPublishedQpcTicks;
    private long _cursorShapeVersion;
    private CursorSnapshot _latestCursor = new(false, 0, 0, 0);
    private CursorShapeSnapshot? _latestCursorShape;

    private long _totalFramesPublished;
    private long _totalCursorOnlyUpdates;
    private long _totalTimeouts;
    private long _totalAccessLostRecoveries;
    private long _totalDeviceResetRecoveries;
    private long _totalProtectedDesktopEvents;
    private long _totalPoolExhaustedDrops;
    private long _totalNotCurrentlyAvailableRetries;
    private DesktopSurfaceAvailability _surfaceAvailability = DesktopSurfaceAvailability.Unavailable;
    private DxgiCaptureStatus _lastStatus = DxgiCaptureStatus.Ok;
    private int _lastHResult = DxgiHResult.SOk;

    private int _activeExecutions;
    private int _disposeState; // 0 = active, 1 = disposing, 2 = disposed

    public DxgiScreenCaptureSource(
        IDxgiAdapterBackend backend,
        DxgiScreenCaptureOptions? options = null,
        SessionStateMachine? sessionStateMachine = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(backend);

        _options = options ?? new DxgiScreenCaptureOptions();
        _options.Validate();

        _backend = backend;
        _sessionStateMachine = sessionStateMachine;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsCapturing
    {
        get
        {
            lock (_stateLock)
            {
                return _isCapturing && Volatile.Read(ref _disposeState) == 0;
            }
        }
    }

    public MonitorDescriptor? ActiveMonitor
    {
        get
        {
            lock (_stateLock)
            {
                return _activeMonitor;
            }
        }
    }

    public DesktopSurfaceAvailability SurfaceAvailability
    {
        get
        {
            lock (_stateLock)
            {
                return _surfaceAvailability;
            }
        }
    }

    public CursorSnapshot LatestCursor
    {
        get
        {
            lock (_stateLock)
            {
                return _latestCursor;
            }
        }
    }

    public CursorShapeSnapshot? LatestCursorShape
    {
        get
        {
            lock (_stateLock)
            {
                return _latestCursorShape;
            }
        }
    }

    public long CaptureEpoch => Interlocked.Read(ref _captureEpoch);

    public DxgiCaptureDiagnostics GetDiagnostics()
    {
        lock (_stateLock)
        {
            var pool = _framePool;
            return new DxgiCaptureDiagnostics(
                CaptureEpoch: Interlocked.Read(ref _captureEpoch),
                TotalFramesPublished: Interlocked.Read(ref _totalFramesPublished),
                TotalCursorOnlyUpdates: Interlocked.Read(ref _totalCursorOnlyUpdates),
                TotalTimeouts: Interlocked.Read(ref _totalTimeouts),
                TotalAccessLostRecoveries: Interlocked.Read(ref _totalAccessLostRecoveries),
                TotalDeviceResetRecoveries: Interlocked.Read(ref _totalDeviceResetRecoveries),
                TotalProtectedDesktopEvents: Interlocked.Read(ref _totalProtectedDesktopEvents),
                TotalPoolExhaustedDrops: Interlocked.Read(ref _totalPoolExhaustedDrops),
                ActiveLeasedFrames: pool?.ActiveLeasedCount ?? 0,
                TotalPooledSlots: pool?.TotalAllocatedSlots ?? 0,
                SurfaceAvailability: _surfaceAvailability,
                LastStatus: _lastStatus,
                LastHResult: _lastHResult,
                TotalNotCurrentlyAvailableRetries: Interlocked.Read(ref _totalNotCurrentlyAvailableRetries));
        }
    }

    public async Task<IReadOnlyList<MonitorDescriptor>> GetMonitorsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(!TryEnterExecutionReservation(), this);
        try
        {
            ct.ThrowIfCancellationRequested();
            var rawMonitors = await _backend.EnumerateMonitorsAsync(ct).ConfigureAwait(false);
            return DxgiGeometryNormalizer.OrderMonitorsDeterministically(rawMonitors);
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public async Task StartCaptureAsync(string monitorId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(!TryEnterExecutionReservation(), this);
        try
        {
            if (string.IsNullOrWhiteSpace(monitorId))
            {
                throw new ArgumentException("A non-empty monitorId (or 'primary') is required.", nameof(monitorId));
            }

            ct.ThrowIfCancellationRequested();
            await _captureGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ct.ThrowIfCancellationRequested();

                var orderedMonitors = DxgiGeometryNormalizer.OrderMonitorsDeterministically(
                    await _backend.EnumerateMonitorsAsync(ct).ConfigureAwait(false));
                if (orderedMonitors.Count == 0)
                {
                    SetStatusLocked(
                        DesktopSurfaceAvailability.Unavailable,
                        DxgiCaptureStatus.MonitorNotFound,
                        DxgiHResult.DxgiErrorNotFound);
                    throw new DxgiCaptureException(
                        DxgiCaptureStatus.MonitorNotFound,
                        DxgiHResult.DxgiErrorNotFound,
                        "No active desktop monitors were found by DXGI output enumeration.");
                }

                var targetMonitor = DxgiGeometryNormalizer.ResolveMonitor(orderedMonitors, monitorId);
                if (targetMonitor is null)
                {
                    SetStatusLocked(
                        DesktopSurfaceAvailability.Unavailable,
                        DxgiCaptureStatus.MonitorNotFound,
                        DxgiHResult.DxgiErrorNotFound);
                    throw new DxgiCaptureException(
                        DxgiCaptureStatus.MonitorNotFound,
                        DxgiHResult.DxgiErrorNotFound,
                        $"Monitor '{monitorId}' was not found among {orderedMonitors.Count} active outputs.");
                }

                TeardownActiveSessionUnderGate(markNotCapturing: false);

                var newCts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _activeCaptureCts, newCts);
                oldCts.Dispose();

                var newEpoch = Interlocked.Increment(ref _captureEpoch);
                IDxgiDuplicationSession session;
                try
                {
                    session = await OpenDuplicationWithBoundedContentionRetryAsync(targetMonitor, ct).ConfigureAwait(false);
                }
                catch (DxgiCaptureException ex)
                {
                    HandleOpenSessionExceptionUnderGate(targetMonitor, ex);
                    throw;
                }

                var effectiveMonitor = SynchronizeMonitorWithSessionGeometry(targetMonitor, session);
                var pool = new DxgiFramePool(
                    newEpoch,
                    session.SurfaceSize,
                    session.PixelFormat,
                    session.BackingKind,
                    _options.MaxPooledFrames);

                lock (_stateLock)
                {
                    _activeSession = session;
                    _framePool = pool;
                    _activeMonitor = effectiveMonitor;
                    _requestedMonitorKey = monitorId.Trim();
                    _isCapturing = true;
                    _hasPublishedInitialFrameInEpoch = false;
                    _surfaceAvailability = DesktopSurfaceAvailability.Available;
                    _lastStatus = DxgiCaptureStatus.Ok;
                    _lastHResult = DxgiHResult.SOk;

                    if (_wasLockedOrProtected)
                    {
                        _wasLockedOrProtected = false;
                        _sessionStateMachine?.NotifyDesktopUnlocked();
                    }
                }
            }
            finally
            {
                _captureGate.Release();
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public async ValueTask<CapturedFrame?> AcquireNextFrameAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(!TryEnterExecutionReservation(), this);
        try
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
            ct.ThrowIfCancellationRequested();

            var clampedTimeout = timeout > _options.MaxAcquireTimeout
                ? _options.MaxAcquireTimeout
                : timeout;
            var timeoutMs = (int)Math.Clamp(Math.Ceiling(clampedTimeout.TotalMilliseconds), 0, int.MaxValue);

            CancellationToken stopToken;
            lock (_stateLock)
            {
                if (!_isCapturing)
                {
                    throw new InvalidOperationException("Screen capture has not been started. Call StartCaptureAsync first.");
                }

                stopToken = _activeCaptureCts.Token;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stopToken);
            var linkedCt = linkedCts.Token;

            await _captureGate.WaitAsync(linkedCt).ConfigureAwait(false);
            try
            {
                return await AcquireNextFrameUnderGateAsync(timeoutMs, ct, linkedCt).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && stopToken.IsCancellationRequested)
            {
                // Clean stop or disposal while waiting for frame acquisition returns null rather than faulting.
                return null;
            }
            finally
            {
                _captureGate.Release();
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public async Task StopCaptureAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(!TryEnterExecutionReservation(), this);
        try
        {
            ct.ThrowIfCancellationRequested();
            await _activeCaptureCts.CancelAsync().ConfigureAwait(false);

            await _captureGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                TeardownActiveSessionUnderGate(markNotCapturing: true);
            }
            finally
            {
                _captureGate.Release();
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
        {
            await _allExecutionsDrainedTcs.Task.ConfigureAwait(false);
            return;
        }

        if (Volatile.Read(ref _activeExecutions) == 0)
        {
            _allExecutionsDrainedTcs.TrySetResult();
        }

        try
        {
            await _activeCaptureCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        await _allExecutionsDrainedTcs.Task.ConfigureAwait(false);

        await _captureGate.WaitAsync().ConfigureAwait(false);
        try
        {
            TeardownActiveSessionUnderGate(markNotCapturing: true);
        }
        finally
        {
            _captureGate.Release();
        }

        try
        {
            await _backend.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _activeCaptureCts.Dispose();
            _captureGate.Dispose();
            Volatile.Write(ref _disposeState, 2);
        }
    }

    private async ValueTask<CapturedFrame?> AcquireNextFrameUnderGateAsync(
        int timeoutMs,
        CancellationToken callerCt,
        CancellationToken linkedCt)
    {
        var recoveryAttempt = 0;
        var notAvailableAttempt = 0;

        while (true)
        {
            linkedCt.ThrowIfCancellationRequested();

            IDxgiDuplicationSession? session;
            DxgiFramePool? pool;
            MonitorDescriptor? monitor;
            long epochAtStart;

            lock (_stateLock)
            {
                if (!_isCapturing)
                {
                    return null;
                }

                session = _activeSession;
                pool = _framePool;
                monitor = _activeMonitor;
                epochAtStart = _captureEpoch;
            }

            if (session is null || pool is null || monitor is null)
            {
                if (recoveryAttempt >= _options.MaxConsecutiveRecoveryAttempts)
                {
                    return null;
                }

                recoveryAttempt++;
                var reopened = await TryRecoverDuplicationUnderGateAsync(
                    DxgiCaptureStatus.AccessLost,
                    DxgiHResult.DxgiErrorAccessLost,
                    linkedCt).ConfigureAwait(false);
                if (!reopened)
                {
                    return null;
                }

                continue;
            }

            DxgiAcquireAttemptResult acquireResult;
            try
            {
                acquireResult = session.TryAcquireNextFrame(timeoutMs);
            }
            catch (DxgiCaptureException ex)
            {
                acquireResult = new DxgiAcquireAttemptResult(ex.Status, ex.HResult, null, ex.Message);
            }

            RecordStatusLocked(acquireResult.Status, acquireResult.HResult);

            if (acquireResult.Status == DxgiCaptureStatus.Ok && acquireResult.FrameData is not null)
            {
                try
                {
                    return ProcessAcquiredFrameUnderGate(
                        session,
                        pool,
                        monitor,
                        epochAtStart,
                        acquireResult.FrameData,
                        callerCt,
                        linkedCt);
                }
                finally
                {
                    // Guaranteed DXGI ReleaseFrame lifecycle on every successful AcquireNextFrame call.
                    session.ReleaseFrame();
                }
            }

            // Ensure ReleaseFrame is still invoked defensively if the session flagged a frame as acquired.
            if (session.IsFrameAcquired)
            {
                session.ReleaseFrame();
            }

            switch (acquireResult.Status)
            {
                case DxgiCaptureStatus.WaitTimeout:
                    Interlocked.Increment(ref _totalTimeouts);
                    return null;

                case DxgiCaptureStatus.AccessLost:
                case DxgiCaptureStatus.DeviceRemovedOrReset:
                case DxgiCaptureStatus.InvalidCall:
                    if (recoveryAttempt >= _options.MaxConsecutiveRecoveryAttempts)
                    {
                        return null;
                    }

                    recoveryAttempt++;
                    var recovered = await TryRecoverDuplicationUnderGateAsync(
                        acquireResult.Status,
                        acquireResult.HResult,
                        linkedCt).ConfigureAwait(false);
                    if (!recovered)
                    {
                        return null;
                    }

                    // Use a short bounded timeout on immediate post-recovery retry so we don't double-block.
                    timeoutMs = Math.Min(timeoutMs, 50);
                    continue;

                case DxgiCaptureStatus.AccessDeniedOrProtectedDesktop:
                case DxgiCaptureStatus.SessionDisconnected:
                    await HandleProtectedOrDisconnectedDesktopUnderGateAsync(
                        acquireResult.Status,
                        acquireResult.HResult,
                        linkedCt).ConfigureAwait(false);
                    return null;

                case DxgiCaptureStatus.MaxDuplicationClientsReached:
                    if (notAvailableAttempt < _options.MaxNotCurrentlyAvailableRetries)
                    {
                        notAvailableAttempt++;
                        var recoveredFromContention = await TryRecoverDuplicationUnderGateAsync(
                            acquireResult.Status,
                            acquireResult.HResult,
                            linkedCt).ConfigureAwait(false);
                        if (recoveredFromContention)
                        {
                            timeoutMs = Math.Min(timeoutMs, 50);
                            continue;
                        }

                        // If recovery paused cleanly for Protected/Locked desktop or SessionDisconnected, return null fail-closed.
                        DesktopSurfaceAvailability currentAvailability;
                        lock (_stateLock)
                        {
                            currentAvailability = _surfaceAvailability;
                        }

                        if (currentAvailability is DesktopSurfaceAvailability.ProtectedOrLockedDesktop
                            or DesktopSurfaceAvailability.SessionDisconnected)
                        {
                            return null;
                        }
                    }

                    SetStatusLocked(
                        DesktopSurfaceAvailability.Unavailable,
                        acquireResult.Status,
                        acquireResult.HResult);
                    throw new DxgiCaptureException(
                        acquireResult.Status,
                        acquireResult.HResult,
                        acquireResult.DiagnosticDetail ?? $"DXGI Desktop Duplication failed with non-recoverable status {acquireResult.Status} (0x{acquireResult.HResult:X8}).");

                case DxgiCaptureStatus.UnsupportedOrUnavailable:
                case DxgiCaptureStatus.MonitorNotFound:
                    SetStatusLocked(
                        DesktopSurfaceAvailability.Unavailable,
                        acquireResult.Status,
                        acquireResult.HResult);
                    throw new DxgiCaptureException(
                        acquireResult.Status,
                        acquireResult.HResult,
                        acquireResult.DiagnosticDetail ?? $"DXGI Desktop Duplication failed with non-recoverable status {acquireResult.Status} (0x{acquireResult.HResult:X8}).");

                default:
                    throw new DxgiCaptureException(
                        acquireResult.Status,
                        acquireResult.HResult,
                        acquireResult.DiagnosticDetail ?? $"DXGI Desktop Duplication failed with HRESULT 0x{acquireResult.HResult:X8}.");
            }
        }
    }

    private CapturedFrame? ProcessAcquiredFrameUnderGate(
        IDxgiDuplicationSession session,
        DxgiFramePool pool,
        MonitorDescriptor monitor,
        long expectedEpoch,
        DxgiAcquiredFrameData frameData,
        CancellationToken callerCt,
        CancellationToken linkedCt)
    {
        callerCt.ThrowIfCancellationRequested();
        linkedCt.ThrowIfCancellationRequested();

        bool emitInitialIdleFrame;
        lock (_stateLock)
        {
            emitInitialIdleFrame = !_hasPublishedInitialFrameInEpoch;
        }

        // Check if the frame itself reports a surface dimension or rotation change mid-session.
        var effectiveMonitor = monitor;
        if (frameData.SurfaceSize != pool.Size ||
            frameData.PixelFormat != pool.PixelFormat ||
            frameData.SurfaceRotation != monitor.Rotation)
        {
            effectiveMonitor = monitor with
            {
                PhysicalSize = frameData.SurfaceSize,
                Rotation = frameData.SurfaceRotation,
            };

            pool.RetireAll();
            expectedEpoch = Interlocked.Increment(ref _captureEpoch);
            pool = new DxgiFramePool(
                expectedEpoch,
                frameData.SurfaceSize,
                frameData.PixelFormat,
                frameData.BackingKind,
                _options.MaxPooledFrames);

            lock (_stateLock)
            {
                _activeMonitor = effectiveMonitor;
                _framePool = pool;
                _hasPublishedInitialFrameInEpoch = false;
                emitInitialIdleFrame = true;
            }
        }

        var cursor = UpdateCursorStateUnderGate(frameData.PointerUpdate, frameData.SurfaceSize);

        var isCursorOnlySignal = frameData.PointerUpdate.PositionUpdated ||
                                 frameData.PointerUpdate.NewShapeKind is not null;
        var treatIdleInitialAsDesktopFrame = emitInitialIdleFrame && !isCursorOnlySignal;
        var hasEffectiveDesktopUpdate = frameData.HasDesktopImageUpdate || treatIdleInitialAsDesktopFrame;

        if (!hasEffectiveDesktopUpdate)
        {
            Interlocked.Increment(ref _totalCursorOnlyUpdates);
            if (!_options.EmitCursorOnlyFrames)
            {
                return null;
            }
        }

        if (!pool.TryRentLease(out var slot, out var lease) || slot is null || lease is null)
        {
            Interlocked.Increment(ref _totalPoolExhaustedDrops);
            RecordStatusLocked(DxgiCaptureStatus.PoolExhausted, DxgiHResult.SOk);
            return null;
        }

        var leaseTransferredToFrame = false;
        try
        {
            session.CopyAcquiredFrameToSlot(frameData, slot);

            callerCt.ThrowIfCancellationRequested();
            linkedCt.ThrowIfCancellationRequested();

            lock (_stateLock)
            {
                if (!_isCapturing || _captureEpoch != expectedEpoch)
                {
                    // Stale epoch or stopped concurrently: discard frame and release lease immediately.
                    return null;
                }

                if (_wasLockedOrProtected)
                {
                    _wasLockedOrProtected = false;
                    _sessionStateMachine?.NotifyDesktopUnlocked();
                }

                _surfaceAvailability = DesktopSurfaceAvailability.Available;
                _hasPublishedInitialFrameInEpoch = true;
            }

            var rawDirtySpan = frameData.DirtyRects.Span;
            var rawMoveSpan = frameData.MoveRects.Span;

            PixelRect[]? rotatedDirty = null;
            DxgiMoveRect[]? rotatedMoves = null;

            if (frameData.RectsInUnrotatedSpace && effectiveMonitor.Rotation != DisplayRotation.Identity)
            {
                var unrotatedSize = DxgiGeometryNormalizer.GetUnrotatedSize(
                    frameData.SurfaceSize,
                    effectiveMonitor.Rotation);

                if (rawDirtySpan.Length > 0)
                {
                    rotatedDirty = new PixelRect[rawDirtySpan.Length];
                    for (var i = 0; i < rawDirtySpan.Length; i++)
                    {
                        rotatedDirty[i] = DxgiGeometryNormalizer.TransformRectForRotation(
                            rawDirtySpan[i],
                            unrotatedSize,
                            effectiveMonitor.Rotation);
                    }

                    rawDirtySpan = rotatedDirty;
                }

                if (rawMoveSpan.Length > 0)
                {
                    rotatedMoves = new DxgiMoveRect[rawMoveSpan.Length];
                    for (var i = 0; i < rawMoveSpan.Length; i++)
                    {
                        var transformedDest = DxgiGeometryNormalizer.TransformRectForRotation(
                            rawMoveSpan[i].DestinationRect,
                            unrotatedSize,
                            effectiveMonitor.Rotation);
                        rotatedMoves[i] = new DxgiMoveRect(rawMoveSpan[i].SourceX, rawMoveSpan[i].SourceY, transformedDest);
                    }

                    rawMoveSpan = rotatedMoves;
                }
            }

            // Clamp normalized dirty rects so every rect fits strictly within the leased oriented frame's Size.
            var normalizedDirty = DxgiGeometryNormalizer.NormalizeDirtyRegions(
                rawDirtySpan,
                rawMoveSpan,
                frameData.SurfaceSize,
                hasEffectiveDesktopUpdate || _options.EmitCursorOnlyFrames);

            var rawQpc = frameData.PresentTimestampQpcTicks > 0
                ? frameData.PresentTimestampQpcTicks
                : _backend.GetTimestampQpcTicks();
            var monotonicQpc = Math.Max(Interlocked.Read(ref _lastPublishedQpcTicks), Math.Max(1L, rawQpc));
            Interlocked.Exchange(ref _lastPublishedQpcTicks, monotonicQpc);

            var sequence = Interlocked.Increment(ref _frameSequence);
            Interlocked.Increment(ref _totalFramesPublished);

            var capturedFrame = new CapturedFrame(
                frameSequence: sequence,
                timestampQpcTicks: monotonicQpc,
                monitor: effectiveMonitor,
                dirtyRects: normalizedDirty,
                cursor: cursor,
                lease: lease);
            leaseTransferredToFrame = true;
            return capturedFrame;
        }
        finally
        {
            if (!leaseTransferredToFrame)
            {
                lease.Dispose();
            }
        }
    }

    private CursorSnapshot UpdateCursorStateUnderGate(
        DxgiRawPointerUpdate pointerUpdate,
        PixelSize surfaceSize)
    {
        lock (_stateLock)
        {
            var shapeVersion = _cursorShapeVersion;

            if (pointerUpdate.NewShapeKind is { } shapeKind &&
                !pointerUpdate.NewShapeBytes.IsEmpty &&
                !pointerUpdate.NewShapeSize.IsEmpty)
            {
                shapeVersion = ++_cursorShapeVersion;
                var copiedShapeBytes = pointerUpdate.NewShapeBytes.ToArray();
                _latestCursorShape = new CursorShapeSnapshot(
                    ShapeVersion: shapeVersion,
                    Kind: shapeKind,
                    Size: pointerUpdate.NewShapeSize,
                    PitchBytes: pointerUpdate.NewShapePitchBytes,
                    HotSpotX: pointerUpdate.HotSpotX,
                    HotSpotY: pointerUpdate.HotSpotY,
                    PixelData: copiedShapeBytes);
            }

            if (pointerUpdate.PositionUpdated)
            {
                var (clampedX, clampedY) = pointerUpdate.IsVisible
                    ? DxgiGeometryNormalizer.ClampCursorCoordinates(pointerUpdate.X, pointerUpdate.Y, surfaceSize)
                    : (pointerUpdate.X, pointerUpdate.Y);

                _latestCursor = new CursorSnapshot(
                    IsVisible: pointerUpdate.IsVisible,
                    X: clampedX,
                    Y: clampedY,
                    ShapeVersion: shapeVersion);
            }
            else if (shapeVersion != _latestCursor.ShapeVersion)
            {
                _latestCursor = _latestCursor with { ShapeVersion = shapeVersion };
            }

            return _latestCursor;
        }
    }

    private async ValueTask<bool> TryRecoverDuplicationUnderGateAsync(
        DxgiCaptureStatus triggerStatus,
        int triggerHResult,
        CancellationToken ct)
    {
        string? requestedKey;
        MonitorDescriptor? fallbackMonitor;

        lock (_stateLock)
        {
            if (!_isCapturing)
            {
                return false;
            }

            _surfaceAvailability = DesktopSurfaceAvailability.DeviceRecovering;
            _lastStatus = triggerStatus;
            _lastHResult = triggerHResult;
            requestedKey = _requestedMonitorKey;
            fallbackMonitor = _activeMonitor;
        }

        DisposeActiveSessionOnlyUnderGate();

        if (triggerStatus == DxgiCaptureStatus.DeviceRemovedOrReset)
        {
            Interlocked.Increment(ref _totalDeviceResetRecoveries);
            if (_options.DeviceResetRetryDelay > TimeSpan.Zero)
            {
                await DelayWithTimeProviderAsync(_options.DeviceResetRetryDelay, ct).ConfigureAwait(false);
            }

            await _backend.ResetAdapterResourcesAsync(ct).ConfigureAwait(false);
        }
        else if (triggerStatus == DxgiCaptureStatus.MaxDuplicationClientsReached)
        {
            Interlocked.Increment(ref _totalNotCurrentlyAvailableRetries);
            if (_options.NotCurrentlyAvailableRetryDelay > TimeSpan.Zero)
            {
                await DelayWithTimeProviderAsync(_options.NotCurrentlyAvailableRetryDelay, ct).ConfigureAwait(false);
            }
        }
        else
        {
            Interlocked.Increment(ref _totalAccessLostRecoveries);
            if (_options.AccessLostRetryDelay > TimeSpan.Zero)
            {
                await DelayWithTimeProviderAsync(_options.AccessLostRetryDelay, ct).ConfigureAwait(false);
            }
        }

        var monitors = DxgiGeometryNormalizer.OrderMonitorsDeterministically(
            await _backend.EnumerateMonitorsAsync(ct).ConfigureAwait(false));
        var resolvedMonitor = requestedKey is not null
            ? DxgiGeometryNormalizer.ResolveMonitor(monitors, requestedKey)
            : null;
        resolvedMonitor ??= fallbackMonitor is not null
            ? DxgiGeometryNormalizer.ResolveMonitor(monitors, fallbackMonitor.MonitorId)
            : null;

        if (resolvedMonitor is null)
        {
            SetStatusLocked(
                DesktopSurfaceAvailability.Unavailable,
                DxgiCaptureStatus.MonitorNotFound,
                DxgiHResult.DxgiErrorNotFound);
            return false;
        }

        IDxgiDuplicationSession newSession;
        try
        {
            newSession = await OpenDuplicationWithBoundedContentionRetryAsync(resolvedMonitor, ct).ConfigureAwait(false);
        }
        catch (DxgiCaptureException ex) when (
            ex.Status is DxgiCaptureStatus.AccessDeniedOrProtectedDesktop
                or DxgiCaptureStatus.SessionDisconnected)
        {
            await HandleProtectedOrDisconnectedDesktopUnderGateAsync(ex.Status, ex.HResult, ct).ConfigureAwait(false);
            return false;
        }
        catch (DxgiCaptureException ex) when (
            DxgiHResultClassifier.IsTransientRecoverable(ex.Status) ||
            DxgiHResultClassifier.IsBoundedAvailabilityRetry(ex.Status))
        {
            RecordStatusLocked(ex.Status, ex.HResult);
            return false;
        }

        var effectiveMonitor = SynchronizeMonitorWithSessionGeometry(resolvedMonitor, newSession);

        lock (_stateLock)
        {
            if (!_isCapturing)
            {
                newSession.Dispose();
                return false;
            }

            var currentPool = _framePool;
            var needsNewPool = currentPool is null ||
                               currentPool.IsRetired ||
                               currentPool.Size != newSession.SurfaceSize ||
                               currentPool.PixelFormat != newSession.PixelFormat ||
                               currentPool.DefaultBackingKind != newSession.BackingKind ||
                               triggerStatus == DxgiCaptureStatus.DeviceRemovedOrReset;

            if (needsNewPool)
            {
                currentPool?.RetireAll();
                var nextEpoch = Interlocked.Increment(ref _captureEpoch);
                _framePool = new DxgiFramePool(
                    nextEpoch,
                    newSession.SurfaceSize,
                    newSession.PixelFormat,
                    newSession.BackingKind,
                    _options.MaxPooledFrames);
                _hasPublishedInitialFrameInEpoch = false;
            }

            _activeSession = newSession;
            _activeMonitor = effectiveMonitor;
            _surfaceAvailability = DesktopSurfaceAvailability.Available;
            _lastStatus = DxgiCaptureStatus.Ok;
            _lastHResult = DxgiHResult.SOk;

            if (_wasLockedOrProtected)
            {
                _wasLockedOrProtected = false;
                _sessionStateMachine?.NotifyDesktopUnlocked();
            }
        }

        return true;
    }

    private async Task<IDxgiDuplicationSession> OpenDuplicationWithBoundedContentionRetryAsync(
        MonitorDescriptor monitor,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await _backend.OpenDuplicationSessionAsync(monitor, ct).ConfigureAwait(false);
            }
            catch (DxgiCaptureException ex) when (
                DxgiHResultClassifier.IsBoundedAvailabilityRetry(ex.Status) &&
                attempt < _options.MaxNotCurrentlyAvailableRetries)
            {
                attempt++;
                Interlocked.Increment(ref _totalNotCurrentlyAvailableRetries);
                RecordStatusLocked(ex.Status, ex.HResult);

                if (_options.NotCurrentlyAvailableRetryDelay > TimeSpan.Zero)
                {
                    await DelayWithTimeProviderAsync(_options.NotCurrentlyAvailableRetryDelay, ct).ConfigureAwait(false);
                }
            }
        }
    }

    private async ValueTask HandleProtectedOrDisconnectedDesktopUnderGateAsync(
        DxgiCaptureStatus status,
        int hResult,
        CancellationToken ct)
    {
        Interlocked.Increment(ref _totalProtectedDesktopEvents);
        DisposeActiveSessionOnlyUnderGate();

        lock (_stateLock)
        {
            _surfaceAvailability = status == DxgiCaptureStatus.SessionDisconnected
                ? DesktopSurfaceAvailability.SessionDisconnected
                : DesktopSurfaceAvailability.ProtectedOrLockedDesktop;
            _lastStatus = status;
            _lastHResult = hResult;

            if (!_wasLockedOrProtected)
            {
                _wasLockedOrProtected = true;
                _ = _sessionStateMachine?.NotifyDesktopLockedOrUnavailable();
            }
        }

        if (_options.ProtectedDesktopBackoffDelay > TimeSpan.Zero)
        {
            await DelayWithTimeProviderAsync(_options.ProtectedDesktopBackoffDelay, ct).ConfigureAwait(false);
        }
    }

    private void HandleOpenSessionExceptionUnderGate(
        MonitorDescriptor targetMonitor,
        DxgiCaptureException ex)
    {
        lock (_stateLock)
        {
            _lastStatus = ex.Status;
            _lastHResult = ex.HResult;

            if (ex.Status is DxgiCaptureStatus.AccessDeniedOrProtectedDesktop
                or DxgiCaptureStatus.SessionDisconnected)
            {
                Interlocked.Increment(ref _totalProtectedDesktopEvents);
                _activeMonitor = targetMonitor;
                _requestedMonitorKey = targetMonitor.MonitorId;
                _isCapturing = true;
                _surfaceAvailability = ex.Status == DxgiCaptureStatus.SessionDisconnected
                    ? DesktopSurfaceAvailability.SessionDisconnected
                    : DesktopSurfaceAvailability.ProtectedOrLockedDesktop;

                if (!_wasLockedOrProtected)
                {
                    _wasLockedOrProtected = true;
                    _ = _sessionStateMachine?.NotifyDesktopLockedOrUnavailable();
                }
            }
            else
            {
                _isCapturing = false;
                _surfaceAvailability = DesktopSurfaceAvailability.Unavailable;
            }
        }
    }

    private static MonitorDescriptor SynchronizeMonitorWithSessionGeometry(
        MonitorDescriptor monitor,
        IDxgiDuplicationSession session)
    {
        if (monitor.PhysicalSize == session.SurfaceSize &&
            monitor.Rotation == session.SurfaceRotation)
        {
            return monitor;
        }

        return monitor with
        {
            PhysicalSize = session.SurfaceSize,
            Rotation = session.SurfaceRotation,
        };
    }

    private void TeardownActiveSessionUnderGate(bool markNotCapturing)
    {
        IDxgiDuplicationSession? sessionToDispose;
        DxgiFramePool? poolToRetire;

        lock (_stateLock)
        {
            sessionToDispose = _activeSession;
            _activeSession = null;

            poolToRetire = _framePool;
            _framePool = null;

            if (markNotCapturing)
            {
                _isCapturing = false;
                _activeMonitor = null;
                _requestedMonitorKey = null;
                _surfaceAvailability = DesktopSurfaceAvailability.Unavailable;
                Interlocked.Increment(ref _captureEpoch);
            }
        }

        if (sessionToDispose is not null)
        {
            try
            {
                if (sessionToDispose.IsFrameAcquired)
                {
                    sessionToDispose.ReleaseFrame();
                }
            }
            finally
            {
                sessionToDispose.Dispose();
            }
        }

        poolToRetire?.RetireAll();
    }

    private void DisposeActiveSessionOnlyUnderGate()
    {
        IDxgiDuplicationSession? sessionToDispose;
        lock (_stateLock)
        {
            sessionToDispose = _activeSession;
            _activeSession = null;
        }

        if (sessionToDispose is not null)
        {
            try
            {
                if (sessionToDispose.IsFrameAcquired)
                {
                    sessionToDispose.ReleaseFrame();
                }
            }
            finally
            {
                sessionToDispose.Dispose();
            }
        }
    }

    private void RecordStatusLocked(DxgiCaptureStatus status, int hResult)
    {
        lock (_stateLock)
        {
            _lastStatus = status;
            _lastHResult = hResult;
        }
    }

    private void SetStatusLocked(
        DesktopSurfaceAvailability availability,
        DxgiCaptureStatus status,
        int hResult)
    {
        lock (_stateLock)
        {
            _surfaceAvailability = availability;
            _lastStatus = status;
            _lastHResult = hResult;
        }
    }

    private Task DelayWithTimeProviderAsync(TimeSpan delay, CancellationToken ct) =>
        Task.Delay(delay, _timeProvider, ct);

    private bool TryEnterExecutionReservation()
    {
        while (true)
        {
            if (Volatile.Read(ref _disposeState) != 0)
            {
                return false;
            }

            Interlocked.Increment(ref _activeExecutions);
            if (Volatile.Read(ref _disposeState) == 0)
            {
                return true;
            }

            ExitExecutionReservation();
            return false;
        }
    }

    private void ExitExecutionReservation()
    {
        if (Interlocked.Decrement(ref _activeExecutions) == 0 &&
            Volatile.Read(ref _disposeState) != 0)
        {
            _allExecutionsDrainedTcs.TrySetResult();
        }
    }
}
