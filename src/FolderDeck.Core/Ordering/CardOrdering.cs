using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Ordering;

/// <summary>Sorting of cards by name or modified time, with pinned cards fixed in place.</summary>
public static class CardOrdering
{
    /// <summary>Time stamps at or before this (FILETIME 0) mean "unknown".</summary>
    private static readonly DateTime FileTimeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Sorts the non-pinned items of <paramref name="baseOrder"/> with <paramref name="comparer"/>
    /// (stable: equal items keep their base order). Every pinned item keeps the index it has in
    /// <paramref name="baseOrder"/>; the sorted items fill the remaining slots.
    /// </summary>
    /// <example>[A, B📌, C, D📌, E] by name descending → [E, B📌, C, D📌, A].</example>
    public static List<T> SortKeepingPinned<T>(IReadOnlyList<T> baseOrder, Func<T, bool> isPinned, IComparer<T> comparer)
    {
        var pinnedSlots = new bool[baseOrder.Count];
        var free = new List<T>(baseOrder.Count);
        for (int i = 0; i < baseOrder.Count; i++)
        {
            if (isPinned(baseOrder[i]))
            {
                pinnedSlots[i] = true;
            }
            else
            {
                free.Add(baseOrder[i]);
            }
        }

        var sorted = free.OrderBy(item => item, comparer).ToList(); // OrderBy is stable
        var result = new List<T>(baseOrder.Count);
        int next = 0;
        for (int i = 0; i < baseOrder.Count; i++)
        {
            result.Add(pinnedSlots[i] ? baseOrder[i] : sorted[next++]);
        }

        return result;
    }

    /// <summary>
    /// The comparer of a sort mode, or null for <see cref="CardSortMode.Manual"/> (keep the base order).
    /// Without <paramref name="sizeOf"/> / <paramref name="typeOf"/> every item counts as having no
    /// size / no extension, so those modes fall back to the name.
    /// </summary>
    public static IComparer<T>? ComparerFor<T>(
        CardSortMode mode,
        Func<T, string> nameOf,
        Func<T, DateTime?> modifiedOf,
        Func<T, long?>? sizeOf = null,
        Func<T, string>? typeOf = null) => mode switch
    {
        CardSortMode.NameAsc => ByName(nameOf, descending: false),
        CardSortMode.NameDesc => ByName(nameOf, descending: true),
        CardSortMode.ModifiedAsc => ByModified(modifiedOf, descending: false),
        CardSortMode.ModifiedDesc => ByModified(modifiedOf, descending: true),
        CardSortMode.SizeAsc => BySize(sizeOf ?? (_ => null), nameOf, descending: false),
        CardSortMode.SizeDesc => BySize(sizeOf ?? (_ => null), nameOf, descending: true),
        CardSortMode.TypeAsc => ByType(typeOf ?? (_ => string.Empty), nameOf, descending: false),
        CardSortMode.TypeDesc => ByType(typeOf ?? (_ => string.Empty), nameOf, descending: true),
        _ => null,
    };

    /// <summary>Explorer's natural order (<c>StrCmpLogicalW</c>) of the display name, ordinal tie-break.</summary>
    public static IComparer<T> ByName<T>(Func<T, string> nameOf, bool descending) => Comparer<T>.Create((a, b) =>
    {
        string x = nameOf(a);
        string y = nameOf(b);
        int c = NaturalComparer.Instance.Compare(x, y);
        if (c == 0)
        {
            c = string.CompareOrdinal(x, y);
        }

        return descending ? -c : c;
    });

    /// <summary>By last write time; items whose time is unknown (null) go last in both directions.</summary>
    public static IComparer<T> ByModified<T>(Func<T, DateTime?> modifiedOf, bool descending) => Comparer<T>.Create((a, b) =>
    {
        DateTime? x = modifiedOf(a);
        DateTime? y = modifiedOf(b);
        if (x is null || y is null)
        {
            return x is null ? (y is null ? 0 : 1) : -1;
        }

        int c = x.Value.CompareTo(y.Value);
        return descending ? -c : c;
    });

    /// <summary>
    /// By size; items without one (null: folders, files not read yet) go last in both directions.
    /// Equal sizes, and the items without one, are in natural name order in the same direction:
    /// inside a folder the folders all have no size, so a fixed name order there would make the
    /// two directions look the same at the top of the list.
    /// </summary>
    public static IComparer<T> BySize<T>(Func<T, long?> sizeOf, Func<T, string> nameOf, bool descending)
    {
        var byName = ByName(nameOf, descending);
        return Comparer<T>.Create((a, b) =>
        {
            long? x = sizeOf(a);
            long? y = sizeOf(b);
            if (x is null || y is null)
            {
                return x is null ? (y is null ? byName.Compare(a, b) : 1) : -1;
            }

            int c = x.Value.CompareTo(y.Value);
            return c == 0 ? byName.Compare(a, b) : descending ? -c : c;
        });
    }

    /// <summary>
    /// By type, whose key is the extension that <paramref name="typeOf"/> returns, compared without
    /// regard to case (".JPG" and ".jpg" are one type; no extension sorts first when ascending).
    /// Items of one type (all the folders, which have no extension) are in natural name order in
    /// the same direction, so that the two directions differ for them too.
    /// </summary>
    public static IComparer<T> ByType<T>(Func<T, string> typeOf, Func<T, string> nameOf, bool descending)
    {
        var byName = ByName(nameOf, descending);
        return Comparer<T>.Create((a, b) =>
        {
            int c = string.Compare(typeOf(a), typeOf(b), StringComparison.OrdinalIgnoreCase);
            return c == 0 ? byName.Compare(a, b) : descending ? -c : c;
        });
    }

    /// <summary>Folders before files, then <paramref name="inner"/> inside each group (used inside folders).</summary>
    public static IComparer<T> FoldersFirst<T>(Func<T, bool> isFolder, IComparer<T> inner) => Comparer<T>.Create((a, b) =>
    {
        bool x = isFolder(a);
        bool y = isFolder(b);
        if (x != y)
        {
            return x ? -1 : 1;
        }

        return inner.Compare(a, b);
    });

    /// <summary>Null for an unset time stamp (<c>default</c>, or at/before 1601-01-01 as the file system reports for "none").</summary>
    public static DateTime? KnownTime(DateTime utc) => utc <= FileTimeEpoch ? null : utc;
}
