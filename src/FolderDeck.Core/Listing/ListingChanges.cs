using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Listing;

/// <summary>
/// What changed between the listing a folder tab shows and a fresh one (auto refresh).
/// <see cref="Reusable"/>: full paths whose entry is unchanged (their cards may be kept);
/// <see cref="ChangedFolders"/>: subfolders whose own write time changed (their counts and cover
/// are read again).
/// </summary>
public sealed record ListingDelta(
    IReadOnlySet<string> Reusable,
    IReadOnlyList<string> ChangedFolders,
    int Added,
    int Removed,
    int Changed)
{
    /// <summary>Nothing was added, removed or changed: the list on screen stays as it is.</summary>
    public bool IsEmpty => Added == 0 && Removed == 0 && Changed == 0;
}

/// <summary>Compares two listings of one folder, from the entries only (no I/O).</summary>
public static class ListingChanges
{
    /// <summary>
    /// The same entry as far as the list shows it: the same full path and name (ordinal, so a
    /// rename that changes only the case counts as a change), kind, write time and, for files,
    /// length. Attributes and the reparse flag are ignored: the cloud-state bits of an online-only
    /// file flip when its thumbnail downloads it, and comparing them would reload the list again
    /// and again by itself.
    /// </summary>
    public static bool SameEntry(in FsEntry a, in FsEntry b) =>
        string.Equals(a.FullPath, b.FullPath, StringComparison.Ordinal)
        && string.Equals(a.Name, b.Name, StringComparison.Ordinal)
        && a.IsDirectory == b.IsDirectory
        && a.LastWriteTimeUtc == b.LastWriteTimeUtc
        && (a.IsDirectory || a.Length == b.Length);

    /// <summary>
    /// The difference from <paramref name="shown"/> to <paramref name="fresh"/>. An entry with the
    /// same full path that differs counts as Changed (a subfolder among them is in
    /// ChangedFolders); a path only in the fresh listing is Added, one only in the shown listing
    /// Removed.
    /// </summary>
    public static ListingDelta Compare(IReadOnlyList<FsEntry> shown, IReadOnlyList<FsEntry> fresh)
    {
        ArgumentNullException.ThrowIfNull(shown);
        ArgumentNullException.ThrowIfNull(fresh);

        // Both are sorted the same way: an unchanged folder compares position by position.
        if (shown.Count == fresh.Count)
        {
            bool same = true;
            for (int i = 0; i < shown.Count && same; i++)
            {
                same = SameEntry(shown[i], fresh[i]);
            }

            if (same)
            {
                var all = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in fresh)
                {
                    all.Add(entry.FullPath);
                }

                return new ListingDelta(all, [], 0, 0, 0);
            }
        }

        var before = new Dictionary<string, FsEntry>(shown.Count, StringComparer.Ordinal);
        foreach (var entry in shown)
        {
            before.TryAdd(entry.FullPath, entry);
        }

        var reusable = new HashSet<string>(StringComparer.Ordinal);
        var changedFolders = new List<string>();
        int added = 0, changed = 0, matched = 0;
        foreach (var entry in fresh)
        {
            if (!before.TryGetValue(entry.FullPath, out var old))
            {
                added++;
                continue;
            }

            matched++;
            if (SameEntry(old, entry))
            {
                reusable.Add(entry.FullPath);
                continue;
            }

            changed++;
            if (old.IsDirectory && entry.IsDirectory)
            {
                changedFolders.Add(entry.FullPath);
            }
        }

        return new ListingDelta(reusable, changedFolders, added, before.Count - matched, changed);
    }
}
