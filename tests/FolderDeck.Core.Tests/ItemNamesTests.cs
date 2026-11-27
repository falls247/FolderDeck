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

    [Theory]
    [InlineData("photo.jpg", "a.JPG", false, "photo")]
    [InlineData("photo", "a.jpg", false, "photo")]
    [InlineData("photo.png", "a.jpg", false, "photo.png")]
    [InlineData("v1.2", "folder", true, "v1.2")]
    [InlineData("photo .jpg", "a.jpg", false, "photo")]
    public void BatchBaseName_DropsOnlyTheEditedFilesExtension(string name, string edited, bool isFolder, string expected)
    {
        Assert.Equal(expected, ItemNames.BatchBaseName(name, edited, isFolder));
    }

    [Fact]
    public void BatchNames_NumbersInOrder_KeepsEachExtension_SkipsTakenNumbers()
    {
        var names = ItemNames.BatchNames("photo", [("b.jpg", false), ("資料", true), ("c.png", false), (".gitignore", false)],
            ["PHOTO (2)", "other.txt"]);
        Assert.Equal(["photo (1).jpg", "photo (3)", "photo (4).png", "photo (5).gitignore"], names);
    }

    [Fact]
    public void BatchNames_AvoidsOtherSelectedNames_ButAnItemMayKeepItsOwn()
    {
        // "photo (1).jpg" keeps its name; x.jpg skips "photo (2).jpg", the name of another selected item,
        // which then gets the next free number (the count only goes up).
        var names = ItemNames.BatchNames("photo", [("photo (1).jpg", false), ("x.jpg", false), ("photo (2).jpg", false)], []);
        Assert.Equal(["photo (1).jpg", "photo (3).jpg", "photo (4).jpg"], names);
    }
}
