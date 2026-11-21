namespace FolderDeck.Core.Ordering;

/// <summary>Which items the top level of 最近の履歴 / よく使う項目 shows (toolbar, left of the sort button).</summary>
public enum ItemFilter
{
    All,
    Folders,
    Files,
}

public static class ItemFilters
{
    public const string All = "all";
    public const string Folders = "folders";
    public const string Files = "files";

    /// <summary>Every filter, in menu order.</summary>
    public static IReadOnlyList<ItemFilter> Values { get; } = [ItemFilter.All, ItemFilter.Folders, ItemFilter.Files];

    public static string ToSetting(ItemFilter filter) => filter switch
    {
        ItemFilter.Folders => Folders,
        ItemFilter.Files => Files,
        _ => All,
    };

    /// <summary>Missing or unknown values are <see cref="ItemFilter.All"/>.</summary>
    public static ItemFilter FromSetting(string? value) => value switch
    {
        Folders => ItemFilter.Folders,
        Files => ItemFilter.Files,
        _ => ItemFilter.All,
    };

    public static bool Matches(ItemFilter filter, bool isFolder) => filter switch
    {
        ItemFilter.Folders => isFolder,
        ItemFilter.Files => !isFolder,
        _ => true,
    };

    /// <summary>The items the filter shows, in order (<paramref name="items"/> itself for <see cref="ItemFilter.All"/>).</summary>
    public static IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, ItemFilter filter, Func<T, bool> isFolder)
    {
        if (filter == ItemFilter.All)
        {
            return items;
        }

        var shown = new List<T>(items.Count);
        foreach (var item in items)
        {
            if (Matches(filter, isFolder(item)))
            {
                shown.Add(item);
            }
        }

        return shown;
    }

    /// <summary>
    /// The shown items of a filtered list were dragged into a new order: returns the whole list with
    /// every hidden item on its slot and the slots of the shown ones refilled from
    /// <paramref name="shownOrder"/> (the same shown items, reordered). When the two disagree (an
    /// item is missing or extra), <paramref name="all"/> is returned unchanged.
    /// </summary>
    public static IReadOnlyList<T> MergeShownOrder<T>(IReadOnlyList<T> all, IReadOnlyList<T> shownOrder, Func<T, bool> isShown)
    {
        int shownCount = 0;
        foreach (var item in all)
        {
            if (isShown(item))
            {
                shownCount++;
            }
        }

        if (shownCount != shownOrder.Count)
        {
            return all;
        }

        var merged = new List<T>(all.Count);
        int next = 0;
        foreach (var item in all)
        {
            merged.Add(isShown(item) ? shownOrder[next++] : item);
        }

        return merged;
    }
}
