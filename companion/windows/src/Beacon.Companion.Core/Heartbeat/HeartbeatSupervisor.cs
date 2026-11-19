using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Input;
using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Heartbeat;

public interface IHeartbeatSupervisor : IAsyncDisposable, IDisposable
{
    HeartbeatSnapshot Snapshot { get; }

    event EventHandler<HeartbeatSnapshot>? SnapshotChanged;

    event EventHandler<DeviceRevokedEventArgs>? RevocationConfirmed;

    Task StartAsync(CancellationToken ct = default);

    Task StopAsync(CancellationToken ct = default);

    Task<HeartbeatAttemptResult> TriggerHeartbeatNowAsync(CancellationToken ct = default);

    Task AcknowledgeFreshPairingAsync(bool startSupervisorLoop = false, CancellationToken ct = default);

    Task NotifyAuthorizationRevokedAsync(
        Guid? expectedDeviceId = null,
        string? reason = null,
        CancellationToken ct = default);

    Guid EnsureDeviceAuthorized();

    bool TryGetAuthorizedDeviceId(out Guid deviceId);
}

/// <summary>
/// Coordinates periodic device heartbeats, server-verified operational authorization (<see cref="DeviceAuthorizationStatus.PendingVerification"/> -> <see cref="DeviceAuthorizationStatus.Authorized"/>),
/// bounded exponential backoff with jitter, single-loop execution safety, stale-response epoch fencing,
/// active session termination, unconditional input release, durable revocation tombstone persistence,
/// atomic conditional credential deletion, and race-free shutdown/disposal.
/// </summary>
public sealed class HeartbeatSupervisor : IHeartbeatSupervisor
{
    private readonly ICredentialStore _credentialStore;
    private readonly IHeartbeatClient _heartbeatClient;
    private readonly IRevocationTombstoneStore _tombstoneStore;
    private readonly HeartbeatSupervisorOptions _options;
    private readonly SessionStateMachine? _sessionStateMachine;
    private readonly IInputExecutor? _inputExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly IJitterSource _jitterSource;

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _heartbeatExecutionLock = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly TaskCompletionSource _operationsDrainedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<Guid> _revokedDeviceIds = [];

    private DeviceAuthorizationStatus _authorizationStatus = DeviceAuthorizationStatus.Unpaired;
    private HeartbeatConnectivityState _connectivityState = HeartbeatConnectivityState.Idle;
    private Guid? _boundDeviceId;
    private Uri? _boundServerOrigin;
    private long _epoch = 1;
    private long _nextSequence;
    private long _latestAppliedSequence;
    private int _consecutiveTransientFailures;
    private long _totalSucceededCount;
    private long _totalTransientFailureCount;
    private DateTimeOffset? _lastSuccessAtUtc;
    private DateTimeOffset? _lastServerTimeUtc;
    private DateTimeOffset? _lastAttemptAtUtc;
    private DateTimeOffset? _revokedAtUtc;
    private HeartbeatOutcomeKind? _lastOutcome;
    private string? _lastErrorDetail;
    private bool? _lastCredentialPurgeSucceeded;
    private bool? _lastTombstonePersisted;
    private bool? _lastInputResetSucceeded;
    private string? _lastCleanupErrorDetail;
    private DeviceRevokedEventArgs? _lastRevocationDetails;

    private CancellationTokenSource? _runningCts;
    private Task? _loopTask;
    private int _activeExecutionReservations;
    private int _executionResourcesReleased;
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="HeartbeatSupervisor"/> with an explicitly required
    /// <see cref="IRevocationTombstoneStore"/> so production hosts can never silently fall back
    /// to an in-memory tombstone store (ARGON NEW-001).
    /// Windows hosts should use <c>WindowsHeartbeatSupervisorFactory.Create</c> to wire
    /// <c>DpapiRevocationTombstoneStore</c>.
    /// </summary>
    public HeartbeatSupervisor(
        ICredentialStore credentialStore,
        IHeartbeatClient heartbeatClient,
        IRevocationTombstoneStore tombstoneStore,
        HeartbeatSupervisorOptions? options = null,
        SessionStateMachine? sessionStateMachine = null,
        IInputExecutor? inputExecutor = null,
        TimeProvider? timeProvider = null,
        IJitterSource? jitterSource = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(heartbeatClient);
        ArgumentNullException.ThrowIfNull(tombstoneStore);

        _options = options ?? new HeartbeatSupervisorOptions();
        _options.Validate();

        _credentialStore = credentialStore;
        _heartbeatClient = heartbeatClient;
        _tombstoneStore = tombstoneStore;
        _sessionStateMachine = sessionStateMachine;
        _inputExecutor = inputExecutor;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _jitterSource = jitterSource ?? CryptoJitterSource.Shared;
    }

    public event EventHandler<HeartbeatSnapshot>? SnapshotChanged;

    public event EventHandler<DeviceRevokedEventArgs>? RevocationConfirmed;

    public HeartbeatSnapshot Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return BuildSnapshotUnsafe();
            }
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        if (!TryEnterExecutionReservation())
        {
            throw new ObjectDisposedException(nameof(HeartbeatSupervisor));
        }

        try
        {
            while (true)
            {
                Task? cancellingLoopToWait = null;
                CancellationTokenSource? cancellingCts = null;
                HeartbeatSnapshot? snapshot = null;

                lock (_stateLock)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                    if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
                    {
                        throw new DeviceRevokedException(
                            "Cannot start heartbeat supervisor because the device has been revoked. Pair the device again before restarting.",
                            _lastRevocationDetails ?? new DeviceRevokedEventArgs(
                                _boundDeviceId,
                                _revokedAtUtc ?? _timeProvider.GetUtcNow(),
                                "unauthorized",
                                "Device has been revoked."));
                    }

                    // ARGON-3.4-007: Distinguish an active healthy loop from a loop whose CancellationTokenSource
                    // has already been cancelled and is in the middle of exiting.
                    if (_loopTask is { IsCompleted: false })
                    {
                        if (_runningCts is { IsCancellationRequested: false })
                        {
                            return;
                        }

                        cancellingLoopToWait = _loopTask;
                        cancellingCts = _runningCts;
                    }
                    else
                    {
                        _runningCts?.Dispose();
                        _runningCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                        _connectivityState = HeartbeatConnectivityState.Starting;

                        var loopToken = _runningCts.Token;
                        _loopTask = Task.Run(() => RunLoopAsync(loopToken), CancellationToken.None);
                        snapshot = BuildSnapshotUnsafe();
                    }
                }

                if (cancellingLoopToWait is not null)
                {
                    try
                    {
                        await cancellingLoopToWait.WaitAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Previous cancelled loop completed shutdown.
                    }

                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_runningCts, cancellingCts) &&
                            (_loopTask is null || _loopTask.IsCompleted))
                        {
                            _runningCts?.Dispose();
                            _runningCts = null;
                            _loopTask = null;
                        }
                    }

                    continue;
                }

                if (snapshot is not null)
                {
                    SnapshotChanged?.Invoke(this, snapshot);
                }

                return;
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        CancellationTokenSource? ctsToCancel;
        Task? loopToWait;

        lock (_stateLock)
        {
            ctsToCancel = _runningCts;
            loopToWait = _loopTask;
        }

        if (ctsToCancel is not null)
        {
            try
            {
                await ctsToCancel.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed concurrently.
            }
        }

        if (loopToWait is not null)
        {
            try
            {
                await loopToWait.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (loopToWait.IsCanceled || ct.IsCancellationRequested)
            {
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        HeartbeatSnapshot? snapshot = null;
        lock (_stateLock)
        {
            if (ReferenceEquals(_runningCts, ctsToCancel))
            {
                _runningCts?.Dispose();
                _runningCts = null;
                _loopTask = null;
            }

            if (_authorizationStatus != DeviceAuthorizationStatus.Revoked &&
                _connectivityState != HeartbeatConnectivityState.Stopped)
            {
                _connectivityState = HeartbeatConnectivityState.Stopped;
                snapshot = BuildSnapshotUnsafe();
            }
        }

        if (snapshot is not null)
        {
            SnapshotChanged?.Invoke(this, snapshot);
        }
    }

    public async Task<HeartbeatAttemptResult> TriggerHeartbeatNowAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        lock (_stateLock)
        {
            if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Revoked,
                    "Device authorization is revoked; heartbeat rejected without contacting server.",
                    deviceId: _boundDeviceId,
                    httpStatusCode: 401,
                    serverErrorCode: _lastRevocationDetails?.ServerErrorCode ?? "unauthorized");
            }
        }

        // ARGON-3.4-006 & NEW-004: Reserve active execution slot and link to supervisor _lifetimeCts
        // so concurrent Dispose/DisposeAsync cancels in-flight work and never disposes
        // _heartbeatExecutionLock or _lifetimeCts while a thread is waiting on or releasing them.
        if (!TryEnterExecutionReservation())
        {
            throw new ObjectDisposedException(nameof(HeartbeatSupervisor));
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            var effectiveToken = linkedCts.Token;

            var lockAcquired = false;
            try
            {
                await _heartbeatExecutionLock.WaitAsync(effectiveToken).ConfigureAwait(false);
                lockAcquired = true;

                if (Volatile.Read(ref _disposed) != 0)
                {
                    return HeartbeatAttemptResult.Failure(
                        HeartbeatOutcomeKind.Cancelled,
                        "Heartbeat supervisor was disposed while heartbeat was starting.",
                        deviceId: _boundDeviceId);
                }

                return await ExecuteSingleHeartbeatAsync(effectiveToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref _disposed) != 0 && !ct.IsCancellationRequested)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Cancelled,
                    "Heartbeat was cancelled because the supervisor is shutting down.",
                    deviceId: _boundDeviceId);
            }
            finally
            {
                if (lockAcquired)
                {
                    _ = _heartbeatExecutionLock.Release();
                }
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public async Task AcknowledgeFreshPairingAsync(
        bool startSupervisorLoop = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        // ARGON NEW-004: Enroll AcknowledgeFreshPairingAsync in supervisor lifetime reservation.
        if (!TryEnterExecutionReservation())
        {
            throw new ObjectDisposedException(nameof(HeartbeatSupervisor));
        }

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeCts.Token);
            var effectiveToken = linkedCts.Token;

            DeviceCredential? loadedCredential;
            try
            {
                loadedCredential = await _credentialStore.LoadAsync(effectiveToken).ConfigureAwait(false);
            }
            catch (CredentialStoreCorruptedException ex)
            {
                throw new DeviceNotAuthorizedException(
                    "Stored credential could not be decrypted after fresh pairing acknowledgment.",
                    ex);
            }

            if (loadedCredential is null)
            {
                throw new DeviceNotAuthorizedException(
                    "Cannot acknowledge fresh pairing because no credential is stored.",
                    DeviceAuthorizationStatus.Unpaired);
            }

            HeartbeatSnapshot snapshot;
            using (loadedCredential)
            {
                if (loadedCredential.IsDisposed || !DeviceCredential.IsValidTokenFormat(loadedCredential.Token))
                {
                    throw new DeviceNotAuthorizedException(
                        "Stored device credential is invalid.",
                        DeviceAuthorizationStatus.CredentialInvalid);
                }

                var newDeviceId = loadedCredential.Metadata.DeviceId;
                var originValidation = ServerOriginValidator.Validate(
                    loadedCredential.Metadata.ServerOrigin,
                    _options.AllowDevelopmentLoopbackHttp);
                if (!originValidation.IsValid || originValidation.CanonicalOrigin is null)
                {
                    throw new DeviceNotAuthorizedException(
                        originValidation.ErrorDetail ?? "Stored ServerOrigin is invalid.",
                        DeviceAuthorizationStatus.CredentialInvalid);
                }

                // ARGON-3.4-002: Consult durable revocation tombstone store before accepting any DeviceId.
                // Fail closed if the tombstone ledger is corrupted or unreadable.
                bool durablyRevoked;
                try
                {
                    durablyRevoked = await _tombstoneStore.IsRevokedAsync(newDeviceId, effectiveToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new DeviceNotAuthorizedException(
                        "Unable to verify device revocation tombstone ledger; failing closed.",
                        ex);
                }

                CancellationTokenSource? ctsToCancel;
                Task? priorLoopTask;
                lock (_stateLock)
                {
                    ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                    if (durablyRevoked || _revokedDeviceIds.Contains(newDeviceId))
                    {
                        _ = _revokedDeviceIds.Add(newDeviceId);
                        throw new DeviceRevokedException(
                            "Cannot restore authorization using a previously revoked DeviceId. Complete a fresh pairing.",
                            _lastRevocationDetails ?? new DeviceRevokedEventArgs(
                                newDeviceId,
                                _timeProvider.GetUtcNow(),
                                "unauthorized",
                                "Previously revoked device identity rejected."));
                    }

                    // ARGON-3.4-001 & NEW-005: Immediately fence the previous epoch and move to
                    // PendingVerification (NOT Authorized) before terminating any previous active session.
                    _epoch++;
                    _authorizationStatus = DeviceAuthorizationStatus.PendingVerification;
                    _connectivityState = HeartbeatConnectivityState.Idle;
                    _boundDeviceId = newDeviceId;
                    _boundServerOrigin = originValidation.CanonicalOrigin;
                    _consecutiveTransientFailures = 0;
                    _revokedAtUtc = null;
                    _lastErrorDetail = null;
                    _lastOutcome = null;
                    _lastCredentialPurgeSucceeded = null;
                    _lastTombstonePersisted = null;
                    _lastInputResetSucceeded = null;
                    _lastCleanupErrorDetail = null;
                    _lastRevocationDetails = null;
                    ctsToCancel = _runningCts;
                    priorLoopTask = _loopTask;
                }

                if (ctsToCancel is not null)
                {
                    try
                    {
                        await ctsToCancel.CancelAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed.
                    }
                }

                if (priorLoopTask is not null)
                {
                    try
                    {
                        await priorLoopTask.WaitAsync(effectiveToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!effectiveToken.IsCancellationRequested)
                    {
                        // Previous loop shut down cleanly on cancellation.
                    }

                    lock (_stateLock)
                    {
                        if (ReferenceEquals(_runningCts, ctsToCancel))
                        {
                            _runningCts?.Dispose();
                            _runningCts = null;
                            _loopTask = null;
                        }
                    }
                }

                // ARGON NEW-005: Always terminate any active or pending session from the previous device
                // and unconditionally reset held inputs before publishing the new device's PendingVerification state.
                // Cleanup exceptions are recorded without restoring previous authorization.
                var (inputResetSucceeded, cleanupError) = await TerminateActiveSessionAndResetInputsAsync(effectiveToken).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _lastInputResetSucceeded = inputResetSucceeded;
                    _lastCleanupErrorDetail = cleanupError;
                    snapshot = BuildSnapshotUnsafe();
                }
            }

            SnapshotChanged?.Invoke(this, snapshot);

            if (startSupervisorLoop)
            {
                await StartAsync(effectiveToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public async Task NotifyAuthorizationRevokedAsync(
        Guid? expectedDeviceId = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        // ARGON NEW-004: Enroll NotifyAuthorizationRevokedAsync in supervisor lifetime reservation.
        if (!TryEnterExecutionReservation())
        {
            throw new ObjectDisposedException(nameof(HeartbeatSupervisor));
        }

        try
        {
            var now = _timeProvider.GetUtcNow();
            var effectiveReason = string.IsNullOrWhiteSpace(reason)
                ? "Device authorization was revoked."
                : reason.Trim();

            Guid? targetDeviceId = null;
            CancellationTokenSource? ctsToCancel = null;
            bool shouldPerformSideEffects = false;
            Guid? staleRevokedIdToPersistOnly = null;

            lock (_stateLock)
            {
                if (expectedDeviceId.HasValue)
                {
                    _ = _revokedDeviceIds.Add(expectedDeviceId.Value);
                    if (_boundDeviceId.HasValue && _boundDeviceId.Value != expectedDeviceId.Value)
                    {
                        staleRevokedIdToPersistOnly = expectedDeviceId.Value;
                    }
                    else
                    {
                        targetDeviceId = expectedDeviceId.Value;
                    }
                }
                else if (_boundDeviceId.HasValue)
                {
                    targetDeviceId = _boundDeviceId.Value;
                    _ = _revokedDeviceIds.Add(_boundDeviceId.Value);
                }

                if (staleRevokedIdToPersistOnly.HasValue)
                {
                    shouldPerformSideEffects = false;
                }
                else if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
                {
                    return;
                }
                else
                {
                    _epoch++;
                    _authorizationStatus = DeviceAuthorizationStatus.Revoked;
                    _connectivityState = HeartbeatConnectivityState.Revoked;
                    _revokedAtUtc = now;
                    _lastOutcome = HeartbeatOutcomeKind.Revoked;
                    _lastErrorDetail = effectiveReason;
                    _lastRevocationDetails = new DeviceRevokedEventArgs(targetDeviceId, now, "revoked", effectiveReason);
                    ctsToCancel = _runningCts;
                    shouldPerformSideEffects = true;
                }
            }

            if (staleRevokedIdToPersistOnly.HasValue)
            {
                _ = await TryPersistTombstoneWithRetryAsync(staleRevokedIdToPersistOnly.Value, now).ConfigureAwait(false);
                return;
            }

            if (ctsToCancel is not null)
            {
                try
                {
                    await ctsToCancel.CancelAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    // Ignore if already disposed.
                }
            }

            if (shouldPerformSideEffects)
            {
                var cleanup = await ExecuteRevocationSideEffectsAsync(targetDeviceId, now, ct).ConfigureAwait(false);
                DeviceRevokedEventArgs revokedArgs;
                HeartbeatSnapshot snapshot;

                lock (_stateLock)
                {
                    _lastCredentialPurgeSucceeded = cleanup.CredentialPurgeSucceeded;
                    _lastTombstonePersisted = cleanup.TombstonePersisted;
                    _lastInputResetSucceeded = cleanup.InputResetSucceeded;
                    _lastCleanupErrorDetail = cleanup.CleanupErrorDetail;
                    revokedArgs = new DeviceRevokedEventArgs(
                        targetDeviceId,
                        now,
                        "revoked",
                        effectiveReason,
                        cleanup.CredentialPurgeSucceeded,
                        cleanup.TombstonePersisted,
                        cleanup.InputResetSucceeded,
                        cleanup.CleanupErrorDetail);
                    _lastRevocationDetails = revokedArgs;
                    snapshot = BuildSnapshotUnsafe();
                }

                if (Volatile.Read(ref _disposed) == 0)
                {
                    RevocationConfirmed?.Invoke(this, revokedArgs);
                    SnapshotChanged?.Invoke(this, snapshot);
                }
            }
        }
        finally
        {
            ExitExecutionReservation();
        }
    }

    public Guid EnsureDeviceAuthorized()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        lock (_stateLock)
        {
            if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
            {
                throw new DeviceRevokedException(
                    "Operation rejected because the device has been revoked. Pair again to restore access.",
                    _lastRevocationDetails ?? new DeviceRevokedEventArgs(
                        _boundDeviceId,
                        _revokedAtUtc ?? _timeProvider.GetUtcNow(),
                        "unauthorized",
                        "Device has been revoked."));
            }

            if (_authorizationStatus == DeviceAuthorizationStatus.PendingVerification)
            {
                throw new DeviceNotAuthorizedException(
                    "Operation rejected because the device credential is pending server heartbeat verification.",
                    DeviceAuthorizationStatus.PendingVerification);
            }

            if (_authorizationStatus != DeviceAuthorizationStatus.Authorized || _boundDeviceId is not { } deviceId)
            {
                throw new DeviceNotAuthorizedException(
                    "Operation rejected because no server-verified authorized device credential is active.",
                    _authorizationStatus);
            }

            return deviceId;
        }
    }

    public bool TryGetAuthorizedDeviceId(out Guid deviceId)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            deviceId = Guid.Empty;
            return false;
        }

        lock (_stateLock)
        {
            if (_authorizationStatus == DeviceAuthorizationStatus.Authorized && _boundDeviceId is { } id)
            {
                deviceId = id;
                return true;
            }

            deviceId = Guid.Empty;
            return false;
        }
    }

    public TimeSpan ComputeNextDelay(HeartbeatAttemptResult lastResult, int consecutiveTransientFailures)
    {
        ArgumentNullException.ThrowIfNull(lastResult);

        if (lastResult.Succeeded)
        {
            return _options.HeartbeatInterval;
        }

        var exponent = Math.Max(0, Math.Min(10, consecutiveTransientFailures - 1));
        var scaledTicks = _options.InitialRetryDelay.Ticks * (1L << exponent);
        var cappedDelay = scaledTicks <= 0 || scaledTicks > _options.MaxRetryDelay.Ticks
            ? _options.MaxRetryDelay
            : TimeSpan.FromTicks(scaledTicks);

        var jitterRange = _options.JitterRatio;
        if (jitterRange > 0.0)
        {
            var sample = Math.Clamp(_jitterSource.NextUnitDouble(), 0.0, 1.0);
            var factor = 1.0 + (((sample * 2.0) - 1.0) * jitterRange);
            var jitteredMs = Math.Clamp(cappedDelay.TotalMilliseconds * factor, 1.0, _options.MaxRetryDelay.TotalMilliseconds);
            cappedDelay = TimeSpan.FromMilliseconds(jitteredMs);
        }

        if (lastResult.RetryAfterHint is { } hint && hint > cappedDelay)
        {
            cappedDelay = hint > _options.MaxRetryDelay ? _options.MaxRetryDelay : hint;
        }

        return cappedDelay;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? cts;
        lock (_stateLock)
        {
            cts = _runningCts;
            _runningCts = null;
            _loopTask = null;
        }

        try
        {
            _lifetimeCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore concurrent disposal.
        }

        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore concurrent disposal.
        }

        cts?.Dispose();

        // ARGON-3.4-006 & NEW-004: Only release _heartbeatExecutionLock and _lifetimeCts immediately
        // if no execution reservation is active; otherwise the last active reservation releases them in
        // ExitExecutionReservation(), preventing ObjectDisposedException during in-flight operations.
        if (Volatile.Read(ref _activeExecutionReservations) == 0)
        {
            _ = _operationsDrainedTcs.TrySetResult();
            ReleaseExecutionResourcesIfNeeded();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            await _operationsDrainedTcs.Task.ConfigureAwait(false);
            return;
        }

        CancellationTokenSource? cts;
        Task? loopTask;
        lock (_stateLock)
        {
            cts = _runningCts;
            loopTask = _loopTask;
            _runningCts = null;
            _loopTask = null;
        }

        try
        {
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Already disposed.
        }

        if (cts is not null)
        {
            try
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Already disposed.
            }
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        cts?.Dispose();

        if (Volatile.Read(ref _activeExecutionReservations) == 0)
        {
            _ = _operationsDrainedTcs.TrySetResult();
        }

        await _operationsDrainedTcs.Task.ConfigureAwait(false);
        ReleaseExecutionResourcesIfNeeded();
    }

    private bool TryEnterExecutionReservation()
    {
        while (true)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            var current = Volatile.Read(ref _activeExecutionReservations);
            if (Interlocked.CompareExchange(ref _activeExecutionReservations, current + 1, current) == current)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    ExitExecutionReservation();
                    return false;
                }

                return true;
            }
        }
    }

    private void ExitExecutionReservation()
    {
        if (Interlocked.Decrement(ref _activeExecutionReservations) == 0 &&
            Volatile.Read(ref _disposed) != 0)
        {
            _ = _operationsDrainedTcs.TrySetResult();
            ReleaseExecutionResourcesIfNeeded();
        }
    }

    private void ReleaseExecutionResourcesIfNeeded()
    {
        if (Interlocked.Exchange(ref _executionResourcesReleased, 1) != 0)
        {
            return;
        }

        _heartbeatExecutionLock.Dispose();
        _lifetimeCts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken loopToken)
    {
        while (!loopToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            if (!TryEnterExecutionReservation())
            {
                break;
            }

            HeartbeatAttemptResult result;
            int transientFailuresSnapshot;

            try
            {
                var lockAcquired = false;
                try
                {
                    await _heartbeatExecutionLock.WaitAsync(loopToken).ConfigureAwait(false);
                    lockAcquired = true;

                    if (loopToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }

                    result = await ExecuteSingleHeartbeatAsync(loopToken).ConfigureAwait(false);
                }
                finally
                {
                    if (lockAcquired)
                    {
                        _ = _heartbeatExecutionLock.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
            {
                break;
            }
            finally
            {
                ExitExecutionReservation();
            }

            bool shouldContinue;
            lock (_stateLock)
            {
                transientFailuresSnapshot = _consecutiveTransientFailures;
                shouldContinue = _authorizationStatus is
                    DeviceAuthorizationStatus.Authorized or
                    DeviceAuthorizationStatus.PendingVerification &&
                    !loopToken.IsCancellationRequested &&
                    Volatile.Read(ref _disposed) == 0;
            }

            if (!shouldContinue)
            {
                break;
            }

            var delay = ComputeNextDelay(result, Math.Max(1, transientFailuresSnapshot));
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, _timeProvider, loopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (loopToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                {
                    break;
                }
            }
        }
    }

    private async Task<HeartbeatAttemptResult> ExecuteSingleHeartbeatAsync(CancellationToken ct)
    {
        long capturedEpoch;
        Guid? expectedDeviceId;

        lock (_stateLock)
        {
            if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Revoked,
                    "Device authorization is revoked; heartbeat aborted.",
                    deviceId: _boundDeviceId,
                    httpStatusCode: 401,
                    serverErrorCode: _lastRevocationDetails?.ServerErrorCode ?? "unauthorized");
            }

            capturedEpoch = _epoch;
            expectedDeviceId = _boundDeviceId;
        }

        DeviceCredential? credential;
        try
        {
            credential = await _credentialStore.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.Cancelled,
                "Credential load cancelled before heartbeat.");
        }
        catch (CredentialStoreCorruptedException)
        {
            await HandleCredentialUnavailableAsync(
                capturedEpoch,
                DeviceAuthorizationStatus.CredentialInvalid,
                "Stored device credential is corrupted or cannot be decrypted.",
                ct).ConfigureAwait(false);

            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.InvalidCredential,
                "Stored device credential is corrupted or cannot be decrypted.");
        }

        if (credential is null)
        {
            await HandleCredentialUnavailableAsync(
                capturedEpoch,
                DeviceAuthorizationStatus.Unpaired,
                "No paired device credential found.",
                ct).ConfigureAwait(false);

            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.InvalidCredential,
                "No paired device credential is available.");
        }

        using (credential)
        {
            Guid credentialDeviceId;
            Uri credentialOrigin;
            try
            {
                if (credential.IsDisposed || !DeviceCredential.IsValidTokenFormat(credential.Token))
                {
                    await HandleCredentialUnavailableAsync(
                        capturedEpoch,
                        DeviceAuthorizationStatus.CredentialInvalid,
                        "Loaded device credential token format is invalid.",
                        ct).ConfigureAwait(false);

                    return HeartbeatAttemptResult.Failure(
                        HeartbeatOutcomeKind.InvalidCredential,
                        "Loaded device credential token format is invalid.");
                }

                credentialDeviceId = credential.Metadata.DeviceId;
                credentialOrigin = credential.Metadata.ServerOrigin;
            }
            catch (ObjectDisposedException)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.InvalidCredential,
                    "Loaded device credential was disposed unexpectedly.");
            }

            // ARGON-3.4-002: Check durable revocation tombstone store before authorizing or transmitting.
            // If the tombstone store throws (corrupted or tampered file), fail closed immediately.
            bool durablyRevoked;
            try
            {
                durablyRevoked = await _tombstoneStore.IsRevokedAsync(credentialDeviceId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Cancelled,
                    "Tombstone check cancelled before heartbeat.",
                    deviceId: credentialDeviceId);
            }
            catch (Exception)
            {
                await HandleCredentialUnavailableAsync(
                    capturedEpoch,
                    DeviceAuthorizationStatus.CredentialInvalid,
                    "Revocation tombstone store is corrupted or unreadable; failing closed.",
                    ct).ConfigureAwait(false);

                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.InvalidCredential,
                    "Revocation tombstone store is corrupted or unreadable; failing closed.",
                    deviceId: credentialDeviceId);
            }

            long sequence;
            bool blockedByRevokedId = false;
            bool staleBeforeSend = false;
            HeartbeatSnapshot? initialBindSnapshot = null;

            lock (_stateLock)
            {
                if (capturedEpoch != _epoch || _authorizationStatus == DeviceAuthorizationStatus.Revoked)
                {
                    staleBeforeSend = true;
                    sequence = 0;
                }
                else if (durablyRevoked || _revokedDeviceIds.Contains(credentialDeviceId))
                {
                    _ = _revokedDeviceIds.Add(credentialDeviceId);
                    blockedByRevokedId = true;
                    sequence = 0;
                }
                else if (expectedDeviceId.HasValue && expectedDeviceId.Value != credentialDeviceId)
                {
                    staleBeforeSend = true;
                    sequence = 0;
                }
                else
                {
                    // ARGON-3.4-001: Never transition to Authorized before the server returns a valid HTTP 200.
                    // Place unverified credentials in PendingVerification while the heartbeat is in flight.
                    if (_authorizationStatus is not (DeviceAuthorizationStatus.Authorized or DeviceAuthorizationStatus.PendingVerification))
                    {
                        _authorizationStatus = DeviceAuthorizationStatus.PendingVerification;
                        _boundDeviceId = credentialDeviceId;
                        _boundServerOrigin = credentialOrigin;
                        initialBindSnapshot = BuildSnapshotUnsafe();
                    }

                    sequence = ++_nextSequence;
                }
            }

            if (blockedByRevokedId)
            {
                return await ApplyConfirmedRevocationAsync(
                    capturedEpoch,
                    credentialDeviceId,
                    "unauthorized",
                    "Stored credential belongs to a previously revoked device identity.",
                    ct).ConfigureAwait(false);
            }

            if (staleBeforeSend)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Cancelled,
                    "Heartbeat superseded by an authorization state change before transmission.",
                    deviceId: credentialDeviceId);
            }

            if (initialBindSnapshot is not null)
            {
                SnapshotChanged?.Invoke(this, initialBindSnapshot);
            }

            var result = await _heartbeatClient.SendHeartbeatAsync(credential, ct).ConfigureAwait(false);
            return await ApplyAttemptOutcomeAsync(
                capturedEpoch,
                sequence,
                credentialDeviceId,
                credentialOrigin,
                result,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<HeartbeatAttemptResult> ApplyAttemptOutcomeAsync(
        long capturedEpoch,
        long sequence,
        Guid credentialDeviceId,
        Uri credentialOrigin,
        HeartbeatAttemptResult result,
        CancellationToken ct)
    {
        if (result.Outcome == HeartbeatOutcomeKind.Revoked || result.Outcome == HeartbeatOutcomeKind.Forbidden)
        {
            return await ApplyConfirmedRevocationAsync(
                capturedEpoch,
                credentialDeviceId,
                result.ServerErrorCode ?? (result.HttpStatusCode == 403 ? "forbidden" : "unauthorized"),
                result.Detail ?? "Server rejected the device credential.",
                ct,
                result).ConfigureAwait(false);
        }

        if (result.Outcome == HeartbeatOutcomeKind.InvalidCredential)
        {
            await HandleCredentialUnavailableAsync(
                capturedEpoch,
                DeviceAuthorizationStatus.CredentialInvalid,
                result.Detail ?? "Device credential validation failed.",
                ct).ConfigureAwait(false);
            return result;
        }

        HeartbeatSnapshot? snapshotToPublish = null;

        lock (_stateLock)
        {
            // Ignore stale in-flight completions after epoch change, newer applied sequence, or revocation.
            if (capturedEpoch != _epoch ||
                sequence <= _latestAppliedSequence ||
                _authorizationStatus is not (DeviceAuthorizationStatus.Authorized or DeviceAuthorizationStatus.PendingVerification) ||
                _boundDeviceId != credentialDeviceId)
            {
                return result;
            }

            _latestAppliedSequence = sequence;
            _lastAttemptAtUtc = _timeProvider.GetUtcNow();
            _lastOutcome = result.Outcome;

            if (result.Outcome == HeartbeatOutcomeKind.Cancelled)
            {
                return result;
            }

            if (result.Succeeded)
            {
                // ARGON-3.4-001: Only a valid HTTP 200 heartbeat response promotes PendingVerification -> Authorized.
                _authorizationStatus = DeviceAuthorizationStatus.Authorized;
                _connectivityState = HeartbeatConnectivityState.Healthy;
                _boundServerOrigin = credentialOrigin;
                _consecutiveTransientFailures = 0;
                _totalSucceededCount++;
                _lastSuccessAtUtc = _lastAttemptAtUtc;
                _lastServerTimeUtc = result.ServerTimeUtc;
                _lastErrorDetail = null;
            }
            else
            {
                // Transient outage/5xx/timeout/captive-portal never revokes device pairing,
                // and never promotes an unverified PendingVerification credential to Authorized.
                _consecutiveTransientFailures++;
                _totalTransientFailureCount++;
                _connectivityState = _consecutiveTransientFailures >= _options.ConsecutiveFailuresBeforeDisconnected
                    ? HeartbeatConnectivityState.Disconnected
                    : HeartbeatConnectivityState.Retrying;
                _lastErrorDetail = result.Detail;
            }

            snapshotToPublish = BuildSnapshotUnsafe();
        }

        if (snapshotToPublish is not null)
        {
            SnapshotChanged?.Invoke(this, snapshotToPublish);
        }

        return result;
    }

    private async Task<HeartbeatAttemptResult> ApplyConfirmedRevocationAsync(
        long capturedEpoch,
        Guid revokedDeviceId,
        string serverErrorCode,
        string reason,
        CancellationToken ct,
        HeartbeatAttemptResult? originalResult = null)
    {
        var now = _timeProvider.GetUtcNow();
        CancellationTokenSource? ctsToCancel = null;
        bool wasStaleEpoch;
        bool performRevocationSideEffects = false;

        lock (_stateLock)
        {
            _ = _revokedDeviceIds.Add(revokedDeviceId);

            // A stale 401 from an older pairing epoch must never revoke a newer paired device.
            if (capturedEpoch != _epoch || (_boundDeviceId.HasValue && _boundDeviceId.Value != revokedDeviceId))
            {
                wasStaleEpoch = true;
            }
            else if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
            {
                wasStaleEpoch = false;
                _connectivityState = HeartbeatConnectivityState.Revoked;
            }
            else
            {
                wasStaleEpoch = false;
                performRevocationSideEffects = true;
                _epoch++;
                _authorizationStatus = DeviceAuthorizationStatus.Revoked;
                _connectivityState = HeartbeatConnectivityState.Revoked;
                _revokedAtUtc = now;
                _lastAttemptAtUtc = now;
                _lastOutcome = originalResult?.Outcome ?? HeartbeatOutcomeKind.Revoked;
                _lastErrorDetail = reason;
                _lastRevocationDetails = new DeviceRevokedEventArgs(revokedDeviceId, now, serverErrorCode, reason);
                ctsToCancel = _runningCts;
            }
        }

        if (wasStaleEpoch)
        {
            _ = await TryPersistTombstoneWithRetryAsync(revokedDeviceId, now).ConfigureAwait(false);
            return originalResult ?? HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.Cancelled,
                "Stale revocation response ignored because device was already re-paired.",
                deviceId: revokedDeviceId);
        }

        if (ctsToCancel is not null)
        {
            try
            {
                await ctsToCancel.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Ignore if already disposed.
            }
        }

        if (performRevocationSideEffects)
        {
            var cleanup = await ExecuteRevocationSideEffectsAsync(revokedDeviceId, now, ct).ConfigureAwait(false);
            DeviceRevokedEventArgs revokedArgs;
            HeartbeatSnapshot snapshotToPublish;

            lock (_stateLock)
            {
                _lastCredentialPurgeSucceeded = cleanup.CredentialPurgeSucceeded;
                _lastTombstonePersisted = cleanup.TombstonePersisted;
                _lastInputResetSucceeded = cleanup.InputResetSucceeded;
                _lastCleanupErrorDetail = cleanup.CleanupErrorDetail;
                revokedArgs = new DeviceRevokedEventArgs(
                    revokedDeviceId,
                    now,
                    serverErrorCode,
                    reason,
                    cleanup.CredentialPurgeSucceeded,
                    cleanup.TombstonePersisted,
                    cleanup.InputResetSucceeded,
                    cleanup.CleanupErrorDetail);
                _lastRevocationDetails = revokedArgs;
                snapshotToPublish = BuildSnapshotUnsafe();
            }

            if (Volatile.Read(ref _disposed) == 0)
            {
                RevocationConfirmed?.Invoke(this, revokedArgs);
                SnapshotChanged?.Invoke(this, snapshotToPublish);
            }
        }

        return originalResult ?? HeartbeatAttemptResult.Failure(
            HeartbeatOutcomeKind.Revoked,
            reason,
            deviceId: revokedDeviceId,
            httpStatusCode: 401,
            serverErrorCode: serverErrorCode);
    }

    private async Task HandleCredentialUnavailableAsync(
        long capturedEpoch,
        DeviceAuthorizationStatus targetStatus,
        string detail,
        CancellationToken ct)
    {
        HeartbeatSnapshot? snapshot = null;
        CancellationTokenSource? ctsToCancel = null;
        bool terminated = false;

        lock (_stateLock)
        {
            if (capturedEpoch != _epoch || _authorizationStatus == DeviceAuthorizationStatus.Revoked)
            {
                return;
            }

            _epoch++;
            _authorizationStatus = targetStatus;
            _connectivityState = targetStatus == DeviceAuthorizationStatus.Unpaired
                ? HeartbeatConnectivityState.Idle
                : HeartbeatConnectivityState.Stopped;
            _boundDeviceId = null;
            _boundServerOrigin = null;
            _lastOutcome = HeartbeatOutcomeKind.InvalidCredential;
            _lastErrorDetail = detail;
            ctsToCancel = _runningCts;
            snapshot = BuildSnapshotUnsafe();
            terminated = true;
        }

        if (ctsToCancel is not null)
        {
            try
            {
                await ctsToCancel.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // Ignore if already disposed.
            }
        }

        if (terminated)
        {
            _ = await TerminateActiveSessionAndResetInputsAsync(ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }
        }
    }

    private sealed record RevocationCleanupOutcome(
        bool CredentialPurgeSucceeded,
        bool TombstonePersisted,
        bool InputResetSucceeded,
        string? CleanupErrorDetail);

    private async Task<RevocationCleanupOutcome> ExecuteRevocationSideEffectsAsync(
        Guid? revokedDeviceId,
        DateTimeOffset revokedAtUtc,
        CancellationToken ct)
    {
        List<string> errors = [];

        // 1. ARGON-3.4-005: Immediately terminate any active or pending session and unconditionally
        // reset all held inputs first so physical input cannot linger during disk I/O retries.
        var (inputResetSucceeded, inputError) = await TerminateActiveSessionAndResetInputsAsync(ct).ConfigureAwait(false);
        if (!inputResetSucceeded && inputError is not null)
        {
            errors.Add(inputError);
        }

        // 2. ARGON-3.4-002: Persist durable revocation tombstone with bounded retries so a revoked
        // DeviceId remains permanently blocked across process restarts even if credential deletion fails.
        var tombstonePersisted = true;
        if (revokedDeviceId is { } id && id != Guid.Empty)
        {
            var (persisted, tombstoneError) = await TryPersistTombstoneWithRetryAsync(id, revokedAtUtc).ConfigureAwait(false);
            tombstonePersisted = persisted;
            if (!persisted && tombstoneError is not null)
            {
                errors.Add(tombstoneError);
            }
        }

        // 3. ARGON-3.4-002 & NEW-003: Atomically purge local DPAPI credential only if it still
        // matches the revoked DeviceId (or is corrupted), protecting a concurrent fresh pairing.
        var credentialPurgeSucceeded = true;
        if (_options.DeleteLocalCredentialOnRevocation)
        {
            var (purged, purgeError) = await TryDeleteRevokedCredentialWithRetryAsync(revokedDeviceId).ConfigureAwait(false);
            credentialPurgeSucceeded = purged;
            if (!purged && purgeError is not null)
            {
                errors.Add(purgeError);
            }
        }

        var cleanupErrorDetail = errors.Count > 0 ? string.Join(" | ", errors) : null;
        return new RevocationCleanupOutcome(
            credentialPurgeSucceeded,
            tombstonePersisted,
            inputResetSucceeded,
            cleanupErrorDetail);
    }

    private async Task<(bool Succeeded, string? ErrorDetail)> TryPersistTombstoneWithRetryAsync(
        Guid revokedDeviceId,
        DateTimeOffset revokedAtUtc)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _options.CredentialDeleteMaxAttempts; attempt++)
        {
            try
            {
                await _tombstoneStore.RecordRevokedAsync(revokedDeviceId, revokedAtUtc, CancellationToken.None).ConfigureAwait(false);
                return (true, null);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < _options.CredentialDeleteMaxAttempts && _options.CredentialDeleteRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.CredentialDeleteRetryDelay).ConfigureAwait(false);
                }
            }
        }

        return (false, $"Failed to persist durable revocation tombstone after {_options.CredentialDeleteMaxAttempts} attempts: {lastException?.Message}");
    }

    private async Task<(bool Succeeded, string? ErrorDetail)> TryDeleteRevokedCredentialWithRetryAsync(Guid? revokedDeviceId)
    {
        Exception? lastException = null;
        for (var attempt = 1; attempt <= _options.CredentialDeleteMaxAttempts; attempt++)
        {
            try
            {
                if (revokedDeviceId is { } expectedId && expectedId != Guid.Empty)
                {
                    // ARGON NEW-003: Atomic conditional deletion under the credential store's internal gate.
                    // Returns true if deleted (matching DeviceId, corrupted, or missing), or false if a
                    // concurrent fresh pairing has already saved a valid credential for a different DeviceId.
                    _ = await _credentialStore.DeleteIfMatchesAsync(expectedId, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await _credentialStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }

                return (true, null);
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < _options.CredentialDeleteMaxAttempts && _options.CredentialDeleteRetryDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_options.CredentialDeleteRetryDelay).ConfigureAwait(false);
                }
            }
        }

        return (false, $"Failed to delete revoked local credential after {_options.CredentialDeleteMaxAttempts} attempts: {lastException?.Message}");
    }

    private async Task<(bool InputResetSucceeded, string? ErrorDetail)> TerminateActiveSessionAndResetInputsAsync(
        CancellationToken ct)
    {
        List<string> errors = [];

        if (_sessionStateMachine is not null)
        {
            try
            {
                _ = _sessionStateMachine.Terminate();
            }
            catch (Exception ex)
            {
                errors.Add($"Session termination failed: {ex.Message}");
            }
        }

        // ARGON-3.4-005: Unconditionally reset all held inputs whenever an IInputExecutor is attached,
        // regardless of the session state prior to termination (ActiveFullControl, ActiveViewOnly,
        // Paused, LockedOrUnavailable, AwaitingOwnerApproval, or Idle).
        var inputResetSucceeded = true;
        if (_inputExecutor is not null)
        {
            try
            {
                _ = await _inputExecutor.ResetAllAsync(
                    InputResetReason.SessionDisconnected,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                inputResetSucceeded = false;
                errors.Add("Input reset was cancelled unexpectedly during session teardown.");
            }
            catch (Exception ex)
            {
                inputResetSucceeded = false;
                errors.Add($"InputResetFailed: {ex.Message}");
            }
        }

        var errorDetail = errors.Count > 0 ? string.Join(" | ", errors) : null;
        return (inputResetSucceeded && errors.Count == 0, errorDetail);
    }

    private HeartbeatSnapshot BuildSnapshotUnsafe() =>
        new(
            _authorizationStatus,
            _connectivityState,
            _boundDeviceId,
            _boundServerOrigin,
            _epoch,
            _consecutiveTransientFailures,
            _totalSucceededCount,
            _totalTransientFailureCount,
            _lastSuccessAtUtc,
            _lastServerTimeUtc,
            _lastAttemptAtUtc,
            _revokedAtUtc,
            _lastOutcome,
            _lastErrorDetail,
            _lastCredentialPurgeSucceeded,
            _lastTombstonePersisted,
            _lastInputResetSucceeded,
            _lastCleanupErrorDetail);
}
