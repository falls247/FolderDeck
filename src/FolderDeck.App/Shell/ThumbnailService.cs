using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

public enum ThumbMode
{
    None,

    /// <summary>A real thumbnail: fill the frame, crop, align to the top.</summary>
    Image,

    /// <summary>The shell's folder thumbnail (folder shape with content): fit, centered.</summary>
    Shell,

    /// <summary>An icon: centered, not stretched, 48–96 px.</summary>
    Icon,
}

public enum ThumbSource
{
    /// <summary>A cover image found inside a folder.</summary>
    CoverImage,
    Folder,
    File,
}

public sealed record ThumbResult(ImageSource? Image, ThumbMode Mode)
{
    public static readonly ThumbResult Empty = new(null, ThumbMode.None);
}

/// <summary>
/// Thumbnails and icons for cards (§6.4), with a 5 s timeout per call and an in-memory LRU
/// (512 entries, also capped by bytes).
/// <list type="bullet">
/// <item>Pictures (<see cref="ImageDecoder.CanDecode"/>) are decoded by the app itself on a worker per
/// core (less one, 2 to 8) and kept in its own disk cache (<see cref="ThumbDiskCache"/>).</item>
/// <item>Everything else (folders, videos, Office files, icons, pictures the app cannot decode) comes
/// from the shell on 4 STA workers; the disk cache for those is Windows' thumbcache.</item>
/// </list>
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public const int IconPixelsBase = 96;

    /// <summary>
    /// A thumbnail is kept in the disk cache when making it (reading and decoding the file) took at
    /// least this long; a quicker one is made again as fast as it would load from the cache.
    /// </summary>
    private static readonly TimeSpan DiskCacheMinCost = TimeSpan.FromMilliseconds(4);

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly StaWorkerPool _pool = new(4, "Thumbs");

    /// <summary>One core is left to the UI; below normal priority so decoding never makes scrolling stutter.</summary>
    private readonly StaWorkerPool _decoders = new(Math.Clamp(Environment.ProcessorCount - 1, 2, 8), "Decode", ThreadPriority.BelowNormal);
    private readonly ThumbDiskCache? _disk;
    private readonly LruCache<string, ThumbResult> _cache = new(512, 192L * 1024 * 1024, r => EstimateBytes(r.Image));

    /// <param name="cacheDirectory">Folder of the picture thumbnail cache; null = no disk cache.</param>
    /// <param name="cacheMaxBytes">Upper limit of that folder; 0 or less = no disk cache.</param>
    public ThumbnailService(string? cacheDirectory = null, long cacheMaxBytes = 0)
    {
        if (cacheDirectory is not null && cacheMaxBytes > 0)
        {
            _disk = new ThumbDiskCache(cacheDirectory, cacheMaxBytes);
        }
    }

    /// <summary>Rounds a pixel width up to the 256 / 512 buckets (better cache hits).</summary>
    public static int Bucket(double pixels) => pixels <= 256 ? 256 : 512;

    public bool TryGetCached(string key, out ThumbResult result) => _cache.TryGet(key, out result);

    public static string KeyOf(string path, DateTime modifiedUtc, long length, int size, ThumbSource source) =>
        $"{path.ToUpperInvariant()}|{modifiedUtc.Ticks}|{length}|{size}|{(int)source}";

    public async Task<ThumbResult> GetAsync(string path, ThumbSource source, int sizePx, int iconPx, DateTime modifiedUtc, long length, CancellationToken cancellationToken)
    {
        string key = KeyOf(path, modifiedUtc, length, sizePx, source);
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var result = ThumbResult.Empty;
        if (source != ThumbSource.Folder && ImageDecoder.CanDecode(path))
        {
            try
            {
                result = await _decoders.RunAsync(() => DecodePicture(path, sizePx), cancellationToken, CallTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A file that does not read (dead network drive…): the shell and its own timeout decide.
            }
        }

        if (result.Image is null)
        {
            try
            {
                result = await _pool.RunAsync(() => Produce(path, source, sizePx, iconPx), cancellationToken, CallTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result = await FallbackIconAsync(path, source == ThumbSource.Folder, iconPx, cancellationToken).ConfigureAwait(false);
            }
        }

        if (result.Image is not null && !IsCloudOnlyOfficeIcon(path, source, result))
        {
            _cache.Set(key, result);
        }

        return result;
    }

    /// <summary>
    /// An icon for an Office file that is online-only (OneDrive Files On-Demand): its embedded cover was
    /// not read on purpose, so don't remember the icon — once the file is downloaded (same time stamp
    /// and size, so the same key) the cover should show.
    /// </summary>
    private static bool IsCloudOnlyOfficeIcon(string path, ThumbSource source, ThumbResult result) =>
        source == ThumbSource.File && result.Mode == ThumbMode.Icon && PreviewKinds.IsOpenXml(path) && FileContent.IsCloudOnly(path);

    public void Clear() => _cache.Clear();

    public void Dispose()
    {
        _pool.Dispose();
        _decoders.Dispose();
        _disk?.Dispose();
    }

    /// <summary>
    /// A picture's thumbnail from the app's disk cache or, failing that, decoded from the file (and
    /// then stored). Empty when the app cannot make it — the shell is asked next. Runs on a decoder
    /// worker. An online-only file (OneDrive Files On-Demand) is not opened, so it is not downloaded.
    /// </summary>
    private ThumbResult DecodePicture(string path, int sizePx)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists || FileContent.IsCloudOnly(path))
            {
                return ThumbResult.Empty;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return ThumbResult.Empty;
        }

        // The cache key uses the file's own time stamp and size (callers do not always know them).
        string? diskKey = _disk is null ? null : ThumbDiskCache.KeyOf(path, file.LastWriteTimeUtc, file.Length, sizePx);
        if (diskKey is not null && _disk!.TryRead(diskKey) is { } stored)
        {
            return new ThumbResult(stored, ThumbMode.Image);
        }

        long started = Stopwatch.GetTimestamp();
        var image = ImageDecoder.Decode(path, sizePx);
        if (image is null)
        {
            return ThumbResult.Empty;
        }

        if (diskKey is not null && Stopwatch.GetElapsedTime(started) >= DiskCacheMinCost)
        {
            _disk!.Save(diskKey, image);
        }

        return new ThumbResult(image, ThumbMode.Image);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> _extensionIcons = new(StringComparer.OrdinalIgnoreCase);

    private ThumbResult Produce(string path, ThumbSource source, int sizePx, int iconPx)
    {
        const SIIGBF ThumbFlags = SIIGBF.SIIGBF_THUMBNAILONLY | SIIGBF.SIIGBF_BIGGERSIZEOK;
        const SIIGBF IconFlags = SIIGBF.SIIGBF_ICONONLY;

        switch (source)
        {
            case ThumbSource.CoverImage:
            {
                var image = ShellInterop.GetShellImage(path, sizePx, ThumbFlags);
                return image is null ? ThumbResult.Empty : new ThumbResult(image, ThumbMode.Image);
            }

            case ThumbSource.Folder:
            {
                // The shell's folder thumbnail is used only when it is transparent: an opaque one is the
                // plain folder drawn on a white square, which the folder icon below shows better.
                var image = ShellInterop.GetShellImage(path, sizePx, ThumbFlags);
                if (image is not null && !ShellInterop.HasOpaqueCorners(image))
                {
                    return new ThumbResult(image, ThumbMode.Shell);
                }

                var icon = ShellInterop.GetShellImage(path, iconPx, IconFlags)
                    ?? ShellInterop.GetSystemImageListIcon("folder", isFolder: true, jumbo: iconPx > 48);
                return icon is null ? ThumbResult.Empty : new ThumbResult(icon, ThumbMode.Icon);
            }

            default:
            {
                // Open XML files: the shell's thumbnail needs Office's handler; without it, the picture
                // embedded in the file. Slides and sheets are landscape, so they are fitted, not
                // cropped to 3:4; a Word page is portrait and is cropped from the top like a photo.
                var kind = PreviewKinds.KindOf(path);
                bool openXml = kind is PreviewKind.Presentation or PreviewKind.WordDocument or PreviewKind.Spreadsheet;
                var image = ShellInterop.GetShellImage(path, sizePx, ThumbFlags)
                    ?? (openXml ? OfficeCover.Load(path) : null);
                if (image is not null)
                {
                    return new ThumbResult(image, kind is PreviewKind.Presentation or PreviewKind.Spreadsheet ? ThumbMode.Shell : ThumbMode.Image);
                }

                // Most file types share one icon per extension: ask the shell once.
                string ext = PathTools.ExtensionOf(path);
                bool perExtension = !IconCache.HasOwnIcon(ext);
                string iconKey = ext + "|" + iconPx;
                if (perExtension && _extensionIcons.TryGetValue(iconKey, out var known))
                {
                    return new ThumbResult(known, ThumbMode.Icon);
                }

                var icon = ShellInterop.GetShellImage(path, iconPx, IconFlags)
                    ?? ShellInterop.GetSystemImageListIcon("x" + ext, isFolder: false, jumbo: iconPx > 48);
                if (icon is null)
                {
                    return ThumbResult.Empty;
                }

                if (perExtension)
                {
                    _extensionIcons.TryAdd(iconKey, icon);
                }

                return new ThumbResult(icon, ThumbMode.Icon);
            }
        }
    }

    private Task<ThumbResult> FallbackIconAsync(string path, bool isFolder, int iconPx, CancellationToken cancellationToken) =>
        _pool.RunAsync(
            () =>
            {
                var icon = ShellInterop.GetSystemImageListIcon(isFolder ? "folder" : "x" + PathTools.ExtensionOf(path), isFolder, iconPx > 48);
                return icon is null ? ThumbResult.Empty : new ThumbResult(icon, ThumbMode.Icon);
            },
            cancellationToken,
            CallTimeout);

    private static long EstimateBytes(ImageSource? image) =>
        image is BitmapSource b ? (long)b.PixelWidth * b.PixelHeight * 4 : 1024;
}

/// <summary>Thread-safe LRU bounded by entry count and by an estimated byte size.</summary>
public sealed class LruCache<TKey, TValue>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Bytes)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value, long Bytes)> _order = new();
    private readonly int _maxCount;
    private readonly long _maxBytes;
    private readonly Func<TValue, long> _sizeOf;
    private long _bytes;

    public LruCache(int maxCount, long maxBytes, Func<TValue, long> sizeOf)
    {
        _maxCount = maxCount;
        _maxBytes = maxBytes;
        _sizeOf = sizeOf;
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        long bytes = _sizeOf(value);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _bytes -= existing.Value.Bytes;
            }

            var node = _order.AddFirst((key, value, bytes));
            _map[key] = node;
            _bytes += bytes;
            while ((_map.Count > _maxCount || _bytes > _maxBytes) && _order.Last is { } last && last != node)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                _bytes -= last.Value.Bytes;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }
}
