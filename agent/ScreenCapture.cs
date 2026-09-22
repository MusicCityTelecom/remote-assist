using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteAssist.Agent;

internal sealed record MonitorInfo(
    int Index,
    string DeviceName,
    int Left,
    int Top,
    int Width,
    int Height,
    bool Primary);

internal static class ScreenCapture
{
    public const int AllMonitorsIndex = -2;
    private static readonly object CaptureLock = new();
    private static DxgiScreenCapture? _dxgi;
    private static int _dxgiScreenIndex = -1;
    private static DateTime _dxgiRetryAfterUtc = DateTime.MinValue;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize; public int flags; public nint hCursor; public POINT ptScreenPos; }
    private const int CURSOR_SHOWING = 0x00000001;

    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")] private static extern bool DrawIcon(nint hDC, int X, int Y, nint hIcon);

    public static MonitorInfo[] GetMonitors()
    {
        var screens = Screen.AllScreens;
        var result = new MonitorInfo[screens.Length];
        for (var i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            result[i] = new MonitorInfo(
                i,
                s.DeviceName,
                s.Bounds.Left,
                s.Bounds.Top,
                s.Bounds.Width,
                s.Bounds.Height,
                s.Primary);
        }
        return result;
    }

    public static Rectangle GetBounds(int screenIndex)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0)
            return SystemInformation.VirtualScreen;

        if (screenIndex == AllMonitorsIndex && screens.Length > 1)
            return SystemInformation.VirtualScreen;

        if (screenIndex < 0 || screenIndex >= screens.Length)
        {
            var primary = Array.FindIndex(screens, s => s.Primary);
            screenIndex = primary >= 0 ? primary : 0;
        }
        return screens[screenIndex].Bounds;
    }

    public static int NormalizeScreenIndex(int screenIndex)
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return 0;
        if (screenIndex == AllMonitorsIndex && screens.Length > 1)
            return AllMonitorsIndex;
        if (screenIndex >= 0 && screenIndex < screens.Length) return screenIndex;
        var primary = Array.FindIndex(screens, s => s.Primary);
        return primary >= 0 ? primary : 0;
    }

    public static bool TryCaptureJpeg(
        int screenIndex,
        long quality,
        int timeoutMs,
        int scalePercent,
        bool forceGdi,
        out byte[]? jpeg,
        out string backend)
    {
        screenIndex = NormalizeScreenIndex(screenIndex);

        var allMonitors = screenIndex == AllMonitorsIndex;

        lock (CaptureLock)
        {
            if (!allMonitors && !forceGdi && DateTime.UtcNow >= _dxgiRetryAfterUtc)
            {
                try
                {
                    EnsureDxgi(screenIndex);
                    if (_dxgi is not null)
                    {
                        var status = _dxgi.TryCaptureJpeg(timeoutMs, quality, scalePercent, out jpeg);
                        backend = "DXGI";

                        if (status == DxgiCaptureStatus.Frame)
                            return true;

                        if (status == DxgiCaptureStatus.NoFrame)
                            return false;

                        ResetDxgi(TimeSpan.FromMilliseconds(250));
                        jpeg = null;
                        backend = "DXGI reset";
                        return false;
                    }
                }
                catch
                {
                    ResetDxgi(TimeSpan.FromSeconds(30));
                }
            }

            try
            {
                jpeg = CaptureJpegGdi(screenIndex, quality, scalePercent);
                backend = allMonitors
                    ? "GDI all monitors"
                    : forceGdi ? "GDI compatibility" : "GDI fallback";
                return true;
            }
            catch
            {
                jpeg = null;
                backend = "capture unavailable";
                return false;
            }
        }
    }

    public static bool TryCaptureBgra(
        int screenIndex,
        int timeoutMs,
        int scalePercent,
        bool forceGdi,
        out CapturedBgraFrame? frame,
        out string backend)
    {
        screenIndex = NormalizeScreenIndex(screenIndex);

        var allMonitors = screenIndex == AllMonitorsIndex;

        lock (CaptureLock)
        {
            if (!allMonitors && !forceGdi && DateTime.UtcNow >= _dxgiRetryAfterUtc)
            {
                try
                {
                    EnsureDxgi(screenIndex);
                    if (_dxgi is not null)
                    {
                        var status = _dxgi.TryCaptureBgra(timeoutMs, scalePercent, out frame);
                        backend = "DXGI";

                        if (status == DxgiCaptureStatus.Frame)
                            return true;

                        if (status == DxgiCaptureStatus.NoFrame)
                            return false;

                        ResetDxgi(TimeSpan.FromMilliseconds(250));
                        frame = null;
                        backend = "DXGI reset";
                        return false;
                    }
                }
                catch
                {
                    ResetDxgi(TimeSpan.FromSeconds(30));
                }
            }

            try
            {
                frame = CaptureBgraGdi(screenIndex, scalePercent);
                backend = allMonitors
                    ? "GDI all monitors"
                    : forceGdi ? "GDI compatibility" : "GDI fallback";
                return true;
            }
            catch
            {
                frame = null;
                backend = "capture unavailable";
                return false;
            }
        }
    }

    public static void ResetAcceleratedCapture()
    {
        lock (CaptureLock)
        {
            ResetDxgi(TimeSpan.Zero);
        }
    }

    private static void EnsureDxgi(int screenIndex)
    {
        if (_dxgi is not null && _dxgiScreenIndex == screenIndex)
            return;

        ResetDxgi(TimeSpan.Zero);
        _dxgi = DxgiScreenCapture.Create(screenIndex);
        _dxgiScreenIndex = screenIndex;
        _dxgiRetryAfterUtc = DateTime.MinValue;
    }

    private static void ResetDxgi(TimeSpan retryDelay)
    {
        try { _dxgi?.Dispose(); } catch { }
        _dxgi = null;
        _dxgiScreenIndex = -1;
        _dxgiRetryAfterUtc = DateTime.UtcNow.Add(retryDelay);
    }

    private static CapturedBgraFrame CaptureBgraGdi(int screenIndex, int scalePercent)
    {
        var bounds = GetBounds(screenIndex);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(graphics, bounds);
        }

        return CapturedBgraFrame.FromBitmap(bitmap, scalePercent);
    }

    private static byte[] CaptureJpegGdi(int screenIndex, long quality, int scalePercent)
    {
        var bounds = GetBounds(screenIndex);
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
            DrawCursor(graphics, bounds);
        }

        scalePercent = Math.Clamp(scalePercent, 50, 100);
        Bitmap? scaled = null;
        var output = bitmap;

        if (scalePercent != 100)
        {
            var width = Math.Max(1, bitmap.Width * scalePercent / 100);
            var height = Math.Max(1, bitmap.Height * scalePercent / 100);
            scaled = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(scaled);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(bitmap, new Rectangle(0, 0, width, height));
            output = scaled;
        }

        try
        {
            using var stream = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == ImageFormat.Jpeg.Guid);
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

    private static void DrawCursor(Graphics graphics, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING || ci.hCursor == 0) return;
        if (!bounds.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;

        var hdc = graphics.GetHdc();
        try { DrawIcon(hdc, ci.ptScreenPos.X - bounds.Left, ci.ptScreenPos.Y - bounds.Top, ci.hCursor); }
        finally { graphics.ReleaseHdc(hdc); }
    }
}
