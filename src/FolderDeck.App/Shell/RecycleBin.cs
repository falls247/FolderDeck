using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
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
public sealed record RecycleResult(RecycleOutcome Outcome, int Requested, int Recycled, string? Error = null);

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

    /// <summary>Runs on its own STA thread (IFileOperation is apartment-threaded and may show modal UI).</summary>
    public static Task<RecycleResult> MoveAsync(IReadOnlyList<string> paths, nint owner)
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
                completion.SetResult(Move(paths, owner));
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

    private static unsafe RecycleResult Move(IReadOnlyList<string> paths, nint owner)
    {
        int requested = paths.Count;
        var itemObjects = new List<object>(requested);
        object? operationObject = null;
        var sink = new Sink();
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
            }

            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(Flags);
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
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Recycle failed: {paths[0]} (+{requested - 1})", ex);
            return new RecycleResult(RecycleOutcome.Failed, requested, Math.Min(sink.RecycledCount, requested), ex.Message);
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
        public bool Refused { get; private set; }

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
