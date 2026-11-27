namespace FolderDeck.Core.Selection;

/// <summary>The keys that move the keyboard cursor of an item list.</summary>
public enum NavKey { Left, Right, Up, Down, Home, End, PageUp, PageDown }

/// <summary>
/// Where the keyboard cursor of an item list goes, by index into the displayed items. The list is
/// laid out row by row, <c>columns</c> items wide (1 for the details view), so only the last row
/// may be partial. Pure: the view passes its column count and how many rows fill one screen.
/// </summary>
public static class GridNavigation
{
    /// <summary>
    /// The index to move to from <paramref name="current"/>, or -1 for an empty list. Without a
    /// cursor yet (<paramref name="current"/> &lt; 0) every key starts at the first item, except End
    /// at the last.
    /// <list type="bullet">
    /// <item>Left / Right: the previous / next item, stopping at the ends (they wrap across rows).</item>
    /// <item>Up: the item above; on the first row it stays.</item>
    /// <item>
    /// Down: the item below. When the row below exists but is too short to reach this column
    /// (the partial last row), it goes to the last item, as Explorer does; on the last row it stays.
    /// </item>
    /// <item>
    /// PageUp / PageDown: <paramref name="pageRows"/> rows up / down in the same column. Past the
    /// edge it stops at this column's first / last item; when that is where it already is, it goes
    /// to the first / last item of the list.
    /// </item>
    /// <item>Home / End: the first / last item.</item>
    /// </list>
    /// </summary>
    public static int Move(int current, NavKey key, int count, int columns, int pageRows)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (current < 0)
        {
            return key == NavKey.End ? count - 1 : 0;
        }

        int last = count - 1;
        current = Math.Min(current, last); // a stale cursor past a shrunk list counts as the last item
        columns = Math.Max(1, columns);
        int page = columns * Math.Max(1, pageRows);
        int column = current % columns;
        switch (key)
        {
            case NavKey.Left:
                return Math.Max(0, current - 1);
            case NavKey.Right:
                return Math.Min(last, current + 1);
            case NavKey.Up:
                return current - columns >= 0 ? current - columns : current;
            case NavKey.Down:
                if (current + columns <= last)
                {
                    return current + columns;
                }

                return current / columns < last / columns ? last : current;
            case NavKey.PageUp:
                if (current - page >= 0)
                {
                    return current - page;
                }

                return column != current ? column : 0;
            case NavKey.PageDown:
                if (current + page <= last)
                {
                    return current + page;
                }

                int lastInColumn = (last - column) / columns * columns + column;
                return lastInColumn != current ? lastInColumn : last;
            case NavKey.Home:
                return 0;
            default:
                return last;
        }
    }

    /// <summary>
    /// Type-ahead: the index of the next name that starts with <paramref name="typed"/> (ignoring
    /// case), wrapping past the end; -1 when none does. One character searches after
    /// <paramref name="current"/>, so pressing the same letter again cycles through its items. A
    /// longer text (keys typed in quick succession) searches from <paramref name="current"/> itself,
    /// so the item stays while it still matches. A repeated single letter ("ss") that no name starts
    /// with cycles like the letter alone.
    /// </summary>
    public static int FindByPrefix(IReadOnlyList<string> names, int current, string typed)
    {
        if (names.Count == 0 || string.IsNullOrEmpty(typed))
        {
            return -1;
        }

        bool single = typed.Length == 1;
        int found = Find(names, single ? current + 1 : Math.Max(0, current), typed);
        if (found < 0 && !single && typed.All(c => c == typed[0]))
        {
            found = Find(names, current + 1, typed[..1]);
        }

        return found;
    }

    private static int Find(IReadOnlyList<string> names, int start, string prefix)
    {
        int count = names.Count;
        start = ((start % count) + count) % count;
        for (int step = 0; step < count; step++)
        {
            int i = (start + step) % count;
            string name = names[i];
            if (!string.IsNullOrEmpty(name) && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
