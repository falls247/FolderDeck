using FolderDeck.Core.Paths;
using FolderDeck.Core.Transfer;

namespace FolderDeck.Core.Tests;

public class ShortcutNamesTests
{
    [Fact]
    public void FileName_IsExplorersName_ThenNumbered_WithoutCase()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "report.txt - ショートカット.LNK" };

        Assert.Equal("report.txt - ショートカット (2).lnk", ShortcutNames.FileName("report.txt", taken.Contains));
        taken.Add("report.txt - ショートカット (2).lnk");
        Assert.Equal("report.txt - ショートカット (3).lnk", ShortcutNames.FileName("report.txt", taken.Contains));
        Assert.Equal("写真 - ショートカット.lnk", ShortcutNames.FileName("写真", _ => false));
    }

    [Theory]
    [InlineData("ローカル ディスク (C:)", "ローカル ディスク (C)")] // a drive's name loses the colon
    [InlineData(@"C:\", "C")]
    [InlineData(":\\.", ShortcutNames.Fallback)]
    [InlineData("name. ", "name")]
    public void BaseName_DropsWhatAFileNameCannotHold(string itemName, string expected)
    {
        Assert.Equal(expected, ShortcutNames.BaseName(itemName));
    }

    [Fact]
    public void BaseName_LeavesRoomForTheSuffixAndNumber_AndTheStepIsUndoneToTheBin()
    {
        string name = ShortcutNames.FileName(new string('a', 400), _ => false);
        Assert.True(name.Length + " (99)".Length <= ItemNames.MaxLength);

        var step = FileOpStep.ShortcutsMade([@"E:\a\x - ショートカット.lnk", @"e:\A\x - ショートカット.lnk"]);
        Assert.Equal(FileOpKind.Shortcut, step.Kind);
        Assert.Equal("ショートカットの作成", step.Label);
        Assert.Single(step.Made);
    }
}
