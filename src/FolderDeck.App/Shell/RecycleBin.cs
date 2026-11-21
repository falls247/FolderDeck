using System.Runtime.InteropServices;
using FolderDeck.Core.Infrastructure;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace FolderDeck.Shell;

public enum RecycleOutcome
{
    /// <summary>The item is in the Recycle Bin.</summary>
    Recycled,

    /// <summary>The user cancelled (a shell dialog) or the shell skipped the item: nothing was deleted.</summary>
    Canceled,

    /// <summary>The item cannot go to the Recycle Bin (network folder, too large…): it was left as it is.</summary>
    NotRecyclable,

    /// <summary>The shell reported an error (in use, access denied, already gone…).</summary>
    Failed,
}

public sealed record RecycleResult(RecycleOutcome Outcome, string? Error = null);

/// <summary>
/// Moves one file or folder to the Recycle Bin with <c>IFileOperation</c>, like Explorer's Delete key.
/// It never deletes permanently: when the shell would destroy the item instead of recycling it
/// (no Recycle Bin on that drive, item too large…), the operation is cancelled before anything is
/// removed. The app's own confirmation comes first, so the shell's "move to Recycle Bin?" prompt
/// is suppressed; its progress, "file in use" and similar dialogs still show, owned by
/// <c>owner</c>.
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
    public static Task<RecycleResult> MoveAsync(string path, nint owner)
    {
        var completion = new TaskCompletionSource<RecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Move(path, owner));
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Recycle failed: {path}", ex);
                completion.SetResult(new RecycleResult(RecycleOutcome.Failed, ex.Message));
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

    private static unsafe RecycleResult Move(string path, nint owner)
    {
        object? itemObject = null;
        object? operationObject = null;
        var sink = new Sink();
        try
        {
            HRESULT hr = PInvoke.SHCreateItemFromParsingName(ShellActions.ShellPath(path), null, out IShellItem item);
            if (hr.Failed || item is null)
            {
                return new RecycleResult(RecycleOutcome.Failed, Marshal.GetExceptionForHR(hr.Value)?.Message ?? "項目が見つかりません。");
            }

            itemObject = item;
            var operation = (IFileOperation)new FileOperation();
            operationObject = operation;
            operation.SetOperationFlags(Flags);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            operation.DeleteItem(item, sink);
            try
            {
                operation.PerformOperations();
            }
            catch (COMException ex) when (IsCancel(ex.HResult))
            {
                // Cancelled by the user or by the sink (permanent delete refused): reported below.
            }

            BOOL aborted = default;
            operation.GetAnyOperationsAborted(&aborted);
            if (sink.Refused)
            {
                return new RecycleResult(RecycleOutcome.NotRecyclable);
            }

            if (sink.DeleteError is { } error && error.Failed)
            {
                return IsCancel(error.Value)
                    ? new RecycleResult(RecycleOutcome.Canceled)
                    : new RecycleResult(RecycleOutcome.Failed, Marshal.GetExceptionForHR(error.Value)?.Message);
            }

            if (aborted || !sink.Deleted)
            {
                return new RecycleResult(RecycleOutcome.Canceled);
            }

            if (!sink.InRecycleBin)
            {
                // Not expected (the sink refuses permanent deletes); say so rather than claim it is recoverable.
                AppLog.Warn($"Recycle: the shell reported no Recycle Bin item for {path}");
                return new RecycleResult(RecycleOutcome.Failed, "ごみ箱に移動されたことを確認できませんでした。");
            }

            return new RecycleResult(RecycleOutcome.Recycled);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Recycle failed: {path}", ex);
            return new RecycleResult(RecycleOutcome.Failed, ex.Message);
        }
        finally
        {
            Release(operationObject);
            Release(itemObject);
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

    /// <summary>Watches the delete: refuses it when the shell would not recycle, and records the result.</summary>
    private sealed class Sink : IFileOperationProgressSink
    {
        public bool Refused { get; private set; }

        public bool Deleted { get; private set; }

        public bool InRecycleBin { get; private set; }

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
                return;
            }

            Deleted = true;

            // The item now in the Recycle Bin; null when it was deleted for good.
            InRecycleBin = psiNewlyCreated is not null;
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
