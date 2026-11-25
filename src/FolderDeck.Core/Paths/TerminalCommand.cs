using System.Text;

namespace FolderDeck.Core.Paths;

/// <summary>The terminals the address bar opens in the shown folder by name (Explorer's <c>cmd</c>, <c>wsl</c>, …).</summary>
public enum TerminalKind
{
    /// <summary>Not a terminal: any other program, started with the folder as its current directory.</summary>
    None,
    Cmd,
    PowerShell,
    Pwsh,
    Wsl,
    WindowsTerminal,
}

/// <summary>
/// How to start a program for the address bar: always through the shell (ShellExecute, as Explorer
/// and the Run dialog do), with <see cref="WorkingDirectory"/> as its current directory.
/// <see cref="Note"/> is said on the message line (the folder could not be used as asked).
/// With <see cref="Refusal"/> nothing is started: the reason is said instead.
/// </summary>
public sealed record LaunchSpec(string FileName, string Arguments, string WorkingDirectory, string? Note = null, string? Refusal = null)
{
    public static LaunchSpec Refused(string reason) => new(string.Empty, string.Empty, string.Empty, Refusal: reason);
}

/// <summary>
/// Command lines that open a terminal in a folder. A terminal's own way of being told the folder is
/// used where its current directory is not enough: cmd cannot start in a UNC path (<c>pushd</c> maps
/// a drive for it), wsl takes <c>--cd</c> (a folder of a WSL distribution as <c>-d distro --cd /path</c>:
/// wsl refuses its UNC form), Windows Terminal takes <c>-d</c>.
/// </summary>
public static class TerminalCommand
{
    public const string WslHomeNote = "WSL ではネットワーク上のフォルダを開けないため、ホーム ディレクトリで開きました。";

    public const string CmdUncArgumentsRefusal =
        "ネットワーク上のフォルダでは、引数を付けた cmd は実行できません（cmd は UNC のパスを現在のフォルダにできず、Windows のフォルダで実行してしまうため）。cmd とだけ入力すると、ドライブを割り当てて開きます。";

    public const string CmdUncPercentRefusal = "名前に % を含むネットワーク上のフォルダでは cmd を開けません。";

    /// <summary>The terminal a program name stands for: a bare name with or without <c>.exe</c> (<c>cmd</c>, <c>WSL.exe</c>), never a path.</summary>
    public static TerminalKind KindOf(string program)
    {
        string name = (program ?? string.Empty).Trim();
        if (name.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return TerminalKind.None;
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.ToLowerInvariant() switch
        {
            "cmd" => TerminalKind.Cmd,
            "powershell" => TerminalKind.PowerShell,
            "pwsh" => TerminalKind.Pwsh,
            "wsl" => TerminalKind.Wsl,
            "wt" => TerminalKind.WindowsTerminal,
            _ => TerminalKind.None,
        };
    }

    /// <summary>
    /// The command line for <paramref name="program"/> (its typed name) with <paramref name="arguments"/>
    /// (as typed, appended unchanged) in <paramref name="folder"/>. A terminal is never taken from the
    /// folder itself (the shell looks in the working directory first): cmd, PowerShell and wsl are
    /// started by their full path in <paramref name="systemDirectory"/> (System32), pwsh by the full
    /// path <paramref name="locate"/> finds for it (outside the folder) or else from System32 with
    /// <c>-WorkingDirectory</c>, wt from System32 with <c>-d</c>. Any other program goes to the shell
    /// as typed, as in Explorer.
    /// </summary>
    public static LaunchSpec Build(string program, string arguments, string folder, string systemDirectory, Func<string, string?>? locate = null)
    {
        arguments = (arguments ?? string.Empty).Trim();
        folder = PathTools.WithoutExtendedPrefix(folder); // "\\?\UNC\…" is a UNC folder for cmd too
        bool unc = IsUnc(folder);
        switch (KindOf(program))
        {
            case TerminalKind.Cmd:
            {
                string cmd = Path.Combine(systemDirectory, "cmd.exe");
                if (!unc)
                {
                    return new LaunchSpec(cmd, arguments, folder);
                }

                // cmd refuses a UNC current directory (it would run in the Windows folder): pushd maps a
                // drive. Always in quotes: cmd reads the rest of /k as a command line, where & ^ ( ) |
                // outside quotes are its own (no Windows name holds a quote). It expands %…% even inside
                // quotes, and arguments would run outside the folder: both are refused.
                if (arguments.Length > 0)
                {
                    return LaunchSpec.Refused(CmdUncArgumentsRefusal);
                }

                return folder.Contains('%')
                    ? LaunchSpec.Refused(CmdUncPercentRefusal)
                    : new LaunchSpec(cmd, "/k pushd \"" + folder + "\"", systemDirectory);
            }

            case TerminalKind.PowerShell:
                return new LaunchSpec(Path.Combine(systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"), arguments, folder);
            case TerminalKind.Pwsh:
                return locate?.Invoke("pwsh.exe") is { Length: > 0 } pwsh
                    ? new LaunchSpec(pwsh, arguments, folder)
                    : new LaunchSpec("pwsh.exe", Join("-WorkingDirectory " + Quote(folder), arguments), systemDirectory);
            case TerminalKind.Wsl:
            {
                string wsl = Path.Combine(systemDirectory, "wsl.exe");
                if (WslPathOf(folder) is { } inDistro)
                {
                    return new LaunchSpec(wsl, Join($"-d {Quote(inDistro.Distro)} --cd {Quote(inDistro.LinuxPath)}", arguments), systemDirectory);
                }

                return unc
                    ? new LaunchSpec(wsl, Join("--cd ~", arguments), systemDirectory, WslHomeNote)
                    : new LaunchSpec(wsl, Join("--cd " + Quote(folder), arguments), folder);
            }

            case TerminalKind.WindowsTerminal:
                // wt splits its command line at ';' (even inside quotes) unless it is escaped.
                return new LaunchSpec("wt.exe", Join("-d " + Quote(folder.Replace(";", "\\;", StringComparison.Ordinal)), arguments), systemDirectory);
            default:
                return new LaunchSpec(program, arguments, folder);
        }
    }

    /// <summary>
    /// A folder of a WSL distribution (<c>\\wsl.localhost\Ubuntu\home\me</c>, <c>\\wsl$\Ubuntu</c>):
    /// the distribution and the Linux path (<c>/home/me</c>, <c>/</c>). Null for any other path.
    /// </summary>
    public static (string Distro, string LinuxPath)? WslPathOf(string folder)
    {
        if (!IsUnc(folder))
        {
            return null;
        }

        string[] parts = folder[2..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !(parts[0].Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("wsl$", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return (parts[1], "/" + string.Join('/', parts, 2, parts.Length - 2));
    }

    /// <summary>
    /// One argument for a Windows command line (the rules of <c>CommandLineToArgvW</c>): unchanged
    /// when it needs no quotes, else in quotes with inner quotes escaped and the backslashes before a
    /// quote (and before the closing one: <c>"C:\a b\\"</c>) doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var sb = new StringBuilder(argument.Length + 2);
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                sb.Append('\\', backslashes);
            }

            backslashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal);

    private static string Join(string first, string rest) => rest.Length == 0 ? first : first + " " + rest;
}
