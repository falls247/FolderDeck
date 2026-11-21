using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Listing;

public enum ListingStatus
{
    Ok,
    AccessDenied,
    NotFound,
    Error,
}

/// <summary>Sorted direct children of one folder (folders first, then files, natural order).</summary>
public sealed class FolderListing
{
    public FolderListing(string path, IReadOnlyList<FsEntry> entries)
    {
        Path = path;
        Status = ListingStatus.Ok;
        Entries = entries;
        int folders = 0;
        foreach (var e in entries)
        {
            if (e.IsDirectory)
            {
                folders++;
            }
        }

        FolderCount = folders;
        FileCount = entries.Count - folders;
    }

    private FolderListing(string path, ListingStatus status, string? detail)
    {
        Path = path;
        Status = status;
        ErrorDetail = detail;
        Entries = [];
    }

    public string Path { get; }

    public ListingStatus Status { get; }

    public string? ErrorDetail { get; }

    public IReadOnlyList<FsEntry> Entries { get; }

    public int FolderCount { get; }

    public int FileCount { get; }

    public bool IsOk => Status == ListingStatus.Ok;

    /// <summary>User-facing text for an error status (§7.5).</summary>
    public string StatusMessage => Status switch
    {
        ListingStatus.Ok => string.Empty,
        ListingStatus.AccessDenied => "アクセスが拒否されました",
        ListingStatus.NotFound => "見つかりません",
        _ => "読み込めません：" + (ErrorDetail ?? string.Empty),
    };

    public static FolderListing Failed(string path, ListingStatus status, string? detail = null) =>
        new(path, status, detail);
}

public static class FolderLister
{
    /// <summary>Folders first, then files; natural order (StrCmpLogicalW) inside each group.</summary>
    public static readonly IComparer<FsEntry> EntryComparer = Comparer<FsEntry>.Create((a, b) =>
    {
        if (a.IsDirectory != b.IsDirectory)
        {
            return a.IsDirectory ? -1 : 1;
        }

        int c = NaturalComparer.Instance.Compare(a.Name, b.Name);
        return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
    });

    public static void Sort(List<FsEntry> entries) => entries.Sort(EntryComparer);

    /// <summary>Lists and sorts <paramref name="path"/>. Errors become a failed listing; cancellation throws.</summary>
    public static FolderListing List(IFileSystem fs, string path, bool includeHidden, CancellationToken cancellationToken)
    {
        try
        {
            var raw = fs.EnumerateDirectory(path, includeHidden, cancellationToken);
            var list = raw as List<FsEntry> ?? new List<FsEntry>(raw);
            Sort(list);
            cancellationToken.ThrowIfCancellationRequested();
            return new FolderListing(path, list);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderListing.Failed(path, ListingStatus.AccessDenied);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or FileNotFoundException)
        {
            return FolderListing.Failed(path, ListingStatus.NotFound);
        }
        catch (IOException ex) when (IsNotFound(ex))
        {
            return FolderListing.Failed(path, ListingStatus.NotFound);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return FolderListing.Failed(path, ListingStatus.Error, ex.Message);
        }
    }

    private static bool IsNotFound(IOException ex)
    {
        // ERROR_FILE_NOT_FOUND, ERROR_PATH_NOT_FOUND, ERROR_BAD_NETPATH, ERROR_BAD_NET_NAME, ERROR_DIRECTORY (not a folder)
        int code = ex.HResult & 0xFFFF;
        return code is 2 or 3 or 53 or 67 or 267;
    }
}
