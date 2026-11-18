using Beacon.Companion.Core.Audio;
using Beacon.Companion.Core.Capture;
using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Credentials;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class CaptureAndCredentialContractTests
{
    [Fact]
    public void PixelGeometrySupportsNegativeSecondaryDisplayBoundsAndRejectsNegativeDimensions()
    {
        var secondary = new PixelRect(-1920, -200, 1920, 1080);
        Assert.True(secondary.Contains(-1, -1));
        Assert.False(secondary.Contains(0, 0));

        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelSize(-1, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PixelRect(0, 0, 100, -1));
    }

    [Fact]
    public void CapturedFrameOwnsGpuResourceLeaseAndDisposesDeterministically()
    {
        var monitor = new MonitorDescriptor(
            "\\\\.\\DISPLAY1",
            "\\\\.\\DISPLAY1",
            "Integrated Display",
            true,
            new PixelRect(0, 0, 2560, 1440),
            new PixelSize(2560, 1440),
            DisplayRotation.Identity,
            1.5,
            120);
        var lease = new FakeGpuLease(new PixelSize(2560, 1440));
        PixelRect[] dirtyRects = [new PixelRect(10, 20, 100, 50)];
        using var frame = new CapturedFrame(
            frameSequence: 1,
            timestampQpcTicks: 123456,
            monitor,
            dirtyRects,
            new CursorSnapshot(true, 500, 400, 1),
            lease);

        Assert.Equal(FrameBackingKind.GpuTexture, frame.BackingKind);
        Assert.Equal(FramePixelFormat.Bgra8Unorm, frame.PixelFormat);
        Assert.False(frame.IsDisposed);
        Assert.Same(lease, frame.ResourceLease);

        frame.Dispose();
        frame.Dispose();

        Assert.True(frame.IsDisposed);
        Assert.Equal(1, lease.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => _ = frame.ResourceLease);
    }

    [Fact]
    public async Task CaptureSourceHonorsCancellationToken()
    {
        await using IScreenCaptureSource source = new CancellableCaptureSource();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(50), cts.Token).AsTask());
    }

    [Fact]
    public void DeviceCredentialClearsTokenBufferOnDisposeAndValidatesOrigin()
    {
        using var cred = new DeviceCredential(
            Guid.NewGuid(),
            new Uri("https://beacon.example.com"),
            "0123456789abcdefghij",
            DateTimeOffset.UtcNow);

        Assert.Equal("0123456789abcdefghij", cred.Token.ToString());
        cred.Dispose();
        Assert.True(cred.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => _ = cred.Token.Length);

        Assert.Throws<ArgumentException>(() => new DeviceCredential(
            Guid.NewGuid(),
            new Uri("https://beacon.example.com"),
            "short",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CompanionOptionsValidateHttpsOriginAndHeartbeatWindow()
    {
        var valid = new CompanionOptions
        {
            ServerOrigin = new Uri("https://beacon.example.com"),
            DeviceName = "WORKSTATION-01",
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        };
        valid.Validate();

        var localDev = valid with { ServerOrigin = new Uri("http://localhost:3000") };
        localDev.Validate();

        var insecureRemote = valid with { ServerOrigin = new Uri("http://beacon.example.com") };
        Assert.Throws<ArgumentException>(() => insecureRemote.Validate());

        var slowHeartbeat = valid with { HeartbeatInterval = TimeSpan.FromSeconds(95) };
        Assert.Throws<ArgumentOutOfRangeException>(() => slowHeartbeat.Validate());
    }

    [Fact]
    public void AudioLoopbackContractMetadataConstructsCleanly()
    {
        var fmt = new AudioFormatDescriptor(48_000, 2, 32, AudioSampleEncoding.PcmFloat32);
        var chunk = new AudioChunk(9999, new byte[480 * 8], 480, IsSilent: false);
        Assert.Equal(48_000, fmt.SampleRateHz);
        Assert.Equal(480, chunk.FrameCount);
    }

    private sealed class FakeGpuLease(PixelSize size) : IFrameResourceLease
    {
        public int DisposeCount { get; private set; }
        public FrameBackingKind BackingKind => FrameBackingKind.GpuTexture;
        public PixelSize Size { get; } = size;
        public FramePixelFormat PixelFormat => FramePixelFormat.Bgra8Unorm;

        public ValueTask CopyToCpuBufferAsync(Memory<byte> destination, int strideBytes, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public void Dispose() => DisposeCount++;
    }

    private sealed class CancellableCaptureSource : IScreenCaptureSource
    {
        public bool IsCapturing => false;
        public MonitorDescriptor? ActiveMonitor => null;

        public Task<IReadOnlyList<MonitorDescriptor>> GetMonitorsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<MonitorDescriptor>>([]);
        }

        public Task StartCaptureAsync(string monitorId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask<CapturedFrame?> AcquireNextFrameAsync(TimeSpan timeout, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult<CapturedFrame?>(null);
        }

        public Task StopCaptureAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
