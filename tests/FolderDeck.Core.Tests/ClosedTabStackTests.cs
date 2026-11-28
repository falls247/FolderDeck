using FolderDeck.Core.Ordering;

namespace FolderDeck.Core.Tests;

public class ClosedTabStackTests
{
    [Fact]
    public void ReopeningInTurn_GivesTheOldOrderBack_AlsoAfterClosingSeveral()
    {
        var strip = new List<string> { "a", "b", "c", "d", "e" };
        var closed = new ClosedTabStack<string>();
        foreach (string tab in new[] { "b", "d", "a" }) // as 「ほかのタブを閉じる」 or one by one
        {
            closed.Push(tab, strip.IndexOf(tab));
            strip.Remove(tab);
        }

        while (closed.TryPop(_ => true, out string tab, out int index))
        {
            strip.Insert(ClosedTabStack.InsertIndex(index, strip.Count), tab);
        }

        Assert.Equal(["a", "b", "c", "d", "e"], strip);
        Assert.Equal(0, ClosedTabStack.InsertIndex(-3, 2));
        Assert.Equal(2, ClosedTabStack.InsertIndex(9, 2)); // the strip got shorter meanwhile: the right end
    }

    [Fact]
    public void Capacity_DropsTheOldest_TryPopSkipsAndDropsRefusedRecords()
    {
        var closed = new ClosedTabStack<string>(capacity: 3);
        foreach (string tab in new[] { "1", "2", "recent", "4" })
        {
            closed.Push(tab, 0);
        }

        Assert.Equal(3, closed.Count); // "1" fell off
        Assert.True(closed.TryPop(t => t != "4", out string item, out _));
        Assert.Equal("recent", item); // "4" was refused and is gone
        closed.ReplaceAll(t => t + "!");
        Assert.True(closed.TryPop(_ => true, out item, out _));
        Assert.Equal("2!", item);
        Assert.False(closed.TryPop(_ => true, out _, out _));
        Assert.False(closed.CanPop);
    }
}
