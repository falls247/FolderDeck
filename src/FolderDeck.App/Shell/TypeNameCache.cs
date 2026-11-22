using System.Collections.Concurrent;
using FolderDeck.Core.Text;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

/// <summary>
/// The shell's type names for the 種類 column of the details view ("JPG ファイル",
/// "Microsoft Excel ワークシート", "ファイル フォルダー"). Asked by extension only
/// (<c>SHGFI_USEFILEATTRIBUTES</c>: no file is opened) and cached per extension for the life of the
/// app. A call that fails or does not return within 5 seconds leaves
/// <see cref="DetailsText.TypeFallback"/> as the name of that extension.
/// </summary>
public sealed class TypeNameCache
{
    private const string FolderKey = "<folder>";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly StaWorkerPool _pool;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string>> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="pool">STA workers for the shell call. Owned (and disposed) by the caller.</param>
    public TypeNameCache(StaWorkerPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        _pool = pool;
    }

    /// <summary>
    /// The cached name. When the extension has not been asked yet, returns false and
    /// <paramref name="name"/> is the fallback text, ready to show until <see cref="GetAsync"/> answers.
    /// </summary>
    public bool TryGet(string extension, bool isFolder, out string name)
    {
        string key = KeyOf(extension, isFolder);
        if (_cache.TryGetValue(key, out string? cached))
        {
            name = cached;
            return true;
        }

        name = DetailsText.TypeFallback(key, isFolder);
        return false;
    }

    /// <summary>The type name of the extension (with or without the leading dot, any case). Never faults.</summary>
    public Task<string> GetAsync(string extension, bool isFolder)
    {
        string key = KeyOf(extension, isFolder);
        if (_cache.TryGetValue(key, out string? cached))
        {
            return Task.FromResult(cached);
        }

        return _pending.GetOrAdd(key, k => LoadAsync(k, isFolder));
    }

    /// <summary>".JPG", "jpg" → ".jpg"; no extension → ""; every folder shares one key.</summary>
    private static string KeyOf(string? extension, bool isFolder)
    {
        if (isFolder)
        {
            return FolderKey;
        }

        string ext = (extension ?? string.Empty).Trim().TrimStart('.');
        return ext.Length == 0 ? string.Empty : "." + ext.ToLowerInvariant();
    }

    private async Task<string> LoadAsync(string key, bool isFolder)
    {
        string name;
        try
        {
            string? shellName = await _pool.RunAsync<string?>(
                () => ShellTypeName(key, isFolder),
                CancellationToken.None,
                CallTimeout).ConfigureAwait(false);
            name = string.IsNullOrWhiteSpace(shellName) ? DetailsText.TypeFallback(key, isFolder) : shellName;
        }
        catch (Exception)
        {
            // Timeout (the worker was abandoned), or the pool is already disposed at shutdown.
            name = DetailsText.TypeFallback(key, isFolder);
        }

        _cache[key] = name;
        _pending.TryRemove(key, out _);
        return name;
    }

    /// <summary>Runs on an STA worker. Null when the shell has no name for the extension.</summary>
    private static string? ShellTypeName(string key, bool isFolder)
    {
        var info = default(SHFILEINFOW);
        var attributes = isFolder ? FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY : FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;

        // The name is never looked up on disk: only the attributes and the extension count.
        string name = isFolder ? "folder" : "x" + key;
        nuint ok = PInvoke.SHGetFileInfo(name, attributes, ref info, SHGFI_FLAGS.SHGFI_TYPENAME | SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES);
        if (ok == 0)
        {
            return null;
        }

        string typeName = info.szTypeName.ToString();
        return typeName.Length == 0 ? null : typeName;
    }
}
