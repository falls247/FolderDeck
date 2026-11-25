namespace FolderDeck.Core.Paths;

/// <summary>
/// Windows path ⇄ the same place seen from WSL, for the address bar's Ctrl + Shift + C:
/// <c>C:\temp</c> → <c>/mnt/c/temp</c>, a folder of a distribution (<c>\\wsl.localhost\Ubuntu\home\me</c>)
/// → <c>/home/me</c>, and back from <c>/mnt/c/temp</c> to <c>C:\temp</c>. Text only: nothing on disk is read.
/// </summary>
public static class WslPath
{
    /// <summary>
    /// The other form of <paramref name="text"/>: a WSL path for a Windows one, a Windows one for
    /// <c>/mnt/x/…</c>. Null when it has none (other shares, other Linux paths, not a path).
    /// </summary>
    public static string? Toggle(string? text)
    {
        string value = PathTools.CleanInput(text);
        return value.StartsWith('/') && !value.StartsWith("//", StringComparison.Ordinal) ? FromWsl(value) : ToWsl(value);
    }

    /// <summary>
    /// <c>C:\temp\a b</c> → <c>/mnt/c/temp/a b</c> (drive letter in lower case, as WSL mounts it; no
    /// quoting or escaping), <c>C:\</c> → <c>/mnt/c</c>, <c>C:temp</c> → <c>/mnt/c/temp</c>,
    /// <c>\\?\C:\x</c> as <c>C:\x</c>, <c>\\wsl.localhost\Ubuntu\home</c> → <c>/home</c>.
    /// Null for other shares and anything without a drive letter.
    /// </summary>
    public static string? ToWsl(string? text)
    {
        string value = PathTools.CleanInput(text);
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal) || value.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            string rest = value[4..];
            value = rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + rest[4..] : rest;
        }

        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
        {
            return TerminalCommand.WslPathOf(value.Replace('/', '\\'))?.LinuxPath;
        }

        if (value.Length < 2 || value[1] != ':' || !char.IsAsciiLetter(value[0]))
        {
            return null;
        }

        string[] parts = value[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        string drive = "/mnt/" + char.ToLowerInvariant(value[0]);
        return parts.Length == 0 ? drive : drive + "/" + string.Join('/', parts);
    }

    /// <summary>
    /// <c>/mnt/c/temp</c> → <c>C:\temp</c>, <c>/mnt/c</c> → <c>C:\</c>. Null for other Linux paths
    /// (which distribution they are in is not known).
    /// </summary>
    public static string? FromWsl(string? text)
    {
        string value = PathTools.CleanInput(text);
        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (!value.StartsWith('/') || parts.Length < 2 || parts[0] != "mnt"
            || parts[1].Length != 1 || !char.IsAsciiLetter(parts[1][0]))
        {
            return null;
        }

        return char.ToUpperInvariant(parts[1][0]) + @":\" + string.Join('\\', parts, 2, parts.Length - 2);
    }
}
