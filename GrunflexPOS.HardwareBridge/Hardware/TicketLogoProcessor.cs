using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace GrunflexPOS.HardwareBridge.Hardware;

/// <summary>
/// Prepares receipt logos: compact size, optional pure black/white.
/// </summary>
public static class TicketLogoProcessor
{
    public const float TicketContentWidthPx = 280f;
    public const float MaxLogoWidthPx = 220f;
    public const float MaxLogoHeightPx = 48f;
    private const int BwThreshold = 150;

    public static Bitmap? CreatePrintBitmap(
        byte[] pngBytes,
        bool monochrome = true,
        float maxWidth = MaxLogoWidthPx,
        float maxHeight = MaxLogoHeightPx)
    {
        if (pngBytes.Length == 0)
            return null;

        using var input = new MemoryStream(pngBytes);
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        if (source.Width <= 0 || source.Height <= 0)
            return null;

        var scale = Math.Min(maxWidth / source.Width, maxHeight / source.Height);
        scale = Math.Min(scale, 1f);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        var color = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(color))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.DrawImage(source, 0, 0, width, height);
        }

        if (!monochrome)
            return color;

        using (color)
            return ToBlackAndWhite(color, BwThreshold);
    }

    public static Bitmap ToBlackAndWhite(Bitmap source, int threshold = BwThreshold)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        var rect = new Rectangle(0, 0, source.Width, source.Height);
        var srcData = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var srcStride = srcData.Stride;
            var dstStride = dstData.Stride;
            var srcBuffer = new byte[srcStride * source.Height];
            var dstBuffer = new byte[dstStride * source.Height];
            System.Runtime.InteropServices.Marshal.Copy(srcData.Scan0, srcBuffer, 0, srcBuffer.Length);

            for (var y = 0; y < source.Height; y++)
            {
                var srcRow = y * srcStride;
                var dstRow = y * dstStride;
                for (var x = 0; x < source.Width; x++)
                {
                    var i = srcRow + x * 3;
                    // Prefer dark blues/blacks of the brand logo as black ink.
                    var b = srcBuffer[i];
                    var g = srcBuffer[i + 1];
                    var r = srcBuffer[i + 2];
                    var luminance = r * 0.299 + g * 0.587 + b * 0.114;
                    var saturation = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                    var isInk = luminance < threshold || (saturation > 40 && luminance < 210);
                    var tone = isInk ? (byte)0 : (byte)255;
                    var d = dstRow + x * 3;
                    dstBuffer[d] = tone;
                    dstBuffer[d + 1] = tone;
                    dstBuffer[d + 2] = tone;
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(dstBuffer, 0, dstData.Scan0, dstBuffer.Length);
        }
        finally
        {
            source.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }

        return result;
    }
}
