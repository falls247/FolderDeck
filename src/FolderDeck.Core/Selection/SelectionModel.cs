using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Selection;

/// <summary>
/// The multi-selection of one item list, keyed by path: the views recreate their cards on every
/// load, so the selection cannot live on them. Keys are compared with <see cref="PathTools.Comparer"/>
/// and are not normalized; callers pass the paths the list shows.
/// <para>
/// Every mutator returns true when the set of selected keys changed and raises <see cref="Changed"/>
/// once in that case. A move of the <see cref="Anchor"/> alone is not a change. Not thread-safe:
/// one thread (the UI thread) owns an instance.
/// </para>
/// </summary>
public sealed class SelectionModel
{
    private HashSet<string> _keys = new(PathTools.Comparer);

    /// <summary>The set of selected keys changed.</summary>
    public event Action? Changed;

    public int Count => _keys.Count;

    /// <summary>
    /// Where a range selection starts: the key that was toggled last, or the first key of a range
    /// that had no usable anchor. Null when there is none.
    /// </summary>
    public string? Anchor { get; private set; }

    public bool Contains(string key) => !string.IsNullOrEmpty(key) && _keys.Contains(key);

    /// <summary>
    /// The selected keys in the order of <paramref name="displayed"/> (and in its spelling).
    /// Selected keys that are not displayed are left out.
    /// </summary>
    public IReadOnlyList<string> InOrder(IReadOnlyList<string> displayed)
    {
        if (_keys.Count == 0)
        {
            return [];
        }

        var result = new List<string>(Math.Min(_keys.Count, displayed.Count));
        var seen = new HashSet<string>(PathTools.Comparer);
        foreach (string key in displayed)
        {
            if (Contains(key) && seen.Add(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    /// <summary>Ctrl+click: flips <paramref name="key"/> and makes it the anchor.</summary>
    public bool Toggle(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!_keys.Remove(key))
        {
            _keys.Add(key);
        }

        Anchor = key;
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Shift+click: selects everything from the anchor to <paramref name="key"/> in either
    /// direction. The anchor stays where it is, so a second Shift+click re-spans from the same
    /// item; without an anchor in <paramref name="displayed"/> the key becomes the anchor and is
    /// selected alone. <paramref name="additive"/> (Ctrl+Shift) keeps the rest of the selection,
    /// otherwise the range replaces it. A key that is not displayed changes nothing.
    /// </summary>
    public bool SelectRange(IReadOnlyList<string> displayed, string key, bool additive)
    {
        int end = IndexOf(displayed, key);
        if (end < 0)
        {
            return false;
        }

        int start = IndexOf(displayed, Anchor);
        if (start < 0)
        {
            Anchor = key;
            start = end;
        }

        int first = Math.Min(start, end);
        int last = Math.Max(start, end);
        var next = additive ? new HashSet<string>(_keys, PathTools.Comparer) : new HashSet<string>(PathTools.Comparer);
        for (int i = first; i <= last; i++)
        {
            if (!string.IsNullOrEmpty(displayed[i]))
            {
                next.Add(displayed[i]);
            }
        }

        return SetKeys(next);
    }

    /// <summary>Ctrl+A: selects exactly the displayed keys. The anchor is kept.</summary>
    public bool SelectAll(IReadOnlyList<string> displayed) => SetKeys(ToSet(displayed));

    /// <summary>
    /// Selects exactly <paramref name="keys"/>. The anchor is kept when it is one of them,
    /// otherwise it moves to the first key (null for an empty selection).
    /// </summary>
    public bool Replace(IEnumerable<string> keys)
    {
        string? firstKey = null;
        var next = new HashSet<string>(PathTools.Comparer);
        foreach (string key in keys)
        {
            if (!string.IsNullOrEmpty(key) && next.Add(key))
            {
                firstKey ??= key;
            }
        }

        if (Anchor is null || !next.Contains(Anchor))
        {
            Anchor = firstKey;
        }

        return SetKeys(next);
    }

    /// <summary>Deselects everything and forgets the anchor.</summary>
    public bool Clear()
    {
        Anchor = null;
        if (_keys.Count == 0)
        {
            return false;
        }

        _keys = new HashSet<string>(PathTools.Comparer);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Drops what is no longer displayed (after a reload): selected keys, and the anchor when it
    /// is gone too.
    /// </summary>
    public bool Prune(IReadOnlyList<string> displayed)
    {
        if (_keys.Count == 0 && Anchor is null)
        {
            return false;
        }

        HashSet<string> shown = ToSet(displayed);
        if (Anchor is not null && !shown.Contains(Anchor))
        {
            Anchor = null;
        }

        if (_keys.RemoveWhere(key => !shown.Contains(key)) == 0)
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// What an action on <paramref name="clicked"/> applies to (drag, context menu): the whole
    /// selection when the clicked item is part of it, otherwise the clicked item alone.
    /// </summary>
    public static IReadOnlyList<string> TargetsOf(IReadOnlyList<string> selectedInOrder, string clicked)
    {
        foreach (string key in selectedInOrder)
        {
            if (PathTools.Comparer.Equals(key, clicked))
            {
                return selectedInOrder;
            }
        }

        return [clicked];
    }

    private bool SetKeys(HashSet<string> next)
    {
        if (_keys.SetEquals(next))
        {
            return false;
        }

        _keys = next;
        Changed?.Invoke();
        return true;
    }

    private static HashSet<string> ToSet(IReadOnlyList<string> keys)
    {
        var set = new HashSet<string>(PathTools.Comparer);
        foreach (string key in keys)
        {
            if (!string.IsNullOrEmpty(key))
            {
                set.Add(key);
            }
        }

        return set;
    }

    private static int IndexOf(IReadOnlyList<string> displayed, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return -1;
        }

        for (int i = 0; i < displayed.Count; i++)
        {
            if (PathTools.Comparer.Equals(displayed[i], key))
            {
                return i;
            }
        }

        return -1;
    }
}
