using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

/// <summary>excluded.json: items hidden from "最近の履歴" until they are used again (§10.2).</summary>
public sealed class ExcludedStore : StoreBase
{
    public const string DefaultFileName = "excluded.json";

    private readonly List<ExcludedItem> _items = [];

    public ExcludedStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
    }

    public IReadOnlyList<ExcludedItem> Items
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
        var result = JsonFile.Load(FilePath, () => new ExcludedDocument(), d => d.Excluded is not null, Clock);
        ApplyLoad(result);
        lock (Gate)
        {
            _items.Clear();
            foreach (var dto in result.Data.Excluded ?? [])
            {
                if (dto is null || string.IsNullOrWhiteSpace(dto.Path) || dto.ExcludedAt is null)
                {
                    continue;
                }

                Upsert(new ExcludedItem(PathTools.NormalizeOrSelf(dto.Path), dto.ExcludedAt.Value));
            }
        }
    }

    public void Exclude(string path, DateTimeOffset? now = null)
    {
        lock (Gate)
        {
            Upsert(new ExcludedItem(PathTools.NormalizeOrSelf(path), JsonFile.TruncateToSeconds(now ?? Clock.UtcNow)));
        }

        RequestSave();
    }

    protected override byte[] Serialize()
    {
        var doc = new ExcludedDocument
        {
            Version = 1,
            Excluded = _items.Select(i => new ExcludedItemDto { Path = i.Path, ExcludedAt = i.ExcludedAt }).ToList(),
        };
        return JsonFile.Serialize(doc);
    }

    private void Upsert(ExcludedItem item)
    {
        int index = _items.FindIndex(i => PathTools.PathEquals(i.Path, item.Path));
        if (index >= 0)
        {
            _items[index] = item;
        }
        else
        {
            _items.Add(item);
        }
    }
}
