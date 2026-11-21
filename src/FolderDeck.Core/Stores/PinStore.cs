using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

/// <summary>pins.json: items pinned to the top of "よく使う項目".</summary>
public sealed class PinStore : StoreBase
{
    public const string DefaultFileName = "pins.json";

    private readonly List<PinItem> _pins = [];

    public PinStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
    }

    public event Action? Changed;

    /// <summary>Pins in pinning order (oldest <c>pinned_at</c> first).</summary>
    public IReadOnlyList<PinItem> Pins
    {
        get
        {
            lock (Gate)
            {
                return _pins.OrderBy(p => p.PinnedAt).ToArray();
            }
        }
    }

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new PinsDocument(), d => d.Pins is not null, Clock);
        ApplyLoad(result);
        lock (Gate)
        {
            _pins.Clear();
            foreach (var dto in result.Data.Pins ?? [])
            {
                if (dto is null || string.IsNullOrWhiteSpace(dto.Path))
                {
                    continue;
                }

                string path = PathTools.NormalizeOrSelf(dto.Path);
                if (_pins.Any(p => PathTools.PathEquals(p.Path, path)))
                {
                    continue;
                }

                _pins.Add(new PinItem(path, ItemKinds.FromJson(dto.Kind) ?? ItemKind.File, dto.PinnedAt ?? DateTimeOffset.UnixEpoch));
            }
        }
    }

    public bool IsPinned(string path)
    {
        lock (Gate)
        {
            return _pins.Any(p => PathTools.PathEquals(p.Path, path));
        }
    }

    public bool Pin(string path, ItemKind kind, DateTimeOffset? now = null)
    {
        string normalized = PathTools.NormalizeOrSelf(path);
        lock (Gate)
        {
            if (_pins.Any(p => PathTools.PathEquals(p.Path, normalized)))
            {
                return false;
            }

            _pins.Add(new PinItem(normalized, kind, JsonFile.TruncateToSeconds(now ?? Clock.UtcNow)));
        }

        RequestSave();
        Changed?.Invoke();
        return true;
    }

    public bool Unpin(string path)
    {
        bool removed;
        lock (Gate)
        {
            removed = _pins.RemoveAll(p => PathTools.PathEquals(p.Path, path)) > 0;
        }

        if (removed)
        {
            RequestSave();
            Changed?.Invoke();
        }

        return removed;
    }

    protected override byte[] Serialize()
    {
        var doc = new PinsDocument
        {
            Version = 1,
            Pins = _pins.Select(p => new PinItemDto { Path = p.Path, Kind = ItemKinds.ToJson(p.Kind), PinnedAt = p.PinnedAt }).ToList(),
        };
        return JsonFile.Serialize(doc);
    }
}
