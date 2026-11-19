using System.Text.Json.Serialization;
using Beacon.Companion.Core.Configuration;

namespace Beacon.Companion.Core.Heartbeat;

/// <summary>
/// Authoritative companion pairing and server authorization state.
/// Distinct from transient network connectivity so temporary outages do not erase pairing,
/// and confirmed server revocation is latched until a fresh authorized pairing succeeds.
/// </summary>
public enum DeviceAuthorizationStatus
{
    Unpaired = 0,
    Authorized = 1,
    Revoked = 2,
    CredentialInvalid = 3,
}

/// <summary>
/// Runtime lifecycle and network connectivity state reported by the Heartbeat Supervisor.
/// </summary>
public enum HeartbeatConnectivityState
{
    Idle = 0,
    Starting = 1,
    Healthy = 2,
    Retrying = 3,
    Disconnected = 4,
    Revoked = 5,
    Stopped = 6,
}

/// <summary>
/// Typed outcome classification for a single <c>POST /api/public/agent/heartbeat</c> exchange.
/// </summary>
public enum HeartbeatOutcomeKind
{
    Succeeded = 0,
    Revoked = 1,
    Forbidden = 2,
    TransientServerError = 3,
    NetworkError = 4,
    Timeout = 5,
    Cancelled = 6,
    MalformedResponse = 7,
    UnexpectedRedirect = 8,
    InvalidCredential = 9,
}

/// <summary>
/// Wire contract for HTTP 200 success responses from <c>POST /api/public/agent/heartbeat</c>.
/// </summary>
public sealed record HeartbeatSuccessResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("server_time")] string? ServerTime);

/// <summary>
/// Wire contract for JSON error responses from <c>POST /api/public/agent/heartbeat</c>.
/// </summary>
public sealed record HeartbeatErrorResponse(
    [property: JsonPropertyName("error")] string? Error);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HeartbeatSuccessResponse))]
[JsonSerializable(typeof(HeartbeatErrorResponse))]
public sealed partial class HeartbeatJsonContext : JsonSerializerContext;

public sealed record HeartbeatAttemptResult(
    bool Succeeded,
    HeartbeatOutcomeKind Outcome,
    Guid? DeviceId = null,
    DateTimeOffset? ServerTimeUtc = null,
    int? HttpStatusCode = null,
    string? ServerErrorCode = null,
    TimeSpan? RetryAfterHint = null,
    string? Detail = null)
{
    public bool IsConfirmedRevocation => Outcome == HeartbeatOutcomeKind.Revoked;

    public bool IsTransientFailure => Outcome is
        HeartbeatOutcomeKind.TransientServerError or
        HeartbeatOutcomeKind.NetworkError or
        HeartbeatOutcomeKind.Timeout or
        HeartbeatOutcomeKind.MalformedResponse or
        HeartbeatOutcomeKind.UnexpectedRedirect;

    public static HeartbeatAttemptResult Success(
        Guid deviceId,
        DateTimeOffset serverTimeUtc,
        int httpStatusCode = 200) =>
        new(
            Succeeded: true,
            Outcome: HeartbeatOutcomeKind.Succeeded,
            DeviceId: deviceId,
            ServerTimeUtc: serverTimeUtc,
            HttpStatusCode: httpStatusCode);

    public static HeartbeatAttemptResult Failure(
        HeartbeatOutcomeKind outcome,
        string detail,
        Guid? deviceId = null,
        int? httpStatusCode = null,
        string? serverErrorCode = null,
        TimeSpan? retryAfterHint = null) =>
        new(
            Succeeded: false,
            Outcome: outcome,
            DeviceId: deviceId,
            ServerTimeUtc: null,
            HttpStatusCode: httpStatusCode,
            ServerErrorCode: serverErrorCode,
            RetryAfterHint: retryAfterHint,
            Detail: detail);
}

public sealed record HeartbeatClientOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxResponseBytes { get; init; } = 4096;

    public bool AllowDevelopmentLoopbackHttp { get; init; } = true;

    public string AgentVersion { get; init; } = "0.3.4";

    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxResponseBytes, 256);

        if (string.IsNullOrWhiteSpace(AgentVersion) || AgentVersion.Trim().Length > 32)
        {
            throw new ArgumentException("AgentVersion must be 1..32 non-whitespace characters.", nameof(AgentVersion));
        }
    }
}

public sealed record HeartbeatSupervisorOptions
{
    /// <summary>Must remain strictly below backend HEARTBEAT_ONLINE_WINDOW_MS (90s).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Maximum exponential backoff cap between transient failures.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Symmetric jitter ratio applied to backoff delays (0.0..0.5).</summary>
    public double JitterRatio { get; init; } = 0.2;

    /// <summary>Number of consecutive transient failures before transitioning from Retrying to Disconnected.</summary>
    public int ConsecutiveFailuresBeforeDisconnected { get; init; } = 2;

    /// <summary>
    /// Controls whether confirmed server revocation deletes the stored local credential envelope.
    /// Defaults to true so compromised or revoked credentials are purged promptly.
    /// </summary>
    public bool DeleteLocalCredentialOnRevocation { get; init; } = true;

    public bool AllowDevelopmentLoopbackHttp { get; init; } = true;

    public string AgentVersion { get; init; } = "0.3.4";

    public static HeartbeatSupervisorOptions FromCompanionOptions(CompanionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        return new HeartbeatSupervisorOptions
        {
            HeartbeatInterval = options.HeartbeatInterval,
            AgentVersion = options.AgentVersion,
        };
    }

    public void Validate()
    {
        if (HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval >= TimeSpan.FromSeconds(90))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatInterval),
                "HeartbeatInterval must be > 0 and < 90 seconds to remain within the server online window.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(RequestTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(InitialRetryDelay, TimeSpan.Zero);

        if (MaxRetryDelay < InitialRetryDelay || MaxRetryDelay >= TimeSpan.FromSeconds(90))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxRetryDelay),
                "MaxRetryDelay must be >= InitialRetryDelay and < 90 seconds.");
        }

        if (!double.IsFinite(JitterRatio) || JitterRatio < 0.0 || JitterRatio > 0.5)
        {
            throw new ArgumentOutOfRangeException(
                nameof(JitterRatio),
                "JitterRatio must be finite and within [0.0, 0.5].");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(ConsecutiveFailuresBeforeDisconnected, 1);

        if (string.IsNullOrWhiteSpace(AgentVersion) || AgentVersion.Trim().Length > 32)
        {
            throw new ArgumentException("AgentVersion must be 1..32 non-whitespace characters.", nameof(AgentVersion));
        }
    }
}

public sealed record HeartbeatSnapshot(
    DeviceAuthorizationStatus AuthorizationStatus,
    HeartbeatConnectivityState ConnectivityState,
    Guid? DeviceId,
    Uri? ServerOrigin,
    long Epoch,
    int ConsecutiveTransientFailures,
    long TotalSucceededCount,
    long TotalTransientFailureCount,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastServerTimeUtc,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? RevokedAtUtc,
    HeartbeatOutcomeKind? LastOutcome,
    string? LastErrorDetail)
{
    public bool IsAuthorized => AuthorizationStatus == DeviceAuthorizationStatus.Authorized;

    public bool IsRevoked => AuthorizationStatus == DeviceAuthorizationStatus.Revoked;
}

public sealed class DeviceRevokedEventArgs(
    Guid? deviceId,
    DateTimeOffset revokedAtUtc,
    string? serverErrorCode,
    string reason) : EventArgs
{
    public Guid? DeviceId { get; } = deviceId;

    public DateTimeOffset RevokedAtUtc { get; } = revokedAtUtc;

    public string? ServerErrorCode { get; } = serverErrorCode;

    public string Reason { get; } = reason;
}

public interface IJitterSource
{
    /// <summary>Returns a sample within [0.0, 1.0).</summary>
    double NextUnitDouble();
}

public sealed class CryptoJitterSource : IJitterSource
{
    public static CryptoJitterSource Shared { get; } = new();

    public double NextUnitDouble() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(1_000_000) / 1_000_000.0;
}

public sealed class DeviceRevokedException : InvalidOperationException
{
    public DeviceRevokedEventArgs? RevocationDetails { get; }

    public DeviceRevokedException()
        : base("This companion device has been revoked by the server and must be paired again before continuing.")
    {
    }

    public DeviceRevokedException(string message)
        : base(message)
    {
    }

    public DeviceRevokedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DeviceRevokedException(string message, DeviceRevokedEventArgs details)
        : base(message)
    {
        RevocationDetails = details;
    }
}

public sealed class DeviceNotAuthorizedException : InvalidOperationException
{
    public DeviceAuthorizationStatus AuthorizationStatus { get; }

    public DeviceNotAuthorizedException()
        : base("This companion device does not hold a valid active authorization.")
    {
        AuthorizationStatus = DeviceAuthorizationStatus.Unpaired;
    }

    public DeviceNotAuthorizedException(string message)
        : base(message)
    {
        AuthorizationStatus = DeviceAuthorizationStatus.Unpaired;
    }

    public DeviceNotAuthorizedException(string message, Exception innerException)
        : base(message, innerException)
    {
        AuthorizationStatus = DeviceAuthorizationStatus.Unpaired;
    }

    public DeviceNotAuthorizedException(string message, DeviceAuthorizationStatus status)
        : base(message)
    {
        AuthorizationStatus = status;
    }
}
