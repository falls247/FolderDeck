using FolderDeck.Core.Infrastructure;

namespace FolderDeck.Core.Paths;

/// <summary>What the address bar does with what was typed in it (<see cref="AddressInput.Resolve"/>).</summary>
public enum AddressAction
{
    /// <summary>Nothing was typed: the edit just ends.</summary>
    None,

    /// <summary>A folder (<see cref="AddressTarget.Path"/>): the shown tab moves there.</summary>
    OpenFolder,

    /// <summary>A file (<see cref="AddressTarget.Path"/>): opened with its app, as a double click does.</summary>
    OpenFile,

    /// <summary>
    /// A program and its arguments (<see cref="AddressTarget.Program"/>, <see cref="AddressTarget.Arguments"/>),
    /// started with the shown folder as its current directory: <c>cmd</c>, <c>wsl</c>, <c>code .</c>, ….
    /// </summary>
    Run,

    /// <summary>It is a path, but there is nothing there (<see cref="AddressTarget.Path"/>).</summary>
    NotFound,
}

public sealed record AddressTarget(AddressAction Action, string? Path = null, string? Program = null, string Arguments = "")
{
    public static readonly AddressTarget Nothing = new(AddressAction.None);
}

/// <summary>
/// The address bar's input, read as Explorer reads it: an absolute path, a path relative to the
/// shown folder (<c>..</c>, <c>sub\dir</c>, <c>\Windows</c>), a name in the shown folder, or else a
/// command line run in the shown folder. A terminal's name (<see cref="TerminalCommand.KindOf"/>)
/// always runs, even when an item of that name is in the folder.
/// </summary>
public static class AddressInput
{
    /// <summary>
    /// What <paramref name="typed"/> stands for. <paramref name="currentFolder"/> is the folder shown
    /// (null at a top list: only absolute paths and commands then). <paramref name="kindOf"/> checks
    /// a path on disk: blocking I/O, so call this off the UI thread.
    /// </summary>
    public static AddressTarget Resolve(string? typed, string? currentFolder, Func<string, PathKind> kindOf)
    {
        ArgumentNullException.ThrowIfNull(kindOf);
        string text = (typed ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return AddressTarget.Nothing;
        }

        // Quotes on both ends off ("copy as path"), %VAR% expanded.
        string cleaned = PathTools.CleanInput(text);
        if (cleaned.Length == 0)
        {
            return AddressTarget.Nothing;
        }

        cleaned = PathTools.WithoutExtendedPrefix(cleaned); // "\\?\UNC\server\share" is shown (and given to cmd) as "\\server\share"
        if (cleaned.Length == 2 && char.IsAsciiLetter(cleaned[0]) && cleaned[1] == ':')
        {
            cleaned += "\\"; // "E:" is the root of the drive, not its current directory
        }

        string command = Expand(text);
        if (PathTools.IsFullyQualified(cleaned))
        {
            string full = PathTools.NormalizeOrSelf(cleaned);
            switch (kindOf(full))
            {
                case PathKind.Folder:
                    return new AddressTarget(AddressAction.OpenFolder, full);
                case PathKind.File:
                    return new AddressTarget(AddressAction.OpenFile, full);
            }

            // "C:\Tools\app.exe --help": a program with its arguments.
            var (program, arguments) = SplitCommand(command, kindOf);
            if (arguments.Length > 0 && PathTools.IsFullyQualified(program) && kindOf(program) == PathKind.File)
            {
                return new AddressTarget(AddressAction.Run, Program: program, Arguments: arguments);
            }

            return new AddressTarget(AddressAction.NotFound, full);
        }

        var (name, args) = SplitCommand(command, kindOf);
        if (name.Length == 0)
        {
            return AddressTarget.Nothing;
        }

        // A relative path ("..", "sub\dir"), unless it is a command whose arguments hold a separator
        // ("cmd /k dir", "code src\a.cs"): a terminal's name, a program below the folder with its
        // arguments ("bin\tool.exe --version"), or a first word without a separator whose text up to
        // the first separator is no folder here ("setup files\readme.txt" is a mistyped path).
        if (currentFolder is not null && LooksRelative(cleaned) && TerminalCommand.KindOf(name) == TerminalKind.None)
        {
            string? full = Combine(currentFolder, cleaned);
            if (full is not null)
            {
                switch (kindOf(full))
                {
                    case PathKind.Folder:
                        return new AddressTarget(AddressAction.OpenFolder, full);
                    case PathKind.File:
                        return new AddressTarget(AddressAction.OpenFile, full);
                }
            }

            if (args.Length > 0 && name.IndexOfAny(Separators) >= 0
                && Combine(currentFolder, name) is { } program && kindOf(program) == PathKind.File)
            {
                return new AddressTarget(AddressAction.Run, Program: program, Arguments: args);
            }

            int separator = cleaned.IndexOfAny(Separators);
            bool isCommand = args.Length > 0 && name.IndexOfAny(Separators) < 0
                && !(separator >= 0 && Combine(currentFolder, cleaned[..separator]) is { } head && kindOf(head) == PathKind.Folder);
            if (!isCommand)
            {
                return new AddressTarget(AddressAction.NotFound, full ?? cleaned);
            }
        }

        // The name of an item in the shown folder ("src", "read me.md"), unless its first word names a
        // terminal. Only a name the system keeps as typed: "code ." is not the subfolder "code" (the
        // path rules would drop the trailing " .").
        if (currentFolder is not null
            && TerminalCommand.KindOf(name) == TerminalKind.None
            && IsPlainName(cleaned)
            && Combine(currentFolder, cleaned) is { } child
            && PathTools.Comparer.Equals(PathTools.DisplayName(child), cleaned))
        {
            switch (kindOf(child))
            {
                case PathKind.Folder:
                    return new AddressTarget(AddressAction.OpenFolder, child);
                case PathKind.File:
                    return new AddressTarget(AddressAction.OpenFile, child);
            }
        }

        return new AddressTarget(AddressAction.Run, Program: name, Arguments: args);
    }

    /// <summary>
    /// Splits a command line into the program and the rest: a quoted program (<c>"C:\a b\x.exe" -v</c>),
    /// an absolute path with spaces that names a file (<c>C:\Program Files\x\x.exe -v</c>, the longest
    /// such prefix), or else the first word.
    /// </summary>
    public static (string Program, string Arguments) SplitCommand(string text, Func<string, PathKind> kindOf)
    {
        text = text.Trim();
        if (text.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            return close < 0
                ? (text[1..].Trim(), string.Empty)
                : (text[1..close].Trim(), text[(close + 1)..].Trim());
        }

        if (PathTools.IsFullyQualified(text))
        {
            for (int end = text.Length; end > 0; end = text.LastIndexOf(' ', end - 1))
            {
                string head = text[..end].TrimEnd();
                if (head.Length > 0 && PathTools.IsFullyQualified(head) && kindOf(head) == PathKind.File)
                {
                    return (head, text[end..].Trim());
                }
            }
        }

        int space = text.IndexOfAny([' ', '\t']);
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }

    private static readonly char[] Separators = ['\\', '/'];

    private static string Expand(string text) =>
        text.Contains('%') ? Environment.ExpandEnvironmentVariables(text).Trim() : text;

    /// <summary>A path meant below (or beside) the shown folder: <c>.</c>, <c>..</c>, <c>a\b</c>, <c>\Windows</c>. Never a URL or <c>shell:</c>.</summary>
    private static bool LooksRelative(string text) =>
        !text.Contains(':')
        && (text is "." or ".." || text.Contains('\\') || text.Contains('/'));

    /// <summary>One name (spaces allowed): no separator, no drive or scheme, no quote.</summary>
    private static bool IsPlainName(string text) =>
        text.IndexOfAny(['\\', '/', ':', '"']) < 0 && text is not ("." or "..");

    private static string? Combine(string folder, string relative)
    {
        try
        {
            return PathTools.Normalize(Path.GetFullPath(relative, folder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
