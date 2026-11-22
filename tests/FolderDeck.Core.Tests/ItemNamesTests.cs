using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Tests;

public class ItemNamesTests
{
    [Theory]
    [InlineData("  報告書.docx  ", "報告書.docx")]
    [InlineData("memo...", "memo")]
    [InlineData("memo . . ", "memo")]
    [InlineData("...", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    [InlineData(".gitignore", ".gitignore")]
    [InlineData("　名前", "　名前")]
    public void Clean_TrimsEndsAndTrailingDots(string? input, string expected)
    {
        Assert.Equal(expected, ItemNames.Clean(input));
    }

    [Theory]
    [InlineData("報告書.docx", NameProblem.None)]
    [InlineData("CONSOLE.txt", NameProblem.None)]
    [InlineData("", NameProblem.Empty)]
    [InlineData("a/b", NameProblem.InvalidCharacter)]
    [InlineData("a:b", NameProblem.InvalidCharacter)]
    [InlineData("what?", NameProblem.InvalidCharacter)]
    [InlineData("tab\there", NameProblem.InvalidCharacter)]
    [InlineData("con", NameProblem.ReservedName)]
    [InlineData("NUL.txt", NameProblem.ReservedName)]
    [InlineData("com1 .tar.gz", NameProblem.ReservedName)]
    [InlineData("LPT¹", NameProblem.ReservedName)]
    [InlineData("conout$", NameProblem.ReservedName)]
    [InlineData("COM0", NameProblem.None)]
    public void Check_RefusesWhatWindowsRefuses(string name, NameProblem expected)
    {
        Assert.Equal(expected, ItemNames.Check(name));
    }

    [Fact]
    public void Check_LongestNameIsMaxLength()
    {
        Assert.Equal(NameProblem.None, ItemNames.Check(new string('a', ItemNames.MaxLength)));
        Assert.Equal(NameProblem.TooLong, ItemNames.Check(new string('a', ItemNames.MaxLength + 1)));
    }

    [Theory]
    [InlineData("報告書.docx", false, 0, 3)]
    [InlineData("archive.tar.gz", false, 0, 11)]
    [InlineData(".gitignore", false, 0, 10)]
    [InlineData("README", false, 0, 6)]
    [InlineData("v1.2", true, 0, 4)]
    public void EditSelection_FileWithoutExtension_FolderWhole(string name, bool isFolder, int start, int length)
    {
        Assert.Equal((start, length), ItemNames.EditSelection(name, isFolder));
    }

    [Theory]
    [InlineData("a.txt", "b.txt", false)]
    [InlineData("a.txt", "a.TXT", false)]
    [InlineData("a.txt", "a.md", true)]
    [InlineData("a.txt", "a", true)]
    [InlineData("README", "README.md", true)]
    [InlineData(".gitignore", "x.gitignore", false)]
    [InlineData(".gitignore", "gitignore", true)]
    public void ExtensionChanged_IgnoresCase(string oldName, string newName, bool expected)
    {
        Assert.Equal(expected, ItemNames.ExtensionChanged(oldName, newName));
    }

    [Fact]
    public void NextNewFolderName_TakesFirstFreeNumber_WithoutCase()
    {
        Assert.Equal("新しいフォルダー", ItemNames.NextNewFolderName(["資料", "新しいフォルダー (2)"]));
        Assert.Equal("新しいフォルダー (3)", ItemNames.NextNewFolderName(["新しいフォルダー", "新しいフォルダー (2)", "新しいフォルダー (4)"]));
        Assert.Equal("New folder (2)", ItemNames.NextNewFolderName(["NEW FOLDER"], "New folder"));
    }
}
