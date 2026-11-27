using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

/// <summary>
/// How a delete of several items ended. <see cref="Recycled"/>: every item is in the Recycle Bin;
/// the others say why the rest (<see cref="RecycleResult.Requested"/> minus
/// <see cref="RecycleResult.Recycled"/>) was not moved.
/// </summary>
public enum RecycleOutcome
{
    /// <summary>Every item is in the Recycle Bin.</summary>
    Recycled,

    /// <summary>The user cancelled (a shell dialog) or the shell skipped items: those were not deleted.</summary>
    Canceled,

    /// <summary>An item cannot go to the Recycle Bin (network folder, too large…): it and the rest were left as they are.</summary>
    NotRecyclable,

    /// <summary>The shell reported an error (in use, access denied, already gone…).</summary>
    Failed,
}

/// <param name="Requested">How many items were asked for.</param>
/// <param name="Recycled">How many of them reached the Recycle Bin.</param>
public sealed record RecycleResult(RecycleOutcome Outcome, int Requested, int Recycled, string? Error = null)
{
    /// <summary>The requested items now in the Recycle Bin, with their file there (the app's undo history).</summary>
    public IReadOnlyList<BinEntry> Items { get; init; } = [];
}

/// <summary>What <see cref="RecycleBin.RestoreAsync"/> did.</summary>
/// <param name="Requested">Items asked for.</param>
/// <param name="Restored">The items back where they were: reported by the shell at their original path, and so on disk.</param>
/// <param name="Canceled">The user cancelled one of the shell's dialogs, or the shell stopped.</param>
/// <param name="Error">Text for the message line (without the final 「。」) when items were not restored.</param>
public sealed record RestoreResult(int Requested, IReadOnlyList<BinEntry> Restored, bool Canceled, string? Error)
{
    public static RestoreResult None { get; } = new(0, [], false, null);
}

/// <summary>
/// Moves several files and folders together to the Recycle Bin with one <c>IFileOperation</c>, like
/// Explorer's Delete key (one progress, one undo). It never deletes permanently: when the shell
/// would destroy an item instead of recycling it (no Recycle Bin on that drive, item too large…),
/// that item and every one after it are not deleted (items handled before it are in the Recycle
/// Bin). This holds only because all the items go through one <c>PerformOperations</c>: never split
/// them into separate calls. The app's own confirmation comes first, so the shell's "move to
/// Recycle Bin?" prompt is suppressed; its progress, "file in use" and similar dialogs still show,
/// owned by <c>owner</c>.
/// </summary>
public static class RecycleBin
{
    /// <summary><c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c>: what the sink returns to stop a permanent delete.</summary>
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private const int EAbort = unchecked((int)0x80004004);

    /// <summary><c>COPYENGINE_E_USER_CANCELLED</c>: the user cancelled one of the shell's dialogs ("file in use"…).</summary>
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    /// <summary><c>TSF_DELETE_RECYCLE_IF_POSSIBLE</c>: set on <c>PreDeleteItem</c> only when the item will be recycled.</summary>
    private const uint TsfDeleteRecycleIfPossible = 0x0080;

    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE
        | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION

        // Second guard: should the shell ever go for a permanent delete without telling the sink,
        // it asks first (this partly overrides FOF_NOCONFIRMATION).
        | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING;

    /// <summary>
    /// Runs on its own STA thread (IFileOperation is apartment-threaded and may show modal UI).
    /// <paramref name="shellUndo"/> false (the app's own undo / redo): not on Explorer's undo stack
    /// (no <c>FOF_ALLOWUNDO</c>); the items still go to the Recycle Bin (<c>FOFX_RECYCLEONDELETE</c>)
    /// and a permanent delete is still refused.
    /// </summary>
    public static Task<RecycleResult> MoveAsync(IReadOnlyList<string> paths, nint owner, bool shellUndo = true)
    {
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (paths.Count == 0)
        {
            completion.SetResult(new RecycleResult(RecycleOutcome.Recycled, 0, 0));
            return completion.Task;
        }

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Move(paths, owner, shellUndo));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Recycle failed: {paths[0]} (+{paths.Count - 1})", ex);
                completion.SetResult(new RecycleResult(RecycleOutcome.Failed, paths.Count, 0, ex.Message));
            }
        })
        {
            IsBackground = true,
            Name = "FolderDeck recycle",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    /// <summary>
    /// The app's undo of a delete (and redo of a copy / new folder): moves each item back from the
    /// Recycle Bin to where it was, under its old name, in one operation with
    /// <see cref="ShellFileOps.UndoFlags"/> (not on Explorer's undo stack). The item moved is the
    /// Recycle Bin's own item (<c>shell:RecycleBinFolder</c> + the $R path), else the $R file
    /// itself; both bring the contents back and remove the $R file, and leave its $I file as
    /// Explorer's 「元に戻す」 (undelete) does. Checked again on the STA thread: the $R file is
    /// there, the old place is free and its folder exists; an item that fails is left in the bin.
    /// An item is restored only when the shell reported it at its original path and the disk agrees
    /// (<see cref="FileTransfer.MoveBackSink"/>). The task never faults.
    /// </summary>
    public static Task<RestoreResult> RestoreAsync(IReadOnlyList<BinEntry> items, nint owner)
    {
        BinEntry[] batch = items.ToArray();
        if (batch.Length == 0)
        {
            return Task.FromResult(RestoreResult.None);
        }

        return StaThread.Run(
            "FolderDeck restore",
            () => LogRestore(Restore(batch, owner)),
            ex =>
            {
                AppLog.Warn($"Restore failed: {batch[0].Original} (+{batch.Length - 1})", ex);
                return new RestoreResult(batch.Length, [], false, ex.Message);
            });
    }

    private static unsafe RestoreResult Restore(BinEntry[] items, nint owner)
    {
        var comObjects = new List<object>();
        var buffers = new List<nint>(); // the names stay allocated until the operation has run
        object? operationObject = null;
        var queued = new List<(BinEntry Entry, int Index)>();
        var sink = new FileTransfer.MoveBackSink();
        try
        {
            // "::{645FF040-…}" is refused (E_INVALIDARG); the shell: name works. Without it the $R
            // files themselves are moved.
            IShellItem? bin = null;
            if (PInvoke.SHCreateItemFromParsingName("shell:RecycleBinFolder", null, out IShellItem binFolder).Succeeded && binFolder is not null)
            {
                bin = binFolder;
                comObjects.Add(binFolder);
            }

            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(ShellFileOps.UndoFlags);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            foreach (var e in items)
            {
                string folder = PathTools.ParentOf(e.Original);
                string name = Path.GetFileName(PathTools.TrimTrailingSeparators(e.Original));
                if (name.Length == 0 || Exists(e.Original) || !Directory.Exists(folder) || !Exists(e.BinPath))
                {
                    continue;
                }

                if (BinItem(bin, e.BinPath) is not { } item)
                {
                    continue;
                }

                comObjects.Add(item);
                HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(folder), null, out IShellItem destination);
                if (hr.Failed || destination is null)
                {
                    continue;
                }

                comObjects.Add(destination);
                nint buffer = Marshal.StringToHGlobalUni(name);
                buffers.Add(buffer);

                // The Recycle Bin's item reports its $R path as its file-system path.
                int index = sink.Add(e.Original, e.BinPath, FileTransfer.FileSystemPath(item));
                operation.MoveItem(item, destination, new PCWSTR((char*)buffer), sink);
                queued.Add((e, index));
            }

            bool canceled = queued.Count > 0 && ShellFileOps.Perform(operation);
            return Restored(items.Length, queued, sink, canceled, error: null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Restore failed: {items[0].Original} (+{items.Length - 1})", ex);
            return Restored(items.Length, queued, sink, canceled: false, error: ex.Message);
        }
        finally
        {
            Release(operationObject);
            foreach (object o in comObjects)
            {
                Release(o);
            }

            foreach (nint b in buffers)
            {
                Marshal.FreeHGlobal(b);
            }
        }

        // Done: the shell reported the item at its original path (not "keep both", not into an
        // item that appeared there: MoveBackSink) and the disk agrees (there, $R gone).
        static RestoreResult Restored(int requested, List<(BinEntry Entry, int Index)> queued, FileTransfer.MoveBackSink sink, bool canceled, string? error)
        {
            var done = queued
                .Where(q => sink.Landed(q.Index) && Exists(q.Entry.Original) && !Exists(q.Entry.BinPath))
                .Select(q => q.Entry)
                .ToList();
            canceled |= sink.Canceled;
            if (error is null && sink.FirstError is { } hr && !FileTransfer.IsCancel(hr.Value))
            {
                error = Marshal.GetExceptionForHR(hr.Value)?.Message?.TrimEnd('。', '.') ?? "ごみ箱から戻せなかった項目があります";
            }

            if (error is null && !canceled && done.Count < requested)
            {
                error = queued.Count == 0 ? "ごみ箱に項目が見つからないか、元の場所に同じ名前の項目があります"
                    : sink.AnyCollided ? "途中で元の場所に同じ名前の項目ができた項目は、履歴に残していません"
                    : "元に戻せなかった項目があります";
            }

            return new RestoreResult(requested, done, canceled, error);
        }
    }

    /// <summary>The Recycle Bin's item for a $R path (its display name is the original path), else the $R file's item; null when neither can be made.</summary>
    private static unsafe IShellItem? BinItem(IShellItem? bin, string binPath)
    {
        if (bin is not null)
        {
            Guid iid = typeof(IShellItem).GUID;
            HRESULT hr;
            object found;
            fixed (char* p = binPath)
            {
                hr = PInvoke.SHCreateItemFromRelativeName(bin, new PCWSTR(p), null, &iid, out found);
            }

            if (hr.Succeeded && found is IShellItem item)
            {
                return item;
            }
        }

        return PInvoke.SHCreateItemFromParsingName(binPath, null, out IShellItem file).Succeeded ? file : null;
    }

    private static RestoreResult LogRestore(RestoreResult result)
    {
        string canceled = result.Canceled ? ", canceled" : string.Empty;
        string error = result.Error is null ? string.Empty : $" ({result.Error})";
        AppLog.Info($"Restore from the Recycle Bin: {result.Restored.Count}/{result.Requested} restored{canceled}{error}");
        return result;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static unsafe RecycleResult Move(IReadOnlyList<string> paths, nint owner, bool shellUndo)
    {
        int requested = paths.Count;
        var itemObjects = new List<object>(requested);
        object? operationObject = null;
        var sink = new Sink();
        foreach (string path in paths)
        {
            // The spellings the callbacks may report for a requested item (8.3 names over MAX_PATH).
            sink.Register(path, path);
            sink.Register(ShellActions.ShellPath(path), path);
        }

        try
        {
            // Every item first: when one cannot be found, nothing is deleted.
            var items = new List<IShellItem>(requested);
            foreach (string path in paths)
            {
                HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(path), null, out IShellItem item);
                if (hr.Failed || item is null)
                {
                    string message = Marshal.GetExceptionForHR(hr.Value)?.Message
                        ?? $"「{PathTools.DisplayName(path)}」が見つかりません。";
                    return new RecycleResult(RecycleOutcome.Failed, requested, 0, message);
                }

                itemObjects.Add(item);
                items.Add(item);
                sink.Register(FileTransfer.FileSystemPath(item), paths[items.Count - 1]);
            }

            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(shellUndo ? Flags : Flags & ~FILEOPERATION_FLAGS.FOF_ALLOWUNDO);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            foreach (var item in items)
            {
                operation.DeleteItem(item, sink);
            }

            try
            {
                // One call for all the items: a refusal in the sink cancels that item and every later one.
                operation.PerformOperations();
            }
            catch (COMException ex) when (IsCancel(ex.HResult))
            {
                // Cancelled by the user or by the sink (permanent delete refused): reported below.
            }

            BOOL aborted = default;
            operation.GetAnyOperationsAborted(&aborted);
            return Outcome(sink, requested, aborted, paths) with { Items = sink.Binned };
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Recycle failed: {paths[0]} (+{requested - 1})", ex);
            return new RecycleResult(RecycleOutcome.Failed, requested, Math.Min(sink.RecycledCount, requested), ex.Message) { Items = sink.Binned };
        }
        finally
        {
            Release(operationObject);
            foreach (var itemObject in itemObjects)
            {
                Release(itemObject);
            }
        }
    }

    /// <summary>How the delete ended, from the sink's counts.</summary>
    private static RecycleResult Outcome(Sink sink, int requested, bool aborted, IReadOnlyList<string> paths)
    {
        // Capped: should the sink ever hear about items inside a folder, the count stays per item asked for.
        int recycled = Math.Min(sink.RecycledCount, requested);
        if (sink.Refused)
        {
            return new RecycleResult(RecycleOutcome.NotRecyclable, requested, recycled);
        }

        if (sink.Nuked > 0)
        {
            // Not expected (the sink refuses permanent deletes); say so rather than claim it is recoverable.
            AppLog.Warn($"Recycle: the shell reported no Recycle Bin item for {sink.Nuked} item(s) of {paths[0]} (+{requested - 1})");
            return new RecycleResult(RecycleOutcome.Failed, requested, recycled, "ごみ箱に移動されたことを確認できなかった項目があります。");
        }

        if (sink.DeleteError is { } error && error.Failed)
        {
            return IsCancel(error.Value)
                ? new RecycleResult(RecycleOutcome.Canceled, requested, recycled)
                : new RecycleResult(RecycleOutcome.Failed, requested, recycled, Marshal.GetExceptionForHR(error.Value)?.Message);
        }

        if (aborted || recycled < requested)
        {
            return new RecycleResult(RecycleOutcome.Canceled, requested, recycled);
        }

        return new RecycleResult(RecycleOutcome.Recycled, requested, recycled);
    }

    private static bool IsCancel(int hr) => hr is ErrorCancelled or EAbort or CopyEngineUserCancelled;

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    /// <summary>Watches the deletes: refuses one when the shell would not recycle it, and counts how each ended.</summary>
    private sealed class Sink : IFileOperationProgressSink
    {
        /// <summary>Every known spelling of a requested path to that path.</summary>
        private readonly Dictionary<string, string> _requested = new(PathTools.Comparer);

        private readonly List<BinEntry> _binned = [];

        public bool Refused { get; private set; }

        /// <summary>The requested items now in the Recycle Bin and their file there.</summary>
        public IReadOnlyList<BinEntry> Binned => _binned.ToList();

        /// <summary>
        /// The requested path of a deleted item: by any of its spellings; when the shell gives no
        /// path that is known, the one item asked for (a single delete), else null (not recorded).
        /// </summary>
        private string? OriginalOf(IShellItem? item)
        {
            if (FileTransfer.FileSystemPath(item) is { } from && _requested.TryGetValue(from, out var original))
            {
                return original;
            }

            var distinct = _requested.Values.Distinct(PathTools.Comparer).Take(2).ToList();
            return distinct.Count == 1 && _binned.Count == 0 ? distinct[0] : null;
        }

        /// <summary>One spelling of a requested path. The first owner of a spelling keeps it.</summary>
        public void Register(string? spelling, string requested)
        {
            if (!string.IsNullOrEmpty(spelling))
            {
                _requested.TryAdd(spelling, requested);
                _requested.TryAdd(PathTools.TrimTrailingSeparators(spelling), requested);
            }
        }

        /// <summary>Items now in the Recycle Bin.</summary>
        public int RecycledCount { get; private set; }

        /// <summary>Items reported deleted with no Recycle Bin item (deleted for good: not expected).</summary>
        public int Nuked { get; private set; }

        /// <summary>Items the shell skipped with a success code (the user chose スキップ…): not deleted.</summary>
        public int Skipped { get; private set; }

        public int Failures { get; private set; }

        /// <summary>The first error reported for an item.</summary>
        public HRESULT? DeleteError { get; private set; }

        public void PreDeleteItem(uint dwFlags, IShellItem psiItem)
        {
            if ((dwFlags & TsfDeleteRecycleIfPossible) == 0)
            {
                // The item would be destroyed, not recycled: the error HRESULT cancels this and every
                // later operation of the call.
                Refused = true;
                throw new COMException("The item cannot be moved to the Recycle Bin.", ErrorCancelled);
            }
        }

        public void PostDeleteItem(uint dwFlags, IShellItem psiItem, HRESULT hrDelete, IShellItem psiNewlyCreated)
        {
            if (hrDelete.Failed)
            {
                DeleteError ??= hrDelete;
                Failures++;
                return;
            }

            // psiNewlyCreated is the item now in the Recycle Bin. It wins over the success code:
            // an item that landed in the bin is recycled whatever S_* code came with it.
            if (psiNewlyCreated is not null)
            {
                RecycledCount++;

                // Its file in the bin ($R…) for the app's undo; only for a requested item. Never
                // throws (FileSystemPath catches): an exception would go back to the copy engine.
                if (FileTransfer.FileSystemPath(psiNewlyCreated) is { } bin && OriginalOf(psiItem) is { } original)
                {
                    _binned.Add(new BinEntry(original, bin));
                }
            }
            else if (hrDelete.Value != 0)
            {
                // A success code other than S_OK (COPYENGINE_S_USER_IGNORED…) with nothing in the bin:
                // the item was skipped, not deleted; never counted as a permanent delete.
                Skipped++;
            }
            else
            {
                // S_OK with no bin item: deleted for good.
                Nuked++;
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

        public void PostRenameItem(uint dwFlags, IShellItem psiItem, PCWSTR pszNewName, HRESULT hrRename, IShellItem psiNewlyCreated)
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
