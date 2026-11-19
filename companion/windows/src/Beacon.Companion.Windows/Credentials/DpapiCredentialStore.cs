using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Core.Security;

namespace Beacon.Companion.Windows.Credentials;

/// <summary>
/// Windows DPAPI implementation of <see cref="ICredentialStore"/> using <see cref="DataProtectionScope.CurrentUser"/>.
/// <para>
/// Security properties and boundaries:
/// <list type="bullet">
/// <item><description>Confidentiality and cryptographic integrity are provided by Windows DPAPI (<see cref="ProtectedData.Protect"/> / <see cref="ProtectedData.Unprotect"/>) bound to the current user's logon credentials and an entropy buffer combining the versioned application prefix with the 16-byte <c>DeviceId</c>.</description></item>
/// <item><description>The outer 32-byte SHA-256 field in the <c>BCNCRED1</c> envelope is an unkeyed checksum used strictly for accidental storage corruption detection, not cryptographic authentication.</description></item>
/// <item><description>After DPAPI decryption succeeds, the inner encrypted <c>DeviceId</c> is compared against the outer header <c>DeviceId</c> to detect header substitution.</description></item>
/// <item><description>Directory and file ACLs grant explicit <c>FullControl</c> solely to the current Windows user's SID with inheritance disabled. Note that local Windows administrators hold <c>SeTakeOwnershipPrivilege</c> and can always take ownership of local files.</description></item>
/// <item><description>File deletion overwrites the encrypted file bytes before unlinking as best-effort cleanup; physical flash/SSD wear-leveling erasure is not guaranteed by user-mode file I/O.</description></item>
/// </list>
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore, IDisposable
{
    public const string DefaultDirectoryName = "BeaconRemote";
    public const string DefaultFileName = "credentials.dpapi";

    private const byte CurrentEnvelopeVersion = 1;
    private const byte CurrentFlags = 0;
    private const byte CurrentInnerVersion = 1;
    private const int HeaderSize = 8 + 1 + 1 + 16 + 4; // 30 bytes
    private const int ChecksumSize = 32;
    private const int MinEnvelopeBytes = HeaderSize + 1 + ChecksumSize;
    private const int MaxCiphertextBytes = 16_384;
    private const int MaxEnvelopeBytes = HeaderSize + MaxCiphertextBytes + ChecksumSize;

    private static ReadOnlySpan<byte> MagicBytes => "BCNCRED1"u8;
    private static ReadOnlySpan<byte> DefaultEntropyPrefix => "BeaconRemote.Companion.CredentialStore.v1:"u8;

    private readonly string _filePath;
    private readonly string _directoryPath;
    private readonly byte[] _entropyPrefix;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public DpapiCredentialStore(string? filePath = null, ReadOnlySpan<byte> customEntropyPrefix = default)
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
                ?? throw new ArgumentException("Credential file path must have a parent directory.", nameof(filePath));
        }

        _entropyPrefix = customEntropyPrefix.IsEmpty
            ? DefaultEntropyPrefix.ToArray()
            : customEntropyPrefix.ToArray();
    }

    public string FilePath => _filePath;

    public async Task VerifyWritableAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUserRestrictedDirectory(_directoryPath);

            // 1. Verify actual DPAPI Protect + Unprotect round-trip under CurrentUser.
            var probeDeviceId = Guid.NewGuid();
            var probePlaintext = GC.AllocateArray<byte>(32, pinned: true);
            var entropy = BuildEntropy(_entropyPrefix, probeDeviceId);
            byte[] ciphertext;
            try
            {
                RandomNumberGenerator.Fill(probePlaintext);
                ciphertext = ProtectedData.Protect(probePlaintext, entropy, DataProtectionScope.CurrentUser);
                var roundTripped = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
                try
                {
                    if (!CryptographicOperations.FixedTimeEquals(probePlaintext, roundTripped))
                    {
                        throw new InvalidOperationException("DPAPI preflight round-trip verification failed.");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(roundTripped);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(probePlaintext);
                CryptographicOperations.ZeroMemory(entropy);
            }

            // 2. Verify temporary-file creation, current-user ACL application, disk flush, atomic replacement, and cleanup.
            var runId = Guid.NewGuid().ToString("N");
            var tempProbePath = Path.Combine(_directoryPath, $".preflight.{runId}.tmp");
            var targetProbePath = Path.Combine(_directoryPath, $".preflight.{runId}.ok");
            try
            {
                await WriteFileWithCurrentUserAclAsync(tempProbePath, ciphertext, ct).ConfigureAwait(false);
                File.Move(tempProbePath, targetProbePath, overwrite: true);
                ApplyUserRestrictedFileAcl(targetProbePath);
            }
            finally
            {
                BestEffortOverwriteAndDeleteFile(tempProbePath);
                BestEffortOverwriteAndDeleteFile(targetProbePath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ExistsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return File.Exists(_filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DeviceCredential?> LoadAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            byte[] envelopeBytes;
            try
            {
                var info = new FileInfo(_filePath);
                if (info.Length is < MinEnvelopeBytes or > MaxEnvelopeBytes)
                {
                    throw new CredentialStoreCorruptedException(
                        $"Credential file length ({info.Length} bytes) is outside the valid BCNCRED1 envelope bounds.");
                }

                envelopeBytes = await File.ReadAllBytesAsync(_filePath, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not CredentialStoreCorruptedException and not OperationCanceledException)
            {
                throw new CredentialStoreCorruptedException("Failed to read the stored credential envelope.", ex);
            }

            return UnpackAndDecryptEnvelope(envelopeBytes, _entropyPrefix);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(DeviceCredential credential, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(credential);
        ObjectDisposedException.ThrowIf(credential.IsDisposed, credential);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureUserRestrictedDirectory(_directoryPath);

            var envelopeBytes = EncryptAndPackEnvelope(credential, _entropyPrefix);
            var tempPath = $"{_filePath}.tmp.{Guid.NewGuid():N}";
            try
            {
                await WriteFileWithCurrentUserAclAsync(tempPath, envelopeBytes, ct).ConfigureAwait(false);
                File.Move(tempPath, _filePath, overwrite: true);
                ApplyUserRestrictedFileAcl(_filePath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    BestEffortOverwriteAndDeleteFile(tempPath);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BestEffortOverwriteAndDeleteFile(_filePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            CryptographicOperations.ZeroMemory(_entropyPrefix);
            _gate.Dispose();
        }
    }

    private static byte[] EncryptAndPackEnvelope(DeviceCredential credential, ReadOnlySpan<byte> entropyPrefix)
    {
        var originString = credential.Metadata.ServerOrigin.GetLeftPart(UriPartial.Authority);
        var originByteCount = Encoding.UTF8.GetByteCount(originString);
        var tokenByteCount = credential.GetTokenUtf8ByteCount();

        // Inner layout:
        // [0]: InnerVersion (1 byte)
        // [1..17]: InnerDeviceId (16 bytes Guid)
        // [17..25]: PairedAtUtcTicks (8 bytes int64 little-endian)
        // [25..27]: OriginUtf8Length (2 bytes int16 little-endian)
        // [27..27+OriginLen]: OriginUtf8Bytes
        // [27+OriginLen..29+OriginLen]: TokenUtf8Length (2 bytes int16 little-endian)
        // [29+OriginLen..]: TokenUtf8Bytes
        var innerLength = 1 + 16 + 8 + 2 + originByteCount + 2 + tokenByteCount;
        var innerPlaintext = GC.AllocateArray<byte>(innerLength, pinned: true);
        var entropy = BuildEntropy(entropyPrefix, credential.Metadata.DeviceId);

        byte[] ciphertext;
        try
        {
            var span = innerPlaintext.AsSpan();
            span[0] = CurrentInnerVersion;
            _ = credential.Metadata.DeviceId.TryWriteBytes(span.Slice(1, 16));
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(17, 8), credential.Metadata.PairedAtUtc.UtcDateTime.Ticks);
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(25, 2), checked((short)originByteCount));
            _ = Encoding.UTF8.GetBytes(originString, span.Slice(27, originByteCount));

            var tokenLengthOffset = 27 + originByteCount;
            BinaryPrimitives.WriteInt16LittleEndian(span.Slice(tokenLengthOffset, 2), checked((short)tokenByteCount));
            var writtenTokenBytes = credential.CopyTokenToUtf8(span.Slice(tokenLengthOffset + 2, tokenByteCount));
            if (writtenTokenBytes != tokenByteCount)
            {
                throw new InvalidOperationException("Encoded token UTF-8 byte length mismatch.");
            }

            ciphertext = ProtectedData.Protect(innerPlaintext, entropy, DataProtectionScope.CurrentUser);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(innerPlaintext);
            CryptographicOperations.ZeroMemory(entropy);
        }

        if (ciphertext.Length is <= 0 or > MaxCiphertextBytes)
        {
            throw new InvalidOperationException($"Unexpected DPAPI ciphertext length ({ciphertext.Length} bytes).");
        }

        var totalLength = HeaderSize + ciphertext.Length + ChecksumSize;
        var envelope = new byte[totalLength];
        var envSpan = envelope.AsSpan();

        MagicBytes.CopyTo(envSpan[..8]);
        envSpan[8] = CurrentEnvelopeVersion;
        envSpan[9] = CurrentFlags;
        _ = credential.Metadata.DeviceId.TryWriteBytes(envSpan.Slice(10, 16));
        BinaryPrimitives.WriteInt32LittleEndian(envSpan.Slice(26, 4), ciphertext.Length);
        ciphertext.CopyTo(envSpan.Slice(HeaderSize, ciphertext.Length));

        // Unkeyed SHA-256 checksum for accidental corruption detection only.
        _ = SHA256.HashData(
            envSpan[..(HeaderSize + ciphertext.Length)],
            envSpan.Slice(HeaderSize + ciphertext.Length, ChecksumSize));

        return envelope;
    }

    private static DeviceCredential UnpackAndDecryptEnvelope(
        ReadOnlySpan<byte> envelope,
        ReadOnlySpan<byte> entropyPrefix)
    {
        if (envelope.Length is < MinEnvelopeBytes or > MaxEnvelopeBytes)
        {
            throw new CredentialStoreCorruptedException("Credential envelope size is invalid.");
        }

        if (!envelope[..8].SequenceEqual(MagicBytes))
        {
            throw new CredentialStoreCorruptedException("Credential envelope magic header ('BCNCRED1') is invalid.");
        }

        var version = envelope[8];
        if (version != CurrentEnvelopeVersion)
        {
            throw new CredentialStoreCorruptedException($"Unsupported credential envelope version '{version}'.");
        }

        var flags = envelope[9];
        if (flags != CurrentFlags)
        {
            throw new CredentialStoreCorruptedException($"Unsupported credential envelope flags '0x{flags:X2}'.");
        }

        var headerDeviceId = new Guid(envelope.Slice(10, 16));
        if (headerDeviceId == Guid.Empty)
        {
            throw new CredentialStoreCorruptedException("Credential envelope header DeviceId cannot be empty.");
        }

        var ciphertextLength = BinaryPrimitives.ReadInt32LittleEndian(envelope.Slice(26, 4));
        if (ciphertextLength is <= 0 or > MaxCiphertextBytes ||
            HeaderSize + ciphertextLength + ChecksumSize != envelope.Length)
        {
            throw new CredentialStoreCorruptedException("Credential envelope ciphertext length does not match file size.");
        }

        var signedPortion = envelope[..(HeaderSize + ciphertextLength)];
        var storedChecksum = envelope.Slice(HeaderSize + ciphertextLength, ChecksumSize);
        Span<byte> computedChecksum = stackalloc byte[ChecksumSize];
        _ = SHA256.HashData(signedPortion, computedChecksum);

        // Verify accidental corruption checksum before passing ciphertext to DPAPI.
        if (!CryptographicOperations.FixedTimeEquals(storedChecksum, computedChecksum))
        {
            throw new CredentialStoreCorruptedException("Credential envelope failed accidental corruption checksum verification.");
        }

        var ciphertext = envelope.Slice(HeaderSize, ciphertextLength).ToArray();
        var entropy = BuildEntropy(entropyPrefix, headerDeviceId);
        byte[] innerPlaintext;
        try
        {
            // Cryptographic integrity and user-account binding are enforced here by Windows DPAPI.
            innerPlaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new CredentialStoreCorruptedException(
                "Windows DPAPI failed to decrypt the stored credential (wrong Windows user, wrong entropy, or tampered ciphertext).",
                ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }

        try
        {
            return ParseInnerPlaintext(innerPlaintext, headerDeviceId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(innerPlaintext);
        }
    }

    private static DeviceCredential ParseInnerPlaintext(ReadOnlySpan<byte> inner, Guid expectedHeaderDeviceId)
    {
        // Minimum inner length: 1 (version) + 16 (guid) + 8 (ticks) + 2 (origin len) + 8 (min origin) + 2 (token len) + 20 (min token) = 57 bytes
        if (inner.Length < 57)
        {
            throw new CredentialStoreCorruptedException("Decrypted credential payload is truncated.");
        }

        var innerVersion = inner[0];
        if (innerVersion != CurrentInnerVersion)
        {
            throw new CredentialStoreCorruptedException($"Unsupported inner credential payload version '{innerVersion}'.");
        }

        var innerDeviceId = new Guid(inner.Slice(1, 16));
        if (innerDeviceId == Guid.Empty || innerDeviceId != expectedHeaderDeviceId)
        {
            throw new CredentialStoreCorruptedException("Decrypted inner DeviceId does not match the envelope header DeviceId.");
        }

        var utcTicks = BinaryPrimitives.ReadInt64LittleEndian(inner.Slice(17, 8));
        if (utcTicks < DateTimeOffset.MinValue.UtcTicks || utcTicks > DateTimeOffset.MaxValue.UtcTicks)
        {
            throw new CredentialStoreCorruptedException("Decrypted credential timestamp is out of valid UTC bounds.");
        }

        var pairedAtUtc = new DateTimeOffset(utcTicks, TimeSpan.Zero);

        var originByteLength = BinaryPrimitives.ReadInt16LittleEndian(inner.Slice(25, 2));
        if (originByteLength is <= 0 or > 512 || 27 + originByteLength + 2 > inner.Length)
        {
            throw new CredentialStoreCorruptedException("Decrypted server origin length is invalid.");
        }

        var originString = Encoding.UTF8.GetString(inner.Slice(27, originByteLength));
        var originValidation = ServerOriginValidator.Validate(originString);
        if (!originValidation.IsValid || originValidation.CanonicalOrigin is null)
        {
            throw new CredentialStoreCorruptedException(
                originValidation.ErrorDetail ?? "Decrypted server origin is invalid.");
        }

        var tokenLengthOffset = 27 + originByteLength;
        var tokenByteLength = BinaryPrimitives.ReadInt16LittleEndian(inner.Slice(tokenLengthOffset, 2));
        if (tokenByteLength is < 20 or > 128 || tokenLengthOffset + 2 + tokenByteLength != inner.Length)
        {
            throw new CredentialStoreCorruptedException("Decrypted token byte length is invalid.");
        }

        var tokenUtf8Slice = inner.Slice(tokenLengthOffset + 2, tokenByteLength);
        using var tokenBuffer = SensitiveCharBuffer.FromUtf8Bytes(tokenUtf8Slice);
        if (!DeviceCredential.IsValidTokenFormat(tokenBuffer.Span))
        {
            throw new CredentialStoreCorruptedException("Decrypted token contains invalid characters.");
        }

        return new DeviceCredential(innerDeviceId, originValidation.CanonicalOrigin, tokenBuffer.Span, pairedAtUtc);
    }

    private static byte[] BuildEntropy(ReadOnlySpan<byte> prefix, Guid deviceId)
    {
        var entropy = GC.AllocateArray<byte>(prefix.Length + 16, pinned: true);
        prefix.CopyTo(entropy.AsSpan(0, prefix.Length));
        _ = deviceId.TryWriteBytes(entropy.AsSpan(prefix.Length, 16));
        return entropy;
    }

    private static void EnsureUserRestrictedDirectory(string directoryPath)
    {
        var currentUserSid = GetCurrentUserSid();
        var dirInfo = Directory.CreateDirectory(directoryPath);
        var directorySecurity = new DirectorySecurity();

        // Add the current-user rule BEFORE SetAccessRuleProtection so CommonSecurityDescriptor
        // clears its internal EveryOneFullAccessForNullDacl (WorldSid / Everyone) placeholder ACE,
        // then disable inheritance and purge any non-current-user rules.
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

        // Add the current-user rule BEFORE SetAccessRuleProtection so CommonSecurityDescriptor
        // clears its internal EveryOneFullAccessForNullDacl (WorldSid / Everyone) placeholder ACE,
        // then disable inheritance and purge any non-current-user rules.
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
                _ = security.RemoveAccessRuleSpecific(rule);
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

    /// <summary>
    /// Overwrites the file's bytes with zeroes before unlinking as best-effort cleanup.
    /// Does not claim guaranteed physical flash erasure on SSDs or copy-on-write storage.
    /// </summary>
    private static void BestEffortOverwriteAndDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.IsReadOnly)
            {
                info.IsReadOnly = false;
            }

            var length = (int)Math.Clamp(info.Length, 0L, MaxEnvelopeBytes);
            if (length > 0)
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.WriteThrough);
                byte[] zeroes = new byte[length];
                stream.Write(zeroes, 0, zeroes.Length);
                stream.Flush(flushToDisk: true);
            }
        }
        catch (IOException)
        {
            // Best-effort overwrite; proceed to File.Delete.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort overwrite; proceed to File.Delete.
        }

        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // Already deleted concurrently.
        }
    }
}
