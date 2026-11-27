using System.IO.Enumeration;

namespace FolderDeck.Core.Infrastructure;

/// <summary><see cref="IFileSystem"/> backed by System.IO (long paths are handled by .NET).</summary>
public sealed class RealFileSystem : IFileSystem
{
    public static readonly RealFileSystem Instance = new();

    private const FileAttributes HiddenOrSystem = FileAttributes.Hidden | FileAttributes.System;

    public IReadOnlyList<FsEntry> EnumerateDirectory(string path, ListingFilter filter, CancellationToken cancellationToken)
    {
        FileAttributes skip = (filter & ListingFilter.Hidden) != 0 ? 0 : HiddenOrSystem;
        bool skipDotNames = (filter & ListingFilter.DotNames) == 0;
        var options = new EnumerationOptions
        {
            // Non-recursive: IgnoreInaccessible would only hide an error on the folder itself,
            // which must surface as "access denied" instead of an empty list.
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = skip,
            MatchType = MatchType.Simple,
        };

        var enumerable = new FileSystemEnumerable<FsEntry>(
            path,
            (ref FileSystemEntry e) => new FsEntry(
                e.FileName.ToString(),
                e.ToFullPath(),
                e.IsDirectory,
                (e.Attributes & FileAttributes.ReparsePoint) != 0,
                e.IsDirectory ? 0 : e.Length,
                e.LastWriteTimeUtc.UtcDateTime,
                e.Attributes),
            options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) =>
                !(skipDotNames && e.FileName.StartsWith('.')) && (e.Attributes & skip) == 0,
        };

        var result = new List<FsEntry>();
        int n = 0;
        foreach (var entry in enumerable)
        {
            if ((++n & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            result.Add(entry);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public PathKind GetKind(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.Directory) != 0 ? PathKind.Folder : PathKind.File;
        }
        catch (UnauthorizedAccessException)
        {
            if (Directory.Exists(path))
            {
                return PathKind.Folder;
            }

            return File.Exists(path) ? PathKind.File : PathKind.Missing;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return PathKind.Missing;
        }
    }

    public FsEntry? GetEntry(string path)
    {
        try
        {
            FileSystemInfo info = new FileInfo(path);
            if (!info.Exists)
            {
                info = new DirectoryInfo(path);
                if (!info.Exists)
                {
                    return null;
                }
            }

            bool isDir = (info.Attributes & FileAttributes.Directory) != 0;
            return new FsEntry(
                info.Name,
                info.FullName,
                isDir,
                (info.Attributes & FileAttributes.ReparsePoint) != 0,
                info is FileInfo fi ? fi.Length : 0,
                info.LastWriteTimeUtc,
                info.Attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
