namespace FolderDeck.Core.Infrastructure;

/// <summary>Current time. Swapped out in tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>What a path currently is on disk.</summary>
public enum PathKind
{
    Missing,
    Folder,
    File,
}

/// <summary>One directory entry as returned by <see cref="IFileSystem.EnumerateDirectory"/>.</summary>
public readonly record struct FsEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    bool IsReparsePoint,
    long Length,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes);

/// <summary>
/// File system access used by Core. Every member may block (network drives), so callers
/// run them off the UI thread.
/// </summary>
public interface IFileSystem
{
    /// <summary>
    /// Lists the direct children of <paramref name="path"/> (unsorted). Names starting with
    /// '.' are always skipped; hidden and system entries are skipped unless
    /// <paramref name="includeHidden"/> is true.
    /// Throws <see cref="UnauthorizedAccessException"/>, <see cref="DirectoryNotFoundException"/>
    /// or <see cref="IOException"/> when the folder itself cannot be read.
    /// </summary>
    IReadOnlyList<FsEntry> EnumerateDirectory(string path, bool includeHidden, CancellationToken cancellationToken);

    PathKind GetKind(string path);

    /// <summary>Size and time stamp of a single path, or null when it does not exist.</summary>
    FsEntry? GetEntry(string path);
}
