using FolderDeck.Core.Selection;

namespace FolderDeck.Core.Tests;

public class SelectionModelTests
{
    private const string A = @"E:\dir\a";
    private const string B = @"E:\dir\b";
    private const string C = @"E:\dir\c";
    private const string D = @"E:\dir\d";
    private const string E = @"E:\dir\e";

    private static readonly string[] Displayed = [A, B, C, D, E];

    [Fact]
    public void Toggle_FlipsTheKey_MovesTheAnchor_AndReportsEachChange()
    {
        var selection = new SelectionModel();
        int raised = 0;
        selection.Changed += () => raised++;

        Assert.True(selection.Toggle(B));
        Assert.True(selection.Toggle(D));
        Assert.Equal(2, selection.Count);
        Assert.True(selection.Contains(@"e:\DIR\b")); // paths compare case-insensitively
        Assert.Equal(D, selection.Anchor);

        Assert.True(selection.Toggle(B)); // off again; the toggled key is the anchor either way
        Assert.False(selection.Contains(B));
        Assert.Equal(B, selection.Anchor);
        Assert.Equal(3, raised);

        Assert.True(selection.Clear());
        Assert.False(selection.Clear()); // nothing left: no change, no event
        Assert.Equal(0, selection.Count);
        Assert.Null(selection.Anchor);
        Assert.Equal(4, raised);
    }

    [Fact]
    public void SelectRange_SpansFromTheAnchor_InBothDirections()
    {
        var selection = new SelectionModel();
        selection.Toggle(C);
        int raised = 0;
        selection.Changed += () => raised++;

        Assert.True(selection.SelectRange(Displayed, E, additive: false));
        Assert.Equal([C, D, E], selection.InOrder(Displayed));

        // The anchor did not move, so the next range starts at C again and replaces the first one.
        Assert.True(selection.SelectRange(Displayed, A, additive: false));
        Assert.Equal([A, B, C], selection.InOrder(Displayed));
        Assert.Equal(C, selection.Anchor);

        Assert.False(selection.SelectRange(Displayed, A, additive: false)); // same range again
        Assert.Equal(2, raised);
    }

    [Fact]
    public void Focus_IsTheItemClickedLast_TheAnchorOtherwise()
    {
        var selection = new SelectionModel();
        selection.Toggle(A);
        selection.SelectRange(Displayed, E, additive: false);
        Assert.Equal(A, selection.Anchor); // a range keeps its start…
        Assert.Equal(E, selection.Focus); // …but F2 goes to the item clicked last

        selection.Toggle(E); // Ctrl+click it off: the focus is no longer selected
        Assert.Equal(E, selection.Anchor);
        selection.Toggle(C);
        Assert.Equal(C, selection.Focus);

        selection.Clear();
        selection.Replace([B, D]);
        Assert.Equal(B, selection.Focus); // no click since: the anchor
    }

    [Fact]
    public void SelectRange_Additive_KeepsTheRestOfTheSelection()
    {
        var selection = new SelectionModel();
        selection.Toggle(A);
        selection.Toggle(D); // anchor

        Assert.True(selection.SelectRange(Displayed, E, additive: true));
        Assert.Equal([A, D, E], selection.InOrder(Displayed));

        Assert.True(selection.SelectRange(Displayed, C, additive: false)); // plain Shift: the range only
        Assert.Equal([C, D], selection.InOrder(Displayed));
    }

    [Fact]
    public void SelectRange_WithoutUsableAnchor_StartsAtTheClickedKey()
    {
        var selection = new SelectionModel();

        Assert.True(selection.SelectRange(Displayed, C, additive: false)); // no anchor yet
        Assert.Equal([C], selection.InOrder(Displayed));
        Assert.Equal(C, selection.Anchor);
        Assert.True(selection.SelectRange(Displayed, B, additive: false));
        Assert.Equal([B, C], selection.InOrder(Displayed));

        selection.Replace([@"E:\dir\gone"]); // the anchor is no longer in the list
        Assert.True(selection.SelectRange(Displayed, D, additive: false));
        Assert.Equal([D], selection.InOrder(Displayed));
        Assert.Equal(D, selection.Anchor);

        Assert.False(selection.SelectRange(Displayed, @"E:\dir\gone", additive: false)); // not displayed
        Assert.Equal([D], selection.InOrder(Displayed));
    }

    [Fact]
    public void Prune_InOrder_AndTargetsOf_FollowTheDisplayedList()
    {
        var selection = new SelectionModel();
        selection.Replace([A, D, B]);
        Assert.Equal(A, selection.Anchor);

        // Display order (and spelling) wins over the order the keys were selected in.
        string[] reloaded = [@"e:\dir\D", C, A];
        Assert.Equal([@"e:\dir\D", A], selection.InOrder(reloaded));

        Assert.True(selection.Prune(reloaded)); // B is gone
        Assert.Equal(2, selection.Count);
        Assert.Equal(A, selection.Anchor);
        Assert.False(selection.Prune(reloaded));

        Assert.True(selection.Prune([D])); // the anchor goes with its item
        Assert.Equal([D], selection.InOrder(Displayed));
        Assert.Null(selection.Anchor);

        // An action on a selected item applies to the whole selection, otherwise to that item alone.
        string[] selected = [A, B];
        Assert.Equal([A, B], SelectionModel.TargetsOf(selected, @"e:\DIR\b"));
        Assert.Equal([C], SelectionModel.TargetsOf(selected, C));
        Assert.Equal([C], SelectionModel.TargetsOf([], C));
    }

    [Fact]
    public void SelectBox_UnionAndToggle_CombineWithTheSnapshot_AndRestoreReturnsToIt()
    {
        var selection = new SelectionModel();
        selection.Toggle(A);
        selection.Toggle(C);
        var baseline = selection.Snapshot();
        int raised = 0;
        selection.Changed += () => raised++;

        Assert.True(selection.SelectBox(baseline, [C, D], toggle: false));
        Assert.Equal([A, C, D], selection.InOrder(Displayed));
        Assert.False(selection.SelectBox(baseline, [C, D], toggle: false)); // the same box again: no change
        Assert.Equal(1, raised);

        // Ctrl: flipped against the snapshot, not against what the previous box left.
        Assert.True(selection.SelectBox(baseline, [C, D], toggle: true));
        Assert.Equal([A, D], selection.InOrder(Displayed));
        Assert.True(selection.SelectBox(baseline, [D], toggle: true));
        Assert.Equal([A, C, D], selection.InOrder(Displayed));

        Assert.True(selection.Restore(baseline));
        Assert.Equal([A, C], selection.InOrder(Displayed));
        Assert.Equal(C, selection.Anchor);

        // An anchor that is not selected (Ctrl+click took it off) comes back too.
        selection.Toggle(C);
        var unselectedAnchor = selection.Snapshot();
        selection.SelectBox(unselectedAnchor, [B], toggle: false);
        Assert.Equal(B, selection.Anchor);
        selection.Restore(unselectedAnchor);
        Assert.Equal([A], selection.InOrder(Displayed));
        Assert.Equal(C, selection.Anchor);
    }

    [Fact]
    public void SelectBox_Anchor_KeepsBaseAnchor_ElseFirstSelectedBoxKey_ElseNull()
    {
        var selection = new SelectionModel();
        selection.SelectBox(selection.Snapshot(), [B, D], toggle: false);
        Assert.Equal(B, selection.Anchor);

        var onlyB = new SelectionModel();
        onlyB.Toggle(B);
        var baseline = onlyB.Snapshot();
        onlyB.SelectBox(baseline, [B, D], toggle: true); // B goes off: the first selected key of the box
        Assert.Equal(D, onlyB.Anchor);
        onlyB.SelectBox(baseline, [B], toggle: true);
        Assert.Equal(0, onlyB.Count);
        Assert.Null(onlyB.Anchor);
        onlyB.SelectBox(baseline, [D, E], toggle: false); // the baseline's anchor is still selected
        Assert.Equal(B, onlyB.Anchor);
    }
}
