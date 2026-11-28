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

    [Fact]
    public void Replace_PutsTheNewPaneInTheOldOnesPlace_KeepingRatiosAndOtherNodes()
    {
        // a | (b / c) at 0.3 and 0.6
        var inner = new PaneSplit<string>(SplitAxis.Rows, 0.6, Leaf("b"), Leaf("c"));
        var left = Leaf("a");
        var layout = new PaneLayout<string>(new PaneSplit<string>(SplitAxis.Columns, 0.3, left, inner));

        var replaced = layout.Replace("c", "x");
        Assert.Equal("(a | (b / x))", Shape(replaced.Root));
        var root = (PaneSplit<string>)replaced.Root;
        Assert.Equal(0.3, root.Ratio);
        Assert.Equal(0.6, ((PaneSplit<string>)root.Second).Ratio);
        Assert.Same(left, root.First);
        Assert.Same(inner.First, ((PaneSplit<string>)root.Second).First);
        Assert.Equal(["a", "b", "c"], layout.Panes); // immutable

        Assert.Same(layout, layout.Replace("b", "b"));
        Assert.Throws<ArgumentException>(() => layout.Replace("x", "y")); // not in the layout
        Assert.Throws<ArgumentException>(() => layout.Replace("a", "b")); // already in it
    }

    [Fact]
    public void Arrange_LaysThePanesOutOnTheUnitSquareByTheRatios()
    {
        var layout = new PaneLayout<string>(new PaneSplit<string>(
            SplitAxis.Columns, 0.25, Leaf("a"), new PaneSplit<string>(SplitAxis.Rows, 0.4, Leaf("b"), Leaf("c"))));

        var rects = layout.Arrange();
        Assert.Equal(["a", "b", "c"], rects.Select(r => r.Pane));
        Assert.Equal((0, 0, 0.25, 1), (rects[0].X, rects[0].Y, rects[0].Width, rects[0].Height));
        Assert.Equal((0.25, 0, 0.75, 0.4), (rects[1].X, rects[1].Y, rects[1].Width, rects[1].Height));
        Assert.Equal(0.4, rects[2].Y, 9);
        Assert.Equal(0.6, rects[2].Height, 9);
    }

    [Fact]
    public void Neighbor_IsThePaneSharingMostOfThatEdge_TopmostOrLeftmostOnATie_NullAtTheEdge()
    {
        // a | (b / c), half each
        var layout = new PaneLayout<string>(new PaneSplit<string>(
            SplitAxis.Columns, 0.5, Leaf("a"), new PaneSplit<string>(SplitAxis.Rows, 0.5, Leaf("b"), Leaf("c"))));
        Assert.Equal("b", layout.Neighbor("a", PaneDirection.Right)); // b and c share as much: the topmost
        Assert.Equal("a", layout.Neighbor("c", PaneDirection.Left));
        Assert.Equal("a", layout.Neighbor("b", PaneDirection.Left));
        Assert.Equal("b", layout.Neighbor("c", PaneDirection.Up));
        Assert.Equal("c", layout.Neighbor("b", PaneDirection.Down));
        Assert.Null(layout.Neighbor("a", PaneDirection.Left));
        Assert.Null(layout.Neighbor("a", PaneDirection.Up));
        Assert.Null(layout.Neighbor("b", PaneDirection.Right));
        Assert.Null(layout.Neighbor("c", PaneDirection.Down));
        Assert.Throws<ArgumentException>(() => layout.Neighbor("x", PaneDirection.Up));

        // b over c at 0.3: c shares more of a's right edge.
        var uneven = new PaneLayout<string>(new PaneSplit<string>(
            SplitAxis.Columns, 0.5, Leaf("a"), new PaneSplit<string>(SplitAxis.Rows, 0.3, Leaf("b"), Leaf("c"))));
        Assert.Equal("c", uneven.Neighbor("a", PaneDirection.Right));

        // (a / b) | (c / d) with the left split at 0.7 and the right at 0.4: from c (top right, 0–0.4)
        // only a touches on the left; from d (0.4–1), a shares 0.3 and b 0.3: a is topmost.
        var grid = new PaneLayout<string>(new PaneSplit<string>(
            SplitAxis.Columns,
            0.5,
            new PaneSplit<string>(SplitAxis.Rows, 0.7, Leaf("a"), Leaf("b")),
            new PaneSplit<string>(SplitAxis.Rows, 0.4, Leaf("c"), Leaf("d"))));
        Assert.Equal("a", grid.Neighbor("c", PaneDirection.Left));
        Assert.Equal("a", grid.Neighbor("d", PaneDirection.Left));
        Assert.Equal("d", grid.Neighbor("b", PaneDirection.Right)); // b (0.7–1) touches d only
        Assert.Equal("c", grid.Neighbor("a", PaneDirection.Right)); // of a's 0–0.7, c shares 0.4 and d 0.3
        Assert.Null(PaneLayout<string>.Single("x").Neighbor("x", PaneDirection.Right));
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
    public void Settings_RoundTripTheTree_ByIndexIntoTheTabs_WhichItNeedNotAllShow()
    {
        // (d / b) | e: three of five tabs shown.
        string[] tabs = ["a", "b", "c", "d", "e"];
        var layout = PaneLayout<string>.Single(tabs[3]).Split(tabs[3], SplitAxis.Columns, tabs[4]).Split(tabs[3], SplitAxis.Rows, tabs[1]);
        layout = layout.WithRatio((PaneSplit<string>)layout.Root, 0.3);

        var node = PaneLayoutSettings.ToSetting(layout, tabs);
        Assert.Equal("columns", node.Split);
        Assert.Equal(0.3, node.Ratio);
        Assert.Equal("rows", node.First!.Split);
        Assert.Equal([3, 1, 4], new[] { node.First.First!.Pane, node.First.Second!.Pane, node.Second!.Pane }.Select(p => p!.Value));
        Assert.Null(node.Second.Ratio); // leaves carry only their index
        Assert.True(PaneLayoutSettings.IsValid(node, tabs.Length));

        string[] restored = ["v", "w", "x", "y", "z"];
        var back = PaneLayoutSettings.FromSetting(node, restored)!;
        Assert.Equal("((y / w) | z)", Shape(back.Root));
        Assert.Equal(0.3, ((PaneSplit<string>)back.Root).Ratio);

        // A tab of the layout that is not in the order; a shown tab the app could not restore.
        Assert.Throws<ArgumentException>(() => PaneLayoutSettings.ToSetting(layout, ["a", "b", "c"]));
        Assert.Null(PaneLayoutSettings.FromSetting(node, new string?[] { "v", null, "x", "y", "z" }));
        Assert.NotNull(PaneLayoutSettings.FromSetting(node, new string?[] { null, "w", null, "y", "z" })); // only tabs not shown are missing

        // The same item at two leaves.
        string same = "s";
        Assert.Null(PaneLayoutSettings.FromSetting(node, new[] { "v", same, "x", same, "z" }));
    }

    public static TheoryData<string, int> InvalidTrees => new()
    {
        { """{"split":"columns","first":{"pane":0},"second":{"pane":0}}""", 2 },                     // an index twice
        { """{"split":"columns","first":{"pane":0},"second":{"pane":2}}""", 2 },                     // out of range
        { """{"split":"diagonal","first":{"pane":0},"second":{"pane":1}}""", 2 },                    // unknown split
        { """{"split":"rows","first":{"pane":0}}""", 2 },                                            // a child missing
        { """{"split":"rows","pane":0,"first":{"pane":0},"second":{"pane":1}}""", 2 },               // a split with an index
        { """{"split":"rows","first":{"pane":0,"first":{"pane":1}},"second":{"pane":1}}""", 2 },     // a leaf with children
        { """{"split":"rows","first":{},"second":{"pane":1}}""", 2 },                                // a leaf without an index
        { """{"pane":0}""", 3 },                                                                     // one pane is not a split
        { """{"split":"rows","first":{"pane":0},"second":{"pane":-1}}""", 3 },                       // a negative index
        { """{"split":"rows","first":{"pane":0},"second":{"split":"rows","first":{"pane":1},"second":{"split":"rows","first":{"pane":2},"second":{"split":"rows","first":{"pane":3},"second":{"pane":4}}}}}""", 7 }, // more than 4 panes
    };

    [Theory]
    [MemberData(nameof(InvalidTrees))]
    public void Settings_AnInvalidTree_IsNoLayout(string json, int itemCount)
    {
        var node = JsonSerializer.Deserialize<PaneNodeSetting>(json, JsonFile.Options);
        var items = Enumerable.Range(0, itemCount).Select(i => $"p{i}").ToList();
        Assert.False(PaneLayoutSettings.IsValid(node, itemCount));
        Assert.Null(PaneLayoutSettings.FromSetting(node, items));
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

    [Fact]
    public void Settings_TheLeafWriterAndReader_RoundTripEmptyPanes_ThroughTheirOwnLeaves()
    {
        // a | (_ / b): "_" stands for an empty pane, a pane object of its own in the app.
        string empty = "_";
        var layout = new PaneLayout<string>(new PaneSplit<string>(SplitAxis.Columns, 0.4,
            new PaneLeaf<string>("a"), new PaneSplit<string>(SplitAxis.Rows, 0.5, new PaneLeaf<string>(empty), new PaneLeaf<string>("b"))));
        var node = PaneLayoutSettings.ToSetting(layout, p => ReferenceEquals(p, empty) ? PaneNodeSetting.EmptyLeaf() : new PaneNodeSetting { Tab = new OpenTabSetting { Root = p } });
        Assert.Equal(("columns", 0.4, "rows"), (node.Split, node.Ratio, node.Second!.Split));
        Assert.Equal("a", node.First!.Tab!.Root);
        Assert.True(node.Second.First!.Empty);
        Assert.Null(node.Second.First.Pane);
        Assert.Equal("b", node.Second.Second!.Tab!.Root);
        Assert.True(PaneLayoutSettings.IsValidSaved(node));

        var asked = new List<string>();
        var back = PaneLayoutSettings.FromSetting(node, n =>
        {
            string item = n.Empty == true ? "empty" : n.Tab!.Root;
            asked.Add(item);
            return item;
        })!;
        Assert.Equal("(a | (empty / b))", Shape(back.Root));
        Assert.Equal(["a", "empty", "b"], asked); // in reading order

        // A leaf the reader cannot make, the same item twice, a tree of the wrong shape (the reader is not asked).
        Assert.Null(PaneLayoutSettings.FromSetting<string>(node, n => n.Empty == true ? null : n.Tab!.Root));
        Assert.Null(PaneLayoutSettings.FromSetting(node, _ => "same"));
        asked.Clear();
        Assert.Null(PaneLayoutSettings.FromSetting(PaneNodeSetting.EmptyLeaf(), n => { asked.Add("x"); return "x"; }));
        Assert.Empty(asked);
    }

    public static TheoryData<string, int, bool> Groups => new()
    {
        { """{"split":"columns","first":{"pane":1},"second":{"empty":true}}""", 2, true },                     // a tab and an empty pane
        { """{"split":"columns","first":{"pane":0},"second":{"empty":true}}""", 1, true },                     // one tab is enough
        { """{"split":"columns","first":{"split":"rows","first":{"empty":true},"second":{"pane":2}},"second":{"split":"rows","first":{"empty":true},"second":{"empty":true}}}""", 3, true }, // four panes
        { """{"split":"columns","first":{"empty":true},"second":{"empty":true}}""", 2, false },                // no tab
        { """{"split":"columns","first":{"pane":0},"second":{"pane":0}}""", 2, false },                        // a tab twice
        { """{"split":"columns","first":{"pane":2},"second":{"empty":true}}""", 2, false },                    // out of range
        { """{"split":"columns","first":{"pane":0,"empty":true},"second":{"pane":1}}""", 2, false },           // a leaf that is both
        { """{"split":"columns","first":{"tab":{"kind":"recent"}},"second":{"pane":1}}""", 2, false },          // a tab of a registered pane
        { """{"empty":true}""", 2, false },                                                                     // one pane
    };

    [Theory]
    [MemberData(nameof(Groups))]
    public void Settings_IsValidGroup(string json, int itemCount, bool valid) =>
        Assert.Equal(valid, PaneLayoutSettings.IsValidGroup(JsonSerializer.Deserialize<PaneNodeSetting>(json, JsonFile.Options), itemCount));

    public static TheoryData<string, bool> SavedLayouts => new()
    {
        { """{"split":"columns","first":{"tab":{"kind":"folder","root":"E:\\a","path":"b\\c"}},"second":{"empty":true}}""", true },
        { """{"split":"rows","first":{"tab":{"kind":" Recent ","root":"","path":""}},"second":{"tab":{"kind":"frequent"}}}""", true }, // fixed tabs at their top level
        { """{"split":"rows","first":{"tab":{"root":"E:\\a"}},"second":{"tab":{"root":"E:\\a"}}}""", true },       // the same folder twice: two folder tabs
        { """{"split":"rows","first":{"empty":true},"second":{"empty":true}}""", false },                             // no tab
        { """{"split":"rows","first":{"tab":{"kind":"folder","root":" "}},"second":{"empty":true}}""", false },        // a folder tab without a root
        { """{"split":"rows","first":{"tab":{"root":"E:\\a","path":"..\\b"}},"second":{"empty":true}}""", false },      // a path that climbs out
        { """{"split":"rows","first":{"tab":{"kind":"bogus","root":"E:\\a"}},"second":{"empty":true}}""", false },      // an unknown kind
        { """{"split":"rows","first":{"tab":{"kind":"recent"}},"second":{"tab":{"kind":"recent"}}}""", false },          // 最近の履歴 twice
        { """{"split":"rows","first":{"pane":0},"second":{"tab":{"kind":"recent"}}}""", false },                          // an index of settings.json
        { """{"tab":{"kind":"recent"}}""", false },                                                                       // one pane
    };

    [Theory]
    [MemberData(nameof(SavedLayouts))]
    public void Settings_IsValidSaved(string json, bool valid) =>
        Assert.Equal(valid, PaneLayoutSettings.IsValidSaved(JsonSerializer.Deserialize<PaneNodeSetting>(json, JsonFile.Options)));

    // ---- presets (the snap layouts of a tab dragged to the top of the panes) ----

    [Fact]
    public void Presets_PutPanesInReadingOrder_AndCoverTheSquare()
    {
        for (int preset = 0; preset < PanePresets.Count; preset++)
        {
            int count = PanePresets.ZoneCount(preset);
            Assert.InRange(count, 2, PaneRules.MaxPanes);
            var panes = Enumerable.Range(0, count).Select(i => $"p{i}").ToList();
            var layout = PanePresets.Build(preset, panes);
            Assert.Equal(panes, layout.Panes);
            var zones = PanePresets.ZonesOf(preset);
            Assert.Equal(count, zones.Count);
            Assert.Equal(1.0, zones.Sum(z => z.Width * z.Height), 6);
        }
    }

    [Fact]
    public void Presets_ThreeColumns_AreEqual_AndWrongCountThrows()
    {
        var zones = PanePresets.ZonesOf(2);
        Assert.All(zones, z => Assert.Equal(1.0 / 3, z.Width, 6));
        Assert.Throws<ArgumentException>(() => PanePresets.Build(0, new[] { "a" }));
    }
}
