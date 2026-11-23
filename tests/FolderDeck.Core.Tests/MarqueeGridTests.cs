using FolderDeck.Core.Input;
using FolderDeck.Core.Layout;

namespace FolderDeck.Core.Tests;

public class MarqueeGridTests
{
    // 3 columns, 7 items (the last row has one card), cards of 100 × 80 with 12 px gaps.
    private static readonly CellGrid Cards = new(Columns: 3, Count: 7, CellWidth: 100, CellHeight: 80, PitchX: 112, PitchY: 92);

    private static int[] Selected(MarqueeRect rect, CellGrid grid) =>
        [.. MarqueeGrid.Indices(MarqueeGrid.Span(rect, grid), grid.Columns, grid.Count)];

    [Fact]
    public void Span_Grid_SelectsOverlappingCells_InRowMajorOrder_SkippingPartialLastRow()
    {
        Assert.Equal([0, 1, 3, 4], Selected(new MarqueeRect(50, 50, 130, 100), Cards));

        // The box reaches the empty cells after the last card: only the card itself.
        Assert.Equal([6], Selected(new MarqueeRect(50, 190, 300, 200), Cards));

        // Dragged up and to the left: the same box.
        Assert.Equal(MarqueeGrid.Span(new MarqueeRect(50, 50, 130, 100), Cards), MarqueeGrid.Span(MarqueeRect.Between(130, 100, 50, 50), Cards));
    }

    [Fact]
    public void Span_GapEdgeOutsideInvalid_IsEmpty_AndListIsRowRange()
    {
        Assert.Equal(CellSpan.Empty, MarqueeGrid.Span(new MarqueeRect(101, 0, 111, 300), Cards)); // a gap between columns
        Assert.Equal(CellSpan.Empty, MarqueeGrid.Span(new MarqueeRect(0, 81, 400, 91), Cards));   // a gap between rows
        Assert.Equal(CellSpan.Empty, MarqueeGrid.Span(new MarqueeRect(0, 300, 400, 400), Cards)); // below the content
        Assert.Equal(CellSpan.Empty, MarqueeGrid.Span(new MarqueeRect(0, 0, 50, 50), Cards with { Columns = 0 }));
        Assert.Equal(CellSpan.Empty, MarqueeGrid.Span(new MarqueeRect(0, 0, 50, 50), Cards with { PitchY = 0 }));
        Assert.Equal(CellSpan.Empty, MarqueeGrid.Span(new MarqueeRect(double.NaN, 0, 50, 50), Cards));

        // An edge that only touches a cell does not take it.
        Assert.Equal([1, 2], Selected(new MarqueeRect(100, 10, 400, 20), Cards));
        Assert.Equal([0], Selected(new MarqueeRect(10, 10, 112, 20), Cards));

        // The details list: one column, the whole row is the cell.
        var rows = new CellGrid(Columns: 1, Count: 10, CellWidth: 500, CellHeight: 28, PitchX: 500, PitchY: 28);
        Assert.Equal([2, 3, 4, 5], Selected(MarqueeRect.Between(10, 160, 10, 60), rows));
        Assert.Equal(new CellSpan(0, 9, 0, 0), MarqueeGrid.Span(new MarqueeRect(10, -50, 20, 1000), rows));
    }

    [Fact]
    public void OutsideStep_ZeroInside_RampsPastEdge_CapsAtMax()
    {
        static double Step(double y, double height = 400) => EdgeScroll.OutsideStep(y, height, inset: 8, ramp: 64, maxStep: 24);

        Assert.Equal(0, Step(200));
        Assert.Equal(0, Step(392));
        Assert.Equal(3.6, Step(393), 6);
        Assert.Equal(24, Step(500), 6);
        Assert.Equal(-3.6, Step(4), 6);
        Assert.Equal(-24, Step(-100), 6);
        Assert.Equal(0, Step(500, height: 0));
    }
}
