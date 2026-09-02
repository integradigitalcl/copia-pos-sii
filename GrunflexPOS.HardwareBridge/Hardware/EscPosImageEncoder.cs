using System.Drawing;
using System.Drawing.Imaging;

namespace GrunflexPOS.HardwareBridge.Hardware;

public static class EscPosImageEncoder
{
    public static byte[] EncodePng(byte[] pngBytes, int paperWidthMm = 80, bool monochrome = true)
    {
        if (pngBytes.Length == 0)
            return [];

        var maxDots = paperWidthMm <= 58 ? 384 : 576;
        // Keep logo compact and leave side margins so it looks centered on the receipt.
        var maxLogoDots = (int)(maxDots * 0.72);
        using var prepared = TicketLogoProcessor.CreatePrintBitmap(pngBytes, monochrome, maxLogoDots, 96);
        if (prepared is null)
            return [];

        var raster = EncodeBitmap(prepared);
        if (raster.Length == 0)
            return [];

        // Left-aligned with ticket text (ESC a 0).
        using var output = new MemoryStream(3 + raster.Length + 1);
        output.WriteByte(0x1B);
        output.WriteByte(0x61);
        output.WriteByte(0x00);
        output.Write(raster);
        output.WriteByte(0x0A);
        return output.ToArray();
    }

    private static byte[] EncodeBitmap(Bitmap bitmap)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var bytesPerRow = (width + 7) / 8;
        var raster = new byte[bytesPerRow * height];

        var rect = new Rectangle(0, 0, width, height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var buffer = new byte[stride * height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

            for (var y = 0; y < height; y++)
            {
                var rowOffset = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var pixelOffset = rowOffset + x * 3;
                    var b = buffer[pixelOffset];
                    var g = buffer[pixelOffset + 1];
                    var r = buffer[pixelOffset + 2];
                    var luminance = r * 0.299 + g * 0.587 + b * 0.114;
                    if (luminance >= 170)
                        continue;

                    var byteIndex = y * bytesPerRow + x / 8;
                    raster[byteIndex] |= (byte)(0x80 >> (x % 8));
                }
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        var payload = new byte[8 + raster.Length];
        payload[0] = 0x1D;
        payload[1] = 0x76;
        payload[2] = 0x30;
        payload[3] = 0x00;
        payload[4] = (byte)(bytesPerRow & 0xFF);
        payload[5] = (byte)((bytesPerRow >> 8) & 0xFF);
        payload[6] = (byte)(height & 0xFF);
        payload[7] = (byte)((height >> 8) & 0xFF);
        Buffer.BlockCopy(raster, 0, payload, 8, raster.Length);
        return payload;
    }
}
