using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinClip;

/// <summary>
/// Converts between the clipboard's device-independent bitmaps (CF_DIB) and the PNG kept in history.
/// Safe to call off the UI thread: nothing here touches a visual.
/// </summary>
internal static class ImageCodec
{
    // Sharp at the 120-unit expanded preview on 200% displays.
    private const int ThumbnailHeight = 240;
    private const int BitmapFileHeaderSize = 14;
    private const int BitmapInfoHeaderSize = 40;
    private const int BI_BITFIELDS = 3;

    public static byte[] DibToPng(byte[] dib)
    {
        // A DIB is a .bmp file without its 14-byte file header; prepend one so the BMP decoder accepts it.
        var headerSize = BitConverter.ToInt32(dib, 0);
        var bitCount = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToInt32(dib, 16);
        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        var colorMasks = compression == BI_BITFIELDS && headerSize == BitmapInfoHeaderSize ? 12 : 0;
        var pixelOffset = BitmapFileHeaderSize + headerSize + colorMasks + paletteEntries * 4;

        using var file = new MemoryStream(BitmapFileHeaderSize + dib.Length);
        using (var writer = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(BitmapFileHeaderSize + dib.Length);
            writer.Write(0);
            writer.Write(pixelOffset);
            writer.Write(dib);
        }
        file.Position = 0;
        var frame = new BmpBitmapDecoder(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return EncodePng(frame);
    }

    /// <summary>
    /// A 32-bit bottom-up CF_DIB. Transparent areas are flattened onto white, because most apps that read
    /// CF_DIB ignore alpha and would otherwise show them black.
    /// </summary>
    public static byte[] PngToDib(byte[] png)
    {
        var source = new FormatConvertedBitmap(Decode(png), PixelFormats.Bgra32, null, 0);
        int width = source.PixelWidth, height = source.PixelHeight, stride = width * 4;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            for (var channel = 0; channel < 3; channel++)
                pixels[i + channel] = (byte)((pixels[i + channel] * alpha + 255 * (255 - alpha)) / 255);
            pixels[i + 3] = 255;
        }

        var dib = new byte[BitmapInfoHeaderSize + pixels.Length];
        using (var writer = new BinaryWriter(new MemoryStream(dib)))
        {
            writer.Write(BitmapInfoHeaderSize);
            writer.Write(width);
            writer.Write(height); // Positive height: rows stored bottom-up.
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(0); // BI_RGB
            writer.Write(pixels.Length);
        }
        for (var row = 0; row < height; row++)
            Buffer.BlockCopy(pixels, row * stride, dib, BitmapInfoHeaderSize + (height - 1 - row) * stride, stride);
        return dib;
    }

    /// <summary>Pixel size and a small PNG preview for the history list.</summary>
    public static (int Width, int Height, byte[] Thumbnail) Describe(byte[] png)
    {
        var full = Decode(png);
        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.StreamSource = new MemoryStream(png);
        thumbnail.DecodePixelHeight = Math.Min(full.PixelHeight, ThumbnailHeight);
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        thumbnail.EndInit();
        return (full.PixelWidth, full.PixelHeight, EncodePng(thumbnail));
    }

    public static BitmapSource Decode(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
