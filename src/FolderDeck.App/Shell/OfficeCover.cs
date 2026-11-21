using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderDeck.Core.Listing;

namespace FolderDeck.Shell;

/// <summary>Decodes the cover thumbnail embedded in a presentation (see <see cref="OfficeThumbnail"/>).</summary>
internal static class OfficeCover
{
    /// <summary>The cover as a frozen 96-DPI bitmap, or null. Safe on any thread.</summary>
    public static BitmapSource? Load(string path)
    {
        var bytes = OfficeThumbnail.ReadEmbedded(path);
        if (bytes is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return null;
            }

            // Re-create at 96 DPI: the JPEG's own DPI (often 72 or 300) would change its size on screen.
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Bgra32 && frame.Format != PixelFormats.Bgr32)
            {
                frame = new FormatConvertedBitmap(frame, PixelFormats.Bgr32, null, 0);
            }

            int width = frame.PixelWidth;
            int height = frame.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            frame.CopyPixels(pixels, stride, 0);
            var bitmap = BitmapSource.Create(width, height, 96, 96, frame.Format, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException or OverflowException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
