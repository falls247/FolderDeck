using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Selection;

/// <summary>
/// The multi-selection of one item list, keyed by path: the views recreate their cards on every
/// load, so the selection cannot live on them. Keys are compared with <see cref="PathTools.Comparer"/>
/// and are not normalized; callers pass the paths the list shows.
/// <para>
/// Every mutator returns true when the set of selected keys changed and raises <see cref="Changed"/>
/// once in that case. A move of the <see cref="Anchor"/> or the <see cref="Cursor"/> alone is not a
/// change; a move of the cursor raises <see cref="CursorChanged"/> instead (also when the same call
/// changes the selection). Not thread-safe: one thread (the UI thread) owns an instance.
/// </para>
/// </summary>
public sealed class SelectionModel
{
    private HashSet<string> _keys = new(PathTools.Comparer);

    /// <summary>The set of selected keys changed.</summary>
    public event Action? Changed;

    /// <summary><see cref="Cursor"/> moved (the view redraws its frame), raised before any <see cref="Changed"/> of the same call.</summary>
    public event Action? CursorChanged;

    public int Count => _keys.Count;

    /// <summary>
    /// Where a range selection starts: the key that was toggled last, or the first key of a range
    /// that had no usable anchor. Null when there is none.
    /// </summary>
    public string? Anchor { get; private set; }

    /// <summary>The key clicked last (Ctrl+click, Shift+click), kept only while it is selected.</summary>
    private string? _focus;

    /// <summary>
    /// The item the user acted on last, as Explorer's focus (where F2 puts the name box): the key of
    /// the last Ctrl+click or Shift+click while it is still selected, else <see cref="Anchor"/>. A
    /// Shift+click moves it to the end of the range; the anchor stays at the start.
    /// </summary>
    public string? Focus => _focus is not null && _keys.Contains(_focus) ? _focus : Anchor;

    /// <summary>
    /// The item the keyboard is on, as Explorer's focus rectangle: not necessarily selected (Ctrl + arrows
    /// move it alone). Every gesture that picks an item moves it there: a click (<see cref="Replace"/>,
    /// <see cref="Toggle"/>, <see cref="SelectRange"/>) and <see cref="MoveCursor"/>. Gestures without a
    /// picked item follow the anchor (<see cref="SelectBox"/>) or keep it (<see cref="SelectAll"/>), so a
    /// following Shift + arrow spans from where the mouse left off. Null when there is none.
    /// </summary>
    public string? Cursor { get; private set; }

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
        _focus = key;
        SetCursor(key);
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

        _focus = key;
        SetCursor(key);
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
    /// otherwise it moves to the first key (null for an empty selection). The cursor goes to the
    /// anchor, so a plain click (the clicked item alone) puts it there; an empty selection keeps it.
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

        if (Anchor is not null)
        {
            SetCursor(Anchor);
        }

        return SetKeys(next);
    }

    /// <summary>A copy of the selection, its anchor and cursor (the start of a marquee drag), independent of later changes.</summary>
    public SelectionSnapshot Snapshot() => new(new HashSet<string>(_keys, PathTools.Comparer), Anchor, Cursor);

    /// <summary>
    /// A marquee drag: the selection becomes <paramref name="baseline"/> combined with the keys in
    /// the box (<paramref name="box"/>, in display order): added to it, or with
    /// <paramref name="toggle"/> (Ctrl) flipped against it. The result depends on the baseline and
    /// the box only, so the box may grow and shrink freely. The anchor stays the baseline's when it
    /// is still selected, else it is the first selected key of the box (null when none is). The
    /// cursor follows the anchor, or stays the baseline's when there is no anchor.
    /// </summary>
    public bool SelectBox(SelectionSnapshot baseline, IEnumerable<string> box, bool toggle)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        var next = new HashSet<string>(baseline.Keys, PathTools.Comparer);
        var inBox = new List<string>();
        var seen = new HashSet<string>(PathTools.Comparer);
        foreach (string key in box)
        {
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
            {
                continue;
            }

            inBox.Add(key);
            if (!toggle)
            {
                next.Add(key);
            }
            else if (!next.Remove(key))
            {
                next.Add(key);
            }
        }

        _focus = null; // a marquee has no last click: the focus is the anchor
        if (baseline.Anchor is { } anchor && next.Contains(anchor))
        {
            Anchor = anchor;
        }
        else
        {
            Anchor = inBox.FirstOrDefault(next.Contains);
        }

        SetCursor(Anchor ?? baseline.Cursor);
        return SetKeys(next);
    }

    /// <summary>Goes back to <paramref name="baseline"/> (a marquee drag cancelled with Esc), its anchor and cursor included.</summary>
    public bool Restore(SelectionSnapshot baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        Anchor = baseline.Anchor;
        _focus = null;
        SetCursor(baseline.Cursor);
        return SetKeys(new HashSet<string>(baseline.Keys, PathTools.Comparer));
    }

    /// <summary>Deselects everything and forgets the anchor and the cursor (another folder is shown).</summary>
    public bool Clear()
    {
        Anchor = null;
        _focus = null;
        SetCursor(null);
        if (_keys.Count == 0)
        {
            return false;
        }

        _keys = new HashSet<string>(PathTools.Comparer);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Drops what is no longer displayed (after a reload): selected keys, and the anchor and the
    /// cursor when they are gone too. <paramref name="cursorDisplayed"/>, when given, is what the
    /// cursor may stay on instead (every item shown: the cursor may rest on one that cannot be
    /// selected, such as a missing folder, while <paramref name="displayed"/> holds the selectable ones).
    /// </summary>
    public bool Prune(IReadOnlyList<string> displayed, IReadOnlyList<string>? cursorDisplayed = null)
    {
        if (_keys.Count == 0 && Anchor is null && Cursor is null)
        {
            return false;
        }

        HashSet<string> shown = ToSet(displayed);
        if (Anchor is not null && !shown.Contains(Anchor))
        {
            Anchor = null;
        }

        if (Cursor is not null && !(cursorDisplayed is null ? shown : ToSet(cursorDisplayed)).Contains(Cursor))
        {
            SetCursor(null);
        }

        if (_keys.RemoveWhere(key => !shown.Contains(key)) == 0)
        {
            return false;
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// The keyboard's arrows, Home, End and Page keys (the caller picks <paramref name="target"/> with
    /// <see cref="GridNavigation.Move"/>): the cursor goes to <paramref name="target"/>, and
    /// <list type="bullet">
    /// <item><paramref name="select"/> alone (plain arrow): the target alone is selected and is the anchor, as a click.</item>
    /// <item>neither (Ctrl + arrow): the selection and the anchor stay.</item>
    /// <item>
    /// <paramref name="extend"/> (Shift + arrow): the range from the anchor to the target, as a
    /// Shift + click, replacing the selection with <paramref name="select"/> and added to it without
    /// (Ctrl + Shift). Without a displayed anchor the range starts at the old cursor, else at the target.
    /// </item>
    /// </list>
    /// A target that is not displayed changes nothing. Returns true when the selection changed (a
    /// cursor-only move returns false and raises <see cref="CursorChanged"/> only).
    /// </summary>
    public bool MoveCursor(IReadOnlyList<string> displayed, string target, bool select, bool extend)
    {
        if (IndexOf(displayed, target) < 0)
        {
            return false;
        }

        if (extend)
        {
            if (IndexOf(displayed, Anchor) < 0 && IndexOf(displayed, Cursor) >= 0)
            {
                Anchor = Cursor;
            }

            return SelectRange(displayed, target, additive: !select);
        }

        if (select)
        {
            Anchor = target; // Replace keeps an anchor that stays selected: the target must become it
            return Replace([target]);
        }

        SetCursor(target);
        return false;
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

    private void SetCursor(string? key)
    {
        if (string.Equals(Cursor, key, StringComparison.Ordinal))
        {
            return;
        }

        Cursor = key;
        CursorChanged?.Invoke();
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

/// <summary>A selection at one moment (see <see cref="SelectionModel.Snapshot"/>).</summary>
public sealed record SelectionSnapshot(IReadOnlySet<string> Keys, string? Anchor, string? Cursor = null);
