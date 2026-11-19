using System.Net;
using System.Text;
using System.Text.Json;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Pairing;
using Beacon.Companion.Core.Security;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class PairingClientTests
{
    private static readonly Uri TrustedOrigin = new("https://beacon.example.com");
    private static readonly PairingDeviceMetadata DefaultDevice = new("WORKSTATION-01", "Windows 11", "0.3.3");

    private static readonly string SharedFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "pairing-protocol-contract.json");

    [Fact]
    public async Task SixDigitPinPairingSendsExactJsonAndPersistsCredential()
    {
        using var store = new InMemoryCredentialStore();
        string? capturedPath = null;
        string? capturedBody = null;
        var expectedDeviceId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        const string expectedToken = "deviceToken_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456";

        using var handler = new StubHttpMessageHandler((req, body) =>
        {
            capturedPath = req.RequestUri?.ToString();
            capturedBody = body;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"device_id":"{{expectedDeviceId}}","device_token":"{{expectedToken}}"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new PairingClient(
            httpClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        // Accepts UI-formatted "482 910" with internal space and normalizes to "482910".
        var result = await client.PairWithPinAsync("482 910".AsMemory(), DefaultDevice);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.CredentialMetadata);
        Assert.Equal(expectedDeviceId, result.CredentialMetadata!.DeviceId);
        Assert.Equal(TrustedOrigin, result.CredentialMetadata.ServerOrigin);
        Assert.Equal("https://beacon.example.com/api/public/agent/pair", capturedPath);

        using var sentDoc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("482910", sentDoc.RootElement.GetProperty("code").GetString());
        var dev = sentDoc.RootElement.GetProperty("device");
        Assert.Equal("WORKSTATION-01", dev.GetProperty("name").GetString());
        Assert.Equal("Windows 11", dev.GetProperty("os").GetString());
        Assert.Equal("0.3.3", dev.GetProperty("agent_version").GetString());

        using var saved = await store.LoadAsync();
        Assert.NotNull(saved);
        Assert.Equal(expectedToken, saved!.Token.ToString());
    }

    [Fact]
    public async Task QrPairingEnforcesOriginApprovalAndSendsExactJsonWhenApproved()
    {
        using var store = new InMemoryCredentialStore();
        var customOrigin = new Uri("https://custom.beacon.example.org");
        var pairingId = Guid.Parse("550e8400-e29b-41d4-a716-446655440000");
        const string qrToken = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01";
        var qrJson = $$"""
            {
              "v": 1,
              "type": "beacon-pair",
              "server": "{{customOrigin}}",
              "pairing_id": "{{pairingId}}",
              "token": "{{qrToken}}"
            }
            """;

        var networkCalled = false;
        using var handler = new StubHttpMessageHandler((req, body) =>
        {
            networkCalled = true;
            using var doc = JsonDocument.Parse(body);
            Assert.Equal(pairingId.ToString("D"), doc.RootElement.GetProperty("pairing_id").GetString());
            Assert.Equal(qrToken, doc.RootElement.GetProperty("token").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"device_id":"7c9e6679-7425-40de-944b-e07fc1f90ae7","device_token":"deviceToken_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new PairingClient(
            httpClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        // 1. Without explicit custom origin approval, QR pointing to another server is blocked pre-network!
        var rejected = await client.PairWithQrAsync(
            qrJson,
            DefaultDevice,
            OriginApprovalMode.ConfiguredOriginOnly);
        Assert.False(rejected.Succeeded);
        Assert.Equal(PairingFailureReason.UntrustedServerOrigin, rejected.FailureReason);
        Assert.False(networkCalled);

        // 2. With explicit owner approval for that exact custom origin, QR pairing succeeds.
        var approved = await client.PairWithQrAsync(
            qrJson,
            DefaultDevice,
            OriginApprovalMode.ExplicitlyApprovedCustomOrigin,
            explicitlyApprovedCustomOrigin: customOrigin);
        Assert.True(approved.Succeeded);
        Assert.True(networkCalled);
        Assert.Equal(customOrigin, approved.CredentialMetadata!.ServerOrigin);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":"invalid_request"}""", PairingFailureReason.BadRequestRejectedByServer)]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"invalid_code"}""", PairingFailureReason.InvalidOrLockedCodeOrToken)]
    [InlineData(HttpStatusCode.Gone, """{"error":"expired"}""", PairingFailureReason.PairingSessionExpired)]
    [InlineData(HttpStatusCode.TooManyRequests, """{"error":"too_many_attempts"}""", PairingFailureReason.ServerRateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"server_error"}""", PairingFailureReason.ServerError)]
    [InlineData(HttpStatusCode.Found, "", PairingFailureReason.UnexpectedRedirect)]
    [InlineData(HttpStatusCode.TemporaryRedirect, "", PairingFailureReason.UnexpectedRedirect)]
    public async Task MapsBackendErrorAndRedirectResponsesToTypedReasons(
        HttpStatusCode statusCode,
        string responseBody,
        PairingFailureReason expectedReason)
    {
        using var store = new InMemoryCredentialStore();
        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        var client = new PairingClient(
            httpClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        var result = await client.PairWithPinAsync("123456".AsMemory(), DefaultDevice);
        Assert.False(result.Succeeded);
        Assert.Equal(expectedReason, result.FailureReason);
        Assert.Equal((int)statusCode, result.HttpStatusCode);
        Assert.False(await store.ExistsAsync());
    }

    [Fact]
    public async Task EnforcesClientSidePinAttemptCooldownAndNeverLogsSecrets()
    {
        var fakeTime = new FakePairingTimeProvider();
        using var store = new InMemoryCredentialStore();
        var networkCalls = 0;

        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            networkCalls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":"invalid_code"}""", Encoding.UTF8, "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        var client = new PairingClient(
            httpClient,
            store,
            new PairingClientOptions
            {
                ConfiguredServerOrigin = TrustedOrigin,
                MaxFailedPinAttemptsBeforeCooldown = 3,
                PinCooldownDuration = TimeSpan.FromSeconds(30),
            },
            timeProvider: fakeTime);

        const string secretPin = "918273";
        for (var i = 0; i < 3; i++)
        {
            var attempt = await client.PairWithPinAsync(secretPin.AsMemory(), DefaultDevice);
            Assert.Equal(PairingFailureReason.InvalidOrLockedCodeOrToken, attempt.FailureReason);
            Assert.DoesNotContain(secretPin, attempt.UserGuidance ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Equal(3, networkCalls);

        // 4th attempt is throttled locally without hitting the network.
        var throttled = await client.PairWithPinAsync(secretPin.AsMemory(), DefaultDevice);
        Assert.Equal(PairingFailureReason.ClientRateLimited, throttled.FailureReason);
        Assert.Equal(3, networkCalls);

        // Advance past cooldown -> network call permitted again.
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        _ = await client.PairWithPinAsync(secretPin.AsMemory(), DefaultDevice);
        Assert.Equal(4, networkCalls);
    }

    [Fact]
    public async Task PreflightStoreFailureAbortsBeforeConsumingSingleUseServerCode()
    {
        using var store = new InMemoryCredentialStore { FailPreflight = true };
        var networkCalled = false;

        using var handler = new StubHttpMessageHandler((_, _) =>
        {
            networkCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var client = new PairingClient(
            httpClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        var result = await client.PairWithPinAsync("123456".AsMemory(), DefaultDevice);
        Assert.False(result.Succeeded);
        Assert.Equal(PairingFailureReason.CredentialStoreUnavailablePreFlight, result.FailureReason);
        Assert.False(networkCalled);
    }

    [Fact]
    public async Task DistinguishesPreSendNetworkErrorFromAmbiguousPostSendDisconnect()
    {
        using var store = new InMemoryCredentialStore();

        // 1. Pre-send failure (connection refused before request body is read/sent).
        using var preSendHandler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("Connection refused before request sent."),
            readRequestBodyBeforeCallback: false);
        using var preSendClient = new HttpClient(preSendHandler);
        var client1 = new PairingClient(
            preSendClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        var preSendResult = await client1.PairWithPinAsync("123456".AsMemory(), DefaultDevice);
        Assert.False(preSendResult.Succeeded);
        Assert.Equal(PairingFailureReason.NetworkErrorBeforeRequestSent, preSendResult.FailureReason);
        Assert.False(preSendResult.IsOutcomeAmbiguous);

        // 2. Post-send disconnect (request body was transmitted, then connection dropped).
        using var postSendHandler = new StubHttpMessageHandler(
            (_, _) => throw new HttpRequestException("Connection reset after request sent."),
            readRequestBodyBeforeCallback: true);
        using var postSendClient = new HttpClient(postSendHandler);
        var client2 = new PairingClient(
            postSendClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        var postSendResult = await client2.PairWithPinAsync("123456".AsMemory(), DefaultDevice);
        Assert.False(postSendResult.Succeeded);
        Assert.Equal(PairingFailureReason.AmbiguousServerOutcomeAfterRequestSent, postSendResult.FailureReason);
        Assert.True(postSendResult.IsOutcomeAmbiguous);
        Assert.Contains("dashboard", postSendResult.UserGuidance ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetriesTransientSaveFailureAndReportsOrphanedDeviceIdOnPersistentSaveFailure()
    {
        var expectedDeviceId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        using var handler = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$"""{"device_id":"{{expectedDeviceId}}","device_token":"deviceToken_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456"}""",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);

        // Case 1: Transient save failure on attempt 1 succeeds on retry attempt 2.
        using var flakyStore = new InMemoryCredentialStore { RemainingSaveFailures = 1 };
        var clientFlaky = new PairingClient(
            httpClient,
            flakyStore,
            new PairingClientOptions
            {
                ConfiguredServerOrigin = TrustedOrigin,
                LocalSaveMaxAttempts = 3,
                LocalSaveRetryDelay = TimeSpan.FromMilliseconds(1),
            });

        var recovered = await clientFlaky.PairWithPinAsync("123456".AsMemory(), DefaultDevice);
        Assert.True(recovered.Succeeded);
        Assert.True(await flakyStore.ExistsAsync());

        // Case 2: Persistent save failure across all 3 attempts reports false and surfaces OrphanedDeviceId.
        using var brokenStore = new InMemoryCredentialStore { RemainingSaveFailures = 10 };
        var clientBroken = new PairingClient(
            httpClient,
            brokenStore,
            new PairingClientOptions
            {
                ConfiguredServerOrigin = TrustedOrigin,
                LocalSaveMaxAttempts = 3,
                LocalSaveRetryDelay = TimeSpan.FromMilliseconds(1),
            });

        var failedSave = await clientBroken.PairWithPinAsync("123456".AsMemory(), DefaultDevice);
        Assert.False(failedSave.Succeeded);
        Assert.Equal(PairingFailureReason.CredentialPersistenceFailedAfterServerConfirm, failedSave.FailureReason);
        Assert.Equal(expectedDeviceId, failedSave.OrphanedDeviceId);
        Assert.False(await brokenStore.ExistsAsync());
    }

    [Fact]
    public void SharedCrossLanguagePairingFixtureMatchesCSharpValidatorsAndSerializers()
    {
        Assert.True(File.Exists(SharedFixturePath), $"Missing shared pairing fixture at '{SharedFixturePath}'.");
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFixturePath));
        var root = doc.RootElement;

        Assert.Equal(PairingClient.PairingEndpointPath, root.GetProperty("endpointPath").GetString());

        foreach (var entry in root.GetProperty("validQrPayloads").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            var res = QrPairingPayloadValidator.Validate(json);
            Assert.True(res.IsValid, $"Expected valid QR fixture '{entry.GetProperty("name").GetString()}': {res.ErrorDetail}");
            using var payload = res.Payload!;
            Assert.Equal(entry.GetProperty("expectedServer").GetString(), payload.CanonicalServerOrigin.GetLeftPart(UriPartial.Authority));
            Assert.Equal(Guid.Parse(entry.GetProperty("expectedPairingId").GetString()!), payload.PairingId);
        }

        foreach (var entry in root.GetProperty("invalidQrPayloads").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            var res = QrPairingPayloadValidator.Validate(json);
            Assert.False(res.IsValid, $"Expected invalid QR fixture '{entry.GetProperty("name").GetString()}'.");
        }

        foreach (var entry in root.GetProperty("validPinRequests").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            var pinReq = JsonSerializer.Deserialize(json, PairingJsonContext.Default.SixDigitPinPairingRequest);
            Assert.NotNull(pinReq);
            Assert.Equal(6, pinReq.Code.Length);
            Assert.All(pinReq.Code, ch => Assert.True(char.IsAsciiDigit(ch)));
            Assert.False(string.IsNullOrWhiteSpace(pinReq.Device.Name));
        }

        foreach (var entry in root.GetProperty("validQrRequests").EnumerateArray())
        {
            var json = entry.GetProperty("json").GetString()!;
            var qrReq = JsonSerializer.Deserialize(json, PairingJsonContext.Default.QrTokenPairingRequest);
            Assert.NotNull(qrReq);
            Assert.NotEqual(Guid.Empty, qrReq.PairingId);
            Assert.True(QrPairingPayloadValidator.IsValidBase64UrlToken(qrReq.Token));
        }

        var responses = root.GetProperty("serverResponses");
        var confirmed200 = JsonSerializer.Deserialize(
            responses.GetProperty("confirmed200").GetString()!,
            PairingJsonContext.Default.PairingSuccessResponse);
        Assert.NotNull(confirmed200);
        Assert.NotEqual(Guid.Empty, confirmed200.DeviceId);
        Assert.True(DeviceCredential.IsValidTokenFormat(confirmed200.DeviceToken));

        ReadOnlySpan<string> errorProperties = ["invalidCode401", "expired410", "tooManyAttempts429", "serverError500"];
        foreach (var errProp in errorProperties)
        {
            var err = JsonSerializer.Deserialize(
                responses.GetProperty(errProp).GetString()!,
                PairingJsonContext.Default.PairingErrorResponse);
            Assert.NotNull(err);
            Assert.False(string.IsNullOrWhiteSpace(err.Error));
        }
    }

    [Fact]
    public async Task SerializesLargeEscapedDeviceMetadataAcrossPinnedBufferGrowthForPinAndQrRequests()
    {
        // 64-char Name and 64-char Os with characters escaped as \u003C (~6 bytes each) exceed
        // the initial 256/384-byte pinned writer capacity, exercising buffer growth and zeroing.
        var escapedName = new string('<', 64);
        var escapedOs = new string('>', 64);
        var largeDevice = new PairingDeviceMetadata(escapedName, escapedOs, "0.3.3-rc1");
        var expectedDeviceId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        var bodies = new List<string>();

        using var handler = new StubHttpMessageHandler((_, body) =>
        {
            bodies.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""{"device_id":"{{expectedDeviceId}}","device_token":"deviceToken_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        using var httpClient = new HttpClient(handler);
        using var store = new InMemoryCredentialStore();
        var client = new PairingClient(
            httpClient,
            store,
            new PairingClientOptions { ConfiguredServerOrigin = TrustedOrigin });

        var pinResult = await client.PairWithPinAsync("654321".AsMemory(), largeDevice);
        Assert.True(pinResult.Succeeded);

        var qrJson = """{"v":1,"type":"beacon-pair","server":"https://beacon.example.com","pairing_id":"550e8400-e29b-41d4-a716-446655440000","token":"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmno01"}""";
        var qrResult = await client.PairWithQrAsync(
            qrJson,
            largeDevice,
            OriginApprovalMode.ConfiguredOriginOnly,
            overwriteExistingCredential: true);
        Assert.True(qrResult.Succeeded);

        Assert.Equal(2, bodies.Count);
        foreach (var body in bodies)
        {
            Assert.True(Encoding.UTF8.GetByteCount(body) > 512);
            using var doc = JsonDocument.Parse(body);
            var dev = doc.RootElement.GetProperty("device");
            Assert.Equal(escapedName, dev.GetProperty("name").GetString());
            Assert.Equal(escapedOs, dev.GetProperty("os").GetString());
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore, IDisposable
    {
        private DeviceCredential? _stored;

        public bool FailPreflight { get; set; }
        public int RemainingSaveFailures { get; set; }

        public Task VerifyWritableAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (FailPreflight)
            {
                throw new IOException("Simulated preflight failure.");
            }

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
            if (RemainingSaveFailures > 0)
            {
                RemainingSaveFailures--;
                throw new IOException("Simulated local credential persistence failure.");
            }

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
        Func<HttpRequestMessage, string, HttpResponseMessage> responder,
        bool readRequestBodyBeforeCallback = true) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = string.Empty;
            if (readRequestBodyBeforeCallback && request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return responder(request, body);
        }
    }

    private sealed class FakePairingTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
