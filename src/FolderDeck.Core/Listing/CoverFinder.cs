using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Listing;

/// <summary>
/// Cover image of a folder card (§6.4): the first image directly inside, otherwise the first image
/// found depth-first through sub folders (natural order). Reparse points are not followed.
/// </summary>
public static class CoverFinder
{
    /// <summary>The folder itself is depth 0; sub folders are searched down to this depth.</summary>
    public const int MaxDepth = 6;

    /// <summary>At most this many folders (including the start folder) are listed per card.</summary>
    public const int MaxFolders = 64;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp",
    };

    public static bool IsImageName(string name) => ImageExtensions.Contains(Path.GetExtension(name));

    /// <summary>
    /// Returns the cover image's full path, or null. <paramref name="rootEntries"/> may pass the
    /// already listed (sorted) children of <paramref name="folder"/> to avoid a second listing.
    /// </summary>
    public static string? Find(
        IFileSystem fs,
        string folder,
        IReadOnlyList<FsEntry>? rootEntries,
        bool includeHidden,
        CancellationToken cancellationToken,
        int maxDepth = MaxDepth,
        int maxFolders = MaxFolders)
    {
        int budget = maxFolders;
        return Search(fs, folder, rootEntries, includeHidden, maxDepth, ref budget, cancellationToken);
    }

    private static string? Search(
        IFileSystem fs,
        string folder,
        IReadOnlyList<FsEntry>? entries,
        bool includeHidden,
        int depthLeft,
        ref int budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (budget <= 0)
        {
            return null;
        }

        budget--;
        if (entries is null)
        {
            var listing = FolderLister.List(fs, folder, includeHidden, cancellationToken);
            if (!listing.IsOk)
            {
                return null;
            }

            entries = listing.Entries;
        }

        foreach (var e in entries)
        {
            if (!e.IsDirectory && IsImageName(e.Name))
            {
                return e.FullPath;
            }
        }

        if (depthLeft <= 0)
        {
            return null;
        }

        foreach (var e in entries)
        {
            if (!e.IsDirectory || e.IsReparsePoint)
            {
                continue;
            }

            if (budget <= 0)
            {
                return null;
            }

            string? found = Search(fs, e.FullPath, null, includeHidden, depthLeft - 1, ref budget, cancellationToken);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Natural-order helper for callers that hold unsorted names.</summary>
    public static IOrderedEnumerable<string> OrderNatural(IEnumerable<string> names) =>
        names.OrderBy(n => n, NaturalComparer.Instance);
}
