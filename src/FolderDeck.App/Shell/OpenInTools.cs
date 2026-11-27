using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using Microsoft.Win32;

namespace FolderDeck.Shell;

/// <summary>
/// What 「… で開く」 can start (Windows Terminal, PowerShell 7, VS Code, WSL), looked for off the UI
/// thread (App Paths, PATH and files: PATH may hold a slow network folder) and kept for the process.
/// Until the first look ends, <see cref="Current"/> is a quick look of the usual places only (local
/// files and the registry, no PATH), so that a right click right after the start offers them too.
/// </summary>
public static class OpenInTools
{
    private static readonly long Stale = (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
    private static OpenInPrograms? _found; // the last full look; null until the first one ends
    private static long _nextLookAt; // Environment.TickCount64; 0 = look now
    private static int _looking;

    /// <summary>The last look (never null). UI thread: reads only local files and the registry.</summary>
    public static OpenInPrograms Current => Volatile.Read(ref _found) ?? QuickLook();

    /// <summary>
    /// Starts a full look in the background unless one runs or the last ended within a minute (an
    /// install or uninstall shows from the next right click on). Any thread.
    /// </summary>
    public static void Refresh()
    {
        if (Environment.TickCount64 < Interlocked.Read(ref _nextLookAt) || Interlocked.Exchange(ref _looking, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Volatile.Write(ref _found, FullLook());
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not look for the programs of 「… で開く」", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _nextLookAt, Environment.TickCount64 + Stale);
                Volatile.Write(ref _looking, 0);
            }
        });
    }

    private static OpenInPrograms QuickLook()
    {
        try
        {
            return new(Existing(WindowsTerminalAlias), Existing(DefaultPwsh), DefaultCodeLocations().FirstOrDefault(File.Exists), HasWslDistribution());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return OpenInPrograms.None;
        }
    }

    /// <summary>Blocking I/O (PATH): off the UI thread.</summary>
    private static OpenInPrograms FullLook() =>
        new(
            Existing(WindowsTerminalAlias) ?? Find("wt.exe"),
            Find("pwsh.exe") ?? Existing(DefaultPwsh),
            FindCode(),
            HasWslDistribution());

    /// <summary>The full path the shell would find for <paramref name="fileName"/> (App Paths, then PATH), or null.</summary>
    private static string? Find(string fileName) => ShellActions.FindProgram(fileName, excludedFolder: string.Empty);

    private static string? Existing(string path) => File.Exists(path) ? path : null;

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>The app execution alias of the Store (and inbox) Windows Terminal.</summary>
    private static string WindowsTerminalAlias => Path.Combine(LocalAppData, @"Microsoft\WindowsApps\wt.exe");

    private static string DefaultPwsh => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");

    private static IEnumerable<string> DefaultCodeLocations()
    {
        yield return Path.Combine(LocalAppData, @"Programs\Microsoft VS Code\Code.exe"); // user setup
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\Code.exe"); // system setup
    }

    /// <summary>
    /// Code.exe itself (as Explorer's 「Code で開く」 runs it; code.cmd would open a console window too):
    /// next to the code.cmd PATH finds (<c>&lt;install&gt;\bin\code.cmd</c>), else in the usual places.
    /// </summary>
    private static string? FindCode()
    {
        if (Find("code.cmd") is { } cmd
            && PathTools.ParentOf(PathTools.ParentOf(cmd)) is { Length: > 0 } install
            && PathTools.IsFullyQualified(install) // never a relative Code.exe (the current directory)
            && Existing(Path.Combine(install, "Code.exe")) is { } beside)
        {
            return beside;
        }

        return DefaultCodeLocations().FirstOrDefault(File.Exists) ?? Find("Code.exe");
    }

    /// <summary>
    /// wsl.exe is inbox on Windows 11 even without WSL (it offers to install it): a distribution must be
    /// registered for the user (a <c>{GUID}</c> key under Lxss).
    /// </summary>
    private static bool HasWslDistribution()
    {
        if (!File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (key is null)
            {
                return false;
            }

            // wsl.exe --cd opens the default distribution. Docker Desktop registers its own
            // (docker-desktop, docker-desktop-data), which is no shell for the user.
            if (key.GetValue("DefaultDistribution") is string preferred)
            {
                return IsUserDistribution(key, preferred);
            }

            return key.GetSubKeyNames().Any(name => name.StartsWith('{') && IsUserDistribution(key, name));
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static bool IsUserDistribution(RegistryKey lxss, string id)
    {
        using var distribution = lxss.OpenSubKey(id);
        return distribution?.GetValue("DistributionName") is string name
            && !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase);
    }
}
