using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Tests;

public class PathToolsTests
{
    [Theory]
    [InlineData("\"E:\\資料\"", "E:\\資料")]
    [InlineData("  \"E:\\資料\"  ", "E:\\資料")]
    [InlineData("\"'E:\\資料'\"", "E:\\資料")]
    [InlineData("“E:\\資料”", "E:\\資料")]
    [InlineData("‘E:\\資料’", "E:\\資料")]
    [InlineData("\" E:\\資料 \"", "E:\\資料")]
    [InlineData("E:\\資料", "E:\\資料")]
    [InlineData("\"E:\\資料", "\"E:\\資料")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("\"\"", "")]
    public void CleanInput_TrimsAndStripsQuotesRepeatedly(string input, string expected)
    {
        Assert.Equal(expected, PathTools.CleanInput(input));
    }

    [Fact]
    public void CleanInput_ExpandsEnvironmentVariables()
    {
        Environment.SetEnvironmentVariable("FD_TEST_ROOT", @"C:\fd-root");
        Assert.Equal(@"C:\fd-root\資料", PathTools.CleanInput("\"%FD_TEST_ROOT%\\資料\""));
    }

    [Fact]
    public void CleanInput_Null_IsEmpty()
    {
        Assert.Equal(string.Empty, PathTools.CleanInput(null));
    }

    [Theory]
    [InlineData("E:\\資料\\", "E:\\資料")]
    [InlineData("E:\\資料\\\\", "E:\\資料")]
    [InlineData("E:\\", "E:\\")]
    [InlineData("e:\\a\\.\\b\\..\\c", "e:\\a\\c")]
    [InlineData("E:/資料/2026/", "E:\\資料\\2026")]
    [InlineData("\"E:\\資料\\\"", "E:\\資料")]
    public void TryNormalizeInput_NormalizesAbsolutePaths(string input, string expected)
    {
        Assert.True(PathTools.TryNormalizeInput(input, out string normalized, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void TryNormalizeInput_Empty_IsRejected()
    {
        Assert.False(PathTools.TryNormalizeInput("  \"\" ", out _, out string? error));
        Assert.Equal("フォルダのパスを入力してください。", error);
    }

    [Theory]
    [InlineData("資料\\2026")]
    [InlineData("E:資料")]
    [InlineData("\\資料")]
    public void TryNormalizeInput_Relative_IsRejected(string input)
    {
        Assert.False(PathTools.TryNormalizeInput(input, out _, out string? error));
        Assert.Equal($"絶対パスで指定してください: {input}", error);
    }

    [Theory]
    [InlineData("E:\\", true)]
    [InlineData("E:", true)]
    [InlineData("E:\\資料", false)]
    [InlineData("\\\\server\\share", true)]
    [InlineData("\\\\server\\share\\", true)]
    [InlineData("\\\\server\\share\\dir", false)]
    public void IsRoot(string path, bool expected)
    {
        Assert.Equal(expected, PathTools.IsRoot(path));
    }

    [Theory]
    [InlineData("E:\\", "E:\\")]
    [InlineData("E:\\資料", "資料")]
    [InlineData("E:\\資料\\", "資料")]
    [InlineData("E:\\資料\\2026", "2026")]
    [InlineData("\\\\server\\share", "\\\\server\\share")]
    public void DisplayName_IsLastPartOrRoot(string path, string expected)
    {
        Assert.Equal(expected, PathTools.DisplayName(path));
    }

    [Theory]
    [InlineData("C:\\a\\\\", "C:\\a")]
    [InlineData("C:\\", "C:\\")]
    [InlineData("\\\\server\\share\\", "\\\\server\\share")]
    [InlineData("C:\\a", "C:\\a")]
    public void TrimTrailingSeparators_KeepsRoot(string path, string expected)
    {
        Assert.Equal(expected, PathTools.TrimTrailingSeparators(path));
    }

    [Fact]
    public void RelativeHelpers()
    {
        Assert.Equal("a", PathTools.JoinRel("", "a"));
        Assert.Equal("a\\b", PathTools.JoinRel("a", "b"));
        Assert.Equal(["a", "b", "c"], PathTools.SplitRel("a\\b/c\\"));
        Assert.Empty(PathTools.SplitRel(""));
        Assert.Equal("a\\b", PathTools.ParentRel("a\\b\\c"));
        Assert.Equal(string.Empty, PathTools.ParentRel("a"));
        Assert.Equal("E:\\資料\\2026\\見積", PathTools.Combine("E:\\資料", "2026\\見積"));
        Assert.Equal("E:\\資料", PathTools.Combine("E:\\資料", ""));
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("a\\b", true)]
    [InlineData("a\\..\\b", false)]
    [InlineData("..", false)]
    [InlineData(".\\a", false)]
    [InlineData("C:\\a", false)]
    [InlineData("\\a", false)]
    public void IsSafeRel(string rel, bool expected)
    {
        Assert.Equal(expected, PathTools.IsSafeRel(rel));
    }

    [Theory]
    [InlineData("E:\\資料", "E:\\資料\\2026\\見積", "2026\\見積")]
    [InlineData("E:\\資料", "e:\\資料\\", "")]
    [InlineData("E:\\資料", "E:\\資料2\\x", null)]
    [InlineData("E:\\", "E:\\a\\b", "a\\b")]
    public void RelativeTo(string root, string path, string? expected)
    {
        Assert.Equal(expected, PathTools.RelativeTo(root, path));
    }

    [Fact]
    public void PathEquals_IsCaseInsensitive()
    {
        Assert.True(PathTools.PathEquals("E:\\資料\\ABC", "e:\\資料\\abc"));
        Assert.False(PathTools.PathEquals("E:\\a", "E:\\b"));
    }

    [Fact]
    public void NormalizeOrSelf_LeavesInvalidPathsAlone()
    {
        Assert.Equal("relative\\x", PathTools.NormalizeOrSelf("relative\\x"));
        Assert.Equal("C:\\a\\b", PathTools.NormalizeOrSelf("C:\\a\\.\\b\\"));
    }

    [Fact]
    public void ExtensionOf_IsLowerCase()
    {
        Assert.Equal(".pdf", PathTools.ExtensionOf("C:\\a\\B.PDF"));
        Assert.Equal(string.Empty, PathTools.ExtensionOf("C:\\a\\noext"));
    }
}
