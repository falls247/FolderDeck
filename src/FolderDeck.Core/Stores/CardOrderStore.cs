using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

/// <summary>Which top-level list a saved card order belongs to.</summary>
public enum CardOrderList
{
    Recent,
    Frequent,
}

/// <summary>
/// card_order.json: the drag &amp; drop order of the top level of "最近の履歴" and "よく使う項目"
/// (normalized paths, no duplicates, at most <see cref="MaxEntries"/> per list).
/// "登録フォルダ" keeps its order in registered_folders.json instead.
/// </summary>
public sealed class CardOrderStore : StoreBase
{
    public const string DefaultFileName = "card_order.json";
    public const int MaxEntries = 1000;

    private List<string> _recent = [];
    private List<string> _frequent = [];

    public CardOrderStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
    }

    public IReadOnlyList<string> Get(CardOrderList list)
    {
        lock (Gate)
        {
            return ListOf(list).ToArray();
        }
    }

    /// <summary>True when <paramref name="list"/> has a saved order (cards were dragged in that tab).</summary>
    public bool HasOrder(CardOrderList list)
    {
        lock (Gate)
        {
            return ListOf(list).Count > 0;
        }
    }

    /// <summary>Forgets one list's saved order (「手動の並びをリセット」). Returns false when there was none.</summary>
    public bool Clear(CardOrderList list) => Set(list, []);

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new CardOrderDocument(), d => d.Recent is not null && d.Frequent is not null, Clock);
        ApplyLoad(result);
        var recent = Clean(result.Data.Recent);
        var frequent = Clean(result.Data.Frequent);
        lock (Gate)
        {
            _recent = recent;
            _frequent = frequent;
        }
    }

    /// <summary>Replaces one list (normalized, deduplicated, capped). Returns false when nothing changed.</summary>
    public bool Set(CardOrderList list, IEnumerable<string> paths)
    {
        var cleaned = Clean(paths);
        lock (Gate)
        {
            if (ListOf(list).SequenceEqual(cleaned, StringComparer.Ordinal))
            {
                return false;
            }

            if (list == CardOrderList.Recent)
            {
                _recent = cleaned;
            }
            else
            {
                _frequent = cleaned;
            }
        }

        RequestSave();
        return true;
    }

    protected override byte[] Serialize()
    {
        var doc = new CardOrderDocument
        {
            Version = 1,
            Recent = [.. _recent],
            Frequent = [.. _frequent],
        };
        return JsonFile.Serialize(doc);
    }

    private static List<string> Clean(IEnumerable<string?>? paths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathTools.Comparer);
        foreach (string? raw in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string path = PathTools.NormalizeOrSelf(raw);
            if (seen.Add(path))
            {
                result.Add(path);
                if (result.Count >= MaxEntries)
                {
                    break;
                }
            }
        }

        return result;
    }

    private List<string> ListOf(CardOrderList list) => list == CardOrderList.Recent ? _recent : _frequent;
}

/// <summary>On-disk shape of card_order.json.</summary>
public sealed class CardOrderDocument
{
    public int Version { get; set; } = 1;

    public List<string?>? Recent { get; set; } = [];

    public List<string?>? Frequent { get; set; } = [];
}
