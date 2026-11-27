using System.Globalization;

namespace FolderDeck.Core.Text;

/// <summary>The status bar's text for a selection.</summary>
public static class SelectionSummary
{
    /// <summary>
    /// "3 個を選択　12.4 MB": the count and the total size of the selected files. Folders are not
    /// summed (their contents would need a recursive walk), so a selection with folders says
    /// "（フォルダを除く）". <paramref name="sizedFiles"/> counts only the files whose size is known
    /// (an online-only file may have none) and <paramref name="bytes"/> is their total; with no such
    /// file (folders only) there is no size at all.
    /// </summary>
    public static string Format(int count, int sizedFiles, long bytes, bool hasFolders)
    {
        string text = $"{count.ToString("N0", CultureInfo.InvariantCulture)} 個を選択";
        if (sizedFiles <= 0)
        {
            return text;
        }

        text += "　" + Formatting.FileSize(Math.Max(0, bytes));
        return hasFolders ? text + "（フォルダを除く）" : text;
    }
}
