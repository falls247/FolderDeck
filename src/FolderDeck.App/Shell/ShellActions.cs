using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Shell;

/// <summary>The outcome of opening or starting something. <see cref="Cancelled"/>: the user declined (the elevation prompt).</summary>
public sealed record OpenResult(bool Success, string? Error, bool Cancelled = false);

/// <summary>Opening files and folders through the shell (§11, §12).</summary>
public static class ShellActions
{
    private const int ErrorNoAssociation = 1155;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorCancelled = 1223;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".cpl", ".hta",
    };

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(PathTools.ExtensionOf(path));

    /// <summary>"PC" (This PC) as a shell namespace path; explorer.exe and Navigate2 both accept it.</summary>
    public const string ThisPcPath = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";

    /// <summary>A shell namespace path ("::{CLSID}") rather than a file system path.</summary>
    public static bool IsShellNamespace(string path) => path.StartsWith("::{", StringComparison.Ordinal);

    /// <summary>Opens a file with its default app, like a double click in Explorer. Blocking: run off the UI thread.</summary>
    public static OpenResult Open(string path)
    {
        try
        {
            StartShell(path);
            return new OpenResult(true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoAssociation)
        {
            return new OpenResult(false, "このファイルを開くアプリが関連付けられていません。");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Shell APIs are limited to MAX_PATH: retry with the 8.3 short path when that helps.
            if (path.Length >= 260 && ShellInterop.GetShortPath(path) is { } shortPath && shortPath.Length < 260)
            {
                try
                {
                    StartShell(shortPath);
                    return new OpenResult(true, null);
                }
                catch (Win32Exception ex2) when (ex2.NativeErrorCode == ErrorNoAssociation)
                {
                    return new OpenResult(false, "このファイルを開くアプリが関連付けられていません。");
                }
                catch (Exception ex2) when (ex2 is Win32Exception or InvalidOperationException or IOException)
                {
                    AppLog.Warn($"Could not open (short path) {path}", ex2);
                }
            }

            AppLog.Warn($"Could not open {path}", ex);
            return new OpenResult(false, "開けませんでした：" + ex.Message);
        }
    }

    /// <summary><c>explorer.exe /select,"path"</c>: parent folder with the item selected.</summary>
    public static OpenResult ShowInExplorer(string path) =>
        RunExplorer($"/select,\"{ShellPath(path)}\"");

    /// <summary><c>explorer.exe "path"</c>.</summary>
    public static OpenResult OpenInExplorer(string path) =>
        RunExplorer($"\"{ShellPath(path)}\"");

    /// <summary>
    /// Starts a program typed in the address bar (a terminal, <c>code .</c>, …) through the shell, as
    /// Explorer's address bar and the Run dialog do, in <see cref="LaunchSpec.WorkingDirectory"/>.
    /// With <paramref name="elevated"/> as administrator (runas: the UAC prompt; the process then starts
    /// in System32 whatever the working directory). Blocking (until the prompt is answered): run off the
    /// UI thread.
    /// </summary>
    public static OpenResult Launch(LaunchSpec spec, bool elevated = false)
    {
        try
        {
            var info = new ProcessStartInfo(spec.FileName)
            {
                Arguments = spec.Arguments,
                UseShellExecute = true,
                WorkingDirectory = spec.WorkingDirectory,
            };
            if (elevated)
            {
                info.Verb = "runas";
            }

            using var process = Process.Start(info);
            return new OpenResult(true, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return new OpenResult(false, $"「{spec.FileName}」が見つかりません。名前を確かめてください。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new OpenResult(false, $"「{spec.FileName}」の実行を取り消しました。", Cancelled: true); // e.g. the elevation prompt was declined
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            AppLog.Warn($"Could not start {spec.FileName} in {spec.WorkingDirectory}", ex);
            return new OpenResult(false, $"「{spec.FileName}」を実行できませんでした：{ex.Message}");
        }
    }

    /// <summary>
    /// The full path of a program the shell would find by name (its App Paths entry, then the folders of
    /// PATH), never in <paramref name="excludedFolder"/> nor in a relative PATH entry: the shell would
    /// look in the working directory first, where anyone may have put a program of that name. Null when
    /// it is not found. Blocking I/O: run off the UI thread.
    /// </summary>
    public static string? FindProgram(string fileName, string excludedFolder)
    {
        bool Usable(string? candidate) =>
            candidate is { Length: > 0 }
            && PathTools.IsFullyQualified(candidate)
            && !PathTools.PathEquals(PathTools.ParentOf(candidate), PathTools.NormalizeOrSelf(excludedFolder))
            && File.Exists(candidate);

        foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + fileName);
                string? registered = (key?.GetValue(null) as string)?.Trim('"');
                if (registered is not null)
                {
                    registered = Environment.ExpandEnvironmentVariables(registered);
                    if (Usable(registered))
                    {
                        return registered;
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
            }
        }

        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(folder.Trim('"')), fileName);
                if (Usable(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    public static bool CopyPath(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(path);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
            }
            catch (ExternalException)
            {
                Thread.Sleep(30);
            }
        }

        return false;
    }

    private static void StartShell(string path)
    {
        string? dir = null;
        try
        {
            dir = Path.GetDirectoryName(path);
        }
        catch (ArgumentException)
        {
        }

        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        };
        if (!string.IsNullOrEmpty(dir))
        {
            info.WorkingDirectory = dir;
        }

        using var process = Process.Start(info);
    }

    private static OpenResult RunExplorer(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
            return new OpenResult(true, null);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warn($"explorer.exe {arguments} failed", ex);
            return new OpenResult(false, "エクスプローラーを開けませんでした：" + ex.Message);
        }
    }

    /// <summary>The path itself, or its 8.3 short form when it is too long for the shell (MAX_PATH).</summary>
    internal static string ShellPath(string path) => PathTools.ForShell(path, ShellInterop.GetShortPath);
}
