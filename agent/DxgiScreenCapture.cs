using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace RemoteAssist.Agent;

internal enum DxgiCaptureStatus
{
    Frame,
    NoFrame,
    AccessLost
}

internal sealed class DxgiScreenCapture : IDisposable
{
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int CursorShowing = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PointNative
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public PointNative ptScreenPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(nint hDC, int x, int y, nint hIcon);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly ID3D11Texture2D _staging;
    private readonly Rectangle _bounds;
    private bool _disposed;

    public int Width => _bounds.Width;
    public int Height => _bounds.Height;
    public string DeviceName { get; }

    private DxgiScreenCapture(
        string deviceName,
        Rectangle bounds,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication,
        ID3D11Texture2D staging)
    {
        DeviceName = deviceName;
        _bounds = bounds;
        _device = device;
        _context = context;
        _duplication = duplication;
        _staging = staging;
    }

    public static DxgiScreenCapture Create(int screenIndex)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            throw new InvalidOperationException("No Windows displays are available.");

        screenIndex = ScreenCapture.NormalizeScreenIndex(screenIndex);
        var screen = screens[screenIndex];
        var wantedName = screen.DeviceName;

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

        for (uint adapterIndex = 0; adapterIndex < 16; adapterIndex++)
        {
            var adapterResult = factory.EnumAdapters1(adapterIndex, out var adapter);
            if (adapterResult.Failure)
            {
                adapter?.Dispose();
                break;
            }

            using (adapter)
            {
                for (uint outputIndex = 0; outputIndex < 16; outputIndex++)
                {
                    var outputResult = adapter.EnumOutputs(outputIndex, out var output);
                    if (outputResult.Failure)
                    {
                        output?.Dispose();
                        break;
                    }

                    using (output)
                    {
                        var desc = output.Description;
                        if (!string.Equals(desc.DeviceName, wantedName, StringComparison.OrdinalIgnoreCase))
                            continue;

                        var levels = new[]
                        {
                            FeatureLevel.Level_11_1,
                            FeatureLevel.Level_11_0,
                            FeatureLevel.Level_10_1,
                            FeatureLevel.Level_10_0
                        };

                        var create = D3D11.D3D11CreateDevice(
                            adapter,
                            DriverType.Unknown,
                            DeviceCreationFlags.BgraSupport,
                            levels,
                            out ID3D11Device device,
                            out ID3D11DeviceContext context);

                        if (create.Failure)
                        {
                            context?.Dispose();
                            device?.Dispose();
                            throw new InvalidOperationException($"Direct3D device creation failed: 0x{create.Code:X8}");
                        }

                        try
                        {
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            var duplication = output1.DuplicateOutput(device);

                            var width = desc.DesktopCoordinates.Right - desc.DesktopCoordinates.Left;
                            var height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

                            var staging = device.CreateTexture2D(new Texture2DDescription
                            {
                                Width = (uint)width,
                                Height = (uint)height,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = Format.B8G8R8A8_UNorm,
                                SampleDescription = new SampleDescription(1, 0),
                                Usage = ResourceUsage.Staging,
                                BindFlags = BindFlags.None,
                                CPUAccessFlags = CpuAccessFlags.Read,
                                MiscFlags = ResourceOptionFlags.None
                            });

                            return new DxgiScreenCapture(
                                wantedName,
                                new Rectangle(
                                    desc.DesktopCoordinates.Left,
                                    desc.DesktopCoordinates.Top,
                                    width,
                                    height),
                                device,
                                context,
                                duplication,
                                staging);
                        }
                        catch
                        {
                            context.Dispose();
                            device.Dispose();
                            throw;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException($"DXGI output {wantedName} was not found.");
    }

    public DxgiCaptureStatus TryCaptureJpeg(int timeoutMs, long quality, int scalePercent, out byte[]? jpeg)
    {
        jpeg = null;
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = _duplication.AcquireNextFrame(
            (uint)Math.Max(0, timeoutMs),
            out _,
            out IDXGIResource? resource);

        if (result.Code == DxgiErrorWaitTimeout)
        {
            resource?.Dispose();
            return DxgiCaptureStatus.NoFrame;
        }

        if (result.Code == DxgiErrorAccessLost)
        {
            resource?.Dispose();
            return DxgiCaptureStatus.AccessLost;
        }

        if (result.Failure)
        {
            resource?.Dispose();
            throw new InvalidOperationException($"DXGI AcquireNextFrame failed: 0x{result.Code:X8}");
        }

        try
        {
            using var source = resource!.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, source);

            var mapped = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                CopyMappedFrame(bitmap, mapped.DataPointer, (int)mapped.RowPitch);
                DrawCursor(bitmap);

                jpeg = EncodeJpeg(bitmap, quality, scalePercent);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            return DxgiCaptureStatus.Frame;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    public DxgiCaptureStatus TryCaptureBgra(int timeoutMs, int scalePercent, out CapturedBgraFrame? frame)
    {
        frame = null;
        ObjectDisposedException.ThrowIf(_disposed, this);

        var result = _duplication.AcquireNextFrame(
            (uint)Math.Max(0, timeoutMs),
            out _,
            out IDXGIResource? resource);

        if (result.Code == DxgiErrorWaitTimeout)
        {
            resource?.Dispose();
            return DxgiCaptureStatus.NoFrame;
        }

        if (result.Code == DxgiErrorAccessLost)
        {
            resource?.Dispose();
            return DxgiCaptureStatus.AccessLost;
        }

        if (result.Failure)
        {
            resource?.Dispose();
            throw new InvalidOperationException($"DXGI AcquireNextFrame failed: 0x{result.Code:X8}");
        }

        try
        {
            using var source = resource!.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_staging, source);

            var mapped = _context.Map(_staging, 0, MapMode.Read);
            try
            {
                using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
                CopyMappedFrame(bitmap, mapped.DataPointer, (int)mapped.RowPitch);
                DrawCursor(bitmap);
                frame = CapturedBgraFrame.FromBitmap(bitmap, scalePercent);
            }
            finally
            {
                _context.Unmap(_staging, 0);
            }

            return DxgiCaptureStatus.Frame;
        }
        finally
        {
            resource?.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    private static byte[] EncodeJpeg(Bitmap source, long quality, int scalePercent)
    {
        scalePercent = Math.Clamp(scalePercent, 50, 100);
        Bitmap? scaled = null;
        var output = source;

        if (scalePercent != 100)
        {
            var width = Math.Max(1, source.Width * scalePercent / 100);
            var height = Math.Max(1, source.Height * scalePercent / 100);
            scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(scaled);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
            output = scaled;
        }

        try
        {
            using var stream = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders()
                .First(x => x.FormatID == ImageFormat.Jpeg.Guid);
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(
                System.Drawing.Imaging.Encoder.Quality,
                Math.Clamp(quality, 20L, 90L));
            output.Save(stream, encoder, parameters);
            return stream.ToArray();
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    private static unsafe void CopyMappedFrame(Bitmap bitmap, nint source, int sourceStride)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytesPerRow = bitmap.Width * 4;
            var src = (byte*)source;
            var dst = (byte*)data.Scan0;

            for (var y = 0; y < bitmap.Height; y++)
            {
                Buffer.MemoryCopy(
                    src + y * sourceStride,
                    dst + y * data.Stride,
                    Math.Abs(data.Stride),
                    bytesPerRow);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private void DrawCursor(Bitmap bitmap)
    {
        var ci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CursorShowing || ci.hCursor == 0)
            return;
        if (!_bounds.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y))
            return;

        using var graphics = Graphics.FromImage(bitmap);
        var hdc = graphics.GetHdc();
        try
        {
            DrawIcon(
                hdc,
                ci.ptScreenPos.X - _bounds.Left,
                ci.ptScreenPos.Y - _bounds.Top,
                ci.hCursor);
        }
        finally
        {
            graphics.ReleaseHdc(hdc);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _staging.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
