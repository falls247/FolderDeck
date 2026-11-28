namespace FolderDeck.Core.Ordering;

/// <summary>
/// The tabs closed in this session, newest last, each with the place it had on the strip, for
/// 「閉じたタブを開き直す」 (Ctrl + Shift + T). Bounded: the oldest drop off. Reopening them in turn
/// at their places (<see cref="InsertIndex"/>) gives the strip its old order back when it was not
/// changed in between, also after 「ほかのタブを閉じる」 closed several at once. Not thread-safe (UI thread).
/// </summary>
public sealed class ClosedTabStack<T>(int capacity = ClosedTabStack.DefaultCapacity)
{
    private readonly List<(T Item, int Index)> _items = [];

    public int Count => _items.Count;

    public bool CanPop => _items.Count > 0;

    public void Push(T item, int index)
    {
        _items.Add((item, index));
        if (_items.Count > capacity)
        {
            _items.RemoveAt(0);
        }
    }

    /// <summary>
    /// The newest record that <paramref name="usable"/> accepts; newer ones it refuses are dropped
    /// (a fixed tab that is open again cannot be reopened). False when none is left.
    /// </summary>
    public bool TryPop(Func<T, bool> usable, out T item, out int index)
    {
        while (_items.Count > 0)
        {
            var last = _items[^1];
            _items.RemoveAt(_items.Count - 1);
            if (usable(last.Item))
            {
                item = last.Item;
                index = last.Index;
                return true;
            }
        }

        item = default!;
        index = -1;
        return false;
    }

    /// <summary>Rewrites every record (a folder of them was renamed or moved).</summary>
    public void ReplaceAll(Func<T, T> map)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            _items[i] = (map(_items[i].Item), _items[i].Index);
        }
    }
}

public static class ClosedTabStack
{
    /// <summary>As many as a browser keeps.</summary>
    public const int DefaultCapacity = 25;

    /// <summary>Where a reopened tab goes on a strip of <paramref name="count"/> tabs: its old place, or the nearest end.</summary>
    public static int InsertIndex(int savedIndex, int count) => Math.Clamp(savedIndex, 0, count);
}
