#pragma warning disable IDE0079
#pragma warning disable SYSLIB1054, CA1401, CA2101, CA5392

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Beacon.Companion.Core.Capture;

namespace Beacon.Companion.Windows.Capture;

/// <summary>
/// Low-level Win32, DXGI 1.2, and Direct3D 11 interop structures and COM vtable bindings
/// for Desktop Duplication and display topology enumeration.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static unsafe class NativeDxgiInterop
{
    public const uint DxgiFormatB8G8R8A8Unorm = 87;
    public const uint DxgiFormatB8G8R8A8UnormSrgb = 91;
    public const uint DxgiFormatR16G16B16A16Float = 10;

    public const uint D3dDriverTypeUnknown = 0;
    public const uint D3d11CreateDeviceBgraSupport = 0x20;
    public const uint D3d11SdkVersion = 7;

    public const uint D3d11UsageStaging = 3;
    public const uint D3d11CpuAccessRead = 0x20000;
    public const uint D3d11MapRead = 1;

    public const int EnumCurrentSettings = -1;
    public const uint MonitorInfoFPrimary = 1;
    public const uint MonitorDefaultToNearest = 2;
    public const int MdtEffectiveDpi = 0;

    public const uint DesktopReadObjects = 0x0001;
    public static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);
    public const uint SrcCopy = 0x00CC0020;
    public const uint CaptureBlt = 0x40000000;
    public const uint DibRgbColors = 0;
    public const uint BiRgb = 0;

    public static readonly Guid IidIdxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    public static readonly Guid IidIdxgiOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
    public static readonly Guid IidId3d11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [StructLayout(LayoutKind.Sequential)]
    public struct RawRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public PixelRect ToPixelRect() =>
            new(
                Left,
                Top,
                Math.Max(0, Right - Left),
                Math.Max(0, Bottom - Top));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RawPoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiModeDesc
    {
        public uint Width;
        public uint Height;
        public DxgiRational RefreshRate;
        public uint Format;
        public uint ScanlineOrdering;
        public uint Scaling;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutputDesc
    {
        public fixed char DeviceName[32];
        public RawRect DesktopCoordinates;
        public int AttachedToDesktop;
        public uint Rotation;
        public nint MonitorHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutduplDesc
    {
        public DxgiModeDesc ModeDesc;
        public uint Rotation;
        public int DesktopImageInSystemMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutduplPointerPosition
    {
        public RawPoint Position;
        public int Visible;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutduplFrameInfo
    {
        public long LastPresentTime;
        public long LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectsCoalesced;
        public int ProtectedContentMaskedOut;
        public DxgiOutduplPointerPosition PointerPosition;
        public uint TotalMetadataBufferSize;
        public uint PointerShapeBufferSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutduplMoveRectNative
    {
        public RawPoint SourcePoint;
        public RawRect DestinationRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiOutduplPointerShapeInfo
    {
        public uint Type;
        public uint Width;
        public uint Height;
        public uint Pitch;
        public RawPoint HotSpot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DxgiSampleDesc
    {
        public uint Count;
        public uint Quality;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3d11Texture2dDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public uint Format;
        public DxgiSampleDesc SampleDesc;
        public uint Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3d11MappedSubresource
    {
        public nint PData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoExW
    {
        public uint CbSize;
        public RawRect RcMonitor;
        public RawRect RcWork;
        public uint DwFlags;
        public fixed char SzDevice[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayDeviceW
    {
        public uint Cb;
        public fixed char DeviceName[32];
        public fixed char DeviceString[128];
        public uint StateFlags;
        public fixed char DeviceId[128];
        public fixed char DeviceKey[128];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevModeW
    {
        public fixed char DmDeviceName[32];
        public ushort DmSpecVersion;
        public ushort DmDriverVersion;
        public ushort DmSize;
        public ushort DmDriverExtra;
        public uint DmFields;
        public int DmPositionX;
        public int DmPositionY;
        public uint DmDisplayOrientation;
        public uint DmDisplayFixedOutput;
        public short DmColor;
        public short DmDuplex;
        public short DmYResolution;
        public short DmTTOption;
        public short DmCollate;
        public fixed char DmFormName[32];
        public ushort DmLogPixels;
        public uint DmBitsPerPel;
        public uint DmPelsWidth;
        public uint DmPelsHeight;
        public uint DmDisplayFlags;
        public uint DmDisplayFrequency;
        public uint DmICMMethod;
        public uint DmICMIntent;
        public uint DmMediaType;
        public uint DmDitherType;
        public uint DmReserved1;
        public uint DmReserved2;
        public uint DmPanningWidth;
        public uint DmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfo
    {
        public BitmapInfoHeader BmiHeader;
        public uint BmiColor0;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    public static extern int CreateDXGIFactory1(in Guid riid, out nint ppFactory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11CreateDevice(
        nint pAdapter,
        uint driverType,
        nint software,
        uint flags,
        uint* pFeatureLevels,
        uint featureLevels,
        uint sdkVersion,
        out nint ppDevice,
        out uint pFeatureLevel,
        out nint ppImmediateContext);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfoExW lpmi);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayDevicesW(
        string? lpDevice,
        uint iDevNum,
        ref DisplayDeviceW lpDisplayDevice,
        uint dwFlags);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplaySettingsExW(
        string lpszDeviceName,
        int iModeNum,
        ref DevModeW lpDevMode,
        uint dwFlags);

    [DllImport("shcore.dll", ExactSpelling = true)]
    public static extern int GetDpiForMonitor(
        nint hmonitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern nint OpenInputDesktop(
        uint dwFlags,
        [MarshalAs(UnmanagedType.Bool)] bool fInherit,
        uint dwDesiredAccess);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(nint hDesktop);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern int ReleaseDC(nint hWnd, nint hDc);

    [DllImport("user32.dll", ExactSpelling = true)]
    public static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern nint CreateDIBSection(
        nint hdc,
        in BitmapInfo pbmi,
        uint usage,
        out nint ppvBits,
        nint hSection,
        uint offset);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BitBlt(
        nint hdc,
        int x,
        int y,
        int cx,
        int cy,
        nint hdcSrc,
        int x1,
        int y1,
        uint rop);

    public static long GetQpcTicks() => Stopwatch.GetTimestamp();

    public static int QueryInterface(nint comObj, in Guid iid, out nint queried)
    {
        queried = 0;
        if (comObj == 0)
        {
            return DxgiHResult.DxgiErrorInvalidCall;
        }

        var vtbl = *(nint**)comObj;
        var fn = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[0];
        var localGuid = iid;
        nint outPtr = 0;
        var hr = fn(comObj, &localGuid, &outPtr);
        queried = outPtr;
        return hr;
    }

    public static uint ReleaseComObject(ref nint comObj)
    {
        var current = Interlocked.Exchange(ref comObj, 0);
        if (current == 0)
        {
            return 0;
        }

        var vtbl = *(nint**)current;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2];
        return fn(current);
    }

    public static int FactoryEnumAdapters1(nint factory1, uint index, out nint adapter1)
    {
        var vtbl = *(nint**)factory1;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtbl[12];
        nint outPtr = 0;
        var hr = fn(factory1, index, &outPtr);
        adapter1 = outPtr;
        return hr;
    }

    public static int AdapterEnumOutputs(nint adapter1, uint index, out nint output)
    {
        var vtbl = *(nint**)adapter1;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)vtbl[7];
        nint outPtr = 0;
        var hr = fn(adapter1, index, &outPtr);
        output = outPtr;
        return hr;
    }

    public static int OutputGetDesc(nint output, out DxgiOutputDesc desc)
    {
        var vtbl = *(nint**)output;
        var fn = (delegate* unmanaged[Stdcall]<nint, DxgiOutputDesc*, int>)vtbl[7];
        DxgiOutputDesc local = default;
        var hr = fn(output, &local);
        desc = local;
        return hr;
    }

    public static int Output1DuplicateOutput(nint output1, nint d3d11Device, out nint outputDuplication)
    {
        var vtbl = *(nint**)output1;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtbl[22];
        nint outPtr = 0;
        var hr = fn(output1, d3d11Device, &outPtr);
        outputDuplication = outPtr;
        return hr;
    }

    public static void DuplicationGetDesc(nint duplication, out DxgiOutduplDesc desc)
    {
        var vtbl = *(nint**)duplication;
        var fn = (delegate* unmanaged[Stdcall]<nint, DxgiOutduplDesc*, void>)vtbl[7];
        DxgiOutduplDesc local = default;
        fn(duplication, &local);
        desc = local;
    }

    public static int DuplicationAcquireNextFrame(
        nint duplication,
        uint timeoutMilliseconds,
        out DxgiOutduplFrameInfo frameInfo,
        out nint desktopResource)
    {
        var vtbl = *(nint**)duplication;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, DxgiOutduplFrameInfo*, nint*, int>)vtbl[8];
        DxgiOutduplFrameInfo localInfo = default;
        nint outResource = 0;
        var hr = fn(duplication, timeoutMilliseconds, &localInfo, &outResource);
        frameInfo = localInfo;
        desktopResource = outResource;
        return hr;
    }

    public static int DuplicationGetFrameDirtyRects(
        nint duplication,
        uint bufferSizeBytes,
        RawRect* pBuffer,
        out uint requiredBytes)
    {
        var vtbl = *(nint**)duplication;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, RawRect*, uint*, int>)vtbl[9];
        uint req = 0;
        var hr = fn(duplication, bufferSizeBytes, pBuffer, &req);
        requiredBytes = req;
        return hr;
    }

    public static int DuplicationGetFrameMoveRects(
        nint duplication,
        uint bufferSizeBytes,
        DxgiOutduplMoveRectNative* pBuffer,
        out uint requiredBytes)
    {
        var vtbl = *(nint**)duplication;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, DxgiOutduplMoveRectNative*, uint*, int>)vtbl[10];
        uint req = 0;
        var hr = fn(duplication, bufferSizeBytes, pBuffer, &req);
        requiredBytes = req;
        return hr;
    }

    public static int DuplicationGetFramePointerShape(
        nint duplication,
        uint bufferSizeBytes,
        byte* pBuffer,
        out uint requiredBytes,
        out DxgiOutduplPointerShapeInfo shapeInfo)
    {
        var vtbl = *(nint**)duplication;
        var fn = (delegate* unmanaged[Stdcall]<nint, uint, void*, uint*, DxgiOutduplPointerShapeInfo*, int>)vtbl[11];
        uint req = 0;
        DxgiOutduplPointerShapeInfo localInfo = default;
        var hr = fn(duplication, bufferSizeBytes, pBuffer, &req, &localInfo);
        requiredBytes = req;
        shapeInfo = localInfo;
        return hr;
    }

    public static int DuplicationReleaseFrame(nint duplication)
    {
        var vtbl = *(nint**)duplication;
        var fn = (delegate* unmanaged[Stdcall]<nint, int>)vtbl[14];
        return fn(duplication);
    }

    public static int DeviceCreateTexture2D(
        nint d3d11Device,
        in D3d11Texture2dDesc desc,
        out nint texture2D)
    {
        var vtbl = *(nint**)d3d11Device;
        var fn = (delegate* unmanaged[Stdcall]<nint, D3d11Texture2dDesc*, nint, nint*, int>)vtbl[5];
        var localDesc = desc;
        nint outTex = 0;
        var hr = fn(d3d11Device, &localDesc, 0, &outTex);
        texture2D = outTex;
        return hr;
    }

    public static void Texture2DGetDesc(nint texture2D, out D3d11Texture2dDesc desc)
    {
        var vtbl = *(nint**)texture2D;
        var fn = (delegate* unmanaged[Stdcall]<nint, D3d11Texture2dDesc*, void>)vtbl[10];
        D3d11Texture2dDesc local = default;
        fn(texture2D, &local);
        desc = local;
    }

    public static void ContextCopyResource(nint deviceContext, nint dstResource, nint srcResource)
    {
        var vtbl = *(nint**)deviceContext;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, nint, void>)vtbl[47];
        fn(deviceContext, dstResource, srcResource);
    }

    public static int ContextMap(
        nint deviceContext,
        nint resource,
        uint subresource,
        uint mapType,
        uint mapFlags,
        out D3d11MappedSubresource mapped)
    {
        var vtbl = *(nint**)deviceContext;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, D3d11MappedSubresource*, int>)vtbl[14];
        D3d11MappedSubresource local = default;
        var hr = fn(deviceContext, resource, subresource, mapType, mapFlags, &local);
        mapped = local;
        return hr;
    }

    public static void ContextUnmap(nint deviceContext, nint resource, uint subresource)
    {
        var vtbl = *(nint**)deviceContext;
        var fn = (delegate* unmanaged[Stdcall]<nint, nint, uint, void>)vtbl[15];
        fn(deviceContext, resource, subresource);
    }

    public static DisplayRotation MapDxgiRotation(uint dxgiRotation) =>
        dxgiRotation switch
        {
            2 => DisplayRotation.Rotate90,
            3 => DisplayRotation.Rotate180,
            4 => DisplayRotation.Rotate270,
            _ => DisplayRotation.Identity,
        };

    public static FramePixelFormat MapDxgiFormat(uint dxgiFormat) =>
        dxgiFormat switch
        {
            DxgiFormatR16G16B16A16Float => FramePixelFormat.Rgba16Float,
            _ => FramePixelFormat.Bgra8Unorm,
        };
}
