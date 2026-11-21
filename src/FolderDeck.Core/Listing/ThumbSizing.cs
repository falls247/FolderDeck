namespace FolderDeck.Core.Listing;

/// <summary>The pixel size a picture is decoded to when the app makes its thumbnail itself.</summary>
public static class ThumbSizing
{
    /// <summary>The longer side is at most this many times the requested size (panoramas, long strips).</summary>
    public const int MaxAspect = 4;

    /// <summary>
    /// Scales <paramref name="width"/> × <paramref name="height"/> down so that the shorter side is
    /// <paramref name="sizePx"/>: the picture then covers a square of that size, which is what a
    /// card's cropped thumbnail needs. Never scales up, and keeps the longer side within
    /// <see cref="MaxAspect"/> × <paramref name="sizePx"/>.
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int sizePx)
    {
        if (width <= 0 || height <= 0 || sizePx <= 0)
        {
            return (Math.Max(1, width), Math.Max(1, height));
        }

        double scale = Math.Min(1.0, (double)sizePx / Math.Min(width, height));
        scale = Math.Min(scale, (double)sizePx * MaxAspect / Math.Max(width, height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }
}
