using Beacon.Companion.Core.Capture;
using Beacon.Companion.Core.Configuration;
using Beacon.Companion.Core.Security;
using Beacon.Companion.Windows.Capture;
using Xunit;

namespace Beacon.Companion.Tests;

public sealed class DxgiScreenCaptureSourceTests
{
    private static readonly MonitorDescriptor PrimaryMonitor = new(
        MonitorId: "\\\\.\\DISPLAY1",
        DeviceName: "\\\\.\\DISPLAY1",
        FriendlyName: "Primary Laptop Display",
        IsPrimary: true,
        DesktopBounds: new PixelRect(0, 0, 1920, 1080),
        PhysicalSize: new PixelSize(1920, 1080),
        Rotation: DisplayRotation.Identity,
        ScaleFactor: 1.25,
        RefreshRateHz: 120);

    private static readonly MonitorDescriptor LeftSecondaryMonitor = new(
        MonitorId: "\\\\.\\DISPLAY2",
        DeviceName: "\\\\.\\DISPLAY2",
        FriendlyName: "Left External QHD",
        IsPrimary: false,
        DesktopBounds: new PixelRect(-2560, -180, 2560, 1440),
        PhysicalSize: new PixelSize(2560, 1440),
        Rotation: DisplayRotation.Identity,
        ScaleFactor: 1.0,
        RefreshRateHz: 60);

    private static readonly MonitorDescriptor RightPortraitMonitor = new(
        MonitorId: "\\\\.\\DISPLAY3",
        DeviceName: "\\\\.\\DISPLAY3",
        FriendlyName: "Right Portrait Display",
        IsPrimary: false,
        DesktopBounds: new PixelRect(1920, 0, 1080, 1920),
        PhysicalSize: new PixelSize(1920, 1080),
        Rotation: DisplayRotation.Rotate90,
        ScaleFactor: 1.0,
        RefreshRateHz: 60);

    [Theory]
    [InlineData(DxgiHResult.SOk, DxgiCaptureStatus.Ok, false)]
    [InlineData(DxgiHResult.DxgiErrorWaitTimeout, DxgiCaptureStatus.WaitTimeout, false)]
    [InlineData(DxgiHResult.DxgiErrorAccessLost, DxgiCaptureStatus.AccessLost, true)]
    [InlineData(DxgiHResult.DxgiErrorDeviceRemoved, DxgiCaptureStatus.DeviceRemovedOrReset, true)]
    [InlineData(DxgiHResult.DxgiErrorDeviceHung, DxgiCaptureStatus.DeviceRemovedOrReset, true)]
    [InlineData(DxgiHResult.DxgiErrorDeviceReset, DxgiCaptureStatus.DeviceRemovedOrReset, true)]
    [InlineData(DxgiHResult.DxgiErrorDriverInternalError, DxgiCaptureStatus.DeviceRemovedOrReset, true)]
    [InlineData(DxgiHResult.DxgiErrorInvalidCall, DxgiCaptureStatus.InvalidCall, true)]
    [InlineData(DxgiHResult.DxgiErrorAccessDenied, DxgiCaptureStatus.AccessDeniedOrProtectedDesktop, false)]
    [InlineData(DxgiHResult.EAccessDenied, DxgiCaptureStatus.AccessDeniedOrProtectedDesktop, false)]
    [InlineData(DxgiHResult.DxgiErrorSessionDisconnected, DxgiCaptureStatus.SessionDisconnected, false)]
    [InlineData(DxgiHResult.DxgiErrorNotCurrentlyAvailable, DxgiCaptureStatus.MaxDuplicationClientsReached, false)]
    [InlineData(DxgiHResult.DxgiErrorUnsupported, DxgiCaptureStatus.UnsupportedOrUnavailable, false)]
    [InlineData(DxgiHResult.DxgiErrorNotFound, DxgiCaptureStatus.MonitorNotFound, false)]
    public void DxgiHResultClassifierMapsErrorCodesDeterministically(
        int hResult,
        DxgiCaptureStatus expectedStatus,
        bool expectedRecoverable)
    {
        var actual = DxgiHResultClassifier.Classify(hResult);
        Assert.Equal(expectedStatus, actual);
        Assert.Equal(expectedRecoverable, DxgiHResultClassifier.IsTransientRecoverable(actual));
    }

    [Fact]
    public async Task OutputEnumerationOrdersPrimaryFirstPreservesNegativeBoundsAndResolvesPrimaryAlias()
    {
        var backend = new FakeDxgiAdapterBackend([RightPortraitMonitor, LeftSecondaryMonitor, PrimaryMonitor]);
        await using var source = new DxgiScreenCaptureSource(backend);

        var monitors = await source.GetMonitorsAsync();

        Assert.Equal(3, monitors.Count);
        Assert.Equal("\\\\.\\DISPLAY1", monitors[0].MonitorId);
        Assert.True(monitors[0].IsPrimary);
        Assert.Equal("\\\\.\\DISPLAY2", monitors[1].MonitorId);
        Assert.Equal(-2560, monitors[1].DesktopBounds.Left);
        Assert.Equal(-180, monitors[1].DesktopBounds.Top);
        Assert.Equal("\\\\.\\DISPLAY3", monitors[2].MonitorId);

        await source.StartCaptureAsync("primary");
        Assert.True(source.IsCapturing);
        Assert.NotNull(source.ActiveMonitor);
        Assert.Equal("\\\\.\\DISPLAY1", source.ActiveMonitor.MonitorId);
        Assert.Equal(DesktopSurfaceAvailability.Available, source.SurfaceAvailability);
    }

    [Fact]
    public async Task StartCaptureRejectsUnknownOrEmptyMonitorId()
    {
        var backend = new FakeDxgiAdapterBackend([PrimaryMonitor]);
        await using var source = new DxgiScreenCaptureSource(backend);

        await Assert.ThrowsAsync<ArgumentException>(() => source.StartCaptureAsync("   "));

        var ex = await Assert.ThrowsAsync<DxgiCaptureException>(() => source.StartCaptureAsync("\\\\.\\DISPLAY99"));
        Assert.Equal(DxgiCaptureStatus.MonitorNotFound, ex.Status);
        Assert.False(source.IsCapturing);
    }

    [Fact]
    public async Task FrameAcquisitionCopiesPixelsGuaranteesReleaseFrameAndEnforcesMonotonicSequenceAndQpc()
    {
        var smallMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 4, 2),
            PhysicalSize = new PixelSize(4, 2),
        };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);

        var expectedPixels = new byte[4 * 2 * 4];
        for (var i = 0; i < expectedPixels.Length; i++)
        {
            expectedPixels[i] = (byte)(i + 17);
        }

        session.EnqueueDesktopFrame(
            qpcTicks: 5000,
            dirtyRects: [new PixelRect(1, 0, 2, 2)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 2, 1),
            pixelBytes: expectedPixels);

        session.EnqueueDesktopFrame(
            qpcTicks: 4000, // Out-of-order lower QPC from driver should be clamped to monotonic >= 5000
            dirtyRects: [],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 3, 1),
            pixelBytes: expectedPixels);

        await using var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        using var frame1 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(frame1);
        Assert.Equal(1, frame1.FrameSequence);
        Assert.Equal(5000, frame1.TimestampQpcTicks);
        Assert.Equal(new PixelSize(4, 2), frame1.Size);
        Assert.Equal(FramePixelFormat.Bgra8Unorm, frame1.PixelFormat);
        Assert.Single(frame1.DirtyRects.ToArray());
        Assert.Equal(new PixelRect(1, 0, 2, 2), frame1.DirtyRects.Span[0]);
        Assert.True(frame1.Cursor.IsVisible);
        Assert.Equal(2, frame1.Cursor.X);
        Assert.Equal(1, frame1.Cursor.Y);

        // ReleaseFrame must ALREADY have been called before AcquireNextFrameAsync returned to caller.
        Assert.Equal(1, session.AcquireCallCount);
        Assert.Equal(1, session.ReleaseFrameCallCount);
        Assert.False(session.IsFrameAcquired);

        var copiedBuffer = new byte[expectedPixels.Length];
        await frame1.ResourceLease.CopyToCpuBufferAsync(copiedBuffer, strideBytes: 16);
        Assert.Equal(expectedPixels, copiedBuffer);

        using var frame2 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(frame2);
        Assert.Equal(2, frame2.FrameSequence);
        Assert.Equal(5000, frame2.TimestampQpcTicks);
        // Empty dirty rects on a desktop update must normalize to full surface rect.
        Assert.Single(frame2.DirtyRects.ToArray());
        Assert.Equal(new PixelRect(0, 0, 4, 2), frame2.DirtyRects.Span[0]);
        Assert.Equal(2, session.ReleaseFrameCallCount);
    }

    [Fact]
    public async Task ReleaseFrameIsGuaranteedWhenCopyThrowsOrCursorOnlyUpdateArrives()
    {
        var smallMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 8, 8),
            PhysicalSize = new PixelSize(8, 8),
        };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);

        // 1. Cursor-only update (HasDesktopImageUpdate = false) with new pointer shape:
        var cursorShapeBytes = new byte[] { 0xFF, 0x00, 0xAA, 0x55 };
        session.EnqueueCursorOnlyFrame(
            new DxgiRawPointerUpdate(
                PositionUpdated: true,
                IsVisible: true,
                X: 6,
                Y: 5,
                NewShapeKind: PointerShapeKind.MaskedColor,
                NewShapeSize: new PixelSize(1, 1),
                NewShapePitchBytes: 4,
                HotSpotX: 0,
                HotSpotY: 0,
                NewShapeBytes: cursorShapeBytes));

        // 2. Frame where CopyAcquiredFrameToSlot throws an IO/GPU copy exception:
        session.EnqueueCopyFaultingFrame();

        await using var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        var cursorOnlyResult = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.Null(cursorOnlyResult);
        Assert.Equal(1, session.ReleaseFrameCallCount);
        Assert.False(session.IsFrameAcquired);
        Assert.Equal(1, source.LatestCursor.ShapeVersion);
        Assert.Equal(6, source.LatestCursor.X);
        Assert.Equal(5, source.LatestCursor.Y);
        Assert.NotNull(source.LatestCursorShape);
        Assert.Equal(PointerShapeKind.MaskedColor, source.LatestCursorShape.Kind);
        Assert.Equal(cursorShapeBytes, source.LatestCursorShape.PixelData.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20)).AsTask());

        // Even though CopyAcquiredFrameToSlot threw, ReleaseFrame MUST have been called and the frame pool lease returned!
        Assert.Equal(2, session.ReleaseFrameCallCount);
        Assert.False(session.IsFrameAcquired);
        Assert.Equal(0, source.GetDiagnostics().ActiveLeasedFrames);
    }

    [Theory]
    [InlineData(DisplayRotation.Identity, 10, 20, 30, 40, 10, 20, 30, 40)]
    [InlineData(DisplayRotation.Rotate90, 10, 20, 30, 40, 40, 10, 40, 30)]
    [InlineData(DisplayRotation.Rotate180, 10, 20, 30, 40, 160, 40, 30, 40)]
    [InlineData(DisplayRotation.Rotate270, 10, 20, 30, 40, 20, 160, 40, 30)]
    public void GeometryNormalizerTransformsDirtyRectanglesAcrossAllFourDisplayRotations(
        DisplayRotation rotation,
        int srcLeft,
        int srcTop,
        int srcWidth,
        int srcHeight,
        int expectedLeft,
        int expectedTop,
        int expectedWidth,
        int expectedHeight)
    {
        var unrotatedSurface = new PixelSize(200, 100);
        var sourceRect = new PixelRect(srcLeft, srcTop, srcWidth, srcHeight);

        var transformed = DxgiGeometryNormalizer.TransformRectForRotation(
            sourceRect,
            unrotatedSurface,
            rotation);

        Assert.Equal(new PixelRect(expectedLeft, expectedTop, expectedWidth, expectedHeight), transformed);
    }

    [Fact]
    public async Task AccessLostDuringResolutionAndRotationChangeRecreatesDuplicationRetiresOldPoolAndPublishesUpdatedGeometry()
    {
        var initialMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 16, 8),
            PhysicalSize = new PixelSize(16, 8),
            Rotation = DisplayRotation.Identity,
        };
        var rotatedMonitor = initialMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 8, 16),
            PhysicalSize = new PixelSize(8, 16),
            Rotation = DisplayRotation.Rotate90,
        };

        var backend = new FakeDxgiAdapterBackend([initialMonitor]);
        var session1 = backend.GetOrCreateScriptedSession(initialMonitor);
        session1.EnqueueDesktopFrame(
            qpcTicks: 100,
            dirtyRects: [new PixelRect(0, 0, 16, 8)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 4, 4),
            pixelBytes: new byte[16 * 8 * 4]);
        session1.EnqueueStatus(DxgiHResult.DxgiErrorAccessLost);

        var options = new DxgiScreenCaptureOptions
        {
            MaxPooledFrames = 2,
            AccessLostRetryDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);
        await source.StartCaptureAsync(initialMonitor.MonitorId);

        using var preChangeFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(preChangeFrame);
        Assert.Equal(new PixelSize(16, 8), preChangeFrame.Size);

        // Simulate Windows display topology/rotation change before the ACCESS_LOST recovery re-enumerates monitors.
        backend.UpdateMonitors([rotatedMonitor]);
        var session2 = backend.CreateReplacementScriptedSession(rotatedMonitor);
        session2.EnqueueDesktopFrame(
            qpcTicks: 200,
            dirtyRects: [new PixelRect(0, 0, 8, 16)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 2, 3),
            pixelBytes: new byte[8 * 16 * 4]);

        using var postChangeFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(postChangeFrame);
        Assert.True(session1.IsDisposed);
        Assert.Equal(new PixelSize(8, 16), postChangeFrame.Size);
        Assert.Equal(DisplayRotation.Rotate90, postChangeFrame.Monitor.Rotation);
        Assert.Equal(1, source.GetDiagnostics().TotalAccessLostRecoveries);

        // Disposing the pre-resize frame after the pool was retired must release cleanly without corrupting the new pool.
        preChangeFrame.Dispose();
        Assert.Equal(1, source.GetDiagnostics().ActiveLeasedFrames);
    }

    [Fact]
    public async Task DeviceRemovedAndDeviceResetTriggerAdapterResetAndSessionRecreation()
    {
        var smallMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 4, 4),
            PhysicalSize = new PixelSize(4, 4),
        };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session1 = backend.GetOrCreateScriptedSession(smallMonitor);
        session1.EnqueueStatus(DxgiHResult.DxgiErrorDeviceRemoved);

        backend.OnAdapterReset = () =>
        {
            var session2 = backend.CreateReplacementScriptedSession(smallMonitor);
            session2.EnqueueDesktopFrame(
                qpcTicks: 900,
                dirtyRects: [new PixelRect(0, 0, 4, 4)],
                moveRects: [],
                pointer: new DxgiRawPointerUpdate(false, true, 0, 0),
                pixelBytes: new byte[4 * 4 * 4]);
        };

        var options = new DxgiScreenCaptureOptions
        {
            DeviceResetRetryDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        using var recoveredFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(25));
        Assert.NotNull(recoveredFrame);
        Assert.True(session1.IsDisposed);
        Assert.Equal(1, backend.AdapterResetCount);
        Assert.Equal(1, source.GetDiagnostics().TotalDeviceResetRecoveries);
    }

    [Fact]
    public async Task ProtectedDesktopAccessDeniedTransitionsSessionStateMachineToLockedAndRecoversOnUnlock()
    {
        var smallMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 4, 4),
            PhysicalSize = new PixelSize(4, 4),
        };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session1 = backend.GetOrCreateScriptedSession(smallMonitor);
        session1.EnqueueStatus(DxgiHResult.DxgiErrorAccessDenied);

        var sessionStateMachine = new SessionStateMachine();
        var companionOptions = new CompanionOptions
        {
            ServerOrigin = new Uri("https://beacon.example.com"),
            DeviceName = "LAPTOP-01",
            RequireOwnerApprovalForFullControl = false,
        };
        _ = sessionStateMachine.BeginSession(
            new SessionApprovalRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                PermissionMode.FullControl,
                IsUnattendedAttempt: false,
                DateTimeOffset.UtcNow),
            companionOptions,
            ownerExplicitlyApproved: true);
        Assert.Equal(SessionState.ActiveFullControl, sessionStateMachine.State);

        var captureOptions = new DxgiScreenCaptureOptions
        {
            ProtectedDesktopBackoffDelay = TimeSpan.Zero,
            AccessLostRetryDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(
            backend,
            captureOptions,
            sessionStateMachine);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        // 1. Encountering DXGI_ERROR_ACCESS_DENIED (UAC / Winlogon Secure Desktop) must fail closed,
        // transition SurfaceAvailability to ProtectedOrLockedDesktop, and notify SessionStateMachine.
        var lockedFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.Null(lockedFrame);
        Assert.Equal(DesktopSurfaceAvailability.ProtectedOrLockedDesktop, source.SurfaceAvailability);
        Assert.Equal(SessionState.LockedOrUnavailable, sessionStateMachine.State);
        Assert.Equal(1, source.GetDiagnostics().TotalProtectedDesktopEvents);

        // 2. When the user exits Secure Desktop and interactive desktop duplication succeeds again,
        // SurfaceAvailability and SessionStateMachine must automatically restore to Available / ActiveFullControl.
        var session2 = backend.CreateReplacementScriptedSession(smallMonitor);
        session2.EnqueueDesktopFrame(
            qpcTicks: 1200,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 1, 1),
            pixelBytes: new byte[4 * 4 * 4]);

        using var unlockedFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(unlockedFrame);
        Assert.Equal(DesktopSurfaceAvailability.Available, source.SurfaceAvailability);
        Assert.Equal(SessionState.ActiveFullControl, sessionStateMachine.State);
    }

    [Fact]
    public async Task BoundedFramePoolPreventsUnboundedAllocationAndRecyclesSlotsOnFrameDispose()
    {
        var smallMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 4, 4),
            PhysicalSize = new PixelSize(4, 4),
        };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);

        for (var i = 0; i < 4; i++)
        {
            session.EnqueueDesktopFrame(
                qpcTicks: 100 + i,
                dirtyRects: [new PixelRect(0, 0, 4, 4)],
                moveRects: [],
                pointer: new DxgiRawPointerUpdate(false, true, 0, 0),
                pixelBytes: new byte[4 * 4 * 4]);
        }

        var options = new DxgiScreenCaptureOptions
        {
            MaxPooledFrames = 2,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        using var lease1 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        using var lease2 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        Assert.NotNull(lease1);
        Assert.NotNull(lease2);
        Assert.Equal(2, source.GetDiagnostics().ActiveLeasedFrames);
        Assert.Equal(2, source.GetDiagnostics().TotalPooledSlots);

        // 3rd acquisition while 2 slots are still held by caller must drop the frame cleanly AND still call ReleaseFrame!
        var droppedFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        Assert.Null(droppedFrame);
        Assert.Equal(1, source.GetDiagnostics().TotalPoolExhaustedDrops);
        Assert.Equal(3, session.ReleaseFrameCallCount);
        Assert.Equal(2, source.GetDiagnostics().TotalPooledSlots);

        // Disposing lease1 frees 1 slot in the bounded pool so the 4th frame can be acquired without allocating a 3rd slot.
        lease1.Dispose();
        Assert.Equal(1, source.GetDiagnostics().ActiveLeasedFrames);

        using var lease4 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        Assert.NotNull(lease4);
        Assert.Equal(2, source.GetDiagnostics().ActiveLeasedFrames);
        Assert.Equal(2, source.GetDiagnostics().TotalPooledSlots);
        Assert.Equal(4, session.ReleaseFrameCallCount);
    }

    [Fact]
    public async Task RuntimeMultiMonitorSwitchIncrementsEpochDisposesPriorSessionAndPreventsStaleMonitorFrames()
    {
        var mon1 = PrimaryMonitor with { PhysicalSize = new PixelSize(8, 8) };
        var mon2 = LeftSecondaryMonitor with { PhysicalSize = new PixelSize(12, 6) };

        var backend = new FakeDxgiAdapterBackend([mon1, mon2]);
        var session1 = backend.GetOrCreateScriptedSession(mon1);
        var session2 = backend.GetOrCreateScriptedSession(mon2);

        session1.EnqueueDesktopFrame(
            qpcTicks: 100,
            dirtyRects: [new PixelRect(0, 0, 8, 8)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 1, 1),
            pixelBytes: new byte[8 * 8 * 4]);

        session2.EnqueueDesktopFrame(
            qpcTicks: 200,
            dirtyRects: [new PixelRect(0, 0, 12, 6)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 5, 3),
            pixelBytes: new byte[12 * 6 * 4]);

        await using var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(mon1.MonitorId);
        var epoch1 = source.CaptureEpoch;

        using var frameMon1 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        Assert.NotNull(frameMon1);
        Assert.Equal(mon1.MonitorId, frameMon1.Monitor.MonitorId);
        Assert.Equal(new PixelSize(8, 8), frameMon1.Size);

        await source.StartCaptureAsync(mon2.MonitorId);
        var epoch2 = source.CaptureEpoch;
        Assert.True(epoch2 > epoch1);
        Assert.True(session1.IsDisposed);
        Assert.Equal(mon2.MonitorId, source.ActiveMonitor?.MonitorId);

        using var frameMon2 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        Assert.NotNull(frameMon2);
        Assert.Equal(mon2.MonitorId, frameMon2.Monitor.MonitorId);
        Assert.Equal(new PixelSize(12, 6), frameMon2.Size);
    }

    [Fact]
    public async Task StopAndConcurrentDisposeDrainSafelyWithoutObjectDisposedException()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);
        session.EnqueueWaitTimeout();

        var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        var timeoutResult = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(5));
        Assert.Null(timeoutResult);
        Assert.Equal(1, source.GetDiagnostics().TotalTimeouts);

        await source.StopCaptureAsync();
        Assert.False(source.IsCapturing);
        Assert.True(session.IsDisposed);

        await Task.WhenAll(
            source.DisposeAsync().AsTask(),
            source.DisposeAsync().AsTask(),
            Task.Run(source.Dispose));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.GetMonitorsAsync());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartCaptureAsync("primary"));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(5)).AsTask());
    }

    [Fact]
    public async Task LongRange64BitQpcTimestampPreservesPrecisionAndMonotonicClampingWithout32BitOverflow()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);
        var pixels = new byte[4 * 4 * 4];

        const long beyondInt32Qpc = (long)int.MaxValue + 5_000_000_000L;
        const long regressedQpc = (long)int.MaxValue - 100L;
        const long nearInt64MaxQpc = long.MaxValue - 10L;

        session.EnqueueDesktopFrame(
            qpcTicks: beyondInt32Qpc,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(false, true, 0, 0),
            pixelBytes: pixels);

        session.EnqueueDesktopFrame(
            qpcTicks: regressedQpc,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(false, true, 0, 0),
            pixelBytes: pixels);

        session.EnqueueDesktopFrame(
            qpcTicks: nearInt64MaxQpc,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(false, true, 0, 0),
            pixelBytes: pixels);

        await using var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        using var frame1 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        using var frame2 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));
        using var frame3 = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(10));

        Assert.NotNull(frame1);
        Assert.NotNull(frame2);
        Assert.NotNull(frame3);

        Assert.Equal(1, frame1.FrameSequence);
        Assert.Equal(beyondInt32Qpc, frame1.TimestampQpcTicks);

        Assert.Equal(2, frame2.FrameSequence);
        Assert.Equal(beyondInt32Qpc, frame2.TimestampQpcTicks);

        Assert.Equal(3, frame3.FrameSequence);
        Assert.Equal(nearInt64MaxQpc, frame3.TimestampQpcTicks);
        Assert.Equal(3, session.ReleaseFrameCallCount);
    }

    [Fact]
    public async Task ConcurrentFrameAcquisitionsSerializeSafelyAndInFlightExecutionDrainsBeforeDisposeCompletes()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);
        var pixels = new byte[4 * 4 * 4];

        session.EnqueueDesktopFrame(
            qpcTicks: 10_000,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 1, 1),
            pixelBytes: pixels);

        session.EnqueueDesktopFrame(
            qpcTicks: 20_000,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(true, true, 2, 2),
            pixelBytes: pixels);

        await using var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        var concurrentFrames = await Task.WhenAll(
            source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20)).AsTask(),
            source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20)).AsTask());

        using var first = concurrentFrames[0];
        using var second = concurrentFrames[1];

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.FrameSequence, second.FrameSequence);
        Assert.Equal(2, session.ReleaseFrameCallCount);
        Assert.Equal(2, source.GetDiagnostics().ActiveLeasedFrames);
    }

    [Fact]
    public async Task WindowsScreenCaptureFactoryCreatesValidSourceAndDisposesCleanly()
    {
        await using var source = WindowsScreenCaptureFactory.Create(
            new DxgiScreenCaptureOptions
            {
                MaxPooledFrames = 3,
            });

        Assert.False(source.IsCapturing);
        Assert.Null(source.ActiveMonitor);
        Assert.Equal(DesktopSurfaceAvailability.Unavailable, source.SurfaceAvailability);
    }

    [Theory]
    [InlineData(DisplayRotation.Identity)]
    [InlineData(DisplayRotation.Rotate90)]
    [InlineData(DisplayRotation.Rotate180)]
    [InlineData(DisplayRotation.Rotate270)]
    public void RotatePixelsPlacesDistinctPixelMarkersAtExactOrientedCoordinatesWithPaddedSourcePitch(
        DisplayRotation rotation)
    {
        const int srcWidth = 3;
        const int srcHeight = 2;
        const int srcRowPitch = 256; // Simulated D3D11 staging pitch padding (> 3 * 4 = 12 bytes)
        var unrotatedSize = new PixelSize(srcWidth, srcHeight);
        var orientedSize = DxgiGeometryNormalizer.GetOrientedSize(unrotatedSize, rotation);

        // Distinct BGRA pixel for each of the 6 coordinates (x, y) in 3x2 unrotated surface:
        // (0,0)=P00, (1,0)=P10, (2,0)=P20
        // (0,1)=P01, (1,1)=P11, (2,1)=P21
        uint[,] markerPixels =
        {
            { 0x11223344u, 0x22334455u, 0x33445566u },
            { 0x44556677u, 0x55667788u, 0x66778899u },
        };

        var srcBuffer = new byte[srcHeight * srcRowPitch];
        for (var y = 0; y < srcHeight; y++)
        {
            for (var x = 0; x < srcWidth; x++)
            {
                WritePixel4Bpp(srcBuffer, y * srcRowPitch + x * 4, markerPixels[y, x]);
            }
        }

        var dstRowPitch = orientedSize.Width * 4;
        var dstBuffer = new byte[orientedSize.Height * dstRowPitch];

        DxgiGeometryNormalizer.RotatePixels(
            source: srcBuffer,
            sourceWidth: srcWidth,
            sourceHeight: srcHeight,
            sourceRowPitchBytes: srcRowPitch,
            destination: dstBuffer,
            destinationRowPitchBytes: dstRowPitch,
            bytesPerPixel: 4,
            rotation: rotation);

        // Every 1x1 pixel rect rotated via TransformRectForRotation must land at the exact same (dstX, dstY) pixel!
        for (var y = 0; y < srcHeight; y++)
        {
            for (var x = 0; x < srcWidth; x++)
            {
                var transformedRect = DxgiGeometryNormalizer.TransformRectForRotation(
                    new PixelRect(x, y, 1, 1),
                    unrotatedSize,
                    rotation);
                Assert.Equal(1, transformedRect.Width);
                Assert.Equal(1, transformedRect.Height);

                var actualPixel = ReadPixel4Bpp(
                    dstBuffer,
                    transformedRect.Top * dstRowPitch + transformedRect.Left * 4);
                Assert.Equal(markerPixels[y, x], actualPixel);
            }
        }
    }

    [Theory]
    [InlineData(DisplayRotation.Rotate90)]
    [InlineData(DisplayRotation.Rotate180)]
    [InlineData(DisplayRotation.Rotate270)]
    public void RotatePixelsSupportsRgba16Float8BytesPerPixel(DisplayRotation rotation)
    {
        const int srcWidth = 2;
        const int srcHeight = 2;
        const int srcRowPitch = 64; // Padded pitch for 8-byte pixels
        var unrotatedSize = new PixelSize(srcWidth, srcHeight);
        var orientedSize = DxgiGeometryNormalizer.GetOrientedSize(unrotatedSize, rotation);

        ulong[,] markers =
        {
            { 0x0102030405060708UL, 0x1112131415161718UL },
            { 0x2122232425262728UL, 0x3132333435363738UL },
        };

        var src = new byte[srcHeight * srcRowPitch];
        for (var y = 0; y < srcHeight; y++)
        {
            for (var x = 0; x < srcWidth; x++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    src.AsSpan(y * srcRowPitch + x * 8, 8),
                    markers[y, x]);
            }
        }

        var dstRowPitch = orientedSize.Width * 8;
        var dst = new byte[orientedSize.Height * dstRowPitch];

        DxgiGeometryNormalizer.RotatePixels(
            src,
            srcWidth,
            srcHeight,
            srcRowPitch,
            dst,
            dstRowPitch,
            bytesPerPixel: 8,
            rotation);

        for (var y = 0; y < srcHeight; y++)
        {
            for (var x = 0; x < srcWidth; x++)
            {
                var mapped = DxgiGeometryNormalizer.TransformRectForRotation(
                    new PixelRect(x, y, 1, 1),
                    unrotatedSize,
                    rotation);
                var actual = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(
                    dst.AsSpan(mapped.Top * dstRowPitch + mapped.Left * 8, 8));
                Assert.Equal(markers[y, x], actual);
            }
        }
    }

    [Theory]
    [InlineData(DisplayRotation.Identity)]
    [InlineData(DisplayRotation.Rotate90)]
    [InlineData(DisplayRotation.Rotate180)]
    [InlineData(DisplayRotation.Rotate270)]
    public async Task CaptureSourceDeliversOrientedPixelsAndOrientedDirtyRectsFromUnrotatedDxgiSurface(
        DisplayRotation rotation)
    {
        var unrotatedSize = new PixelSize(4, 2);
        var orientedSize = DxgiGeometryNormalizer.GetOrientedSize(unrotatedSize, rotation);
        var rotatedMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, orientedSize.Width, orientedSize.Height),
            PhysicalSize = orientedSize,
            Rotation = rotation,
        };

        var rawUnrotatedPixels = new byte[unrotatedSize.Width * unrotatedSize.Height * 4];
        for (var y = 0; y < unrotatedSize.Height; y++)
        {
            for (var x = 0; x < unrotatedSize.Width; x++)
            {
                var color = 0xAA000000u | ((uint)(y + 1) << 16) | ((uint)(x + 1) << 8) | 0x5Au;
                WritePixel4Bpp(rawUnrotatedPixels, (y * unrotatedSize.Width + x) * 4, color);
            }
        }

        // Dirty rect in unrotated 4x2 surface space: (left=2, top=1, width=2, height=1).
        var rawDirtyRect = new PixelRect(2, 1, 2, 1);

        var backend = new FakeDxgiAdapterBackend([rotatedMonitor]);
        var session = backend.GetOrCreateScriptedSession(rotatedMonitor);
        session.EnqueueUnrotatedDesktopFrame(
            qpcTicks: 5500,
            unrotatedSize: unrotatedSize,
            unrotatedDirtyRects: [rawDirtyRect],
            pointer: new DxgiRawPointerUpdate(true, true, 1, 1),
            unrotatedPixelBytes: rawUnrotatedPixels);

        await using var source = new DxgiScreenCaptureSource(backend);
        await source.StartCaptureAsync(rotatedMonitor.MonitorId);

        using var frame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(frame);
        Assert.Equal(orientedSize, frame.Size);
        Assert.Equal(orientedSize, frame.Monitor.PhysicalSize);
        Assert.Equal(rotation, frame.Monitor.Rotation);

        var expectedDirty = DxgiGeometryNormalizer.TransformRectForRotation(rawDirtyRect, unrotatedSize, rotation);
        Assert.Single(frame.DirtyRects.ToArray());
        Assert.Equal(expectedDirty, frame.DirtyRects.Span[0]);
        Assert.True(frame.DirtyRects.Span[0].Right <= frame.Size.Width);
        Assert.True(frame.DirtyRects.Span[0].Bottom <= frame.Size.Height);

        var dstStride = orientedSize.Width * 4;
        var copied = new byte[orientedSize.Height * dstStride];
        await frame.ResourceLease.CopyToCpuBufferAsync(copied, dstStride);

        // Verify the pixel inside the dirty corner (3, 1) landed at its exact rotated position.
        var cornerMapped = DxgiGeometryNormalizer.TransformRectForRotation(
            new PixelRect(3, 1, 1, 1),
            unrotatedSize,
            rotation);
        var expectedCornerColor = ReadPixel4Bpp(rawUnrotatedPixels, (1 * unrotatedSize.Width + 3) * 4);
        var actualCornerColor = ReadPixel4Bpp(copied, cornerMapped.Top * dstStride + cornerMapped.Left * 4);
        Assert.Equal(expectedCornerColor, actualCornerColor);
    }

    [Fact]
    public async Task StartCaptureRecoversFromTransientNotCurrentlyAvailableWithinRetryBudget()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        backend.EnqueueOpenSessionFailure(
            DxgiCaptureStatus.MaxDuplicationClientsReached,
            DxgiHResult.DxgiErrorNotCurrentlyAvailable,
            "Transient DWM duplication contention during transition.");
        backend.EnqueueOpenSessionFailure(
            DxgiCaptureStatus.MaxDuplicationClientsReached,
            DxgiHResult.DxgiErrorNotCurrentlyAvailable,
            "Transient DWM duplication contention during transition.");

        var options = new DxgiScreenCaptureOptions
        {
            MaxNotCurrentlyAvailableRetries = 3,
            NotCurrentlyAvailableRetryDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        Assert.True(source.IsCapturing);
        Assert.Equal(DesktopSurfaceAvailability.Available, source.SurfaceAvailability);
        Assert.Equal(2, source.GetDiagnostics().TotalNotCurrentlyAvailableRetries);
    }

    [Fact]
    public async Task AcquireNextFrameRecoversFromTransientNotCurrentlyAvailableThenResumesFrames()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session1 = backend.GetOrCreateScriptedSession(smallMonitor);
        session1.EnqueueStatus(DxgiHResult.DxgiErrorNotCurrentlyAvailable);

        var session2 = backend.EnqueueReplacementScriptedSession(smallMonitor);
        session2.EnqueueDesktopFrame(
            qpcTicks: 2200,
            dirtyRects: [new PixelRect(0, 0, 4, 4)],
            moveRects: [],
            pointer: new DxgiRawPointerUpdate(false, true, 0, 0),
            pixelBytes: new byte[4 * 4 * 4]);

        var options = new DxgiScreenCaptureOptions
        {
            MaxNotCurrentlyAvailableRetries = 2,
            NotCurrentlyAvailableRetryDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        using var recovered = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(recovered);
        Assert.True(session1.IsDisposed);
        Assert.Equal(1, source.GetDiagnostics().TotalNotCurrentlyAvailableRetries);
        Assert.Equal(DesktopSurfaceAvailability.Available, source.SurfaceAvailability);
    }

    [Fact]
    public async Task PersistentNotCurrentlyAvailableExhaustsBoundedRetriesAndFailsClosed()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        for (var i = 0; i < 6; i++)
        {
            backend.EnqueueOpenSessionFailure(
                DxgiCaptureStatus.MaxDuplicationClientsReached,
                DxgiHResult.DxgiErrorNotCurrentlyAvailable,
                "4 duplication clients active.");
        }

        var options = new DxgiScreenCaptureOptions
        {
            MaxNotCurrentlyAvailableRetries = 2,
            NotCurrentlyAvailableRetryDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);

        var ex = await Assert.ThrowsAsync<DxgiCaptureException>(
            () => source.StartCaptureAsync(smallMonitor.MonitorId));
        Assert.Equal(DxgiCaptureStatus.MaxDuplicationClientsReached, ex.Status);
        Assert.Equal(DxgiHResult.DxgiErrorNotCurrentlyAvailable, ex.HResult);
        Assert.False(source.IsCapturing);
        Assert.Equal(DesktopSurfaceAvailability.Unavailable, source.SurfaceAvailability);
        Assert.Equal(2, source.GetDiagnostics().TotalNotCurrentlyAvailableRetries);
    }

    [Fact]
    public async Task NotCurrentlyAvailableTransitionToProtectedDesktopFailsClosedWithoutBypass()
    {
        var smallMonitor = PrimaryMonitor with { PhysicalSize = new PixelSize(4, 4) };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session1 = backend.GetOrCreateScriptedSession(smallMonitor);
        session1.EnqueueStatus(DxgiHResult.DxgiErrorNotCurrentlyAvailable);

        // Next reopen hits Winlogon / UAC Secure Desktop DXGI_ERROR_ACCESS_DENIED.
        backend.EnqueueOpenSessionFailure(
            DxgiCaptureStatus.AccessDeniedOrProtectedDesktop,
            DxgiHResult.DxgiErrorAccessDenied,
            "Secure desktop active.");

        var sessionStateMachine = new SessionStateMachine();
        var companionOptions = new CompanionOptions
        {
            ServerOrigin = new Uri("https://beacon.example.com"),
            DeviceName = "LAPTOP-01",
            RequireOwnerApprovalForFullControl = false,
        };
        _ = sessionStateMachine.BeginSession(
            new SessionApprovalRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                PermissionMode.FullControl,
                IsUnattendedAttempt: false,
                DateTimeOffset.UtcNow),
            companionOptions,
            ownerExplicitlyApproved: true);

        var options = new DxgiScreenCaptureOptions
        {
            MaxNotCurrentlyAvailableRetries = 2,
            NotCurrentlyAvailableRetryDelay = TimeSpan.Zero,
            ProtectedDesktopBackoffDelay = TimeSpan.Zero,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options, sessionStateMachine);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        var frame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.Null(frame);
        Assert.Equal(DesktopSurfaceAvailability.ProtectedOrLockedDesktop, source.SurfaceAvailability);
        Assert.Equal(SessionState.LockedOrUnavailable, sessionStateMachine.State);
        Assert.Equal(1, source.GetDiagnostics().TotalProtectedDesktopEvents);
    }

    [Fact]
    public async Task InitialIdleFrameWithZeroPresentTimestampIsPublishedEvenWhenEmitCursorOnlyFramesIsFalse()
    {
        var smallMonitor = PrimaryMonitor with
        {
            DesktopBounds = new PixelRect(0, 0, 4, 2),
            PhysicalSize = new PixelSize(4, 2),
        };
        var backend = new FakeDxgiAdapterBackend([smallMonitor]);
        var session = backend.GetOrCreateScriptedSession(smallMonitor);

        var initialPixels = new byte[4 * 2 * 4];
        for (var i = 0; i < initialPixels.Length; i++)
        {
            initialPixels[i] = (byte)(0x40 + i);
        }

        // Frame 1: unchanged idle desktop (HasDesktopImageUpdate == false, PresentTimestampQpcTicks == 0, no pointer change).
        session.EnqueueIdleFrame(initialPixels);

        // Frame 2: subsequent cursor-only move must still be suppressed when EmitCursorOnlyFrames == false.
        session.EnqueueCursorOnlyFrame(new DxgiRawPointerUpdate(true, true, 2, 1));

        var options = new DxgiScreenCaptureOptions
        {
            EmitCursorOnlyFrames = false,
        };

        await using var source = new DxgiScreenCaptureSource(backend, options);
        await source.StartCaptureAsync(smallMonitor.MonitorId);

        using var initialFrame = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.NotNull(initialFrame);
        Assert.Equal(1, initialFrame.FrameSequence);
        Assert.Single(initialFrame.DirtyRects.ToArray());
        Assert.Equal(new PixelRect(0, 0, 4, 2), initialFrame.DirtyRects.Span[0]);

        var copied = new byte[initialPixels.Length];
        await initialFrame.ResourceLease.CopyToCpuBufferAsync(copied, 4 * 4);
        Assert.Equal(initialPixels, copied);

        var cursorTick = await source.AcquireNextFrameAsync(TimeSpan.FromMilliseconds(20));
        Assert.Null(cursorTick);
        Assert.Equal(1, source.GetDiagnostics().TotalCursorOnlyUpdates);
        Assert.Equal(2, session.ReleaseFrameCallCount);
    }

    private static void WritePixel4Bpp(byte[] buffer, int offset, uint value) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);

    private static uint ReadPixel4Bpp(byte[] buffer, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));

    private sealed class FakeDxgiAdapterBackend : IDxgiAdapterBackend
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, ScriptedDuplicationSession> _sessions =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<(DxgiCaptureStatus Status, int HResult, string Detail)> _openFailures = new();
        private readonly Queue<ScriptedDuplicationSession> _queuedReplacementSessions = new();
        private IReadOnlyList<MonitorDescriptor> _monitors;
        private long _qpc = 10_000;

        public FakeDxgiAdapterBackend(IReadOnlyList<MonitorDescriptor> monitors)
        {
            _monitors = monitors;
        }

        public int AdapterResetCount { get; private set; }
        public Action? OnAdapterReset { get; set; }

        public void UpdateMonitors(IReadOnlyList<MonitorDescriptor> monitors)
        {
            lock (_gate)
            {
                _monitors = monitors;
            }
        }

        public void EnqueueOpenSessionFailure(DxgiCaptureStatus status, int hResult, string detail)
        {
            lock (_gate)
            {
                _openFailures.Enqueue((status, hResult, detail));
            }
        }

        public ScriptedDuplicationSession EnqueueReplacementScriptedSession(MonitorDescriptor monitor)
        {
            lock (_gate)
            {
                var session = new ScriptedDuplicationSession(monitor);
                _queuedReplacementSessions.Enqueue(session);
                return session;
            }
        }

        public ScriptedDuplicationSession GetOrCreateScriptedSession(MonitorDescriptor monitor)
        {
            lock (_gate)
            {
                if (!_sessions.TryGetValue(monitor.MonitorId, out var session))
                {
                    session = new ScriptedDuplicationSession(monitor);
                    _sessions[monitor.MonitorId] = session;
                }

                return session;
            }
        }

        public ScriptedDuplicationSession CreateReplacementScriptedSession(MonitorDescriptor monitor)
        {
            lock (_gate)
            {
                var session = new ScriptedDuplicationSession(monitor);
                _sessions[monitor.MonitorId] = session;
                return session;
            }
        }

        public Task<IReadOnlyList<MonitorDescriptor>> EnumerateMonitorsAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                return Task.FromResult(_monitors);
            }
        }

        public Task<IDxgiDuplicationSession> OpenDuplicationSessionAsync(
            MonitorDescriptor monitor,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_openFailures.TryDequeue(out var failure))
                {
                    throw new DxgiCaptureException(failure.Status, failure.HResult, failure.Detail);
                }

                if (_queuedReplacementSessions.TryDequeue(out var queuedReplacement))
                {
                    _sessions[monitor.MonitorId] = queuedReplacement;
                    return Task.FromResult<IDxgiDuplicationSession>(queuedReplacement);
                }

                if (_sessions.TryGetValue(monitor.MonitorId, out var scripted) && !scripted.IsDisposed)
                {
                    return Task.FromResult<IDxgiDuplicationSession>(scripted);
                }

                var fresh = new ScriptedDuplicationSession(monitor);
                _sessions[monitor.MonitorId] = fresh;
                return Task.FromResult<IDxgiDuplicationSession>(fresh);
            }
        }

        public Task ResetAdapterResourcesAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AdapterResetCount++;
            OnAdapterReset?.Invoke();
            return Task.CompletedTask;
        }

        public long GetTimestampQpcTicks() => Interlocked.Add(ref _qpc, 100);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedDuplicationSession : IDxgiDuplicationSession
    {
        private readonly Queue<(DxgiAcquireAttemptResult Result, byte[]? PixelBytes, bool ThrowOnCopy, PixelSize? UnrotatedSourceSize)> _script = new();

        public ScriptedDuplicationSession(MonitorDescriptor monitor)
        {
            Monitor = monitor;
            SurfaceSize = monitor.PhysicalSize;
            SurfaceRotation = monitor.Rotation;
            PixelFormat = FramePixelFormat.Bgra8Unorm;
        }

        public MonitorDescriptor Monitor { get; }
        public PixelSize SurfaceSize { get; }
        public FramePixelFormat PixelFormat { get; }
        public DisplayRotation SurfaceRotation { get; }
        public FrameBackingKind BackingKind => FrameBackingKind.GpuTexture;
        public bool IsFrameAcquired { get; private set; }
        public bool IsDisposed { get; private set; }
        public int AcquireCallCount { get; private set; }
        public int ReleaseFrameCallCount { get; private set; }

        private byte[]? _currentPixelBytes;
        private bool _currentThrowOnCopy;
        private PixelSize? _currentUnrotatedSourceSize;

        public void EnqueueDesktopFrame(
            long qpcTicks,
            PixelRect[] dirtyRects,
            DxgiMoveRect[] moveRects,
            DxgiRawPointerUpdate pointer,
            byte[] pixelBytes)
        {
            var data = new DxgiAcquiredFrameData(
                PresentTimestampQpcTicks: qpcTicks,
                HasDesktopImageUpdate: true,
                SurfaceSize: SurfaceSize,
                PixelFormat: PixelFormat,
                SurfaceRotation: SurfaceRotation,
                DirtyRects: dirtyRects,
                MoveRects: moveRects,
                PointerUpdate: pointer);
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), pixelBytes, false, null));
        }

        public void EnqueueUnrotatedDesktopFrame(
            long qpcTicks,
            PixelSize unrotatedSize,
            PixelRect[] unrotatedDirtyRects,
            DxgiRawPointerUpdate pointer,
            byte[] unrotatedPixelBytes)
        {
            var orientedSize = DxgiGeometryNormalizer.GetOrientedSize(unrotatedSize, SurfaceRotation);
            var data = new DxgiAcquiredFrameData(
                PresentTimestampQpcTicks: qpcTicks,
                HasDesktopImageUpdate: true,
                SurfaceSize: orientedSize,
                PixelFormat: PixelFormat,
                SurfaceRotation: SurfaceRotation,
                DirtyRects: unrotatedDirtyRects,
                MoveRects: ReadOnlyMemory<DxgiMoveRect>.Empty,
                PointerUpdate: pointer,
                RectsInUnrotatedSpace: SurfaceRotation != DisplayRotation.Identity);
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), unrotatedPixelBytes, false, unrotatedSize));
        }

        public void EnqueueIdleFrame(byte[] pixelBytes)
        {
            var data = new DxgiAcquiredFrameData(
                PresentTimestampQpcTicks: 0,
                HasDesktopImageUpdate: false,
                SurfaceSize: SurfaceSize,
                PixelFormat: PixelFormat,
                SurfaceRotation: SurfaceRotation,
                DirtyRects: ReadOnlyMemory<PixelRect>.Empty,
                MoveRects: ReadOnlyMemory<DxgiMoveRect>.Empty,
                PointerUpdate: new DxgiRawPointerUpdate(false, true, 0, 0));
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), pixelBytes, false, null));
        }

        public void EnqueueCursorOnlyFrame(DxgiRawPointerUpdate pointer)
        {
            var data = new DxgiAcquiredFrameData(
                PresentTimestampQpcTicks: 0,
                HasDesktopImageUpdate: false,
                SurfaceSize: SurfaceSize,
                PixelFormat: PixelFormat,
                SurfaceRotation: SurfaceRotation,
                DirtyRects: ReadOnlyMemory<PixelRect>.Empty,
                MoveRects: ReadOnlyMemory<DxgiMoveRect>.Empty,
                PointerUpdate: pointer);
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), null, false, null));
        }

        public void EnqueueCopyFaultingFrame()
        {
            var data = new DxgiAcquiredFrameData(
                PresentTimestampQpcTicks: 777,
                HasDesktopImageUpdate: true,
                SurfaceSize: SurfaceSize,
                PixelFormat: PixelFormat,
                SurfaceRotation: SurfaceRotation,
                DirtyRects: new[] { new PixelRect(0, 0, SurfaceSize.Width, SurfaceSize.Height) },
                MoveRects: ReadOnlyMemory<DxgiMoveRect>.Empty,
                PointerUpdate: new DxgiRawPointerUpdate(false, true, 0, 0));
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), null, true, null));
        }

        public void EnqueueWaitTimeout() =>
            _script.Enqueue((DxgiAcquireAttemptResult.WaitTimeout(), null, false, null));

        public void EnqueueStatus(int hResult) =>
            _script.Enqueue((DxgiAcquireAttemptResult.FromHResult(hResult), null, false, null));

        public DxgiAcquireAttemptResult TryAcquireNextFrame(int timeoutMilliseconds)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            AcquireCallCount++;

            if (!_script.TryDequeue(out var next))
            {
                return DxgiAcquireAttemptResult.WaitTimeout();
            }

            if (next.Result.Status == DxgiCaptureStatus.Ok)
            {
                IsFrameAcquired = true;
                _currentPixelBytes = next.PixelBytes;
                _currentThrowOnCopy = next.ThrowOnCopy;
                _currentUnrotatedSourceSize = next.UnrotatedSourceSize;
            }

            return next.Result;
        }

        public void CopyAcquiredFrameToSlot(DxgiAcquiredFrameData frameData, DxgiPooledFrameSlot slot)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (_currentThrowOnCopy)
            {
                throw new InvalidOperationException("Simulated GPU staging texture map failure.");
            }

            var dst = slot.GetOrAllocateStagingBuffer(FrameBackingKind.GpuTexture);
            if (_currentPixelBytes is null)
            {
                return;
            }

            if (_currentUnrotatedSourceSize is { } unrotatedSize)
            {
                DxgiGeometryNormalizer.RotatePixels(
                    source: _currentPixelBytes,
                    sourceWidth: unrotatedSize.Width,
                    sourceHeight: unrotatedSize.Height,
                    sourceRowPitchBytes: unrotatedSize.Width * slot.BytesPerPixel,
                    destination: dst,
                    destinationRowPitchBytes: slot.TightRowPitchBytes,
                    bytesPerPixel: slot.BytesPerPixel,
                    rotation: frameData.SurfaceRotation);
                return;
            }

            _currentPixelBytes.AsSpan(0, Math.Min(_currentPixelBytes.Length, dst.Length)).CopyTo(dst);
        }

        public void ReleaseFrame()
        {
            if (IsFrameAcquired)
            {
                IsFrameAcquired = false;
                _currentPixelBytes = null;
                _currentThrowOnCopy = false;
                _currentUnrotatedSourceSize = null;
                ReleaseFrameCallCount++;
            }
        }

        public void Dispose()
        {
            if (!IsDisposed)
            {
                ReleaseFrame();
                IsDisposed = true;
            }
        }
    }
}

