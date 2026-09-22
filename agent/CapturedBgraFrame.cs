using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace RemoteAssist.Agent;

internal sealed record CapturedBgraFrame(int Width, int Height, byte[] Data)
{
    public int Stride => Width * 4;

    public static CapturedBgraFrame FromBitmap(Bitmap source, int scalePercent)
    {
        scalePercent = Math.Clamp(scalePercent, 50, 100);

        var width = Math.Max(2, source.Width * scalePercent / 100);
        var height = Math.Max(2, source.Height * scalePercent / 100);

        // NV12/H.264 requires even dimensions.
        width &= ~1;
        height &= ~1;

        using var converted = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(converted))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = scalePercent == 100
                ? InterpolationMode.NearestNeighbor
                : InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }

        var bytes = new byte[checked(width * height * 4)];
        var rect = new Rectangle(0, 0, width, height);
        var bits = converted.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                var src = bits.Stride >= 0
                    ? bits.Scan0 + y * bits.Stride
                    : bits.Scan0 + (height - 1 - y) * -bits.Stride;
                Marshal.Copy(src, bytes, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            converted.UnlockBits(bits);
        }

        return new CapturedBgraFrame(width, height, bytes);
    }
}
