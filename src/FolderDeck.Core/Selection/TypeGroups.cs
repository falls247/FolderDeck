namespace FolderDeck.Core.Selection;

/// <summary>One choice of 「ファイル種別選択」: folders, one extension, or files without one.</summary>
/// <param name="Key">What <see cref="TypeGroups.KeyOf"/> returns for its items.</param>
/// <param name="Count">How many of the given items it holds.</param>
public readonly record struct TypeGroup(string Key, int Count)
{
    public bool IsFolder => Key == TypeGroups.FolderKey;

    public bool IsNoExtension => Key == TypeGroups.NoExtensionKey;
}

/// <summary>
/// 「ファイル種別選択」 of the right-click menu: the kinds of the items shown, to select all of one
/// kind at once. An extension is compared without case (<c>.JPG</c> = <c>.jpg</c>) but as written
/// otherwise (<c>.jpg</c> ≠ <c>.jpeg</c>); a name that starts with a dot and has no other
/// (<c>.gitignore</c>) has that as its extension, as in Explorer.
/// </summary>
public static class TypeGroups
{
    /// <summary>The key of folders (no extension can be written so: it has no dot).</summary>
    public const string FolderKey = "folder";

    /// <summary>The key of files without an extension (a name ending in a dot has none either).</summary>
    public const string NoExtensionKey = "";

    /// <summary><see cref="FolderKey"/>, the lower-case extension with its dot, or <see cref="NoExtensionKey"/>.</summary>
    public static string KeyOf(string name, bool isFolder)
    {
        if (isFolder)
        {
            return FolderKey;
        }

        string extension = Path.GetExtension(name);
        return extension.Length <= 1 ? NoExtensionKey : extension.ToLowerInvariant();
    }

    /// <summary>The kinds present: folders first, then the extensions in order, then files without one.</summary>
    public static IReadOnlyList<TypeGroup> Of(IEnumerable<(string Name, bool IsFolder)> items)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, isFolder) in items)
        {
            string key = KeyOf(name, isFolder);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        return counts
            .OrderBy(p => p.Key == FolderKey ? 0 : p.Key == NoExtensionKey ? 2 : 1)
            .ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new TypeGroup(p.Key, p.Value))
            .ToList();
    }
}
