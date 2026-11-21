using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Ordering;

/// <summary>Applies a saved drag &amp; drop order to a freshly built list.</summary>
public static class ManualOrder
{
    /// <summary>
    /// Items whose key is in <paramref name="saved"/> keep the saved relative order. An item that is
    /// not saved (new) goes right after the item that precedes it in <paramref name="defaultOrder"/>
    /// (at the very front when nothing precedes it); consecutive new items keep their default order.
    /// Saved keys that match nothing are ignored. Keys are compared case-insensitively.
    /// </summary>
    /// <example>default [n1, a, n2, b, c], saved [c, a, b] → [n1, c, a, n2, b].</example>
    public static List<T> Apply<T>(IReadOnlyList<T> defaultOrder, IReadOnlyList<string> saved, Func<T, string> keyOf)
    {
        var rank = new Dictionary<string, int>(PathTools.Comparer);
        for (int i = 0; i < saved.Count; i++)
        {
            if (saved[i] is { } key)
            {
                rank.TryAdd(key, i);
            }
        }

        // Each saved item carries the run of new items that follows it in the default order.
        var leading = new List<T>();
        var groups = new List<(int Rank, int Position, List<T> Items)>();
        List<T> current = leading;
        for (int i = 0; i < defaultOrder.Count; i++)
        {
            var item = defaultOrder[i];
            if (rank.TryGetValue(keyOf(item), out int r))
            {
                current = [item];
                groups.Add((r, i, current));
            }
            else
            {
                current.Add(item);
            }
        }

        if (groups.Count == 0)
        {
            return [.. defaultOrder];
        }

        groups.Sort((x, y) => x.Rank != y.Rank ? x.Rank.CompareTo(y.Rank) : x.Position.CompareTo(y.Position));
        var result = new List<T>(defaultOrder.Count);
        result.AddRange(leading);
        foreach (var group in groups)
        {
            result.AddRange(group.Items);
        }

        return result;
    }
}
