using FolderDeck.Core.Selection;

namespace FolderDeck.Core.Tests;

public class GridNavigationTests
{
    // 10 items, 4 columns:  0 1 2 3 / 4 5 6 7 / 8 9
    private const int Count = 10;
    private const int Columns = 4;

    [Theory]
    [InlineData(0, NavKey.Left, 0)]   // the first item: no move
    [InlineData(9, NavKey.Right, 9)]  // the last item: no move
    [InlineData(3, NavKey.Right, 4)]  // Left / Right run across rows
    [InlineData(2, NavKey.Up, 2)]     // the first row: stays
    [InlineData(6, NavKey.Up, 2)]
    [InlineData(9, NavKey.Down, 9)]   // the last row: stays
    [InlineData(0, NavKey.Home, 0)]
    [InlineData(5, NavKey.End, 9)]
    public void Move_StopsAtTheEdges(int current, NavKey key, int expected)
    {
        Assert.Equal(expected, GridNavigation.Move(current, key, Count, Columns, pageRows: 2));
    }

    [Theory]
    [InlineData(5, 9)] // the item below exists
    [InlineData(6, 9)] // the last row is too short for this column: the last item, as Explorer does
    [InlineData(7, 9)]
    public void Move_Down_IntoAPartialLastRow_GoesToTheLastItem(int current, int expected)
    {
        Assert.Equal(expected, GridNavigation.Move(current, NavKey.Down, Count, Columns, pageRows: 2));
    }

    [Theory]
    [InlineData(1, NavKey.PageDown, 1, 5)]  // one row of 4
    [InlineData(1, NavKey.PageDown, 3, 9)]  // past the end: this column's last item
    [InlineData(6, NavKey.PageDown, 3, 9)]  // already this column's last item: the list's last item
    [InlineData(9, NavKey.PageUp, 1, 5)]
    [InlineData(9, NavKey.PageUp, 5, 1)]    // past the top: this column's first item
    [InlineData(2, NavKey.PageUp, 5, 0)]    // already on the first row: the first item
    public void Move_PageKeys_MoveByScreens_InTheSameColumn(int current, NavKey key, int pageRows, int expected)
    {
        Assert.Equal(expected, GridNavigation.Move(current, key, Count, Columns, pageRows));
    }

    [Fact]
    public void Move_WithoutACursor_StartsAtTheFirstItem_EndAtTheLast_EmptyListHasNone()
    {
        Assert.Equal(0, GridNavigation.Move(-1, NavKey.Down, Count, Columns, pageRows: 2));
        Assert.Equal(0, GridNavigation.Move(-1, NavKey.PageDown, Count, Columns, pageRows: 2));
        Assert.Equal(9, GridNavigation.Move(-1, NavKey.End, Count, Columns, pageRows: 2));
        Assert.Equal(-1, GridNavigation.Move(-1, NavKey.Down, 0, Columns, pageRows: 2));
        Assert.Equal(-1, GridNavigation.Move(3, NavKey.Up, 0, Columns, pageRows: 2));
    }

    [Fact]
    public void FindByPrefix_CyclesAfterTheCurrent_KeepsItWhileTyping_WrapsAndMisses()
    {
        string[] names = ["apple", "Banana", "avocado", "berry", "Blueberry"];

        Assert.Equal(1, GridNavigation.FindByPrefix(names, -1, "b"));    // ignores case
        Assert.Equal(3, GridNavigation.FindByPrefix(names, 1, "b"));     // the same letter: the next one
        Assert.Equal(0, GridNavigation.FindByPrefix(names, 2, "a"));     // wraps past the end
        Assert.Equal(4, GridNavigation.FindByPrefix(names, 4, "bl"));    // typed on: the current item stays
        Assert.Equal(2, GridNavigation.FindByPrefix(names, 0, "av"));
        Assert.Equal(2, GridNavigation.FindByPrefix(names, 0, "aa"));    // a repeated letter no name has: cycles
        Assert.Equal(-1, GridNavigation.FindByPrefix(names, 0, "z"));
        Assert.Equal(-1, GridNavigation.FindByPrefix([], -1, "a"));
    }
}
