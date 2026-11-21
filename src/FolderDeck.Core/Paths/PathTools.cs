namespace FolderDeck.Core.Paths;

/// <summary>Input cleanup, normalization, comparison keys and display names for paths.</summary>
public static class PathTools
{
    /// <summary>Paths are compared case-insensitively after normalization.</summary>
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    public const string MsgEmptyInput = "フォルダのパスを入力してください。";
    public const string MsgNotAbsolute = "絶対パスで指定してください: {0}";
    public const string MsgFolderNotFound = "フォルダが見つかりません: {0}";

    private const string Quotes = "\"'“”‘’";

    /// <summary>
    /// Cleans a pasted path: trims whitespace, removes quotes on both ends until none are
    /// left (Explorer's "copy as path"), then expands <c>%VAR%</c>.
    /// </summary>
    public static string CleanInput(string? text)
    {
        string value = (text ?? string.Empty).Trim();
        while (value.Length >= 2 && Quotes.Contains(value[0]) && Quotes.Contains(value[^1]))
        {
            value = value[1..^1].Trim();
        }

        if (value.Contains('%'))
        {
            value = Environment.ExpandEnvironmentVariables(value).Trim();
        }

        return value;
    }

    /// <summary>
    /// Validates a cleaned input and returns the normalized absolute path (no existence check).
    /// </summary>
    public static bool TryNormalizeInput(string? rawInput, out string normalized, out string? error)
    {
        normalized = string.Empty;
        string cleaned = CleanInput(rawInput);
        if (cleaned.Length == 0)
        {
            error = MsgEmptyInput;
            return false;
        }

        if (!IsFullyQualified(cleaned))
        {
            error = string.Format(MsgNotAbsolute, cleaned);
            return false;
        }

        try
        {
            normalized = Normalize(cleaned);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = string.Format(MsgNotAbsolute, cleaned);
            return false;
        }

        error = null;
        return true;
    }

    public static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>Path.GetFullPath</c> plus removal of trailing separators. Drive roots such as
    /// <c>E:\</c> keep their separator.
    /// </summary>
    public static string Normalize(string path)
    {
        string full = Path.GetFullPath(path);
        return TrimTrailingSeparators(full);
    }

    /// <summary>Normalizes when possible; returns the input unchanged when it is not a valid path.</summary>
    public static string NormalizeOrSelf(string path)
    {
        try
        {
            return IsFullyQualified(path) ? Normalize(path) : path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    public static string TrimTrailingSeparators(string path)
    {
        string root = SafeGetRoot(path);
        int end = path.Length;
        // Never strip below the root ("E:\" keeps its separator, "\\server\share\" → "\\server\share").
        while (end > root.Length && end > 0 && IsSeparator(path[end - 1]))
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
    }

    /// <summary>True for <c>E:\</c>, <c>\\server\share</c> and similar.</summary>
    public static bool IsRoot(string path)
    {
        string root = SafeGetRoot(path);
        if (root.Length == 0)
        {
            return false;
        }

        return string.Equals(TrimSeparatorsAll(root), TrimSeparatorsAll(path), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Last path component, or the whole path for a drive root (<c>E:\</c>).</summary>
    public static string DisplayName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        if (IsRoot(path))
        {
            return path;
        }

        string trimmed = TrimTrailingSeparators(path);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>Parent folder, or an empty string for a root.</summary>
    public static string ParentOf(string path)
    {
        try
        {
            return Path.GetDirectoryName(TrimTrailingSeparators(path)) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    public static bool PathEquals(string? a, string? b) => Comparer.Equals(a, b);

    /// <summary>Joins a relative path ("a\b") and a child name.</summary>
    public static string JoinRel(string rel, string name) =>
        string.IsNullOrEmpty(rel) ? name : rel + "\\" + name;

    /// <summary>Absolute path of <paramref name="rel"/> below <paramref name="root"/>.</summary>
    public static string Combine(string root, string rel)
    {
        if (string.IsNullOrEmpty(rel))
        {
            return root;
        }

        return Path.Join(root, rel);
    }

    public static string[] SplitRel(string? rel) =>
        string.IsNullOrEmpty(rel)
            ? []
            : rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>A relative path is safe when it does not climb out of its root.</summary>
    public static bool IsSafeRel(string? rel)
    {
        if (string.IsNullOrEmpty(rel))
        {
            return true;
        }

        if (Path.IsPathRooted(rel) || rel.Contains(':'))
        {
            return false;
        }

        foreach (string part in SplitRel(rel))
        {
            if (part is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Parent of a relative path ("a\b" → "a", "a" → "").</summary>
    public static string ParentRel(string rel)
    {
        string[] parts = SplitRel(rel);
        return parts.Length <= 1 ? string.Empty : string.Join('\\', parts, 0, parts.Length - 1);
    }

    /// <summary>
    /// Relative path of <paramref name="path"/> below <paramref name="root"/>, or null when it is
    /// not inside it. Equal paths give an empty string.
    /// </summary>
    public static string? RelativeTo(string root, string path)
    {
        string r = TrimTrailingSeparators(root);
        string p = TrimTrailingSeparators(path);
        if (Comparer.Equals(r, p))
        {
            return string.Empty;
        }

        string prefix = IsSeparator(r[^1]) ? r : r + "\\";
        if (p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return p[prefix.Length..];
        }

        return null;
    }

    /// <summary>Lower-case extension including the dot, or an empty string.</summary>
    public static string ExtensionOf(string path)
    {
        try
        {
            return Path.GetExtension(path).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string SafeGetRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(path) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    private static string TrimSeparatorsAll(string s) => s.TrimEnd('\\', '/');

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
