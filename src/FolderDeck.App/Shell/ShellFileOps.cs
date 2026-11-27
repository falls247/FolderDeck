using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

/// <summary>What <see cref="ShellFileOps.RenameAsync"/> / <see cref="ShellFileOps.NewFolderAsync"/> did.</summary>
public enum ShellOpOutcome
{
    /// <summary>Done: <see cref="ShellOpResult.NewPath"/> is the item as it is now.</summary>
    Done,

    /// <summary>Another item already has the name (checked before anything ran; rename only).</summary>
    NameTaken,

    /// <summary>The user cancelled one of the shell's dialogs, or the shell stopped the operation.</summary>
    Canceled,

    /// <summary>The item or the folder was not there, or the shell reported an error.</summary>
    Failed,
}

/// <param name="NewPath">The path of the renamed or created item (as the shell reports it); null unless done.</param>
/// <param name="Error">Text for the message line when it failed.</param>
public sealed record ShellOpResult(ShellOpOutcome Outcome, string? NewPath, string? Error = null);

/// <summary>One item of <see cref="ShellFileOps.RenameManyAsync"/>.</summary>
/// <param name="Path">The item.</param>
/// <param name="Name">Its name as listed.</param>
/// <param name="IsFolder">A folder: its new name gets no extension.</param>
public sealed record BatchRenameItem(string Path, string Name, bool IsFolder);

/// <summary>An item <see cref="ShellFileOps.RenameManyAsync"/> renamed.</summary>
public sealed record RenamedItem(string OldPath, string NewPath);

/// <summary>What <see cref="ShellFileOps.RenameManyAsync"/> did.</summary>
/// <param name="Requested">Items asked for.</param>
/// <param name="Renamed">Items renamed (old and new path).</param>
/// <param name="Skipped">Items left as they were on purpose (the name already, a name too long, not found).</param>
/// <param name="Canceled">The user cancelled one of the shell's dialogs, or the shell stopped (later items keep their names).</param>
/// <param name="Error">The first failure's text, when an item failed.</param>
public sealed record BatchRenameResult(int Requested, IReadOnlyList<RenamedItem> Renamed, int Skipped, bool Canceled, string? Error);

/// <summary>
/// Renames one item and creates new folders with <c>IFileOperation</c>, like Explorer's edit box and
/// Ctrl+Shift+N. Each call runs on its own STA thread (<see cref="StaThread.Run{T}"/>): the shell may
/// show its own dialogs (access, elevation, "in use"), owned by <c>owner</c>, and the operation goes
/// on Explorer's undo stack. As in <see cref="FileTransfer"/>, no flag answers a question for the user.
/// <para>
/// The name is used as it is given (the caller checked it with <see cref="ItemNames"/>): the file's
/// full name with its extension. The new path is the one the shell reports, so a name the shell
/// changed (a collision renamed by <c>FOF_RENAMEONCOLLISION</c>) is still found.
/// </para>
/// </summary>
public static class ShellFileOps
{
    // Undoable from Explorer (Ctrl+Z); a file is renamed without its "connected" companion folder
    // (page.htm + page_files), so the result is exactly the one item that was asked for.
    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOFX_ADDUNDORECORD
        | FILEOPERATION_FLAGS.FOF_NO_CONNECTED_ELEMENTS;

    /// <summary>
    /// Renames <paramref name="path"/> to <paramref name="newName"/> (a name, not a path) in the same
    /// folder. A name that only changes the case of letters is a rename too. Another item with the
    /// name (any case) gives <see cref="ShellOpOutcome.NameTaken"/> without asking the shell: for a
    /// collision the copy engine shows "unexpected error 0x80070057" instead of its question, and
    /// for two folders it offers to merge them. The task never faults.
    /// </summary>
    public static Task<ShellOpResult> RenameAsync(string path, string newName, nint owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(newName);
        return StaThread.Run(
            "FolderDeck rename",
            () => Log($"Rename {path} -> {newName}", Rename(path, newName, owner)),
            ex => Log($"Rename {path} -> {newName}", new ShellOpResult(ShellOpOutcome.Failed, null, ex.Message), ex));
    }

    /// <summary>
    /// Explorer's batch rename of <paramref name="items"/> (all in <paramref name="folder"/>, display
    /// order) to "<paramref name="baseName"/> (1)", " (2)"…: the names come from
    /// <see cref="ItemNames.BatchNames"/> against the folder's names on disk (hidden items too) and
    /// <paramref name="shownNames"/>, so no new name is taken and no NameTaken check is needed. One
    /// operation for all of them, as in <see cref="RecycleBin"/>: one progress, one undo record. The
    /// shell asks about each failure itself (try again / skip / cancel). The task never faults.
    /// </summary>
    public static Task<BatchRenameResult> RenameManyAsync(string folder, IReadOnlyList<BatchRenameItem> items, string baseName, IReadOnlyCollection<string> shownNames, nint owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(baseName);
        return StaThread.Run(
            "FolderDeck batch rename",
            () => LogMany(folder, baseName, RenameMany(folder, items, baseName, shownNames, owner)),
            ex => LogMany(folder, baseName, new BatchRenameResult(items.Count, [], 0, false, ex.Message), ex));
    }

    /// <summary>
    /// Creates the folder <paramref name="name"/> in <paramref name="folder"/>. When the name is taken
    /// (by a hidden item, or by one made meanwhile) the shell picks the next free one; the result
    /// says which. The task never faults.
    /// </summary>
    public static Task<ShellOpResult> NewFolderAsync(string folder, string name, nint owner)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return StaThread.Run(
            "FolderDeck new folder",
            () => Log($"New folder {name} in {folder}", NewFolder(folder, name, owner)),
            ex => Log($"New folder {name} in {folder}", new ShellOpResult(ShellOpOutcome.Failed, null, ex.Message), ex));
    }

    private static unsafe ShellOpResult Rename(string path, string newName, nint owner)
    {
        if (!Exists(path))
        {
            return new ShellOpResult(ShellOpOutcome.Failed, null, $"「{PathTools.DisplayName(path)}」が見つかりません。");
        }

        string target = Path.Join(PathTools.ParentOf(path), newName);
        bool sameItem = string.Equals(PathTools.DisplayName(path), newName, StringComparison.OrdinalIgnoreCase);
        if (!sameItem && Exists(target))
        {
            return new ShellOpResult(ShellOpOutcome.NameTaken, null);
        }

        object? itemObject = null;
        object? operationObject = null;
        var sink = new Sink();
        try
        {
            HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(path), null, out IShellItem item);
            if (hr.Failed || item is null)
            {
                return new ShellOpResult(ShellOpOutcome.Failed, null, Marshal.GetExceptionForHR(hr.Value)?.Message ?? "項目が見つかりません。");
            }

            itemObject = item;
            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(Flags);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            bool canceled;
            fixed (char* name = newName)
            {
                // The name stays pinned until the operation has run.
                operation.RenameItem(item, new PCWSTR(name), sink);
                canceled = Perform(operation);
            }

            // Without a report the new name counts when the item is found under it (and, for a
            // change of letters other than their case, no longer under the old one).
            bool renamed = Exists(target) && (sameItem || !Exists(path));
            return Settle(sink, canceled, fallback: renamed ? target : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Rename failed: {path}", ex);
            return new ShellOpResult(ShellOpOutcome.Failed, null, ex.Message);
        }
        finally
        {
            FileTransfer.Release(operationObject);
            FileTransfer.Release(itemObject);
        }
    }

    private static unsafe BatchRenameResult RenameMany(string folder, IReadOnlyList<BatchRenameItem> items, string baseName, IReadOnlyCollection<string> shownNames, nint owner)
    {
        // The numbers avoid every name in the folder, also hidden ones the list does not show: the
        // copy engine answers a collision with "unexpected error 0x80070057", not with its question.
        var existing = new List<string>(shownNames);
        try
        {
            existing.AddRange(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            AppLog.Warn($"Batch rename: could not list {folder}; only the shown names are avoided", ex);
        }

        var names = ItemNames.BatchNames(baseName, items.Select(i => (i.Name, i.IsFolder)).ToList(), existing);
        var queued = new List<(BatchRenameItem Item, string Name)>();
        int skipped = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (names[i] is not { } name || string.Equals(name, items[i].Name, StringComparison.Ordinal) || !Exists(items[i].Path))
            {
                skipped++;
                continue;
            }

            queued.Add((items[i], name));
        }

        if (queued.Count == 0)
        {
            return new BatchRenameResult(items.Count, [], skipped, false, null);
        }

        var itemObjects = new List<object>();
        var buffers = new List<nint>(); // the names stay allocated until the operation has run
        object? operationObject = null;
        var sink = new ManySink();
        try
        {
            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(Flags);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            var running = new List<(BatchRenameItem Item, string Name)>();
            foreach (var (item, name) in queued)
            {
                HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(item.Path), null, out IShellItem shellItem);
                if (hr.Failed || shellItem is null)
                {
                    skipped++;
                    continue;
                }

                itemObjects.Add(shellItem);
                nint buffer = Marshal.StringToHGlobalUni(name);
                buffers.Add(buffer);
                operation.RenameItem(shellItem, new PCWSTR((char*)buffer), sink);
                running.Add((item, name));
            }

            bool canceled = false;
            string? performError = null;
            if (running.Count > 0)
            {
                try
                {
                    canceled = Perform(operation);
                }
                catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException or IOException)
                {
                    // Some items may be renamed already (and on the undo record): they are settled below,
                    // so the list is read again and renamed folders followed.
                    AppLog.Warn($"Batch rename: PerformOperations failed in {folder}", ex);
                    performError = ex.Message;
                }
            }

            var renamed = new List<RenamedItem>();
            foreach (var (item, name) in running)
            {
                // The sink's report first (by the item's path), the disk second: the new name was free
                // before. A change of case only has no proof on disk (both names are found): counted
                // as done unless the sink reported a failure.
                string target = Path.Join(folder, name);
                bool caseOnly = string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase);
                string? reported = sink.NewPathOf(item.Path);
                bool done = reported is not null
                    || (!sink.FailedFor(item.Path) && Exists(target) && (caseOnly || !Exists(item.Path)));
                if (done)
                {
                    renamed.Add(new RenamedItem(item.Path, reported ?? target));
                }
            }

            string? error = sink.FirstError is { } e && !FileTransfer.IsCancel(e.Value)
                ? Marshal.GetExceptionForHR(e.Value)?.Message ?? "操作できませんでした。"
                : performError;
            return new BatchRenameResult(items.Count, renamed, skipped, canceled || sink.Canceled, error);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Batch rename failed: {folder}", ex);
            return new BatchRenameResult(items.Count, [], skipped, false, ex.Message);
        }
        finally
        {
            FileTransfer.Release(operationObject);
            foreach (object o in itemObjects)
            {
                FileTransfer.Release(o);
            }

            foreach (nint b in buffers)
            {
                Marshal.FreeHGlobal(b);
            }
        }
    }

    private static unsafe ShellOpResult NewFolder(string folder, string name, nint owner)
    {
        if (!Directory.Exists(folder))
        {
            return new ShellOpResult(ShellOpOutcome.Failed, null, string.Format(PathTools.MsgFolderNotFound, folder));
        }

        string expected = Path.Join(folder, name);
        bool takenBefore = Exists(expected);
        object? folderObject = null;
        object? operationObject = null;
        var sink = new Sink();
        try
        {
            HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(folder), null, out IShellItem destination);
            if (hr.Failed || destination is null)
            {
                return new ShellOpResult(ShellOpOutcome.Failed, null, Marshal.GetExceptionForHR(hr.Value)?.Message ?? "フォルダが見つかりません。");
            }

            folderObject = destination;
            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(Flags | FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            bool canceled;
            fixed (char* p = name)
            {
                operation.NewItem(destination, (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY, new PCWSTR(p), default, sink);
                canceled = Perform(operation);
            }

            // Without a report the name asked for is only trusted when nothing had it before.
            return Settle(sink, canceled, fallback: !takenBefore && Directory.Exists(expected) ? expected : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"New folder failed: {folder}", ex);
            return new ShellOpResult(ShellOpOutcome.Failed, null, ex.Message);
        }
        finally
        {
            FileTransfer.Release(operationObject);
            FileTransfer.Release(folderObject);
        }
    }

    /// <summary>Runs the queued operation. True when it was cancelled or aborted.</summary>
    private static unsafe bool Perform(IFileOperation operation)
    {
        bool canceled = false;
        try
        {
            operation.PerformOperations();
        }
        catch (Exception ex) when (FileTransfer.IsCancel(ex.HResult))
        {
            canceled = true;
        }

        BOOL aborted = default;
        operation.GetAnyOperationsAborted(&aborted);
        return canceled || aborted;
    }

    /// <summary>
    /// The result from what the shell reported: its error, the item it made, or (no report) the
    /// <paramref name="fallback"/> path the file system shows.
    /// </summary>
    private static ShellOpResult Settle(Sink sink, bool canceled, string? fallback)
    {
        if (sink.Result is { } hr && hr.Failed)
        {
            return FileTransfer.IsCancel(hr.Value)
                ? new ShellOpResult(ShellOpOutcome.Canceled, null)
                : new ShellOpResult(ShellOpOutcome.Failed, null, Marshal.GetExceptionForHR(hr.Value)?.Message ?? "操作できませんでした。");
        }

        string? created = sink.NewPath ?? (sink.Result is null ? fallback : null);
        if (created is not null && Exists(created))
        {
            return new ShellOpResult(ShellOpOutcome.Done, created);
        }

        return canceled
            ? new ShellOpResult(ShellOpOutcome.Canceled, null)
            : new ShellOpResult(ShellOpOutcome.Failed, null, "操作できませんでした。");
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>One line per call: what was asked, the outcome and the new path; never a file's contents.</summary>
    private static ShellOpResult Log(string what, ShellOpResult result, Exception? ex = null)
    {
        string detail = result.Error is null ? string.Empty : $" ({result.Error})";
        string created = result.NewPath is null ? string.Empty : $" => {result.NewPath}";
        if (ex is null)
        {
            AppLog.Info($"{what}: {result.Outcome}{created}{detail}");
        }
        else
        {
            AppLog.Warn($"{what}: {result.Outcome}{detail}", ex);
        }

        return result;
    }

    /// <summary>One line per batch: the folder, the base name and the counts; never a file's contents.</summary>
    private static BatchRenameResult LogMany(string folder, string baseName, BatchRenameResult result, Exception? ex = null)
    {
        string canceled = result.Canceled ? ", canceled" : string.Empty;
        string detail = result.Error is null ? string.Empty : $" ({result.Error})";
        string text = $"Batch rename in {folder} to '{baseName} (n)': {result.Renamed.Count} of {result.Requested} renamed, {result.Skipped} skipped{canceled}{detail}";
        if (ex is null)
        {
            AppLog.Info(text);
        }
        else
        {
            AppLog.Warn(text, ex);
        }

        return result;
    }

    /// <summary>
    /// Keeps what the shell reported for the one item of the operation (all calls on its STA thread).
    /// The last report counts: a rename first reports <c>COPYENGINE_S_PENDING</c> without an item
    /// (ignored), then the outcome; a successful rename reports <c>COPYENGINE_S_DONT_PROCESS_CHILDREN</c>,
    /// not <c>S_OK</c>.
    /// </summary>
    private sealed class Sink : IFileOperationProgressSink
    {
        /// <summary><c>COPYENGINE_S_PENDING</c>: the item is queued for later in the operation, not done.</summary>
        private const int CopyEnginePending = 0x0027000B;

        /// <summary>The code of the item's own callback; null when it never came.</summary>
        public HRESULT? Result { get; private set; }

        public string? NewPath { get; private set; }

        private void Record(HRESULT hr, IShellItem? created)
        {
            if (hr.Value == CopyEnginePending)
            {
                return;
            }

            // An exception here would go back to the copy engine as a failure of its own.
            try
            {
                Result = hr;
                NewPath = hr.Failed ? null : FileTransfer.FileSystemPath(created);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Rename / new folder: the progress callback failed.", ex);
            }
        }

        public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated)
        {
            Record(hrRename, psiNewlyCreated);
        }

        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName, PCWSTR pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem)
        {
            Record(hrNew, psiNewItem);
        }

        public void StartOperations()
        {
        }

        public void FinishOperations(HRESULT hrResult)
        {
        }

        public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
        {
        }

        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
        {
        }

        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated)
        {
        }

        public void PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
        }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated)
        {
        }

        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
        }

        public void ResetTimer()
        {
        }

        public void PauseTimer()
        {
        }

        public void ResumeTimer()
        {
        }
    }

    /// <summary>
    /// Keeps what the shell reported for each item of a batch rename (all calls on its STA thread),
    /// by the item's path. As in <see cref="Sink"/>: <c>COPYENGINE_S_PENDING</c> is ignored, and a
    /// rename that worked is any code that is not a failure (<c>COPYENGINE_S_DONT_PROCESS_CHILDREN</c>).
    /// </summary>
    private sealed class ManySink : IFileOperationProgressSink
    {
        private const int CopyEnginePending = 0x0027000B;

        private readonly Dictionary<string, string?> _done = new(PathTools.Comparer);
        private readonly HashSet<string> _failed = new(PathTools.Comparer);

        /// <summary>The first failure reported for an item.</summary>
        public HRESULT? FirstError { get; private set; }

        /// <summary>An item was cancelled (by the user in a shell dialog, or the operation stopped).</summary>
        public bool Canceled { get; private set; }

        /// <summary>The new path the shell reported for the item at <paramref name="path"/>, or null.</summary>
        public string? NewPathOf(string path) => _done.TryGetValue(path, out var p) ? p : null;

        /// <summary>True when the shell reported a failure for the item at <paramref name="path"/>.</summary>
        public bool FailedFor(string path) => _failed.Contains(path);

        public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated)
        {
            if (hrRename.Value == CopyEnginePending)
            {
                return;
            }

            // An exception here would go back to the copy engine as a failure of its own.
            try
            {
                string? path = FileTransfer.FileSystemPath(psiItem);
                if (hrRename.Failed)
                {
                    FirstError ??= hrRename;
                    Canceled |= FileTransfer.IsCancel(hrRename.Value);
                    if (path is not null)
                    {
                        _failed.Add(path);
                        _done.Remove(path);
                    }
                }
                else if (path is not null)
                {
                    _done[path] = FileTransfer.FileSystemPath(psiNewlyCreated);
                    _failed.Remove(path);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Batch rename: the progress callback failed.", ex);
            }
        }

        public void StartOperations()
        {
        }

        public void FinishOperations(HRESULT hrResult)
        {
        }

        public void PreRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName)
        {
        }

        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
        {
        }

        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated)
        {
        }

        public void PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
        }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated)
        {
        }

        public void PreNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
        }

        public void PostNewItem(uint dwFlags, IShellItem psiDestinationFolder, PCWSTR pszNewName, PCWSTR pszTemplateName, uint dwFileAttributes, HRESULT hrNew, IShellItem psiNewItem)
        {
        }

        public void UpdateProgress(uint iWorkTotal, uint iWorkSoFar)
        {
        }

        public void ResetTimer()
        {
        }

        public void PauseTimer()
        {
        }

        public void ResumeTimer()
        {
        }
    }
}
