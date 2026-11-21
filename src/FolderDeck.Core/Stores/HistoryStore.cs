using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

/// <summary>history.json: usage records behind "最近の履歴" and "よく使う項目" (§10.4).</summary>
public sealed class HistoryStore : StoreBase
{
    public const string DefaultFileName = "history.json";
    public const int MaxItems = 2000;
    public const int MaxRecentOpens = 10;

    private readonly List<HistoryItem> _items = [];

    public HistoryStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
    }

    /// <summary>Raised after any change (from the calling thread).</summary>
    public event Action? Changed;

    public IReadOnlyList<HistoryItem> Items
    {
        get
        {
            lock (Gate)
            {
                return _items.ToArray();
            }
        }
    }

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new HistoryDocument(), d => d.Items is not null, Clock);
        ApplyLoad(result);
        lock (Gate)
        {
            _items.Clear();
            foreach (var dto in result.Data.Items ?? [])
            {
                if (dto is null || string.IsNullOrWhiteSpace(dto.Path))
                {
                    continue;
                }

                var opens = (dto.RecentOpens ?? [])
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .OrderByDescending(t => t)
                    .Take(MaxRecentOpens)
                    .ToArray();
                DateTimeOffset? last = dto.LastOpenedAt ?? (opens.Length > 0 ? opens[0] : null);
                if (last is null)
                {
                    continue;
                }

                string path = PathTools.NormalizeOrSelf(dto.Path);
                if (_items.Any(i => PathTools.PathEquals(i.Path, path)))
                {
                    continue;
                }

                _items.Add(new HistoryItem(
                    path,
                    ItemKinds.FromJson(dto.Kind) ?? ItemKind.File,
                    Math.Max(0, dto.OpenCount),
                    last.Value,
                    opens));
            }

            TrimLocked();
        }
    }

    public HistoryItem? Find(string path)
    {
        lock (Gate)
        {
            return _items.FirstOrDefault(i => PathTools.PathEquals(i.Path, path));
        }
    }

    /// <summary>Counts one use of <paramref name="path"/> at <paramref name="now"/> (defaults to the clock).</summary>
    public HistoryItem Record(string path, ItemKind kind, DateTimeOffset? now = null)
    {
        var at = JsonFile.TruncateToSeconds(now ?? Clock.UtcNow);
        string normalized = PathTools.NormalizeOrSelf(path);
        HistoryItem updated;
        lock (Gate)
        {
            int index = _items.FindIndex(i => PathTools.PathEquals(i.Path, normalized));
            if (index >= 0)
            {
                var old = _items[index];
                var opens = new List<DateTimeOffset>(old.RecentOpens.Count + 1) { at };
                opens.AddRange(old.RecentOpens);
                if (opens.Count > MaxRecentOpens)
                {
                    opens.RemoveRange(MaxRecentOpens, opens.Count - MaxRecentOpens);
                }

                updated = old with { Kind = kind, OpenCount = old.OpenCount + 1, LastOpenedAt = at, RecentOpens = opens };
                _items[index] = updated;
            }
            else
            {
                updated = new HistoryItem(normalized, kind, 1, at, [at]);
                _items.Add(updated);
                TrimLocked();
            }
        }

        RequestSave();
        Changed?.Invoke();
        return updated;
    }

    public bool Remove(string path)
    {
        bool removed;
        lock (Gate)
        {
            removed = _items.RemoveAll(i => PathTools.PathEquals(i.Path, path)) > 0;
        }

        if (removed)
        {
            RequestSave();
            Changed?.Invoke();
        }

        return removed;
    }

    public void Clear()
    {
        lock (Gate)
        {
            _items.Clear();
        }

        RequestSave();
        Changed?.Invoke();
    }

    protected override byte[] Serialize()
    {
        var doc = new HistoryDocument
        {
            Version = 1,
            Items = _items.Select(i => new HistoryItemDto
            {
                Path = i.Path,
                Kind = ItemKinds.ToJson(i.Kind),
                OpenCount = i.OpenCount,
                LastOpenedAt = i.LastOpenedAt,
                RecentOpens = i.RecentOpens.Select(t => (DateTimeOffset?)t).ToList(),
            }).ToList(),
        };
        return JsonFile.Serialize(doc);
    }

    private void TrimLocked()
    {
        if (_items.Count <= MaxItems)
        {
            return;
        }

        var keep = _items.OrderByDescending(i => i.LastOpenedAt).Take(MaxItems).ToHashSet();
        _items.RemoveAll(i => !keep.Contains(i));
    }
}
