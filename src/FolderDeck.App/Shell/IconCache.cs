using System.Collections.Concurrent;
using System.Windows.Media;
using FolderDeck.Core.Paths;

namespace FolderDeck.Shell;

/// <summary>
/// 16 px shell icons for list rows (§9.4). Cached per extension; files whose icon is their own
/// (<c>.exe .lnk .ico</c>…) are cached per path.
/// </summary>
public sealed class IconCache : IDisposable
{
    private static readonly HashSet<string> PerPathExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".msc", ".appref-ms",
    };

    private readonly StaWorkerPool _pool = new(1, "Icons");
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True for extensions whose files carry their own icon (.exe, .lnk, .ico…).</summary>
    public static bool HasOwnIcon(string extension) => PerPathExtensions.Contains(extension);

    public static string KeyOf(string path, bool isFolder)
    {
        if (isFolder)
        {
            return "<folder>";
        }

        string ext = PathTools.ExtensionOf(path);
        return PerPathExtensions.Contains(ext) ? "path:" + path : "ext:" + ext;
    }

    public bool TryGet(string path, bool isFolder, out ImageSource? icon) =>
        _cache.TryGetValue(KeyOf(path, isFolder), out icon);

    public Task<ImageSource?> GetAsync(string path, bool isFolder)
    {
        string key = KeyOf(path, isFolder);
        if (_cache.TryGetValue(key, out var icon))
        {
            return Task.FromResult(icon);
        }

        return _pending.GetOrAdd(key, k => LoadAsync(k, path, isFolder));
    }

    public void Dispose() => _pool.Dispose();

    private async Task<ImageSource?> LoadAsync(string key, string path, bool isFolder)
    {
        try
        {
            var icon = await _pool.RunAsync<ImageSource?>(
                () =>
                {
                    if (isFolder)
                    {
                        return ShellInterop.GetFileInfoIcon("folder", isFolder: true, useAttributesOnly: true, small: true);
                    }

                    if (key.StartsWith("path:", StringComparison.Ordinal))
                    {
                        return ShellInterop.GetFileInfoIcon(path, isFolder: false, useAttributesOnly: false, small: true)
                            ?? ShellInterop.GetFileInfoIcon("x" + PathTools.ExtensionOf(path), isFolder: false, useAttributesOnly: true, small: true);
                    }

                    return ShellInterop.GetFileInfoIcon("x" + PathTools.ExtensionOf(path), isFolder: false, useAttributesOnly: true, small: true);
                },
                CancellationToken.None,
                TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _cache[key] = icon;
            return icon;
        }
        catch (Exception)
        {
            _cache[key] = null;
            return null;
        }
        finally
        {
            _pending.TryRemove(key, out _);
        }
    }
}
