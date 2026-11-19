using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Heartbeat;
using Beacon.Companion.Core.Input;
using Beacon.Companion.Core.Pairing;
using Beacon.Companion.Core.Protocol;
using Beacon.Companion.Core.Security;
using Beacon.Companion.Windows.Credentials;
using Beacon.Companion.Windows.Heartbeat;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class HeartbeatSupervisorTests
{
    private static readonly Uri TrustedOrigin = new("https://beacon.example.com");
    private static readonly Guid DeviceIdA = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
    private static readonly Guid DeviceIdB = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string TokenA = "deviceToken_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456";
    private const string TokenB = "deviceToken_ZYXWVUTSRQPONMLKJIHGFEDCBA6543210";

    private static readonly string SharedFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "heartbeat-protocol-contract.json");

    private static DeviceCredential CreateCredential(
        Guid? deviceId = null,
        string? token = null,
        Uri? origin = null) =>
        new(
            deviceId ?? DeviceIdA,
            origin ?? TrustedOrigin,
            (token ?? TokenA).AsSpan(),
            new DateTimeOffset(2026, 11, 18, 12, 0, 0, TimeSpan.Zero));

    private static async Task SaveSeedCredentialAsync(
        ICredentialStore store,
        Guid? deviceId = null,
        string? token = null,
        Uri? origin = null)
    {
        using var credential = CreateCredential(deviceId, token, origin);
        await store.SaveAsync(credential);
    }

    private static HttpResponseMessage OkJsonResponse(DateTimeOffset serverTimeUtc) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"ok":true,"server_time":"{{serverTimeUtc:O}}"}""",
                Encoding.UTF8,
                "application/json"),
        };

    // 1. Successful heartbeat
    [Fact]
    public async Task SuccessfulHeartbeatSendsBearerPostAndUpdatesHealthyState()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        Uri? capturedUri = null;
        HttpMethod? capturedMethod = null;
        string? capturedAuthScheme = null;
        string? capturedAuthParam = null;
        bool? capturedNoStore = null;

        using var handler = new StubHttpMessageHandler((req, unusedBody) =>
        {
            capturedUri = req.RequestUri;
            capturedMethod = req.Method;
            capturedAuthScheme = req.Headers.Authorization?.Scheme;
            capturedAuthParam = req.Headers.Authorization?.Parameter;
            capturedNoStore = req.Headers.CacheControl?.NoStore;
            return OkJsonResponse(timeProvider.GetUtcNow());
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient, timeProvider: timeProvider);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            client,
            new InMemoryRevocationTombstoneStore(),
            timeProvider: timeProvider);

        var result = await supervisor.TriggerHeartbeatNowAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.Succeeded, result.Outcome);
        Assert.Equal(DeviceIdA, result.DeviceId);
        Assert.Equal(200, result.HttpStatusCode);
        Assert.Equal(HttpMethod.Post, capturedMethod);
        Assert.Equal("https://beacon.example.com/api/public/agent/heartbeat", capturedUri?.ToString());
        Assert.Equal("Bearer", capturedAuthScheme);
        Assert.Equal(TokenA, capturedAuthParam);
        Assert.True(capturedNoStore);

        var snap = supervisor.Snapshot;
        Assert.True(snap.IsAuthorized);
        Assert.False(snap.IsRevoked);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, snap.AuthorizationStatus);
        Assert.Equal(HeartbeatConnectivityState.Healthy, snap.ConnectivityState);
        Assert.Equal(DeviceIdA, snap.DeviceId);
        Assert.Equal(1, snap.TotalSucceededCount);
        Assert.Equal(0, snap.ConsecutiveTransientFailures);
        Assert.Equal(DeviceIdA, supervisor.EnsureDeviceAuthorized());
    }

    // 2. Repeated periodic heartbeats
    [Fact]
    public async Task RepeatedPeriodicHeartbeatsExecuteOnConfiguredInterval()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var callCount = 0;
        var beatSignal = new AsyncSignal();

        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            _ = Interlocked.Increment(ref callCount);
            beatSignal.Signal();
            return OkJsonResponse(timeProvider.GetUtcNow());
        });
        using var httpClient = new HttpClient(handler);
        var options = new HeartbeatSupervisorOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            InitialRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(30),
        };
        var client = new HeartbeatClient(httpClient, timeProvider: timeProvider);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            client,
            new InMemoryRevocationTombstoneStore(),
            options,
            timeProvider: timeProvider);

        await supervisor.StartAsync();
        await beatSignal.WaitForCountAsync(1);

        Assert.Equal(1, Volatile.Read(ref callCount));

        timeProvider.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(25);
        Assert.Equal(1, Volatile.Read(ref callCount));

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await beatSignal.WaitForCountAsync(2);

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        await beatSignal.WaitForCountAsync(3);

        Assert.Equal(3, Volatile.Read(ref callCount));
        Assert.Equal(3, supervisor.Snapshot.TotalSucceededCount);
        Assert.Equal(HeartbeatConnectivityState.Healthy, supervisor.Snapshot.ConnectivityState);

        await supervisor.StopAsync();
    }

    // 3. Network timeout (remains PendingVerification until first 200 OK succeeds)
    [Fact]
    public async Task NetworkTimeoutEntersRetryingStateWithoutRevokingOrPrematurelyAuthorizingDevice()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var attempt = 0;
        using var handler = new AsyncStubHttpMessageHandler(async (unusedReq, ct) =>
        {
            var current = Interlocked.Increment(ref attempt);
            if (current == 1)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            return OkJsonResponse(timeProvider.GetUtcNow());
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(
            httpClient,
            new HeartbeatClientOptions { RequestTimeout = TimeSpan.FromSeconds(5) },
            timeProvider);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            client,
            new InMemoryRevocationTombstoneStore(),
            timeProvider: timeProvider);

        var timedOutTask = supervisor.TriggerHeartbeatNowAsync();
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var timedOut = await timedOutTask;

        Assert.False(timedOut.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.Timeout, timedOut.Outcome);
        Assert.True(timedOut.IsTransientFailure);
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(HeartbeatConnectivityState.Retrying, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(1, supervisor.Snapshot.ConsecutiveTransientFailures);
        Assert.True(await store.ExistsAsync());

        // Subsequent recovery promotes PendingVerification -> Authorized and restores Healthy.
        var recovered = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(HeartbeatConnectivityState.Healthy, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(0, supervisor.Snapshot.ConsecutiveTransientFailures);
    }

    // 4. Internet disconnection
    [Fact]
    public async Task InternetDisconnectionTransitionsToDisconnectedAfterThresholdAndPreservesCredential()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var attempt = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                return OkJsonResponse(DateTimeOffset.UtcNow);
            }

            throw new HttpRequestException("No such host is known.");
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);
        var options = new HeartbeatSupervisorOptions
        {
            ConsecutiveFailuresBeforeDisconnected = 2,
        };
        await using var supervisor = new HeartbeatSupervisor(
            store,
            client,
            new InMemoryRevocationTombstoneStore(),
            options);

        // First heartbeat verifies credential with the server.
        var verified = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(verified.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);

        var r1 = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.NetworkError, r1.Outcome);
        Assert.Equal(HeartbeatConnectivityState.Retrying, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);

        var r2 = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.NetworkError, r2.Outcome);
        Assert.Equal(HeartbeatConnectivityState.Disconnected, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(2, supervisor.Snapshot.ConsecutiveTransientFailures);
        Assert.True(await store.ExistsAsync());
    }

    // 5. Automatic reconnection after outage with bounded exponential backoff + jitter
    [Fact]
    public async Task AutomaticReconnectionUsesBoundedExponentialBackoffWithJitterAndResetsOnRecovery()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var attempt = 0;
        var attemptSignal = new AsyncSignal();

        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            var current = Interlocked.Increment(ref attempt);
            attemptSignal.Signal();
            return current <= 3
                ? throw new HttpRequestException("Simulated WAN outage")
                : OkJsonResponse(timeProvider.GetUtcNow());
        });
        using var httpClient = new HttpClient(handler);
        var jitter = new FixedJitterSource(0.5); // Centered jitter => exact nominal backoff (factor 1.0)
        var options = new HeartbeatSupervisorOptions
        {
            HeartbeatInterval = TimeSpan.FromSeconds(30),
            InitialRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(5),
            JitterRatio = 0.2,
            ConsecutiveFailuresBeforeDisconnected = 2,
        };
        var client = new HeartbeatClient(httpClient, timeProvider: timeProvider);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            client,
            new InMemoryRevocationTombstoneStore(),
            options,
            timeProvider: timeProvider,
            jitterSource: jitter);

        await supervisor.StartAsync();

        // Failure #1 -> backoff 2s (remains PendingVerification while unverified)
        await attemptSignal.WaitForCountAsync(1);
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        timeProvider.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref attempt));
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Failure #2 -> backoff 4s
        await attemptSignal.WaitForCountAsync(2);
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);
        Assert.Equal(HeartbeatConnectivityState.Disconnected, supervisor.Snapshot.ConnectivityState);
        timeProvider.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(2, Volatile.Read(ref attempt));
        timeProvider.Advance(TimeSpan.FromSeconds(1));

        // Failure #3 -> backoff 8s capped to MaxRetryDelay (5s)
        await attemptSignal.WaitForCountAsync(3);
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);
        timeProvider.Advance(TimeSpan.FromSeconds(5));

        // Attempt #4 succeeds, promotes PendingVerification -> Authorized, and resets transient failure count!
        await attemptSignal.WaitForCountAsync(4);
        await WaitUntilAsync(() => supervisor.Snapshot.ConnectivityState == HeartbeatConnectivityState.Healthy);

        Assert.Equal(0, supervisor.Snapshot.ConsecutiveTransientFailures);
        Assert.Equal(3, supervisor.Snapshot.TotalTransientFailureCount);
        Assert.Equal(1, supervisor.Snapshot.TotalSucceededCount);
        Assert.True(supervisor.Snapshot.IsAuthorized);

        await supervisor.StopAsync();
    }

    [Fact]
    public void BackoffCalculationAppliesSymmetricJitterAndCapsAtMaxRetryDelay()
    {
        using var store = new InMemoryCredentialStore();
        using var handler = new StubHttpMessageHandler((_, _) => OkJsonResponse(DateTimeOffset.UtcNow));
        using var httpClient = new HttpClient(handler);
        var minJitter = new FixedJitterSource(0.0); // factor 0.8
        var maxJitter = new FixedJitterSource(0.999999); // factor ~1.2

        var options = new HeartbeatSupervisorOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(2),
            MaxRetryDelay = TimeSpan.FromSeconds(10),
            JitterRatio = 0.2,
        };

        using var supMin = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore(),
            options,
            jitterSource: minJitter);
        using var supMax = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore(),
            options,
            jitterSource: maxJitter);

        var transientFail = HeartbeatAttemptResult.Failure(HeartbeatOutcomeKind.NetworkError, "err");
        var d1Min = supMin.ComputeNextDelay(transientFail, 1);
        var d1Max = supMax.ComputeNextDelay(transientFail, 1);
        Assert.InRange(d1Min.TotalMilliseconds, 1599, 1601);
        Assert.InRange(d1Max.TotalMilliseconds, 2399, 2401);

        var cappedWithRetryAfter = HeartbeatAttemptResult.Failure(
            HeartbeatOutcomeKind.TransientServerError,
            "busy",
            httpStatusCode: 429,
            retryAfterHint: TimeSpan.FromSeconds(60));
        var dCapped = supMax.ComputeNextDelay(cappedWithRetryAfter, 10);
        Assert.Equal(TimeSpan.FromSeconds(10), dCapped);
    }

    // 6. Server unavailable (5xx / 429)
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"server_error"}""")]
    [InlineData(HttpStatusCode.BadGateway, "Bad Gateway")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Service Unavailable")]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"rate_limited"}""")]
    public async Task ServerUnavailableIsClassifiedAsTransientAndAllowsRecovery(
        HttpStatusCode statusCode,
        string responseBody)
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var attempt = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                var msg = new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                };
                if (statusCode == HttpStatusCode.TooManyRequests)
                {
                    msg.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
                }

                return msg;
            }

            return OkJsonResponse(DateTimeOffset.UtcNow);
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);
        await using var supervisor = new HeartbeatSupervisor(store, client, new InMemoryRevocationTombstoneStore());

        var first = await supervisor.TriggerHeartbeatNowAsync();
        Assert.False(first.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.TransientServerError, first.Outcome);
        Assert.False(first.IsConfirmedRevocation);
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            Assert.Equal(TimeSpan.FromSeconds(12), first.RetryAfterHint);
        }

        var second = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(second.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(HeartbeatConnectivityState.Healthy, supervisor.Snapshot.ConnectivityState);
    }

    // 7. Authentication failure / missing or corrupted credential
    [Fact]
    public async Task MissingOrCorruptedCredentialRejectsAuthorizationAndTerminatesActiveSession()
    {
        var sessionMachine = new SessionStateMachine();
        _ = sessionMachine.BeginSession(
            new SessionApprovalRequest(Guid.NewGuid(), DeviceIdA, PermissionMode.FullControl, false, DateTimeOffset.UtcNow),
            new CompanionOptions { ServerOrigin = TrustedOrigin },
            ownerExplicitlyApproved: true);

        var inputExecutor = new RecordingInputExecutor();
        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new InvalidOperationException("Network should not be called without a valid credential."));
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);

        // Case A: Missing credential
        using var emptyStore = new InMemoryCredentialStore();
        await using (var supMissing = new HeartbeatSupervisor(
            emptyStore,
            client,
            new InMemoryRevocationTombstoneStore(),
            sessionStateMachine: sessionMachine,
            inputExecutor: inputExecutor))
        {
            var r = await supMissing.TriggerHeartbeatNowAsync();
            Assert.Equal(HeartbeatOutcomeKind.InvalidCredential, r.Outcome);
            Assert.Equal(DeviceAuthorizationStatus.Unpaired, supMissing.Snapshot.AuthorizationStatus);
            Assert.Equal(SessionState.Terminated, sessionMachine.State);
            Assert.Equal(1, inputExecutor.ResetCount);
            _ = Assert.Throws<DeviceNotAuthorizedException>(() => supMissing.EnsureDeviceAuthorized());
        }

        // Case B: Corrupted DPAPI store
        using var corruptStore = new InMemoryCredentialStore { ThrowCorruptedOnLoad = true };
        await using (var supCorrupt = new HeartbeatSupervisor(corruptStore, client, new InMemoryRevocationTombstoneStore()))
        {
            var r = await supCorrupt.TriggerHeartbeatNowAsync();
            Assert.Equal(HeartbeatOutcomeKind.InvalidCredential, r.Outcome);
            Assert.Equal(DeviceAuthorizationStatus.CredentialInvalid, supCorrupt.Snapshot.AuthorizationStatus);
            _ = Assert.Throws<DeviceNotAuthorizedException>(() => supCorrupt.EnsureDeviceAuthorized());
        }

        // Case C: Disposed credential passed directly to HeartbeatClient
        using var disposedCred = CreateCredential();
        disposedCred.Dispose();
        var directDisposed = await client.SendHeartbeatAsync(disposedCred);
        Assert.Equal(HeartbeatOutcomeKind.InvalidCredential, directDisposed.Outcome);
    }

    // 8. Server-confirmed revocation (401 { "error": "unauthorized" } and 403 { "error": "revoked" })
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":"revoked"}""")]
    public async Task ServerConfirmedRevocationHaltsLoopDeletesCredentialAndBlocksRestart(
        HttpStatusCode statusCode,
        string responseJson)
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var networkCalls = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            _ = Interlocked.Increment(ref networkCalls);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);
        await using var supervisor = new HeartbeatSupervisor(store, client, new InMemoryRevocationTombstoneStore());

        var revokedSignal = new TaskCompletionSource<DeviceRevokedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.RevocationConfirmed += (sender, args) => _ = revokedSignal.TrySetResult(args);

        await supervisor.StartAsync();
        var args = await revokedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DeviceIdA, args.DeviceId);
        Assert.True(args.CredentialPurgeSucceeded);
        Assert.True(args.TombstonePersisted);
        await WaitUntilAsync(async () => !await store.ExistsAsync());

        // Loop must halt cleanly after single confirmed revocation without auto-reconnecting.
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref networkCalls));
        Assert.Equal(DeviceAuthorizationStatus.Revoked, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(HeartbeatConnectivityState.Revoked, supervisor.Snapshot.ConnectivityState);
        Assert.True(supervisor.Snapshot.IsRevoked);
        Assert.False(supervisor.TryGetAuthorizedDeviceId(out _));
        _ = Assert.Throws<DeviceRevokedException>(() => supervisor.EnsureDeviceAuthorized());
        _ = await Assert.ThrowsAsync<DeviceRevokedException>(() => supervisor.StartAsync());
    }

    // 9. Repeated revocation responses
    [Fact]
    public async Task RepeatedRevocationResponsesRemainIdempotentAndDoNotTouchNetworkOnceLatched()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var networkCalls = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            _ = Interlocked.Increment(ref networkCalls);
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            client,
            new InMemoryRevocationTombstoneStore(),
            new HeartbeatSupervisorOptions { DeleteLocalCredentialOnRevocation = false });

        var eventCount = 0;
        supervisor.RevocationConfirmed += (_, _) => _ = Interlocked.Increment(ref eventCount);

        var first = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(first.IsConfirmedRevocation);

        var second = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.Revoked, second.Outcome);

        // External repeated revocation signal is also safely idempotent.
        await supervisor.NotifyAuthorizationRevokedAsync(DeviceIdA, "Duplicate signal");

        Assert.Equal(1, Volatile.Read(ref networkCalls));
        Assert.Equal(1, Volatile.Read(ref eventCount));
        Assert.True(supervisor.Snapshot.IsRevoked);
    }

    // 10. Revocation during active operations
    [Fact]
    public async Task RevocationDuringActiveSessionTerminatesSessionResetsInputsAndDeniesFurtherInput()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var sessionMachine = new SessionStateMachine();
        var approval = sessionMachine.BeginSession(
            new SessionApprovalRequest(Guid.NewGuid(), DeviceIdA, PermissionMode.FullControl, false, DateTimeOffset.UtcNow),
            new CompanionOptions { ServerOrigin = TrustedOrigin },
            ownerExplicitlyApproved: true);
        Assert.True(approval.Approved);

        var inputExecutor = new RecordingInputExecutor();
        var evaluator = new InputPermissionEvaluator();

        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore(),
            sessionStateMachine: sessionMachine,
            inputExecutor: inputExecutor);

        Assert.True(evaluator.Evaluate(sessionMachine.State, sessionMachine.Mode, new KeyInputEvent("KeyA", InputAction.Down)).Allowed);

        var res = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.Revoked, res.Outcome);

        Assert.Equal(SessionState.Terminated, sessionMachine.State);
        Assert.Null(sessionMachine.ActiveSessionId);
        Assert.Equal(1, inputExecutor.ResetCount);
        Assert.Equal(InputResetReason.SessionDisconnected, inputExecutor.LastResetReason);

        var postRevokeDecision = evaluator.Evaluate(
            sessionMachine.State,
            sessionMachine.Mode,
            new KeyInputEvent("KeyA", InputAction.Down));
        Assert.False(postRevokeDecision.Allowed);
        Assert.Equal(InputRejectionReason.SessionNotActive, postRevokeDecision.Reason);
        _ = Assert.Throws<DeviceRevokedException>(() => supervisor.EnsureDeviceAuthorized());
    }

    // 11. Cancellation during in-flight heartbeat
    [Fact]
    public async Task CancellationDuringInFlightHeartbeatIsDistinguishedFromNetworkFailure()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var inFlightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new AsyncStubHttpMessageHandler(async (unusedReq, ct) =>
        {
            _ = inFlightStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return OkJsonResponse(DateTimeOffset.UtcNow);
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore());

        using var cts = new CancellationTokenSource();
        var task = supervisor.TriggerHeartbeatNowAsync(cts.Token);
        await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        var result = await task;

        Assert.Equal(HeartbeatOutcomeKind.Cancelled, result.Outcome);
        Assert.Equal(0, supervisor.Snapshot.ConsecutiveTransientFailures);
        Assert.Equal(0, supervisor.Snapshot.TotalTransientFailureCount);
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(supervisor.Snapshot.IsAuthorized);
    }

    // 12. Clean application shutdown
    [Fact]
    public async Task CleanShutdownStopsBackgroundLoopPromptlyAndSafely()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var beatSignal = new AsyncSignal();
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            beatSignal.Signal();
            return OkJsonResponse(timeProvider.GetUtcNow());
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient, timeProvider: timeProvider),
            new InMemoryRevocationTombstoneStore(),
            timeProvider: timeProvider);

        await supervisor.StartAsync();
        await beatSignal.WaitForCountAsync(1);
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);

        await supervisor.StopAsync();
        Assert.Equal(HeartbeatConnectivityState.Stopped, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(0, timeProvider.ActiveTimerCount);

        await supervisor.DisposeAsync();
        _ = Assert.Throws<ObjectDisposedException>(() => supervisor.EnsureDeviceAuthorized());
    }

    // 13. Concurrent heartbeat and StartAsync attempts
    [Fact]
    public async Task ConcurrentStartAndTriggerAttemptsPreventDuplicateLoopsAndSerializeExecution()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var concurrentInFlight = 0;
        var maxObservedConcurrency = 0;
        var totalRequests = 0;
        var requestEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var handler = new AsyncStubHttpMessageHandler(async (_, _) =>
        {
            var current = Interlocked.Increment(ref concurrentInFlight);
            var snapshotMax = Volatile.Read(ref maxObservedConcurrency);
            if (current > snapshotMax)
            {
                _ = Interlocked.CompareExchange(ref maxObservedConcurrency, current, snapshotMax);
            }

            _ = Interlocked.Increment(ref totalRequests);
            _ = requestEntered.TrySetResult();
            await releaseRequest.Task.ConfigureAwait(false);

            _ = Interlocked.Decrement(ref concurrentInFlight);
            return OkJsonResponse(timeProvider.GetUtcNow());
        });

        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient, timeProvider: timeProvider),
            new InMemoryRevocationTombstoneStore(),
            new HeartbeatSupervisorOptions { HeartbeatInterval = TimeSpan.FromSeconds(60) },
            timeProvider: timeProvider);

        // Concurrent StartAsync calls must create strictly one background loop.
        await Task.WhenAll(
            supervisor.StartAsync(),
            supervisor.StartAsync(),
            supervisor.StartAsync());

        await requestEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Fire concurrent manual triggers while first loop tick is in flight.
        var manual1 = supervisor.TriggerHeartbeatNowAsync();
        var manual2 = supervisor.TriggerHeartbeatNowAsync();

        await Task.Delay(30);
        Assert.Equal(1, Volatile.Read(ref maxObservedConcurrency));
        Assert.Equal(1, Volatile.Read(ref totalRequests));

        _ = releaseRequest.TrySetResult();
        var results = await Task.WhenAll(manual1, manual2);
        Assert.All(results, r => Assert.True(r.Succeeded));
        Assert.Equal(1, Volatile.Read(ref maxObservedConcurrency));

        await supervisor.StopAsync();
    }

    // 14. Stale heartbeat responses arriving after revocation or re-pairing
    [Fact]
    public async Task StaleOkAndStaleRevocationResponsesAreIgnoredAcrossRevocationAndRepairing()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var call1Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call1Release = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var call2Release = new TaskCompletionSource<HttpStatusCode>(TaskCreationOptions.RunContinuationsAsynchronously);

        var callIndex = 0;
        var stubClient = new DelegateHeartbeatClient(async (cred, unusedCt) =>
        {
            var idx = Interlocked.Increment(ref callIndex);
            var tcs = idx == 1 ? call1Release : call2Release;
            if (idx == 1)
            {
                _ = call1Started.TrySetResult();
            }

            var status = await tcs.Task.ConfigureAwait(false);
            return status == HttpStatusCode.OK
                ? HeartbeatAttemptResult.Success(cred.Metadata.DeviceId, DateTimeOffset.UtcNow)
                : HeartbeatAttemptResult.Failure(HeartbeatOutcomeKind.Revoked, "revoked", cred.Metadata.DeviceId, 401, "unauthorized");
        });

        await using var supervisor = new HeartbeatSupervisor(
            store,
            stubClient,
            new InMemoryRevocationTombstoneStore());

        // Scenario A: Stale 200 OK arriving AFTER revocation must NOT restore Authorized state.
        var inFlight1 = supervisor.TriggerHeartbeatNowAsync();
        await call1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // ARGON-3.4-001: While beat #1 is in flight, credential is only PendingVerification (never Authorized).
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());

        await supervisor.NotifyAuthorizationRevokedAsync(DeviceIdA, "Revoked while beat #1 in flight");
        Assert.True(supervisor.Snapshot.IsRevoked);

        _ = call1Release.TrySetResult(HttpStatusCode.OK);
        _ = await inFlight1;

        Assert.True(supervisor.Snapshot.IsRevoked);
        Assert.Equal(0, supervisor.Snapshot.TotalSucceededCount);
        _ = Assert.Throws<DeviceRevokedException>(() => supervisor.EnsureDeviceAuthorized());

        // Scenario B: Fresh re-pair to DeviceIdB enters PendingVerification until its heartbeat succeeds.
        await SaveSeedCredentialAsync(store, DeviceIdB, TokenB);
        await supervisor.AcknowledgeFreshPairingAsync();
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());

        _ = call2Release.TrySetResult(HttpStatusCode.OK);
        var resB = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(resB.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());

        // Even a late external revocation notification targeting old DeviceIdA leaves DeviceIdB Authorized.
        await supervisor.NotifyAuthorizationRevokedAsync(DeviceIdA, "Late revocation for old DeviceIdA");
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());
    }

    // 15. Invalid, malformed, oversized, and redirect responses
    [Theory]
    [InlineData(HttpStatusCode.OK, """{"ok":false,"server_time":"2026-11-18T12:00:00Z"}""", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.OK, """{"ok":true}""", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.OK, """{"ok":true,"server_time":"invalid-date"}""", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.OK, """{"ok":"true","server_time":"2026-11-18T12:00:00Z"}""", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.OK, "<html>captive portal login</html>", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.OK, """{"ok":true,"server_time":"2026-11-18T12:00:00Z"} trailing""", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.Unauthorized, "<html>proxy auth required</html>", HeartbeatOutcomeKind.MalformedResponse)]
    [InlineData(HttpStatusCode.Found, "", HeartbeatOutcomeKind.UnexpectedRedirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "", HeartbeatOutcomeKind.UnexpectedRedirect)]
    public async Task RejectsMalformedRedirectAndCaptivePortalResponsesWithoutFalseSuccessOrCredentialWipe(
        HttpStatusCode statusCode,
        string body,
        HeartbeatOutcomeKind expectedOutcome)
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);
        await using var supervisor = new HeartbeatSupervisor(store, client, new InMemoryRevocationTombstoneStore());

        var result = await supervisor.TriggerHeartbeatNowAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(0, supervisor.Snapshot.TotalSucceededCount);
        // Captive portal / redirect / malformed 200 must never wipe stored DPAPI pairing credential
        // and must never prematurely promote PendingVerification -> Authorized.
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(supervisor.Snapshot.IsAuthorized);
        Assert.True(await store.ExistsAsync());
    }

    [Fact]
    public async Task RejectsOversizedHeartbeatResponsePayload()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var oversized = new string('a', 5000);
        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"ok":true,"pad":"{{oversized}}"}""", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient, new HeartbeatClientOptions { MaxResponseBytes = 4096 });
        await using var supervisor = new HeartbeatSupervisor(store, client, new InMemoryRevocationTombstoneStore());

        var result = await supervisor.TriggerHeartbeatNowAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.MalformedResponse, result.Outcome);
    }

    // 16. Credential expiration / deletion & fresh re-pairing restoration flow
    [Fact]
    public async Task FreshPairingIsRequiredAfterRevocationAndOldDeviceIdRemainsBlocked()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        using var handler = new StubHttpMessageHandler((req, unusedBody) =>
        {
            var token = req.Headers.Authorization?.Parameter;
            if (token == TokenA)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
                };
            }

            if (token == TokenB)
            {
                return OkJsonResponse(timeProvider.GetUtcNow());
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient, timeProvider: timeProvider),
            new InMemoryRevocationTombstoneStore(),
            timeProvider: timeProvider);

        // 1. Device A revoked -> local credential deleted.
        var revoked = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.Revoked, revoked.Outcome);
        Assert.False(await store.ExistsAsync());

        // 2. Attempting to restore access with previously revoked DeviceIdA must still fail.
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenB);
        _ = await Assert.ThrowsAsync<DeviceRevokedException>(() => supervisor.AcknowledgeFreshPairingAsync());
        Assert.True(supervisor.Snapshot.IsRevoked);

        // 3. Real fresh pairing via PairingClient -> confirm_pairing issues new DeviceIdB + TokenB.
        using var pairingHandler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"device_id":"{{DeviceIdB}}","device_token":"{{TokenB}}"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var pairingHttp = new HttpClient(pairingHandler);
        var pairingClient = new PairingClient(
            pairingHttp,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        var pairResult = await pairingClient.PairWithPinAsync(
            "482910".AsMemory(),
            new PairingDeviceMetadata("WORKSTATION-01", "Windows 11", "0.3.4"),
            overwriteExistingCredential: true);
        Assert.True(pairResult.Succeeded);

        // 4. Acknowledge fresh pairing and restart supervisor loop cleanly.
        await supervisor.AcknowledgeFreshPairingAsync(startSupervisorLoop: true);
        await WaitUntilAsync(() => supervisor.Snapshot.ConnectivityState == HeartbeatConnectivityState.Healthy);

        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());
        Assert.Equal(1, supervisor.Snapshot.TotalSucceededCount);

        await supervisor.StopAsync();
    }

    // Cross-language fixture contract parity
    [Fact]
    public async Task SharedCrossLanguageHeartbeatFixtureMatchesHeartbeatClientContract()
    {
        Assert.True(File.Exists(SharedFixturePath), $"Missing shared heartbeat fixture at '{SharedFixturePath}'.");
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFixturePath));
        var root = doc.RootElement;

        Assert.Equal(HeartbeatClient.HeartbeatEndpointPath, root.GetProperty("endpointPath").GetString());
        Assert.Equal("POST", root.GetProperty("method").GetString());
        Assert.Equal("no-store", root.GetProperty("requiredResponseHeaders").GetProperty("cacheControl").GetString());
        Assert.True(new HeartbeatSupervisorOptions().HeartbeatInterval.TotalMilliseconds < root.GetProperty("onlineWindowMs").GetInt32());

        using var cred = CreateCredential();

        foreach (var entry in root.GetProperty("valid200Responses").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
            using var httpClient = new HttpClient(handler);
            var res = await new HeartbeatClient(httpClient).SendHeartbeatAsync(cred);
            Assert.True(res.Succeeded, $"Expected valid fixture '{entry.GetProperty("name").GetString()}': {res.Detail}");
        }

        foreach (var entry in root.GetProperty("invalid200Responses").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
            using var httpClient = new HttpClient(handler);
            var res = await new HeartbeatClient(httpClient).SendHeartbeatAsync(cred);
            Assert.False(res.Succeeded, $"Expected invalid fixture '{entry.GetProperty("name").GetString()}'.");
            Assert.Equal(HeartbeatOutcomeKind.MalformedResponse, res.Outcome);
        }

        foreach (var codeElement in root.GetProperty("recognizedRevocationErrorCodes").EnumerateArray())
        {
            var code = codeElement.GetString()!;
            var payload = $$"""{"error":"{{code}}"}""";
            using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
            using var httpClient = new HttpClient(handler);
            var res = await new HeartbeatClient(httpClient).SendHeartbeatAsync(cred);
            Assert.Equal(HeartbeatOutcomeKind.Revoked, res.Outcome);
            Assert.Equal(code, res.ServerErrorCode);
        }

        foreach (var untrusted in root.GetProperty("untrusted401Responses").EnumerateArray())
        {
            var body = untrusted.GetProperty("body").GetString()!;
            using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            using var httpClient = new HttpClient(handler);
            var res = await new HeartbeatClient(httpClient).SendHeartbeatAsync(cred);
            Assert.Equal(HeartbeatOutcomeKind.MalformedResponse, res.Outcome);
        }

        var revJson = root.GetProperty("revocation401Response").GetString()!;
        using (var revHandler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(revJson, Encoding.UTF8, "application/json"),
        }))
        using (var revHttp = new HttpClient(revHandler))
        {
            var revRes = await new HeartbeatClient(revHttp).SendHeartbeatAsync(cred);
            Assert.Equal(HeartbeatOutcomeKind.Revoked, revRes.Outcome);
            Assert.Equal("unauthorized", revRes.ServerErrorCode);
        }

        var errJson = root.GetProperty("serverError500Response").GetString()!;
        using (var errHandler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent(errJson, Encoding.UTF8, "application/json"),
        }))
        using (var errHttp = new HttpClient(errHandler))
        {
            var errRes = await new HeartbeatClient(errHttp).SendHeartbeatAsync(cred);
            Assert.Equal(HeartbeatOutcomeKind.TransientServerError, errRes.Outcome);
            Assert.Equal("server_error", errRes.ServerErrorCode);
        }
    }

    // =========================================================================
    // ARGON FIRST & SECOND SECURITY AUDIT REMEDIATION — ADVERSARIAL TESTS
    // =========================================================================

    // Scenarios 1 & 2 (ARGON-3.4-001):
    // 1. Startup never authorizes a credential before server verification.
    // 2. Fresh pairing (AcknowledgeFreshPairingAsync) remains unverified until a successful heartbeat.
    [Fact]
    public async Task Argon34001StartupAndFreshPairingRemainPendingVerificationUntilServerHeartbeatSucceeds()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var inFlightEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstBeat = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        var attempt = 0;
        using var handler = new AsyncStubHttpMessageHandler(async (_, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                _ = inFlightEntered.TrySetResult();
                return await releaseFirstBeat.Task.ConfigureAwait(false);
            }

            return OkJsonResponse(DateTimeOffset.UtcNow);
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore());

        // Before any heartbeat: Unpaired, not authorized.
        Assert.Equal(DeviceAuthorizationStatus.Unpaired, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(supervisor.Snapshot.IsAuthorized);
        Assert.False(supervisor.TryGetAuthorizedDeviceId(out _));
        _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());

        // Scenario 1: While first heartbeat is in flight (or if first heartbeat fails with 503),
        // status must be PendingVerification and EnsureDeviceAuthorized / TryGetAuthorizedDeviceId MUST reject!
        var beat1Task = supervisor.TriggerHeartbeatNowAsync();
        await inFlightEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.True(supervisor.Snapshot.IsPendingVerification);
        Assert.False(supervisor.Snapshot.IsAuthorized);
        Assert.False(supervisor.TryGetAuthorizedDeviceId(out _));
        var pendingEx = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, pendingEx.AuthorizationStatus);

        _ = releaseFirstBeat.TrySetResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        var beat1Result = await beat1Task;
        Assert.False(beat1Result.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(supervisor.TryGetAuthorizedDeviceId(out _));
        _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());

        // Scenario 2: AcknowledgeFreshPairingAsync with a newly saved credential also transitions
        // to PendingVerification (NOT Authorized) until a valid HTTP 200 heartbeat succeeds.
        await SaveSeedCredentialAsync(store, DeviceIdB, TokenB);
        await supervisor.AcknowledgeFreshPairingAsync(startSupervisorLoop: false);

        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(supervisor.Snapshot.IsAuthorized);
        Assert.False(supervisor.TryGetAuthorizedDeviceId(out _));
        _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());

        // Only after a valid 200 OK heartbeat does the supervisor grant operational authorization.
        var beat2Result = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(beat2Result.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.True(supervisor.Snapshot.IsAuthorized);
        Assert.True(supervisor.TryGetAuthorizedDeviceId(out var authorizedId));
        Assert.Equal(DeviceIdB, authorizedId);
        Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());
    }

    // Scenarios 3, 4, 19 & 20 (ARGON-3.4-002):
    // 3. Failed DPAPI deletion cannot restore authorization after restart.
    // 4. Persistent revocation tombstone blocks the same DeviceId across restarts.
    // 19. Fresh pairing with a new DeviceId works.
    // 20. Old revoked DeviceId cannot regain authorization.
    [Fact]
    public async Task Argon34002FailedCredentialDeletionAndProcessRestartRemainBlockedByDurableTombstoneUntilNewDeviceIdPaired()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeaconStep34_Tombstone_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.dpapi");
            var tombstonePath = Path.Combine(tempDir, "revoked-devices.dpapi");

            using var realDpapiCredStore = new DpapiCredentialStore(credPath);
            await SaveSeedCredentialAsync(realDpapiCredStore, DeviceIdA, TokenA);

            // Wrap real DPAPI credential store to simulate an I/O failure on DeleteIfMatchesAsync during revocation.
            using var failingDeleteCredStore = new FailingDeleteCredentialStoreWrapper(realDpapiCredStore, failDeletes: true);
            using var dpapiTombstoneStore1 = new DpapiRevocationTombstoneStore(tombstonePath);

            using var handler1 = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
            });
            using var http1 = new HttpClient(handler1);

            DeviceRevokedEventArgs? capturedRevokedEvent = null;
            await using (var supervisor1 = WindowsHeartbeatSupervisorFactory.Create(
                failingDeleteCredStore,
                new HeartbeatClient(http1),
                dpapiTombstoneStore1,
                new HeartbeatSupervisorOptions
                {
                    CredentialDeleteMaxAttempts = 3,
                    CredentialDeleteRetryDelay = TimeSpan.FromMilliseconds(5),
                }))
            {
                supervisor1.RevocationConfirmed += (sender, e) => capturedRevokedEvent = e;

                var revokeRes = await supervisor1.TriggerHeartbeatNowAsync();
                Assert.Equal(HeartbeatOutcomeKind.Revoked, revokeRes.Outcome);
                Assert.True(supervisor1.Snapshot.IsRevoked);
                Assert.NotNull(capturedRevokedEvent);

                // Verify bounded retries ran (3 attempts) and telemetry reported purge failure + tombstone success.
                Assert.Equal(3, failingDeleteCredStore.DeleteAttemptCount);
                Assert.False(capturedRevokedEvent!.CredentialPurgeSucceeded);
                Assert.True(capturedRevokedEvent.TombstonePersisted);
                Assert.False(supervisor1.Snapshot.CredentialPurgeSucceeded);
                Assert.True(supervisor1.Snapshot.TombstonePersisted);
                Assert.NotNull(supervisor1.Snapshot.CleanupErrorDetail);
            }

            // Because DeleteIfMatchesAsync failed, credentials.dpapi still exists on disk with DeviceIdA.
            Assert.True(await realDpapiCredStore.ExistsAsync());
            Assert.True(File.Exists(tombstonePath));

            // Simulate a full Windows process restart: instantiate a brand-new DpapiRevocationTombstoneStore
            // and a brand-new HeartbeatSupervisor via WindowsHeartbeatSupervisorFactory pointing at the surviving credentials.dpapi file.
            using var dpapiTombstoneStore2 = new DpapiRevocationTombstoneStore(tombstonePath);
            Assert.True(await dpapiTombstoneStore2.IsRevokedAsync(DeviceIdA));

            var networkCallsAfterRestart = 0;
            using var handler2 = new StubHttpMessageHandler((req, unusedBody) =>
            {
                _ = Interlocked.Increment(ref networkCallsAfterRestart);
                var token = req.Headers.Authorization?.Parameter;
                return token == TokenB
                    ? OkJsonResponse(DateTimeOffset.UtcNow)
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
                    };
            });
            using var http2 = new HttpClient(handler2);

            await using var supervisorAfterRestart = WindowsHeartbeatSupervisorFactory.Create(
                realDpapiCredStore,
                new HeartbeatClient(http2),
                dpapiTombstoneStore2);

            // Scenario 3 & 4: New supervisor after restart immediately blocks DeviceIdA via the DPAPI tombstone
            // WITHOUT contacting the network and purges the lingering credential file!
            var restartBeat = await supervisorAfterRestart.TriggerHeartbeatNowAsync();
            Assert.Equal(HeartbeatOutcomeKind.Revoked, restartBeat.Outcome);
            Assert.Equal(0, Volatile.Read(ref networkCallsAfterRestart));
            Assert.True(supervisorAfterRestart.Snapshot.IsRevoked);
            Assert.False(await realDpapiCredStore.ExistsAsync());

            // Scenario 20: Even if old DeviceIdA is re-saved to DPAPI storage, AcknowledgeFreshPairingAsync rejects it.
            await SaveSeedCredentialAsync(realDpapiCredStore, DeviceIdA, TokenB);
            _ = await Assert.ThrowsAsync<DeviceRevokedException>(() =>
                supervisorAfterRestart.AcknowledgeFreshPairingAsync());

            // Scenario 19: Fresh pairing with a genuinely new DeviceIdB succeeds and reaches Authorized after 200 OK.
            await SaveSeedCredentialAsync(realDpapiCredStore, DeviceIdB, TokenB);
            await supervisorAfterRestart.AcknowledgeFreshPairingAsync();
            Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisorAfterRestart.Snapshot.AuthorizationStatus);

            var freshBeat = await supervisorAfterRestart.TriggerHeartbeatNowAsync();
            Assert.True(freshBeat.Succeeded);
            Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisorAfterRestart.Snapshot.AuthorizationStatus);
            Assert.Equal(DeviceIdB, supervisorAfterRestart.EnsureDeviceAuthorized());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Argon34002CorruptedDpapiRevocationTombstoneFileFailsClosed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeaconStep34_TombstoneCorrupt_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tombstonePath = Path.Combine(tempDir, "revoked-devices.dpapi");
            using var tombstoneStore = new DpapiRevocationTombstoneStore(tombstonePath);
            await tombstoneStore.RecordRevokedAsync(DeviceIdA, DateTimeOffset.UtcNow);

            // Tamper with the tombstone file on disk.
            var bytes = await File.ReadAllBytesAsync(tombstonePath);
            bytes[^5] ^= 0xFF;
            await File.WriteAllBytesAsync(tombstonePath, bytes);

            _ = await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() =>
                tombstoneStore.IsRevokedAsync(DeviceIdB));

            using var credStore = new InMemoryCredentialStore();
            await SaveSeedCredentialAsync(credStore, DeviceIdB, TokenB);
            using var handler = new StubHttpMessageHandler((_, _) => OkJsonResponse(DateTimeOffset.UtcNow));
            using var http = new HttpClient(handler);
            await using var supervisor = WindowsHeartbeatSupervisorFactory.Create(
                credStore,
                new HeartbeatClient(http),
                tombstoneStore);

            // Supervisor must fail closed (InvalidCredential / CredentialInvalid) if the tombstone file is tampered with.
            var res = await supervisor.TriggerHeartbeatNowAsync();
            Assert.False(res.Succeeded);
            Assert.Equal(HeartbeatOutcomeKind.InvalidCredential, res.Outcome);
            Assert.Equal(DeviceAuthorizationStatus.CredentialInvalid, supervisor.Snapshot.AuthorizationStatus);
            Assert.False(supervisor.Snapshot.IsAuthorized);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // Scenarios 5, 6, 7, 8, 9, 10 & 11 (ARGON-3.4-003 & ARGON-3.4-004):
    [Theory]
    [InlineData("")]
    [InlineData("   \t\r\n  ")]
    [InlineData("{}")]
    [InlineData("""{"error":""}""")]
    [InlineData("""{"error":"   "}""")]
    [InlineData("""{"error":null}""")]
    [InlineData("""{"error":401}""")]
    [InlineData("""{"error":"proxy_auth_required"}""")]
    [InlineData("""{"error":"gateway_timeout"}""")]
    [InlineData("""{"error":"unauthorized","error":"proxy_override"}""")]
    [InlineData("""{"error":"proxy_override","error":"unauthorized"}""")]
    [InlineData("""{"error":"unauthorized"} trailing""")]
    public async Task Argon34003And004UntrustedOrMalformed401ResponsesDoNotRevokeOrDeleteCredentials(
        string untrusted401Body)
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var attempt = 0;
        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            var current = Interlocked.Increment(ref attempt);
            if (current == 1)
            {
                return OkJsonResponse(DateTimeOffset.UtcNow);
            }

            if (current == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(untrusted401Body, Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore());

        // 1. Initial verification succeeds.
        var okRes = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(okRes.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);

        // 2. Untrusted/proxy HTTP 401 (with application/json) is classified as MalformedResponse (transient) and preserves credentials.
        var untrustedRes = await supervisor.TriggerHeartbeatNowAsync();
        Assert.False(untrustedRes.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.MalformedResponse, untrustedRes.Outcome);
        Assert.False(untrustedRes.IsConfirmedRevocation);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.True(await store.ExistsAsync());

        // 2b. Even a verbatim {"error":"unauthorized"} body returned with a non-JSON Content-Type (e.g. text/plain or text/html from a proxy)
        // must be classified as MalformedResponse and must never trigger credential deletion.
        using (var wrongCtHandler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "text/plain"),
        }))
        using (var wrongCtHttp = new HttpClient(wrongCtHandler))
        {
            var wrongCtRes = await new HeartbeatClient(wrongCtHttp).SendHeartbeatAsync(CreateCredential());
            Assert.False(wrongCtRes.Succeeded);
            Assert.Equal(HeartbeatOutcomeKind.MalformedResponse, wrongCtRes.Outcome);
            Assert.False(wrongCtRes.IsConfirmedRevocation);
        }

        // 3. Scenario 11: Recognized backend {"error":"unauthorized"} with application/json triggers confirmed revocation and deletes credential.
        var confirmedRes = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.Revoked, confirmedRes.Outcome);
        Assert.True(confirmedRes.IsConfirmedRevocation);
        Assert.Equal("unauthorized", confirmedRes.ServerErrorCode);
        Assert.Equal(DeviceAuthorizationStatus.Revoked, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(await store.ExistsAsync());
    }

    // Scenarios 12 & 13 (ARGON-3.4-005):
    [Theory]
    [InlineData(SessionState.ActiveFullControl)]
    [InlineData(SessionState.ActiveViewOnly)]
    [InlineData(SessionState.Paused)]
    [InlineData(SessionState.LockedOrUnavailable)]
    [InlineData(SessionState.AwaitingOwnerApproval)]
    public async Task Argon34005RevocationUnconditionallyResetsInputsAcrossAllSessionStates(
        SessionState targetSessionState)
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var sessionMachine = CreateSessionMachineInState(targetSessionState, DeviceIdA);
        Assert.Equal(targetSessionState, sessionMachine.State);

        var inputExecutor = new RecordingInputExecutor();
        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            new InMemoryRevocationTombstoneStore(),
            sessionStateMachine: sessionMachine,
            inputExecutor: inputExecutor);

        var res = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.Revoked, res.Outcome);
        Assert.Equal(SessionState.Terminated, sessionMachine.State);
        Assert.Equal(1, inputExecutor.ResetCount);
        Assert.Equal(InputResetReason.SessionDisconnected, inputExecutor.LastResetReason);
        Assert.True(supervisor.Snapshot.InputResetSucceeded);
    }

    [Fact]
    public async Task Argon34005CleanupFailureDuringRevocationNeverRestoresAuthorization()
    {
        using var store = new InMemoryCredentialStore { FailDelete = true };
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var throwingInputExecutor = new ThrowingInputExecutor();
        var failingTombstoneStore = new FailingTombstoneStore();

        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(httpClient),
            failingTombstoneStore,
            new HeartbeatSupervisorOptions
            {
                CredentialDeleteMaxAttempts = 2,
                CredentialDeleteRetryDelay = TimeSpan.Zero,
            },
            inputExecutor: throwingInputExecutor);

        DeviceRevokedEventArgs? revokedEvent = null;
        supervisor.RevocationConfirmed += (sender, e) => revokedEvent = e;

        var res = await supervisor.TriggerHeartbeatNowAsync();
        Assert.Equal(HeartbeatOutcomeKind.Revoked, res.Outcome);
        Assert.True(supervisor.Snapshot.IsRevoked);
        Assert.False(supervisor.Snapshot.IsAuthorized);
        Assert.NotNull(revokedEvent);
        Assert.False(revokedEvent!.CredentialPurgeSucceeded);
        Assert.False(revokedEvent.TombstonePersisted);
        Assert.False(revokedEvent.InputResetSucceeded);
        Assert.NotNull(revokedEvent.CleanupErrorDetail);
        _ = Assert.Throws<DeviceRevokedException>(() => supervisor.EnsureDeviceAuthorized());
    }

    // Scenarios 14, 15 & 16 (ARGON-3.4-006):
    [Fact]
    public async Task Argon34006DisposeAndDisposeAsyncDuringManualAndBackgroundHeartbeatsAreRaceFree()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        // Scenario 14: Synchronous Dispose() while manual TriggerHeartbeatNowAsync is in flight.
        var manualEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var handler1 = new AsyncStubHttpMessageHandler(async (unusedReq, ct) =>
        {
            _ = manualEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return OkJsonResponse(DateTimeOffset.UtcNow);
        }))
        using (var http1 = new HttpClient(handler1))
        {
            var sup1 = new HeartbeatSupervisor(store, new HeartbeatClient(http1), new InMemoryRevocationTombstoneStore());
            var inFlightManual = sup1.TriggerHeartbeatNowAsync();
            await manualEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            sup1.Dispose();

            var manualOutcome = await inFlightManual.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HeartbeatOutcomeKind.Cancelled, manualOutcome.Outcome);
        }

        // Scenario 15 & 16: Concurrent Dispose() and DisposeAsync() while background heartbeat loop is in flight.
        var bgEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var handler2 = new AsyncStubHttpMessageHandler(async (unusedReq, ct) =>
        {
            _ = bgEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            return OkJsonResponse(DateTimeOffset.UtcNow);
        }))
        using (var http2 = new HttpClient(handler2))
        {
            var sup2 = new HeartbeatSupervisor(store, new HeartbeatClient(http2), new InMemoryRevocationTombstoneStore());
            await sup2.StartAsync();
            await bgEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var d1 = Task.Run(() => sup2.Dispose());
            var d2 = sup2.DisposeAsync().AsTask();
            var d3 = sup2.DisposeAsync().AsTask();

            await Task.WhenAll(d1, d2, d3).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    // Scenarios 17 & 18 (ARGON-3.4-007):
    [Fact]
    public async Task Argon34007StartAsyncAwaitsCancellingLoopAndStartsSingleReplacementLoop()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var loop1InFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoop1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loop2Beat = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var callCount = 0;
        var activeRequests = 0;
        var maxConcurrentRequests = 0;

        var stubClient = new DelegateHeartbeatClient(async (cred, unusedCt) =>
        {
            var currentActive = Interlocked.Increment(ref activeRequests);
            var prevMax = Volatile.Read(ref maxConcurrentRequests);
            if (currentActive > prevMax)
            {
                _ = Interlocked.CompareExchange(ref maxConcurrentRequests, currentActive, prevMax);
            }

            try
            {
                var idx = Interlocked.Increment(ref callCount);
                if (idx == 1)
                {
                    _ = loop1InFlight.TrySetResult();
                    await releaseLoop1.Task.ConfigureAwait(false);
                }
                else
                {
                    _ = loop2Beat.TrySetResult();
                }

                return HeartbeatAttemptResult.Success(cred.Metadata.DeviceId, DateTimeOffset.UtcNow);
            }
            finally
            {
                _ = Interlocked.Decrement(ref activeRequests);
            }
        });

        await using var supervisor = new HeartbeatSupervisor(
            store,
            stubClient,
            new InMemoryRevocationTombstoneStore(),
            new HeartbeatSupervisorOptions { HeartbeatInterval = TimeSpan.FromSeconds(60) });

        await supervisor.StartAsync();
        await loop1InFlight.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var shortStopCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            supervisor.StopAsync(shortStopCts.Token));

        var restart1 = supervisor.StartAsync();
        var restart2 = supervisor.StartAsync();

        _ = releaseLoop1.TrySetResult();
        await Task.WhenAll(restart1, restart2).WaitAsync(TimeSpan.FromSeconds(5));

        await loop2Beat.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref callCount));
        Assert.Equal(1, Volatile.Read(ref maxConcurrentRequests));

        await supervisor.StopAsync();
    }

    // =========================================================================
    // ARGON SECOND-AUDIT REMEDIATION — NEW-001 THROUGH NEW-005 + HTTP HARDENING
    // =========================================================================

    // NEW-001 (Tests 1 & 2):
    // 1. Production factory always injects durable DPAPI tombstone storage.
    // 2. Missing tombstone configuration cannot silently use in-memory storage.
    [Fact]
    public async Task ArgonNew001WindowsFactoryInjectsDurableDpapiTombstoneStoreAndCoreRejectsNullTombstoneStore()
    {
        using var store = new InMemoryCredentialStore();
        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
        });
        using var http = new HttpClient(handler);
        var client = new HeartbeatClient(http);

        // 1. Passing null IRevocationTombstoneStore to HeartbeatSupervisor constructor is rejected (no silent in-memory fallback).
        _ = Assert.Throws<ArgumentNullException>(() =>
            new HeartbeatSupervisor(store, client, tombstoneStore: null!));

        // 2. WindowsHeartbeatSupervisorFactory always wires a durable DpapiRevocationTombstoneStore.
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeaconStep34_Factory_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tombstonePath = Path.Combine(tempDir, "revoked-devices.dpapi");
            using var dpapiTombstone = new DpapiRevocationTombstoneStore(tombstonePath);
            await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

            await using (var supervisor = WindowsHeartbeatSupervisorFactory.Create(store, client, dpapiTombstone))
            {
                var res = await supervisor.TriggerHeartbeatNowAsync();
                Assert.Equal(HeartbeatOutcomeKind.Revoked, res.Outcome);
                Assert.True(supervisor.Snapshot.TombstonePersisted);
            }

            Assert.True(File.Exists(tombstonePath));
            using var verifyTombstoneAfterRestart = new DpapiRevocationTombstoneStore(tombstonePath);
            Assert.True(await verifyTombstoneAfterRestart.IsRevokedAsync(DeviceIdA));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // NEW-002A (Test 3):
    // 3. Corrupted revocation history never gets overwritten as clean history.
    [Fact]
    public async Task ArgonNew002ACorruptedRevocationHistoryLatchesDurableFailClosedLockoutEvenAfterSubsequentRevocation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeaconStep34_CorruptHistory_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var tombstonePath = Path.Combine(tempDir, "revoked-devices.dpapi");
            using var tombstoneStore = new DpapiRevocationTombstoneStore(tombstonePath);

            // 1. Revoke DeviceIdA.
            await tombstoneStore.RecordRevokedAsync(DeviceIdA, DateTimeOffset.UtcNow);
            Assert.True(await tombstoneStore.IsRevokedAsync(DeviceIdA));

            // 2. Tamper/corrupt the tombstone file on disk.
            var corruptedBytes = await File.ReadAllBytesAsync(tombstonePath);
            corruptedBytes[15] ^= 0xAA;
            await File.WriteAllBytesAsync(tombstonePath, corruptedBytes);

            // 3. Subsequent revocation of DeviceIdB occurs while the tombstone file is corrupted.
            // RecordRevokedAsync must NOT reset the ledger to a clean file that forgets DeviceIdA!
            await tombstoneStore.RecordRevokedAsync(DeviceIdB, DateTimeOffset.UtcNow);

            // 4. Even across a brand-new DpapiRevocationTombstoneStore instance (process restart),
            // IsRevokedAsync must throw CredentialStoreCorruptedException for BOTH DeviceIdA and any other DeviceId
            // because the durable InnerFlagHistoryCorruptedLockout bit is set inside the DPAPI payload!
            using var restartedTombstoneStore = new DpapiRevocationTombstoneStore(tombstonePath);
            _ = await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() =>
                restartedTombstoneStore.IsRevokedAsync(DeviceIdA));
            _ = await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() =>
                restartedTombstoneStore.IsRevokedAsync(DeviceIdB));

            // 5. Explicit safe recovery (ResetCorruptedHistoryWithFreshBaselineAsync) retaining known revoked IDs
            // restores normal operation while keeping DeviceIdA and DeviceIdB revoked.
            await restartedTombstoneStore.ResetCorruptedHistoryWithFreshBaselineAsync([DeviceIdA, DeviceIdB]);
            Assert.True(await restartedTombstoneStore.IsRevokedAsync(DeviceIdA));
            Assert.True(await restartedTombstoneStore.IsRevokedAsync(DeviceIdB));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // NEW-002B (Tests 4 & 5):
    // 4. Double persistence/deletion failure never restores operational authorization.
    // 5. Process restart after double failure remains fail-closed.
    [Fact]
    public async Task ArgonNew002BDoublePersistenceAndDeletionFailureRemainsFailClosedBeforeAndAfterProcessRestart()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeaconStep34_DoubleFail_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.dpapi");
            var tombstonePath = Path.Combine(tempDir, "revoked-devices.dpapi");

            using var realDpapiCredStore = new DpapiCredentialStore(credPath);
            await SaveSeedCredentialAsync(realDpapiCredStore, DeviceIdA, TokenA);

            // Simulate double failure: both tombstone persistence (disk full / permission denied) and
            // credential deletion (sharing violation / file lock) fail during revocation.
            using var failingDeleteCredStore = new FailingDeleteCredentialStoreWrapper(realDpapiCredStore, failDeletes: true);
            var failingTombstoneStore = new FailingTombstoneStore();

            using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
            });
            using var http = new HttpClient(handler);

            await using (var supervisor1 = new HeartbeatSupervisor(
                failingDeleteCredStore,
                new HeartbeatClient(http),
                failingTombstoneStore,
                new HeartbeatSupervisorOptions
                {
                    CredentialDeleteMaxAttempts = 2,
                    CredentialDeleteRetryDelay = TimeSpan.Zero,
                }))
            {
                var res = await supervisor1.TriggerHeartbeatNowAsync();
                Assert.Equal(HeartbeatOutcomeKind.Revoked, res.Outcome);
                Assert.True(supervisor1.Snapshot.IsRevoked);
                Assert.False(supervisor1.Snapshot.IsAuthorized);
                Assert.False(supervisor1.Snapshot.CredentialPurgeSucceeded);
                Assert.False(supervisor1.Snapshot.TombstonePersisted);
                Assert.NotNull(supervisor1.Snapshot.CleanupErrorDetail);
                _ = Assert.Throws<DeviceRevokedException>(() => supervisor1.EnsureDeviceAuthorized());
            }

            // Simulate process restart with the surviving credentials.dpapi still on disk.
            using var dpapiTombstoneAfterRestart = new DpapiRevocationTombstoneStore(tombstonePath);
            await using var supervisorAfterRestart = WindowsHeartbeatSupervisorFactory.Create(
                realDpapiCredStore,
                new HeartbeatClient(http),
                dpapiTombstoneAfterRestart);

            // On startup before heartbeat: Unpaired (never authorized).
            Assert.False(supervisorAfterRestart.Snapshot.IsAuthorized);
            Assert.False(supervisorAfterRestart.TryGetAuthorizedDeviceId(out _));
            _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisorAfterRestart.EnsureDeviceAuthorized());

            // When first heartbeat runs after restart, the credential enters PendingVerification (never Authorized)
            // and the server's 401 response immediately revokes it, persists the DPAPI tombstone, and deletes credentials.dpapi!
            var postRestartBeat = await supervisorAfterRestart.TriggerHeartbeatNowAsync();
            Assert.Equal(HeartbeatOutcomeKind.Revoked, postRestartBeat.Outcome);
            Assert.True(supervisorAfterRestart.Snapshot.IsRevoked);
            Assert.False(supervisorAfterRestart.Snapshot.IsAuthorized);
            Assert.True(supervisorAfterRestart.Snapshot.TombstonePersisted);
            Assert.True(supervisorAfterRestart.Snapshot.CredentialPurgeSucceeded);
            Assert.False(await realDpapiCredStore.ExistsAsync());
            Assert.True(await dpapiTombstoneAfterRestart.IsRevokedAsync(DeviceIdA));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // NEW-003 (Tests 6 & 7):
    // 6. Conditional credential deletion protects a concurrently saved new credential.
    // 7. Concurrent pairing and revocation are correctly serialized.
    [Fact]
    public async Task ArgonNew003ConditionalCredentialDeletionAndConcurrentPairingRevocationProtectNewlySavedCredential()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"BeaconStep34_Toctou_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credPath = Path.Combine(tempDir, "credentials.dpapi");
            var tombstonePath = Path.Combine(tempDir, "revoked-devices.dpapi");

            using var dpapiCredStore = new DpapiCredentialStore(credPath);
            using var dpapiTombstoneStore = new DpapiRevocationTombstoneStore(tombstonePath);

            // 1. Direct verification of DpapiCredentialStore.DeleteIfMatchesAsync atomic protection:
            // Save DeviceIdB, then call DeleteIfMatchesAsync(DeviceIdA) -> must return false and preserve DeviceIdB!
            await SaveSeedCredentialAsync(dpapiCredStore, DeviceIdB, TokenB);
            var deletedWrongId = await dpapiCredStore.DeleteIfMatchesAsync(DeviceIdA);
            Assert.False(deletedWrongId);
            using (var surviving = await dpapiCredStore.LoadAsync())
            {
                Assert.NotNull(surviving);
                Assert.Equal(DeviceIdB, surviving!.Metadata.DeviceId);
            }

            // 2. Deterministic concurrency test: intercept DeleteIfMatchesAsync(DeviceIdA) during revocation
            // and save DeviceIdB right before DeleteIfMatchesAsync executes on the underlying DpapiCredentialStore.
            await SaveSeedCredentialAsync(dpapiCredStore, DeviceIdA, TokenA);
            var deleteReachedBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowDeleteToProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            using var racingStore = new RacingDeleteCredentialStoreWrapper(
                dpapiCredStore,
                onBeforeConditionalDelete: async () =>
                {
                    _ = deleteReachedBarrier.TrySetResult();
                    await allowDeleteToProceed.Task.ConfigureAwait(false);
                });

            using var handler = new StubHttpMessageHandler((req, unusedBody) =>
            {
                var token = req.Headers.Authorization?.Parameter;
                return token == TokenB
                    ? OkJsonResponse(DateTimeOffset.UtcNow)
                    : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                    {
                        Content = new StringContent("""{"error":"unauthorized"}""", Encoding.UTF8, "application/json"),
                    };
            });
            using var http = new HttpClient(handler);
            await using var supervisor = WindowsHeartbeatSupervisorFactory.Create(
                racingStore,
                new HeartbeatClient(http),
                dpapiTombstoneStore);

            // Start revocation for DeviceIdA; pause right before DeleteIfMatchesAsync(DeviceIdA) runs.
            var revokeTask = supervisor.NotifyAuthorizationRevokedAsync(DeviceIdA, "Concurrent revocation");
            await deleteReachedBarrier.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // Concurrent fresh pairing saves DeviceIdB into the DPAPI credential store before DeleteIfMatchesAsync(DeviceIdA) runs.
            await SaveSeedCredentialAsync(dpapiCredStore, DeviceIdB, TokenB);
            _ = allowDeleteToProceed.TrySetResult();

            await revokeTask.WaitAsync(TimeSpan.FromSeconds(5));

            // DeviceIdB credential on disk MUST NOT have been deleted by DeviceIdA's revocation cleanup!
            Assert.True(await dpapiCredStore.ExistsAsync());
            using (var loadedAfterRace = await dpapiCredStore.LoadAsync())
            {
                Assert.NotNull(loadedAfterRace);
                Assert.Equal(DeviceIdB, loadedAfterRace!.Metadata.DeviceId);
            }

            // Acknowledge fresh pairing for DeviceIdB and verify it reaches Authorized after heartbeat.
            await supervisor.AcknowledgeFreshPairingAsync();
            Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
            var beatB = await supervisor.TriggerHeartbeatNowAsync();
            Assert.True(beatB.Succeeded);
            Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
            Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    // NEW-004 (Tests 8 & 9):
    // 8. Concurrent disposal and pairing are safe.
    // 9. Concurrent disposal and revocation are safe.
    [Fact]
    public async Task ArgonNew004ConcurrentDisposalDuringPairingAndRevocationDrainsSafelyWithoutObjectDisposedException()
    {
        // Test 8: Concurrent DisposeAsync while AcknowledgeFreshPairingAsync is mid-execution.
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var blockingLoadStore = new BlockingLoadCredentialStore(
            CreateCredential(DeviceIdB, TokenB),
            onLoadAsync: async ct =>
            {
                _ = loadEntered.TrySetResult();
                await releaseLoad.Task.WaitAsync(ct).ConfigureAwait(false);
            });
        using var handler = new StubHttpMessageHandler((_, _) => OkJsonResponse(DateTimeOffset.UtcNow));
        using var http = new HttpClient(handler);

        var supPairing = new HeartbeatSupervisor(
            blockingLoadStore,
            new HeartbeatClient(http),
            new InMemoryRevocationTombstoneStore());

        var pairingTask = supPairing.AcknowledgeFreshPairingAsync(startSupervisorLoop: true);
        await loadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposePairingTask = supPairing.DisposeAsync().AsTask();
        _ = releaseLoad.TrySetResult();

        // Pairing either completes or throws OperationCanceledException/ObjectDisposedException cleanly;
        // DisposeAsync must drain without unhandled exceptions or deadlocks.
        try
        {
            await pairingTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled by concurrent disposal.
        }
        catch (ObjectDisposedException)
        {
            // Expected when supervisor is disposed mid-pairing.
        }

        await disposePairingTask.WaitAsync(TimeSpan.FromSeconds(5));

        // Test 9: Concurrent Dispose() and DisposeAsync() while NotifyAuthorizationRevokedAsync is mid-cleanup.
        var tombstoneWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTombstoneWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var store2 = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store2, DeviceIdA, TokenA);
        var blockingTombstoneStore = new BlockingTombstoneStore(async () =>
        {
            _ = tombstoneWriteEntered.TrySetResult();
            await releaseTombstoneWrite.Task.ConfigureAwait(false);
        });

        var supRevoke = new HeartbeatSupervisor(
            store2,
            new HeartbeatClient(http),
            blockingTombstoneStore);

        var revokeTask = supRevoke.NotifyAuthorizationRevokedAsync(DeviceIdA, "Disposing during revocation");
        await tombstoneWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var syncDisposeTask = Task.Run(() => supRevoke.Dispose());
        var asyncDisposeTask = supRevoke.DisposeAsync().AsTask();

        _ = releaseTombstoneWrite.TrySetResult();
        await Task.WhenAll(revokeTask, syncDisposeTask, asyncDisposeTask).WaitAsync(TimeSpan.FromSeconds(5));
    }

    // NEW-005 (Tests 10, 11 & 12):
    // 10. Re-pairing terminates an old active session.
    // 11. Re-pairing resets all held inputs.
    // 12. PendingVerification remains enforced for the new device.
    [Theory]
    [InlineData(SessionState.ActiveFullControl)]
    [InlineData(SessionState.ActiveViewOnly)]
    [InlineData(SessionState.Paused)]
    [InlineData(SessionState.LockedOrUnavailable)]
    [InlineData(SessionState.AwaitingOwnerApproval)]
    public async Task ArgonNew005RepairingDuringAnyActiveSessionTerminatesOldSessionResetsInputsAndEnforcesPendingVerification(
        SessionState oldSessionState)
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var sessionMachine = CreateSessionMachineInState(oldSessionState, DeviceIdA);
        Assert.Equal(oldSessionState, sessionMachine.State);

        var inputExecutor = new RecordingInputExecutor();
        using var handler = new StubHttpMessageHandler((_, _) => OkJsonResponse(DateTimeOffset.UtcNow));
        using var http = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(
            store,
            new HeartbeatClient(http),
            new InMemoryRevocationTombstoneStore(),
            sessionStateMachine: sessionMachine,
            inputExecutor: inputExecutor);

        // Save new credential for DeviceIdB and acknowledge fresh pairing while DeviceIdA's session is still active.
        await SaveSeedCredentialAsync(store, DeviceIdB, TokenB);
        await supervisor.AcknowledgeFreshPairingAsync(startSupervisorLoop: false);

        // Test 10: Old session is immediately terminated.
        Assert.Equal(SessionState.Terminated, sessionMachine.State);
        Assert.Null(sessionMachine.ActiveSessionId);

        // Test 11: Held inputs are unconditionally reset.
        Assert.Equal(1, inputExecutor.ResetCount);
        Assert.Equal(InputResetReason.SessionDisconnected, inputExecutor.LastResetReason);
        Assert.True(supervisor.Snapshot.InputResetSucceeded);

        // Test 12: New device remains in PendingVerification until its heartbeat succeeds.
        Assert.Equal(DeviceAuthorizationStatus.PendingVerification, supervisor.Snapshot.AuthorizationStatus);
        Assert.False(supervisor.Snapshot.IsAuthorized);
        Assert.False(supervisor.TryGetAuthorizedDeviceId(out _));
        _ = Assert.Throws<DeviceNotAuthorizedException>(() => supervisor.EnsureDeviceAuthorized());

        var beatResult = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(beatResult.Succeeded);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());
    }

    // Requirement 6 (Test 13):
    // 13. Untrusted HTTP error strings are bounded and sanitized.
    [Fact]
    public async Task ArgonHttpDiagnosticHardeningUntrustedProxyErrorStringsAreBoundedAndSanitizedWithoutDeletingCredentials()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store, DeviceIdA, TokenA);

        var overlongError = new string('x', 200);
        var unsafeCharsError = "proxy_error: <script>alert(1)</script>\r\nInjected-Header: true";

        var payloads = new[]
        {
            (Status: HttpStatusCode.Unauthorized, Json: $$"""{"error":"{{overlongError}}"}""", ExpectedSanitized: "untrusted_error_code"),
            (Status: HttpStatusCode.Unauthorized, Json: JsonSerializer.Serialize(new { error = unsafeCharsError }), ExpectedSanitized: "untrusted_error_code"),
            (Status: HttpStatusCode.Unauthorized, Json: """{"error":"proxy_gateway_timeout"}""", ExpectedSanitized: "proxy_gateway_timeout"),
            (Status: HttpStatusCode.InternalServerError, Json: $$"""{"error":"{{overlongError}}"}""", ExpectedSanitized: "untrusted_error_code"),
        };

        foreach (var item in payloads)
        {
            using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(item.Status)
            {
                Content = new StringContent(item.Json, Encoding.UTF8, "application/json"),
            });
            using var http = new HttpClient(handler);
            await using var supervisor = new HeartbeatSupervisor(
                store,
                new HeartbeatClient(http),
                new InMemoryRevocationTombstoneStore());

            var res = await supervisor.TriggerHeartbeatNowAsync();
            Assert.False(res.Succeeded);
            Assert.False(res.IsConfirmedRevocation);
            Assert.Equal(item.ExpectedSanitized, res.ServerErrorCode);
            Assert.True(res.ServerErrorCode!.Length <= HeartbeatClient.MaxDiagnosticErrorCodeLength);
            Assert.DoesNotContain("<script>", supervisor.Snapshot.LastErrorDetail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.True(await store.ExistsAsync());
        }
    }

    private static SessionStateMachine CreateSessionMachineInState(SessionState targetSessionState, Guid deviceId)
    {
        var sessionMachine = new SessionStateMachine();
        var options = new CompanionOptions { ServerOrigin = TrustedOrigin };

        if (targetSessionState == SessionState.AwaitingOwnerApproval)
        {
            _ = sessionMachine.BeginSession(
                new SessionApprovalRequest(Guid.NewGuid(), deviceId, PermissionMode.FullControl, false, DateTimeOffset.UtcNow),
                options,
                ownerExplicitlyApproved: false);
        }
        else if (targetSessionState == SessionState.ActiveViewOnly)
        {
            _ = sessionMachine.BeginSession(
                new SessionApprovalRequest(Guid.NewGuid(), deviceId, PermissionMode.ViewOnly, false, DateTimeOffset.UtcNow),
                options,
                ownerExplicitlyApproved: true);
        }
        else
        {
            _ = sessionMachine.BeginSession(
                new SessionApprovalRequest(Guid.NewGuid(), deviceId, PermissionMode.FullControl, false, DateTimeOffset.UtcNow),
                options,
                ownerExplicitlyApproved: true);

            if (targetSessionState == SessionState.Paused)
            {
                _ = sessionMachine.Pause();
            }
            else if (targetSessionState == SessionState.LockedOrUnavailable)
            {
                _ = sessionMachine.NotifyDesktopLockedOrUnavailable();
            }
        }

        return sessionMachine;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Condition was not satisfied before timeout.");
            }

            await Task.Delay(5);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, int timeoutMs = 4000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!await condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail("Async condition was not satisfied before timeout.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore, IDisposable
    {
        private readonly Lock _gate = new();
        private DeviceCredential? _stored;

        public bool ThrowCorruptedOnLoad { get; set; }

        public bool FailDelete { get; set; }

        public Task VerifyWritableAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_stored is { IsDisposed: false });
            }
        }

        public Task<DeviceCredential?> LoadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (ThrowCorruptedOnLoad)
                {
                    throw new CredentialStoreCorruptedException("Simulated DPAPI corruption.");
                }

                if (_stored is null || _stored.IsDisposed)
                {
                    return Task.FromResult<DeviceCredential?>(null);
                }

                return Task.FromResult<DeviceCredential?>(new DeviceCredential(
                    _stored.Metadata.DeviceId,
                    _stored.Metadata.ServerOrigin,
                    _stored.Token,
                    _stored.Metadata.PairedAtUtc));
            }
        }

        public Task SaveAsync(DeviceCredential credential, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _stored?.Dispose();
                _stored = new DeviceCredential(
                    credential.Metadata.DeviceId,
                    credential.Metadata.ServerOrigin,
                    credential.Token,
                    credential.Metadata.PairedAtUtc);
            }

            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailDelete)
                {
                    throw new IOException("Simulated DPAPI file deletion failure (locked or access denied).");
                }

                _stored?.Dispose();
                _stored = null;
            }

            return Task.CompletedTask;
        }

        public Task<bool> DeleteIfMatchesAsync(Guid expectedDeviceId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (FailDelete)
                {
                    throw new IOException("Simulated DPAPI file deletion failure (locked or access denied).");
                }

                if (_stored is null || _stored.IsDisposed)
                {
                    return Task.FromResult(true);
                }

                if (!ThrowCorruptedOnLoad && _stored.Metadata.DeviceId != expectedDeviceId)
                {
                    return Task.FromResult(false);
                }

                _stored.Dispose();
                _stored = null;
                return Task.FromResult(true);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stored?.Dispose();
            }
        }
    }

    private sealed class FailingDeleteCredentialStoreWrapper(
        ICredentialStore inner,
        bool failDeletes) : ICredentialStore, IDisposable
    {
        private int _deleteAttempts;

        public int DeleteAttemptCount => Volatile.Read(ref _deleteAttempts);

        public Task VerifyWritableAsync(CancellationToken ct = default) => inner.VerifyWritableAsync(ct);

        public Task<bool> ExistsAsync(CancellationToken ct = default) => inner.ExistsAsync(ct);

        public Task<DeviceCredential?> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);

        public Task SaveAsync(DeviceCredential credential, CancellationToken ct = default) =>
            inner.SaveAsync(credential, ct);

        public Task DeleteAsync(CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _deleteAttempts);
            if (failDeletes)
            {
                throw new IOException("Simulated sharing violation while deleting credentials.dpapi.");
            }

            return inner.DeleteAsync(ct);
        }

        public Task<bool> DeleteIfMatchesAsync(Guid expectedDeviceId, CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _deleteAttempts);
            if (failDeletes)
            {
                throw new IOException("Simulated sharing violation while deleting credentials.dpapi.");
            }

            return inner.DeleteIfMatchesAsync(expectedDeviceId, ct);
        }

        public void Dispose()
        {
            // Inner store lifetime is managed by caller.
        }
    }

    private sealed class RacingDeleteCredentialStoreWrapper(
        ICredentialStore inner,
        Func<Task> onBeforeConditionalDelete) : ICredentialStore, IDisposable
    {
        public Task VerifyWritableAsync(CancellationToken ct = default) => inner.VerifyWritableAsync(ct);

        public Task<bool> ExistsAsync(CancellationToken ct = default) => inner.ExistsAsync(ct);

        public Task<DeviceCredential?> LoadAsync(CancellationToken ct = default) => inner.LoadAsync(ct);

        public Task SaveAsync(DeviceCredential credential, CancellationToken ct = default) =>
            inner.SaveAsync(credential, ct);

        public Task DeleteAsync(CancellationToken ct = default) => inner.DeleteAsync(ct);

        public async Task<bool> DeleteIfMatchesAsync(Guid expectedDeviceId, CancellationToken ct = default)
        {
            await onBeforeConditionalDelete().ConfigureAwait(false);
            return await inner.DeleteIfMatchesAsync(expectedDeviceId, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingLoadCredentialStore(
        DeviceCredential seed,
        Func<CancellationToken, Task> onLoadAsync) : ICredentialStore, IDisposable
    {
        public Task VerifyWritableAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> ExistsAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<DeviceCredential?> LoadAsync(CancellationToken ct = default)
        {
            await onLoadAsync(ct).ConfigureAwait(false);
            return new DeviceCredential(
                seed.Metadata.DeviceId,
                seed.Metadata.ServerOrigin,
                seed.Token,
                seed.Metadata.PairedAtUtc);
        }

        public Task SaveAsync(DeviceCredential credential, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteIfMatchesAsync(Guid expectedDeviceId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public void Dispose() => seed.Dispose();
    }

    private sealed class FailingTombstoneStore : IRevocationTombstoneStore
    {
        public Task<bool> IsRevokedAsync(Guid deviceId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task RecordRevokedAsync(Guid deviceId, DateTimeOffset revokedAtUtc, CancellationToken ct = default) =>
            throw new IOException("Simulated disk full error while writing revocation tombstone.");
    }

    private sealed class BlockingTombstoneStore(Func<Task> onRecordAsync) : IRevocationTombstoneStore
    {
        public Task<bool> IsRevokedAsync(Guid deviceId, CancellationToken ct = default) =>
            Task.FromResult(false);

        public async Task RecordRevokedAsync(Guid deviceId, DateTimeOffset revokedAtUtc, CancellationToken ct = default) =>
            await onRecordAsync().ConfigureAwait(false);
    }

    private sealed class ThrowingInputExecutor : IInputExecutor
    {
        public HeldInputSnapshot GetHeldInputs() =>
            new(new HashSet<MouseButton>(), new HashSet<string>(StringComparer.Ordinal));

        public ValueTask<InputExecutionResult> ExecuteAsync(RemoteInputEvent command, CancellationToken ct = default) =>
            ValueTask.FromResult(new InputExecutionResult(InputExecutionStatus.Executed));

        public ValueTask<InputResetResult> ResetAllAsync(InputResetReason reason, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated Win32 SendInput reset failure.");
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responder(request, body);
        }
    }

    private sealed class AsyncStubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class DelegateHeartbeatClient(
        Func<DeviceCredential, CancellationToken, Task<HeartbeatAttemptResult>> sendAsync) : IHeartbeatClient
    {
        public Task<HeartbeatAttemptResult> SendHeartbeatAsync(DeviceCredential credential, CancellationToken ct = default) =>
            sendAsync(credential, ct);
    }

    private sealed class RecordingInputExecutor : IInputExecutor
    {
        public int ResetCount { get; private set; }
        public InputResetReason? LastResetReason { get; private set; }

        public HeldInputSnapshot GetHeldInputs() =>
            new(new HashSet<MouseButton>(), new HashSet<string>(StringComparer.Ordinal));

        public ValueTask<InputExecutionResult> ExecuteAsync(RemoteInputEvent command, CancellationToken ct = default) =>
            ValueTask.FromResult(new InputExecutionResult(InputExecutionStatus.Executed));

        public ValueTask<InputResetResult> ResetAllAsync(InputResetReason reason, CancellationToken ct = default)
        {
            ResetCount++;
            LastResetReason = reason;
            return ValueTask.FromResult(new InputResetResult(1, 1, reason));
        }
    }

    private sealed class FixedJitterSource(double value) : IJitterSource
    {
        public double NextUnitDouble() => value;
    }

    private sealed class AsyncSignal
    {
        private readonly Lock _lock = new();
        private int _count;
        private readonly List<(int Target, TaskCompletionSource Tcs)> _waiters = [];

        public void Signal()
        {
            List<TaskCompletionSource>? completed = null;
            lock (_lock)
            {
                _count++;
                for (var i = _waiters.Count - 1; i >= 0; i--)
                {
                    if (_count >= _waiters[i].Target)
                    {
                        completed ??= [];
                        completed.Add(_waiters[i].Tcs);
                        _waiters.RemoveAt(i);
                    }
                }
            }

            if (completed is not null)
            {
                foreach (var tcs in completed)
                {
                    _ = tcs.TrySetResult();
                }
            }
        }

        public Task WaitForCountAsync(int target, int timeoutMs = 4000)
        {
            TaskCompletionSource tcs;
            lock (_lock)
            {
                if (_count >= target)
                {
                    return Task.CompletedTask;
                }

                tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((target, tcs));
            }

            return tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _lock = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public int ActiveTimerCount
        {
            get
            {
                lock (_lock)
                {
                    return _timers.Count;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_lock)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            if (dueTime == TimeSpan.Zero)
            {
                _ = ThreadPool.QueueUserWorkItem(_ => timer.FireOnce());
            }

            return timer;
        }

        public void Advance(TimeSpan delta)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
            List<ManualTimer> due = [];

            lock (_lock)
            {
                _utcNow = _utcNow.Add(delta);
                for (var i = _timers.Count - 1; i >= 0; i--)
                {
                    var timer = _timers[i];
                    if (timer.IsDueUnsafe(_utcNow))
                    {
                        due.Add(timer);
                        if (!timer.AdvancePeriodOrDisableUnsafe())
                        {
                            _timers.RemoveAt(i);
                        }
                    }
                }
            }

            foreach (var timer in due)
            {
                timer.InvokeCallback();
            }
        }

        private void RegisterOrUpdate(ManualTimer timer)
        {
            lock (_lock)
            {
                if (!timer.IsDisposedUnsafe && timer.HasDueTimeUnsafe && !_timers.Contains(timer))
                {
                    _timers.Add(timer);
                }
                else if (timer.IsDisposedUnsafe || !timer.HasDueTimeUnsafe)
                {
                    _ = _timers.Remove(timer);
                }
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly ManualTimeProvider _owner;
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private DateTimeOffset? _dueUtc;
            private TimeSpan _period;
            private bool _disposed;

            public ManualTimer(
                ManualTimeProvider owner,
                TimerCallback callback,
                object? state,
                TimeSpan dueTime,
                TimeSpan period)
            {
                _owner = owner;
                _callback = callback;
                _state = state;
                SetScheduleUnsafe(dueTime, period);
                _owner.RegisterOrUpdate(this);
            }

            public bool IsDisposedUnsafe => _disposed;
            public bool HasDueTimeUnsafe => _dueUtc.HasValue;

            public bool IsDueUnsafe(DateTimeOffset now) =>
                !_disposed && _dueUtc.HasValue && now >= _dueUtc.Value;

            public bool AdvancePeriodOrDisableUnsafe()
            {
                if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan && _dueUtc.HasValue)
                {
                    _dueUtc = _dueUtc.Value.Add(_period);
                    return true;
                }

                _dueUtc = null;
                return false;
            }

            public void FireOnce()
            {
                lock (_owner._lock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _ = AdvancePeriodOrDisableUnsafe();
                    _owner.RegisterOrUpdate(this);
                }

                InvokeCallback();
            }

            public void InvokeCallback() => _callback(_state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_owner._lock)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    SetScheduleUnsafe(dueTime, period);
                }

                _owner.RegisterOrUpdate(this);
                if (dueTime == TimeSpan.Zero)
                {
                    _ = ThreadPool.QueueUserWorkItem(_ => FireOnce());
                }

                return true;
            }

            public void Dispose()
            {
                lock (_owner._lock)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                    _dueUtc = null;
                }

                _owner.RegisterOrUpdate(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            private void SetScheduleUnsafe(TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _dueUtc = null;
                }
                else
                {
                    _dueUtc = _owner.GetUtcNow().Add(dueTime);
                }
            }
        }
    }
}
