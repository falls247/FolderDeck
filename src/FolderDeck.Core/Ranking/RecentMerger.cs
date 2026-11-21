using FolderDeck.Core.Paths;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Ranking;

[Flags]
public enum RecentOrigin
{
    None = 0,
    Windows = 1,
    App = 2,
}

/// <summary>One item read from <c>%APPDATA%\Microsoft\Windows\Recent\*.lnk</c>.</summary>
public sealed record WindowsRecentItem(string TargetPath, DateTimeOffset LastUsed);

public sealed record RecentCandidate(string Path, DateTimeOffset LastUsed, ItemKind? Kind, RecentOrigin Origin);

/// <summary>Merges Windows' recent items with the app's usage records (§10.2).</summary>
public static class RecentMerger
{
    /// <summary>
    /// Deduplicates by normalized path (case-insensitive, newest time wins), drops excluded and
    /// cleared items, and sorts newest first. Existence is checked separately.
    /// </summary>
    public static List<RecentCandidate> Merge(
        IEnumerable<WindowsRecentItem> windowsItems,
        IEnumerable<HistoryItem> history,
        IEnumerable<ExcludedItem> excluded,
        DateTimeOffset? clearedAt)
    {
        var merged = new Dictionary<string, RecentCandidate>(PathTools.Comparer);

        void Add(string rawPath, DateTimeOffset at, ItemKind? kind, RecentOrigin origin)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || !PathTools.IsFullyQualified(rawPath))
            {
                return;
            }

            string path = PathTools.NormalizeOrSelf(rawPath);
            if (merged.TryGetValue(path, out var existing))
            {
                merged[path] = new RecentCandidate(
                    existing.Path,
                    at > existing.LastUsed ? at : existing.LastUsed,
                    existing.Kind ?? kind,
                    existing.Origin | origin);
            }
            else
            {
                merged[path] = new RecentCandidate(path, at, kind, origin);
            }
        }

        foreach (var h in history)
        {
            Add(h.Path, h.LastOpenedAt, h.Kind, RecentOrigin.App);
        }

        foreach (var w in windowsItems)
        {
            Add(w.TargetPath, w.LastUsed, null, RecentOrigin.Windows);
        }

        var excludedAt = new Dictionary<string, DateTimeOffset>(PathTools.Comparer);
        foreach (var e in excluded)
        {
            string key = PathTools.NormalizeOrSelf(e.Path);
            if (!excludedAt.TryGetValue(key, out var t) || e.ExcludedAt > t)
            {
                excludedAt[key] = e.ExcludedAt;
            }
        }

        return merged.Values
            .Where(c => clearedAt is null || c.LastUsed > clearedAt.Value)
            .Where(c => !excludedAt.TryGetValue(c.Path, out var t) || c.LastUsed > t)
            .OrderByDescending(c => c.LastUsed)
            .ThenBy(c => c.Path, NaturalComparer.Instance)
            .ToList();
    }
}
