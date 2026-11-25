using System.Security.Cryptography;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Stores;

public enum RegisterOutcome
{
    Added,
    AlreadyRegistered,
    Invalid,
    NotFound,
}

public sealed record RegisterResult(RegisterOutcome Outcome, string Input, string? Path, string? Error, RegisteredFolder? Folder);

/// <summary>Summary of a multi-folder registration (§10.1 "N 件登録しました。…").</summary>
public sealed record RegisterSummary(IReadOnlyList<RegisterResult> Results)
{
    public int Added => Results.Count(r => r.Outcome == RegisterOutcome.Added);

    public int AlreadyRegistered => Results.Count(r => r.Outcome == RegisterOutcome.AlreadyRegistered);

    public int NotFound => Results.Count(r => r.Outcome == RegisterOutcome.NotFound);

    public IEnumerable<string> Errors => Results.Where(r => r.Error is not null).Select(r => r.Error!);

    public string ToMessage()
    {
        string text = $"{Added} 件登録しました。";
        if (AlreadyRegistered > 0)
        {
            text += $"{AlreadyRegistered} 件は登録済みです。";
        }

        string errors = string.Join(" ", Errors);
        if (errors.Length > 0)
        {
            text += errors;
        }

        return text;
    }
}

/// <summary>registered_folders.json (same format as imgtool).</summary>
public sealed class RegisteredFoldersStore : StoreBase
{
    public const string DefaultFileName = "registered_folders.json";

    private readonly List<RegisteredFolder> _folders = [];

    public RegisteredFoldersStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
    }

    public IReadOnlyList<RegisteredFolder> Folders
    {
        get
        {
            lock (Gate)
            {
                return _folders.ToArray();
            }
        }
    }

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new RegisteredFoldersDocument(), d => d.Folders is not null, Clock);
        ApplyLoad(result);
        lock (Gate)
        {
            _folders.Clear();
            foreach (var dto in result.Data.Folders ?? [])
            {
                if (dto is null || string.IsNullOrWhiteSpace(dto.Id) || string.IsNullOrWhiteSpace(dto.Path))
                {
                    continue;
                }

                if (_folders.Any(f => f.Id == dto.Id))
                {
                    continue;
                }

                _folders.Add(new RegisteredFolder(dto.Id, dto.Path, dto.AddedAt ?? string.Empty, string.IsNullOrWhiteSpace(dto.FileId) ? null : dto.FileId));
            }
        }
    }

    public RegisteredFolder? FindById(string id)
    {
        lock (Gate)
        {
            return _folders.FirstOrDefault(f => f.Id == id);
        }
    }

    public RegisteredFolder? FindByPath(string path)
    {
        lock (Gate)
        {
            return _folders.FirstOrDefault(f => PathTools.PathEquals(KeyOf(f.Path), KeyOf(path)));
        }
    }

    /// <summary>
    /// Registers one input following §10.1: clean, validate, normalize, check the folder exists,
    /// skip duplicates. Blocks on I/O: call off the UI thread.
    /// </summary>
    public RegisterResult Register(string rawInput, IFileSystem fs, bool save = true)
    {
        if (!PathTools.TryNormalizeInput(rawInput, out string normalized, out string? error))
        {
            return new RegisterResult(RegisterOutcome.Invalid, rawInput, null, error, null);
        }

        if (fs.GetKind(normalized) != PathKind.Folder)
        {
            return new RegisterResult(RegisterOutcome.NotFound, rawInput, normalized, string.Format(PathTools.MsgFolderNotFound, normalized), null);
        }

        RegisteredFolder? existing = FindByPath(normalized);
        if (existing is not null)
        {
            return new RegisterResult(RegisterOutcome.AlreadyRegistered, rawInput, normalized, null, existing);
        }

        RegisteredFolder added;
        lock (Gate)
        {
            existing = _folders.FirstOrDefault(f => PathTools.PathEquals(KeyOf(f.Path), normalized));
            if (existing is not null)
            {
                return new RegisterResult(RegisterOutcome.AlreadyRegistered, rawInput, normalized, null, existing);
            }

            added = new RegisteredFolder(NewId(), normalized, JsonFile.FormatTime(Clock.UtcNow));
            _folders.Add(added);
        }

        if (save)
        {
            RequestSave();
        }

        return new RegisterResult(RegisterOutcome.Added, rawInput, normalized, null, added);
    }

    public RegisterSummary RegisterMany(IEnumerable<string> inputs, IFileSystem fs)
    {
        var results = inputs.Select(i => Register(i, fs, save: false)).ToList();
        if (results.Any(r => r.Outcome == RegisterOutcome.Added))
        {
            RequestSave();
        }

        return new RegisterSummary(results);
    }

    public bool Remove(string id)
    {
        bool removed;
        lock (Gate)
        {
            removed = _folders.RemoveAll(f => f.Id == id) > 0;
        }

        if (removed)
        {
            RequestSave();
        }

        return removed;
    }

    /// <summary>
    /// Remembers the identity of a registered folder (<see cref="FolderIdentity"/>; null forgets it).
    /// Saves only when it changed. False when the id is not registered.
    /// </summary>
    public bool SetFileId(string id, string? fileId)
    {
        bool changed = false;
        lock (Gate)
        {
            int i = _folders.FindIndex(f => f.Id == id);
            if (i >= 0 && !string.Equals(_folders[i].FileId, fileId, StringComparison.Ordinal))
            {
                _folders[i] = _folders[i] with { FileId = fileId };
                changed = true;
            }
        }

        if (changed)
        {
            RequestSave();
        }

        return changed;
    }

    /// <summary>
    /// A folder was renamed or moved (<paramref name="oldPath"/> → <paramref name="newPath"/>): every
    /// registered folder at or below it follows (same id, same place in the order, same identity). A
    /// folder whose new path is registered already stays as it is (no duplicates). Returns how many
    /// moved; saves when any did.
    /// </summary>
    public int Repath(string oldPath, string newPath)
    {
        int moved = 0;
        lock (Gate)
        {
            string from = KeyOf(oldPath);
            string to = KeyOf(newPath);
            for (int i = 0; i < _folders.Count; i++)
            {
                var folder = _folders[i];
                string? rebased = PathRebase.Rebase(KeyOf(folder.Path), from, to);
                if (rebased is null || string.Equals(rebased, folder.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                bool taken = _folders.Any(f => !ReferenceEquals(f, folder) && PathTools.PathEquals(KeyOf(f.Path), rebased));
                if (taken && !PathTools.PathEquals(KeyOf(folder.Path), rebased))
                {
                    continue;
                }

                _folders[i] = folder with { Path = rebased };
                moved++;
            }
        }

        if (moved > 0)
        {
            RequestSave();
        }

        return moved;
    }

    /// <summary>
    /// Drag &amp; drop order: the listed ids first, in that order; folders not listed keep their
    /// relative order after them. Unknown ids are ignored. Saves only when the order changed.
    /// </summary>
    public bool Reorder(IReadOnlyList<string> orderedIds)
    {
        bool changed;
        lock (Gate)
        {
            var byId = new Dictionary<string, RegisteredFolder>(StringComparer.Ordinal);
            foreach (var f in _folders)
            {
                byId.TryAdd(f.Id, f);
            }

            var reordered = new List<RegisteredFolder>(_folders.Count);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in orderedIds)
            {
                if (id is not null && byId.TryGetValue(id, out var folder) && used.Add(id))
                {
                    reordered.Add(folder);
                }
            }

            foreach (var f in _folders)
            {
                if (!used.Contains(f.Id))
                {
                    reordered.Add(f);
                }
            }

            changed = !reordered.SequenceEqual(_folders);
            if (changed)
            {
                _folders.Clear();
                _folders.AddRange(reordered);
            }
        }

        if (changed)
        {
            RequestSave();
        }

        return changed;
    }

    protected override byte[] Serialize()
    {
        var doc = new RegisteredFoldersDocument
        {
            Version = 1,
            Folders = _folders.Select(f => new RegisteredFolderDto { Id = f.Id, Path = f.Path, AddedAt = f.AddedAt, FileId = f.FileId }).ToList(),
        };
        return JsonFile.Serialize(doc);
    }

    private static string KeyOf(string path) => PathTools.NormalizeOrSelf(path);

    private string NewId()
    {
        while (true)
        {
            string id = RandomNumberGenerator.GetHexString(12, lowercase: true);
            if (!_folders.Any(f => f.Id == id))
            {
                return id;
            }
        }
    }
}
