using System.Text;

namespace FolderDeck.Core.Paths;

/// <summary>The programs of the 「… で開く」 entries (blank area, item and selection menus).</summary>
public enum OpenInTool
{
    /// <summary>Windows Terminal; cmd when it is not installed.</summary>
    Terminal,

    /// <summary>PowerShell 7 (pwsh) when installed, else Windows PowerShell.</summary>
    PowerShell,
    Code,
    Wsl,
}

/// <summary>
/// What is installed for 「… で開く」, found by the app off the UI thread: the full paths of Windows
/// Terminal (wt.exe), PowerShell 7 (pwsh.exe) and VS Code (Code.exe), null when absent, and whether
/// WSL has a distribution. cmd and Windows PowerShell (System32) are always there.
/// </summary>
public sealed record OpenInPrograms(string? WindowsTerminal, string? Pwsh, string? Code, bool Wsl)
{
    public static OpenInPrograms None { get; } = new(null, null, null, false);
}

/// <summary>
/// Command lines of 「ターミナルで開く」「PowerShell で開く」 (開く / 管理者として開く), 「Code で開く」 and
/// 「WSL で開く」 for a folder. The folder goes on the command line wherever the program takes it: an
/// elevated process (ShellExecute runas) starts in System32 whatever the working directory. Programs
/// are full paths with System32 as the working directory (no program is taken from the folder).
/// </summary>
public static class OpenInCommand
{
    public const string CmdPercentRefusal = "名前に % を含むフォルダは、管理者として cmd で開けません。";

    /// <summary>
    /// The command line for <paramref name="tool"/> in <paramref name="folder"/> (a mapped network drive
    /// already as its UNC path when it must be). Null when <paramref name="tool"/> is not installed in
    /// <paramref name="programs"/> (Code, WSL); 「ターミナル」 without Windows Terminal is cmd.
    /// </summary>
    public static LaunchSpec? Build(OpenInTool tool, bool elevated, string folder, OpenInPrograms programs, string systemDirectory)
    {
        folder = PathTools.WithoutExtendedPrefix(folder);
        switch (tool)
        {
            case OpenInTool.Terminal:
                // wt splits its command line at ';' (even inside quotes) unless it is escaped.
                return programs.WindowsTerminal is { Length: > 0 } wt
                    ? new LaunchSpec(wt, "-d " + TerminalCommand.Quote(folder.Replace(";", "\\;", StringComparison.Ordinal)), systemDirectory)
                    : Cmd(elevated, folder, systemDirectory);
            case OpenInTool.PowerShell:
            {
                string exe = programs.Pwsh is { Length: > 0 } pwsh ? pwsh : Path.Combine(systemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
                string command = "Set-Location -LiteralPath " + PowerShellLiteral(folder);
                return new LaunchSpec(exe, "-NoExit -Command " + TerminalCommand.Quote(command), systemDirectory);
            }

            case OpenInTool.Code:
                return programs.Code is { Length: > 0 } code ? new LaunchSpec(code, TerminalCommand.Quote(folder), systemDirectory) : null;
            case OpenInTool.Wsl:
                // A distribution's share as -d distro --cd /path, another share in the home directory (Note).
                return programs.Wsl ? TerminalCommand.Build("wsl", string.Empty, folder, systemDirectory) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// <paramref name="text"/> as a single-quoted PowerShell string: every character PowerShell reads as
    /// a single quote (' ‘ ’ ‚ ‛) doubled; $ ` and the rest stay literal there.
    /// </summary>
    public static string PowerShellLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('\'');
        foreach (char c in text)
        {
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
            {
                sb.Append(c);
            }

            sb.Append(c);
        }

        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// cmd in <paramref name="folder"/>, for 「ターミナルで開く」 without Windows Terminal. Not elevated it
    /// is the address bar's cmd (started in the folder, a UNC folder through pushd). Elevated it always
    /// goes through pushd, in quotes (an elevated cmd starts in System32; pushd also changes the drive
    /// and maps one for a UNC folder). cmd expands %…% even inside quotes: such a folder is refused.
    /// </summary>
    private static LaunchSpec Cmd(bool elevated, string folder, string systemDirectory)
    {
        if (!elevated)
        {
            return TerminalCommand.Build("cmd", string.Empty, folder, systemDirectory);
        }

        return folder.Contains('%')
            ? LaunchSpec.Refused(CmdPercentRefusal)
            : new LaunchSpec(Path.Combine(systemDirectory, "cmd.exe"), "/k pushd \"" + folder + "\"", systemDirectory);
    }
}
