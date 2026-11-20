using System.Buffers.Binary;

namespace Beacon.Companion.Core.Capture;

/// <summary>
/// Deterministic geometry, rotation, pixel-orientation, dirty-rectangle, cursor-coordinate,
/// and monitor-selection helpers for DXGI Desktop Duplication.
/// </summary>
public static class DxgiGeometryNormalizer
{
    /// <summary>
    /// Computes the logical oriented dimensions for an unrotated surface under <paramref name="rotation"/>.
    /// <see cref="DisplayRotation.Rotate90"/> and <see cref="DisplayRotation.Rotate270"/> swap width and height.
    /// </summary>
    public static PixelSize GetOrientedSize(PixelSize unrotatedSize, DisplayRotation rotation) =>
        rotation is DisplayRotation.Rotate90 or DisplayRotation.Rotate270
            ? new PixelSize(unrotatedSize.Height, unrotatedSize.Width)
            : unrotatedSize;

    /// <summary>
    /// Computes the unrotated surface dimensions corresponding to an oriented <paramref name="orientedSize"/>
    /// under <paramref name="rotation"/>.
    /// </summary>
    public static PixelSize GetUnrotatedSize(PixelSize orientedSize, DisplayRotation rotation) =>
        rotation is DisplayRotation.Rotate90 or DisplayRotation.Rotate270
            ? new PixelSize(orientedSize.Height, orientedSize.Width)
            : orientedSize;

    /// <summary>
    /// Rotates raw desktop surface pixels from unrotated panel coordinates (<paramref name="sourceWidth"/> x <paramref name="sourceHeight"/>)
    /// into upright oriented desktop coordinates matching <see cref="TransformRectForRotation"/>.
    /// Step 3.5 <c>IDXGIOutput1::DuplicateOutput</c> provides 4-byte (<see cref="FramePixelFormat.Bgra8Unorm"/>) SDR pixels
    /// with potentially padded D3D11 staging row pitches.
    /// </summary>
    public static void RotatePixels(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceRowPitchBytes,
        Span<byte> destination,
        int destinationRowPitchBytes,
        int bytesPerPixel,
        DisplayRotation rotation)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceWidth, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sourceHeight, 0);
        if (bytesPerPixel != 4)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytesPerPixel),
                bytesPerPixel,
                "Step 3.5 IDXGIOutput1::DuplicateOutput supports only 4-byte (Bgra8Unorm) pixels.");
        }

        var sourceTightRowBytes = checked(sourceWidth * bytesPerPixel);
        ArgumentOutOfRangeException.ThrowIfLessThan(sourceRowPitchBytes, sourceTightRowBytes);

        var orientedSize = GetOrientedSize(new PixelSize(sourceWidth, sourceHeight), rotation);
        var destinationTightRowBytes = checked(orientedSize.Width * bytesPerPixel);
        ArgumentOutOfRangeException.ThrowIfLessThan(destinationRowPitchBytes, destinationTightRowBytes);

        var requiredSourceBytes = checked((sourceHeight - 1) * sourceRowPitchBytes + sourceTightRowBytes);
        if (source.Length < requiredSourceBytes)
        {
            throw new ArgumentException(
                $"Source span length ({source.Length}) is smaller than required ({requiredSourceBytes} bytes).",
                nameof(source));
        }

        var requiredDestinationBytes = checked((orientedSize.Height - 1) * destinationRowPitchBytes + destinationTightRowBytes);
        if (destination.Length < requiredDestinationBytes)
        {
            throw new ArgumentException(
                $"Destination span length ({destination.Length}) is smaller than required ({requiredDestinationBytes} bytes).",
                nameof(destination));
        }

        if (rotation == DisplayRotation.Identity)
        {
            if (sourceRowPitchBytes == sourceTightRowBytes &&
                destinationRowPitchBytes == destinationTightRowBytes)
            {
                source[..requiredSourceBytes].CopyTo(destination[..requiredDestinationBytes]);
                return;
            }

            for (var y = 0; y < sourceHeight; y++)
            {
                var srcRow = source.Slice(y * sourceRowPitchBytes, sourceTightRowBytes);
                var dstRow = destination.Slice(y * destinationRowPitchBytes, destinationTightRowBytes);
                srcRow.CopyTo(dstRow);
            }

            return;
        }

        RotatePixels4Bpp(
            source,
            sourceWidth,
            sourceHeight,
            sourceRowPitchBytes,
            destination,
            destinationRowPitchBytes,
            rotation);
    }

    private static void RotatePixels4Bpp(
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceRowPitchBytes,
        Span<byte> destination,
        int destinationRowPitchBytes,
        DisplayRotation rotation)
    {
        switch (rotation)
        {
            case DisplayRotation.Rotate90:
                for (var y = 0; y < sourceHeight; y++)
                {
                    var srcRowOffset = y * sourceRowPitchBytes;
                    var dstXOffset = (sourceHeight - 1 - y) * 4;
                    for (var x = 0; x < sourceWidth; x++)
                    {
                        var pixel = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(srcRowOffset + x * 4, 4));
                        var dstOffset = x * destinationRowPitchBytes + dstXOffset;
                        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(dstOffset, 4), pixel);
                    }
                }

                break;

            case DisplayRotation.Rotate180:
                for (var y = 0; y < sourceHeight; y++)
                {
                    var srcRowOffset = y * sourceRowPitchBytes;
                    var dstRowOffset = (sourceHeight - 1 - y) * destinationRowPitchBytes;
                    for (var x = 0; x < sourceWidth; x++)
                    {
                        var pixel = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(srcRowOffset + x * 4, 4));
                        var dstOffset = dstRowOffset + (sourceWidth - 1 - x) * 4;
                        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(dstOffset, 4), pixel);
                    }
                }

                break;

            case DisplayRotation.Rotate270:
                for (var y = 0; y < sourceHeight; y++)
                {
                    var srcRowOffset = y * sourceRowPitchBytes;
                    var dstXOffset = y * 4;
                    for (var x = 0; x < sourceWidth; x++)
                    {
                        var pixel = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(srcRowOffset + x * 4, 4));
                        var dstOffset = (sourceWidth - 1 - x) * destinationRowPitchBytes + dstXOffset;
                        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(dstOffset, 4), pixel);
                    }
                }

                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "Unsupported display rotation.");
        }
    }

    /// <summary>
    /// Transforms a rectangle from unrotated surface space into the rotated coordinate space defined by <paramref name="rotation"/>.
    /// </summary>
    public static PixelRect TransformRectForRotation(
        PixelRect rect,
        PixelSize unrotatedSurfaceSize,
        DisplayRotation rotation)
    {
        if (rect.IsEmpty || unrotatedSurfaceSize.IsEmpty)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        var clamped = ClampToBounds(rect, unrotatedSurfaceSize);
        if (clamped.IsEmpty)
        {
            return clamped;
        }

        return rotation switch
        {
            DisplayRotation.Identity => clamped,
            DisplayRotation.Rotate90 => new PixelRect(
                left: unrotatedSurfaceSize.Height - clamped.Bottom,
                top: clamped.Left,
                width: clamped.Height,
                height: clamped.Width),
            DisplayRotation.Rotate180 => new PixelRect(
                left: unrotatedSurfaceSize.Width - clamped.Right,
                top: unrotatedSurfaceSize.Height - clamped.Bottom,
                width: clamped.Width,
                height: clamped.Height),
            DisplayRotation.Rotate270 => new PixelRect(
                left: clamped.Top,
                top: unrotatedSurfaceSize.Width - clamped.Right,
                width: clamped.Height,
                height: clamped.Width),
            _ => clamped,
        };
    }

    /// <summary>
    /// Clamps <paramref name="rect"/> into <c>[0, surfaceSize.Width] x [0, surfaceSize.Height]</c>.
    /// </summary>
    public static PixelRect ClampToBounds(PixelRect rect, PixelSize surfaceSize)
    {
        if (rect.IsEmpty || surfaceSize.IsEmpty)
        {
            return new PixelRect(0, 0, 0, 0);
        }

        var left = Math.Clamp(rect.Left, 0, surfaceSize.Width);
        var top = Math.Clamp(rect.Top, 0, surfaceSize.Height);
        var right = Math.Clamp(rect.Right, left, surfaceSize.Width);
        var bottom = Math.Clamp(rect.Bottom, top, surfaceSize.Height);

        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Normalizes raw DXGI dirty rectangles and move destination rectangles into valid, clamped,
    /// non-empty <see cref="PixelRect"/> regions. Falls back to the full surface rectangle when
    /// a desktop visual update occurred without explicit dirty regions.
    /// </summary>
    public static ReadOnlyMemory<PixelRect> NormalizeDirtyRegions(
        ReadOnlySpan<PixelRect> rawDirtyRects,
        ReadOnlySpan<DxgiMoveRect> rawMoveRects,
        PixelSize targetSurfaceSize,
        bool hasDesktopImageUpdate)
    {
        if (targetSurfaceSize.IsEmpty || !hasDesktopImageUpdate)
        {
            return ReadOnlyMemory<PixelRect>.Empty;
        }

        var totalCandidates = rawDirtyRects.Length + rawMoveRects.Length;
        if (totalCandidates == 0)
        {
            return new[] { new PixelRect(0, 0, targetSurfaceSize.Width, targetSurfaceSize.Height) };
        }

        var normalized = new List<PixelRect>(totalCandidates);

        foreach (var dirty in rawDirtyRects)
        {
            var clamped = ClampToBounds(dirty, targetSurfaceSize);
            if (!clamped.IsEmpty && !normalized.Contains(clamped))
            {
                normalized.Add(clamped);
            }
        }

        foreach (var move in rawMoveRects)
        {
            var clampedDest = ClampToBounds(move.DestinationRect, targetSurfaceSize);
            if (!clampedDest.IsEmpty && !normalized.Contains(clampedDest))
            {
                normalized.Add(clampedDest);
            }
        }

        if (normalized.Count == 0)
        {
            normalized.Add(new PixelRect(0, 0, targetSurfaceSize.Width, targetSurfaceSize.Height));
        }

        return normalized.ToArray();
    }

    /// <summary>
    /// Clamps raw pointer coordinates to the active surface bounds when visible.
    /// </summary>
    public static (int X, int Y) ClampCursorCoordinates(int x, int y, PixelSize surfaceSize)
    {
        if (surfaceSize.IsEmpty)
        {
            return (0, 0);
        }

        var clampedX = Math.Clamp(x, 0, Math.Max(0, surfaceSize.Width - 1));
        var clampedY = Math.Clamp(y, 0, Math.Max(0, surfaceSize.Height - 1));
        return (clampedX, clampedY);
    }

    /// <summary>
    /// Orders enumerated monitors deterministically: primary monitor first, then by virtual desktop
    /// Left coordinate, Top coordinate, and ordinal MonitorId.
    /// </summary>
    public static IReadOnlyList<MonitorDescriptor> OrderMonitorsDeterministically(
        IEnumerable<MonitorDescriptor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        return monitors
            .Where(static m => m is not null && !m.PhysicalSize.IsEmpty && !string.IsNullOrWhiteSpace(m.MonitorId))
            .OrderByDescending(static m => m.IsPrimary)
            .ThenBy(static m => m.DesktopBounds.Left)
            .ThenBy(static m => m.DesktopBounds.Top)
            .ThenBy(static m => m.MonitorId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves a monitor from <paramref name="monitors"/> by exact <c>MonitorId</c>, <c>DeviceName</c>,
    /// or the <c>"primary"</c> alias.
    /// </summary>
    public static MonitorDescriptor? ResolveMonitor(
        IReadOnlyList<MonitorDescriptor> monitors,
        string requestedMonitorId)
    {
        ArgumentNullException.ThrowIfNull(monitors);

        if (string.IsNullOrWhiteSpace(requestedMonitorId) || monitors.Count == 0)
        {
            return null;
        }

        var trimmed = requestedMonitorId.Trim();
        if (string.Equals(trimmed, DxgiScreenCaptureOptions.PrimaryMonitorAlias, StringComparison.OrdinalIgnoreCase))
        {
            return monitors.FirstOrDefault(static m => m.IsPrimary) ?? monitors[0];
        }

        return monitors.FirstOrDefault(m =>
                   string.Equals(m.MonitorId, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? monitors.FirstOrDefault(m =>
                   string.Equals(m.DeviceName, trimmed, StringComparison.OrdinalIgnoreCase));
    }
}
