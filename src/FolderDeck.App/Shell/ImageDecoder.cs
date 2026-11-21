using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;

namespace FolderDeck.Shell;

/// <summary>
/// Makes a picture's thumbnail without the shell: the file is decoded with WIC straight to thumbnail
/// size. The JPEG codec scales while it decodes (1/2, 1/4, 1/8), which is several times faster than
/// the shell's extractor, and any thread may decode, so every core is used. The result is frozen
/// (usable from any thread) and is Bgr32, or Pbgra32 when the picture really is transparent.
/// </summary>
public static class ImageDecoder
{
    /// <summary>
    /// The formats decoded here. HEIC / AVIF (their codecs turn the picture themselves) and .ico
    /// (many sizes in one file) stay with the shell.
    /// </summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp",
    };

    /// <summary>Formats whose codec is an optional Windows component.</summary>
    private static readonly HashSet<string> OptionalCodecs = new(StringComparer.OrdinalIgnoreCase) { ".webp" };

    /// <summary>Optional codecs found missing in this run: those files go to the shell at once.</summary>
    private static readonly ConcurrentDictionary<string, bool> MissingCodecs = new(StringComparer.OrdinalIgnoreCase);

    public static bool CanDecode(string path)
    {
        string ext = PathTools.ExtensionOf(path);
        return Extensions.Contains(ext) && !MissingCodecs.ContainsKey(ext);
    }

    /// <summary>
    /// The picture scaled so that its shorter side is <paramref name="sizePx"/> (see
    /// <see cref="ThumbSizing.Fit"/>), turned as its EXIF orientation says. Null when the file cannot
    /// be read or decoded — the caller then asks the shell. Never throws.
    /// </summary>
    public static BitmapSource? Decode(string path, int sizePx)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            return Decode(stream, sizePx);
        }
        catch (NotSupportedException) when (OptionalCodecs.Contains(PathTools.ExtensionOf(path)))
        {
            MissingCodecs.TryAdd(PathTools.ExtensionOf(path), true);
            return null;
        }
        catch (Exception)
        {
            // A broken or unusual file, no access, a codec error…: whatever it is, the shell gets a try.
            return null;
        }
    }

    /// <summary>The picture in <paramref name="stream"/>; may throw what WIC and the stream throw.</summary>
    internal static BitmapSource? Decode(Stream stream, int sizePx)
    {
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.None);
        if (decoder.Frames.Count == 0)
        {
            return null;
        }

        BitmapFrame frame = decoder.Frames[0];
        int width = frame.PixelWidth;
        int height = frame.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var (w, h) = ThumbSizing.Fit(width, height, sizePx);
        var scale = w == width && h == height ? null : new ScaleTransform((double)w / width, (double)h / height);

        BitmapSource source = frame;
        if (ScalesInCodec(frame.Format))
        {
            // The scaler sits directly on the codec, so a JPEG is decoded at 1/2, 1/4 or 1/8;
            // only the few remaining pixels are converted.
            source = ConvertTo(scale is null ? source : new TransformedBitmap(source, scale), PixelFormats.Bgr32);
        }
        else
        {
            // Palettes, 1–4 bit pixels and straight alpha do not scale well: premultiply first.
            source = ConvertTo(source, PixelFormats.Pbgra32);
            source = scale is null ? source : new TransformedBitmap(source, scale);
        }

        var image = ToMemory(source);
        return Orientation(frame) is var orientation and >= 2 and <= 8
            ? ToMemory(new TransformedBitmap(image, new MatrixTransform(OrientationMatrix(orientation))))
            : image;
    }

    /// <summary>Opaque full-color formats; everything else may carry transparency or needs conversion first.</summary>
    private static bool ScalesInCodec(PixelFormat format) =>
        format == PixelFormats.Bgr24 || format == PixelFormats.Bgr32 || format == PixelFormats.Rgb24
        || format == PixelFormats.Gray8 || format == PixelFormats.Cmyk32
        || format == PixelFormats.Bgr555 || format == PixelFormats.Bgr565
        || format == PixelFormats.Gray16 || format == PixelFormats.Rgb48 || format == PixelFormats.Bgr101010;

    private static BitmapSource ConvertTo(BitmapSource source, PixelFormat format) =>
        source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);

    /// <summary>
    /// Copies the pixels out of the decoding chain into a bitmap of its own (the chain reads the file
    /// lazily). A Pbgra32 picture without a single transparent pixel becomes Bgr32.
    /// </summary>
    private static unsafe BitmapSource ToMemory(BitmapSource source)
    {
        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 4;
        int size = stride * height;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            source.CopyPixels(new Int32Rect(0, 0, width, height), buffer, stride, 0);
            var format = source.Format;
            if (format == PixelFormats.Pbgra32 && IsOpaque(buffer.AsSpan(0, size)))
            {
                format = PixelFormats.Bgr32;
            }

            fixed (byte* pixels = buffer)
            {
                var bitmap = BitmapSource.Create(width, height, 96, 96, format, null, (IntPtr)pixels, size, stride);
                bitmap.Freeze();
                return bitmap;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool IsOpaque(ReadOnlySpan<byte> bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 255)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The EXIF orientation (1–8; 1 = as stored), read through WIC's photo metadata policy.</summary>
    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("System.Photo.Orientation") is ushort value)
            {
                return value;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException or IOException)
        {
            // No metadata reader for this format, or damaged metadata: show it as stored.
        }

        return 1;
    }

    /// <summary>How a picture stored with EXIF orientation 2–8 is turned to be upright.</summary>
    private static Matrix OrientationMatrix(int orientation)
    {
        var m = Matrix.Identity;
        switch (orientation)
        {
            case 2: // mirrored left-right
                m.Scale(-1, 1);
                break;
            case 3:
                m.Rotate(180);
                break;
            case 4: // mirrored top-bottom
                m.Scale(1, -1);
                break;
            case 5: // transposed
                m.Rotate(90);
                m.Scale(-1, 1);
                break;
            case 6:
                m.Rotate(90);
                break;
            case 7: // transversed
                m.Rotate(270);
                m.Scale(-1, 1);
                break;
            case 8:
                m.Rotate(270);
                break;
        }

        return m;
    }
}
