namespace Beacon.Companion.Core.Protocol;

/// <summary>
/// Configuration for converting frontend <see cref="PointerWheelEvent"/> CSS-pixel deltas
/// into discrete Win32-ready wheel units at the Windows input adapter boundary (Step 3.6).
/// </summary>
public sealed record WheelConversionOptions
{
    /// <summary>
    /// Standard Windows <c>WHEEL_DELTA</c> notch value (120 units per detent).
    /// Incoming wire <c>dx</c>/<c>dy</c> values are NOT assumed to be in this unit.
    /// </summary>
    public const int StandardWin32WheelDetent = 120;

    /// <summary>
    /// Target Win32 wheel units per incoming wire unit (negated CSS pixel * scrollSpeed).
    /// Default 3.0 maps ~40 CSS pixels of two-finger swipe to one 120-unit Windows notch.
    /// </summary>
    public double WheelUnitsPerCssPixel { get; init; } = 3.0;

    /// <summary>
    /// Clamping ceiling per converted batch to protect downstream native integer calls from overflow.
    /// </summary>
    public int MaxWheelUnitsPerBatch { get; init; } = 12_000;

    public void Validate()
    {
        if (!double.IsFinite(WheelUnitsPerCssPixel) || WheelUnitsPerCssPixel <= 0.0 || WheelUnitsPerCssPixel > 1_000.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(WheelUnitsPerCssPixel),
                "WheelUnitsPerCssPixel must be finite, > 0, and <= 1000.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxWheelUnitsPerBatch);
    }
}

/// <summary>
/// Result of accumulating a <see cref="PointerWheelEvent"/> across sub-unit remainders.
/// </summary>
public readonly record struct WheelDeltaBatch(
    int VerticalUnits,
    int HorizontalUnits,
    double RemainingVerticalUnits,
    double RemainingHorizontalUnits);

/// <summary>
/// Conversion boundary between wire-protocol wheel events (CSS pixels scaled by client scrollSpeed)
/// and discrete platform wheel injection units used by future OS adapters (Step 3.6).
/// </summary>
public interface IWheelDeltaConverter
{
    WheelDeltaBatch Accumulate(PointerWheelEvent wheelEvent);
    void Reset();
}

/// <summary>
/// Preserves fractional CSS-pixel scroll deltas across small increments so smooth low-velocity
/// gestures do not get dropped by integer truncation, while bounding single-batch outputs.
/// </summary>
public sealed class WheelDeltaAccumulator : IWheelDeltaConverter
{
    private readonly WheelConversionOptions _options;
    private readonly Lock _sync = new();
    private double _pendingVerticalUnits;
    private double _pendingHorizontalUnits;

    public WheelDeltaAccumulator(WheelConversionOptions? options = null)
    {
        _options = options ?? new WheelConversionOptions();
        _options.Validate();
    }

    public WheelDeltaBatch Accumulate(PointerWheelEvent wheelEvent)
    {
        ArgumentNullException.ThrowIfNull(wheelEvent);

        if (!double.IsFinite(wheelEvent.Dx) || !double.IsFinite(wheelEvent.Dy))
        {
            throw new ArgumentOutOfRangeException(nameof(wheelEvent), "Wheel deltas must be finite numbers.");
        }

        lock (_sync)
        {
            var totalY = _pendingVerticalUnits + (wheelEvent.Dy * _options.WheelUnitsPerCssPixel);
            var totalX = _pendingHorizontalUnits + (wheelEvent.Dx * _options.WheelUnitsPerCssPixel);

            var (verticalUnits, remY) = ExtractWholeAndRemainder(totalY, _options.MaxWheelUnitsPerBatch);
            var (horizontalUnits, remX) = ExtractWholeAndRemainder(totalX, _options.MaxWheelUnitsPerBatch);

            _pendingVerticalUnits = remY;
            _pendingHorizontalUnits = remX;

            return new WheelDeltaBatch(verticalUnits, horizontalUnits, remY, remX);
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _pendingVerticalUnits = 0;
            _pendingHorizontalUnits = 0;
        }
    }

    private static (int WholeUnits, double Remainder) ExtractWholeAndRemainder(double totalUnits, int maxAbsUnits)
    {
        if (totalUnits > maxAbsUnits)
        {
            return (maxAbsUnits, 0.0);
        }

        if (totalUnits < -maxAbsUnits)
        {
            return (-maxAbsUnits, 0.0);
        }

        var truncated = Math.Truncate(totalUnits);
        var whole = (int)truncated;
        var remainder = totalUnits - truncated;

        // Avoid tiny IEEE-754 noise (-0.0 or 1e-16) accumulating or reporting signed zero.
        if (Math.Abs(remainder) < 1e-9)
        {
            remainder = 0.0;
        }

        return (whole, remainder);
    }
}
