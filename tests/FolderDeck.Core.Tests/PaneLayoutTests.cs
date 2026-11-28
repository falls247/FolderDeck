using System.Text.Json;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Stores;

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

    private static PaneLeaf<string> Leaf(string pane) => new(pane);

    /// <summary>The tree as text: a leaf is its pane, a split "(first | second)" for columns, "(first / second)" for rows.</summary>
    private static string Shape(PaneNode<string> node) => node switch
    {
        PaneLeaf<string> leaf => leaf.Pane,
        PaneSplit<string> split => $"({Shape(split.First)} {(split.Axis == SplitAxis.Columns ? "|" : "/")} {Shape(split.Second)})",
        _ => "?",
    };

    [Fact]
    public void Split_PutsTheNewPaneRightOfOrBelowTheTarget_HalfEach()
    {
        var layout = PaneLayout<string>.Single("a").Split("a", SplitAxis.Columns, "b");
        Assert.Equal("(a | b)", Shape(layout.Root));
        Assert.Equal(0.5, ((PaneSplit<string>)layout.Root).Ratio);

        // Splitting a pane inside a split replaces only its leaf; the untouched part keeps its node.
        var keptLeaf = ((PaneSplit<string>)layout.Root).First;
        var nested = layout.Split("b", SplitAxis.Rows, "c");
        Assert.Equal("(a | (b / c))", Shape(nested.Root));
        Assert.Same(keptLeaf, ((PaneSplit<string>)nested.Root).First);
        Assert.Equal(["a", "b", "c"], nested.Panes);
        Assert.Equal(["a", "b"], layout.Panes); // immutable

        Assert.Throws<ArgumentException>(() => nested.Split("x", SplitAxis.Rows, "d"));
        Assert.Throws<ArgumentException>(() => nested.Split("a", SplitAxis.Rows, "c"));
    }

    [Fact]
    public void Close_TheOtherPartTakesTheSpace_AndTheNearestPaneIsNext()
    {
        // (a | (b / c)) | d
        var root = new PaneSplit<string>(
            SplitAxis.Columns,
            0.7,
            new PaneSplit<string>(SplitAxis.Columns, 0.4, Leaf("a"), new PaneSplit<string>(SplitAxis.Rows, 0.5, Leaf("b"), Leaf("c"))),
            Leaf("d"));
        var layout = new PaneLayout<string>(root);

        // a came first: its sibling (b / c) takes its place, its first pane is next.
        var withoutA = layout.Close("a", out string? next);
        Assert.Equal("((b / c) | d)", Shape(withoutA!.Root));
        Assert.Equal("b", next);
        Assert.Equal(0.7, ((PaneSplit<string>)withoutA.Root).Ratio);

        // d came second: the sibling's last pane (the one that touched d) is next.
        var withoutD = layout.Close("d", out next);
        Assert.Equal("(a | (b / c))", Shape(withoutD!.Root));
        Assert.Equal("c", next);

        var withoutB = layout.Close("b", out next);
        Assert.Equal("((a | c) | d)", Shape(withoutB!.Root));
        Assert.Equal("c", next);

        // The only pane: the tab closes.
        Assert.Null(PaneLayout<string>.Single("x").Close("x", out next));
        Assert.Null(next);
        Assert.Throws<ArgumentException>(() => layout.Close("x", out _));
    }

    [Fact]
    public void WithRatio_ChangesThatSplitOnly_ClampedToItsRange()
    {
        var inner = new PaneSplit<string>(SplitAxis.Rows, 0.5, Leaf("b"), Leaf("c"));
        var left = Leaf("a");
        var layout = new PaneLayout<string>(new PaneSplit<string>(SplitAxis.Columns, 0.5, left, inner));

        var changed = layout.WithRatio(inner, 0.3);
        var root = (PaneSplit<string>)changed.Root;
        Assert.Equal(0.5, root.Ratio);
        Assert.Equal(0.3, ((PaneSplit<string>)root.Second).Ratio);
        Assert.Same(left, root.First);
        Assert.Same(inner.First, ((PaneSplit<string>)root.Second).First);

        Assert.Equal(PaneRules.MaxRatio, ((PaneSplit<string>)layout.WithRatio((PaneSplit<string>)layout.Root, 2).Root).Ratio);
        Assert.Equal(PaneRules.MinRatio, ((PaneSplit<string>)layout.WithRatio((PaneSplit<string>)layout.Root, -1).Root).Ratio);
        var nan = (PaneSplit<string>)layout.WithRatio(inner, double.NaN).Root;
        Assert.Equal(PaneRules.DefaultRatio, ((PaneSplit<string>)nan.Second).Ratio);

        // A node of another layout (even one of the same shape) is refused.
        Assert.Throws<ArgumentException>(() => changed.WithRatio(inner, 0.4));
    }

    [Fact]
    public void Map_KeepsShapeAndRatios()
    {
        var layout = new PaneLayout<string>(new PaneSplit<string>(
            SplitAxis.Rows, 0.25, Leaf("a"), new PaneSplit<string>(SplitAxis.Columns, 0.6, Leaf("bb"), Leaf("ccc"))));

        var mapped = layout.Map(p => new Box(p.Length));
        Assert.Equal([1, 2, 3], mapped.Panes.Select(p => p.Value));
        var root = (PaneSplit<Box>)mapped.Root;
        Assert.Equal((SplitAxis.Rows, 0.25), (root.Axis, root.Ratio));
        var second = (PaneSplit<Box>)root.Second;
        Assert.Equal((SplitAxis.Columns, 0.6), (second.Axis, second.Ratio));
    }

    private sealed record Box(int Value);

    [Fact]
    public void Next_FollowsReadingOrder_AndWrapsBothWays()
    {
        var layout = PaneLayout<string>.Single("a").Split("a", SplitAxis.Columns, "b").Split("b", SplitAxis.Rows, "c");
        Assert.Equal("b", layout.Next("a", back: false));
        Assert.Equal("a", layout.Next("c", back: false));
        Assert.Equal("c", layout.Next("a", back: true));
        Assert.Equal("a", layout.Next("b", back: true));
        Assert.Equal("x", PaneLayout<string>.Single("x").Next("x", back: true));
        Assert.Throws<ArgumentException>(() => layout.Next("x", back: false));
    }

    [Theory]
    [InlineData(1, 565, 0, SplitAxis.Columns, SplitRefusal.None)] // exactly 2 × 280 + 5
    [InlineData(1, 564, 9999, SplitAxis.Columns, SplitRefusal.TooNarrow)]
    [InlineData(3, 9999, 325, SplitAxis.Rows, SplitRefusal.None)] // exactly 2 × 160 + 5
    [InlineData(3, 9999, 324, SplitAxis.Rows, SplitRefusal.TooLow)]
    [InlineData(4, 9999, 9999, SplitAxis.Columns, SplitRefusal.TooManyPanes)]
    public void CanSplit_NeedsRoomForTwoMinimumPanes_AndAtMostFourPanes(int panes, double width, double height, SplitAxis axis, SplitRefusal expected)
    {
        Assert.Equal(expected, PaneRules.CanSplit(panes, width, height, axis));
    }

    [Fact]
    public void MinExtent_AddsAlongTheSplitAxis_AndTakesTheLargerAcrossIt()
    {
        // a | (b / (c | d))
        var cd = new PaneSplit<string>(SplitAxis.Columns, 0.5, Leaf("c"), Leaf("d"));
        var bcd = new PaneSplit<string>(SplitAxis.Rows, 0.5, Leaf("b"), cd);
        var root = new PaneSplit<string>(SplitAxis.Columns, 0.5, Leaf("a"), bcd);

        Assert.Equal(280 + 5 + (280 + 5 + 280), PaneRules.MinExtent(root, SplitAxis.Columns));
        Assert.Equal(160 + 5 + 160, PaneRules.MinExtent(root, SplitAxis.Rows));
        Assert.Equal(160, PaneRules.MinExtent(Leaf("a"), SplitAxis.Rows));
    }

    [Fact]
    public void ClampRatio_KeepsBothPartsAtTheirMinimum_OrOnlyTheRangeWhenTheyCannotBe()
    {
        // a | (b | c): the first part needs 280, the second 565, of 1005 - 5 = 1000.
        var split = new PaneSplit<string>(
            SplitAxis.Columns, 0.5, Leaf("a"), new PaneSplit<string>(SplitAxis.Columns, 0.5, Leaf("b"), Leaf("c")));

        Assert.Equal(0.4, PaneRules.ClampRatio(split, 1005, 0.4), 6);
        Assert.Equal(0.28, PaneRules.ClampRatio(split, 1005, 0.1), 6);
        Assert.Equal(1 - 0.565, PaneRules.ClampRatio(split, 1005, 0.9), 6);

        // 600 wide cannot hold 280 + 5 + 565: the ratio stays as asked, only kept in range.
        Assert.Equal(0.9, PaneRules.ClampRatio(split, 600, 0.9), 6);
        Assert.Equal(PaneRules.MaxRatio, PaneRules.ClampRatio(split, 600, 1.5));
        Assert.Equal(PaneRules.MinRatio, PaneRules.ClampRatio(split, 0, -3));
    }

    [Fact]
    public void Settings_RoundTripTheTree_ByIndexIntoThePanes()
    {
        var layout = PaneLayout<string>.Single("a").Split("a", SplitAxis.Columns, "b").Split("a", SplitAxis.Rows, "c");
        layout = layout.WithRatio((PaneSplit<string>)layout.Root, 0.3);

        var node = PaneLayoutSettings.ToSetting(layout, layout.Panes);
        Assert.Equal("columns", node.Split);
        Assert.Equal(0.3, node.Ratio);
        Assert.Equal("rows", node.First!.Split);
        Assert.Equal([0, 1, 2], new[] { node.First.First!.Pane, node.First.Second!.Pane, node.Second!.Pane }.Select(p => p!.Value));
        Assert.Null(node.Second.Ratio); // leaves carry only their index

        string[] panes = ["x", "y", "z"];
        var back = PaneLayoutSettings.FromSetting(node, panes)!;
        Assert.Equal("((x / y) | z)", Shape(back.Root));
        Assert.Equal(0.3, ((PaneSplit<string>)back.Root).Ratio);
    }

    public static TheoryData<string, int> InvalidTrees => new()
    {
        { """{"split":"columns","first":{"pane":0},"second":{"pane":0}}""", 2 },                     // an index twice
        { """{"split":"columns","first":{"pane":0},"second":{"pane":1}}""", 3 },                     // pane 2 unused
        { """{"split":"columns","first":{"pane":0},"second":{"pane":2}}""", 2 },                     // out of range
        { """{"split":"diagonal","first":{"pane":0},"second":{"pane":1}}""", 2 },                    // unknown split
        { """{"split":"rows","first":{"pane":0}}""", 2 },                                            // a child missing
        { """{"split":"rows","pane":0,"first":{"pane":0},"second":{"pane":1}}""", 2 },               // a split with an index
        { """{"split":"rows","first":{"pane":0,"first":{"pane":1}},"second":{"pane":1}}""", 2 },     // a leaf with children
        { """{"split":"rows","first":{},"second":{"pane":1}}""", 2 },                                // a leaf without an index
        { """{"pane":0}""", 1 },                                                                     // one pane is not a split
        { """{"split":"rows","first":{"pane":0},"second":{"split":"rows","first":{"pane":1},"second":{"split":"rows","first":{"pane":2},"second":{"split":"rows","first":{"pane":3},"second":{"pane":4}}}}}""", 5 }, // more than 4 panes
    };

    [Theory]
    [MemberData(nameof(InvalidTrees))]
    public void Settings_AnInvalidTree_IsNoLayout(string json, int paneCount)
    {
        var node = JsonSerializer.Deserialize<PaneNodeSetting>(json, JsonFile.Options);
        var panes = Enumerable.Range(0, paneCount).Select(i => $"p{i}").ToList();
        Assert.False(PaneLayoutSettings.IsValid(node, paneCount));
        Assert.Null(PaneLayoutSettings.FromSetting(node, panes));
    }

    [Fact]
    public void Settings_ASplitValueIsReadLeniently_AndARatioOutOfRangeIsPulledIn()
    {
        var node = JsonSerializer.Deserialize<PaneNodeSetting>(
            """{"split":" Rows ","ratio":7,"first":{"pane":1},"second":{"pane":0}}""", JsonFile.Options);
        var layout = PaneLayoutSettings.FromSetting(node, ["a", "b"])!;
        Assert.Equal("(b / a)", Shape(layout.Root));
        Assert.Equal(PaneRules.MaxRatio, ((PaneSplit<string>)layout.Root).Ratio);
    }
}
