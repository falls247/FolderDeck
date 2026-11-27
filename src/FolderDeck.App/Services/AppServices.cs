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
    private volatile ListingFilter _filter;

    public AppServices(DataStores stores)
    {
        Stores = stores;
        _filter = stores.Settings.Current.ToListingFilter();
        Listings = new FolderListingCache(FileSystem, () => _filter);
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

    /// <summary>
    /// 「ログオン時に起動」 reads and writes the Run key (<see cref="Shell.RunKeyRegistry"/>). False
    /// with <c>--data-dir</c>: a test run keeps its hands off the registration of the copy in use.
    /// </summary>
    public bool ManagesRunKey { get; init; }

    /// <summary>At most 8 cards load their details at the same time (§7.4.1).</summary>
    public SemaphoreSlim DetailGate { get; } = new(8, 8);

    /// <summary>What listings show (show_hidden, show_dot_items). Changing it drops the cached listings and covers.</summary>
    public ListingFilter Filter
    {
        get => _filter;
        set
        {
            if (_filter != value)
            {
                _filter = value;
                ClearCaches();
            }
        }
    }

    /// <summary>
    /// One line for app.log: the process's working set and managed heap, what the two largest
    /// caches hold (thumbnail LRU, folder listings), and what the memory is made of
    /// (<see cref="FolderDeck.Shell.ProcessMemory"/>: private bytes, committed memory by kind, handles). Cheap:
    /// counters and one walk of the address space.
    /// </summary>
    public string DescribeMemory()
    {
        const int Mb = 1024 * 1024;
        return $"memory: ws {Environment.WorkingSet / Mb} MB, managed {GC.GetTotalMemory(forceFullCollection: false) / Mb} MB, "
            + $"thumbs {Thumbnails.CachedCount} ({Thumbnails.CachedBytes / Mb} MB), "
            + $"listings {Listings.CachedListings} ({Listings.CachedEntries} entries); "
            + FolderDeck.Shell.ProcessMemory.Describe();
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
