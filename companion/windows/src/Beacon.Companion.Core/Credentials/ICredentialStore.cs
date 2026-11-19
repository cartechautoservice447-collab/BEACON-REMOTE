using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Core.Credentials;

/// <summary>
/// Thrown when the persisted credential envelope is corrupted, truncated, tampered with,
/// encrypted under a different Windows user / entropy, or fails inner-outer DeviceId verification.
/// </summary>
public sealed class CredentialStoreCorruptedException : Exception
{
    public CredentialStoreCorruptedException()
        : base("The stored device credential envelope is corrupted or cannot be decrypted.")
    {
    }

    public CredentialStoreCorruptedException(string message)
        : base(message)
    {
    }

    public CredentialStoreCorruptedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Non-secret metadata for a paired device credential.
/// </summary>
public sealed record DeviceCredentialMetadata(
    Guid DeviceId,
    Uri ServerOrigin,
    DateTimeOffset PairedAtUtc);

/// <summary>
/// In-memory container holding the paired device credential issued by <c>POST /api/public/agent/pair</c>.
/// The underlying token buffer is allocated on the Pinned Object Heap via <see cref="SensitiveCharBuffer"/>
/// and cleared deterministically via <c>CryptographicOperations.ZeroMemory</c> on <see cref="Dispose"/>.
/// </summary>
public sealed class DeviceCredential : IDisposable
{
    private readonly SensitiveCharBuffer _tokenBuffer;

    public DeviceCredential(
        Guid deviceId,
        Uri serverOrigin,
        ReadOnlySpan<char> deviceToken,
        DateTimeOffset pairedAtUtc)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId cannot be empty.", nameof(deviceId));
        }

        ArgumentNullException.ThrowIfNull(serverOrigin);
        var originValidation = ServerOriginValidator.Validate(serverOrigin);
        if (!originValidation.IsValid || originValidation.CanonicalOrigin is null)
        {
            throw new ArgumentException(
                originValidation.ErrorDetail ?? "ServerOrigin must be a valid canonical HTTPS or loopback HTTP origin.",
                nameof(serverOrigin));
        }

        ValidateTokenCharacters(deviceToken);

        Metadata = new DeviceCredentialMetadata(deviceId, originValidation.CanonicalOrigin, pairedAtUtc);
        _tokenBuffer = SensitiveCharBuffer.FromChars(deviceToken);
    }

    public DeviceCredentialMetadata Metadata { get; }

    public bool IsDisposed => _tokenBuffer.IsDisposed;

    public ReadOnlySpan<char> Token => _tokenBuffer.Span;

    public int CopyTokenToUtf8(Span<byte> destination) => _tokenBuffer.CopyToUtf8(destination);

    public int GetTokenUtf8ByteCount() => _tokenBuffer.GetUtf8ByteCount();

    public static bool IsValidTokenFormat(ReadOnlySpan<char> token)
    {
        if (token.Length is < 20 or > 128)
        {
            return false;
        }

        foreach (var ch in token)
        {
            // Printable non-whitespace ASCII token characters only (0x21..0x7E).
            if (ch is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Redacts the bearer token so accidental logging or string formatting never leaks credentials.
    /// </summary>
    public override string ToString() =>
        $"DeviceCredential {{ DeviceId = {Metadata.DeviceId}, ServerOrigin = {Metadata.ServerOrigin}, Token = [REDACTED] }}";

    public void Dispose() => _tokenBuffer.Dispose();

    private static void ValidateTokenCharacters(ReadOnlySpan<char> deviceToken)
    {
        if (!IsValidTokenFormat(deviceToken))
        {
            throw new ArgumentException(
                "Device token must be 20..128 printable ASCII characters.",
                nameof(deviceToken));
        }
    }
}

/// <summary>
/// Platform-independent credential storage abstraction.
/// Windows-specific DPAPI (<c>DataProtectionScope.CurrentUser</c>) storage implements this in <c>Beacon.Companion.Windows</c>.
/// Never stores raw secrets in plaintext config files.
/// </summary>
public interface ICredentialStore
{
    /// <summary>
    /// Performs a preflight verification of directory permissions, encryption/decryption round-trip,
    /// temporary file creation, atomic replacement, and cleanup before a single-use server pairing credential is consumed.
    /// </summary>
    Task VerifyWritableAsync(CancellationToken ct = default);

    Task<bool> ExistsAsync(CancellationToken ct = default);

    Task<DeviceCredential?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(DeviceCredential credential, CancellationToken ct = default);

    Task DeleteAsync(CancellationToken ct = default);

    /// <summary>
    /// Atomically verifies the stored credential under the store's internal synchronization gate
    /// and deletes it if and only if no credential exists, the stored credential envelope is corrupted,
    /// or the stored credential's <c>DeviceId</c> equals <paramref name="expectedDeviceId"/>.
    /// Returns <see langword="false"/> without deleting if a valid credential for a different
    /// <c>DeviceId</c> is stored (preventing a concurrent re-pairing TOCTOU overwrite/deletion).
    /// </summary>
    Task<bool> DeleteIfMatchesAsync(Guid expectedDeviceId, CancellationToken ct = default);
}
