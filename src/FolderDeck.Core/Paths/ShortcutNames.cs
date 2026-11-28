namespace FolderDeck.Core.Paths;

/// <summary>
/// The file name of a shortcut made by Alt + drop, as Explorer names it on a Japanese Windows:
/// "<c>名前 - ショートカット.lnk</c>", then "<c>名前 - ショートカット (2).lnk</c>"… The shell's own
/// rule (<c>SHGetNewLinkInfo</c>) comes first in the App; this one is used when the shell gives none
/// (a long path) and is what the tests pin down.
/// </summary>
public static class ShortcutNames
{
    public const string Suffix = " - ショートカット";

    public const string Extension = ".lnk";

    /// <summary>Used when nothing usable is left of the item's name (a drive root such as <c>C:\</c> without its label).</summary>
    public const string Fallback = "ショートカット";

    /// <summary>
    /// The base of the name: the item's name (with its extension, as Explorer shows it) without the
    /// characters a file name cannot hold (a drive's "ローカル ディスク (C:)" → "ローカル ディスク (C)"),
    /// without trailing dots and spaces, cut so that the suffix, " (nn)" and ".lnk" still fit.
    /// </summary>
    public static string BaseName(string itemName)
    {
        var chars = (itemName ?? string.Empty).Where(c => !ItemNames.IsInvalidChar(c)).ToArray();
        string name = new string(chars).Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            return Fallback;
        }

        int room = ItemNames.MaxLength - Suffix.Length - Extension.Length - " (99)".Length;
        return name.Length > room ? name[..room].TrimEnd('.', ' ') : name;
    }

    /// <summary>
    /// The first free file name for a shortcut to <paramref name="itemName"/>: <paramref name="isTaken"/>
    /// says whether a name is already used in the target folder (compare without case).
    /// </summary>
    public static string FileName(string itemName, Func<string, bool> isTaken)
    {
        string stem = BaseName(itemName) + Suffix;
        string first = stem + Extension;
        if (!isTaken(first))
        {
            return first;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){Extension}";
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }
    }
}
