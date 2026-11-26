using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Stores;
using FolderDeck.Shell;

namespace FolderDeck.Services;

/// <summary>Composition root: stores, caches and shell services shared by every view model.</summary>
public sealed class AppServices : IDisposable
{
    /// <summary>Cover results live as long as folder listings (§6.4).</summary>
    public static readonly TimeSpan CoverTtl = FolderListingCache.DefaultTtl;

    /// <summary>The picture thumbnail cache, inside the data folder.</summary>
    public const string ThumbCacheFolderName = "thumbs";

    private readonly StaWorkerPool _typeNameWorker = new(1, "TypeNames");
    private volatile bool _showHidden;

    public AppServices(DataStores stores)
    {
        Stores = stores;
        _showHidden = stores.Settings.Current.ShowHidden;
        Listings = new FolderListingCache(FileSystem, () => _showHidden);
        Thumbnails = new ThumbnailService(
            Path.Combine(stores.DataDirectory, ThumbCacheFolderName),
            stores.Settings.Current.ThumbCacheMb * 1024L * 1024L);
        TypeNames = new TypeNameCache(_typeNameWorker);
    }

    public DataStores Stores { get; }

    public IFileSystem FileSystem { get; } = RealFileSystem.Instance;

    public IClock Clock { get; } = SystemClock.Instance;

    public FolderListingCache Listings { get; }

    /// <summary>Folder path → cover image path ("" = none).</summary>
    public TtlCache<string, string> Covers { get; } = new(CoverTtl, 8000, StringComparer.OrdinalIgnoreCase);

    public ThumbnailService Thumbnails { get; }

    public IconCache Icons { get; } = new();

    /// <summary>Single STA worker for other shell calls (reading .lnk files).</summary>
    public StaWorkerPool Shell { get; } = new(1, "Shell");

    /// <summary>
    /// Type names for the 種類 column of the details list. On a worker of its own: <see cref="Shell"/>
    /// can be busy for seconds (reading the Windows recent items), and the names would wait behind it.
    /// </summary>
    public TypeNameCache TypeNames { get; }

    /// <summary>At most 8 cards load their details at the same time (§7.4.1).</summary>
    public SemaphoreSlim DetailGate { get; } = new(8, 8);

    public bool ShowHidden
    {
        get => _showHidden;
        set
        {
            if (_showHidden != value)
            {
                _showHidden = value;
                ClearCaches();
            }
        }
    }

    /// <summary>
    /// One line for app.log: the process's working set and managed heap, and what the two largest
    /// caches hold (thumbnail LRU, folder listings). Cheap: counters only, no walk.
    /// </summary>
    public string DescribeMemory()
    {
        const int Mb = 1024 * 1024;
        return $"memory: ws {Environment.WorkingSet / Mb} MB, managed {GC.GetTotalMemory(forceFullCollection: false) / Mb} MB, "
            + $"thumbs {Thumbnails.CachedCount} ({Thumbnails.CachedBytes / Mb} MB), "
            + $"listings {Listings.CachedListings} ({Listings.CachedEntries} entries)";
    }

    public void ClearCaches()
    {
        Listings.Clear();
        Covers.Clear();
    }

    public void Dispose()
    {
        Thumbnails.Dispose();
        Icons.Dispose();
        Shell.Dispose();
        _typeNameWorker.Dispose();
    }
}
