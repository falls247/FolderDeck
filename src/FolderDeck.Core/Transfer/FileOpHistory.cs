using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Transfer;

/// <summary>What kind of file operation a <see cref="FileOpStep"/> records (its name in the menu and on the message line).</summary>
public enum FileOpKind
{
    Rename,
    NewFolder,
    Copy,
    Move,
    MoveAndCopy,
    Delete,
}

/// <summary>The item now at <see cref="Target"/> was at <see cref="Path"/> (a rename or a move).</summary>
public readonly record struct PathMove(string Path, string Target)
{
    /// <summary>The move that takes the item back (from <see cref="Target"/> to <see cref="Path"/>).</summary>
    public PathMove Reversed => new(Target, Path);
}

/// <summary>An item in the Recycle Bin: where it was, and its file there (the $R path the shell reported).</summary>
public readonly record struct BinEntry(string Original, string BinPath);

/// <summary>
/// One step of the app's undo / redo history, kept in the form its reversal takes, so undo and redo
/// are the same work: <see cref="Moved"/> go back from their Target to their Path (a rename or a
/// move), <see cref="Made"/> go to the Recycle Bin (a copy, a new folder, a delete undone) and
/// <see cref="Binned"/> come back from it (a delete, a copy undone). A copy is redone from the
/// Recycle Bin, not copied again: same contents, no name collision.
/// </summary>
public sealed record FileOpStep(FileOpKind Kind, IReadOnlyList<PathMove> Moved, IReadOnlyList<string> Made, IReadOnlyList<BinEntry> Binned)
{
    public int Count => Moved.Count + Made.Count + Binned.Count;

    public bool IsEmpty => Count == 0;

    /// <summary>The operation's name: 「元に戻す（{Label}）」 in the menu, the message line.</summary>
    public string Label => Kind switch
    {
        FileOpKind.Rename => "名前の変更",
        FileOpKind.NewFolder => "新しいフォルダーの作成",
        FileOpKind.Copy => "コピー",
        FileOpKind.Move => "移動",
        FileOpKind.MoveAndCopy => "移動・コピー",
        _ => "削除",
    };

    /// <summary>A rename (one item, or a batch rename as one step).</summary>
    public static FileOpStep Renamed(IEnumerable<PathMove> moves) => new(FileOpKind.Rename, moves.ToList(), [], []);

    public static FileOpStep NewFolder(string path) => new(FileOpKind.NewFolder, [], [path], []);

    public static FileOpStep Deleted(IEnumerable<BinEntry> binned) => new(FileOpKind.Delete, [], [], binned.ToList());

    /// <summary>A paste or a drop: the items moved (from, to) and the copies made.</summary>
    public static FileOpStep Transferred(IEnumerable<PathMove> moved, IEnumerable<string> copied)
    {
        var m = moved.ToList();
        var c = copied.Distinct(PathTools.Comparer).ToList(); // two sources of one name may end in one path
        var kind = m.Count == 0 ? FileOpKind.Copy : c.Count == 0 ? FileOpKind.Move : FileOpKind.MoveAndCopy;
        return new(kind, m, c, []);
    }

    /// <summary>The moves a reversal makes (each Target back to its Path).</summary>
    public IReadOnlyList<PathMove> MovesBack => Moved.Select(x => x.Reversed).ToList();

    /// <summary>
    /// The step after a reversal, from what was really done: the moves made (from, to), what went to
    /// the Recycle Bin, and what came back from it (their original paths). Same kind and name.
    /// </summary>
    public FileOpStep After(IReadOnlyList<PathMove> movedBack, IReadOnlyList<BinEntry> recycled, IReadOnlyList<BinEntry> restored) =>
        new(Kind, movedBack.ToList(), restored.Select(e => e.Original).ToList(), recycled.ToList());

    /// <summary>
    /// What a partial reversal left undone, from the same results as <see cref="After"/>: the moves
    /// not made back, the items not recycled, the entries not restored. Same kind; empty when all
    /// of it was done. It stays where the step was, so the next try acts on it and never on an
    /// older step below it that may hold its items.
    /// </summary>
    public FileOpStep Remainder(IReadOnlyList<PathMove> movedBack, IReadOnlyList<BinEntry> recycled, IReadOnlyList<BinEntry> restored)
    {
        static bool Same(PathMove a, PathMove b) => PathTools.PathEquals(a.Path, b.Path) && PathTools.PathEquals(a.Target, b.Target);

        var moved = Moved.Where(m => !movedBack.Any(d => Same(d, m.Reversed))).ToList();
        var made = Made.Where(p => !recycled.Any(r => PathTools.PathEquals(r.Original, p))).ToList();
        var binned = Binned.Where(b => !restored.Any(r => PathTools.PathEquals(r.Original, b.Original))).ToList();
        return new(Kind, moved, made, binned);
    }

    /// <summary>Every path the step names: the items' places on both sides of each move, the items made, the original places of the binned ones.</summary>
    public IEnumerable<string> Paths =>
        Moved.SelectMany(m => new[] { m.Path, m.Target }).Concat(Made).Concat(Binned.Select(b => b.Original));

    /// <summary>True when one of the step's paths is one of <paramref name="paths"/>, lies inside one of them or holds one of them.</summary>
    public bool Overlaps(IReadOnlyCollection<string> paths) =>
        Paths.Any(p => paths.Any(q => PathTools.IsSameOrUnder(p, q) || PathTools.IsSameOrUnder(q, p)));
}

/// <summary>
/// The app's own undo / redo history of the file operations it ran this session: one for the whole
/// app (not per tab), capped at <see cref="DefaultCapacity"/> steps per stack. Pure: no I/O. A
/// reversal is never recorded as a new step; it moves the step between the two stacks.
/// </summary>
public sealed class FileOpHistory(int capacity = FileOpHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 30;

    private readonly List<FileOpStep> _undo = [];
    private readonly List<FileOpStep> _redo = [];

    public FileOpStep? NextUndo => _undo.Count > 0 ? _undo[^1] : null;

    public FileOpStep? NextRedo => _redo.Count > 0 ? _redo[^1] : null;

    /// <summary>
    /// A new operation: on top of the undo stack (the oldest goes past the cap). The redo stack is
    /// cleared whenever the operation <paramref name="changed"/> something, also when nothing of it
    /// can be undone (an empty step: a replace or a merge): a redo after it could move or recycle
    /// what it brought.
    /// </summary>
    public void Record(FileOpStep step, bool changed = true)
    {
        if (changed || !step.IsEmpty)
        {
            _redo.Clear();
        }

        if (!step.IsEmpty)
        {
            Push(_undo, step);
        }
    }

    /// <summary>
    /// A reversal of <paramref name="step"/> ended (<paramref name="undo"/>: it was on the undo stack).
    /// It leaves its stack; <paramref name="after"/> goes on the other one unless it is null or empty.
    /// The redo stack is not cleared by a redo.
    /// </summary>
    public void Reversed(FileOpStep step, FileOpStep? after, bool undo) => Reversed(step, after, null, undo);

    /// <summary>
    /// As <see cref="Reversed(FileOpStep, FileOpStep?, bool)"/>, for a reversal that may have done only
    /// part of the step: <paramref name="remainder"/> (<see cref="FileOpStep.Remainder"/>), when not
    /// empty, takes the step's place on its stack (the step itself stays when nothing of it was
    /// done), and only <paramref name="after"/> goes on the other stack.
    /// </summary>
    public void Reversed(FileOpStep step, FileOpStep? after, FileOpStep? remainder, bool undo)
    {
        var stack = undo ? _undo : _redo;
        int i = stack.FindLastIndex(s => ReferenceEquals(s, step)); // by reference: see RemoveByReference
        if (i >= 0)
        {
            if (remainder is null || remainder.IsEmpty)
            {
                stack.RemoveAt(i);
            }
            else if (remainder.Count < step.Count)
            {
                stack[i] = remainder;
            }
        }

        if (after is { IsEmpty: false })
        {
            Push(undo ? _redo : _undo, after);
        }
    }

    /// <summary>Takes a step out of the history (refused: the items are no longer where it says).</summary>
    public void Drop(FileOpStep step)
    {
        RemoveByReference(_undo, step);
        RemoveByReference(_redo, step);
    }

    /// <summary>
    /// Takes out of both stacks every step that names one of <paramref name="paths"/>, a path inside
    /// one of them or a folder holding one of them (<see cref="FileOpStep.Overlaps"/>): something
    /// the history did not record changed there (a merge or a replace landed, a step was refused), so
    /// reversing those steps could move or recycle what is there now. Returns how many were removed.
    /// </summary>
    public int Invalidate(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrEmpty(p)).Distinct(PathTools.Comparer).ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        return _undo.RemoveAll(s => s.Overlaps(list)) + _redo.RemoveAll(s => s.Overlaps(list));
    }

    /// <summary>By reference: the record's value equality could take another step with the same contents.</summary>
    private static void RemoveByReference(List<FileOpStep> stack, FileOpStep step)
    {
        int i = stack.FindLastIndex(s => ReferenceEquals(s, step));
        if (i >= 0)
        {
            stack.RemoveAt(i);
        }
    }

    private void Push(List<FileOpStep> stack, FileOpStep step)
    {
        stack.Add(step);
        if (stack.Count > capacity)
        {
            stack.RemoveAt(0);
        }
    }
}

/// <summary>The checks before a reversal starts (blocking file system work: run it off the UI thread).</summary>
public static class FileOpChecks
{
    /// <summary>
    /// Why reversing <paramref name="step"/> cannot start (text for the message line, without the
    /// final 「。」), or null: every item is where the step says and every place it goes back to is free.
    /// </summary>
    public static string? ProblemReversing(FileOpStep step, IFileSystem fs)
    {
        foreach (var m in step.MovesBack) // moved / renamed back: m.Path -> m.Target
        {
            if (fs.GetKind(m.Path) == PathKind.Missing)
            {
                return $"「{PathTools.DisplayName(m.Path)}」が見つかりません";
            }

            bool sameItem = string.Equals(m.Path, m.Target, StringComparison.OrdinalIgnoreCase); // a change of case only
            if (!sameItem && fs.GetKind(m.Target) != PathKind.Missing)
            {
                return $"「{PathTools.DisplayName(m.Target)}」と同じ名前の項目がすでにあります";
            }

            if (fs.GetKind(PathTools.ParentOf(m.Target)) != PathKind.Folder)
            {
                return $"フォルダ「{PathTools.ParentOf(m.Target)}」が見つかりません";
            }
        }

        foreach (string p in step.Made)
        {
            var kind = fs.GetKind(p);
            if (kind == PathKind.Missing)
            {
                return $"「{PathTools.DisplayName(p)}」が見つかりません";
            }

            // A new folder goes to the Recycle Bin only while empty: whatever is in it now (moved in,
            // copied in by another app, a remainder of a reversal) was not made by this step.
            if (step.Kind == FileOpKind.NewFolder && (kind != PathKind.Folder || !IsEmptyFolder(p, fs)))
            {
                return kind == PathKind.Folder
                    ? $"「{PathTools.DisplayName(p)}」は空ではありません"
                    : $"「{PathTools.DisplayName(p)}」はフォルダではありません";
            }
        }

        foreach (var b in step.Binned)
        {
            if (fs.GetKind(b.BinPath) == PathKind.Missing)
            {
                return $"ごみ箱に「{PathTools.DisplayName(b.Original)}」が見つかりません";
            }

            if (fs.GetKind(b.Original) != PathKind.Missing)
            {
                return $"「{PathTools.DisplayName(b.Original)}」と同じ名前の項目がすでにあります";
            }

            if (fs.GetKind(PathTools.ParentOf(b.Original)) != PathKind.Folder)
            {
                return $"元のフォルダ「{PathTools.ParentOf(b.Original)}」が見つかりません";
            }
        }

        return null;
    }

    /// <summary>True when the folder lists nothing, hidden and '.' names included; a folder that cannot be read is not empty.</summary>
    private static bool IsEmptyFolder(string path, IFileSystem fs)
    {
        try
        {
            return fs.EnumerateDirectory(path, ListingFilter.Hidden | ListingFilter.DotNames, CancellationToken.None).Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return false;
        }
    }
}
