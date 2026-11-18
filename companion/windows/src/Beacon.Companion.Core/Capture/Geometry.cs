namespace Beacon.Companion.Core.Capture;

/// <summary>Immutable non-negative pixel dimensions.</summary>
public readonly record struct PixelSize
{
    public int Width { get; }
    public int Height { get; }

    public PixelSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Width = width;
        Height = height;
    }

    public bool IsEmpty => Width == 0 || Height == 0;
}

/// <summary>
/// Immutable display or dirty-region rectangle in Windows virtual-desktop pixel coordinates.
/// Left and Top may be negative on secondary monitors positioned left/above the primary display.
/// </summary>
public readonly record struct PixelRect
{
    public int Left { get; }
    public int Top { get; }
    public int Width { get; }
    public int Height { get; }

    public PixelRect(int left, int top, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public int Right => Left + Width;
    public int Bottom => Top + Height;
    public bool IsEmpty => Width == 0 || Height == 0;

    public bool Contains(int x, int y) =>
        x >= Left && x < Right && y >= Top && y < Bottom;
}
