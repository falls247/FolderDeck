using FolderDeck.Core.Layout;

namespace FolderDeck.Core.Tests;

public class PaneLayoutTests
{
    [Fact]
    public void Panes_AreInReadingOrder_AndATabThatIsNotSplitHasOne()
    {
        // a | (b over c), then d right of all that
        var layout = new PaneLayout<string>(new PaneSplit<string>(
            SplitAxis.Columns,
            0.7,
            new PaneSplit<string>(
                SplitAxis.Columns,
                0.5,
                new PaneLeaf<string>("a"),
                new PaneSplit<string>(SplitAxis.Rows, 0.5, new PaneLeaf<string>("b"), new PaneLeaf<string>("c"))),
            new PaneLeaf<string>("d")));

        Assert.Equal(["a", "b", "c", "d"], layout.Panes);
        Assert.True(layout.Contains("c"));

        var single = PaneLayout<string>.Single("x");
        Assert.Equal(["x"], single.Panes);
        Assert.IsType<PaneLeaf<string>>(single.Root);
    }

    [Fact]
    public void APaneIsInTheLayoutOnce_AndPanesCompareByReference()
    {
        string pane = new('a', 1);
        Assert.Throws<ArgumentException>(() => new PaneLayout<string>(
            new PaneSplit<string>(SplitAxis.Rows, 0.5, new PaneLeaf<string>(pane), new PaneLeaf<string>(pane))));

        string equalButOther = new('a', 1);
        var layout = PaneLayout<string>.Single(pane);
        Assert.False(layout.Contains(equalButOther));
    }
}
