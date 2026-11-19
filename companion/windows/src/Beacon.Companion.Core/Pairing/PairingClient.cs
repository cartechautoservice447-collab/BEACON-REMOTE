using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Pairing;

public sealed record PairingClientOptions
{
    public Uri? ConfiguredServerOrigin { get; init; }

    public bool AllowDevelopmentLoopbackHttp { get; init; } = true;

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxResponseBytes { get; init; } = 4096;

    /// <summary>
    /// Client-side cap on failed 6-digit PIN submissions before enforcing a local cooldown,
    /// protecting the user from exhausting the server's 10-per-15-minute IP rate limit.
    /// </summary>
    public int MaxFailedPinAttemptsBeforeCooldown { get; init; } = 3;

    public TimeSpan PinCooldownDuration { get; init; } = TimeSpan.FromSeconds(30);

    public int LocalSaveMaxAttempts { get; init; } = 3;

    public TimeSpan LocalSaveRetryDelay { get; init; } = TimeSpan.FromMilliseconds(50);

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 256);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxFailedPinAttemptsBeforeCooldown);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(PinCooldownDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LocalSaveMaxAttempts);
        ArgumentOutOfRangeException.ThrowIfLessThan(LocalSaveRetryDelay, TimeSpan.Zero);

        if (ConfiguredServerOrigin is not null)
        {
            var validation = ServerOriginValidator.Validate(ConfiguredServerOrigin, AllowDevelopmentLoopbackHttp);
            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    validation.ErrorDetail ?? "ConfiguredServerOrigin is invalid.",
                    nameof(ConfiguredServerOrigin));
            }
        }
    }
}

public interface IPairingClient
{
    Task<PairingAttemptResult> PairWithPinAsync(
        ReadOnlyMemory<char> pinCode,
        PairingDeviceMetadata device,
        bool overwriteExistingCredential = false,
        CancellationToken ct = default);

    Task<PairingAttemptResult> PairWithQrAsync(
        ReadOnlyMemory<byte> utf8QrPayload,
        PairingDeviceMetadata device,
        OriginApprovalMode originApprovalMode = OriginApprovalMode.ConfiguredOriginOnly,
        Uri? explicitlyApprovedCustomOrigin = null,
        bool overwriteExistingCredential = false,
        CancellationToken ct = default);

    Task<PairingAttemptResult> PairWithQrAsync(
        string rawQrPayload,
        PairingDeviceMetadata device,
        OriginApprovalMode originApprovalMode = OriginApprovalMode.ConfiguredOriginOnly,
        Uri? explicitlyApprovedCustomOrigin = null,
        bool overwriteExistingCredential = false,
        CancellationToken ct = default);
}

/// <summary>
/// Secure client for <c>POST /api/public/agent/pair</c>.
/// Enforces strict origin validation, redirect blocking, client-side PIN throttling,
/// preflight credential-store verification, pinned secret-buffer handling,
/// and explicit reporting for both ambiguous network drops and post-confirm persistence failures.
/// </summary>
public sealed class PairingClient : IPairingClient
{
    public const string PairingEndpointPath = "/api/public/agent/pair";

    private readonly HttpClient _httpClient;
    private readonly ICredentialStore _credentialStore;
    private readonly PairingClientOptions _options;
    private readonly IServerOriginTrustPolicy _originTrustPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _pinThrottleLock = new();

    private int _consecutivePinFailures;
    private DateTimeOffset _pinCooldownUntilUtc = DateTimeOffset.MinValue;

    public PairingClient(
        HttpClient httpClient,
        ICredentialStore credentialStore,
        PairingClientOptions? options = null,
        IServerOriginTrustPolicy? originTrustPolicy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(credentialStore);

        _options = options ?? new PairingClientOptions();
        _options.Validate();

        _httpClient = httpClient;
        _credentialStore = credentialStore;
        _originTrustPolicy = originTrustPolicy ?? new DefaultServerOriginTrustPolicy();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a hardened <see cref="HttpClient"/> configured with <c>AllowAutoRedirect = false</c>,
    /// <c>UseCookies = false</c>, and TLS 1.2 / 1.3 only.
    /// </summary>
    public static HttpClient CreateSecureHttpClient(TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            },
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 8192,
        };
    }

    public async Task<PairingAttemptResult> PairWithPinAsync(
        ReadOnlyMemory<char> pinCode,
        PairingDeviceMetadata device,
        bool overwriteExistingCredential = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (IsPinRateLimited(out var remainingCooldown))
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.ClientRateLimited,
                $"Too many failed PIN attempts. Wait {Math.Max(1, (int)Math.Ceiling(remainingCooldown.TotalSeconds))} seconds before trying again.");
        }

        var normalizedDigits = GC.AllocateArray<char>(6, pinned: true);
        try
        {
            if (!TryNormalizeSixDigitPin(pinCode.Span, normalizedDigits))
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.InvalidPinFormat,
                    "Pairing code must be exactly 6 digits (0-9).");
            }

            if (!device.TryNormalize(out var normalizedDevice, out var deviceError) || normalizedDevice is null)
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.InvalidDeviceMetadata,
                    deviceError ?? "Invalid device metadata.");
            }

            if (_options.ConfiguredServerOrigin is null)
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.UntrustedServerOrigin,
                    "A trusted ServerOrigin must be configured before using a 6-digit pairing code.");
            }

            var originValidation = ServerOriginValidator.Validate(
                _options.ConfiguredServerOrigin,
                _options.AllowDevelopmentLoopbackHttp);
            if (!originValidation.IsValid || originValidation.CanonicalOrigin is null)
            {
                var reason = originValidation.Error == ServerOriginValidationError.InsecureHttpNotLoopback
                    ? PairingFailureReason.InsecureServerOrigin
                    : PairingFailureReason.UntrustedServerOrigin;
                return PairingAttemptResult.Failure(
                    reason,
                    originValidation.ErrorDetail ?? "Configured server origin is invalid.");
            }

            var requestBytes = WritePinPairingRequestUtf8(normalizedDigits, normalizedDevice);
            try
            {
                var result = await ExecutePairingTransactionAsync(
                    originValidation.CanonicalOrigin,
                    requestBytes,
                    normalizedDevice.AgentVersion,
                    overwriteExistingCredential,
                    ct).ConfigureAwait(false);

                RecordPinAttemptOutcome(result);
                return result;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(requestBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(normalizedDigits.AsSpan()));
        }
    }

    public async Task<PairingAttemptResult> PairWithQrAsync(
        string rawQrPayload,
        PairingDeviceMetadata device,
        OriginApprovalMode originApprovalMode = OriginApprovalMode.ConfiguredOriginOnly,
        Uri? explicitlyApprovedCustomOrigin = null,
        bool overwriteExistingCredential = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (string.IsNullOrWhiteSpace(rawQrPayload))
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.InvalidQrPayload,
                "QR pairing payload must not be empty.");
        }

        if (rawQrPayload.Length > QrPairingPayloadValidator.MaxPayloadBytes)
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.InvalidQrPayload,
                $"QR pairing payload exceeds maximum length of {QrPairingPayloadValidator.MaxPayloadBytes} bytes.");
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(rawQrPayload);
        var utf8Bytes = GC.AllocateArray<byte>(byteCount, pinned: true);
        try
        {
            _ = System.Text.Encoding.UTF8.GetBytes(rawQrPayload, utf8Bytes);
            return await PairWithQrAsync(
                utf8Bytes.AsMemory(),
                device,
                originApprovalMode,
                explicitlyApprovedCustomOrigin,
                overwriteExistingCredential,
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8Bytes);
        }
    }

    public async Task<PairingAttemptResult> PairWithQrAsync(
        ReadOnlyMemory<byte> utf8QrPayload,
        PairingDeviceMetadata device,
        OriginApprovalMode originApprovalMode = OriginApprovalMode.ConfiguredOriginOnly,
        Uri? explicitlyApprovedCustomOrigin = null,
        bool overwriteExistingCredential = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        var qrValidation = QrPairingPayloadValidator.Validate(
            utf8QrPayload.Span,
            _options.AllowDevelopmentLoopbackHttp);
        if (!qrValidation.IsValid || qrValidation.Payload is null)
        {
            var reason = qrValidation.FailureReason switch
            {
                QrValidationFailureReason.UnsupportedVersion => PairingFailureReason.UnsupportedQrVersion,
                QrValidationFailureReason.InsecureServerOrigin => PairingFailureReason.InsecureServerOrigin,
                _ => PairingFailureReason.InvalidQrPayload,
            };
            return PairingAttemptResult.Failure(
                reason,
                qrValidation.ErrorDetail ?? "Invalid QR pairing payload.");
        }

        using var qr = qrValidation.Payload;

        if (!device.TryNormalize(out var normalizedDevice, out var deviceError) || normalizedDevice is null)
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.InvalidDeviceMetadata,
                deviceError ?? "Invalid device metadata.");
        }

        var trustDecision = _originTrustPolicy.Evaluate(
            qr.CanonicalServerOrigin,
            _options.ConfiguredServerOrigin,
            originApprovalMode,
            explicitlyApprovedCustomOrigin,
            _options.AllowDevelopmentLoopbackHttp);
        if (!trustDecision.IsTrusted || trustDecision.CanonicalOrigin is null)
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.UntrustedServerOrigin,
                trustDecision.RejectionReason ?? "The server origin in the QR code is not trusted.");
        }

        var requestBytes = WriteQrPairingRequestUtf8(qr.PairingId, qr.Token, normalizedDevice);
        try
        {
            return await ExecutePairingTransactionAsync(
                trustDecision.CanonicalOrigin,
                requestBytes,
                normalizedDevice.AgentVersion,
                overwriteExistingCredential,
                ct).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(requestBytes);
        }
    }

    private async Task<PairingAttemptResult> ExecutePairingTransactionAsync(
        Uri canonicalOrigin,
        byte[] pinnedRequestUtf8,
        string agentVersion,
        bool overwriteExistingCredential,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Step 1: Prevent accidental overwrite of an existing paired credential unless explicitly requested.
        if (!overwriteExistingCredential)
        {
            bool exists;
            try
            {
                exists = await _credentialStore.ExistsAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return PairingAttemptResult.Failure(PairingFailureReason.Cancelled, "Pairing was cancelled.");
            }
            catch (Exception)
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.CredentialStoreUnavailablePreFlight,
                    "Unable to inspect local credential storage before pairing.");
            }

            if (exists)
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.AlreadyPaired,
                    "This companion already has a stored device credential. Unpair or explicitly confirm replacement before pairing again.");
            }
        }

        // Step 2: Mandatory end-to-end preflight verification of the credential store (DPAPI + atomic write + cleanup)
        // BEFORE consuming the single-use pairing code or QR token on the backend.
        try
        {
            await _credentialStore.VerifyWritableAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PairingAttemptResult.Failure(PairingFailureReason.Cancelled, "Pairing was cancelled.");
        }
        catch (Exception)
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.CredentialStoreUnavailablePreFlight,
                "Local DPAPI credential storage failed preflight verification. Pairing was aborted before contacting the server.");
        }

        // Step 3: Send POST /api/public/agent/pair with redirect protection and transmit tracking.
        var endpointUri = new Uri(canonicalOrigin, PairingEndpointPath);
        using var requestContent = new TrackingUtf8JsonContent(pinnedRequestUtf8);
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, endpointUri)
        {
            Content = requestContent,
        };
        requestMessage.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        requestMessage.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (!requestMessage.Headers.UserAgent.TryParseAdd($"BeaconCompanion-Windows/{agentVersion}"))
        {
            requestMessage.Headers.UserAgent.ParseAdd("BeaconCompanion-Windows/0.3.3");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                requestMessage,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return PairingAttemptResult.Failure(
                PairingFailureReason.Cancelled,
                "Pairing was cancelled.");
        }
        catch (OperationCanceledException)
        {
            if (requestContent.HasStartedSending)
            {
                return CreateAmbiguousOutcomeResult(httpStatusCode: null);
            }

            return PairingAttemptResult.Failure(
                PairingFailureReason.NetworkErrorBeforeRequestSent,
                "Timed out connecting to the server before the pairing request was sent.");
        }
        catch (HttpRequestException)
        {
            if (requestContent.HasStartedSending)
            {
                return CreateAmbiguousOutcomeResult(httpStatusCode: null);
            }

            return PairingAttemptResult.Failure(
                PairingFailureReason.NetworkErrorBeforeRequestSent,
                "Could not establish a secure connection to the server before sending the pairing request.");
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;

            // Step 4: Block all HTTP 3xx redirects so secrets or responses are never redirected across origins.
            if (statusCode is >= 300 and <= 399)
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.UnexpectedRedirect,
                    "Server returned an unexpected HTTP redirect, which is blocked for pairing security.",
                    httpStatusCode: statusCode);
            }

            var responseBuffer = GC.AllocateArray<byte>(_options.MaxResponseBytes, pinned: true);
            int bytesRead;
            try
            {
                bytesRead = await ReadBoundedResponseBytesAsync(
                    response.Content,
                    responseBuffer,
                    timeoutCts.Token).ConfigureAwait(false);

                if (bytesRead < 0)
                {
                    return PairingAttemptResult.Failure(
                        PairingFailureReason.MalformedServerResponse,
                        "Server response exceeded the maximum allowed size.",
                        httpStatusCode: statusCode);
                }

                var responseSpan = responseBuffer.AsSpan(0, bytesRead);

                if (statusCode != (int)HttpStatusCode.OK)
                {
                    return MapNonSuccessHttpResponse(statusCode, responseSpan);
                }

                // Step 5: Parse HTTP 200 { "device_id", "device_token" } directly into a pinned DeviceCredential.
                if (!TryParseSuccessResponse(
                        responseSpan,
                        canonicalOrigin,
                        _timeProvider.GetUtcNow(),
                        out var credential,
                        out var parseError) ||
                    credential is null)
                {
                    return PairingAttemptResult.Failure(
                        PairingFailureReason.MalformedServerResponse,
                        parseError ?? "Server returned HTTP 200 with a malformed pairing payload.",
                        httpStatusCode: statusCode);
                }

                using (credential)
                {
                    // Step 6: Persist encrypted credential atomically with bounded transient I/O retry.
                    var saved = await TryPersistCredentialWithRetryAsync(credential, ct).ConfigureAwait(false);
                    if (!saved)
                    {
                        return PairingAttemptResult.Failure(
                            PairingFailureReason.CredentialPersistenceFailedAfterServerConfirm,
                            "The server confirmed pairing, but Windows could not save the encrypted credential locally. Open your authenticated Beacon dashboard (Devices page), remove the newly created offline device entry, and pair again.",
                            httpStatusCode: statusCode,
                            orphanedDeviceId: credential.Metadata.DeviceId);
                    }

                    // Step 7 & 8: Temporary buffers are zeroed in finally; return non-secret metadata.
                    return PairingAttemptResult.Success(credential.Metadata);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return PairingAttemptResult.Failure(
                    PairingFailureReason.Cancelled,
                    "Pairing was cancelled.");
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException)
            {
                return CreateAmbiguousOutcomeResult(statusCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBuffer);
            }
        }
    }

    private async Task<bool> TryPersistCredentialWithRetryAsync(
        DeviceCredential credential,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _options.LocalSaveMaxAttempts; attempt++)
        {
            try
            {
                // Do not allow caller cancellation mid-save after the server already committed confirm_pairing.
                await _credentialStore.SaveAsync(credential, CancellationToken.None).ConfigureAwait(false);
                var exists = await _credentialStore.ExistsAsync(CancellationToken.None).ConfigureAwait(false);
                if (exists)
                {
                    return true;
                }
            }
            catch (Exception) when (attempt < _options.LocalSaveMaxAttempts)
            {
                await Task.Delay(_options.LocalSaveRetryDelay, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    private static PairingAttemptResult MapNonSuccessHttpResponse(int statusCode, ReadOnlySpan<byte> bodyUtf8)
    {
        var serverErrorCode = TryReadErrorCode(bodyUtf8);

        return statusCode switch
        {
            (int)HttpStatusCode.BadRequest => PairingAttemptResult.Failure(
                PairingFailureReason.BadRequestRejectedByServer,
                "The pairing request was rejected as invalid by the server.",
                httpStatusCode: statusCode,
                serverErrorCode: serverErrorCode),

            (int)HttpStatusCode.Unauthorized => PairingAttemptResult.Failure(
                PairingFailureReason.InvalidOrLockedCodeOrToken,
                "The pairing code or QR token is invalid, cancelled, or locked after too many wrong attempts.",
                httpStatusCode: statusCode,
                serverErrorCode: serverErrorCode ?? "invalid_code"),

            (int)HttpStatusCode.Gone => PairingAttemptResult.Failure(
                PairingFailureReason.PairingSessionExpired,
                "The pairing code has expired. Generate a new code in your Beacon dashboard.",
                httpStatusCode: statusCode,
                serverErrorCode: serverErrorCode ?? "expired"),

            (int)HttpStatusCode.TooManyRequests => PairingAttemptResult.Failure(
                PairingFailureReason.ServerRateLimited,
                "Too many failed pairing attempts from this network. Wait 15 minutes before trying again.",
                httpStatusCode: statusCode,
                serverErrorCode: serverErrorCode ?? "too_many_attempts"),

            >= 500 and <= 599 when string.Equals(serverErrorCode, "server_error", StringComparison.Ordinal) =>
                PairingAttemptResult.Failure(
                    PairingFailureReason.ServerError,
                    "The server encountered an internal database error while processing pairing.",
                    httpStatusCode: statusCode,
                    serverErrorCode: serverErrorCode),

            _ => CreateAmbiguousOutcomeResult(statusCode, serverErrorCode),
        };
    }

    private static PairingAttemptResult CreateAmbiguousOutcomeResult(int? httpStatusCode, string? serverErrorCode = null) =>
        PairingAttemptResult.Failure(
            PairingFailureReason.AmbiguousServerOutcomeAfterRequestSent,
            "The pairing request was transmitted, but the connection dropped or returned an ambiguous gateway response before confirmation completed. The outcome is unknown: do not reuse this code. Inspect your authenticated Beacon dashboard (Devices page), remove any newly created offline device if present, and start a fresh pairing session.",
            httpStatusCode: httpStatusCode,
            serverErrorCode: serverErrorCode,
            isOutcomeAmbiguous: true);

    private static bool TryParseSuccessResponse(
        ReadOnlySpan<byte> utf8Json,
        Uri canonicalOrigin,
        DateTimeOffset pairedAtUtc,
        out DeviceCredential? credential,
        out string? errorDetail)
    {
        credential = null;
        errorDetail = null;

        if (utf8Json.IsEmpty)
        {
            errorDetail = "Server returned an empty HTTP 200 response.";
            return false;
        }

        Guid deviceId = Guid.Empty;
        var hasDeviceId = false;
        SensitiveCharBuffer? tokenBuffer = null;

        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                errorDetail = "Pairing response root must be a JSON object.";
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    tokenBuffer?.Dispose();
                    errorDetail = "Malformed property inside pairing response.";
                    return false;
                }

                if (reader.ValueTextEquals("device_id"u8))
                {
                    if (hasDeviceId || !reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        tokenBuffer?.Dispose();
                        errorDetail = "Property 'device_id' must be a single UUID string.";
                        return false;
                    }

                    var rawId = reader.GetString();
                    if (string.IsNullOrWhiteSpace(rawId) ||
                        !Guid.TryParseExact(rawId, "D", out deviceId) ||
                        deviceId == Guid.Empty)
                    {
                        tokenBuffer?.Dispose();
                        errorDetail = "Property 'device_id' must be a non-empty canonical UUID.";
                        return false;
                    }

                    hasDeviceId = true;
                }
                else if (reader.ValueTextEquals("device_token"u8))
                {
                    if (tokenBuffer is not null || !reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        tokenBuffer?.Dispose();
                        errorDetail = "Property 'device_token' must be a single string.";
                        return false;
                    }

                    if (reader.HasValueSequence || reader.ValueSpan.IsEmpty)
                    {
                        var fallback = reader.GetString();
                        if (string.IsNullOrEmpty(fallback))
                        {
                            errorDetail = "Property 'device_token' must not be empty.";
                            return false;
                        }

                        tokenBuffer = SensitiveCharBuffer.FromChars(fallback.AsSpan());
                    }
                    else
                    {
                        tokenBuffer = SensitiveCharBuffer.FromUtf8Bytes(reader.ValueSpan);
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        tokenBuffer?.Dispose();
                        errorDetail = "Unexpected end of JSON in pairing response.";
                        return false;
                    }

                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            tokenBuffer?.Dispose();
            errorDetail = "Server returned malformed JSON on HTTP 200.";
            return false;
        }

        if (!hasDeviceId || tokenBuffer is null)
        {
            tokenBuffer?.Dispose();
            errorDetail = "Pairing response is missing required 'device_id' or 'device_token'.";
            return false;
        }

        using (tokenBuffer)
        {
            if (!DeviceCredential.IsValidTokenFormat(tokenBuffer.Span))
            {
                errorDetail = "Server returned a 'device_token' outside the valid 20..128 ASCII format.";
                return false;
            }

            credential = new DeviceCredential(deviceId, canonicalOrigin, tokenBuffer.Span, pairedAtUtc);
            return true;
        }
    }

    private static string? TryReadErrorCode(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.IsEmpty)
        {
            return null;
        }

        try
        {
            var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 4,
            });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("error"u8))
                {
                    return reader.Read() && reader.TokenType == JsonTokenType.String
                        ? reader.GetString()
                        : null;
                }

                if (reader.TokenType == JsonTokenType.PropertyName && reader.Read())
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static async Task<int> ReadBoundedResponseBytesAsync(
        HttpContent content,
        byte[] buffer,
        CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var totalRead = 0;
            while (totalRead < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, buffer.Length - totalRead),
                    ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return totalRead;
                }

                totalRead += read;
            }

            byte[] extraByte = new byte[1];
            var extra = await stream.ReadAsync(extraByte.AsMemory(0, 1), ct).ConfigureAwait(false);
            return extra > 0 ? -1 : totalRead;
        }
    }

    private static bool TryNormalizeSixDigitPin(ReadOnlySpan<char> input, Span<char> sixDigitsOut)
    {
        if (sixDigitsOut.Length != 6)
        {
            return false;
        }

        var count = 0;
        foreach (var ch in input)
        {
            if (ch == ' ')
            {
                continue;
            }

            if (ch is < '0' or > '9')
            {
                return false;
            }

            if (count >= 6)
            {
                return false;
            }

            sixDigitsOut[count++] = ch;
        }

        return count == 6;
    }

    private static byte[] WritePinPairingRequestUtf8(
        ReadOnlySpan<char> sixDigits,
        PairingDeviceMetadata device)
    {
        using var bufferWriter = new PinnedZeroingBufferWriter(256);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("code"u8, sixDigits);
            WriteDeviceObject(writer, device);
            writer.WriteEndObject();
        }

        return bufferWriter.ToPinnedArray();
    }

    private static byte[] WriteQrPairingRequestUtf8(
        Guid pairingId,
        ReadOnlySpan<char> token,
        PairingDeviceMetadata device)
    {
        using var bufferWriter = new PinnedZeroingBufferWriter(384);
        using (var writer = new Utf8JsonWriter(bufferWriter))
        {
            writer.WriteStartObject();
            writer.WriteString("pairing_id"u8, pairingId.ToString("D"));
            writer.WriteString("token"u8, token);
            WriteDeviceObject(writer, device);
            writer.WriteEndObject();
        }

        return bufferWriter.ToPinnedArray();
    }

    /// <summary>
    /// Minimal pinned <see cref="IBufferWriter{T}"/> for serializing secret pairing JSON payloads.
    /// Owns a writable pinned <see cref="byte"/> array so both resized intermediate buffers and the
    /// final backing buffer are deterministically zeroed via <see cref="CryptographicOperations.ZeroMemory(Span{byte})"/>.
    /// </summary>
    private sealed class PinnedZeroingBufferWriter : IBufferWriter<byte>, IDisposable
    {
        private byte[] _buffer;
        private int _written;
        private bool _disposed;

        public PinnedZeroingBufferWriter(int initialCapacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
            _buffer = GC.AllocateArray<byte>(initialCapacity, pinned: true);
        }

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (_written > _buffer.Length - count)
            {
                throw new InvalidOperationException("Cannot advance past the end of the pinned buffer.");
            }

            _written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureCapacity(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureCapacity(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public byte[] ToPinnedArray()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var copy = GC.AllocateArray<byte>(_written, pinned: true);
            _buffer.AsSpan(0, _written).CopyTo(copy);
            return copy;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CryptographicOperations.ZeroMemory(_buffer);
                _written = 0;
                _disposed = true;
            }
        }

        private void EnsureCapacity(int sizeHint)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            var required = sizeHint == 0 ? 1 : sizeHint;
            if (_buffer.Length - _written >= required)
            {
                return;
            }

            var minNewLength = checked(_written + required);
            var doubled = _buffer.Length <= int.MaxValue / 2 ? _buffer.Length * 2 : int.MaxValue;
            var newLength = Math.Max(doubled, minNewLength);

            var next = GC.AllocateArray<byte>(newLength, pinned: true);
            _buffer.AsSpan(0, _written).CopyTo(next);
            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = next;
        }
    }

    private static void WriteDeviceObject(Utf8JsonWriter writer, PairingDeviceMetadata device)
    {
        writer.WritePropertyName("device"u8);
        writer.WriteStartObject();
        writer.WriteString("name"u8, device.Name);
        writer.WriteString("os"u8, device.Os);
        writer.WriteString("agent_version"u8, device.AgentVersion);
        writer.WriteEndObject();
    }

    private bool IsPinRateLimited(out TimeSpan remainingCooldown)
    {
        lock (_pinThrottleLock)
        {
            var now = _timeProvider.GetUtcNow();
            if (now < _pinCooldownUntilUtc)
            {
                remainingCooldown = _pinCooldownUntilUtc - now;
                return true;
            }

            remainingCooldown = TimeSpan.Zero;
            return false;
        }
    }

    private void RecordPinAttemptOutcome(PairingAttemptResult result)
    {
        lock (_pinThrottleLock)
        {
            if (result.Succeeded)
            {
                _consecutivePinFailures = 0;
                _pinCooldownUntilUtc = DateTimeOffset.MinValue;
                return;
            }

            if (result.FailureReason == PairingFailureReason.InvalidOrLockedCodeOrToken)
            {
                _consecutivePinFailures++;
                if (_consecutivePinFailures >= _options.MaxFailedPinAttemptsBeforeCooldown)
                {
                    _pinCooldownUntilUtc = _timeProvider.GetUtcNow() + _options.PinCooldownDuration;
                    _consecutivePinFailures = 0;
                }
            }
            else if (result.FailureReason == PairingFailureReason.ServerRateLimited)
            {
                _pinCooldownUntilUtc = _timeProvider.GetUtcNow() + _options.PinCooldownDuration;
            }
        }
    }

    private sealed class TrackingUtf8JsonContent : HttpContent
    {
        private readonly byte[] _utf8Bytes;
        private int _startedSending;

        public TrackingUtf8JsonContent(byte[] utf8Bytes)
        {
            _utf8Bytes = utf8Bytes;
            Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8",
            };
        }

        public bool HasStartedSending => Volatile.Read(ref _startedSending) != 0;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Exchange(ref _startedSending, 1);
            return stream.WriteAsync(_utf8Bytes.AsMemory()).AsTask();
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref _startedSending, 1);
            return stream.WriteAsync(_utf8Bytes.AsMemory(), cancellationToken).AsTask();
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _utf8Bytes.Length;
            return true;
        }
    }
}
