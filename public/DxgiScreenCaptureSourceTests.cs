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

    private sealed class FakeDxgiAdapterBackend : IDxgiAdapterBackend
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, ScriptedDuplicationSession> _sessions =
            new(StringComparer.OrdinalIgnoreCase);
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
        private readonly Queue<(DxgiAcquireAttemptResult Result, byte[]? PixelBytes, bool ThrowOnCopy)> _script = new();

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
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), pixelBytes, false));
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
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), null, false));
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
            _script.Enqueue((DxgiAcquireAttemptResult.Success(data), null, true));
        }

        public void EnqueueWaitTimeout() =>
            _script.Enqueue((DxgiAcquireAttemptResult.WaitTimeout(), null, false));

        public void EnqueueStatus(int hResult) =>
            _script.Enqueue((DxgiAcquireAttemptResult.FromHResult(hResult), null, false));

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
            if (_currentPixelBytes is not null)
            {
                _currentPixelBytes.AsSpan(0, Math.Min(_currentPixelBytes.Length, dst.Length)).CopyTo(dst);
            }
        }

        public void ReleaseFrame()
        {
            if (IsFrameAcquired)
            {
                IsFrameAcquired = false;
                _currentPixelBytes = null;
                _currentThrowOnCopy = false;
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
