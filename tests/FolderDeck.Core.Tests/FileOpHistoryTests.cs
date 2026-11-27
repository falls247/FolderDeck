using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Transfer;

namespace FolderDeck.Core.Tests;

public class FileOpHistoryTests
{
    private static FileOpStep Rename(string from, string to) => FileOpStep.Renamed([new PathMove(from, to)]);

    [Fact]
    public void Record_ClearsRedo_AndDropsOldestPastCap()
    {
        var history = new FileOpHistory(capacity: 3);
        var steps = Enumerable.Range(1, 4).Select(i => Rename($@"C:\d\{i}.txt", $@"C:\d\{i}b.txt")).ToList();
        foreach (var s in steps)
        {
            history.Record(s);
        }

        int undoable = 0;
        while (history.NextUndo is { } s)
        {
            history.Reversed(s, null, undo: true);
            undoable++;
        }

        Assert.Equal(3, undoable);

        history.Record(steps[0]);
        var top = history.NextUndo!;
        history.Reversed(top, top.After(top.MovesBack, [], []), undo: true);
        Assert.NotNull(history.NextRedo);

        history.Record(Rename(@"C:\d\x.txt", @"C:\d\y.txt"));
        Assert.Null(history.NextRedo);

        history.Record(FileOpStep.Transferred([], [])); // empty: ignored
        Assert.Equal(FileOpKind.Rename, history.NextUndo!.Kind);
    }

    [Fact]
    public void Record_EmptyStepThatChangedFiles_ClearsRedo_ButKeepsUndo()
    {
        var history = new FileOpHistory();
        var step = Rename(@"C:\d\a.txt", @"C:\d\b.txt");
        history.Record(step);
        history.Reversed(step, step.After(step.MovesBack, [], []), undo: true);

        history.Record(FileOpStep.Transferred([], []), changed: false); // nothing happened: redo stays
        Assert.NotNull(history.NextRedo);

        history.Record(FileOpStep.Transferred([], [])); // a replace / merge only: nothing to undo, but redo would act on it
        Assert.Null(history.NextRedo);
        Assert.Null(history.NextUndo);
    }

    [Fact]
    public void Reversed_MovesStepBetweenStacks()
    {
        var history = new FileOpHistory();
        var first = FileOpStep.NewFolder(@"C:\d\新しいフォルダー");
        var second = FileOpStep.Transferred([new PathMove(@"C:\a\f.txt", @"C:\b\f.txt")], []);
        history.Record(first);
        history.Record(second);
        Assert.Equal(FileOpKind.Move, second.Kind);

        // Undo both: the move went back, the folder to the Recycle Bin.
        var movedBack = second.After(second.MovesBack, [], []);
        history.Reversed(second, movedBack, undo: true);
        var binned = first.After([], [new BinEntry(@"C:\d\新しいフォルダー", @"C:\$Recycle.Bin\S\$R1")], []);
        history.Reversed(first, binned, undo: true);
        Assert.Null(history.NextUndo);
        Assert.Same(binned, history.NextRedo);

        // Redo one: the other stays to be redone.
        var restored = binned.After([], [], binned.Binned);
        history.Reversed(binned, restored, undo: false);
        Assert.Same(restored, history.NextUndo);
        Assert.Same(movedBack, history.NextRedo);
        Assert.Equal([@"C:\d\新しいフォルダー"], restored.Made);

        // The move to redo goes from the old place to the new one again.
        Assert.Equal([new PathMove(@"C:\a\f.txt", @"C:\b\f.txt")], movedBack.MovesBack);
    }

    [Fact]
    public void Reversed_WithNothingDone_DropsStep()
    {
        var history = new FileOpHistory();
        var a = Rename(@"C:\d\a.txt", @"C:\d\b.txt");
        var same = Rename(@"C:\d\a.txt", @"C:\d\b.txt"); // equal by value, another step
        history.Record(a);
        history.Record(same);

        history.Reversed(same, same.After([], [], []), undo: true);
        Assert.Same(a, history.NextUndo); // by reference: the equal one below stays
        Assert.Null(history.NextRedo);

        history.Drop(a);
        Assert.Null(history.NextUndo);
    }

    [Fact]
    public void ProblemReversing_ChecksItemsAndPlaces()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\d\b.txt")
            .AddFile(@"C:\d\a.txt")
            .AddFile(@"C:\d\Case.txt")
            .AddFile(@"C:\bin\$R1.txt");

        // The old name is taken again.
        Assert.NotNull(FileOpChecks.ProblemReversing(Rename(@"C:\d\a.txt", @"C:\d\b.txt"), fs));

        // A change of case only: the same item.
        Assert.Null(FileOpChecks.ProblemReversing(Rename(@"C:\d\case.txt", @"C:\d\Case.txt"), fs));

        // The item is gone.
        Assert.NotNull(FileOpChecks.ProblemReversing(Rename(@"C:\d\z.txt", @"C:\d\gone.txt"), fs));
        Assert.NotNull(FileOpChecks.ProblemReversing(FileOpStep.NewFolder(@"C:\d\gone"), fs));

        // From the Recycle Bin: the original place is free / taken.
        Assert.Null(FileOpChecks.ProblemReversing(FileOpStep.Deleted([new BinEntry(@"C:\d\free.txt", @"C:\bin\$R1.txt")]), fs));
        Assert.NotNull(FileOpChecks.ProblemReversing(FileOpStep.Deleted([new BinEntry(@"C:\d\a.txt", @"C:\bin\$R1.txt")]), fs));
    }

    [Fact]
    public void Invalidate_RemovesStepsAtInsideOrAboveThePath_FromBothStacks()
    {
        var history = new FileOpHistory();
        var above = FileOpStep.NewFolder(@"C:\B");                              // holds the landing place
        var same = FileOpStep.Transferred([], [@"C:\B\Photos"]);                // a copy made there
        var unrelated = FileOpStep.Transferred([], [@"C:\B\Photos2"]);          // a name that only starts the same
        var inside = Rename(@"C:\B\Photos\x.jpg", @"C:\B\Photos\y.jpg");        // inside it, then on the redo stack
        history.Record(above);
        history.Record(same);
        history.Record(unrelated);
        history.Record(inside);
        history.Reversed(inside, inside.After(inside.MovesBack, [], []), undo: true);
        Assert.NotNull(history.NextRedo);

        // A merge landed at C:\B\Photos (case and a trailing separator do not matter).
        Assert.Equal(3, history.Invalidate([@"c:\b\photos\"]));
        Assert.Null(history.NextRedo);
        Assert.Same(unrelated, history.NextUndo);
        history.Drop(unrelated);
        Assert.Null(history.NextUndo);
    }

    [Fact]
    public void Reversed_PartOfStep_LeavesRemainderWhereTheStepWas()
    {
        var history = new FileOpHistory();
        var folder = FileOpStep.NewFolder(@"E:\N");
        PathMove[] moves = [new(@"F:\W\1", @"E:\N\1"), new(@"F:\W\2", @"E:\N\2")];
        var step = FileOpStep.Transferred(moves, [@"E:\N\c.txt"]);
        history.Record(folder);
        history.Record(step);

        // Cancelled midway: the first move went back, the copy was not recycled.
        PathMove[] movedBack = [moves[0].Reversed];
        var after = step.After(movedBack, [], []);
        var remainder = step.Remainder(movedBack, [], []);
        history.Reversed(step, after, remainder, undo: true);

        Assert.Same(remainder, history.NextUndo); // not the new folder below it
        Assert.Equal(FileOpKind.MoveAndCopy, remainder.Kind);
        Assert.Equal([moves[1]], remainder.Moved);
        Assert.Equal([@"E:\N\c.txt"], remainder.Made);
        Assert.Same(after, history.NextRedo);

        // Nothing of it done after a failure: the step itself stays.
        history.Reversed(remainder, remainder.After([], [], []), remainder.Remainder([], [], []), undo: true);
        Assert.Same(remainder, history.NextUndo);

        // The rest done: only then the older step is next.
        var bin = new BinEntry(@"E:\N\c.txt", @"E:\$Recycle.Bin\S\$R1.txt");
        var rest = remainder.MovesBack;
        history.Reversed(remainder, remainder.After(rest, [bin], []), remainder.Remainder(rest, [bin], []), undo: true);
        Assert.Same(folder, history.NextUndo);
    }

    [Fact]
    public void ProblemReversing_NewFolder_OnlyWhileEmpty()
    {
        var fs = new FakeFileSystem()
            .AddFolder(@"C:\d\Empty")
            .AddFile(@"C:\d\Dot\.keep", extra: FileAttributes.Hidden) // hidden and a '.' name: still not empty
            .AddFile(@"C:\d\File");

        Assert.Null(FileOpChecks.ProblemReversing(FileOpStep.NewFolder(@"C:\d\Empty"), fs));
        Assert.Contains("空ではありません", FileOpChecks.ProblemReversing(FileOpStep.NewFolder(@"C:\d\Dot"), fs));
        Assert.NotNull(FileOpChecks.ProblemReversing(FileOpStep.NewFolder(@"C:\d\File"), fs));
        Assert.NotNull(FileOpChecks.ProblemReversing(FileOpStep.NewFolder(@"C:\x"), new UnreadableFolders()));

        // A copied folder is recycled with what it holds (only a new folder is checked).
        Assert.Null(FileOpChecks.ProblemReversing(FileOpStep.Transferred([], [@"C:\d\Dot"]), fs));
    }

    /// <summary>Every path is a folder that cannot be listed.</summary>
    private sealed class UnreadableFolders : IFileSystem
    {
        public IReadOnlyList<FsEntry> EnumerateDirectory(string path, ListingFilter filter, CancellationToken cancellationToken) =>
            throw new UnauthorizedAccessException(path);

        public PathKind GetKind(string path) => PathKind.Folder;

        public FsEntry? GetEntry(string path) => null;
    }
}
