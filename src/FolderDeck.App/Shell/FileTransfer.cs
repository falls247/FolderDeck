using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

/// <summary>
/// What one transfer did. <c>Requested</c> / <c>Done</c> count the top-level items handed to
/// <see cref="FileTransfer.RunAsync"/> (never the contents of a folder); <c>MovesRequested</c> /
/// <c>MovesDone</c> are the part of them that were moves. <c>NewPaths</c> holds the items created in
/// the destination for the top-level items that were done, in the order of the request;
/// <c>Items</c> says for each of them where it came from and how (the app's undo history).
/// </summary>
public sealed record TransferResult(
    TransferOutcome Outcome,
    int Requested,
    int Done,
    int MovesRequested,
    int MovesDone,
    IReadOnlyList<string> NewPaths,
    string? Error,
    IReadOnlyList<TransferredItem> Items)
{
    /// <summary>
    /// Items that were already in the destination under the name a request went to (a merge or a
    /// replace was offered), whether or not that request ended done: a merge stopped part-way (a
    /// skip, a locked file, a cancel) has still moved part of its content into them. The app's undo
    /// history forgets every step that touches them.
    /// </summary>
    public IReadOnlyList<string> TakenTargets { get; init; } = [];
}

/// <summary>A top-level item a transfer did.</summary>
/// <param name="Source">The requested item.</param>
/// <param name="NewPath">The item it made in the destination.</param>
/// <param name="Kind">Copied or moved.</param>
/// <param name="Fresh">
/// The result is a new item: false when it went into an item that was there before under its own
/// name (a folder merged, a file replaced), which the app's undo never moves or recycles (it would
/// take what was there before with it). A "keep both" copy under a new name is fresh.
/// </param>
public sealed record TransferredItem(string Source, string NewPath, TransferKind Kind, bool Fresh);

/// <summary>What an undo / redo of moves or renames did (<see cref="FileTransfer.MoveBackAsync"/>, <see cref="ShellFileOps.RenameBackAsync"/>).</summary>
/// <param name="Requested">Items asked for.</param>
/// <param name="Done">The items moved or renamed (from, to): reported by the shell at their requested target, and so on disk.</param>
/// <param name="Canceled">The user cancelled one of the shell's dialogs, or the shell stopped.</param>
/// <param name="Error">Text for the message line (without the final 「。」) when items were not done.</param>
public sealed record MoveBackResult(int Requested, IReadOnlyList<PathMove> Done, bool Canceled, string? Error)
{
    public static MoveBackResult None { get; } = new(0, [], false, null);
}

/// <summary>
/// Copies or moves files and folders into a folder with <c>IFileOperation</c>, like Explorer's paste
/// (its callers are the app's paste and drops on a folder). This is the only code in the app that copies or moves the user's files.
/// <para>
/// Nothing is decided silently: the shell's own dialogs (name collision, folder merge, "file in
/// use", elevation, errors) and its progress window all show, owned by <c>owner</c>, and the
/// operation goes on Explorer's undo stack. The flags that would suppress a question
/// (<c>FOF_NOCONFIRMATION</c>, <c>FOF_SILENT</c>, <c>FOF_NOERRORUI</c>, <c>FOF_NOCONFIRMMKDIR</c>,
/// <c>FOFX_SHOWELEVATIONPROMPT</c>) are never passed.
/// </para>
/// </summary>
public static class FileTransfer
{
    /// <summary><c>HRESULT_FROM_WIN32(ERROR_CANCELLED)</c>.</summary>
    private const int ErrorCancelled = unchecked((int)0x800704C7);

    private const int EAbort = unchecked((int)0x80004004);

    /// <summary><c>COPYENGINE_E_USER_CANCELLED</c>: the user cancelled one of the shell's dialogs.</summary>
    private const int CopyEngineUserCancelled = unchecked((int)0x80270000);

    /// <summary><c>COPYENGINE_S_USER_IGNORED</c>: the user skipped the item in one of the shell's dialogs.</summary>
    private const int CopyEngineUserIgnored = 0x00270005;

    /// <summary>Upper bound for the walk from a reported path up to a requested one.</summary>
    private const int MaxAncestorSteps = 512;

    // Undoable from Explorer (Ctrl+Z), and a file is never taken together with its "connected"
    // companion folder (page.htm + page_files). No flag here answers a question for the user.
    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO
        | FILEOPERATION_FLAGS.FOFX_ADDUNDORECORD
        | FILEOPERATION_FLAGS.FOF_NO_CONNECTED_ELEMENTS;

    /// <summary>
    /// Runs the transfer on its own STA thread (IFileOperation is apartment-threaded and shows modal
    /// UI). The task never faults: a failure is a result with <see cref="TransferOutcome.Failed"/> or
    /// <see cref="TransferOutcome.Partial"/> and an error text.
    /// </summary>
    public static Task<TransferResult> RunAsync(IReadOnlyList<TransferItem> items, string destinationFolder, nint owner)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(destinationFolder);

        // A copy: the caller's list must not change under the worker thread.
        TransferItem[] batch = items.ToArray();
        if (batch.Length == 0)
        {
            return Task.FromResult(new TransferResult(TransferOutcome.Completed, 0, 0, 0, 0, [], null, []));
        }

        // Shared with onError, so what was already done is still reported after an unexpected failure.
        var sink = new Sink(batch);
        return StaThread.Run(
            "FolderDeck transfer",
            () => Run(batch, destinationFolder, owner, sink),
            ex =>
            {
                AppLog.Warn($"Transfer failed: -> {destinationFolder}", ex);
                return Finish(sink, destinationFolder, aborted: false, error: ex.Message);
            });
    }

    private static TransferResult Run(TransferItem[] items, string destinationFolder, nint owner, Sink sink)
    {
        object? destinationObject = null;
        bool aborted = false;
        string? error = null;
        try
        {
            // The destination comes first: without it nothing is queued and nothing runs.
            if (!Directory.Exists(destinationFolder))
            {
                return Refuse(sink, destinationFolder, string.Format(PathTools.MsgFolderNotFound, destinationFolder));
            }

            HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(destinationFolder), null, out IShellItem destination);
            if (hr.Failed || destination is null)
            {
                return Refuse(sink, destinationFolder, Marshal.GetExceptionForHR(hr.Value)?.Message ?? "フォルダが見つかりません。");
            }

            destinationObject = destination;

            // Which results will go into an item that is there now (a merge, a replace): the undo
            // history leaves those alone.
            sink.NoteTargets(destinationFolder);

            aborted = RunGroup(items, renameOnCollision: false, destination, owner, sink);
            if (!aborted)
            {
                // "Keep both" copies (a paste into the folder the items came from) need their own
                // operation object: the flag applies to a whole operation, not to one item.
                aborted = RunGroup(items, renameOnCollision: true, destination, owner, sink);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Transfer failed: -> {destinationFolder}", ex);
            error = ex.Message;
        }
        finally
        {
            Release(destinationObject);
        }

        return Finish(sink, destinationFolder, aborted, error);
    }

    /// <summary>
    /// Queues the items of one group (with or without rename-on-collision) in a new
    /// <c>FileOperation</c> and performs it. Returns true when the operation was aborted (cancelled
    /// by the user, or stopped by the shell). No object is created for an empty group.
    /// </summary>
    private static unsafe bool RunGroup(TransferItem[] items, bool renameOnCollision, IShellItem destination, nint owner, Sink sink)
    {
        var sources = new List<object>();
        object? operationObject = null;
        bool stopped = true;
        try
        {
            IFileOperation? operation = null;
            for (int i = 0; i < items.Length; i++)
            {
                TransferItem item = items[i];
                if (item.RenameOnCollision != renameOnCollision)
                {
                    continue;
                }

                try
                {
                    // The form the shell item is created from (8.3 names for a path over MAX_PATH)
                    // is one more spelling the callbacks may report.
                    string shellPath = ShellActions.ShellPath(item.Path);
                    sink.Register(i, shellPath);

                    HRESULT hr = PInvoke.SHCreateItemFromParsingName(shellPath, null, out IShellItem source);
                    if (hr.Failed || source is null)
                    {
                        // This one counts as failed; the others still go.
                        sink.SourceFailed(i, Marshal.GetExceptionForHR(hr.Value)?.Message ?? "項目が見つかりません。");
                        continue;
                    }

                    sources.Add(source);

                    // The name the shell itself uses for the item, next to the requested forms.
                    sink.Register(i, FileSystemPath(source));

                    if (operation is null)
                    {
                        operation = (IFileOperation)new FileOperation();
                        operationObject = operation;
                        operation.SetOperationFlags(renameOnCollision ? Flags | FILEOPERATION_FLAGS.FOF_RENAMEONCOLLISION : Flags);
                        if (owner != 0)
                        {
                            operation.SetOwnerWindow(new HWND(owner));
                        }
                    }

                    // A null name keeps the item's own name.
                    if (item.Kind == TransferKind.Move)
                    {
                        operation.MoveItem(source, destination, default(PCWSTR), sink);
                    }
                    else
                    {
                        operation.CopyItem(source, destination, default(PCWSTR), sink);
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
                {
                    AppLog.Warn($"Transfer: could not queue {item.Path}", ex);
                    sink.SourceFailed(i, ex.Message);
                }
            }

            if (operation is null)
            {
                stopped = false;
                return false;
            }

            bool canceled = false;
            try
            {
                operation.PerformOperations();
            }
            catch (Exception ex) when (IsCancel(ex.HResult))
            {
                // Cancelled by the user in one of the shell's dialogs: reported as aborted.
                canceled = true;
            }

            BOOL aborted = default;
            operation.GetAnyOperationsAborted(&aborted);
            stopped = canceled || aborted;
            return stopped;
        }
        finally
        {
            // The callbacks only noted what the shell reported for the top-level items; what counts
            // as done is settled now, against the file system (still on the STA thread). After an
            // exception the group counts as stopped.
            sink.Confirm(renameOnCollision, stopped);
            Release(operationObject);
            foreach (object source in sources)
            {
                Release(source);
            }
        }
    }

    /// <summary>The destination is unusable: nothing ran.</summary>
    private static TransferResult Refuse(Sink sink, string destinationFolder, string error)
    {
        var result = new TransferResult(TransferOutcome.Failed, sink.Requested, 0, sink.MovesRequested, 0, [], error, []);
        Log(result, destinationFolder);
        return result;
    }

    private static TransferResult Finish(Sink sink, string destinationFolder, bool aborted, string? error)
    {
        error ??= sink.FirstError();
        int done = sink.Done;
        TransferOutcome outcome = TransferOutcomes.Classify(sink.Requested, done, aborted || sink.Canceled, error);
        var result = new TransferResult(outcome, sink.Requested, done, sink.MovesRequested, sink.MovesDone, sink.NewPaths(), error, sink.Items())
        {
            TakenTargets = sink.TakenTargets(),
        };
        Log(result, destinationFolder);
        return result;
    }

    /// <summary>One line per transfer: counts, destination and error text; never a file's contents.</summary>
    private static void Log(TransferResult result, string destinationFolder)
    {
        string error = result.Error is null ? string.Empty : $" ({result.Error})";
        AppLog.Info(
            $"Transfer {result.Outcome}: {result.Done}/{result.Requested} done, moves {result.MovesDone}/{result.MovesRequested} -> {destinationFolder}{error}");
    }

    /// <summary>
    /// The app's undo / redo of moves: each item at <c>Path</c> goes to <c>Target</c> (its folder and
    /// its name there; the folders may differ per item) in one operation with
    /// <see cref="ShellFileOps.UndoFlags"/>, so it is not on Explorer's undo stack. Checked again on
    /// the STA thread: the item is there, the target name is free and its folder exists; an item that
    /// fails a check is left as it is. An item is done only when the shell reported it at its
    /// target and the disk agrees (<see cref="MoveBackSink"/>: never one resolved in the shell's
    /// collision dialog). The task never faults.
    /// </summary>
    public static Task<MoveBackResult> MoveBackAsync(IReadOnlyList<PathMove> moves, nint owner)
    {
        PathMove[] batch = moves.ToArray();
        if (batch.Length == 0)
        {
            return Task.FromResult(MoveBackResult.None);
        }

        return StaThread.Run(
            "FolderDeck move back",
            () => LogBack(MoveBack(batch, owner)),
            ex =>
            {
                AppLog.Warn($"Move back failed: {batch[0].Path} (+{batch.Length - 1})", ex);
                return new MoveBackResult(batch.Length, [], false, ex.Message);
            });
    }

    private static unsafe MoveBackResult MoveBack(PathMove[] moves, nint owner)
    {
        var comObjects = new List<object>();
        var buffers = new List<nint>(); // the names stay allocated until the operation has run
        object? operationObject = null;
        var queued = new List<(PathMove Move, int Index)>();
        var sink = new MoveBackSink();
        try
        {
            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(ShellFileOps.UndoFlags);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            foreach (var m in moves)
            {
                string folder = PathTools.ParentOf(m.Target);
                string name = Path.GetFileName(PathTools.TrimTrailingSeparators(m.Target));
                if (name.Length == 0 || !Exists(m.Path) || Exists(m.Target) || !Directory.Exists(folder))
                {
                    continue;
                }

                HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(m.Path), null, out IShellItem source);
                if (hr.Failed || source is null)
                {
                    continue;
                }

                comObjects.Add(source);
                hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(folder), null, out IShellItem destination);
                if (hr.Failed || destination is null)
                {
                    continue;
                }

                comObjects.Add(destination);
                nint buffer = Marshal.StringToHGlobalUni(name);
                buffers.Add(buffer);
                int index = sink.Add(m.Target, m.Path, ShellActions.ShellPath(m.Path), FileSystemPath(source));
                operation.MoveItem(source, destination, new PCWSTR((char*)buffer), sink);
                queued.Add((m, index));
            }

            bool canceled = queued.Count > 0 && ShellFileOps.Perform(operation);
            return Settled(moves.Length, queued, sink, canceled, error: null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Move back failed: {moves[0].Path} (+{moves.Length - 1})", ex);
            return Settled(moves.Length, queued, sink, canceled: false, error: ex.Message);
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

        // Done: the shell reported the item at its requested target (not "keep both", not into an
        // item that appeared there: MoveBackSink) and the disk agrees (source gone, target there).
        static MoveBackResult Settled(int requested, List<(PathMove Move, int Index)> queued, MoveBackSink sink, bool canceled, string? error)
        {
            var done = queued
                .Where(q => sink.Landed(q.Index) && !Exists(q.Move.Path) && Exists(q.Move.Target))
                .Select(q => q.Move)
                .ToList();
            canceled |= sink.Canceled;
            if (error is null && sink.FirstError is { } hr && !IsCancel(hr.Value))
            {
                error = Marshal.GetExceptionForHR(hr.Value)?.Message?.TrimEnd('。', '.') ?? "移動できなかった項目があります";
            }

            if (error is null && !canceled && done.Count < requested)
            {
                error = queued.Count == 0 ? "項目が見つからないか、移動先に同じ名前の項目があります"
                    : sink.AnyCollided ? "途中で移動先に同じ名前の項目ができた項目は、履歴に残していません"
                    : "元に戻せなかった項目があります";
            }

            return new MoveBackResult(requested, done, canceled, error);
        }
    }

    private static MoveBackResult LogBack(MoveBackResult result)
    {
        string canceled = result.Canceled ? ", canceled" : string.Empty;
        string error = result.Error is null ? string.Empty : $" ({result.Error})";
        string first = result.Done.Count > 0 ? $" {result.Done[0].Path} -> {result.Done[0].Target}" : string.Empty;
        AppLog.Info($"Move back: {result.Done.Count}/{result.Requested} done{first}{canceled}{error}");
        return result;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>File-system path of a shell item, or null when it has none or cannot be read.</summary>
    internal static unsafe string? FileSystemPath(IShellItem? item)
    {
        if (item is null)
        {
            return null;
        }

        PWSTR name = default;
        try
        {
            item.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &name);
            return name.Value is null ? null : name.ToString();
        }
        catch (Exception)
        {
            // Only a lookup for the accounting (called from the shell's callbacks too): an item
            // without a file-system path is simply not one of the requested items.
            return null;
        }
        finally
        {
            if (name.Value is not null)
            {
                Marshal.FreeCoTaskMem((nint)name.Value);
            }
        }
    }

    /// <summary>A cancel by the user or the shell (also for <see cref="ShellFileOps"/>).</summary>
    internal static bool IsCancel(int hr) => hr is ErrorCancelled or EAbort or CopyEngineUserCancelled;

    internal static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.ReleaseComObject(comObject);
        }
    }

    /// <summary>
    /// The reports of an undo / redo's moves (<see cref="MoveBack"/>, <see cref="RecycleBin.RestoreAsync"/>),
    /// per queued item: the path the shell says it made (<c>PostMoveItem</c> with a success code other
    /// than <c>COPYENGINE_S_USER_IGNORED</c>), and whether its target was taken when the move began
    /// (<c>PreMoveItem</c>). These operations run without <c>FOF_NOCONFIRMATION</c>, so an item that
    /// appeared at the target meanwhile gets the shell's collision dialog: "keep both" lands under
    /// another name, a merge or a replace lands in or over that other item. Such an item never counts
    /// as done (<see cref="Landed"/>): recorded at the requested target, a later redo would move or
    /// recycle the other item. All calls arrive on the operation's STA thread.
    /// </summary>
    internal sealed class MoveBackSink : IFileOperationProgressSink
    {
        private readonly List<string> _targets = [];
        private readonly Dictionary<string, int> _indexByPath = new(PathTools.Comparer);
        private readonly Dictionary<int, string?> _reported = [];
        private readonly HashSet<int> _collided = [];

        /// <summary>The first failure reported for an item.</summary>
        public HRESULT? FirstError { get; private set; }

        /// <summary>An item was cancelled (by the user in a shell dialog, or the operation stopped).</summary>
        public bool Canceled { get; private set; }

        /// <summary>An item whose target was taken when its move began.</summary>
        public bool AnyCollided => _collided.Count > 0;

        /// <summary>A queued item going to <paramref name="target"/>; <paramref name="spellings"/> are the ways the shell may report its source.</summary>
        public int Add(string target, params string?[] spellings)
        {
            int index = _targets.Count;
            _targets.Add(target);
            foreach (string? s in spellings)
            {
                if (!string.IsNullOrEmpty(s))
                {
                    _indexByPath.TryAdd(s, index);
                    _indexByPath.TryAdd(PathTools.TrimTrailingSeparators(s), index);
                }
            }

            return index;
        }

        /// <summary>
        /// The item reached its requested target: the shell reported it there (as given or in the
        /// form handed to the shell), its target was free when the move began, and no failure came
        /// after. The caller checks the disk as well.
        /// </summary>
        public bool Landed(int index)
        {
            if (_collided.Contains(index) || !_reported.TryGetValue(index, out var made) || made is null)
            {
                return false;
            }

            string target = _targets[index];
            string trimmed = PathTools.TrimTrailingSeparators(made);
            return PathTools.PathEquals(trimmed, PathTools.TrimTrailingSeparators(target))
                || PathTools.PathEquals(trimmed, PathTools.TrimTrailingSeparators(ShellActions.ShellPath(target)));
        }

        private bool TryIndex(IShellItem? item, out int index)
        {
            index = -1;
            return FileSystemPath(item) is { } path
                && (_indexByPath.TryGetValue(path, out index) || _indexByPath.TryGetValue(PathTools.TrimTrailingSeparators(path), out index));
        }

        public void PreMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
        {
            // An exception here would go back to the copy engine as a failure of its own.
            try
            {
                if (TryIndex(psiItem, out int index) && Exists(_targets[index]))
                {
                    _collided.Add(index);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Move back: the progress callback failed.", ex);
            }
        }

        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
        {
            try
            {
                if (!TryIndex(psiItem, out int index))
                {
                    return; // something inside a moved folder: the disk check covers it
                }

                if (hrMove.Failed)
                {
                    FirstError ??= hrMove;
                    Canceled |= IsCancel(hrMove.Value);
                    _reported.Remove(index);
                }
                else if (hrMove.Value == CopyEngineUserIgnored || psiNewlyCreated is null)
                {
                    _reported.Remove(index); // skipped in a dialog, or no resulting item
                }
                else
                {
                    _reported[index] = FileSystemPath(psiNewlyCreated);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn("Move back: the progress callback failed.", ex);
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

    /// <summary>
    /// Counts what happened to the requested top-level items, in two steps.
    /// <para>
    /// While the operation runs, the callbacks only note what the shell reported. A top-level item is
    /// "reported" when its own callback came with a success code other than
    /// <c>COPYENGINE_S_USER_IGNORED</c> (skipped in a dialog) together with the item it created. The
    /// code is not required to be <c>S_OK</c>: a successful move reports
    /// <c>COPYENGINE_S_DONT_PROCESS_CHILDREN</c> (0x00270008) or, for a folder that goes to another
    /// volume, <c>COPYENGINE_S_NOT_HANDLED</c> (0x00270003); only copies report <c>S_OK</c> (seen on
    /// Windows 11 26200). The callback of a folder arrives before its contents are processed, so it
    /// proves nothing about them; a failed or skipped item inside a requested folder is noted against
    /// that folder.
    /// </para>
    /// <para>
    /// <see cref="Confirm"/> then settles each reported item of a group once its operation has ended.
    /// A move is done only when its source is gone and its result exists (the copy engine removes a
    /// source folder only after everything in it went over), so <c>MovesDone</c> never counts a
    /// source that is still there. A copy is done when its result exists, nothing inside it failed
    /// or was skipped, and, for a folder, the operation was not stopped midway.
    /// </para>
    /// One instance serves both operation objects of a transfer; all calls arrive on its STA thread.
    /// </summary>
    private sealed class Sink : IFileOperationProgressSink
    {
        private readonly TransferItem[] _items;

        /// <summary>
        /// Every known spelling of a requested path to its index: as given (the constructor), the
        /// form handed to the shell and the one the shell reports for it (added while queueing).
        /// </summary>
        private readonly Dictionary<string, int> _indexByPath = new(PathTools.Comparer);

        private readonly bool[] _done;

        /// <summary>The shell reported success for the top-level item itself (not yet <see cref="_done"/>).</summary>
        private readonly bool[] _reported;

        /// <summary>Per requested folder: the paths inside it that failed or were skipped (and did not succeed later).</summary>
        private readonly HashSet<string>?[] _failedInside;

        private readonly string?[] _newPaths;
        private readonly string?[] _errors;

        /// <summary>The path under the item's own name in the destination (<see cref="NoteTargets"/>).</summary>
        private readonly string?[] _expected;

        /// <summary>An item was at <see cref="_expected"/> before the transfer (never set for "keep both" copies).</summary>
        private readonly bool[] _takenBefore;

        public Sink(TransferItem[] items)
        {
            _items = items;
            _expected = new string?[items.Length];
            _takenBefore = new bool[items.Length];
            _done = new bool[items.Length];
            _reported = new bool[items.Length];
            _failedInside = new HashSet<string>?[items.Length];
            _newPaths = new string?[items.Length];
            _errors = new string?[items.Length];
            for (int i = 0; i < items.Length; i++)
            {
                Register(i, items[i].Path);
                if (items[i].Kind == TransferKind.Move)
                {
                    MovesRequested++;
                }
            }
        }

        public int Requested => _items.Length;

        public int MovesRequested { get; }

        public int Done => _done.Count(done => done);

        public int MovesDone
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _items.Length; i++)
                {
                    if (_done[i] && _items[i].Kind == TransferKind.Move)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>The shell reported a cancel for one of the requested items or for something inside one.</summary>
        public bool Canceled { get; private set; }

        /// <summary>Adds one spelling of the path of item <paramref name="index"/>. The first owner of a spelling keeps it.</summary>
        public void Register(int index, string? path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            _indexByPath.TryAdd(path, index);
            _indexByPath.TryAdd(PathTools.TrimTrailingSeparators(path), index);
        }

        /// <summary>The item never reached the shell (its shell item could not be created or queued).</summary>
        public void SourceFailed(int index, string? error)
        {
            _errors[index] ??= error ?? "項目を処理できませんでした。";
        }

        /// <summary>Error of the first requested item that was not done, or null.</summary>
        public string? FirstError()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                if (!_done[i] && _errors[i] is { } error)
                {
                    return error;
                }
            }

            return null;
        }

        /// <summary>Results of the items that were done, in the order of the request.</summary>
        public IReadOnlyList<string> NewPaths()
        {
            var paths = new List<string>();
            for (int i = 0; i < _items.Length; i++)
            {
                if (_done[i] && _newPaths[i] is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }

            return paths;
        }

        /// <summary>
        /// Before anything runs: the path each item would get under its own name in
        /// <paramref name="destinationFolder"/>, and whether an item is there already (a merge or a
        /// replace if the result lands there). Touches the file system; never throws.
        /// </summary>
        public void NoteTargets(string destinationFolder)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                try
                {
                    string name = Path.GetFileName(PathTools.TrimTrailingSeparators(_items[i].Path));
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    _expected[i] = Path.Join(destinationFolder, name);
                    _takenBefore[i] = !_items[i].RenameOnCollision && Exists(_expected[i]!);
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Transfer: could not check the destination of {_items[i].Path}", ex);
                }
            }
        }

        /// <summary>The items that were done, in the order of the request: where each came from, how, and whether its result is new (see <see cref="TransferredItem.Fresh"/>).</summary>
        /// <summary>The destinations that were taken before the transfer (see <see cref="TransferResult.TakenTargets"/>).</summary>
        public IReadOnlyList<string> TakenTargets()
        {
            var taken = new List<string>();
            for (int i = 0; i < _items.Length; i++)
            {
                if (_takenBefore[i] && _expected[i] is { Length: > 0 } path)
                {
                    taken.Add(path);
                }
            }

            return taken;
        }

        public IReadOnlyList<TransferredItem> Items()
        {
            // Two sources of one name (search results) can end in one path: the second merged into or
            // replaced the first. Neither result is new then (undoing one would take the other along).
            var arrivals = new Dictionary<string, int>(PathTools.Comparer);
            for (int i = 0; i < _items.Length; i++)
            {
                if (_done[i] && _newPaths[i] is { Length: > 0 } p)
                {
                    arrivals[p] = arrivals.GetValueOrDefault(p) + 1;
                }
            }

            var items = new List<TransferredItem>();
            for (int i = 0; i < _items.Length; i++)
            {
                if (_done[i] && _newPaths[i] is { Length: > 0 } path)
                {
                    bool fresh = arrivals[path] == 1 && !(_takenBefore[i] && PathTools.PathEquals(path, _expected[i]));
                    items.Add(new TransferredItem(_items[i].Path, path, _items[i].Kind, fresh));
                }
            }

            return items;
        }

        /// <summary>
        /// Settles the reported items of one group after its operation has ended (see the class
        /// remarks). <paramref name="stopped"/>: the operation was cancelled, aborted or failed.
        /// Touches the file system; never throws.
        /// </summary>
        public void Confirm(bool renameOnCollision, bool stopped)
        {
            for (int i = 0; i < _items.Length; i++)
            {
                TransferItem item = _items[i];
                if (item.RenameOnCollision != renameOnCollision || !_reported[i] || _done[i])
                {
                    continue;
                }

                try
                {
                    // An unreadable result name cannot be checked; the source still is.
                    bool resultExists = _newPaths[i] is not { Length: > 0 } result || Exists(result);
                    bool done;
                    if (item.Kind == TransferKind.Move)
                    {
                        // Whatever the callbacks said: a source that is still there was not moved.
                        done = resultExists && !Exists(item.Path);
                    }
                    else
                    {
                        // A folder's callback came before its contents: after a stop its copy
                        // cannot be vouched for.
                        done = resultExists
                            && _failedInside[i] is not { Count: > 0 }
                            && !(stopped && Directory.Exists(item.Path));
                    }

                    if (done)
                    {
                        _done[i] = true;
                        _errors[i] = null;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"Transfer: could not confirm {item.Path}", ex);
                }
            }
        }

        private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

        /// <summary>The requested item that <paramref name="path"/> lies inside, if any.</summary>
        private bool TryFindAncestor(string path, out int index)
        {
            string current = path;
            for (int step = 0; step < MaxAncestorSteps; step++)
            {
                string parent = PathTools.ParentOf(current);
                if (parent.Length == 0 || parent.Length >= current.Length)
                {
                    break;
                }

                if (_indexByPath.TryGetValue(parent, out index))
                {
                    return true;
                }

                current = parent;
            }

            index = -1;
            return false;
        }

        /// <summary>Notes a cancel, or the error text of a failure, against a requested item.</summary>
        private void NoteFailure(int index, HRESULT hr)
        {
            if (!hr.Failed)
            {
                return;
            }

            if (IsCancel(hr.Value))
            {
                Canceled = true;
            }
            else
            {
                _errors[index] ??= Marshal.GetExceptionForHR(hr.Value)?.Message ?? "項目を処理できませんでした。";
            }
        }

        private void Record(TransferKind kind, IShellItem? source, HRESULT hr, IShellItem? created)
        {
            // An exception here would go back to the copy engine as a failure of its own.
            try
            {
                string? path = FileSystemPath(source);
                if (path is null)
                {
                    return;
                }

                bool notDone = hr.Failed || hr.Value == CopyEngineUserIgnored;
                if (!_indexByPath.TryGetValue(path, out int index))
                {
                    // Something inside a requested folder: never counted itself, but a failure or a
                    // skip there leaves that folder incomplete (a later success, after "retry",
                    // takes the mark back).
                    if (!TryFindAncestor(path, out index))
                    {
                        return;
                    }

                    if (notDone)
                    {
                        (_failedInside[index] ??= new HashSet<string>(PathTools.Comparer)).Add(path);
                        NoteFailure(index, hr);
                    }
                    else if (_items[index].Kind == kind)
                    {
                        _failedInside[index]?.Remove(path);
                    }

                    return;
                }

                if (_done[index])
                {
                    return;
                }

                if (hr.Failed)
                {
                    // A failure of any step of the item (a move across volumes copies and deletes).
                    NoteFailure(index, hr);
                    if (_items[index].Kind == kind)
                    {
                        _reported[index] = false;
                    }

                    return;
                }

                // Only the callback of the operation that was asked for counts: a move is not
                // reported because a copy step of it succeeded.
                if (_items[index].Kind != kind)
                {
                    return;
                }

                if (notDone || created is null)
                {
                    // Skipped by the user, or no resulting item.
                    _reported[index] = false;
                    return;
                }

                // Not done yet: Confirm decides once the whole operation has ended.
                _reported[index] = true;
                _newPaths[index] = FileSystemPath(created);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Transfer: the progress callback failed.", ex);
            }
        }

        public void PostMoveItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrMove, IShellItem psiNewlyCreated)
        {
            Record(TransferKind.Move, psiItem, hrMove, psiNewlyCreated);
        }

        public void PostCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName, HRESULT hrCopy, IShellItem psiNewlyCreated)
        {
            Record(TransferKind.Copy, psiItem, hrCopy, psiNewlyCreated);
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

        public void PreCopyItem(uint dwFlags, IShellItem psiItem, IShellItem psiDestinationFolder, PCWSTR pszNewName)
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
