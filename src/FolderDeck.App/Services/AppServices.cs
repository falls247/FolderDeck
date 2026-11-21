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

    private volatile bool _showHidden;

    public AppServices(DataStores stores)
    {
        Stores = stores;
        _showHidden = stores.Settings.Current.ShowHidden;
        Listings = new FolderListingCache(FileSystem, () => _showHidden);
    }

    public DataStores Stores { get; }

    public IFileSystem FileSystem { get; } = RealFileSystem.Instance;

    public IClock Clock { get; } = SystemClock.Instance;

    public FolderListingCache Listings { get; }

    /// <summary>Folder path → cover image path ("" = none).</summary>
    public TtlCache<string, string> Covers { get; } = new(CoverTtl, 8000, StringComparer.OrdinalIgnoreCase);

    public ThumbnailService Thumbnails { get; } = new();

    public IconCache Icons { get; } = new();

    /// <summary>Single STA worker for other shell calls (reading .lnk files).</summary>
    public StaWorkerPool Shell { get; } = new(1, "Shell");

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
    }
}
