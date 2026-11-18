namespace Beacon.Companion.Core.Audio;

public enum AudioSampleEncoding
{
    PcmFloat32 = 0,
    PcmInt16 = 1,
}

public sealed record AudioFormatDescriptor(
    int SampleRateHz,
    int Channels,
    int BitsPerSample,
    AudioSampleEncoding Encoding);

public sealed record AudioChunk(
    long TimestampQpcTicks,
    ReadOnlyMemory<byte> Payload,
    int FrameCount,
    bool IsSilent);

/// <summary>
/// Contract for system loopback audio capture (implemented via WASAPI in Step 8).
/// </summary>
public interface IAudioLoopbackSource : IAsyncDisposable
{
    bool IsActive { get; }
    AudioFormatDescriptor? CurrentFormat { get; }

    Task StartAsync(CancellationToken ct = default);
    IAsyncEnumerable<AudioChunk> ReadChunksAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
