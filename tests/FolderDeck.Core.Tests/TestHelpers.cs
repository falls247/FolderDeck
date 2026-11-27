using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Tests;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now)
    {
        UtcNow = now;
    }

    public static DateTimeOffset Default => new(2026, 11, 21, 1, 23, 45, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; set; }

    public static FakeClock Fixed() => new(Default);
}

/// <summary>In-memory file system: folder path → child entries.</summary>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, List<FsEntry>> _dirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public int EnumerateCalls;

    /// <summary>When set, every enumeration waits for it (a listing kept running).</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public List<string> Enumerated { get; } = [];

    public FakeFileSystem AddFolder(string path, bool reparse = false, FileAttributes extra = 0)
    {
        if (!_dirs.ContainsKey(path))
        {
            _dirs[path] = [];
        }

        string? parent = Path.GetDirectoryName(path);
        if (parent is not null && parent != path)
        {
            if (!_dirs.ContainsKey(parent))
            {
                AddFolder(parent);
            }

            var list = _dirs[parent];
            if (!list.Any(e => string.Equals(e.FullPath, path, StringComparison.OrdinalIgnoreCase)))
            {
                var attrs = FileAttributes.Directory | extra | (reparse ? FileAttributes.ReparsePoint : 0);
                list.Add(new FsEntry(Path.GetFileName(path), path, true, reparse, 0, DateTime.UtcNow, attrs));
            }
        }

        return this;
    }

    public FakeFileSystem AddFile(string path, long length = 1, FileAttributes extra = 0)
    {
        string parent = Path.GetDirectoryName(path)!;
        AddFolder(parent);
        _files.Add(path);
        _dirs[parent].Add(new FsEntry(Path.GetFileName(path), path, false, false, length, DateTime.UtcNow, FileAttributes.Archive | extra));
        return this;
    }

    public IReadOnlyList<FsEntry> EnumerateDirectory(string path, ListingFilter filter, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref EnumerateCalls);
        Gate?.Wait(cancellationToken);
        lock (Enumerated)
        {
            Enumerated.Add(path);
        }

        if (!_dirs.TryGetValue(path, out var list))
        {
            throw new DirectoryNotFoundException(path);
        }

        const FileAttributes skip = FileAttributes.Hidden | FileAttributes.System;
        return list
            .Where(e => (filter & ListingFilter.DotNames) != 0 || !e.Name.StartsWith('.'))
            .Where(e => (filter & ListingFilter.Hidden) != 0 || (e.Attributes & skip) == 0)
            .ToList();
    }

    public PathKind GetKind(string path) =>
        _dirs.ContainsKey(path) ? PathKind.Folder : _files.Contains(path) ? PathKind.File : PathKind.Missing;

    public FsEntry? GetEntry(string path)
    {
        var kind = GetKind(path);
        if (kind == PathKind.Missing)
        {
            return null;
        }

        return new FsEntry(Path.GetFileName(path), path, kind == PathKind.Folder, false, 0, DateTime.UtcNow, 0);
    }
}

/// <summary>Real temporary folder, removed (attributes cleared first) on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FolderDeckTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string MakeDir(params string[] parts)
    {
        string p = Combine(parts);
        Directory.CreateDirectory(p);
        return p;
    }

    public string MakeFile(string relative, string content = "x", FileAttributes attributes = 0)
    {
        string p = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        if (attributes != 0)
        {
            File.SetAttributes(p, File.GetAttributes(p) | attributes);
        }

        return p;
    }

    public void Dispose()
    {
        try
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(entry, FileAttributes.Normal);
                }
                catch
                {
                    // ignore
                }
            }

            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
