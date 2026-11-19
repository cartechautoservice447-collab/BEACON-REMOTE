using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Heartbeat;

public interface IHeartbeatClient
{
    Task<HeartbeatAttemptResult> SendHeartbeatAsync(DeviceCredential credential, CancellationToken ct = default);
}

/// <summary>
/// Hardened HTTP client for <c>POST /api/public/agent/heartbeat</c>.
/// Enforces origin validation, redirect blocking, bounded JSON response parsing,
/// zeroed response buffers, and strict classification of server revocation vs transient failures.
/// </summary>
public sealed class HeartbeatClient : IHeartbeatClient
{
    public const string HeartbeatEndpointPath = "/api/public/agent/heartbeat";

    private static readonly TimeSpan MaxRetryAfterHint = TimeSpan.FromSeconds(85);

    private readonly HttpClient _httpClient;
    private readonly HeartbeatClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public HeartbeatClient(
        HttpClient httpClient,
        HeartbeatClientOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _options = options ?? new HeartbeatClientOptions();
        _options.Validate();

        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a hardened <see cref="HttpClient"/> matching the pairing security posture:
    /// <c>AllowAutoRedirect = false</c>, <c>UseCookies = false</c>, and TLS 1.2 / 1.3 only.
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

    public async Task<HeartbeatAttemptResult> SendHeartbeatAsync(
        DeviceCredential credential,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        if (ct.IsCancellationRequested)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.Cancelled,
                "Heartbeat was cancelled before sending.",
                deviceId: TryReadDeviceId(credential));
        }

        Guid deviceId;
        Uri rawOrigin;
        string bearerToken;
        try
        {
            if (credential.IsDisposed)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.InvalidCredential,
                    "Device credential has already been disposed.");
            }

            deviceId = credential.Metadata.DeviceId;
            rawOrigin = credential.Metadata.ServerOrigin;

            var tokenSpan = credential.Token;
            if (!DeviceCredential.IsValidTokenFormat(tokenSpan))
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.InvalidCredential,
                    "Device token does not conform to the required format.",
                    deviceId: deviceId);
            }

            bearerToken = new string(tokenSpan);
        }
        catch (ObjectDisposedException)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.InvalidCredential,
                "Device credential was disposed before the heartbeat could be prepared.");
        }

        var originValidation = ServerOriginValidator.Validate(rawOrigin, _options.AllowDevelopmentLoopbackHttp);
        if (!originValidation.IsValid || originValidation.CanonicalOrigin is null)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.InvalidCredential,
                originValidation.ErrorDetail ?? "Device credential ServerOrigin failed validation.",
                deviceId: deviceId);
        }

        var endpointUri = new Uri(originValidation.CanonicalOrigin, HeartbeatEndpointPath);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (!request.Headers.UserAgent.TryParseAdd($"BeaconCompanion-Windows/{_options.AgentVersion.Trim()}"))
        {
            request.Headers.UserAgent.ParseAdd("BeaconCompanion-Windows/0.3.4");
        }

        using var timeoutCts = new CancellationTokenSource(_options.RequestTimeout, _timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.Cancelled,
                "Heartbeat was cancelled by the caller.",
                deviceId: deviceId);
        }
        catch (OperationCanceledException)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.Timeout,
                "Heartbeat request timed out waiting for the server.",
                deviceId: deviceId);
        }
        catch (HttpRequestException)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.NetworkError,
                "Network connection to the Beacon server failed during heartbeat.",
                deviceId: deviceId);
        }
        catch (IOException)
        {
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.NetworkError,
                "Network I/O failed while contacting the Beacon server during heartbeat.",
                deviceId: deviceId);
        }

        using (response)
        {
            var statusCode = (int)response.StatusCode;

            // Block redirects unconditionally so bearer tokens or auth status are never inferred across redirects.
            if (statusCode is >= 300 and <= 399)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.UnexpectedRedirect,
                    "Server returned an HTTP redirect on the heartbeat endpoint, which is blocked.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode);
            }

            var responseBuffer = GC.AllocateArray<byte>(_options.MaxResponseBytes, pinned: true);
            try
            {
                var bytesRead = await ReadBoundedResponseBytesAsync(
                    response.Content,
                    responseBuffer,
                    linkedCts.Token).ConfigureAwait(false);

                if (bytesRead < 0)
                {
                    return HeartbeatAttemptResult.Failure(
                        HeartbeatOutcomeKind.MalformedResponse,
                        "Heartbeat response exceeded the maximum allowed payload size.",
                        deviceId: deviceId,
                        httpStatusCode: statusCode);
                }

                var responseSpan = responseBuffer.AsSpan(0, bytesRead);
                return ClassifyResponse(deviceId, statusCode, response.Headers.RetryAfter, responseSpan);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Cancelled,
                    "Heartbeat response read was cancelled by the caller.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode);
            }
            catch (OperationCanceledException)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Timeout,
                    "Timed out reading heartbeat response from the server.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode);
            }
            catch (HttpRequestException)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.NetworkError,
                    "Connection interrupted while reading heartbeat response.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode);
            }
            catch (IOException)
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.NetworkError,
                    "Stream I/O interrupted while reading heartbeat response.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(responseBuffer);
            }
        }
    }

    private static Guid? TryReadDeviceId(DeviceCredential credential)
    {
        try
        {
            return credential.IsDisposed ? null : credential.Metadata.DeviceId;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static HeartbeatAttemptResult ClassifyResponse(
        Guid deviceId,
        int statusCode,
        RetryConditionHeaderValue? retryAfterHeader,
        ReadOnlySpan<byte> bodyUtf8)
    {
        if (statusCode == (int)HttpStatusCode.OK)
        {
            if (TryParseSuccessBody(bodyUtf8, out var serverTimeUtc, out var parseError))
            {
                return HeartbeatAttemptResult.Success(deviceId, serverTimeUtc, statusCode);
            }

            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.MalformedResponse,
                parseError ?? "Server returned HTTP 200 with a malformed heartbeat response.",
                deviceId: deviceId,
                httpStatusCode: statusCode);
        }

        if (statusCode == (int)HttpStatusCode.Unauthorized)
        {
            // Backend returns 401 { "error": "unauthorized" } when device_credentials are deleted or revoked.
            // Guard against non-JSON captive-portal HTML falsely wiping local DPAPI credentials.
            if (IsWhitespaceOnlyUtf8(bodyUtf8))
            {
                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Revoked,
                    "Server rejected the device credential with HTTP 401.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode,
                    serverErrorCode: "unauthorized");
            }

            if (TryReadJsonErrorCode(bodyUtf8, out var errorCode))
            {
                if (IsConfirmedRejectionErrorCode(errorCode))
                {
                    return HeartbeatAttemptResult.Failure(
                        HeartbeatOutcomeKind.Revoked,
                        "Server confirmed the device credential is unauthorized or revoked.",
                        deviceId: deviceId,
                        httpStatusCode: statusCode,
                        serverErrorCode: errorCode ?? "unauthorized");
                }

                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Revoked,
                    "Server rejected the device credential with HTTP 401 JSON error.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode,
                    serverErrorCode: errorCode);
            }

            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.MalformedResponse,
                "Received HTTP 401 with a non-JSON body (possible captive portal or proxy interception).",
                deviceId: deviceId,
                httpStatusCode: statusCode);
        }

        if (statusCode == (int)HttpStatusCode.Forbidden)
        {
            if (TryReadJsonErrorCode(bodyUtf8, out var errorCode))
            {
                if (IsConfirmedRejectionErrorCode(errorCode))
                {
                    return HeartbeatAttemptResult.Failure(
                        HeartbeatOutcomeKind.Revoked,
                        "Server confirmed device revocation with HTTP 403.",
                        deviceId: deviceId,
                        httpStatusCode: statusCode,
                        serverErrorCode: errorCode);
                }

                return HeartbeatAttemptResult.Failure(
                    HeartbeatOutcomeKind.Forbidden,
                    "Server returned HTTP 403 Forbidden for the heartbeat request.",
                    deviceId: deviceId,
                    httpStatusCode: statusCode,
                    serverErrorCode: errorCode);
            }

            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.MalformedResponse,
                "Received HTTP 403 with a non-JSON response body.",
                deviceId: deviceId,
                httpStatusCode: statusCode);
        }

        if (statusCode is (int)HttpStatusCode.RequestTimeout or (int)HttpStatusCode.TooManyRequests ||
            statusCode is >= 500 and <= 599)
        {
            _ = TryReadJsonErrorCode(bodyUtf8, out var errorCode);
            var retryHint = ExtractBoundedRetryAfter(retryAfterHeader);
            return HeartbeatAttemptResult.Failure(
                HeartbeatOutcomeKind.TransientServerError,
                $"Server temporarily unavailable (HTTP {statusCode}).",
                deviceId: deviceId,
                httpStatusCode: statusCode,
                serverErrorCode: errorCode,
                retryAfterHint: retryHint);
        }

        _ = TryReadJsonErrorCode(bodyUtf8, out var unexpectedErrorCode);
        return HeartbeatAttemptResult.Failure(
            HeartbeatOutcomeKind.MalformedResponse,
            $"Unexpected HTTP {statusCode} response from heartbeat endpoint.",
            deviceId: deviceId,
            httpStatusCode: statusCode,
            serverErrorCode: unexpectedErrorCode);
    }

    private static bool IsConfirmedRejectionErrorCode(string? errorCode) =>
        errorCode is null or
            "unauthorized" or
            "revoked" or
            "device_revoked" or
            "credential_revoked" or
            "invalid_token" or
            "token_expired";

    private static TimeSpan? ExtractBoundedRetryAfter(RetryConditionHeaderValue? header)
    {
        if (header?.Delta is not { } delta || delta <= TimeSpan.Zero)
        {
            return null;
        }

        return delta > MaxRetryAfterHint ? MaxRetryAfterHint : delta;
    }

    private static bool TryParseSuccessBody(
        ReadOnlySpan<byte> utf8Json,
        out DateTimeOffset serverTimeUtc,
        out string? errorDetail)
    {
        serverTimeUtc = default;
        errorDetail = null;

        if (IsWhitespaceOnlyUtf8(utf8Json))
        {
            errorDetail = "Heartbeat HTTP 200 response body was empty.";
            return false;
        }

        bool? okValue = null;
        string? rawServerTime = null;

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
                errorDetail = "Heartbeat HTTP 200 root must be a JSON object.";
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
                    errorDetail = "Malformed JSON property in heartbeat response.";
                    return false;
                }

                if (reader.ValueTextEquals("ok"u8))
                {
                    if (okValue.HasValue || !reader.Read() ||
                        reader.TokenType is not (JsonTokenType.True or JsonTokenType.False))
                    {
                        errorDetail = "Property 'ok' must be a single JSON boolean.";
                        return false;
                    }

                    okValue = reader.GetBoolean();
                }
                else if (reader.ValueTextEquals("server_time"u8))
                {
                    if (rawServerTime is not null || !reader.Read() || reader.TokenType != JsonTokenType.String)
                    {
                        errorDetail = "Property 'server_time' must be a single ISO-8601 string.";
                        return false;
                    }

                    rawServerTime = reader.GetString();
                }
                else
                {
                    if (!reader.Read())
                    {
                        errorDetail = "Unexpected end of heartbeat JSON payload.";
                        return false;
                    }

                    reader.Skip();
                }
            }

            // Ensure no trailing garbage after closing object brace.
            if (reader.Read())
            {
                errorDetail = "Unexpected trailing tokens after heartbeat JSON object.";
                return false;
            }
        }
        catch (JsonException)
        {
            errorDetail = "Heartbeat HTTP 200 body contained invalid JSON.";
            return false;
        }

        if (okValue != true)
        {
            errorDetail = "Heartbeat HTTP 200 response did not contain 'ok: true'.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(rawServerTime) ||
            !DateTimeOffset.TryParse(
                rawServerTime,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsedTime))
        {
            errorDetail = "Heartbeat HTTP 200 response is missing a valid ISO-8601 'server_time'.";
            return false;
        }

        serverTimeUtc = parsedTime.ToUniversalTime();
        return true;
    }

    private static bool TryReadJsonErrorCode(ReadOnlySpan<byte> utf8Json, out string? errorCode)
    {
        errorCode = null;
        if (IsWhitespaceOnlyUtf8(utf8Json))
        {
            return false;
        }

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
                    return false;
                }

                if (reader.ValueTextEquals("error"u8))
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    if (reader.TokenType == JsonTokenType.String)
                    {
                        errorCode = reader.GetString();
                    }
                    else if (reader.TokenType != JsonTokenType.Null)
                    {
                        return false;
                    }
                }
                else
                {
                    if (!reader.Read())
                    {
                        return false;
                    }

                    reader.Skip();
                }
            }

            return true;
        }
        catch (JsonException)
        {
            errorCode = null;
            return false;
        }
    }

    private static bool IsWhitespaceOnlyUtf8(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
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
}
