using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Beacon.Companion.Core.Capture;
using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Security;
using Beacon.Companion.Windows.Capture;

namespace Beacon.Companion.DxgiSmokeTest;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static partial class Program
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint UoiName = 2;

    private static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            Console.WriteLine("ERROR: Beacon.Companion.DxgiSmokeTest requires Windows 10 (19041+) or Windows 11.");
            return 1;
        }

        if (!Environment.UserInteractive)
        {
            Console.WriteLine("ERROR: Interactive desktop session required. Cannot run DXGI Desktop Duplication from Session 0.");
            return 2;
        }

        var desktopName = QueryCurrentInputDesktopName();
        Console.WriteLine("=================================================================");
        Console.WriteLine("BEACON REMOTE — STEP 3.5 PHYSICAL DXGI HARDWARE SMOKE HARNESS");
        Console.WriteLine("=================================================================");
        Console.WriteLine($"Timestamp (UTC): {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"OS Version     : {RuntimeInformation.OSDescription}");
        Console.WriteLine($"Process Arch   : {RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"Interactive    : {Environment.UserInteractive}");
        Console.WriteLine($"Input Desktop  : {desktopName}");
        Console.WriteLine();

        var report = new StringBuilder();
        AppendHeader(report, desktopName);

        var runNonInteractiveQuick = args.Any(a => string.Equals(a, "--quick", StringComparison.OrdinalIgnoreCase));

        await RunMonitorEnumerationAndInitialCaptureAsync(report).ConfigureAwait(false);
        await RunPoolExhaustionAndLifecycleCheckAsync(report).ConfigureAwait(false);

        if (!runNonInteractiveQuick)
        {
            await RunInteractiveHardwareChecksAsync(report).ConfigureAwait(false);
        }
        else
        {
            report.AppendLine("[SKIPPED INTERACTIVE CHECKS: invoked with --quick]");
        }

        var reportPath = Path.Combine(
            Directory.GetCurrentDirectory(),
            $"dxgi-physical-smoke-report-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.txt");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Hardware smoke report written to: {reportPath}");
        return 0;
    }

    private static async Task RunMonitorEnumerationAndInitialCaptureAsync(StringBuilder report)
    {
        Console.WriteLine("[1] Monitor Enumeration, DuplicateOutput, Initial Idle Frame & 300-Frame ReleaseFrame Stress");
        report.AppendLine("## 1. Monitor Enumeration, DuplicateOutput, Initial Idle Frame & ReleaseFrame Stress");

        var options = new DxgiScreenCaptureOptions
        {
            MaxPooledFrames = 4,
            MaxConsecutiveRecoveryAttempts = 4,
            MaxNotCurrentlyAvailableRetries = 3,
            EmitCursorOnlyFrames = false,
        };

        await using var source = WindowsScreenCaptureFactory.Create(options);

        var monitors = await source.GetMonitorsAsync().ConfigureAwait(false);
        Console.WriteLine($"  Enumerated {monitors.Count} display output(s):");
        report.AppendLine(CultureInfo.InvariantCulture, $"Enumerated Monitors: {monitors.Count}");
        foreach (var mon in monitors)
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"  - Id={mon.MonitorId} | Name={mon.FriendlyName} | Primary={mon.IsPrimary} | Bounds={mon.DesktopBounds.Left},{mon.DesktopBounds.Top},{mon.DesktopBounds.Width}x{mon.DesktopBounds.Height} | Physical={mon.PhysicalSize.Width}x{mon.PhysicalSize.Height} | Rot={mon.Rotation} | Scale={mon.ScaleFactor:F2} | Hz={mon.RefreshRateHz}");
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        if (monitors.Count == 0)
        {
            report.AppendLine("RESULT: FAIL (no attached desktop outputs found)");
            return;
        }

        await source.StartCaptureAsync(DxgiScreenCaptureOptions.PrimaryMonitorAlias).ConfigureAwait(false);
        Console.WriteLine($"  DuplicateOutput initialized: IsCapturing={source.IsCapturing}, Active={source.ActiveMonitor?.MonitorId}, Availability={source.SurfaceAvailability}");
        report.AppendLine(CultureInfo.InvariantCulture, $"DuplicateOutput Initialized: Active={source.ActiveMonitor?.MonitorId}, Epoch={source.CaptureEpoch}, Availability={source.SurfaceAvailability}");

        // Idle initial frame check: do NOT move mouse or open windows.
        Console.WriteLine("  Waiting for initial idle desktop frame (do not move mouse)...");
        using var initialFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        if (initialFrame is null)
        {
            Console.WriteLine("  WARNING: Initial AcquireNextFrameAsync returned null on idle desktop.");
            report.AppendLine("Initial Idle Frame: FAIL (null returned)");
        }
        else
        {
            var sample = await SampleFramePixelsAsync(initialFrame).ConfigureAwait(false);
            var msg = string.Create(
                CultureInfo.InvariantCulture,
                $"Initial Idle Frame OK: Seq={initialFrame.FrameSequence}, Size={initialFrame.Size.Width}x{initialFrame.Size.Height}, Format={initialFrame.PixelFormat}, Backing={initialFrame.BackingKind}, DirtyCount={initialFrame.DirtyRects.Length}, SHA256={sample.Sha256Prefix}, NonZeroBytes={sample.NonZeroSamples}/{sample.SampleCount}");
            Console.WriteLine($"  {msg}");
            report.AppendLine(msg);
        }

        // 300-frame ReleaseFrame & cursor tracking loop
        var captured = 0;
        var timeouts = 0;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 300 && sw.Elapsed < TimeSpan.FromSeconds(5); i++)
        {
            using var frame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(32)).ConfigureAwait(false);
            if (frame is not null)
            {
                captured++;
            }
            else
            {
                timeouts++;
            }
        }

        var diag = source.GetDiagnostics();
        var loopSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"Stress Loop: Frames={captured}, Timeouts={timeouts}, CursorOnlyUpdates={diag.TotalCursorOnlyUpdates}, CursorPos=({source.LatestCursor.X},{source.LatestCursor.Y}) Visible={source.LatestCursor.IsVisible} ShapeVer={source.LatestCursor.ShapeVersion}, AccessLost={diag.TotalAccessLostRecoveries}, DeviceReset={diag.TotalDeviceResetRecoveries}, LastStatus={diag.LastStatus} (0x{diag.LastHResult:X8})");
        Console.WriteLine($"  {loopSummary}");
        report.AppendLine(loopSummary);

        if (source.LatestCursorShape is { } shape)
        {
            var shapeHash = Convert.ToHexString(SHA256.HashData(shape.PixelData.Span))[..16];
            var shapeLine = string.Create(
                CultureInfo.InvariantCulture,
                $"Latest Cursor Shape: Kind={shape.Kind}, Size={shape.Size.Width}x{shape.Size.Height}, HotSpot=({shape.HotSpotX},{shape.HotSpotY}), Pitch={shape.PitchBytes}, SHA256={shapeHash}");
            Console.WriteLine($"  {shapeLine}");
            report.AppendLine(shapeLine);
        }

        if (monitors.Count > 1)
        {
            var secondary = monitors[1];
            var beforeEpoch = source.CaptureEpoch;
            await source.StartCaptureAsync(secondary.MonitorId).ConfigureAwait(false);
            using var secFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
            var switchLine = string.Create(
                CultureInfo.InvariantCulture,
                $"Multi-Monitor Switch: {monitors[0].MonitorId} -> {secondary.MonitorId}, Epoch {beforeEpoch}->{source.CaptureEpoch}, FrameAcquired={secFrame is not null}, FrameSize={secFrame?.Size.Width}x{secFrame?.Size.Height}");
            Console.WriteLine($"  {switchLine}");
            report.AppendLine(switchLine);
        }
        else
        {
            Console.WriteLine("  Multi-Monitor Switch: single monitor connected; attach external display to exercise switching.");
            report.AppendLine("Multi-Monitor Switch: NOT TESTED (only 1 monitor attached)");
        }

        await source.StopCaptureAsync().ConfigureAwait(false);
        Console.WriteLine($"  StopCaptureAsync complete: IsCapturing={source.IsCapturing}, Availability={source.SurfaceAvailability}");
        report.AppendLine(CultureInfo.InvariantCulture, $"StopCaptureAsync: IsCapturing={source.IsCapturing}, Availability={source.SurfaceAvailability}");
        await source.DisposeAsync().ConfigureAwait(false);

        var disposedRejected = false;
        try
        {
            _ = await source.GetMonitorsAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            disposedRejected = true;
        }

        Console.WriteLine($"  DisposeAsync verified (post-dispose throws ObjectDisposedException): {disposedRejected}");
        report.AppendLine(CultureInfo.InvariantCulture, $"DisposeAsync Clean: {disposedRejected}");
        report.AppendLine();
    }

    private static async Task RunPoolExhaustionAndLifecycleCheckAsync(StringBuilder report)
    {
        Console.WriteLine("[2] Frame-Pool Exhaustion & Slot Recycling Verification (MaxPooledFrames = 2)");
        report.AppendLine("## 2. Frame-Pool Exhaustion & Recycling");

        await using var source = WindowsScreenCaptureFactory.Create(new DxgiScreenCaptureOptions
        {
            MaxPooledFrames = 2,
            EmitCursorOnlyFrames = true,
        });

        await source.StartCaptureAsync(DxgiScreenCaptureOptions.PrimaryMonitorAlias).ConfigureAwait(false);

        CapturedFrame? f1 = null;
        CapturedFrame? f2 = null;
        try
        {
            f1 = await AcquireWithRetryAsync(source, 5).ConfigureAwait(false);
            f2 = await AcquireWithRetryAsync(source, 5).ConfigureAwait(false);

            if (f1 is null || f2 is null)
            {
                Console.WriteLine("  Skipped exhaustion check (desktop did not produce 2 frames within window).");
                report.AppendLine("Pool Exhaustion: INCONCLUSIVE (insufficient frames acquired)");
                return;
            }

            using var dropped = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            var diagExhausted = source.GetDiagnostics();
            Console.WriteLine($"  Exhausted check: DroppedNull={dropped is null}, ActiveLeased={diagExhausted.ActiveLeasedFrames}, TotalSlots={diagExhausted.TotalPooledSlots}, Drops={diagExhausted.TotalPoolExhaustedDrops}");

            f1.Dispose();
            f1 = null;

            using var recycled = await AcquireWithRetryAsync(source, 5).ConfigureAwait(false);
            var diagRecycled = source.GetDiagnostics();
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"Pool Exhaustion & Recycle: DroppedOnFull={dropped is null}, LeasedWhenFull={diagExhausted.ActiveLeasedFrames}/{diagExhausted.TotalPooledSlots}, Drops={diagExhausted.TotalPoolExhaustedDrops}, RecycledFrameAcquired={recycled is not null}, TotalSlotsAfterRecycle={diagRecycled.TotalPooledSlots}");
            Console.WriteLine($"  {line}");
            report.AppendLine(line);
        }
        finally
        {
            f1?.Dispose();
            f2?.Dispose();
        }

        report.AppendLine();
    }

    private static async Task RunInteractiveHardwareChecksAsync(StringBuilder report)
    {
        Console.WriteLine("[3] Interactive Hardware Checks (Rotation, Resolution/ACCESS_LOST, Secure Desktop Win+L/UAC)");
        report.AppendLine("## 3. Interactive Hardware Checks");

        var stateMachine = new SessionStateMachine();
        var companionOptions = new CompanionOptions
        {
            ServerOrigin = new Uri("https://beacon.example.com"),
            DeviceName = Environment.MachineName,
            RequireOwnerApprovalForFullControl = false,
        };
        _ = stateMachine.BeginSession(
            new SessionApprovalRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                PermissionMode.FullControl,
                IsUnattendedAttempt: false,
                DateTimeOffset.UtcNow),
            companionOptions,
            ownerExplicitlyApproved: true);

        await using var source = WindowsScreenCaptureFactory.Create(
            new DxgiScreenCaptureOptions
            {
                MaxPooledFrames = 4,
                ProtectedDesktopBackoffDelay = TimeSpan.FromMilliseconds(150),
            },
            stateMachine);

        await source.StartCaptureAsync(DxgiScreenCaptureOptions.PrimaryMonitorAlias).ConfigureAwait(false);

        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("Select interactive hardware check:");
            Console.WriteLine("  [R] Monitor rotation / resolution / topology change (15s capture window)");
            Console.WriteLine("  [S] Secure Desktop (Win+L or UAC prompt) fail-closed & unlock check (20s window)");
            Console.WriteLine("  [C] Cursor shape & position tracking check (10s window)");
            Console.WriteLine("  [Q] Finish and write smoke report");
            Console.Write("Choice: ");
            var choice = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(choice) || choice.Equals("Q", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (choice.Equals("R", StringComparison.OrdinalIgnoreCase))
            {
                await ObserveWindowAsync(source, stateMachine, report, "Rotation/Resolution/Topology", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            else if (choice.Equals("S", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Trigger UAC elevation or press Win+L now, then cancel UAC / unlock within 20 seconds...");
                await ObserveWindowAsync(source, stateMachine, report, "ProtectedDesktop/Win+L/UAC", TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            else if (choice.Equals("C", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  Move mouse across text boxes, window borders, and links for 10 seconds...");
                await ObserveWindowAsync(source, stateMachine, report, "CursorTracking", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
    }

    private static async Task ObserveWindowAsync(
        DxgiScreenCaptureSource source,
        SessionStateMachine stateMachine,
        StringBuilder report,
        string label,
        TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        var lastAvail = source.SurfaceAvailability;
        var lastSessionState = stateMachine.State;
        var lastSize = source.ActiveMonitor?.PhysicalSize ?? default;
        var lastRot = source.ActiveMonitor?.Rotation ?? DisplayRotation.Identity;
        var lastShapeVer = source.LatestCursor.ShapeVersion;
        var frames = 0;

        while (sw.Elapsed < duration)
        {
            try
            {
                using var frame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
                if (frame is not null)
                {
                    frames++;
                    if (frame.Size != lastSize || frame.Monitor.Rotation != lastRot)
                    {
                        var sample = await SampleFramePixelsAsync(frame).ConfigureAwait(false);
                        var msg = string.Create(
                            CultureInfo.InvariantCulture,
                            $"[{label}] Geometry change: {lastSize.Width}x{lastSize.Height} ({lastRot}) -> {frame.Size.Width}x{frame.Size.Height} ({frame.Monitor.Rotation}), Dirty={frame.DirtyRects.Length}, SHA256={sample.Sha256Prefix}");
                        Console.WriteLine($"  {msg}");
                        report.AppendLine(msg);
                        lastSize = frame.Size;
                        lastRot = frame.Monitor.Rotation;
                    }
                }
            }
            catch (DxgiCaptureException ex)
            {
                var errLine = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{label}] DxgiCaptureException: Status={ex.Status}, HResult=0x{ex.HResult:X8}, Message={ex.Message}");
                Console.WriteLine($"  {errLine}");
                report.AppendLine(errLine);
            }

            if (source.SurfaceAvailability != lastAvail || stateMachine.State != lastSessionState)
            {
                var diagNow = source.GetDiagnostics();
                var stateMsg = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{label}] State transition: Surface={lastAvail}->{source.SurfaceAvailability}, Session={lastSessionState}->{stateMachine.State}, LastHResult=0x{diagNow.LastHResult:X8}");
                Console.WriteLine($"  {stateMsg}");
                report.AppendLine(stateMsg);
                lastAvail = source.SurfaceAvailability;
                lastSessionState = stateMachine.State;
            }

            if (source.LatestCursor.ShapeVersion != lastShapeVer && source.LatestCursorShape is { } newShape)
            {
                lastShapeVer = source.LatestCursor.ShapeVersion;
                var hash = Convert.ToHexString(SHA256.HashData(newShape.PixelData.Span))[..16];
                var curMsg = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{label}] Cursor shape v{lastShapeVer}: {newShape.Kind} {newShape.Size.Width}x{newShape.Size.Height} pos=({source.LatestCursor.X},{source.LatestCursor.Y}) hash={hash}");
                Console.WriteLine($"  {curMsg}");
                report.AppendLine(curMsg);
            }
        }

        var endDiag = source.GetDiagnostics();
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"[{label}] Window Done: Frames={frames}, AccessLost={endDiag.TotalAccessLostRecoveries}, DeviceReset={endDiag.TotalDeviceResetRecoveries}, ProtectedEvents={endDiag.TotalProtectedDesktopEvents}, NotAvailRetries={endDiag.TotalNotCurrentlyAvailableRetries}, FinalAvail={endDiag.SurfaceAvailability}");
        Console.WriteLine($"  {summary}");
        report.AppendLine(summary);
    }

    private static async ValueTask<CapturedFrame?> AcquireWithRetryAsync(DxgiScreenCaptureSource source, int maxAttempts)
    {
        for (var i = 0; i < maxAttempts; i++)
        {
            var frame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
            if (frame is not null)
            {
                return frame;
            }
        }

        return null;
    }

    private static async Task<(string Sha256Prefix, int NonZeroSamples, int SampleCount)> SampleFramePixelsAsync(
        CapturedFrame frame)
    {
        var bpp = frame.PixelFormat == FramePixelFormat.Rgba16Float ? 8 : 4;
        var stride = frame.Size.Width * bpp;
        var buffer = new byte[stride * frame.Size.Height];
        await frame.ResourceLease.CopyToCpuBufferAsync(buffer, stride).ConfigureAwait(false);

        var hash = Convert.ToHexString(SHA256.HashData(buffer))[..16];
        var step = Math.Max(1, buffer.Length / 1024);
        var nonZero = 0;
        var total = 0;
        for (var i = 0; i < buffer.Length; i += step)
        {
            total++;
            if (buffer[i] != 0)
            {
                nonZero++;
            }
        }

        return (hash, nonZero, total);
    }

    private static void AppendHeader(StringBuilder report, string desktopName)
    {
        report.AppendLine("BEACON REMOTE — STEP 3.5 PHYSICAL DXGI HARDWARE SMOKE REPORT");
        report.AppendLine(CultureInfo.InvariantCulture, $"TimestampUTC: {DateTimeOffset.UtcNow:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"OS: {RuntimeInformation.OSDescription}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Interactive: {Environment.UserInteractive}");
        report.AppendLine(CultureInfo.InvariantCulture, $"InputDesktop: {desktopName}");
        report.AppendLine();
    }

    private static unsafe string QueryCurrentInputDesktopName()
    {
        var hDesktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (hDesktop == 0)
        {
            return $"Unavailable (Win32Error={Marshal.GetLastWin32Error()})";
        }

        try
        {
            var buffer = new byte[256];
            fixed (byte* pBuffer = buffer)
            {
                if (GetUserObjectInformationW(hDesktop, UoiName, pBuffer, (uint)buffer.Length, out var needed) && needed > 0)
                {
                    return Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length)).TrimEnd('\0');
                }
            }

            return "Unknown";
        }
        finally
        {
            _ = CloseDesktop(hDesktop);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint dwFlags, [MarshalAs(UnmanagedType.Bool)] bool fInherit, uint dwDesiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint hDesktop);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetUserObjectInformationW(
        nint hObj,
        int nIndex,
        byte* pvInfo,
        uint nLength,
        out uint lpnLengthNeeded);
}
