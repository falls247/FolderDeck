using System.Globalization;

namespace FolderDeck.Core.Text;

/// <summary>Cell texts of the details view that need no shell call.</summary>
public static class DetailsText
{
    /// <summary>
    /// Size the way Explorer's details view shows it: whole kilobytes, rounded up, with thousands
    /// separators ("0 KB", "1 KB" for 1 byte, "1,234 KB"). A negative value counts as 0.
    /// </summary>
    public static string FileSizeKb(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 KB";
        }

        // Not (bytes + 1023) / 1024: that overflows near long.MaxValue.
        long kb = bytes / 1024 + (bytes % 1024 == 0 ? 0 : 1);
        return $"{kb.ToString("N0", CultureInfo.InvariantCulture)} KB";
    }

    /// <summary>
    /// The 種類 text shown until (or instead of) the shell's type name: "ファイル フォルダー" for a
    /// folder, "JPG ファイル" for an extension (with or without the leading dot, any case), and
    /// "ファイル" when there is no extension. A folder's extension is ignored.
    /// </summary>
    public static string TypeFallback(string extension, bool isFolder)
    {
        if (isFolder)
        {
            return "ファイル フォルダー";
        }

        string ext = (extension ?? "").Trim().TrimStart('.');
        return ext.Length == 0 ? "ファイル" : $"{ext.ToUpperInvariant()} ファイル";
    }
}
