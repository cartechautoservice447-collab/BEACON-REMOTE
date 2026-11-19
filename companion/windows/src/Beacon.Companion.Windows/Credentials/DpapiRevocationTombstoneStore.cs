using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Heartbeat;

namespace Beacon.Companion.Windows.Credentials;

/// <summary>
/// Windows DPAPI implementation of <see cref="IRevocationTombstoneStore"/> using <see cref="DataProtectionScope.CurrentUser"/>.
/// <para>
/// Persists a DPAPI-encrypted ledger of revoked <c>DeviceId</c> values in <c>%LOCALAPPDATA%\BeaconRemote\revoked-devices.dpapi</c>
/// with current-user-only SID ACLs and atomic temp-file replacement so that even if <c>credentials.dpapi</c> deletion fails
/// (for example due to an antivirus lock or sharing violation) and the process restarts, the revoked <c>DeviceId</c> can
/// never regain operational authorization.
/// </para>
/// <para>
/// Fail-closed guarantee (ARGON NEW-002):
/// <list type="bullet">
/// <item><description>If the tombstone file exists on disk but is corrupted, truncated, or fails DPAPI authentication, <see cref="IsRevokedAsync"/> throws <see cref="CredentialStoreCorruptedException"/>.</description></item>
/// <item><description>If <see cref="RecordRevokedAsync"/> detects that the existing tombstone file was corrupted before recording a new revocation, it NEVER silently replaces the corrupted ledger with an incomplete clean ledger. Instead, it sets a durable DPAPI-protected <c>InnerFlagHistoryCorruptedLockout</c> bit inside the encrypted payload so <see cref="IsRevokedAsync"/> remains permanently fail-closed (<see cref="CredentialStoreCorruptedException"/>) across restarts until an explicit <see cref="ResetCorruptedHistoryWithFreshBaselineAsync"/> recovery is executed.</description></item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiRevocationTombstoneStore : IRevocationTombstoneStore, IDisposable
{
    public const string DefaultDirectoryName = "BeaconRemote";
    public const string DefaultFileName = "revoked-devices.dpapi";

    private const byte CurrentEnvelopeVersion = 1;
    private const byte CurrentInnerVersion = 2;
    private const byte LegacyInnerVersionV1 = 1;
    private const byte InnerFlagNone = 0;
    private const byte InnerFlagHistoryCorruptedLockout = 1;
    private const int HeaderSize = 8 + 1 + 4; // Magic (8) + Version (1) + CiphertextLength (4) = 13 bytes
    private const int ChecksumSize = 32;
    private const int EntrySize = 16 + 8; // Guid (16) + UnixTimeMilliseconds (8) = 24 bytes
    private const int MaxRevokedEntries = 256;
    private const int MaxCiphertextBytes = 32_768;
    private const int MinEnvelopeBytes = HeaderSize + 1 + ChecksumSize;
    private const int MaxEnvelopeBytes = HeaderSize + MaxCiphertextBytes + ChecksumSize;

    private static ReadOnlySpan<byte> MagicBytes => "BCNREVK1"u8;
    private static ReadOnlySpan<byte> DefaultEntropy => "BeaconRemote.Companion.RevocationTombstone.v1"u8;

    private readonly string _filePath;
    private readonly string _directoryPath;
    private readonly byte[] _entropy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public DpapiRevocationTombstoneStore(string? filePath = null, ReadOnlySpan<byte> customEntropy = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("LOCALAPPDATA path is not available for the current user session.");
            }

            _directoryPath = Path.Combine(localAppData, DefaultDirectoryName);
            _filePath = Path.Combine(_directoryPath, DefaultFileName);
        }
        else
        {
            _filePath = Path.GetFullPath(filePath);
            _directoryPath = Path.GetDirectoryName(_filePath)
                ?? throw new ArgumentException("Tombstone file path must have a parent directory.", nameof(filePath));
        }

        _entropy = customEntropy.IsEmpty
            ? DefaultEntropy.ToArray()
            : customEntropy.ToArray();
    }

    public string FilePath => _filePath;

    public async Task<bool> IsRevokedAsync(Guid deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (deviceId == Guid.Empty)
        {
            return true;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (entries, isHistoryCorruptedLockedOut) = await LoadEntriesCoreAsync(ct).ConfigureAwait(false);
            if (isHistoryCorruptedLockedOut)
            {
                throw new CredentialStoreCorruptedException(
                    "Revocation tombstone ledger is in a durable fail-closed lockout state because previous revocation history was corrupted.");
            }

            return entries.ContainsKey(deviceId);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public async Task RecordRevokedAsync(Guid deviceId, DateTimeOffset revokedAtUtc, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("DeviceId cannot be Guid.Empty.", nameof(deviceId));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUserRestrictedDirectory(_directoryPath);

            Dictionary<Guid, long> entries;
            var historyCorruptedLockout = false;
            try
            {
                var loaded = await LoadEntriesCoreAsync(ct).ConfigureAwait(false);
                entries = loaded.Entries;
                historyCorruptedLockout = loaded.IsHistoryCorruptedLockedOut;
            }
            catch (CredentialStoreCorruptedException)
            {
                // ARGON NEW-002: Never convert a corrupted tombstone ledger back into a clean ledger
                // that forgets earlier revoked DeviceIds. Latch InnerFlagHistoryCorruptedLockout
                // inside the DPAPI-protected payload so IsRevokedAsync remains permanently fail-closed.
                entries = [];
                historyCorruptedLockout = true;
            }

            entries[deviceId] = revokedAtUtc.ToUnixTimeMilliseconds();

            // Bound ledger size by keeping the most recent MaxRevokedEntries entries.
            if (entries.Count > MaxRevokedEntries)
            {
                entries = entries
                    .OrderByDescending(kvp => kvp.Value)
                    .Take(MaxRevokedEntries)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }

            await PersistLedgerUnsafeAsync(entries, historyCorruptedLockout, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Explicit recovery procedure to clear a durable corruption lockout ONLY after the local credential
    /// store has been verified purged or re-initialized with known revoked device IDs retained.
    /// </summary>
    public async Task ResetCorruptedHistoryWithFreshBaselineAsync(
        IEnumerable<Guid>? knownRevokedDeviceIds = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUserRestrictedDirectory(_directoryPath);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var entries = new Dictionary<Guid, long>();

            if (knownRevokedDeviceIds is not null)
            {
                foreach (var id in knownRevokedDeviceIds)
                {
                    if (id == Guid.Empty)
                    {
                        throw new ArgumentException("Known revoked DeviceId cannot be Guid.Empty.", nameof(knownRevokedDeviceIds));
                    }

                    entries[id] = nowMs;
                }
            }

            await PersistLedgerUnsafeAsync(entries, historyCorruptedLockout: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_entropy);
        _gate.Dispose();
    }

    private async Task PersistLedgerUnsafeAsync(
        Dictionary<Guid, long> entries,
        bool historyCorruptedLockout,
        CancellationToken ct)
    {
        var flags = historyCorruptedLockout ? InnerFlagHistoryCorruptedLockout : InnerFlagNone;
        var plaintext = SerializeEntries(entries, flags);
        byte[] ciphertext;
        try
        {
            ciphertext = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }

        if (ciphertext.Length == 0 || ciphertext.Length > MaxCiphertextBytes)
        {
            throw new InvalidOperationException("DPAPI tombstone ciphertext length is outside allowed bounds.");
        }

        var envelope = BuildEnvelope(ciphertext);
        var tempPath = Path.Combine(_directoryPath, $".revoked.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteFileWithCurrentUserAclAsync(tempPath, envelope, ct).ConfigureAwait(false);
            File.Move(tempPath, _filePath, overwrite: true);
            ApplyUserRestrictedFileAcl(_filePath);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
                // Ignore best-effort cleanup failure.
            }

            throw;
        }
    }

    private async Task<(Dictionary<Guid, long> Entries, bool IsHistoryCorruptedLockedOut)> LoadEntriesCoreAsync(
        CancellationToken ct)
    {
        if (!File.Exists(_filePath))
        {
            return ([], false);
        }

        byte[] rawBytes;
        try
        {
            var info = new FileInfo(_filePath);
            if (info.Length < MinEnvelopeBytes || info.Length > MaxEnvelopeBytes)
            {
                throw new CredentialStoreCorruptedException("Revocation tombstone file size is outside valid bounds.");
            }

            rawBytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return ([], false);
        }
        catch (DirectoryNotFoundException)
        {
            return ([], false);
        }

        if (rawBytes.Length < MinEnvelopeBytes || rawBytes.Length > MaxEnvelopeBytes)
        {
            throw new CredentialStoreCorruptedException("Revocation tombstone payload length is invalid.");
        }

        var span = rawBytes.AsSpan();
        if (!span[..8].SequenceEqual(MagicBytes))
        {
            throw new CredentialStoreCorruptedException("Revocation tombstone magic header is invalid.");
        }

        if (span[8] != CurrentEnvelopeVersion)
        {
            throw new CredentialStoreCorruptedException($"Unsupported revocation tombstone version '{span[8]}'.");
        }

        var ciphertextLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(9, 4));
        if (ciphertextLen <= 0 ||
            ciphertextLen > MaxCiphertextBytes ||
            HeaderSize + ciphertextLen + ChecksumSize != rawBytes.Length)
        {
            throw new CredentialStoreCorruptedException("Revocation tombstone ciphertext length header does not match file size.");
        }

        var signedRegion = span[..(HeaderSize + ciphertextLen)];
        var storedChecksum = span.Slice(HeaderSize + ciphertextLen, ChecksumSize);
        Span<byte> computedChecksum = stackalloc byte[ChecksumSize];
        _ = SHA256.HashData(signedRegion, computedChecksum);
        if (!CryptographicOperations.FixedTimeEquals(storedChecksum, computedChecksum))
        {
            throw new CredentialStoreCorruptedException("Revocation tombstone outer SHA-256 checksum failed.");
        }

        var ciphertext = span.Slice(HeaderSize, ciphertextLen).ToArray();
        byte[] plaintext;
        try
        {
            plaintext = ProtectedData.Unprotect(ciphertext, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new CredentialStoreCorruptedException("DPAPI failed to decrypt the revocation tombstone ledger.", ex);
        }

        try
        {
            return DeserializeEntries(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] SerializeEntries(Dictionary<Guid, long> entries, byte flags)
    {
        var count = entries.Count;
        var buffer = GC.AllocateArray<byte>(1 + 1 + 4 + (count * EntrySize), pinned: true);
        var span = buffer.AsSpan();
        span[0] = CurrentInnerVersion;
        span[1] = flags;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(2, 4), count);

        var offset = 6;
        foreach (var (deviceId, timestampMs) in entries)
        {
            _ = deviceId.TryWriteBytes(span.Slice(offset, 16));
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset + 16, 8), timestampMs);
            offset += EntrySize;
        }

        return buffer;
    }

    private static (Dictionary<Guid, long> Entries, bool IsHistoryCorruptedLockedOut) DeserializeEntries(
        ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length < 5)
        {
            throw new CredentialStoreCorruptedException("Decrypted revocation tombstone inner header is truncated.");
        }

        var innerVersion = plaintext[0];
        byte flags;
        int count;
        int headerBytes;

        if (innerVersion == CurrentInnerVersion)
        {
            if (plaintext.Length < 6)
            {
                throw new CredentialStoreCorruptedException("Decrypted revocation tombstone v2 inner header is truncated.");
            }

            flags = plaintext[1];
            if (flags is not (InnerFlagNone or InnerFlagHistoryCorruptedLockout))
            {
                throw new CredentialStoreCorruptedException($"Unsupported revocation tombstone inner flags '{flags}'.");
            }

            count = BinaryPrimitives.ReadInt32LittleEndian(plaintext.Slice(2, 4));
            headerBytes = 6;
        }
        else if (innerVersion == LegacyInnerVersionV1)
        {
            flags = InnerFlagNone;
            count = BinaryPrimitives.ReadInt32LittleEndian(plaintext.Slice(1, 4));
            headerBytes = 5;
        }
        else
        {
            throw new CredentialStoreCorruptedException($"Unsupported revocation tombstone inner version '{innerVersion}'.");
        }

        if (count < 0 || count > MaxRevokedEntries || plaintext.Length != headerBytes + (count * EntrySize))
        {
            throw new CredentialStoreCorruptedException("Decrypted revocation tombstone entry count does not match payload size.");
        }

        var result = new Dictionary<Guid, long>(count);
        var offset = headerBytes;
        for (var i = 0; i < count; i++)
        {
            var id = new Guid(plaintext.Slice(offset, 16));
            if (id == Guid.Empty)
            {
                throw new CredentialStoreCorruptedException("Decrypted revocation tombstone contains Guid.Empty.");
            }

            var ts = BinaryPrimitives.ReadInt64LittleEndian(plaintext.Slice(offset + 16, 8));
            result[id] = ts;
            offset += EntrySize;
        }

        return (result, flags == InnerFlagHistoryCorruptedLockout);
    }

    private static byte[] BuildEnvelope(ReadOnlySpan<byte> ciphertext)
    {
        var envelope = new byte[HeaderSize + ciphertext.Length + ChecksumSize];
        var span = envelope.AsSpan();
        MagicBytes.CopyTo(span[..8]);
        span[8] = CurrentEnvelopeVersion;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(9, 4), ciphertext.Length);
        ciphertext.CopyTo(span.Slice(HeaderSize, ciphertext.Length));

        _ = SHA256.HashData(
            span[..(HeaderSize + ciphertext.Length)],
            span.Slice(HeaderSize + ciphertext.Length, ChecksumSize));
        return envelope;
    }

    private static void EnsureUserRestrictedDirectory(string directoryPath)
    {
        var currentUserSid = GetCurrentUserSid();
        var dirInfo = Directory.CreateDirectory(directoryPath);
        var directorySecurity = new DirectorySecurity();

        directorySecurity.AddAccessRule(new FileSystemAccessRule(
            currentUserSid,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveNonCurrentUserRules(directorySecurity, currentUserSid);

        FileSystemAclExtensions.SetAccessControl(dirInfo, directorySecurity);
    }

    private static void ApplyUserRestrictedFileAcl(string filePath)
    {
        var currentUserSid = GetCurrentUserSid();
        var fileSecurity = new FileSecurity();

        fileSecurity.AddAccessRule(new FileSystemAccessRule(
            currentUserSid,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        RemoveNonCurrentUserRules(fileSecurity, currentUserSid);

        var fileInfo = new FileInfo(filePath);
        FileSystemAclExtensions.SetAccessControl(fileInfo, fileSecurity);
    }

    private static void RemoveNonCurrentUserRules(FileSystemSecurity security, SecurityIdentifier currentUserSid)
    {
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        foreach (var rule in rules)
        {
            if (!Equals(rule.IdentityReference, currentUserSid) || rule.AccessControlType != AccessControlType.Allow)
            {
                security.RemoveAccessRuleSpecific(rule);
            }
        }
    }

    private static SecurityIdentifier GetCurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new InvalidOperationException("Unable to resolve the current Windows user SecurityIdentifier (SID).");
    }

    private static async Task WriteFileWithCurrentUserAclAsync(
        string path,
        byte[] content,
        CancellationToken ct)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough | FileOptions.Asynchronous);
        await using (stream.ConfigureAwait(false))
        {
            await stream.WriteAsync(content.AsMemory(), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        ApplyUserRestrictedFileAcl(path);
    }
}
