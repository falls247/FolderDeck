using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderDeck.Core.Infrastructure;
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

    /// <summary>
    /// Online-only pictures (OneDrive Files On-Demand…) larger than this are not downloaded for a
    /// thumbnail or a preview (<see cref="DownloadAsync"/>).
    /// </summary>
    public const long MaxDownloadBytes = 100L * 1024 * 1024;

    /// <summary>Longest a picture's download for its thumbnail may take.</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Downloads of online-only pictures run a few at a time, outside the worker pools (the cards of
    /// local files never wait behind the network), in the order they were asked for. A preview has
    /// its own: the user is waiting on it, it does not queue behind the cards'.
    /// </summary>
    private readonly SemaphoreSlim _downloads = new(2);
    private readonly SemaphoreSlim _previewDownloads = new(1);

    /// <summary>Pause after a download found the network or the sync app unavailable.</summary>
    private static readonly TimeSpan UnreachablePause = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Wait before asking the shell again when its thumbnail provider answered that the thumbnail
    /// was still being made (<see cref="ShellInterop.GetShellImage(string, int, SIIGBF, out bool)"/>).
    /// </summary>
    private static readonly TimeSpan PendingRetryDelay = TimeSpan.FromMilliseconds(1500);

    /// <summary>Downloads were refused (blocked in Windows' settings, or cancelled): none for the rest of the session.</summary>
    private volatile bool _downloadsRefused;

    /// <summary><see cref="Environment.TickCount64"/> before which no download starts (offline, sync app not running).</summary>
    private long _downloadsResumeAt;

    /// <summary>
    /// Files whose download failed or took too long this session (until <see cref="Clear"/>): a card
    /// does not try again each time it comes back on screen; a preview (the user asks) does.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _failedDownloads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Picture types the app does not decode itself (HEIC, AVIF…) that, once downloaded, gave no
    /// thumbnail (Windows lacks their codec): count of such files, or -1 once one worked. After
    /// <see cref="ExtensionMissesToStop"/> without one working, they are no longer downloaded.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _extensionMisses = new(StringComparer.OrdinalIgnoreCase);

    private const int ExtensionMissesToStop = 2;

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

    /// <summary>Windows or the user refused this app's downloads of online-only files during this session.</summary>
    public bool DownloadsRefused => _downloadsRefused;

    /// <summary>Downloads are paused for a while: the last one found the network or the sync app unavailable.</summary>
    public bool DownloadsPaused => Environment.TickCount64 < Interlocked.Read(ref _downloadsResumeAt);

    /// <param name="downloading">
    /// Told, with the result so far (the icon), when the file is an online-only picture the shell
    /// has no thumbnail for and its download starts (<see cref="DownloadAsync"/>); the picture is
    /// then the result, once downloaded. A <see cref="Progress{T}"/> made on the UI thread reports
    /// there. Cancelling <paramref name="cancellationToken"/> stops the download.
    /// </param>
    /// <param name="urgent">The user is waiting on this one (a preview): its download does not queue behind the cards'.</param>
    public async Task<ThumbResult> GetAsync(
        string path,
        ThumbSource source,
        int sizePx,
        int iconPx,
        DateTime modifiedUtc,
        long length,
        CancellationToken cancellationToken,
        IProgress<ThumbResult>? downloading = null,
        bool urgent = false)
    {
        string key = KeyOf(path, modifiedUtc, length, sizePx, source);
        if (_cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var (result, shellPending) = await ProduceAsync(path, source, sizePx, iconPx, downloaded: false, cancellationToken).ConfigureAwait(false);
        bool remember = false;
        if (source == ThumbSource.File && result.Mode != ThumbMode.Image
            && PreviewKinds.KindOf(path) == PreviewKind.Image && FileContent.IsDownloadable(path))
        {
            (result, remember) = await FromCloudAsync(path, sizePx, iconPx, length, result, shellPending, downloading, urgent, cancellationToken).ConfigureAwait(false);
        }

        if (result.Image is not null && (remember || !IsCloudOnlyIcon(path, source, result)))
        {
            _cache.Set(key, result);
        }

        return result;
    }

    /// <summary>
    /// The picture of an online-only picture file that the shell gave no thumbnail for
    /// (<paramref name="icon"/> is what it gave): the shell's thumbnail on a second try when its
    /// thumbnail provider (OneDrive's) was still making it, else the file downloaded
    /// (<see cref="DownloadAsync"/>) and decoded here. The user asked for that: without a picture
    /// there is no telling which photo to open. <paramref name="icon"/> when neither works.
    /// Remember: the result is final for the session and may be cached (an icon of an online-only
    /// picture is otherwise not, so that its picture shows once it can be had).
    /// </summary>
    private async Task<(ThumbResult Result, bool Remember)> FromCloudAsync(
        string path, int sizePx, int iconPx, long length, ThumbResult icon, bool shellPending, IProgress<ThumbResult>? downloading, bool urgent, CancellationToken cancellationToken)
    {
        if (shellPending)
        {
            await Task.Delay(PendingRetryDelay, cancellationToken).ConfigureAwait(false);
            try
            {
                var image = await _pool.RunAsync(() => ShellInterop.GetShellImage(path, sizePx, ShellThumbFlags), cancellationToken, CallTimeout).ConfigureAwait(false);
                if (image is not null)
                {
                    return (new ThumbResult(image, ThumbMode.Image), true);
                }
            }
            catch (TimeoutException)
            {
            }
        }

        switch (await DownloadAsync(path, length, icon, downloading, urgent, cancellationToken).ConfigureAwait(false))
        {
            case Fetch.Never:
                return (icon, true);
            case Fetch.NotNow:
                return (icon, false);
        }

        var (picture, _) = await ProduceAsync(path, ThumbSource.File, sizePx, iconPx, downloaded: true, cancellationToken).ConfigureAwait(false);
        bool got = picture.Image is not null && picture.Mode == ThumbMode.Image;
        if (!ImageDecoder.CanDecode(path))
        {
            // HEIC, AVIF…: the shell makes their thumbnail, if Windows has their codec.
            string ext = PathTools.ExtensionOf(path);
            _extensionMisses.AddOrUpdate(ext, got ? -1 : 1, (_, n) => got || n < 0 ? -1 : n + 1);
        }

        if (!got)
        {
            _failedDownloads.TryAdd(path, 0);
        }

        return (picture.Image is not null ? picture : icon, true); // here now: the result is the file's
    }

    /// <summary>What <see cref="DownloadAsync"/> did.</summary>
    private enum Fetch
    {
        /// <summary>The file is here now.</summary>
        Downloaded,

        /// <summary>Not downloaded, and it will not be this session (refused, too large, a type Windows cannot show).</summary>
        Never,

        /// <summary>Not downloaded this time (paused, failed, took too long): maybe later.</summary>
        NotNow,
    }

    /// <summary>
    /// The decoded picture (app decoder), or else what the shell gives (its thumbnail, else the icon),
    /// and whether the shell said its thumbnail was still being made.
    /// <paramref name="downloaded"/>: the file was just downloaded on purpose (<see cref="DownloadAsync"/>),
    /// so it is read even if its attributes still say online-only.
    /// </summary>
    private async Task<(ThumbResult Result, bool ShellPending)> ProduceAsync(
        string path, ThumbSource source, int sizePx, int iconPx, bool downloaded, CancellationToken cancellationToken)
    {
        var result = ThumbResult.Empty;
        if (source != ThumbSource.Folder && ImageDecoder.CanDecode(path))
        {
            try
            {
                result = await _decoders.RunAsync(() => DecodePicture(path, sizePx, downloaded), cancellationToken, CallTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A file that does not read (dead network drive…): the shell and its own timeout decide.
            }
        }

        bool pending = false;
        if (result.Image is null)
        {
            try
            {
                result = await _pool.RunAsync(
                    () =>
                    {
                        var produced = Produce(path, source, sizePx, iconPx, out bool shellPending);
                        pending = shellPending;
                        return produced;
                    },
                    cancellationToken,
                    CallTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result = await FallbackIconAsync(path, source == ThumbSource.Folder, iconPx, cancellationToken).ConfigureAwait(false);
            }
        }

        return (result, pending);
    }

    /// <summary>
    /// Downloads an online-only picture of at most <see cref="MaxDownloadBytes"/> so that its
    /// thumbnail can be made. Only for files (cards and previews), not folder covers: a folder would
    /// download pictures it shows one of. Tells <paramref name="downloading"/> (with
    /// <paramref name="icon"/>) when it starts. A refusal (the app blocked in Windows' "Automatic file
    /// downloads", a download cancelled in Windows' notification) stops all of them for the session,
    /// so as not to ask again what the user refused; offline or with the sync app not running, they
    /// pause; a file that failed is not tried again for a card.
    /// </summary>
    private async Task<Fetch> DownloadAsync(
        string path, long length, ThumbResult icon, IProgress<ThumbResult>? downloading, bool urgent, CancellationToken cancellationToken)
    {
        if (_downloadsRefused || (length > 0 ? length : LengthOf(path)) > MaxDownloadBytes
            || (_extensionMisses.TryGetValue(PathTools.ExtensionOf(path), out int misses) && misses >= ExtensionMissesToStop))
        {
            return Fetch.Never;
        }

        if (DownloadsPaused || (!urgent && _failedDownloads.ContainsKey(path)))
        {
            return Fetch.NotNow;
        }

        var gate = urgent ? _previewDownloads : _downloads;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!FileContent.IsDownloadable(path))
            {
                return Fetch.Downloaded; // meanwhile (the card's and the preview's request for one file)
            }

            if (_downloadsRefused)
            {
                return Fetch.Never;
            }

            if (DownloadsPaused)
            {
                return Fetch.NotNow; // found unreachable while this one was waiting
            }

            downloading?.Report(icon);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DownloadTimeout);
            DownloadResult outcome;
            try
            {
                outcome = await FileContent.DownloadAsync(path, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Our time limit, or the read aborted by someone else (not by our caller).
                AppLog.Warn(timeout.IsCancellationRequested
                    ? $"Download for its thumbnail took too long: {path}"
                    : $"Download for its thumbnail was aborted: {path}");
                _failedDownloads.TryAdd(path, 0);
                return Fetch.NotNow;
            }

            switch (outcome)
            {
                case DownloadResult.Done:
                    _failedDownloads.TryRemove(path, out _);
                    return Fetch.Downloaded;
                case DownloadResult.Refused:
                    if (!_downloadsRefused)
                    {
                        _downloadsRefused = true;
                        AppLog.Warn($"Downloads of online-only files refused (blocked in Windows' settings or cancelled): none more this session. {path}");
                    }

                    return Fetch.Never;
                case DownloadResult.Unreachable:
                    Interlocked.Exchange(ref _downloadsResumeAt, Environment.TickCount64 + (long)UnreachablePause.TotalMilliseconds);
                    AppLog.Warn($"Could not download for its thumbnail (offline, or the sync app is not running): {path}");
                    return Fetch.NotNow;
                default:
                    AppLog.Warn($"Could not download for its thumbnail: {path}");
                    _failedDownloads.TryAdd(path, 0);
                    return Fetch.NotNow;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length; // metadata only: does not download
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return long.MaxValue;
        }
    }

    /// <summary>
    /// An icon for an online-only Office file or picture (OneDrive Files On-Demand): its picture was
    /// not read (an Office file's embedded cover on purpose; a picture too large, or its download
    /// failed or was stopped), so don't remember the icon — once the file is here (same time stamp and
    /// size, so the same key) the picture should show.
    /// </summary>
    private static bool IsCloudOnlyIcon(string path, ThumbSource source, ThumbResult result) =>
        source == ThumbSource.File && result.Mode == ThumbMode.Icon
        && (PreviewKinds.IsOpenXml(path) || PreviewKinds.KindOf(path) == PreviewKind.Image)
        && FileContent.IsCloudOnly(path);

    public void Clear()
    {
        _cache.Clear();
        _failedDownloads.Clear(); // F5 / settings: try the failed downloads again
    }

    public void Dispose()
    {
        _pool.Dispose();
        _decoders.Dispose();
        _disk?.Dispose();
    }

    /// <summary>
    /// A picture's thumbnail from the app's disk cache or, failing that, decoded from the file (and
    /// then stored). Empty when the app cannot make it — the shell is asked next. Runs on a decoder
    /// worker. An online-only file (OneDrive Files On-Demand) is not opened here, so it is not
    /// downloaded, unless <paramref name="downloaded"/> (<see cref="DownloadAsync"/> brought it on
    /// purpose); its thumbnail from an earlier download still comes from the disk cache.
    /// </summary>
    private ThumbResult DecodePicture(string path, int sizePx, bool downloaded)
    {
        FileInfo file;
        bool cloudOnly;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
            {
                return ThumbResult.Empty;
            }

            cloudOnly = !downloaded && FileContent.IsCloudOnly(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return ThumbResult.Empty;
        }

        // The cache key uses the file's own time stamp and size (callers do not always know them).
        // Both are metadata: an online-only file keeps them, and reading them downloads nothing.
        string? diskKey = _disk is null ? null : ThumbDiskCache.KeyOf(path, file.LastWriteTimeUtc, file.Length, sizePx);
        if (diskKey is not null && _disk!.TryRead(diskKey) is { } stored)
        {
            return new ThumbResult(stored, ThumbMode.Image);
        }

        if (cloudOnly)
        {
            return ThumbResult.Empty;
        }

        long started = Stopwatch.GetTimestamp();
        var image = ImageDecoder.Decode(path, sizePx);
        if (image is null)
        {
            return ThumbResult.Empty;
        }

        // A downloaded picture is always kept: once Windows frees it again (online-only), its
        // thumbnail comes from the cache instead of another download.
        if (diskKey is not null && (downloaded || Stopwatch.GetElapsedTime(started) >= DiskCacheMinCost))
        {
            _disk!.Save(diskKey, image);
        }

        return new ThumbResult(image, ThumbMode.Image);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageSource> _extensionIcons = new(StringComparer.OrdinalIgnoreCase);

    private const SIIGBF ShellThumbFlags = SIIGBF.SIIGBF_THUMBNAILONLY | SIIGBF.SIIGBF_BIGGERSIZEOK;

    /// <param name="pending">A file's shell thumbnail was still being made (see <see cref="FromCloudAsync"/>).</param>
    private ThumbResult Produce(string path, ThumbSource source, int sizePx, int iconPx, out bool pending)
    {
        const SIIGBF ThumbFlags = ShellThumbFlags;
        const SIIGBF IconFlags = SIIGBF.SIIGBF_ICONONLY;
        pending = false;

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
                var image = ShellInterop.GetShellImage(path, sizePx, ThumbFlags, out pending)
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
