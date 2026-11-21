using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Shell;

public sealed record OpenResult(bool Success, string? Error);

/// <summary>Opening files and folders through the shell (§11, §12).</summary>
public static class ShellActions
{
    private const int ErrorNoAssociation = 1155;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".cpl", ".hta",
    };

    public static bool IsExecutable(string path) => ExecutableExtensions.Contains(PathTools.ExtensionOf(path));

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

    private static string ShellPath(string path)
    {
        if (path.Length >= 260 && ShellInterop.GetShortPath(path) is { } shortPath && shortPath.Length < 260)
        {
            return shortPath;
        }

        return path;
    }
}
