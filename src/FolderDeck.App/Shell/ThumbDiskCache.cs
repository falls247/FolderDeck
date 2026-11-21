using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Shell;

/// <summary>
/// The thumbnails <see cref="ImageDecoder"/> made, kept on disk (<c>thumbs</c> in the data folder)
/// so that a folder seen before shows at once — also after a restart, and without reading the
/// pictures again (which is what takes long on a hard disk). One small JPEG per picture (PNG when it
/// is transparent), named by a hash of path, time stamp, size and thumbnail size: a changed picture
/// simply gets a new file. Bounded by bytes; the least recently used files go first.
/// Reading runs on the caller's (decoder) thread; writing, touching and trimming on one background thread.
/// </summary>
internal sealed class ThumbDiskCache : IDisposable
{
    /// <summary>Part of every key: raise it when the stored pictures change (size rule, quality…).</summary>
    private const string Version = "1";

    private const int JpegQuality = 85;

    /// <summary>Writes waiting at once; above this new thumbnails are simply not stored.</summary>
    private const int MaxPending = 256;

    /// <summary>A file used again after this long gets a fresh time stamp ("recently used").</summary>
    private static readonly TimeSpan TouchAfter = TimeSpan.FromDays(3);

    private static readonly TimeSpan FirstTrimDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan StaleTemp = TimeSpan.FromHours(1);

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly StaWorkerPool _writer = new(1, "ThumbCache", ThreadPriority.Lowest);
    private long _writtenSinceTrim;
    private int _pending;
    private int _warned;

    public ThumbDiskCache(string root, long maxBytes)
    {
        _root = root;
        _maxBytes = maxBytes;
        _ = Task.Delay(FirstTrimDelay).ContinueWith(_ => Queue(Trim), TaskScheduler.Default);
    }

    public static string KeyOf(string path, DateTime modifiedUtc, long length, int sizePx) =>
        $"{Version}|{path.ToUpperInvariant()}|{modifiedUtc.Ticks}|{length}|{sizePx}";

    /// <summary>The stored thumbnail, or null (not stored, unreadable). Never throws.</summary>
    public BitmapSource? TryRead(string key)
    {
        string file = FileOf(key);
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32 * 1024, FileOptions.SequentialScan);
            var image = ImageDecoder.Decode(stream, int.MaxValue);
            if (image is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(stream.SafeFileHandle) > TouchAfter)
            {
                Queue(() => Touch(file));
            }

            return image;
        }
        catch (Exception)
        {
            return null; // damaged or locked: it is made (and stored) again
        }
    }

    /// <summary>Stores <paramref name="image"/> (frozen) in the background.</summary>
    public void Save(string key, BitmapSource image) => Queue(() => Write(FileOf(key), image));

    public void Dispose() => _writer.Dispose();

    private string FileOf(string key) => Path.Combine(_root, DiskCacheRules.RelativePathOf(key));

    private void Queue(Action work)
    {
        if (Interlocked.Increment(ref _pending) > MaxPending)
        {
            Interlocked.Decrement(ref _pending);
            return;
        }

        try
        {
            _ = _writer.RunAsync(
                () =>
                {
                    try
                    {
                        work();
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }

                    return true;
                },
                CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            Interlocked.Decrement(ref _pending); // the app is closing
        }
    }

    private void Write(string file, BitmapSource image)
    {
        string temp = file + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            BitmapEncoder encoder = image.Format == PixelFormats.Bgr32
                ? new JpegBitmapEncoder { QualityLevel = JpegQuality }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            long bytes;
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(stream);
                bytes = stream.Length;
            }

            File.Move(temp, file, overwrite: true);
            if (Interlocked.Add(ref _writtenSinceTrim, bytes) > _maxBytes / 8)
            {
                Trim();
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            WarnOnce("Could not store a thumbnail", ex);
            TryDelete(temp);
        }
    }

    private static void Touch(string file)
    {
        try
        {
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // Trimmed meanwhile, or read-only: it only ages a little sooner.
        }
    }

    /// <summary>
    /// Deletes the least recently used files when the cache is over its limit, and temporary files a
    /// crash left behind. Only files this cache names (<see cref="DiskCacheRules.IsCacheFileName"/>)
    /// in its own two-character sub folders are ever touched.
    /// </summary>
    private void Trim()
    {
        Interlocked.Exchange(ref _writtenSinceTrim, 0);
        try
        {
            var root = new DirectoryInfo(_root);
            if (!root.Exists)
            {
                return;
            }

            var files = new List<CacheFile>();
            var now = DateTime.UtcNow;
            foreach (var folder in root.EnumerateDirectories())
            {
                if (folder.Name.Length != 2 || (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in folder.EnumerateFiles())
                {
                    if (DiskCacheRules.IsCacheFileName(file.Name))
                    {
                        files.Add(new CacheFile(file.FullName, file.Length, file.LastWriteTimeUtc));
                    }
                    else if (file.Name.EndsWith(".tmp", StringComparison.Ordinal) && DiskCacheRules.IsCacheFileName(file.Name[..^4])
                             && now - file.LastWriteTimeUtc > StaleTemp)
                    {
                        TryDelete(file.FullName);
                    }
                }
            }

            var removed = DiskCacheRules.SelectForRemoval(files, _maxBytes);
            foreach (var file in removed)
            {
                TryDelete(file.Path);
            }

            if (removed.Count > 0)
            {
                AppLog.Info($"Thumbnail cache trimmed: {removed.Count} of {files.Count} files removed.");
            }
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            WarnOnce("Could not trim the thumbnail cache", ex);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (IsStorageFailure(ex))
        {
            // In use or gone: the next trim sees it again.
        }
    }

    private void WarnOnce(string message, Exception ex)
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            AppLog.Warn($"{message} ({_root}); further failures are not logged.", ex);
        }
    }

    private static bool IsStorageFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException
            or ArgumentException or COMException or System.Security.SecurityException;
}
