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
        InMemoryCredentialStore store,
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

        using var handler = new StubHttpMessageHandler((req, _) =>
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
        await using var supervisor = new HeartbeatSupervisor(store, client, timeProvider: timeProvider);

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
        await using var supervisor = new HeartbeatSupervisor(store, client, options, timeProvider: timeProvider);

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

    // 3. Network timeout
    [Fact]
    public async Task NetworkTimeoutEntersRetryingStateWithoutRevokingDevice()
    {
        var timeProvider = new ManualTimeProvider();
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        var attempt = 0;
        using var handler = new AsyncStubHttpMessageHandler(async (_, ct) =>
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
        await using var supervisor = new HeartbeatSupervisor(store, client, timeProvider: timeProvider);

        var timedOutTask = supervisor.TriggerHeartbeatNowAsync();
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var timedOut = await timedOutTask;

        Assert.False(timedOut.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.Timeout, timedOut.Outcome);
        Assert.True(timedOut.IsTransientFailure);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        Assert.Equal(HeartbeatConnectivityState.Retrying, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(1, supervisor.Snapshot.ConsecutiveTransientFailures);
        Assert.True(await store.ExistsAsync());

        // Subsequent recovery restores Healthy.
        var recovered = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(recovered.Succeeded);
        Assert.Equal(HeartbeatConnectivityState.Healthy, supervisor.Snapshot.ConnectivityState);
        Assert.Equal(0, supervisor.Snapshot.ConsecutiveTransientFailures);
    }

    // 4. Internet disconnection
    [Fact]
    public async Task InternetDisconnectionTransitionsToDisconnectedAfterThresholdAndPreservesCredential()
    {
        using var store = new InMemoryCredentialStore();
        await SaveSeedCredentialAsync(store);

        using var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException("No such host is known."));
        using var httpClient = new HttpClient(handler);
        var client = new HeartbeatClient(httpClient);
        var options = new HeartbeatSupervisorOptions
        {
            ConsecutiveFailuresBeforeDisconnected = 2,
        };
        await using var supervisor = new HeartbeatSupervisor(store, client, options);

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
            options,
            timeProvider: timeProvider,
            jitterSource: jitter);

        await supervisor.StartAsync();

        // Failure #1 -> backoff 2s
        await attemptSignal.WaitForCountAsync(1);
        await WaitUntilAsync(() => timeProvider.ActiveTimerCount > 0);
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

        // Attempt #4 succeeds and resets transient failure count!
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

        using var supMin = new HeartbeatSupervisor(store, new HeartbeatClient(httpClient), options, jitterSource: minJitter);
        using var supMax = new HeartbeatSupervisor(store, new HeartbeatClient(httpClient), options, jitterSource: maxJitter);

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
        await using var supervisor = new HeartbeatSupervisor(store, client);

        var first = await supervisor.TriggerHeartbeatNowAsync();
        Assert.False(first.Succeeded);
        Assert.Equal(HeartbeatOutcomeKind.TransientServerError, first.Outcome);
        Assert.False(first.IsConfirmedRevocation);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            Assert.Equal(TimeSpan.FromSeconds(12), first.RetryAfterHint);
        }

        var second = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(second.Succeeded);
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
        await using (var supMissing = new HeartbeatSupervisor(emptyStore, client, sessionStateMachine: sessionMachine, inputExecutor: inputExecutor))
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
        await using (var supCorrupt = new HeartbeatSupervisor(corruptStore, client))
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
        await using var supervisor = new HeartbeatSupervisor(store, client);

        var revokedSignal = new TaskCompletionSource<DeviceRevokedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        supervisor.RevocationConfirmed += (_, args) => _ = revokedSignal.TrySetResult(args);

        await supervisor.StartAsync();
        var args = await revokedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DeviceIdA, args.DeviceId);
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
        using var handler = new AsyncStubHttpMessageHandler(async (_, ct) =>
        {
            _ = inFlightStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return OkJsonResponse(DateTimeOffset.UtcNow);
        });
        using var httpClient = new HttpClient(handler);
        await using var supervisor = new HeartbeatSupervisor(store, new HeartbeatClient(httpClient));

        using var cts = new CancellationTokenSource();
        var task = supervisor.TriggerHeartbeatNowAsync(cts.Token);
        await inFlightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cts.CancelAsync();
        var result = await task;

        Assert.Equal(HeartbeatOutcomeKind.Cancelled, result.Outcome);
        Assert.Equal(0, supervisor.Snapshot.ConsecutiveTransientFailures);
        Assert.Equal(0, supervisor.Snapshot.TotalTransientFailureCount);
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
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
        var stubClient = new DelegateHeartbeatClient(async (cred, _) =>
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

        await using var supervisor = new HeartbeatSupervisor(store, stubClient);

        // Scenario A: Stale 200 OK arriving AFTER revocation must NOT restore Authorized state.
        var inFlight1 = supervisor.TriggerHeartbeatNowAsync();
        await call1Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await supervisor.NotifyAuthorizationRevokedAsync(DeviceIdA, "Revoked while beat #1 in flight");
        Assert.True(supervisor.Snapshot.IsRevoked);

        _ = call1Release.TrySetResult(HttpStatusCode.OK);
        _ = await inFlight1;

        Assert.True(supervisor.Snapshot.IsRevoked);
        Assert.Equal(0, supervisor.Snapshot.TotalSucceededCount);
        _ = Assert.Throws<DeviceRevokedException>(() => supervisor.EnsureDeviceAuthorized());

        // Scenario B: Fresh re-pair to DeviceIdB, then confirm stale revocation for old DeviceIdA never revokes DeviceIdB.
        await SaveSeedCredentialAsync(store, DeviceIdB, TokenB);
        await supervisor.AcknowledgeFreshPairingAsync();
        Assert.Equal(DeviceIdB, supervisor.EnsureDeviceAuthorized());

        _ = call2Release.TrySetResult(HttpStatusCode.OK);
        var resB = await supervisor.TriggerHeartbeatNowAsync();
        Assert.True(resB.Succeeded);
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
        await using var supervisor = new HeartbeatSupervisor(store, client);

        var result = await supervisor.TriggerHeartbeatNowAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(expectedOutcome, result.Outcome);
        Assert.Equal(0, supervisor.Snapshot.TotalSucceededCount);
        // Captive portal / redirect / malformed 200 must never wipe stored DPAPI pairing credential.
        Assert.Equal(DeviceAuthorizationStatus.Authorized, supervisor.Snapshot.AuthorizationStatus);
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
        await using var supervisor = new HeartbeatSupervisor(store, client);

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

        using var handler = new StubHttpMessageHandler((req, _) =>
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
        private DeviceCredential? _stored;

        public bool ThrowCorruptedOnLoad { get; set; }

        public Task VerifyWritableAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_stored is { IsDisposed: false });
        }

        public Task<DeviceCredential?> LoadAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
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

        public Task SaveAsync(DeviceCredential credential, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _stored?.Dispose();
            _stored = new DeviceCredential(
                credential.Metadata.DeviceId,
                credential.Metadata.ServerOrigin,
                credential.Token,
                credential.Metadata.PairedAtUtc);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _stored?.Dispose();
            _stored = null;
            return Task.CompletedTask;
        }

        public void Dispose() => _stored?.Dispose();
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
