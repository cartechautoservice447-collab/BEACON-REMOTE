using System.Net;
using System.Net.Sockets;

namespace Beacon.Companion.Core.Security;

public enum ServerOriginValidationError
{
    None = 0,
    NullOrEmpty = 1,
    NotAbsoluteUri = 2,
    InvalidScheme = 3,
    InsecureHttpNotLoopback = 4,
    ContainsUserInfo = 5,
    ContainsNonRootPath = 6,
    ContainsQueryOrFragment = 7,
    InvalidHost = 8,
    UnspecifiedOrLinkLocalAddress = 9,
}

public sealed record ServerOriginValidationResult(
    bool IsValid,
    Uri? CanonicalOrigin = null,
    ServerOriginValidationError Error = ServerOriginValidationError.None,
    string? ErrorDetail = null)
{
    public static ServerOriginValidationResult Valid(Uri canonicalOrigin)
    {
        ArgumentNullException.ThrowIfNull(canonicalOrigin);
        return new(true, canonicalOrigin);
    }

    public static ServerOriginValidationResult Invalid(ServerOriginValidationError error, string detail) =>
        new(false, null, error, detail);
}

public enum OriginApprovalMode
{
    /// <summary>Only the companion's configured trusted <c>ServerOrigin</c> is accepted.</summary>
    ConfiguredOriginOnly = 0,

    /// <summary>The laptop owner explicitly inspected and approved the custom server origin in UI.</summary>
    ExplicitlyApprovedCustomOrigin = 1,
}

public sealed record ServerOriginTrustDecision(
    bool IsTrusted,
    Uri? CanonicalOrigin = null,
    bool IsCustomOrigin = false,
    string? RejectionReason = null);

public interface IServerOriginTrustPolicy
{
    ServerOriginTrustDecision Evaluate(
        Uri candidateOrigin,
        Uri? configuredOrigin,
        OriginApprovalMode approvalMode,
        Uri? explicitlyApprovedCustomOrigin = null,
        bool allowDevelopmentLoopbackHttp = true);
}

/// <summary>
/// Validates server origins for pairing and credential storage.
/// Rejects embedded credentials, non-root paths, query strings, fragments, non-HTTPS non-loopback origins,
/// and unapproved custom QR destinations.
/// </summary>
public static class ServerOriginValidator
{
    public static ServerOriginValidationResult Validate(
        string? rawOrigin,
        bool allowDevelopmentLoopbackHttp = true)
    {
        if (string.IsNullOrWhiteSpace(rawOrigin))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.NullOrEmpty,
                "Server origin must not be empty.");
        }

        var trimmed = rawOrigin.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal) ||
            !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.NotAbsoluteUri,
                "Server origin must be an absolute URI.");
        }

        return Validate(uri, allowDevelopmentLoopbackHttp);
    }

    public static ServerOriginValidationResult Validate(
        Uri? origin,
        bool allowDevelopmentLoopbackHttp = true)
    {
        if (origin is null)
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.NullOrEmpty,
                "Server origin must not be null.");
        }

        if (!origin.IsAbsoluteUri)
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.NotAbsoluteUri,
                "Server origin must be an absolute URI.");
        }

        var isHttps = origin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isHttp = origin.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (!isHttps && !isHttp)
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.InvalidScheme,
                "Server origin scheme must be 'https' (or 'http' on loopback for development).");
        }

        if (!string.IsNullOrEmpty(origin.UserInfo))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.ContainsUserInfo,
                "Server origin must not contain user credentials.");
        }

        if (!string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.ContainsQueryOrFragment,
                "Server origin must not contain a query string or fragment.");
        }

        if (!string.Equals(origin.AbsolutePath, "/", StringComparison.Ordinal))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.ContainsNonRootPath,
                "Server origin must not contain a URL path.");
        }

        // Also guard against raw strings ending with "?#" or "//" anomalies in OriginalString.
        var raw = origin.OriginalString.Trim();
        if (raw.Contains('?', StringComparison.Ordinal) || raw.Contains('#', StringComparison.Ordinal))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.ContainsQueryOrFragment,
                "Server origin must not contain empty query or fragment delimiters.");
        }

        if (string.IsNullOrWhiteSpace(origin.Host) ||
            origin.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.InvalidHost,
                "Server origin host is invalid.");
        }

        if (IPAddress.TryParse(origin.DnsSafeHost, out var ipAddress) &&
            IsUnspecifiedOrLinkLocal(ipAddress))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.UnspecifiedOrLinkLocalAddress,
                "Server origin must not target an unspecified (0.0.0.0/::) or link-local IP address.");
        }

        if (isHttp && (!allowDevelopmentLoopbackHttp || !origin.IsLoopback))
        {
            return ServerOriginValidationResult.Invalid(
                ServerOriginValidationError.InsecureHttpNotLoopback,
                "Plain HTTP is only permitted for localhost/loopback during development.");
        }

        var canonicalAuthority = origin.GetLeftPart(UriPartial.Authority);
        return ServerOriginValidationResult.Valid(new Uri(canonicalAuthority, UriKind.Absolute));
    }

    public static bool IsAllowedServerOrigin(Uri origin, bool allowDevelopmentLoopbackHttp = true) =>
        Validate(origin, allowDevelopmentLoopbackHttp).IsValid;

    public static bool AreSameCanonicalOrigin(Uri left, Uri right)
    {
        var leftRes = Validate(left);
        var rightRes = Validate(right);
        if (!leftRes.IsValid || !rightRes.IsValid)
        {
            return false;
        }

        return Uri.Compare(
            leftRes.CanonicalOrigin,
            rightRes.CanonicalOrigin,
            UriComponents.SchemeAndServer,
            UriFormat.Unescaped,
            StringComparison.OrdinalIgnoreCase) == 0;
    }

    private static bool IsUnspecifiedOrLinkLocal(IPAddress address)
    {
        if (IPAddress.Any.Equals(address) || IPAddress.IPv6Any.Equals(address) || IPAddress.Broadcast.Equals(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            if (address.TryWriteBytes(bytes, out _))
            {
                // 169.254.0.0/16 (APIPA link-local) or 0.0.0.0/8 ("this" network)
                return bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254);
            }
        }

        return false;
    }
}

public sealed class DefaultServerOriginTrustPolicy : IServerOriginTrustPolicy
{
    public ServerOriginTrustDecision Evaluate(
        Uri candidateOrigin,
        Uri? configuredOrigin,
        OriginApprovalMode approvalMode,
        Uri? explicitlyApprovedCustomOrigin = null,
        bool allowDevelopmentLoopbackHttp = true)
    {
        var candidateValidation = ServerOriginValidator.Validate(candidateOrigin, allowDevelopmentLoopbackHttp);
        if (!candidateValidation.IsValid || candidateValidation.CanonicalOrigin is null)
        {
            return new ServerOriginTrustDecision(
                IsTrusted: false,
                RejectionReason: candidateValidation.ErrorDetail ?? "Candidate server origin is invalid.");
        }

        var canonicalCandidate = candidateValidation.CanonicalOrigin;

        if (configuredOrigin is not null)
        {
            var configuredValidation = ServerOriginValidator.Validate(configuredOrigin, allowDevelopmentLoopbackHttp);
            if (configuredValidation.IsValid &&
                configuredValidation.CanonicalOrigin is not null &&
                ServerOriginValidator.AreSameCanonicalOrigin(canonicalCandidate, configuredValidation.CanonicalOrigin))
            {
                return new ServerOriginTrustDecision(
                    IsTrusted: true,
                    CanonicalOrigin: canonicalCandidate,
                    IsCustomOrigin: false);
            }
        }

        if (approvalMode == OriginApprovalMode.ExplicitlyApprovedCustomOrigin &&
            explicitlyApprovedCustomOrigin is not null &&
            ServerOriginValidator.AreSameCanonicalOrigin(canonicalCandidate, explicitlyApprovedCustomOrigin))
        {
            return new ServerOriginTrustDecision(
                IsTrusted: true,
                CanonicalOrigin: canonicalCandidate,
                IsCustomOrigin: true);
        }

        return new ServerOriginTrustDecision(
            IsTrusted: false,
            CanonicalOrigin: canonicalCandidate,
            IsCustomOrigin: true,
            RejectionReason: "QR server origin differs from the configured server origin and has not been explicitly approved by the laptop owner.");
    }
}
