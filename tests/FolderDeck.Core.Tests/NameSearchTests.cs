using FolderDeck.Core.Ordering;

namespace FolderDeck.Core.Tests;

public class NameSearchTests
{
    private static readonly string[] Names = ["README.md", "会議メモ_2026.docx", "かいぎ資料.pptx", "build.ps1", "Readme_old.txt"];

    [Theory]
    [InlineData("readme", new[] { "README.md", "Readme_old.txt" })] // case
    [InlineData("ｒｅａｄｍｅ", new[] { "README.md", "Readme_old.txt" })] // full-width letters
    [InlineData("カイギ", new[] { "かいぎ資料.pptx" })] // katakana finds hiragana
    [InlineData("readme old", new[] { "Readme_old.txt" })] // every word, any order
    [InlineData("old　readme", new[] { "Readme_old.txt" })] // full-width space separates words too
    [InlineData("nothing", new string[0])]
    public void Apply_FindsNamesHoldingEveryWord(string query, string[] expected)
    {
        Assert.Equal(expected, NameSearch.Apply(Names, query, n => n));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  　 ")]
    public void Apply_EmptyQuery_ReturnsTheListItself(string? query)
    {
        Assert.Same(Names, NameSearch.Apply(Names, query, n => n));
    }
}
