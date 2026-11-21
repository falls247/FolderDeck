using System.Security.Cryptography;
using System.Text;

namespace FolderDeck.Core.Infrastructure;

/// <summary>A file of the thumbnail disk cache, as seen when the cache folder is listed.</summary>
public readonly record struct CacheFile(string Path, long Bytes, DateTime LastUsedUtc);

/// <summary>
/// Naming and trimming rules of the thumbnail disk cache. No I/O here: the app does the reading,
/// writing and deleting, and asks these rules which file a key maps to and which files must go.
/// </summary>
public static class DiskCacheRules
{
    /// <summary>After a trim the cache is at most this share of its limit, so trims stay rare.</summary>
    public const double KeepRatio = 0.8;

    private const int NameLength = 32;

    /// <summary>
    /// The cache file of <paramref name="key"/>, relative to the cache folder: a two-character sub
    /// folder and a 32-character lower-case hex name (the first half of the key's SHA-256).
    /// </summary>
    public static string RelativePathOf(string key)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), hash);
        string name = Convert.ToHexStringLower(hash[..(NameLength / 2)]);
        return Path.Combine(name[..2], name);
    }

    /// <summary>
    /// True for a name <see cref="RelativePathOf"/> produces. The trim deletes only such files, so a
    /// cache folder that holds anything else (a mistyped data folder) never loses it.
    /// </summary>
    public static bool IsCacheFileName(string name)
    {
        if (name.Length != NameLength)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The files to delete, least recently used first: none while the total is within
    /// <paramref name="maxBytes"/>, otherwise enough to bring it down to <see cref="KeepRatio"/> of it.
    /// </summary>
    public static List<CacheFile> SelectForRemoval(IReadOnlyCollection<CacheFile> files, long maxBytes)
    {
        var removed = new List<CacheFile>();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Bytes;
        }

        if (total <= maxBytes)
        {
            return removed;
        }

        long target = (long)(Math.Max(0, maxBytes) * KeepRatio);
        foreach (var file in files.OrderBy(f => f.LastUsedUtc))
        {
            if (total <= target)
            {
                break;
            }

            removed.Add(file);
            total -= file.Bytes;
        }

        return removed;
    }
}
