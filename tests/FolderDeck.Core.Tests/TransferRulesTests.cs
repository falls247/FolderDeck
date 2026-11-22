using FolderDeck.Core.Transfer;

namespace FolderDeck.Core.Tests;

public class TransferRulesTests
{
    private static TransferSource Local(string path) => new(path, SourceMedia.Local, CanDelete: true);

    private static (TransferKind, CopyReason)[] Kinds(TransferPlan plan) =>
        plan.Items.Select(i => (i.Kind, i.Reason)).ToArray();

    // ---- TransferRules.Decide -------------------------------------------------------------------

    [Fact]
    public void Decide_SameFolder_MoveSkipped_PasteCopyRenamed_DragCopySkipped()
    {
        TransferSource[] sources = [Local(@"E:\docs\a.txt")];

        var cut = TransferRules.Decide(sources, @"E:\docs", TransferOptions.Paste(TransferKind.Move));
        Assert.Empty(cut.Items);
        Assert.Equal(TransferRefusal.SameFolder, cut.Refusal);

        // The target is the same folder in another spelling.
        var paste = TransferRules.Decide(sources, @"e:\DOCS\", TransferOptions.Paste(TransferKind.Copy));
        TransferItem copy = Assert.Single(paste.Items);
        Assert.Equal(new TransferItem(@"E:\docs\a.txt", TransferKind.Copy, CopyReason.Requested, RenameOnCollision: true), copy);
        Assert.Equal(TransferRefusal.None, paste.Refusal);

        var dragMove = TransferRules.Decide(sources, @"E:\docs", TransferOptions.Drag(ctrl: false));
        Assert.Empty(dragMove.Items);
        Assert.Equal(TransferRefusal.SameFolder, dragMove.Refusal);

        var dragCopy = TransferRules.Decide(sources, @"E:\docs", TransferOptions.Drag(ctrl: true));
        Assert.Empty(dragCopy.Items);
        Assert.Equal(TransferRefusal.SameFolder, dragCopy.Refusal);
    }

    [Fact]
    public void Decide_IntoItself_IsRefused_ButNotASiblingWithTheSamePrefix()
    {
        // Into a descendant, into the folder itself (case-insensitively), and as a copy too.
        Assert.Equal(TransferRefusal.IntoItself,
            TransferRules.Decide([Local(@"E:\a")], @"E:\a\b", TransferOptions.Drag(ctrl: false)).Refusal);
        Assert.Equal(TransferRefusal.IntoItself,
            TransferRules.Decide([Local(@"E:\a")], @"e:\A", TransferOptions.Drag(ctrl: false)).Refusal);
        Assert.Equal(TransferRefusal.IntoItself,
            TransferRules.Decide([Local(@"E:\a\")], @"E:\a\b\c", TransferOptions.Paste(TransferKind.Copy)).Refusal);
        Assert.False(TransferRules.CanDropInto([@"E:\a"], @"E:\a\b"));

        // "E:\ab" only shares the first characters with "E:\a".
        var sibling = TransferRules.Decide([Local(@"E:\a")], @"E:\ab", TransferOptions.Drag(ctrl: false));
        TransferItem item = Assert.Single(sibling.Items);
        Assert.Equal(new TransferItem(@"E:\a", TransferKind.Move, CopyReason.None, RenameOnCollision: false), item);
        Assert.Empty(sibling.Skipped);
        Assert.True(TransferRules.CanDropInto([@"E:\a"], @"E:\ab"));
    }

    [Theory]
    [InlineData(@"C:\", @"C:\foo")]
    [InlineData(@"\\srv\share", @"\\srv\share\sub")]
    [InlineData(@"\\srv\share\", @"E:\backup")]
    public void Decide_RootSource_IsRefusedAsRoot(string root, string target)
    {
        TransferOptions[] all =
        [
            TransferOptions.Drag(ctrl: false),
            TransferOptions.Drag(ctrl: true),
            TransferOptions.Paste(TransferKind.Move),
            TransferOptions.Paste(TransferKind.Copy),
        ];

        foreach (TransferOptions options in all)
        {
            var plan = TransferRules.Decide([Local(root)], target, options);
            Assert.Empty(plan.Items);
            Assert.Equal(TransferRefusal.Root, plan.Refusal);
        }

        Assert.False(TransferRules.IsTransferable(root));
        Assert.False(TransferRules.CanDropInto([root], target));
    }

    [Fact]
    public void Decide_KindPerItem_FollowsMediaAndDeletability()
    {
        TransferSource[] sources =
        [
            new(@"E:\src\local.txt", SourceMedia.Local, CanDelete: true),
            new(@"Z:\src\net.txt", SourceMedia.Network, CanDelete: true),
            new(@"D:\disc\ro.txt", SourceMedia.ReadOnly, CanDelete: true),
            new(@"E:\src\locked.txt", SourceMedia.Local, CanDelete: false),
        ];

        // Drag: NetworkBecomesCopy is on.
        var drag = TransferRules.Decide(sources, @"E:\dst", TransferOptions.Drag(ctrl: false));
        Assert.Equal(
            new[]
            {
                (TransferKind.Move, CopyReason.None),
                (TransferKind.Copy, CopyReason.Network),
                (TransferKind.Copy, CopyReason.ReadOnlyMedia),
                (TransferKind.Copy, CopyReason.NotDeletable),
            },
            Kinds(drag));
        Assert.Equal(1, drag.MoveCount);
        Assert.Equal(3, drag.CopyCount);

        // Cut and paste: NetworkBecomesCopy is off, a network source is really moved.
        var cut = TransferRules.Decide(sources, @"E:\dst", TransferOptions.Paste(TransferKind.Move));
        Assert.Equal(
            new[]
            {
                (TransferKind.Move, CopyReason.None),
                (TransferKind.Move, CopyReason.None),
                (TransferKind.Copy, CopyReason.ReadOnlyMedia),
                (TransferKind.Copy, CopyReason.NotDeletable),
            },
            Kinds(cut));

        // A requested copy is a copy for every item, whatever the media.
        var copy = TransferRules.Decide(sources, @"E:\dst", TransferOptions.Drag(ctrl: true));
        Assert.Equal(4, copy.CopyCount);
        Assert.All(copy.Items, i => Assert.Equal(CopyReason.Requested, i.Reason));
        Assert.All(drag.Items.Concat(cut.Items).Concat(copy.Items), i => Assert.False(i.RenameOnCollision));
    }

    [Fact]
    public void Decide_MixedBatch_TransfersTheValidItems_AndReportsTheRest()
    {
        TransferSource[] sources =
        [
            Local(@"E:\src\a.txt"),
            Local(@"e:\SRC\A.TXT"),                                        // the same file again: dropped
            new(@"\\nas\share\b.txt", SourceMedia.Network, CanDelete: true),
            Local(@"E:\src\sub"),
            Local(@"E:\src\sub\inner.txt"),                                // travels with E:\src\sub: dropped
            Local(@"E:\dst\here.txt"),                                     // already in the target
            Local(@"relative\x.txt"),
            Local(@"C:\"),
        ];

        var plan = TransferRules.Decide(sources, @"E:\dst", TransferOptions.Drag(ctrl: false));

        Assert.Equal(
            new[]
            {
                new TransferItem(@"E:\src\a.txt", TransferKind.Move, CopyReason.None, RenameOnCollision: false),
                new TransferItem(@"\\nas\share\b.txt", TransferKind.Copy, CopyReason.Network, RenameOnCollision: false),
                new TransferItem(@"E:\src\sub", TransferKind.Move, CopyReason.None, RenameOnCollision: false),
            },
            plan.Items.ToArray());
        Assert.Equal(
            new[]
            {
                (@"E:\dst\here.txt", TransferRefusal.SameFolder),
                (@"relative\x.txt", TransferRefusal.NotAPath),
                (@"C:\", TransferRefusal.Root),
            },
            plan.Skipped.ToArray());
        Assert.Equal(TransferRefusal.None, plan.Refusal);
        Assert.Equal(2, plan.MoveCount);
        Assert.Equal(1, plan.CopyCount);
    }

    [Fact]
    public void Decide_AllSkipped_RefusesWithTheFirstReason()
    {
        var plan = TransferRules.Decide(
            [Local(@"relative\x.txt"), Local(@"E:\dst\a.txt")], @"E:\dst", TransferOptions.Drag(ctrl: false));
        Assert.Empty(plan.Items);
        Assert.Equal(2, plan.Skipped.Count);
        Assert.Equal(TransferRefusal.NotAPath, plan.Refusal);

        // No destination folder (a virtual list): nothing is transferred.
        var noTarget = TransferRules.Decide([Local(@"E:\src\a.txt")], null, TransferOptions.Paste(TransferKind.Copy));
        Assert.Empty(noTarget.Items);
        Assert.Equal(TransferRefusal.NoTarget, noTarget.Refusal);
    }

    // ---- ShellMenuTargets.TryGroup --------------------------------------------------------------

    [Fact]
    public void TryGroup_SameParent_CaseInsensitively()
    {
        var group = ShellMenuTargets.TryGroup([@"E:\Docs\a.txt", @"e:\docs\B"]);

        Assert.True(group.HasValue);
        var (parent, paths) = group.GetValueOrDefault();
        Assert.Equal(@"E:\Docs", parent);
        Assert.Equal(new[] { @"E:\Docs\a.txt", @"e:\docs\B" }, paths.ToArray());
    }

    [Fact]
    public void TryGroup_DifferentParents_OrARoot_IsNull()
    {
        Assert.False(ShellMenuTargets.TryGroup([@"E:\a\x.txt", @"E:\b\y.txt"]).HasValue);
        Assert.False(ShellMenuTargets.TryGroup([@"E:\a\x.txt", @"E:\"]).HasValue);
    }
}
