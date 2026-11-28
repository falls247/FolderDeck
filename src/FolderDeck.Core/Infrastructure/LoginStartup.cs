namespace FolderDeck.Core.Infrastructure;

/// <summary>What the Run key holds for FolderDeck, compared with the running exe.</summary>
public enum LoginStartupState
{
    /// <summary>No value (or an empty one).</summary>
    NotRegistered,

    /// <summary>The running exe with <c>--background</c>: the sign-in starts this copy, hidden.</summary>
    Registered,

    /// <summary>
    /// A value that would not start this copy hidden: another exe (moved, or another copy) or no
    /// <c>--background</c>. Counted as not registered; turning the setting on writes it again.
    /// </summary>
    Other,
}

/// <summary>
/// 常駐モードの「ログオン時に起動」: the decisions about the value
/// <see cref="ValueName"/> of <c>HKCU\<see cref="RunKeyPath"/></c>. Reading and writing the
/// registry is the App's (<c>Shell/RunKeyRegistry</c>); nothing here touches it.
/// </summary>
public static class LoginStartup
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public const string ValueName = "FolderDeck";

    /// <summary>Started without a window: it waits in the notification area.</summary>
    public const string BackgroundArg = "--background";

    /// <summary>The value written: the exe in quotes (a path may hold spaces) and <see cref="BackgroundArg"/>.</summary>
    public static string BuildCommand(string exePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        return $"\"{exePath.Trim()}\" {BackgroundArg}";
    }

    /// <summary>Whether the command line asks for the start without a window.</summary>
    public static bool IsBackgroundLaunch(IEnumerable<string> args) =>
        args.Any(a => string.Equals(a?.Trim(), BackgroundArg, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Compares the registered <paramref name="command"/> with <paramref name="exePath"/>. A value
    /// typed by hand may lack the quotes; then the exe is everything up to <c>.exe</c>.
    /// </summary>
    public static LoginStartupState StateOf(string? command, string exePath)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return LoginStartupState.NotRegistered;
        }

        if (!TrySplit(command.Trim(), out string exe, out string args))
        {
            return LoginStartupState.Other;
        }

        bool sameExe = string.Equals(Full(exe), Full(exePath), StringComparison.OrdinalIgnoreCase);
        bool background = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(a => string.Equals(a, BackgroundArg, StringComparison.OrdinalIgnoreCase));
        return sameExe && background ? LoginStartupState.Registered : LoginStartupState.Other;
    }

    private static bool TrySplit(string command, out string exe, out string args)
    {
        exe = string.Empty;
        args = string.Empty;
        if (command.StartsWith('"'))
        {
            int close = command.IndexOf('"', 1);
            if (close <= 1)
            {
                return false;
            }

            exe = command[1..close];
            args = command[(close + 1)..];
            return true;
        }

        int end = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return false;
        }

        end += ".exe".Length;
        exe = command[..end];
        args = command[end..];
        return true;
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }
}
