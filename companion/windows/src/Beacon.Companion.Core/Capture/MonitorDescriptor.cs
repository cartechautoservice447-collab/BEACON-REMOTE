namespace Beacon.Companion.Core.Capture;

public enum DisplayRotation
{
    Identity = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270,
}

/// <summary>
/// Immutable metadata describing a connected interactive desktop display.
/// </summary>
public sealed record MonitorDescriptor(
    string MonitorId,
    string DeviceName,
    string FriendlyName,
    bool IsPrimary,
    PixelRect DesktopBounds,
    PixelSize PhysicalSize,
    DisplayRotation Rotation,
    double ScaleFactor,
    int RefreshRateHz);
