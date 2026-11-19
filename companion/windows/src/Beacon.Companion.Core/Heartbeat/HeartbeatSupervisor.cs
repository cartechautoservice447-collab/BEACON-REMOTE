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
/// Coordinates periodic device heartbeats, bounded exponential backoff with jitter,
/// single-loop execution safety, stale-response epoch fencing, active session termination,
/// and fail-closed revocation handling requiring fresh authorized pairing before recovery.
/// </summary>
public sealed class HeartbeatSupervisor : IHeartbeatSupervisor
{
    private readonly ICredentialStore _credentialStore;
    private readonly IHeartbeatClient _heartbeatClient;
    private readonly HeartbeatSupervisorOptions _options;
    private readonly SessionStateMachine? _sessionStateMachine;
    private readonly IInputExecutor? _inputExecutor;
    private readonly TimeProvider _timeProvider;
    private readonly IJitterSource _jitterSource;

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _heartbeatExecutionLock = new(1, 1);
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
    private DeviceRevokedEventArgs? _lastRevocationDetails;

    private CancellationTokenSource? _runningCts;
    private Task? _loopTask;
    private int _disposed;

    public HeartbeatSupervisor(
        ICredentialStore credentialStore,
        IHeartbeatClient heartbeatClient,
        HeartbeatSupervisorOptions? options = null,
        SessionStateMachine? sessionStateMachine = null,
        IInputExecutor? inputExecutor = null,
        TimeProvider? timeProvider = null,
        IJitterSource? jitterSource = null)
    {
        ArgumentNullException.ThrowIfNull(credentialStore);
        ArgumentNullException.ThrowIfNull(heartbeatClient);

        _options = options ?? new HeartbeatSupervisorOptions();
        _options.Validate();

        _credentialStore = credentialStore;
        _heartbeatClient = heartbeatClient;
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

    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        HeartbeatSnapshot snapshot;
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

            if (_loopTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            _runningCts?.Dispose();
            _runningCts = new CancellationTokenSource();
            _connectivityState = HeartbeatConnectivityState.Starting;

            var loopToken = _runningCts.Token;
            _loopTask = Task.Run(() => RunLoopAsync(loopToken), CancellationToken.None);
            snapshot = BuildSnapshotUnsafe();
        }

        SnapshotChanged?.Invoke(this, snapshot);
        return Task.CompletedTask;
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
            await ctsToCancel.CancelAsync().ConfigureAwait(false);
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

        await _heartbeatExecutionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await ExecuteSingleHeartbeatAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _heartbeatExecutionLock.Release();
        }
    }

    public async Task AcknowledgeFreshPairingAsync(
        bool startSupervisorLoop = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        DeviceCredential? loadedCredential;
        try
        {
            loadedCredential = await _credentialStore.LoadAsync(ct).ConfigureAwait(false);
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

            CancellationTokenSource? ctsToCancel;
            Task? priorLoopTask;
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

                if (_revokedDeviceIds.Contains(newDeviceId))
                {
                    throw new DeviceRevokedException(
                        "Cannot restore authorization using a previously revoked DeviceId. Complete a fresh pairing.",
                        _lastRevocationDetails ?? new DeviceRevokedEventArgs(
                            newDeviceId,
                            _timeProvider.GetUtcNow(),
                            "unauthorized",
                            "Previously revoked device identity rejected."));
                }

                // Fence any older in-flight heartbeats from the previous epoch so stale 200 or 401
                // responses can never clobber the newly paired device.
                _epoch++;
                _authorizationStatus = DeviceAuthorizationStatus.Authorized;
                _connectivityState = HeartbeatConnectivityState.Idle;
                _boundDeviceId = newDeviceId;
                _boundServerOrigin = originValidation.CanonicalOrigin;
                _consecutiveTransientFailures = 0;
                _revokedAtUtc = null;
                _lastErrorDetail = null;
                _lastOutcome = null;
                _lastRevocationDetails = null;
                ctsToCancel = _runningCts;
                priorLoopTask = _loopTask;
                snapshot = BuildSnapshotUnsafe();
            }

            if (ctsToCancel is not null)
            {
                await ctsToCancel.CancelAsync().ConfigureAwait(false);
            }

            if (startSupervisorLoop && priorLoopTask is not null)
            {
                try
                {
                    await priorLoopTask.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
        }

        SnapshotChanged?.Invoke(this, snapshot);

        if (startSupervisorLoop)
        {
            await StartAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task NotifyAuthorizationRevokedAsync(
        Guid? expectedDeviceId = null,
        string? reason = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var now = _timeProvider.GetUtcNow();
        var effectiveReason = string.IsNullOrWhiteSpace(reason)
            ? "Device authorization was revoked."
            : reason.Trim();

        Guid? targetDeviceId = null;
        DeviceRevokedEventArgs? revokedArgs = null;
        HeartbeatSnapshot? snapshot = null;
        CancellationTokenSource? ctsToCancel = null;
        bool shouldPerformSideEffects = false;

        lock (_stateLock)
        {
            if (expectedDeviceId.HasValue)
            {
                if (_boundDeviceId.HasValue && _boundDeviceId.Value != expectedDeviceId.Value)
                {
                    _ = _revokedDeviceIds.Add(expectedDeviceId.Value);
                    return;
                }

                targetDeviceId = expectedDeviceId.Value;
                _ = _revokedDeviceIds.Add(expectedDeviceId.Value);
            }
            else if (_boundDeviceId.HasValue)
            {
                targetDeviceId = _boundDeviceId.Value;
                _ = _revokedDeviceIds.Add(_boundDeviceId.Value);
            }

            if (_authorizationStatus == DeviceAuthorizationStatus.Revoked)
            {
                return;
            }

            _epoch++;
            _authorizationStatus = DeviceAuthorizationStatus.Revoked;
            _connectivityState = HeartbeatConnectivityState.Revoked;
            _revokedAtUtc = now;
            _lastOutcome = HeartbeatOutcomeKind.Revoked;
            _lastErrorDetail = effectiveReason;
            revokedArgs = new DeviceRevokedEventArgs(targetDeviceId, now, "revoked", effectiveReason);
            _lastRevocationDetails = revokedArgs;
            ctsToCancel = _runningCts;
            snapshot = BuildSnapshotUnsafe();
            shouldPerformSideEffects = true;
        }

        if (ctsToCancel is not null)
        {
            await ctsToCancel.CancelAsync().ConfigureAwait(false);
        }

        if (shouldPerformSideEffects)
        {
            await ExecuteRevocationSideEffectsAsync(targetDeviceId, ct).ConfigureAwait(false);
            if (revokedArgs is not null)
            {
                RevocationConfirmed?.Invoke(this, revokedArgs);
            }

            if (snapshot is not null)
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }
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

            if (_authorizationStatus != DeviceAuthorizationStatus.Authorized || _boundDeviceId is not { } deviceId)
            {
                throw new DeviceNotAuthorizedException(
                    "Operation rejected because no authorized device credential is active.",
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
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Ignore concurrent disposal.
        }

        cts?.Dispose();
        _heartbeatExecutionLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
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
        _heartbeatExecutionLock.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken loopToken)
    {
        while (!loopToken.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
        {
            HeartbeatAttemptResult result;
            int transientFailuresSnapshot;

            try
            {
                await _heartbeatExecutionLock.WaitAsync(loopToken).ConfigureAwait(false);
                try
                {
                    if (loopToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
                    {
                        break;
                    }

                    result = await ExecuteSingleHeartbeatAsync(loopToken).ConfigureAwait(false);
                }
                finally
                {
                    _ = _heartbeatExecutionLock.Release();
                }
            }
            catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
            {
                break;
            }

            bool shouldContinue;
            lock (_stateLock)
            {
                transientFailuresSnapshot = _consecutiveTransientFailures;
                shouldContinue = _authorizationStatus == DeviceAuthorizationStatus.Authorized &&
                    !loopToken.IsCancellationRequested;
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
                catch (OperationCanceledException) when (loopToken.IsCancellationRequested)
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
                else if (_revokedDeviceIds.Contains(credentialDeviceId))
                {
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
                    if (_authorizationStatus != DeviceAuthorizationStatus.Authorized)
                    {
                        _authorizationStatus = DeviceAuthorizationStatus.Authorized;
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
                _authorizationStatus != DeviceAuthorizationStatus.Authorized ||
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
                // Transient outage/5xx/timeout/captive-portal never revokes device pairing.
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
        DeviceRevokedEventArgs? revokedArgs = null;
        HeartbeatSnapshot? snapshotToPublish = null;
        CancellationTokenSource? ctsToCancel = null;
        bool wasStaleEpoch;

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
                _epoch++;
                _authorizationStatus = DeviceAuthorizationStatus.Revoked;
                _connectivityState = HeartbeatConnectivityState.Revoked;
                _revokedAtUtc = now;
                _lastAttemptAtUtc = now;
                _lastOutcome = originalResult?.Outcome ?? HeartbeatOutcomeKind.Revoked;
                _lastErrorDetail = reason;
                revokedArgs = new DeviceRevokedEventArgs(revokedDeviceId, now, serverErrorCode, reason);
                _lastRevocationDetails = revokedArgs;
                ctsToCancel = _runningCts;
                snapshotToPublish = BuildSnapshotUnsafe();
            }
        }

        if (wasStaleEpoch)
        {
            return originalResult ?? HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.Cancelled,
                "Stale revocation response ignored because device was already re-paired.",
                deviceId: revokedDeviceId);
        }

        if (ctsToCancel is not null)
        {
            await ctsToCancel.CancelAsync().ConfigureAwait(false);
        }

        if (revokedArgs is not null)
        {
            await ExecuteRevocationSideEffectsAsync(revokedDeviceId, ct).ConfigureAwait(false);
            RevocationConfirmed?.Invoke(this, revokedArgs);
            if (snapshotToPublish is not null)
            {
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
            await ctsToCancel.CancelAsync().ConfigureAwait(false);
        }

        if (terminated)
        {
            await TerminateActiveSessionAsync(ct).ConfigureAwait(false);
            if (snapshot is not null)
            {
                SnapshotChanged?.Invoke(this, snapshot);
            }
        }
    }

    private async Task ExecuteRevocationSideEffectsAsync(Guid? revokedDeviceId, CancellationToken ct)
    {
        if (_options.DeleteLocalCredentialOnRevocation)
        {
            try
            {
                // Verify we only delete the matching revoked device credential (or corrupted envelope),
                // protecting a concurrent fresh pairing from accidental deletion.
                using var current = await _credentialStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                if (current is not null &&
                    (!revokedDeviceId.HasValue || current.Metadata.DeviceId == revokedDeviceId.Value))
                {
                    await _credentialStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (CredentialStoreCorruptedException)
            {
                try
                {
                    await _credentialStore.DeleteAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // Best-effort purge; memory revocation latch remains enforced.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort purge; memory revocation latch remains enforced.
                }
            }
            catch (IOException)
            {
                // Best-effort purge; memory revocation latch remains enforced.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort purge; memory revocation latch remains enforced.
            }
        }

        await TerminateActiveSessionAsync(ct).ConfigureAwait(false);
    }

    private async Task TerminateActiveSessionAsync(CancellationToken ct)
    {
        var hadControl = _sessionStateMachine?.Terminate() ?? false;
        if (_inputExecutor is not null && (hadControl || _sessionStateMachine is null))
        {
            try
            {
                _ = await _inputExecutor.ResetAllAsync(
                    InputResetReason.SessionDisconnected,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Best-effort safety release on teardown.
            }
            catch (InvalidOperationException)
            {
                // Best-effort safety release on teardown.
            }
        }
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
            _lastErrorDetail);
}
