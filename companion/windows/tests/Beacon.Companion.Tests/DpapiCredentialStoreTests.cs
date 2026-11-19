using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Beacon.Companion.Core.Credentials;
using Beacon.Companion.Windows.Credentials;
using Xunit;

namespace Beacon.Companion.Tests;

[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStoreTests : IDisposable
{
    private readonly string _testRootDir;

    public DpapiCredentialStoreTests()
    {
        _testRootDir = Path.Combine(
            Path.GetTempPath(),
            "BeaconCompanionDpapiTests",
            Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task UnpairedStoreReportsNotExistsReturnsNullOnLoadAndSucceedsOnDelete()
    {
        var credPath = Path.Combine(_testRootDir, "credentials.dpapi");
        using var store = new DpapiCredentialStore(credPath);

        Assert.False(await store.ExistsAsync());
        Assert.Null(await store.LoadAsync());
        await store.DeleteAsync();
        Assert.False(await store.ExistsAsync());
    }

    [Fact]
    public async Task PreflightVerifyWritableExecutesRealDpapiRoundTripAndCleansUpTempFiles()
    {
        var credPath = Path.Combine(_testRootDir, "credentials.dpapi");
        using var store = new DpapiCredentialStore(credPath);

        await store.VerifyWritableAsync();

        Assert.True(Directory.Exists(_testRootDir));
        var leftoverFiles = Directory.GetFiles(_testRootDir);
        Assert.Empty(leftoverFiles);
    }

    [Fact]
    public async Task SaveAndLoadRoundTripsCredentialWithoutStoringPlaintextOnDiskAndRestrictsAclToCurrentUser()
    {
        var credPath = Path.Combine(_testRootDir, "credentials.dpapi");
        using var store = new DpapiCredentialStore(credPath);

        var deviceId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");
        var origin = new Uri("https://beacon.example.com");
        var pairedAt = new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero);
        const string secretToken = "SecretBearerToken_0123456789abcdefghijk";

        using (var original = new DeviceCredential(deviceId, origin, secretToken, pairedAt))
        {
            await store.SaveAsync(original);
        }

        Assert.True(await store.ExistsAsync());
        Assert.Empty(Directory.GetFiles(_testRootDir, "*.tmp*"));

        // Verify raw file on disk has BCNCRED1 header and zero plaintext occurrences (UTF-8 or UTF-16LE).
        var rawDiskBytes = await File.ReadAllBytesAsync(credPath);
        Assert.Equal("BCNCRED1", Encoding.ASCII.GetString(rawDiskBytes, 0, 8));
        Assert.Equal(-1, rawDiskBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secretToken)));
        Assert.Equal(-1, rawDiskBytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secretToken)));

        // Verify restrictive current-user SID ACL on both directory and file (and confirm Everyone/WorldSid is absent).
        using var currentIdentity = WindowsIdentity.GetCurrent();
        var currentUserSid = currentIdentity.User!;
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);

        var dirSecurity = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(_testRootDir));
        Assert.True(dirSecurity.AreAccessRulesProtected);
        var dirRules = dirSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var dirRule = Assert.Single(dirRules);
        Assert.Equal(currentUserSid, dirRule.IdentityReference);
        Assert.NotEqual(worldSid, dirRule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, dirRule.AccessControlType);

        var fileSecurity = FileSystemAclExtensions.GetAccessControl(new FileInfo(credPath));
        Assert.True(fileSecurity.AreAccessRulesProtected);
        var rules = fileSecurity.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
        var rule = Assert.Single(rules);
        Assert.Equal(currentUserSid, rule.IdentityReference);
        Assert.NotEqual(worldSid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);

        using var loaded = await store.LoadAsync();
        Assert.NotNull(loaded);
        Assert.Equal(deviceId, loaded!.Metadata.DeviceId);
        Assert.Equal(origin, loaded.Metadata.ServerOrigin);
        Assert.Equal(pairedAt, loaded.Metadata.PairedAtUtc);
        Assert.Equal(secretToken, loaded.Token.ToString());
        Assert.DoesNotContain(secretToken, loaded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsWrongApplicationEntropyAndTamperedHeaderDeviceIdEvenWithRecomputedChecksum()
    {
        var credPath = Path.Combine(_testRootDir, "credentials.dpapi");
        using var writerStore = new DpapiCredentialStore(credPath, "AppEntropy.v1:"u8);

        using (var cred = new DeviceCredential(
                   Guid.NewGuid(),
                   new Uri("https://beacon.example.com"),
                   "ValidDeviceToken_1234567890",
                   DateTimeOffset.UtcNow))
        {
            await writerStore.SaveAsync(cred);
        }

        // 1. Different application entropy must fail DPAPI Unprotect -> CredentialStoreCorruptedException.
        using var wrongEntropyStore = new DpapiCredentialStore(credPath, "DifferentAppEntropy.v1:"u8);
        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => wrongEntropyStore.LoadAsync());

        // 2. Tamper outer HeaderDeviceId (bytes 10..26) AND recompute the outer unkeyed SHA-256 checksum
        // so the corruption checksum passes — DPAPI DeviceId-bound entropy must still reject it!
        var bytes = await File.ReadAllBytesAsync(credPath);
        _ = Guid.NewGuid().TryWriteBytes(bytes.AsSpan(10, 16));
        _ = SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32, 32));
        await File.WriteAllBytesAsync(credPath, bytes);

        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => writerStore.LoadAsync());
    }

    [Fact]
    public async Task RejectsCorruptedTruncatedOrInvalidMagicFiles()
    {
        var credPath = Path.Combine(_testRootDir, "credentials.dpapi");
        using var store = new DpapiCredentialStore(credPath);

        using (var cred = new DeviceCredential(
                   Guid.NewGuid(),
                   new Uri("https://beacon.example.com"),
                   "ValidDeviceToken_1234567890",
                   DateTimeOffset.UtcNow))
        {
            await store.SaveAsync(cred);
        }

        var validEnvelope = await File.ReadAllBytesAsync(credPath);
        var ciphertextEnd = validEnvelope.Length - 32;
        Assert.True(ciphertextEnd - 30 >= 128);

        // 1. Flip 1 bit in envelope without updating checksum -> outer SHA-256 corruption check rejects before DPAPI.
        var bitFlipped = (byte[])validEnvelope.Clone();
        bitFlipped[35] ^= 0x01;
        await File.WriteAllBytesAsync(credPath, bitFlipped);
        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => store.LoadAsync());

        // 2. Flip 1 bit in the DPAPI pbSign HMAC tail AND recompute outer SHA-256 -> DPAPI integrity check must reject!
        var hmacTampered = (byte[])validEnvelope.Clone();
        hmacTampered[ciphertextEnd - 10] ^= 0x01;
        _ = SHA256.HashData(hmacTampered.AsSpan(0, ciphertextEnd), hmacTampered.AsSpan(ciphertextEnd, 32));
        await File.WriteAllBytesAsync(credPath, hmacTampered);
        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => store.LoadAsync());

        // 3. Flip 1 bit in the DPAPI pbData encrypted payload AND recompute outer SHA-256 -> DPAPI integrity check must reject!
        var payloadTampered = (byte[])validEnvelope.Clone();
        payloadTampered[ciphertextEnd - 80] ^= 0x01;
        _ = SHA256.HashData(payloadTampered.AsSpan(0, ciphertextEnd), payloadTampered.AsSpan(ciphertextEnd, 32));
        await File.WriteAllBytesAsync(credPath, payloadTampered);
        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => store.LoadAsync());

        // Wrong magic bytes
        var wrongMagic = (byte[])validEnvelope.Clone();
        wrongMagic[0] = (byte)'X';
        await File.WriteAllBytesAsync(credPath, wrongMagic);
        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => store.LoadAsync());

        // Truncated file
        await File.WriteAllBytesAsync(credPath, validEnvelope[..20]);
        await Assert.ThrowsAsync<CredentialStoreCorruptedException>(() => store.LoadAsync());
    }

    [Fact]
    public async Task AtomicReplacementOverwritesExistingCredentialAndDeleteRemovesFile()
    {
        var credPath = Path.Combine(_testRootDir, "credentials.dpapi");
        using var store = new DpapiCredentialStore(credPath);

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        using (var first = new DeviceCredential(
                   firstId,
                   new Uri("https://beacon.example.com"),
                   "FirstDeviceToken_0123456789",
                   DateTimeOffset.UtcNow))
        {
            await store.SaveAsync(first);
        }

        using (var second = new DeviceCredential(
                   secondId,
                   new Uri("https://beacon.example.com"),
                   "SecondDeviceToken_9876543210",
                   DateTimeOffset.UtcNow))
        {
            await store.SaveAsync(second);
        }

        using (var loaded = await store.LoadAsync())
        {
            Assert.NotNull(loaded);
            Assert.Equal(secondId, loaded!.Metadata.DeviceId);
            Assert.Equal("SecondDeviceToken_9876543210", loaded.Token.ToString());
        }

        await store.DeleteAsync();
        Assert.False(await store.ExistsAsync());
        Assert.Null(await store.LoadAsync());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRootDir))
            {
                Directory.Delete(_testRootDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Ignore best-effort test directory cleanup errors.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore best-effort test directory cleanup errors.
        }
    }
}
