using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;

namespace FolderDeck.Shell;

/// <summary>What an Alt + drop made: the new .lnk files, and the first error when some could not be made.</summary>
public sealed record ShortcutResult(int Requested, IReadOnlyList<string> Made, string? Error);

/// <summary>
/// The only place that makes shortcuts (.lnk): Alt + drop, as Explorer's Alt + drag. Each item gets
/// "<c>名前 - ショートカット.lnk</c>" in the target folder (<see cref="ShortcutNames"/>, " (2)" when
/// taken). Not the shell's <c>SHGetNewLinkInfo</c>: without flags it gave "note.txt.lnk", without
/// the suffix Explorer adds. A dragged .lnk is copied as a link (its arguments and icon kept), as
/// Explorer does. Never overwrites: a name that exists right before the save is taken again. On a thread of its own per call
/// (<see cref="StaThread"/>), not <c>AppServices.Shell</c>, which reading the Windows recent items
/// can keep busy for seconds.
/// </summary>
public static class ShellLinks
{
    public static Task<ShortcutResult> CreateAsync(IReadOnlyList<string> targets, string folder) =>
        StaThread.Run("ShellLinks", () => CreateAll(targets, folder), ex => new ShortcutResult(targets.Count, [], ex.Message));

    private static ShortcutResult CreateAll(IReadOnlyList<string> targets, string folder)
    {
        var made = new List<string>();
        string? error = null;
        foreach (string target in targets)
        {
            try
            {
                made.Add(Create(target, folder));
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Warn($"Shortcut to {target} in {folder} could not be made", ex);
                error ??= ex.Message;
            }
        }

        return new ShortcutResult(targets.Count, made, error);
    }

    private static unsafe string Create(string target, string folder)
    {
        string path = NewLinkPath(target, folder, out bool mustCopy);
        object link = new ShellLink();
        try
        {
            var persist = (IPersistFile)link;
            if (mustCopy)
            {
                fixed (char* source = target)
                {
                    persist.Load(new PCWSTR(source), STGM.STGM_READ);
                }
            }
            else
            {
                var shellLink = (IShellLinkW)link;
                ITEMIDLIST* pidl = PInvoke.ILCreateFromPath(ShellActions.ShellPath(target));
                if (pidl is null)
                {
                    throw new IOException($"項目が見つかりません：{target}");
                }

                try
                {
                    shellLink.SetIDList(pidl);
                }
                finally
                {
                    PInvoke.ILFree(pidl);
                }

                if (File.Exists(target) && PathTools.ParentOf(target) is { Length: > 0 } parent)
                {
                    fixed (char* dir = parent)
                    {
                        shellLink.SetWorkingDirectory(new PCWSTR(dir));
                    }
                }
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                path = Path.Combine(folder, ShortcutNames.FileName(LinkBaseName(target, mustCopy), Taken(folder))); // made meanwhile: never overwrite
            }

            fixed (char* file = path)
            {
                persist.Save(new PCWSTR(file), true);
            }

            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>The first free "名前 - ショートカット.lnk" for <paramref name="target"/> in <paramref name="folder"/>; a .lnk is copied, not linked to.</summary>
    private static string NewLinkPath(string target, string folder, out bool mustCopy)
    {
        // A file only: a folder may be named "x.lnk" too, and gets a shortcut to it like any folder.
        mustCopy = string.Equals(Path.GetExtension(target), ShortcutNames.Extension, StringComparison.OrdinalIgnoreCase) && File.Exists(target);
        return Path.Combine(folder, ShortcutNames.FileName(LinkBaseName(target, mustCopy), Taken(folder)));
    }

    /// <summary>The name the shortcut is called after: a copied .lnk without its extension, anything else as shown.</summary>
    private static string LinkBaseName(string target, bool mustCopy) =>
        mustCopy ? Path.GetFileNameWithoutExtension(target) : PathTools.DisplayName(target);

    private static Func<string, bool> Taken(string folder) =>
        name => File.Exists(Path.Combine(folder, name)) || Directory.Exists(Path.Combine(folder, name));
}
