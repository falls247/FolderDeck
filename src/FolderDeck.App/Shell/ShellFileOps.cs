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
}
