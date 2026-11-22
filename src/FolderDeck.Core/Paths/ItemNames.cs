namespace FolderDeck.Core.Paths;

/// <summary>Why a name typed for a file or folder cannot be used (<see cref="ItemNames.Check"/>).</summary>
public enum NameProblem
{
    None,

    /// <summary>Nothing is left after <see cref="ItemNames.Clean"/>.</summary>
    Empty,

    /// <summary>One of <c>\ / : * ? " &lt; &gt; |</c> or a control character.</summary>
    InvalidCharacter,

    /// <summary>A device name of Windows (CON, NUL, COM1…), with or without an extension.</summary>
    ReservedName,

    /// <summary>Longer than <see cref="ItemNames.MaxLength"/> characters.</summary>
    TooLong,
}

/// <summary>
/// The rules for the name of one file or folder as the user types it (rename, new folder), without
/// I/O: the shell does the renaming and still checks the file system (access, an item that appeared
/// meanwhile). Everything here must be refused before the shell is asked: it creates device names
/// (CON, NUL.txt…) as ordinary files, and a name with <c>: \ /</c> makes it offer to create folders.
/// </summary>
public static class ItemNames
{
    /// <summary>The name Explorer gives a new folder on Japanese Windows; the next free one gets " (2)", " (3)"….</summary>
    public const string NewFolderBase = "新しいフォルダー";

    /// <summary>
    /// Longest name accepted. NTFS allows 255, but the shell's <c>IFileOperation.RenameItem</c>
    /// refuses a 255-character name (ERROR_INSUFFICIENT_BUFFER, seen on Windows 11 26200).
    /// </summary>
    public const int MaxLength = 254;

    /// <summary>The characters a name cannot contain, as Explorer lists them.</summary>
    public const string InvalidCharsText = "\\ / : * ? \" < > |";

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
        "CONIN$", "CONOUT$",
    };

    /// <summary>
    /// The name as it would be stored, as Explorer's edit box leaves it: spaces at both ends and dots
    /// at the end are dropped (Win32 drops trailing dots and spaces anyway; the shell reports the
    /// name it was given, not the one on disk, so the name is cleaned before it is handed over).
    /// Only the ASCII space counts: an ideographic space (U+3000) stays, as on disk.
    /// </summary>
    public static string Clean(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return input.Trim(' ').TrimEnd('.', ' ');
    }

    /// <summary>A character no file or folder name may contain.</summary>
    public static bool IsInvalidChar(char c) =>
        c < ' ' || c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|';

    /// <summary>What is wrong with <paramref name="name"/> (pass the result of <see cref="Clean"/>), or <see cref="NameProblem.None"/>.</summary>
    public static NameProblem Check(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return NameProblem.Empty;
        }

        foreach (char c in name)
        {
            if (IsInvalidChar(c))
            {
                return NameProblem.InvalidCharacter;
            }
        }

        if (name.Length > MaxLength)
        {
            return NameProblem.TooLong;
        }

        // A device name stays one with any extension ("CON.txt") and with spaces before the dot.
        int dot = name.IndexOf('.');
        string stem = (dot < 0 ? name : name[..dot]).TrimEnd(' ');
        return Reserved.Contains(stem) ? NameProblem.ReservedName : NameProblem.None;
    }

    /// <summary>The message line text for a <see cref="NameProblem"/> (empty for <see cref="NameProblem.None"/>).</summary>
    public static string MessageOf(NameProblem problem) => problem switch
    {
        NameProblem.Empty => "名前を入力してください。",
        NameProblem.InvalidCharacter => "名前に次の文字は使えません: " + InvalidCharsText,
        NameProblem.ReservedName => "この名前は Windows が予約しているため使えません。",
        NameProblem.TooLong => $"名前が長すぎます（{MaxLength} 文字まで）。",
        _ => string.Empty,
    };

    /// <summary>
    /// The part of <paramref name="name"/> selected when the edit box opens, as Explorer does: a
    /// file's name without its extension (the text before the last dot, when that dot is not the
    /// first character), a folder's whole name.
    /// </summary>
    public static (int Start, int Length) EditSelection(string name, bool isFolder)
    {
        if (isFolder || string.IsNullOrEmpty(name))
        {
            return (0, name?.Length ?? 0);
        }

        int dot = name.LastIndexOf('.');
        return dot > 0 ? (0, dot) : (0, name.Length);
    }

    /// <summary>
    /// True when renaming the file <paramref name="oldName"/> to <paramref name="newName"/> changes
    /// its extension (added, removed or another one; the case of the letters does not count).
    /// Explorer asks before such a rename. The extension is what Windows takes for it (from the last
    /// dot, also the first character: ".gitignore" is all extension), unlike <see cref="EditSelection"/>,
    /// which selects such a name whole.
    /// </summary>
    public static bool ExtensionChanged(string oldName, string newName) =>
        !string.Equals(ExtensionPart(oldName), ExtensionPart(newName), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The name of a new folder that none of <paramref name="existingNames"/> has (compared without
    /// case): <paramref name="baseName"/>, then "<paramref name="baseName"/> (2)", " (3)"… like Explorer.
    /// </summary>
    public static string NextNewFolderName(IEnumerable<string> existingNames, string baseName = NewFolderBase)
    {
        var taken = new HashSet<string>(existingNames.Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(baseName))
        {
            return baseName;
        }

        for (int n = 2; ; n++)
        {
            string candidate = $"{baseName} ({n})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string ExtensionPart(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[dot..] : string.Empty;
    }
}
