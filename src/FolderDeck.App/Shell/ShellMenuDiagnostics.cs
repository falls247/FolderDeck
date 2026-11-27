using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Shell;

/// <summary>
/// Extra lines for app.log when Explorer's menu is slow to build or times out
/// (<see cref="ShellContextMenu"/>): the time goes into <c>QueryContextMenu</c>, that is into the
/// context-menu extensions registered for the item, which the app cannot see from outside. So
/// that a log from another PC tells which extension it was, this writes:
/// <list type="bullet">
/// <item>the handlers registered for the item's kind (Directory\Background, Directory, Folder,
/// AllFilesystemObjects, *, the extension and its ProgID), each with its DLL;</item>
/// <item>the item's file attributes (a cloud file: offline, recall on open / on data access, pinned);</item>
/// <item>the DLLs loaded in the process from outside Windows, the app and the .NET runtime (extensions, security software), only the ones not named before.</item>
/// </list>
/// Once per kind (background / folder / extension) for the life of the process: the registry does
/// not change between two menus. Runs on a pool thread, never on the menu thread or the UI thread.
/// Never throws.
/// </summary>
internal static class ShellMenuDiagnostics
{
    private const int FileAttributeRecallOnOpen = 0x40000;
    private const int FileAttributePinned = 0x80000;
    private const int FileAttributeUnpinned = 0x100000;
    private const int FileAttributeRecallOnDataAccess = 0x400000;

    /// <summary>Kinds already described (background, folder, the extension): any thread.</summary>
    private static readonly HashSet<string> Described = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The modules listed last time: a later description only names the new ones.</summary>
    private static HashSet<string>? _modulesListed;

    private static readonly object Gate = new();

    /// <summary>Describes the menu of <paramref name="path"/> (or its background) in app.log, once per kind.</summary>
    public static void Describe(string path, bool background, string reason)
    {
        _ = Task.Run(() =>
        {
            try
            {
                DescribeNow(path, background, reason);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Shell menu diagnostics failed", ex);
            }
        });
    }

    private static void DescribeNow(string path, bool background, string reason)
    {
        bool folder = background || Directory.Exists(path);
        string extension = folder ? "" : PathTools.ExtensionOf(path);
        string kind = background ? "background" : folder ? "folder" : extension.Length > 0 ? extension : "file";
        string[] modules;
        lock (Gate)
        {
            if (!Described.Add(kind))
            {
                return;
            }

            modules = NewModules();
        }

        var keys = new List<string>();
        if (background)
        {
            keys.Add(@"Directory\Background");
        }
        else
        {
            keys.Add("AllFilesystemObjects");
            if (folder)
            {
                keys.Add("Directory");
                keys.Add("Folder");
            }
            else
            {
                keys.Add("*");
                if (extension.Length > 0)
                {
                    keys.Add(extension);
                    keys.Add($@"SystemFileAssociations\{extension}");
                    if (ProgIdOf(extension) is { } progId)
                    {
                        keys.Add(progId);
                    }
                }
            }
        }

        var handlers = new List<string>();
        foreach (string key in keys)
        {
            foreach (string handler in HandlersOf(key))
            {
                handlers.Add($"{key}: {handler}");
            }
        }

        AppLog.Info(
            $"Shell menu diagnostics ({reason}, {kind}): attributes {AttributesOf(path)}; "
            + $"handlers ({handlers.Count}): {(handlers.Count == 0 ? "none" : string.Join("; ", handlers))}; "
            + $"modules from outside Windows and the app ({modules.Length}): {(modules.Length == 0 ? "none new" : string.Join("; ", modules))}");
    }

    /// <summary>The handlers under <c>HKCR\{key}\shellex\ContextMenuHandlers</c> as "name = DLL".</summary>
    private static IEnumerable<string> HandlersOf(string key)
    {
        using var handlers = Registry.ClassesRoot.OpenSubKey($@"{key}\shellex\ContextMenuHandlers");
        if (handlers is null)
        {
            yield break;
        }

        foreach (string name in handlers.GetSubKeyNames())
        {
            string? clsid = null;
            using (var handler = handlers.OpenSubKey(name))
            {
                clsid = handler?.GetValue(null) as string;
            }

            // The name itself is the CLSID when the default value is empty.
            if (string.IsNullOrWhiteSpace(clsid))
            {
                clsid = name;
            }

            yield return $"{name} = {DllOf(clsid)}";
        }
    }

    private static string DllOf(string clsid)
    {
        if (!clsid.StartsWith('{'))
        {
            return clsid;
        }

        using var server = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
        return server?.GetValue(null) is string dll && dll.Length > 0
            ? Path.GetFileName(Environment.ExpandEnvironmentVariables(dll.Trim('"')))
            : $"{clsid} (no InprocServer32)";
    }

    private static string? ProgIdOf(string extension)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(extension);
        return key?.GetValue(null) as string is { Length: > 0 } progId ? progId : null;
    }

    private static string AttributesOf(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            int bits = (int)attributes;
            var cloud = new List<string>();
            if (attributes.HasFlag(FileAttributes.Offline))
            {
                cloud.Add("offline");
            }

            if ((bits & FileAttributeRecallOnOpen) != 0)
            {
                cloud.Add("recall on open");
            }

            if ((bits & FileAttributeRecallOnDataAccess) != 0)
            {
                cloud.Add("recall on data access");
            }

            if ((bits & FileAttributePinned) != 0)
            {
                cloud.Add("pinned");
            }

            if ((bits & FileAttributeUnpinned) != 0)
            {
                cloud.Add("unpinned");
            }

            return cloud.Count == 0 ? $"0x{bits:x}" : $"0x{bits:x} ({string.Join(", ", cloud)})";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"unknown ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// The DLLs in the process from outside the Windows folder and the app's folder, not named by an
    /// earlier description (under <see cref="Gate"/>).
    /// </summary>
    private static string[] NewModules()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string app = AppContext.BaseDirectory;
        string runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var listed = _modulesListed ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        try
        {
            using var self = Process.GetCurrentProcess();
            foreach (ProcessModule module in self.Modules)
            {
                string file = module.FileName;
                if (string.IsNullOrEmpty(file)
                    || PathTools.IsSameOrUnder(file, windows)
                    || PathTools.IsSameOrUnder(file, app)
                    || PathTools.IsSameOrUnder(file, runtime)
                    || file.Contains(@"\.net\", StringComparison.OrdinalIgnoreCase) // a single-file exe's native libraries, extracted
                    || !listed.Add(file))
                {
                    continue;
                }

                found.Add(file);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            found.Add($"cannot list ({ex.GetType().Name})");
        }

        return found.ToArray();
    }
}
