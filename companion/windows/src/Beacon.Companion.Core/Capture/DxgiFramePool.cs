namespace Beacon.Companion.Core.Capture;

/// <summary>
/// Reusable backing slot managed by <see cref="DxgiFramePool"/>.
/// Holds a pre-allocated or lazily allocated pixel buffer for CPU-staging copies or a GPU texture copy callback.
/// </summary>
public sealed class DxgiPooledFrameSlot
{
    private byte[]? _stagingBuffer;
    private Func<Memory<byte>, int, CancellationToken, ValueTask>? _customCopyDelegate;

    internal DxgiPooledFrameSlot(
        int slotIndex,
        long poolEpoch,
        PixelSize size,
        FramePixelFormat pixelFormat,
        FrameBackingKind backingKind)
    {
        SlotIndex = slotIndex;
        PoolEpoch = poolEpoch;
        Size = size;
        PixelFormat = pixelFormat;
        BackingKind = backingKind;
        BytesPerPixel = GetBytesPerPixel(pixelFormat);
        TightRowPitchBytes = checked(size.Width * BytesPerPixel);
        TotalBufferBytes = checked(TightRowPitchBytes * size.Height);
    }

    public int SlotIndex { get; }
    public long PoolEpoch { get; }
    public PixelSize Size { get; }
    public FramePixelFormat PixelFormat { get; }
    public FrameBackingKind BackingKind { get; private set; }
    public int BytesPerPixel { get; }
    public int TightRowPitchBytes { get; }
    public int TotalBufferBytes { get; }
    public bool IsRetired { get; internal set; }

    public static int GetBytesPerPixel(FramePixelFormat pixelFormat) =>
        pixelFormat switch
        {
            FramePixelFormat.Bgra8Unorm => 4,
            FramePixelFormat.Rgba16Float => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(pixelFormat), pixelFormat, "Unsupported pixel format."),
        };

    /// <summary>
    /// Returns a writable span for the slot's reusable CPU staging buffer, allocating it once per slot lifecycle.
    /// </summary>
    public Span<byte> GetOrAllocateStagingBuffer(FrameBackingKind backingKind = FrameBackingKind.CpuStaging)
    {
        BackingKind = backingKind;
        _customCopyDelegate = null;
        _stagingBuffer ??= new byte[TotalBufferBytes];
        return _stagingBuffer.AsSpan(0, TotalBufferBytes);
    }

    /// <summary>
    /// Binds a custom copy delegate (e.g. from a pooled D3D11 staging texture or test generator) for this frame lease.
    /// </summary>
    public void BindCopyDelegate(
        Func<Memory<byte>, int, CancellationToken, ValueTask>? copyDelegate,
        FrameBackingKind backingKind = FrameBackingKind.GpuTexture)
    {
        BackingKind = backingKind;
        _customCopyDelegate = copyDelegate;
    }

    internal ValueTask CopyToDestinationAsync(
        Memory<byte> destination,
        int destinationStrideBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (destinationStrideBytes < TightRowPitchBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationStrideBytes),
                destinationStrideBytes,
                $"Destination stride ({destinationStrideBytes}) must be at least {TightRowPitchBytes} bytes.");
        }

        var requiredBytes = Size.Height == 0
            ? 0
            : checked((Size.Height - 1) * destinationStrideBytes + TightRowPitchBytes);
        if (destination.Length < requiredBytes)
        {
            throw new ArgumentException(
                $"Destination buffer length ({destination.Length}) is smaller than required ({requiredBytes} bytes).",
                nameof(destination));
        }

        if (_customCopyDelegate is not null)
        {
            return _customCopyDelegate(destination, destinationStrideBytes, ct);
        }

        if (_stagingBuffer is null)
        {
            destination.Span[..requiredBytes].Clear();
            return ValueTask.CompletedTask;
        }

        var destSpan = destination.Span;
        var srcSpan = _stagingBuffer.AsSpan(0, TotalBufferBytes);
        if (destinationStrideBytes == TightRowPitchBytes)
        {
            srcSpan.CopyTo(destSpan[..TotalBufferBytes]);
        }
        else
        {
            for (var row = 0; row < Size.Height; row++)
            {
                var srcRow = srcSpan.Slice(row * TightRowPitchBytes, TightRowPitchBytes);
                var dstRow = destSpan.Slice(row * destinationStrideBytes, TightRowPitchBytes);
                srcRow.CopyTo(dstRow);
            }
        }

        return ValueTask.CompletedTask;
    }

    internal void ReleaseMemory()
    {
        _customCopyDelegate = null;
        if (_stagingBuffer is not null)
        {
            Array.Clear(_stagingBuffer);
            _stagingBuffer = null;
        }
    }
}

/// <summary>
///Bounded pool of <see cref="IFrameResourceLease"/> slots for a specific capture epoch and surface geometry.
/// Guarantees that at most <see cref="MaxSlots"/> frame buffers exist simultaneously for the active pool,
/// preventing unbounded memory growth if a downstream consumer stalls.
/// </summary>
public sealed class DxgiFramePool : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Queue<DxgiPooledFrameSlot> _availableSlots;
    private readonly List<DxgiPooledFrameSlot> _allSlots;
    private int _leasedCount;
    private int _createdSlots;
    private bool _retired;

    public DxgiFramePool(
        long poolEpoch,
        PixelSize size,
        FramePixelFormat pixelFormat,
        FrameBackingKind defaultBackingKind,
        int maxSlots)
    {
        if (size.IsEmpty)
        {
            throw new ArgumentException("Frame pool surface size must have positive width and height.", nameof(size));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maxSlots, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxSlots, 16);

        PoolEpoch = poolEpoch;
        Size = size;
        PixelFormat = pixelFormat;
        DefaultBackingKind = defaultBackingKind;
        MaxSlots = maxSlots;
        _availableSlots = new Queue<DxgiPooledFrameSlot>(maxSlots);
        _allSlots = new List<DxgiPooledFrameSlot>(maxSlots);
    }

    public long PoolEpoch { get; }
    public PixelSize Size { get; }
    public FramePixelFormat PixelFormat { get; }
    public FrameBackingKind DefaultBackingKind { get; }
    public int MaxSlots { get; }

    public int ActiveLeasedCount
    {
        get
        {
            lock (_gate)
            {
                return _leasedCount;
            }
        }
    }

    public int TotalAllocatedSlots
    {
        get
        {
            lock (_gate)
            {
                return _createdSlots;
            }
        }
    }

    public bool IsRetired
    {
        get
        {
            lock (_gate)
            {
                return _retired;
            }
        }
    }

    /// <summary>
    /// Attempts to rent a slot from the bounded pool.
    /// Returns <see langword="false"/> if the pool is retired or all <see cref="MaxSlots"/> are currently leased out.
    /// </summary>
    public bool TryRentLease(
        out DxgiPooledFrameSlot? slot,
        out IFrameResourceLease? lease)
    {
        lock (_gate)
        {
            if (_retired)
            {
                slot = null;
                lease = null;
                return false;
            }

            if (_availableSlots.TryDequeue(out var existing))
            {
                _leasedCount++;
                slot = existing;
                lease = new PooledFrameResourceLease(this, existing);
                return true;
            }

            if (_createdSlots >= MaxSlots)
            {
                slot = null;
                lease = null;
                return false;
            }

            var created = new DxgiPooledFrameSlot(
                _createdSlots,
                PoolEpoch,
                Size,
                PixelFormat,
                DefaultBackingKind);
            _createdSlots++;
            _allSlots.Add(created);
            _leasedCount++;

            slot = created;
            lease = new PooledFrameResourceLease(this, created);
            return true;
        }
    }

    /// <summary>
    /// Retires the pool when monitor dimensions, pixel format, rotation, or capture epoch changes.
    /// Free slots release their memory immediately; currently leased slots release their memory as soon as
    /// their owning <see cref="CapturedFrame"/> is disposed.
    /// </summary>
    public void RetireAll()
    {
        lock (_gate)
        {
            if (_retired)
            {
                return;
            }

            _retired = true;
            foreach (var slot in _allSlots)
            {
                slot.IsRetired = true;
            }

            while (_availableSlots.TryDequeue(out var freeSlot))
            {
                freeSlot.ReleaseMemory();
            }
        }
    }

    public void Dispose() => RetireAll();

    private void ReturnSlot(DxgiPooledFrameSlot slot)
    {
        lock (_gate)
        {
            if (_leasedCount > 0)
            {
                _leasedCount--;
            }

            if (_retired || slot.IsRetired)
            {
                slot.ReleaseMemory();
                return;
            }

            slot.BindCopyDelegate(null, DefaultBackingKind);
            _availableSlots.Enqueue(slot);
        }
    }

    private sealed class PooledFrameResourceLease : IFrameResourceLease
    {
        private readonly DxgiFramePool _pool;
        private readonly DxgiPooledFrameSlot _slot;
        private int _disposed;

        public PooledFrameResourceLease(DxgiFramePool pool, DxgiPooledFrameSlot slot)
        {
            _pool = pool;
            _slot = slot;
        }

        public FrameBackingKind BackingKind => _slot.BackingKind;
        public PixelSize Size => _slot.Size;
        public FramePixelFormat PixelFormat => _slot.PixelFormat;

        public ValueTask CopyToCpuBufferAsync(Memory<byte> destination, int strideBytes, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _slot.CopyToDestinationAsync(destination, strideBytes, ct);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _pool.ReturnSlot(_slot);
            }
        }
    }
}
