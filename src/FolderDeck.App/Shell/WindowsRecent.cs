using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Ranking;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

/// <summary>Reads Windows' "最近使った項目" (<c>%APPDATA%\Microsoft\Windows\Recent\*.lnk</c>, §10.2).</summary>
public static class WindowsRecent
{
    public const int MaxLinks = 300;

    /// <summary>
    /// <c>HKCU\…\Explorer\Advanced\Start_TrackDocs</c>: 0 means Windows does not record recent
    /// documents (estimate, see §20 U3). Missing value means enabled.
    /// </summary>
    public static bool IsTrackingEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            object? value = key?.GetValue("Start_TrackDocs");
            return value is not int i || i != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>Must run on an STA thread. Never calls <c>IShellLink::Resolve</c> (slow, may show UI).</summary>
    public static List<WindowsRecentItem> Read(CancellationToken cancellationToken)
    {
        var result = new List<WindowsRecentItem>();
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return result;
        }

        List<FileInfo> links;
        try
        {
            links = new DirectoryInfo(dir)
                .EnumerateFiles("*.lnk", new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true })
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxLinks)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not list the Recent folder", ex);
            return result;
        }

        foreach (var link in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? target = ReadTarget(link.FullName);
            if (target is null)
            {
                continue;
            }

            result.Add(new WindowsRecentItem(target, new DateTimeOffset(link.LastWriteTimeUtc, TimeSpan.Zero)));
        }

        return result;
    }

    /// <summary>The link's raw target path with environment variables expanded, or null.</summary>
    public static unsafe string? ReadTarget(string lnkPath)
    {
        object? obj = null;
        try
        {
            obj = new ShellLink();
            var persist = (IPersistFile)obj;
            fixed (char* p = lnkPath)
            {
                persist.Load(new PCWSTR(p), STGM.STGM_READ);
            }

            var link = (IShellLinkW)obj;
            const int Capacity = 2048;
            char* buffer = stackalloc char[Capacity];
            const uint SLGP_RAWPATH = 0x4;
            HRESULT hr = link.GetPath(new PWSTR(buffer), Capacity, null, SLGP_RAWPATH);
            if (hr.Value != 0)
            {
                return null;
            }

            string path = new string(buffer);
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            path = Environment.ExpandEnvironmentVariables(path);
            return Path.IsPathFullyQualified(path) ? path : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or UnauthorizedAccessException or FileNotFoundException or IOException)
        {
            return null;
        }
        finally
        {
            if (obj is not null && Marshal.IsComObject(obj))
            {
                Marshal.ReleaseComObject(obj);
            }
        }
    }
}
