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

                _folders.Add(new RegisteredFolder(dto.Id, dto.Path, dto.AddedAt ?? string.Empty));
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

    protected override byte[] Serialize()
    {
        var doc = new RegisteredFoldersDocument
        {
            Version = 1,
            Folders = _folders.Select(f => new RegisteredFolderDto { Id = f.Id, Path = f.Path, AddedAt = f.AddedAt }).ToList(),
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
