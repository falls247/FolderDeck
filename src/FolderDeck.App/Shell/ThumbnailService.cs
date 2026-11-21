using System.Windows.Media;
using System.Windows.Media.Imaging;
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
/// Shell thumbnails and icons for cards (§6.4): 4 STA workers, a 5 s timeout per call, and an
/// in-memory LRU (512 entries, also capped by bytes). The disk cache is Windows' thumbcache.
/// </summary>
public sealed class ThumbnailService : IDisposable
{
    public const int IconPixelsBase = 96;

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly StaWorkerPool _pool = new(4, "Thumbs");
    private readonly LruCache<string, ThumbResult> _cache = new(512, 192L * 1024 * 1024, r => EstimateBytes(r.Image));

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

        ThumbResult result;
        try
        {
            result = await _pool.RunAsync(() => Produce(path, source, sizePx, iconPx), cancellationToken, CallTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            result = await FallbackIconAsync(path, source == ThumbSource.Folder, iconPx, cancellationToken).ConfigureAwait(false);
        }

        if (result.Image is not null)
        {
            _cache.Set(key, result);
        }

        return result;
    }

    public void Clear() => _cache.Clear();

    public void Dispose() => _pool.Dispose();

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
                var image = ShellInterop.GetShellImage(path, sizePx, ThumbFlags);
                if (image is not null)
                {
                    return new ThumbResult(image, ThumbMode.Image);
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
