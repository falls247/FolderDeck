using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;

namespace FolderDeck.Core.Tests;

public class FileDragTests
{
    private static readonly string LongPath = @"E:\" + new string('a', 130) + @"\" + new string('b', 130) + @"\photo.png"; // 274 characters
    private static readonly string ShortForm = @"E:\AAAAAA~1\BBBBBB~1\photo.png";

    private static string? NoShortPath(string path) => null;

    private static string? MustNotBeAsked(string path) => throw new InvalidOperationException("short path asked for " + path);

    // ---- PathTools.ForShell ---------------------------------------------------------------------

    [Fact]
    public void ForShell_ShortPath_IsKept_WithoutAskingTheResolver()
    {
        Assert.Equal(@"E:\資料\a.png", PathTools.ForShell(@"E:\資料\a.png", MustNotBeAsked));
    }

    [Fact]
    public void ForShell_LongPath_UsesTheShortForm()
    {
        Assert.True(LongPath.Length >= PathTools.MaxShellPath);
        Assert.Equal(ShortForm, PathTools.ForShell(LongPath, _ => ShortForm));
    }

    [Fact]
    public void ForShell_Exactly260_IsShortened()
    {
        string path = @"E:\" + new string('x', PathTools.MaxShellPath - 3);
        Assert.Equal(PathTools.MaxShellPath, path.Length);
        Assert.Equal(@"E:\XXXXXX~1", PathTools.ForShell(path, _ => @"E:\XXXXXX~1"));
    }

    [Fact]
    public void ForShell_Just259_IsKept()
    {
        string path = @"E:\" + new string('x', PathTools.MaxShellPath - 4);
        Assert.Equal(PathTools.MaxShellPath - 1, path.Length);
        Assert.Equal(path, PathTools.ForShell(path, MustNotBeAsked));
    }

    [Fact]
    public void ForShell_NoShortForm_KeepsTheLongPath()
    {
        Assert.Equal(LongPath, PathTools.ForShell(LongPath, NoShortPath));
        Assert.Equal(LongPath, PathTools.ForShell(LongPath, _ => string.Empty));
    }

    [Fact]
    public void ForShell_ShortFormStillTooLong_KeepsTheLongPath()
    {
        string stillLong = new('s', PathTools.MaxShellPath);
        Assert.Equal(LongPath, PathTools.ForShell(LongPath, _ => stillLong));
    }

    // ---- FileDrag.IsDraggablePath ---------------------------------------------------------------

    [Theory]
    [InlineData(@"E:\資料\a.png", true)]
    [InlineData(@"E:\", true)]
    [InlineData(@"\\server\share\資料", true)]
    [InlineData(@"資料\a.png", false)]
    [InlineData(@"E:資料", false)]
    [InlineData(@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsDraggablePath_OnlyFullyQualifiedPaths(string? path, bool expected)
    {
        Assert.Equal(expected, FileDrag.IsDraggablePath(path));
    }

    // ---- FileDrag.DropList ----------------------------------------------------------------------

    [Fact]
    public void DropList_KeepsOrder_SkipsInvalid_AndDuplicates()
    {
        var list = FileDrag.DropList([@"E:\b.png", null, "", @"relative\x", @"E:\a.png", @"e:\B.PNG"], NoShortPath);
        Assert.Equal([@"E:\b.png", @"E:\a.png"], list);
    }

    [Fact]
    public void DropList_ShortensLongPaths_Only()
    {
        var list = FileDrag.DropList([@"E:\a.png", LongPath], p => p == LongPath ? ShortForm : throw new InvalidOperationException(p));
        Assert.Equal([@"E:\a.png", ShortForm], list);
    }

    [Fact]
    public void DropList_LongPathWithoutShortForm_StaysLong()
    {
        Assert.Equal([LongPath], FileDrag.DropList([LongPath], NoShortPath));
    }

    [Fact]
    public void DropList_Empty()
    {
        Assert.Empty(FileDrag.DropList([], NoShortPath));
        Assert.Empty(FileDrag.DropList([null, " "], NoShortPath));
    }

    // ---- FileDrag.IsFromThisApp -----------------------------------------------------------------

    [Fact]
    public void IsFromThisApp_OwnDrags()
    {
        Assert.True(FileDrag.IsFromThisApp(Has("FileDrop", FileDrag.SourceFormat)));
        Assert.True(FileDrag.IsFromThisApp(Has("FileDrop", FileDrag.SourceFormat, FileDrag.ReorderFormat)));
        Assert.True(FileDrag.IsFromThisApp(Has(FileDrag.ReorderFormat))); // a reorder drag alone
    }

    [Fact]
    public void IsFromThisApp_ExplorerDrop_IsNot()
    {
        Assert.False(FileDrag.IsFromThisApp(Has("FileDrop", "FileNameW", "Shell IDList Array", "Preferred DropEffect")));
        Assert.False(FileDrag.IsFromThisApp(Has()));
    }

    [Fact]
    public void IsFromThisApp_FormatNamesAreExact()
    {
        Assert.False(FileDrag.IsFromThisApp(Has("FolderDeck", "folderdeck.dragsource ")));
    }

    // ---- FileDrag.IsDragGesture -----------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, 3, false)]
    [InlineData(4, 4, false)] // exactly the system distance: still a click
    [InlineData(-4, -4, false)]
    [InlineData(4.5, 0, true)]
    [InlineData(0, 4.5, true)]
    [InlineData(-5, 0, true)]
    [InlineData(0, -5, true)]
    [InlineData(3, -6, true)]
    public void IsDragGesture_StartsPastTheSystemDistance(double dx, double dy, bool expected)
    {
        Assert.Equal(expected, FileDrag.IsDragGesture(dx, dy, minWidth: 4, minHeight: 4));
    }

    [Fact]
    public void IsDragGesture_AxesHaveTheirOwnDistance()
    {
        Assert.False(FileDrag.IsDragGesture(5, 0, minWidth: 6, minHeight: 2));
        Assert.True(FileDrag.IsDragGesture(0, 3, minWidth: 6, minHeight: 2));
    }

    private static Func<string, bool> Has(params string[] formats)
    {
        var set = new HashSet<string>(formats, StringComparer.Ordinal);
        return set.Contains;
    }
}
