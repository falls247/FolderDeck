using FolderDeck.Core.Paths;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Ranking;

public sealed record FrequentEntry(string Path, ItemKind Kind, bool IsPinned, double Score, DateTimeOffset? LastUsed);

public sealed record FrequentRanking(IReadOnlyList<FrequentEntry> Pinned, IReadOnlyList<FrequentEntry> Ranked);

/// <summary>Frequency × recency score of "よく使う項目" (§10.3).</summary>
public static class FrequentRanker
{
    /// <summary>Weight by age in days (boundaries are inclusive).</summary>
    public static double Weight(double ageDays) => ageDays switch
    {
        <= 4 => 100,
        <= 14 => 70,
        <= 31 => 50,
        <= 90 => 30,
        _ => 10,
    };

    /// <summary><c>(open_count / len(recent_opens)) × Σ w(now − t)</c>; 0 when there are no recent opens.</summary>
    public static double Score(HistoryItem item, DateTimeOffset now)
    {
        if (item.RecentOpens.Count == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var t in item.RecentOpens)
        {
            sum += Weight((now - t).TotalDays);
        }

        return (double)item.OpenCount / item.RecentOpens.Count * sum;
    }

    /// <summary>
    /// Pins in pinning order, then every unpinned record by score (ties: newer
    /// <c>last_opened_at</c> first, then natural path order). Limits and existence are applied by
    /// the caller.
    /// </summary>
    public static FrequentRanking Rank(IEnumerable<HistoryItem> history, IEnumerable<PinItem> pins, DateTimeOffset now)
    {
        var historyByPath = new Dictionary<string, HistoryItem>(PathTools.Comparer);
        foreach (var h in history)
        {
            historyByPath.TryAdd(h.Path, h);
        }

        var pinned = new List<FrequentEntry>();
        var pinnedPaths = new HashSet<string>(PathTools.Comparer);
        foreach (var p in pins.OrderBy(p => p.PinnedAt))
        {
            if (!pinnedPaths.Add(p.Path))
            {
                continue;
            }

            historyByPath.TryGetValue(p.Path, out var h);
            pinned.Add(new FrequentEntry(p.Path, h?.Kind ?? p.Kind, true, h is null ? 0 : Score(h, now), h?.LastOpenedAt));
        }

        var ranked = historyByPath.Values
            .Where(h => !pinnedPaths.Contains(h.Path))
            .Select(h => new FrequentEntry(h.Path, h.Kind, false, Score(h, now), h.LastOpenedAt))
            .OrderByDescending(e => e.Score)
            .ThenByDescending(e => e.LastUsed)
            .ThenBy(e => e.Path, NaturalComparer.Instance)
            .ToList();

        return new FrequentRanking(pinned, ranked);
    }
}
