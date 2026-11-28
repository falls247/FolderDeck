using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

/// <summary>
/// The app's own undo / redo (Ctrl+Z, Ctrl+Y / Ctrl+Shift+Z, the blank area's menu) of the file
/// operations it ran itself this session: renames (a batch rename is one step), new folders, copies
/// and moves (paste, drop), deletes to the Recycle Bin. Never Explorer's session-wide undo
/// (ShellContextMenu's RemovedVerbs), and a reversal is never put on Explorer's undo stack.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly FileOpHistory _fileHistory = new();

    /// <summary>The step Ctrl+Z would undo, or null (nothing to undo). A menu keeps it to pass to <see cref="UndoAsync"/>.</summary>
    public FileOpStep? NextUndoStep => _fileHistory.NextUndo;

    /// <summary>The step Ctrl+Y would redo, or null (nothing to redo). A menu keeps it to pass to <see cref="RedoAsync"/>.</summary>
    public FileOpStep? NextRedoStep => _fileHistory.NextRedo;

    /// <summary>
    /// A forward operation of the app ended: what it did goes on the undo history (empty: nothing to
    /// undo) and, when it <paramref name="changed"/> anything, the redo history is cleared. Never
    /// called for an undo / redo.
    /// </summary>
    private void RecordFileOp(FileOpStep step, bool changed = true) => _fileHistory.Record(step, changed);

    /// <summary>
    /// Something the history did not record changed at <paramref name="paths"/> (a merge or a
    /// replace landed there, a step was refused): every step that names them, a path inside them or
    /// a folder holding them leaves both stacks (<see cref="FileOpHistory.Invalidate"/>).
    /// </summary>
    private void InvalidateFileOps(IEnumerable<string> paths)
    {
        int removed = _fileHistory.Invalidate(paths);
        if (removed > 0)
        {
            AppLog.Info($"File history: {removed} step(s) taken out (their items changed outside the history)");
        }
    }

    /// <summary>Undoes the last step. <paramref name="expected"/> (a menu entry): the step its label named; refused when the history changed since.</summary>
    public Task UndoAsync(FileOpStep? expected = null) => ReverseAsync(undo: true, expected);

    /// <summary>Redoes the last undone step; <paramref name="expected"/> as in <see cref="UndoAsync"/>.</summary>
    public Task RedoAsync(FileOpStep? expected = null) => ReverseAsync(undo: false, expected);

    /// <summary>
    /// Undoes (<paramref name="undo"/>) or redoes the last step. The items are checked first (off
    /// the UI thread): a step whose items are no longer where it says is refused and dropped. Then,
    /// one shell operation each: back from the Recycle Bin, the moves / renames back, to the Recycle
    /// Bin. What was done goes on the other stack and what was not stays where the step was (the next
    /// try acts on it, never on an older step that may hold its items); the lists are read again,
    /// renamed / moved folders followed and what came back into the shown folder selected.
    /// </summary>
    private async Task ReverseAsync(bool undo, FileOpStep? expected)
    {
        var step = undo ? _fileHistory.NextUndo : _fileHistory.NextRedo;
        string cannot = undo ? "元に戻せ" : "やり直せ";
        if (expected is not null && !ReferenceEquals(step, expected))
        {
            // A menu entry picked after the history changed (its label names another step).
            Message.Info("履歴が変わりました。もう一度選んでください。");
            return;
        }

        if (step is null)
        {
            Message.Info(undo ? "元に戻す操作はありません。" : "やり直す操作はありません。");
            return;
        }

        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return;
        }

        _fileOpBusy = true;
        nint owner = View?.WindowHandle ?? 0;
        RestoreResult restored = RestoreResult.None;
        MoveBackResult moved = MoveBackResult.None;
        RecycleResult? recycled = null;
        try
        {
            View?.CloseCascade();
            Message.ClearPersistent();
            string? problem = await OffThreadAsync(
                () => FileOpChecks.ProblemReversing(step, _services.FileSystem) ?? string.Empty,
                SourceProbeTimeout); // null: timed out or failed
            if (problem is null)
            {
                Message.Error($"{step.Label}を{cannot}ません：項目を確認できませんでした。");
                return;
            }

            if (problem.Length > 0)
            {
                // Its items are not where it says: the older steps naming the same places could act
                // on what is there now, so they go too.
                _fileHistory.Drop(step);
                InvalidateFileOps(step.Paths);
                Message.Warning($"{step.Label}を{cannot}ません：{problem}。この操作は履歴から外しました。");
                return;
            }

            // Back from the bin first (it frees nothing the moves need), then the moves, the recycle last.
            if (step.Binned.Count > 0)
            {
                restored = await RecycleBin.RestoreAsync(step.Binned, owner);
            }

            // A phase that was cancelled or failed stops the rest: the user asked to stop, and the
            // later phases (the recycle above all) would act on a half-reversed step.
            bool stop = restored.Canceled || restored.Error is not null;
            if (!stop && step.Moved.Count > 0)
            {
                moved = step.Kind == FileOpKind.Rename
                    ? await ShellFileOps.RenameBackAsync(step.MovesBack, owner)
                    : await FileTransfer.MoveBackAsync(step.MovesBack, owner);
                stop = moved.Canceled || moved.Error is not null;
            }

            if (!stop && step.Made.Count > 0)
            {
                recycled = await RecycleBin.MoveAsync(step.Made, owner, shellUndo: false);
            }
        }
        finally
        {
            _fileOpBusy = false;
        }

        var after = step.After(moved.Done, recycled?.Items ?? [], restored.Restored);
        var remainder = step.Remainder(moved.Done, recycled?.Items ?? [], restored.Restored);
        bool canceled = restored.Canceled || moved.Canceled || recycled?.Outcome == RecycleOutcome.Canceled;
        string? error = restored.Error ?? moved.Error ?? RecycleError(recycled);

        if (after.IsEmpty && canceled && error is null)
        {
            Message.Info($"{step.Label}の{(undo ? "取り消し" : "やり直し")}を中止しました。"); // the step stays for another try
            return;
        }

        // What was not done stays where the step was (nothing done after a failure: the step itself).
        _fileHistory.Reversed(step, after, remainder, undo);
        foreach (var m in moved.Done)
        {
            ApplyRename(m.Path, m.Target); // folders: the 登録フォルダ and tabs at or below it follow (a file: nothing)
        }

        await ReloadAfterFileChangeAsync();
        SelectArrived([.. moved.Done.Select(m => m.Target), .. restored.Restored.Select(e => e.Original)]);
        ShowReverseResult(step, after, undo, canceled, error);
    }

    private static string? RecycleError(RecycleResult? result) => result?.Outcome switch
    {
        RecycleOutcome.NotRecyclable => "ごみ箱に移動できない場所にあります",
        RecycleOutcome.Failed => (result.Error ?? "ごみ箱に移動できませんでした").TrimEnd('。'),
        _ => null,
    };

    /// <summary>Selects what came back into the folder shown (as ReloadAndSelectArrivedAsync; only what the reloaded list shows).</summary>
    private void SelectArrived(IReadOnlyList<string> paths)
    {
        var tab = ActivePane;
        if (tab.TransferTargetPath is not { } shown)
        {
            return;
        }

        var here = paths.Where(p => PathTools.PathEquals(PathTools.ParentOf(p), shown)).ToList();
        if (here.Count > 0)
        {
            tab.Selection.Replace(here);
            tab.Selection.Prune(tab.SelectableKeys());
        }
    }

    /// <summary>The message line after an undo / redo: all of it, part of it, or nothing (the step stays in the history).</summary>
    private void ShowReverseResult(FileOpStep step, FileOpStep after, bool undo, bool canceled, string? error)
    {
        string done = undo ? "元に戻し" : "やり直し";
        string cannot = undo ? "元に戻せ" : "やり直せ";
        if (after.IsEmpty)
        {
            Message.Error($"{step.Label}を{cannot}ませんでした：{error ?? "不明なエラー"}。");
            return;
        }

        if (after.Count < step.Count || error is not null || canceled)
        {
            string why = error is null ? (canceled ? "：残りは中止しました" : string.Empty) : $"：{error}";
            string rest = after.Count < step.Count ? "残りは履歴に残しています。" : string.Empty;
            Message.Warning($"{step.Label}の一部だけ{done}ました（{after.Count:N0} / {step.Count:N0} 個）{why}。{rest}");
            return;
        }

        Message.Success($"{step.Label}を{done}ました（{Detail(after)}）。");
    }

    /// <summary>What a reversal did, for the message line: 「b.txt」→「a.txt」, 「x」をごみ箱に移動…</summary>
    private static string Detail(FileOpStep after)
    {
        static string Name(string path) => $"「{PathTools.DisplayName(path)}」";
        static string What(IReadOnlyList<string> paths) => paths.Count == 1 ? Name(paths[0]) : $"{paths.Count:N0} 個の項目";

        var parts = new List<string>();
        if (after.Moved.Count > 0)
        {
            if (after.Kind == FileOpKind.Rename)
            {
                parts.Add(after.Moved.Count == 1
                    ? $"{Name(after.Moved[0].Path)}→{Name(after.Moved[0].Target)}"
                    : $"{after.Moved.Count:N0} 個の項目の名前");
            }
            else
            {
                var folders = after.Moved.Select(m => PathTools.ParentOf(m.Target)).Distinct(PathTools.Comparer).ToList();
                string what = What(after.Moved.Select(m => m.Target).ToList());
                parts.Add(folders.Count == 1 ? $"{what}を{Name(folders[0])}へ" : $"{what}を移動");
            }
        }

        if (after.Binned.Count > 0)
        {
            parts.Add($"{What(after.Binned.Select(b => b.Original).ToList())}をごみ箱に移動");
        }

        if (after.Made.Count > 0)
        {
            parts.Add($"{What(after.Made)}をごみ箱から元の場所へ");
        }

        return string.Join("、", parts);
    }
}
