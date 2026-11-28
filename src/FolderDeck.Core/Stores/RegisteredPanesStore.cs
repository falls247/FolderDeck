using System.Security.Cryptography;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

/// <summary>
/// registered_panes.json: the 登録ペイン, pane groups saved under a name. Each holds its layout with the tabs
/// themselves as leaves (not indexes: it outlives the open tabs) and empty panes, and its active pane.
/// Layouts are kept in their written form (<see cref="PaneLayoutSettings.NormalizeSaved"/>); entries that
/// are not valid are skipped on load. Every layout handed out is a copy.
/// </summary>
public sealed class RegisteredPanesStore : StoreBase
{
    public const string DefaultFileName = "registered_panes.json";

    private readonly List<RegisteredPane> _panes = [];

    public RegisteredPanesStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
    }

    /// <summary>The registered panes in their order (the sidebar's).</summary>
    public IReadOnlyList<RegisteredPane> Panes
    {
        get
        {
            lock (Gate)
            {
                return _panes.Select(Copy).ToArray();
            }
        }
    }

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new RegisteredPanesDocument(), d => d.Panes is not null, Clock);
        ApplyLoad(result);
        lock (Gate)
        {
            _panes.Clear();
            foreach (var dto in result.Data.Panes ?? [])
            {
                if (dto is null || string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Name)
                    || _panes.Any(p => p.Id == dto.Id)
                    || PaneLayoutSettings.NormalizeSaved(dto.Layout) is not { } layout)
                {
                    continue;
                }

                string addedAt = dto.AddedAt ?? string.Empty;
                _panes.Add(new RegisteredPane(dto.Id, dto.Name.Trim(), layout, ActiveIn(layout, dto.Active), addedAt, dto.UpdatedAt ?? addedAt));
            }
        }
    }

    public RegisteredPane? Find(string id)
    {
        lock (Gate)
        {
            return _panes.FirstOrDefault(p => p.Id == id) is { } pane ? Copy(pane) : null;
        }
    }

    /// <summary>
    /// Registers <paramref name="layout"/> (a copy of it) under <paramref name="name"/> at the end of the
    /// list and returns the new id. ArgumentException when the name is blank or the layout is not
    /// <see cref="PaneLayoutSettings.IsValidSaved"/>. An <paramref name="active"/> out of range is the first pane.
    /// </summary>
    public string Add(string name, PaneNodeSetting layout, int active)
    {
        string trimmed = TrimName(name) ?? throw new ArgumentException("A registered pane needs a name.", nameof(name));
        var normalized = PaneLayoutSettings.NormalizeSaved(layout) ?? throw new ArgumentException("Not a layout of a registered pane.", nameof(layout));
        string id;
        lock (Gate)
        {
            id = NewId();
            string now = JsonFile.FormatTime(Clock.UtcNow);
            _panes.Add(new RegisteredPane(id, trimmed, normalized, ActiveIn(normalized, active), now, now));
        }

        RequestSave();
        return id;
    }

    /// <summary>
    /// Replaces the layout and active pane of <paramref name="id"/> (現在のペインで上書き, and the automatic
    /// record). Saves, with a new <see cref="RegisteredPane.UpdatedAt"/>, only when the content changed.
    /// False when the id is not registered; ArgumentException when the layout is not
    /// <see cref="PaneLayoutSettings.IsValidSaved"/>.
    /// </summary>
    public bool Overwrite(string id, PaneNodeSetting layout, int active)
    {
        var normalized = PaneLayoutSettings.NormalizeSaved(layout) ?? throw new ArgumentException("Not a layout of a registered pane.", nameof(layout));
        int activeIn = ActiveIn(normalized, active);
        bool changed;
        lock (Gate)
        {
            int i = _panes.FindIndex(p => p.Id == id);
            if (i < 0)
            {
                return false;
            }

            var pane = _panes[i];
            changed = pane.Active != activeIn || !PaneLayoutSettings.SameContent(pane.Layout, normalized);
            if (changed)
            {
                _panes[i] = pane with { Layout = normalized, Active = activeIn, UpdatedAt = JsonFile.FormatTime(Clock.UtcNow) };
            }
        }

        if (changed)
        {
            RequestSave();
        }

        return true;
    }

    /// <summary>
    /// Gives <paramref name="id"/> the name <paramref name="name"/> (trimmed). False when the id is not
    /// registered or the name is blank; saves only when the name changed.
    /// </summary>
    public bool Rename(string id, string name)
    {
        if (TrimName(name) is not { } trimmed)
        {
            return false;
        }

        bool changed;
        lock (Gate)
        {
            int i = _panes.FindIndex(p => p.Id == id);
            if (i < 0)
            {
                return false;
            }

            changed = !string.Equals(_panes[i].Name, trimmed, StringComparison.Ordinal);
            if (changed)
            {
                _panes[i] = _panes[i] with { Name = trimmed };
            }
        }

        if (changed)
        {
            RequestSave();
        }

        return true;
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (Gate)
        {
            removed = _panes.RemoveAll(p => p.Id == id) > 0;
        }

        if (removed)
        {
            RequestSave();
        }

        return removed;
    }

    /// <summary>
    /// Drag &amp; drop order: the listed ids first, in that order; panes not listed keep their relative
    /// order after them. Unknown ids are ignored. Saves only when the order changed.
    /// </summary>
    public void Reorder(IReadOnlyList<string> orderedIds)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        bool changed;
        lock (Gate)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var reordered = new List<RegisteredPane>(_panes.Count);
            foreach (string id in orderedIds)
            {
                if (id is not null && _panes.FirstOrDefault(p => p.Id == id) is { } pane && used.Add(id))
                {
                    reordered.Add(pane);
                }
            }

            reordered.AddRange(_panes.Where(p => !used.Contains(p.Id)));
            changed = !reordered.SequenceEqual(_panes);
            if (changed)
            {
                _panes.Clear();
                _panes.AddRange(reordered);
            }
        }

        if (changed)
        {
            RequestSave();
        }
    }

    /// <summary>
    /// A folder was renamed or moved (<paramref name="oldPath"/> → <paramref name="newPath"/>): the tab
    /// leaves at or below it follow (<see cref="PathRebase.RebaseLocation"/>, as the open tabs do). Ids,
    /// names, order and <see cref="RegisteredPane.UpdatedAt"/> stay. True and a save when any leaf moved.
    /// </summary>
    public bool Repath(string oldPath, string newPath)
    {
        bool moved = false;
        lock (Gate)
        {
            string from = PathTools.NormalizeOrSelf(oldPath);
            string to = PathTools.NormalizeOrSelf(newPath);
            for (int i = 0; i < _panes.Count; i++)
            {
                var layout = _panes[i].Layout.Clone();
                bool changed = false;
                foreach (var leaf in PaneLayoutSettings.LeavesOf(layout))
                {
                    if (leaf.Tab is { } tab
                        && PathRebase.RebaseLocation(tab.Root, tab.Path, from, to) is { } at
                        && !(string.Equals(at.Root, tab.Root, StringComparison.Ordinal) && string.Equals(at.Rel, tab.Path, StringComparison.Ordinal)))
                    {
                        tab.Root = at.Root;
                        tab.Path = at.Rel;
                        changed = true;
                    }
                }

                if (changed)
                {
                    _panes[i] = _panes[i] with { Layout = layout };
                    moved = true;
                }
            }
        }

        if (moved)
        {
            RequestSave();
        }

        return moved;
    }

    protected override byte[] Serialize()
    {
        var doc = new RegisteredPanesDocument
        {
            Version = 1,
            Panes = _panes.Select(p => new RegisteredPaneDto
            {
                Id = p.Id,
                Name = p.Name,
                AddedAt = p.AddedAt,
                UpdatedAt = p.UpdatedAt,
                Layout = p.Layout,
                Active = p.Active,
            }).ToList(),
        };
        return JsonFile.Serialize(doc);
    }

    private static RegisteredPane Copy(RegisteredPane pane) => pane with { Layout = pane.Layout.Clone() };

    private static string? TrimName(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    /// <summary><paramref name="active"/> when it is a leaf of <paramref name="layout"/>, else the first pane.</summary>
    private static int ActiveIn(PaneNodeSetting layout, int active) =>
        active >= 0 && active < PaneLayoutSettings.LeavesOf(layout).Count ? active : 0;

    private string NewId()
    {
        while (true)
        {
            string id = RandomNumberGenerator.GetHexString(12, lowercase: true);
            if (!_panes.Any(p => p.Id == id))
            {
                return id;
            }
        }
    }
}
