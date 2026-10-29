using System.Buffers.Binary;
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
    private const int ThumbnailHeight = 160;
    private const int BitmapFileHeaderSize = 14;
    private const int BitmapInfoHeaderSize = 40;
    private const int BitmapV5HeaderSize = 124;
    private const int BI_RGB = 0;
    private const int BI_BITFIELDS = 3;

    // Beyond this a "screenshot" is almost certainly a malformed or hostile header, not something to decode.
    private const long MaxPixels = 100_000_000;

    public static byte[] DibToPng(byte[] dib)
    {
        // A DIB is a .bmp file without its 14-byte file header; prepend one so the BMP decoder accepts it.
        var pixelOffset = ValidateDib(dib);

        using var file = new MemoryStream(BitmapFileHeaderSize + dib.Length);
        using (var writer = new BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(BitmapFileHeaderSize + dib.Length);
            writer.Write(0);
            writer.Write(BitmapFileHeaderSize + pixelOffset);
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
        var (width, height) = ReadSize(png);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException($"An image on the clipboard was {width} × {height} pixels.");

        var thumbnail = new BitmapImage();
        thumbnail.BeginInit();
        thumbnail.StreamSource = new MemoryStream(png);
        thumbnail.DecodePixelHeight = Math.Min(height, ThumbnailHeight);
        thumbnail.CacheOption = BitmapCacheOption.OnLoad;
        thumbnail.EndInit();
        thumbnail.Freeze();
        return (width, height, EncodePng(thumbnail));
    }

    public static BitmapSource Decode(byte[] png)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// Size from the PNG header, or from the decoder's frame metadata for anything else. Neither path decodes
    /// the pixels: the full image is only ever decoded to be shown or re-encoded.
    /// </summary>
    private static (int Width, int Height) ReadSize(byte[] png)
    {
        if (png.Length >= 24 && png[0] == 0x89 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G'
            && png[12] == 'I' && png[13] == 'H' && png[14] == 'D' && png[15] == 'R')
        {
            var width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
            var height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
            if (width > 0 && height > 0)
                return (width, height);
        }

        using var stream = new MemoryStream(png);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    /// <summary>
    /// Checks a CF_DIB header and returns the offset of its pixels. The bytes come from another process, so
    /// every size and shape is validated before the decoder is allowed to see them.
    /// </summary>
    /// <exception cref="InvalidDataException">The header describes something that cannot be a bitmap.</exception>
    private static int ValidateDib(byte[] dib)
    {
        if (dib.Length < BitmapInfoHeaderSize)
            throw new InvalidDataException(
                $"An image on the clipboard had a {dib.Length}-byte header; at least {BitmapInfoHeaderSize} are needed.");

        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize is < BitmapInfoHeaderSize or > BitmapV5HeaderSize || headerSize > dib.Length)
            throw new InvalidDataException($"An image on the clipboard had an unsupported header size ({headerSize}).");

        var width = BitConverter.ToInt32(dib, 4);
        var height = BitConverter.ToInt32(dib, 8); // Negative for a top-down bitmap.
        if (width <= 0 || height == 0 || (long)width * Math.Abs((long)height) > MaxPixels)
            throw new InvalidDataException($"An image on the clipboard was {width} × {height} pixels.");

        var bitCount = BitConverter.ToUInt16(dib, 14);
        if (bitCount is not (1 or 2 or 4 or 8 or 16 or 24 or 32))
            throw new InvalidDataException($"An image on the clipboard had {bitCount} bits per pixel.");

        var compression = BitConverter.ToInt32(dib, 16);
        if (compression is not (BI_RGB or BI_BITFIELDS))
            throw new InvalidDataException($"An image on the clipboard used unsupported compression ({compression}).");

        var colorsUsed = BitConverter.ToInt32(dib, 32);
        var paletteEntries = colorsUsed != 0 ? colorsUsed : bitCount <= 8 ? 1 << bitCount : 0;
        var colorMasks = compression == BI_BITFIELDS && headerSize == BitmapInfoHeaderSize ? 12 : 0;
        var pixelOffset = headerSize + colorMasks + paletteEntries * 4L;
        var stride = ((long)width * bitCount + 31) / 32 * 4;
        if (pixelOffset > dib.Length || dib.Length - pixelOffset < stride * Math.Abs((long)height))
            throw new InvalidDataException(
                $"An image on the clipboard ended before its pixels (declared {dib.Length} bytes).");

        return (int)pixelOffset;
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
