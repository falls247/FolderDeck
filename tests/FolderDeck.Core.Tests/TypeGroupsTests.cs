using FolderDeck.Core.Selection;

namespace FolderDeck.Core.Tests;

public class TypeGroupsTests
{
    [Fact]
    public void Of_KeepsJpgAndJpegApart_IgnoresCase_FoldersFirstAndNoExtensionLast()
    {
        var groups = TypeGroups.Of(
        [
            ("b.JPG", false), ("a.jpg", false), ("c.jpeg", false), ("README", false), ("photos", true),
            ("x.png", false), ("photos.old", true),
        ]);

        Assert.Equal(
            ["folder:2", ".jpeg:1", ".jpg:2", ".png:1", ":1"],
            groups.Select(g => $"{g.Key}:{g.Count}").ToArray());
        Assert.True(groups[0].IsFolder);
        Assert.True(groups[^1].IsNoExtension);
    }

    [Theory]
    [InlineData(".gitignore", ".gitignore")] // as Explorer: the dot name is the extension
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("ends with dot.", "")]
    [InlineData("Makefile", "")]
    public void KeyOf_TakesTheLastExtension(string name, string expected)
    {
        Assert.Equal(expected, TypeGroups.KeyOf(name, isFolder: false));
    }
}
