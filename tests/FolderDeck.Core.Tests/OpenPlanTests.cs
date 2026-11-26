using FolderDeck.Core.Selection;

namespace FolderDeck.Core.Tests;

public class OpenPlanTests
{
    private sealed record Item(string Name, bool IsFolder, bool Busy = false);

    private static OpenPlan.Plan<Item> PlanOf(params Item[] selected) =>
        OpenPlan.Of(selected, x => x.IsFolder, x => !x.Busy);

    [Fact]
    public void OneUsableItem_OpensAsAClick_EvenWhenOthersAreBusy()
    {
        var folder = new Item("a", IsFolder: true);
        var plan = PlanOf(folder, new Item("b.txt", IsFolder: false, Busy: true));

        Assert.Same(folder, plan.Single);
        Assert.Empty(plan.Files);
        Assert.Empty(plan.Folders);
        Assert.Equal(1, plan.Count);
    }

    [Fact]
    public void SeveralItems_SplitIntoFilesThenFolders_InDisplayOrder_WithoutBusyOnes()
    {
        var f1 = new Item("f1", IsFolder: true);
        var x1 = new Item("x1.txt", IsFolder: false);
        var f2 = new Item("f2", IsFolder: true);
        var x2 = new Item("x2.txt", IsFolder: false);
        var plan = PlanOf(f1, x1, new Item("busy.txt", IsFolder: false, Busy: true), f2, x2);

        Assert.Null(plan.Single);
        Assert.Equal([x1, x2], plan.Files);
        Assert.Equal([f1, f2], plan.Folders);
        Assert.Equal(4, plan.Count);

        Assert.True(PlanOf(new Item("a", true, Busy: true), new Item("b", false, Busy: true)).IsEmpty);
    }

    [Fact]
    public void NeedsConfirm_Above15Items()
    {
        Item[] Files(int n) => Enumerable.Range(0, n).Select(i => new Item($"{i}.txt", IsFolder: false)).ToArray();

        Assert.False(PlanOf(Files(OpenPlan.ConfirmAbove)).NeedsConfirm);
        Assert.True(PlanOf(Files(OpenPlan.ConfirmAbove + 1)).NeedsConfirm);
    }
}
