using System.Runtime.Versioning;

namespace Beacon.Companion.Core.Configuration;

/// <summary>
/// Controls whether a remote session may start without interactive local owner consent.
/// Interactive Full Control defaults to requiring explicit laptop-owner approval.
/// </summary>
public enum UnattendedAccessPolicy
{
    /// <summary>Every incoming session requires explicit approval by the logged-in user.</summary>
    Disabled = 0,

    /// <summary>Allows view-only monitoring when unattended; input injection still requires local approval.</summary>
    AllowViewOnly = 1,

    /// <summary>Allows pre-authorized unattended full control only when explicitly enabled by the laptop owner.</summary>
    AllowFullControlWithPreAuthorization = 2,
}

/// <summary>
/// Runtime configuration for the interactive user-session Beacon Windows companion.
/// Contains zero secrets or database credentials.
/// </summary>
public sealed record CompanionOptions
{
    public Uri? ServerOrigin { get; init; }

    public string DeviceName { get; init; } = Environment.MachineName;

    public string AgentVersion { get; init; } = "0.3.1";

    /// <summary>Must remain well below server HEARTBEAT_ONLINE_WINDOW_MS (90s).</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Automatic input release timeout when modifiers/buttons stay held without session traffic.</summary>
    public TimeSpan DeadmanInputTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public int MaxDiscreteInputEventsPerSecond { get; init; } = 120;

    public bool RequireOwnerApprovalForFullControl { get; init; } = true;

    public UnattendedAccessPolicy UnattendedAccess { get; init; } = UnattendedAccessPolicy.Disabled;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Trim().Length > 64)
        {
            throw new ArgumentException("DeviceName must be 1..64 non-whitespace characters.", nameof(DeviceName));
        }

        if (string.IsNullOrWhiteSpace(AgentVersion) || AgentVersion.Trim().Length > 32)
        {
            throw new ArgumentException("AgentVersion must be 1..32 non-whitespace characters.", nameof(AgentVersion));
        }

        if (HeartbeatInterval <= TimeSpan.Zero || HeartbeatInterval >= TimeSpan.FromSeconds(90))
        {
            throw new ArgumentOutOfRangeException(nameof(HeartbeatInterval), "HeartbeatInterval must be > 0 and < 90 seconds.");
        }

        if (DeadmanInputTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(DeadmanInputTimeout));
        }

        if (MaxDiscreteInputEventsPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDiscreteInputEventsPerSecond));
        }

        if (ServerOrigin is not null && !IsAllowedServerOrigin(ServerOrigin))
        {
            throw new ArgumentException("ServerOrigin must use HTTPS (or HTTP only on localhost for development).", nameof(ServerOrigin));
        }
    }

    public static bool IsAllowedServerOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        return Security.ServerOriginValidator.IsAllowedServerOrigin(origin, allowDevelopmentLoopbackHttp: true);
    }
}

/// <summary>
/// Runtime OS compatibility checks so Windows 10 (19041+) and Windows 11 (22621+)
/// capabilities are probed dynamically rather than assumed from compilation alone.
/// </summary>
public static class WindowsPlatformSupport
{
    public const int MinimumWindows10Build = 19041;
    public const int BorderlessWgcWindows11Build = 22621;

    public static bool IsSupportedWindowsDesktop() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, MinimumWindows10Build);

    [SupportedOSPlatformGuard("windows10.0.22621.0")]
    public static bool SupportsBorderlessWindowCapture() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, BorderlessWgcWindows11Build);
}
