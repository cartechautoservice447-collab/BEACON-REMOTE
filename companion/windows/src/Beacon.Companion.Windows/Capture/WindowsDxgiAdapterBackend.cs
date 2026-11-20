using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Beacon.Companion.Core.Capture;

namespace Beacon.Companion.Windows.Capture;

/// <summary>
/// Windows 10 (19041+) and Windows 11 native DXGI 1.2+ Desktop Duplication and D3D11 backend.
/// Enumerates hardware display outputs via <c>IDXGIFactory1</c> and Win32 GDI/ShellScaling APIs,
/// initializes <c>IDXGIOutput1::DuplicateOutput</c> on the output's parent adapter,
/// extracts dirty/move rectangles and pointer shape buffers, and copies desktop surfaces into
/// bounded frame pool slots before <c>IDXGIOutputDuplication::ReleaseFrame</c>.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed unsafe class WindowsDxgiAdapterBackend : IDxgiAdapterBackend
{
    private int _disposed;

    public long GetTimestampQpcTicks() => NativeDxgiInterop.GetQpcTicks();

    public Task<IReadOnlyList<MonitorDescriptor>> EnumerateMonitorsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();

        nint factory = 0;
        var hr = NativeDxgiInterop.CreateDXGIFactory1(in NativeDxgiInterop.IidIdxgiFactory1, out factory);
        if (hr < 0 || factory == 0)
        {
            throw new DxgiCaptureException(
                DxgiHResultClassifier.Classify(hr),
                hr,
                $"CreateDXGIFactory1 failed with HRESULT 0x{hr:X8}.");
        }

        try
        {
            var monitors = new List<MonitorDescriptor>(4);

            for (uint adapterIndex = 0; adapterIndex < 32; adapterIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var adapterHr = NativeDxgiInterop.FactoryEnumAdapters1(factory, adapterIndex, out var adapter);
                if (adapterHr == DxgiHResult.DxgiErrorNotFound)
                {
                    break;
                }

                if (adapterHr < 0 || adapter == 0)
                {
                    continue;
                }

                try
                {
                    for (uint outputIndex = 0; outputIndex < 32; outputIndex++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var outputHr = NativeDxgiInterop.AdapterEnumOutputs(adapter, outputIndex, out var output);
                        if (outputHr == DxgiHResult.DxgiErrorNotFound)
                        {
                            break;
                        }

                        if (outputHr < 0 || output == 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (NativeDxgiInterop.OutputGetDesc(output, out var outputDesc) < 0 ||
                                outputDesc.AttachedToDesktop == 0)
                            {
                                continue;
                            }

                            var descriptor = BuildMonitorDescriptor(outputDesc, adapterIndex, outputIndex);
                            if (!descriptor.PhysicalSize.IsEmpty)
                            {
                                monitors.Add(descriptor);
                            }
                        }
                        finally
                        {
                            _ = NativeDxgiInterop.ReleaseComObject(ref output);
                        }
                    }
                }
                finally
                {
                    _ = NativeDxgiInterop.ReleaseComObject(ref adapter);
                }
            }

            return Task.FromResult(DxgiGeometryNormalizer.OrderMonitorsDeterministically(monitors));
        }
        finally
        {
            _ = NativeDxgiInterop.ReleaseComObject(ref factory);
        }
    }

    public Task<IDxgiDuplicationSession> OpenDuplicationSessionAsync(
        MonitorDescriptor monitor,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(monitor);
        ct.ThrowIfCancellationRequested();

        nint factory = 0;
        var hr = NativeDxgiInterop.CreateDXGIFactory1(in NativeDxgiInterop.IidIdxgiFactory1, out factory);
        if (hr < 0 || factory == 0)
        {
            throw new DxgiCaptureException(
                DxgiHResultClassifier.Classify(hr),
                hr,
                $"CreateDXGIFactory1 failed with HRESULT 0x{hr:X8}.");
        }

        try
        {
            for (uint adapterIndex = 0; adapterIndex < 32; adapterIndex++)
            {
                ct.ThrowIfCancellationRequested();

                var adapterHr = NativeDxgiInterop.FactoryEnumAdapters1(factory, adapterIndex, out var adapter);
                if (adapterHr == DxgiHResult.DxgiErrorNotFound)
                {
                    break;
                }

                if (adapterHr < 0 || adapter == 0)
                {
                    continue;
                }

                try
                {
                    for (uint outputIndex = 0; outputIndex < 32; outputIndex++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var outputHr = NativeDxgiInterop.AdapterEnumOutputs(adapter, outputIndex, out var output);
                        if (outputHr == DxgiHResult.DxgiErrorNotFound)
                        {
                            break;
                        }

                        if (outputHr < 0 || output == 0)
                        {
                            continue;
                        }

                        try
                        {
                            if (NativeDxgiInterop.OutputGetDesc(output, out var outputDesc) < 0 ||
                                outputDesc.AttachedToDesktop == 0)
                            {
                                continue;
                            }

                            string deviceName;
                            fixed (char* pDeviceName = outputDesc.DeviceName)
                            {
                                deviceName = ReadWideString(pDeviceName, 32);
                            }

                            if (!MatchesMonitor(monitor, deviceName))
                            {
                                continue;
                            }

                            var session = CreateSessionForAdapterOutput(monitor, adapter, output);
                            return Task.FromResult<IDxgiDuplicationSession>(session);
                        }
                        finally
                        {
                            _ = NativeDxgiInterop.ReleaseComObject(ref output);
                        }
                    }
                }
                finally
                {
                    _ = NativeDxgiInterop.ReleaseComObject(ref adapter);
                }
            }

            throw new DxgiCaptureException(
                DxgiCaptureStatus.MonitorNotFound,
                DxgiHResult.DxgiErrorNotFound,
                $"Target monitor '{monitor.MonitorId}' ('{monitor.DeviceName}') is no longer attached to the desktop.");
        }
        finally
        {
            _ = NativeDxgiInterop.ReleaseComObject(ref factory);
        }
    }

    public Task ResetAdapterResourcesAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private static WindowsDxgiDuplicationSession CreateSessionForAdapterOutput(
        MonitorDescriptor monitor,
        nint adapter,
        nint output)
    {
        nint output1 = 0;
        nint d3d11Device = 0;
        nint immediateContext = 0;
        nint duplication = 0;

        try
        {
            var qiHr = NativeDxgiInterop.QueryInterface(output, in NativeDxgiInterop.IidIdxgiOutput1, out output1);
            if (qiHr < 0 || output1 == 0)
            {
                throw new DxgiCaptureException(
                    DxgiHResultClassifier.Classify(qiHr),
                    qiHr,
                    $"QueryInterface(IDXGIOutput1) failed with HRESULT 0x{qiHr:X8}.");
            }

            uint* featureLevels = stackalloc uint[4]
            {
                0xb100, // D3D_FEATURE_LEVEL_11_1
                0xb000, // D3D_FEATURE_LEVEL_11_0
                0xa100, // D3D_FEATURE_LEVEL_10_1
                0xa000, // D3D_FEATURE_LEVEL_10_0
            };

            var d3dHr = NativeDxgiInterop.D3D11CreateDevice(
                pAdapter: adapter,
                driverType: NativeDxgiInterop.D3dDriverTypeUnknown,
                software: 0,
                flags: NativeDxgiInterop.D3d11CreateDeviceBgraSupport,
                pFeatureLevels: featureLevels,
                featureLevels: 4,
                sdkVersion: NativeDxgiInterop.D3d11SdkVersion,
                ppDevice: out d3d11Device,
                pFeatureLevel: out _,
                ppImmediateContext: out immediateContext);

            if (d3dHr < 0 || d3d11Device == 0 || immediateContext == 0)
            {
                throw new DxgiCaptureException(
                    DxgiHResultClassifier.Classify(d3dHr),
                    d3dHr,
                    $"D3D11CreateDevice failed for output '{monitor.DeviceName}' with HRESULT 0x{d3dHr:X8}.");
            }

            var dupHr = NativeDxgiInterop.Output1DuplicateOutput(output1, d3d11Device, out duplication);
            if (dupHr < 0 || duplication == 0)
            {
                throw new DxgiCaptureException(
                    DxgiHResultClassifier.Classify(dupHr),
                    dupHr,
                    $"IDXGIOutput1::DuplicateOutput failed for '{monitor.DeviceName}' with HRESULT 0x{dupHr:X8}.");
            }

            NativeDxgiInterop.DuplicationGetDesc(duplication, out var dupDesc);
            var surfaceWidth = dupDesc.ModeDesc.Width > 0
                ? (int)dupDesc.ModeDesc.Width
                : monitor.PhysicalSize.Width;
            var surfaceHeight = dupDesc.ModeDesc.Height > 0
                ? (int)dupDesc.ModeDesc.Height
                : monitor.PhysicalSize.Height;
            var surfaceSize = new PixelSize(Math.Max(1, surfaceWidth), Math.Max(1, surfaceHeight));
            var pixelFormat = NativeDxgiInterop.MapDxgiFormat(dupDesc.ModeDesc.Format);
            var rotation = NativeDxgiInterop.MapDxgiRotation(dupDesc.Rotation);

            var session = new WindowsDxgiDuplicationSession(
                monitor,
                surfaceSize,
                pixelFormat,
                rotation,
                d3d11Device,
                immediateContext,
                duplication);

            // Ownership transferred to session.
            d3d11Device = 0;
            immediateContext = 0;
            duplication = 0;
            return session;
        }
        finally
        {
            _ = NativeDxgiInterop.ReleaseComObject(ref duplication);
            _ = NativeDxgiInterop.ReleaseComObject(ref immediateContext);
            _ = NativeDxgiInterop.ReleaseComObject(ref d3d11Device);
            _ = NativeDxgiInterop.ReleaseComObject(ref output1);
        }
    }

    private static bool MatchesMonitor(MonitorDescriptor monitor, string outputDeviceName) =>
        string.Equals(monitor.MonitorId, outputDeviceName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(monitor.DeviceName, outputDeviceName, StringComparison.OrdinalIgnoreCase);

    private static MonitorDescriptor BuildMonitorDescriptor(
        NativeDxgiInterop.DxgiOutputDesc outputDesc,
        uint adapterIndex,
        uint outputIndex)
    {
        string deviceName;
        fixed (char* pDeviceName = outputDesc.DeviceName)
        {
            deviceName = ReadWideString(pDeviceName, 32);
        }

        if (string.IsNullOrWhiteSpace(deviceName))
        {
            deviceName = $"\\\\.\\DISPLAY{adapterIndex * 10 + outputIndex + 1}";
        }

        var desktopBounds = outputDesc.DesktopCoordinates.ToPixelRect();
        var rotation = NativeDxgiInterop.MapDxgiRotation(outputDesc.Rotation);
        var isPrimary = desktopBounds.Left == 0 && desktopBounds.Top == 0;

        if (outputDesc.MonitorHandle != 0)
        {
            NativeDxgiInterop.MonitorInfoExW monitorInfo = default;
            monitorInfo.CbSize = (uint)sizeof(NativeDxgiInterop.MonitorInfoExW);
            if (NativeDxgiInterop.GetMonitorInfoW(outputDesc.MonitorHandle, ref monitorInfo))
            {
                isPrimary = (monitorInfo.DwFlags & NativeDxgiInterop.MonitorInfoFPrimary) != 0;
                var rc = monitorInfo.RcMonitor.ToPixelRect();
                if (!rc.IsEmpty)
                {
                    desktopBounds = rc;
                }
            }
        }

        var physicalWidth = desktopBounds.Width;
        var physicalHeight = desktopBounds.Height;
        var refreshRateHz = 60;

        NativeDxgiInterop.DevModeW devMode = default;
        devMode.DmSize = (ushort)sizeof(NativeDxgiInterop.DevModeW);
        if (NativeDxgiInterop.EnumDisplaySettingsExW(
                deviceName,
                NativeDxgiInterop.EnumCurrentSettings,
                ref devMode,
                0))
        {
            if (devMode.DmPelsWidth > 0 && devMode.DmPelsHeight > 0)
            {
                physicalWidth = (int)devMode.DmPelsWidth;
                physicalHeight = (int)devMode.DmPelsHeight;
            }

            if (devMode.DmDisplayFrequency > 1)
            {
                refreshRateHz = (int)devMode.DmDisplayFrequency;
            }
        }

        var scaleFactor = 1.0;
        if (outputDesc.MonitorHandle != 0 &&
            NativeDxgiInterop.GetDpiForMonitor(
                outputDesc.MonitorHandle,
                NativeDxgiInterop.MdtEffectiveDpi,
                out var dpiX,
                out _) == 0 &&
            dpiX > 0)
        {
            scaleFactor = Math.Round(dpiX / 96.0, 2);
        }
        else if (desktopBounds.Width > 0 && physicalWidth > 0)
        {
            scaleFactor = Math.Round((double)physicalWidth / desktopBounds.Width, 2);
        }

        var friendlyName = QueryFriendlyDisplayName(deviceName, adapterIndex, outputIndex);

        return new MonitorDescriptor(
            MonitorId: deviceName,
            DeviceName: deviceName,
            FriendlyName: friendlyName,
            IsPrimary: isPrimary,
            DesktopBounds: desktopBounds,
            PhysicalSize: new PixelSize(Math.Max(0, physicalWidth), Math.Max(0, physicalHeight)),
            Rotation: rotation,
            ScaleFactor: scaleFactor <= 0 ? 1.0 : scaleFactor,
            RefreshRateHz: refreshRateHz);
    }

    private static string QueryFriendlyDisplayName(string deviceName, uint adapterIndex, uint outputIndex)
    {
        NativeDxgiInterop.DisplayDeviceW monitorDevice = default;
        monitorDevice.Cb = (uint)sizeof(NativeDxgiInterop.DisplayDeviceW);

        if (NativeDxgiInterop.EnumDisplayDevicesW(deviceName, 0, ref monitorDevice, 0))
        {
            string monitorString;
            fixed (char* pMonitor = monitorDevice.DeviceString)
            {
                monitorString = ReadWideString(pMonitor, 128);
            }

            if (!string.IsNullOrWhiteSpace(monitorString))
            {
                return monitorString.Trim();
            }
        }

        NativeDxgiInterop.DisplayDeviceW adapterDevice = default;
        adapterDevice.Cb = (uint)sizeof(NativeDxgiInterop.DisplayDeviceW);
        if (NativeDxgiInterop.EnumDisplayDevicesW(null, outputIndex, ref adapterDevice, 0))
        {
            string adapterString;
            fixed (char* pAdapter = adapterDevice.DeviceString)
            {
                adapterString = ReadWideString(pAdapter, 128);
            }

            if (!string.IsNullOrWhiteSpace(adapterString))
            {
                return adapterString.Trim();
            }
        }

        return $"Display {adapterIndex + 1}:{outputIndex + 1} ({deviceName})";
    }

    private static string ReadWideString(char* buffer, int maxChars)
    {
        var len = 0;
        while (len < maxChars && buffer[len] != '\0')
        {
            len++;
        }

        return len == 0 ? string.Empty : new string(buffer, 0, len);
    }

    private sealed class WindowsDxgiDuplicationSession : IDxgiDuplicationSession
    {
        private nint _d3d11Device;
        private nint _immediateContext;
        private nint _duplication;
        private nint _stagingTexture;
        private uint _stagingWidth;
        private uint _stagingHeight;
        private uint _stagingFormat;
        private nint _acquiredResource;
        private int _isFrameAcquired;
        private int _disposed;

        private byte[] _dirtyMetadataScratch = new byte[256];
        private byte[] _moveMetadataScratch = new byte[256];
        private byte[] _pointerShapeScratch = new byte[4096];

        public WindowsDxgiDuplicationSession(
            MonitorDescriptor monitor,
            PixelSize surfaceSize,
            FramePixelFormat pixelFormat,
            DisplayRotation surfaceRotation,
            nint d3d11Device,
            nint immediateContext,
            nint duplication)
        {
            Monitor = monitor;
            SurfaceSize = surfaceSize;
            PixelFormat = pixelFormat;
            SurfaceRotation = surfaceRotation;
            _d3d11Device = d3d11Device;
            _immediateContext = immediateContext;
            _duplication = duplication;
        }

        public MonitorDescriptor Monitor { get; }
        public PixelSize SurfaceSize { get; private set; }
        public FramePixelFormat PixelFormat { get; private set; }
        public DisplayRotation SurfaceRotation { get; }
        public FrameBackingKind BackingKind => FrameBackingKind.GpuTexture;
        public bool IsFrameAcquired => Volatile.Read(ref _isFrameAcquired) != 0;

        public DxgiAcquireAttemptResult TryAcquireNextFrame(int timeoutMilliseconds)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (IsFrameAcquired)
            {
                ReleaseFrame();
            }

            var clampedTimeout = (uint)Math.Max(0, timeoutMilliseconds);
            var hr = NativeDxgiInterop.DuplicationAcquireNextFrame(
                _duplication,
                clampedTimeout,
                out var frameInfo,
                out var desktopResource);

            if (hr == DxgiHResult.DxgiErrorWaitTimeout)
            {
                return DxgiAcquireAttemptResult.WaitTimeout();
            }

            if (hr < 0)
            {
                _ = NativeDxgiInterop.ReleaseComObject(ref desktopResource);
                return DxgiAcquireAttemptResult.FromHResult(
                    hr,
                    $"IDXGIOutputDuplication::AcquireNextFrame returned 0x{hr:X8}.");
            }

            _acquiredResource = desktopResource;
            Volatile.Write(ref _isFrameAcquired, 1);

            var hasDesktopUpdate = frameInfo.LastPresentTime > 0 || frameInfo.TotalMetadataBufferSize > 0;
            var dirtyRects = ReadOnlyMemory<PixelRect>.Empty;
            var moveRects = ReadOnlyMemory<DxgiMoveRect>.Empty;

            if (frameInfo.TotalMetadataBufferSize > 0)
            {
                dirtyRects = ReadDirtyRects(frameInfo.TotalMetadataBufferSize);
                moveRects = ReadMoveRects(frameInfo.TotalMetadataBufferSize);
            }

            var pointerUpdate = ReadPointerUpdate(in frameInfo);

            var frameData = new DxgiAcquiredFrameData(
                PresentTimestampQpcTicks: frameInfo.LastPresentTime > 0
                    ? frameInfo.LastPresentTime
                    : NativeDxgiInterop.GetQpcTicks(),
                HasDesktopImageUpdate: hasDesktopUpdate,
                SurfaceSize: SurfaceSize,
                PixelFormat: PixelFormat,
                SurfaceRotation: SurfaceRotation,
                DirtyRects: dirtyRects,
                MoveRects: moveRects,
                PointerUpdate: pointerUpdate,
                CopyPixelsDelegate: null,
                BackingKind: FrameBackingKind.GpuTexture);

            return DxgiAcquireAttemptResult.Success(frameData);
        }

        public void CopyAcquiredFrameToSlot(DxgiAcquiredFrameData frameData, DxgiPooledFrameSlot slot)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ArgumentNullException.ThrowIfNull(frameData);
            ArgumentNullException.ThrowIfNull(slot);

            if (_acquiredResource == 0)
            {
                return;
            }

            nint desktopTexture = 0;
            var qiHr = NativeDxgiInterop.QueryInterface(
                _acquiredResource,
                in NativeDxgiInterop.IidId3d11Texture2D,
                out desktopTexture);
            if (qiHr < 0 || desktopTexture == 0)
            {
                throw new DxgiCaptureException(
                    DxgiHResultClassifier.Classify(qiHr),
                    qiHr,
                    $"QueryInterface(ID3D11Texture2D) on desktop resource failed with 0x{qiHr:X8}.");
            }

            try
            {
                NativeDxgiInterop.Texture2DGetDesc(desktopTexture, out var texDesc);
                EnsureStagingTexture(in texDesc);

                NativeDxgiInterop.ContextCopyResource(_immediateContext, _stagingTexture, desktopTexture);

                var mapHr = NativeDxgiInterop.ContextMap(
                    _immediateContext,
                    _stagingTexture,
                    0,
                    NativeDxgiInterop.D3d11MapRead,
                    0,
                    out var mapped);
                if (mapHr < 0 || mapped.PData == 0)
                {
                    throw new DxgiCaptureException(
                        DxgiHResultClassifier.Classify(mapHr),
                        mapHr,
                        $"ID3D11DeviceContext::Map failed on staging texture with 0x{mapHr:X8}.");
                }

                try
                {
                    var dstSpan = slot.GetOrAllocateStagingBuffer(FrameBackingKind.GpuTexture);
                    var rowBytes = slot.TightRowPitchBytes;
                    var rows = Math.Min(slot.Size.Height, (int)texDesc.Height);
                    var srcBase = (byte*)mapped.PData;
                    var srcRowPitch = (int)mapped.RowPitch;

                    for (var y = 0; y < rows; y++)
                    {
                        var srcRow = new ReadOnlySpan<byte>(srcBase + (nint)y * srcRowPitch, rowBytes);
                        var dstRow = dstSpan.Slice(y * rowBytes, rowBytes);
                        srcRow.CopyTo(dstRow);
                    }
                }
                finally
                {
                    NativeDxgiInterop.ContextUnmap(_immediateContext, _stagingTexture, 0);
                }
            }
            finally
            {
                _ = NativeDxgiInterop.ReleaseComObject(ref desktopTexture);
            }
        }

        public void ReleaseFrame()
        {
            _ = NativeDxgiInterop.ReleaseComObject(ref _acquiredResource);

            if (Interlocked.Exchange(ref _isFrameAcquired, 0) != 0 && _duplication != 0)
            {
                _ = NativeDxgiInterop.DuplicationReleaseFrame(_duplication);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            ReleaseFrame();
            _ = NativeDxgiInterop.ReleaseComObject(ref _stagingTexture);
            _ = NativeDxgiInterop.ReleaseComObject(ref _duplication);
            _ = NativeDxgiInterop.ReleaseComObject(ref _immediateContext);
            _ = NativeDxgiInterop.ReleaseComObject(ref _d3d11Device);
        }

        private void EnsureStagingTexture(in NativeDxgiInterop.D3d11Texture2dDesc srcDesc)
        {
            if (_stagingTexture != 0 &&
                _stagingWidth == srcDesc.Width &&
                _stagingHeight == srcDesc.Height &&
                _stagingFormat == srcDesc.Format)
            {
                return;
            }

            _ = NativeDxgiInterop.ReleaseComObject(ref _stagingTexture);

            var stagingDesc = new NativeDxgiInterop.D3d11Texture2dDesc
            {
                Width = srcDesc.Width,
                Height = srcDesc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = srcDesc.Format,
                SampleDesc = new NativeDxgiInterop.DxgiSampleDesc { Count = 1, Quality = 0 },
                Usage = NativeDxgiInterop.D3d11UsageStaging,
                BindFlags = 0,
                CpuAccessFlags = NativeDxgiInterop.D3d11CpuAccessRead,
                MiscFlags = 0,
            };

            var hr = NativeDxgiInterop.DeviceCreateTexture2D(_d3d11Device, in stagingDesc, out _stagingTexture);
            if (hr < 0 || _stagingTexture == 0)
            {
                throw new DxgiCaptureException(
                    DxgiHResultClassifier.Classify(hr),
                    hr,
                    $"CreateTexture2D (staging {srcDesc.Width}x{srcDesc.Height}) failed with 0x{hr:X8}.");
            }

            _stagingWidth = srcDesc.Width;
            _stagingHeight = srcDesc.Height;
            _stagingFormat = srcDesc.Format;
            SurfaceSize = new PixelSize((int)srcDesc.Width, (int)srcDesc.Height);
            PixelFormat = NativeDxgiInterop.MapDxgiFormat(srcDesc.Format);
        }

        private ReadOnlyMemory<PixelRect> ReadDirtyRects(uint metadataBytesHint)
        {
            if (_dirtyMetadataScratch.Length < metadataBytesHint)
            {
                _dirtyMetadataScratch = new byte[metadataBytesHint];
            }

            fixed (byte* pScratch = _dirtyMetadataScratch)
            {
                var hr = NativeDxgiInterop.DuplicationGetFrameDirtyRects(
                    _duplication,
                    (uint)_dirtyMetadataScratch.Length,
                    (NativeDxgiInterop.RawRect*)pScratch,
                    out var requiredBytes);

                if (hr < 0 || requiredBytes == 0)
                {
                    return ReadOnlyMemory<PixelRect>.Empty;
                }

                var count = (int)(requiredBytes / (uint)sizeof(NativeDxgiInterop.RawRect));
                if (count <= 0)
                {
                    return ReadOnlyMemory<PixelRect>.Empty;
                }

                var rawSpan = new ReadOnlySpan<NativeDxgiInterop.RawRect>(pScratch, count);
                var rects = new PixelRect[count];
                for (var i = 0; i < count; i++)
                {
                    rects[i] = rawSpan[i].ToPixelRect();
                }

                return rects;
            }
        }

        private ReadOnlyMemory<DxgiMoveRect> ReadMoveRects(uint metadataBytesHint)
        {
            if (_moveMetadataScratch.Length < metadataBytesHint)
            {
                _moveMetadataScratch = new byte[metadataBytesHint];
            }

            fixed (byte* pScratch = _moveMetadataScratch)
            {
                var hr = NativeDxgiInterop.DuplicationGetFrameMoveRects(
                    _duplication,
                    (uint)_moveMetadataScratch.Length,
                    (NativeDxgiInterop.DxgiOutduplMoveRectNative*)pScratch,
                    out var requiredBytes);

                if (hr < 0 || requiredBytes == 0)
                {
                    return ReadOnlyMemory<DxgiMoveRect>.Empty;
                }

                var count = (int)(requiredBytes / (uint)sizeof(NativeDxgiInterop.DxgiOutduplMoveRectNative));
                if (count <= 0)
                {
                    return ReadOnlyMemory<DxgiMoveRect>.Empty;
                }

                var rawSpan = new ReadOnlySpan<NativeDxgiInterop.DxgiOutduplMoveRectNative>(pScratch, count);
                var moves = new DxgiMoveRect[count];
                for (var i = 0; i < count; i++)
                {
                    moves[i] = new DxgiMoveRect(
                        rawSpan[i].SourcePoint.X,
                        rawSpan[i].SourcePoint.Y,
                        rawSpan[i].DestinationRect.ToPixelRect());
                }

                return moves;
            }
        }

        private DxgiRawPointerUpdate ReadPointerUpdate(in NativeDxgiInterop.DxgiOutduplFrameInfo frameInfo)
        {
            var positionUpdated = frameInfo.LastMouseUpdateTime > 0;
            var isVisible = frameInfo.PointerPosition.Visible != 0;
            var posX = frameInfo.PointerPosition.Position.X;
            var posY = frameInfo.PointerPosition.Position.Y;

            if (frameInfo.PointerShapeBufferSize == 0)
            {
                return new DxgiRawPointerUpdate(
                    PositionUpdated: positionUpdated,
                    IsVisible: isVisible,
                    X: posX,
                    Y: posY);
            }

            if (_pointerShapeScratch.Length < frameInfo.PointerShapeBufferSize)
            {
                _pointerShapeScratch = new byte[frameInfo.PointerShapeBufferSize];
            }

            fixed (byte* pShape = _pointerShapeScratch)
            {
                var hr = NativeDxgiInterop.DuplicationGetFramePointerShape(
                    _duplication,
                    (uint)_pointerShapeScratch.Length,
                    pShape,
                    out var requiredBytes,
                    out var shapeInfo);

                if (hr < 0 || requiredBytes == 0 || shapeInfo.Width == 0 || shapeInfo.Height == 0)
                {
                    return new DxgiRawPointerUpdate(
                        PositionUpdated: positionUpdated,
                        IsVisible: isVisible,
                        X: posX,
                        Y: posY);
                }

                var shapeKind = shapeInfo.Type switch
                {
                    1 => PointerShapeKind.Monochrome,
                    4 => PointerShapeKind.MaskedColor,
                    _ => PointerShapeKind.Color,
                };

                var shapeBytes = new byte[requiredBytes];
                _pointerShapeScratch.AsSpan(0, (int)requiredBytes).CopyTo(shapeBytes);

                return new DxgiRawPointerUpdate(
                    PositionUpdated: true,
                    IsVisible: isVisible,
                    X: posX,
                    Y: posY,
                    NewShapeKind: shapeKind,
                    NewShapeSize: new PixelSize((int)shapeInfo.Width, (int)shapeInfo.Height),
                    NewShapePitchBytes: (int)shapeInfo.Pitch,
                    HotSpotX: shapeInfo.HotSpot.X,
                    HotSpotY: shapeInfo.HotSpot.Y,
                    NewShapeBytes: shapeBytes);
            }
        }
    }
}
