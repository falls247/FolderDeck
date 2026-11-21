namespace FolderDeck.Core.Listing;

/// <summary>Small thread-safe cache with a fixed time-to-live and an entry cap.</summary>
public sealed class TtlCache<TKey, TValue>
    where TKey : notnull
{
    private readonly object _gate = new();
    private readonly Dictionary<TKey, (TValue Value, long Ticks)> _items;
    private readonly long _ttlMs;
    private readonly int _max;
    private readonly Func<long> _ticks;

    public TtlCache(TimeSpan ttl, int maxEntries, IEqualityComparer<TKey>? comparer = null, Func<long>? tickSource = null)
    {
        _items = new Dictionary<TKey, (TValue, long)>(comparer);
        _ttlMs = (long)ttl.TotalMilliseconds;
        _max = Math.Max(1, maxEntries);
        _ticks = tickSource ?? (() => Environment.TickCount64);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var item))
            {
                if (_ticks() - item.Ticks <= _ttlMs)
                {
                    value = item.Value;
                    return true;
                }

                _items.Remove(key);
            }
        }

        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_gate)
        {
            _items[key] = (value, _ticks());
            if (_items.Count > _max)
            {
                long now = _ticks();
                foreach (var k in _items.Where(kv => now - kv.Value.Ticks > _ttlMs).Select(kv => kv.Key).ToList())
                {
                    _items.Remove(k);
                }

                if (_items.Count > _max)
                {
                    foreach (var k in _items.OrderBy(kv => kv.Value.Ticks).Take(_items.Count - (_max * 9 / 10)).Select(kv => kv.Key).ToList())
                    {
                        _items.Remove(k);
                    }
                }
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }
}
