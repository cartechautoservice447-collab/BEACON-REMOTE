namespace Beacon.Companion.Core.Credentials;

/// <summary>
/// Non-secret metadata for a paired device credential.
/// </summary>
public sealed record DeviceCredentialMetadata(
    Guid DeviceId,
    Uri ServerOrigin,
    DateTimeOffset PairedAtUtc);

/// <summary>
/// In-memory container holding the paired device credential issued by POST /api/public/agent/pair.
/// The underlying token buffer is cleared deterministically on <see cref="Dispose"/>.
/// </summary>
public sealed class DeviceCredential : IDisposable
{
    private readonly char[] _tokenChars;
    private int _disposed;

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
        if (!serverOrigin.IsAbsoluteUri)
        {
            throw new ArgumentException("ServerOrigin must be an absolute URI.", nameof(serverOrigin));
        }

        if (deviceToken.Length < 20 || deviceToken.Length > 128)
        {
            throw new ArgumentException("Device token must be 20..128 characters.", nameof(deviceToken));
        }

        Metadata = new DeviceCredentialMetadata(deviceId, serverOrigin, pairedAtUtc);
        _tokenChars = deviceToken.ToArray();
    }

    public DeviceCredentialMetadata Metadata { get; }
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ReadOnlySpan<char> Token
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _tokenChars;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Array.Clear(_tokenChars, 0, _tokenChars.Length);
        }
    }
}

/// <summary>
/// Platform-independent credential storage abstraction.
/// Windows-specific DPAPI (DataProtectionScope.CurrentUser) storage will implement this in Step 3.3.
/// Never stores raw secrets in plaintext config files.
/// </summary>
public interface ICredentialStore
{
    Task<bool> ExistsAsync(CancellationToken ct = default);
    Task<DeviceCredential?> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(DeviceCredential credential, CancellationToken ct = default);
    Task DeleteAsync(CancellationToken ct = default);
}
