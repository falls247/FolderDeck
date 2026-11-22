using FolderDeck.Core.Drives;

namespace FolderDeck.Core.Tests;

public class DriveRulesTests
{
    [Theory]
    [InlineData(@"C:\", DriveKind.Fixed, "Windows", null, "Windows (C:)")]
    [InlineData(@"c:\", DriveKind.Fixed, "", null, "ローカル ディスク (C:)")]
    [InlineData(@"E:\", DriveKind.Removable, null, null, "リムーバブル ディスク (E:)")]
    [InlineData(@"D:\", DriveKind.Optical, "  ", null, "DVD ドライブ (D:)")]
    [InlineData(@"Z:\", DriveKind.Network, "ignored", @"\\server\share", @"share (\\server) (Z:)")]
    [InlineData(@"Y:\", DriveKind.Network, null, @"\\server\share\sub\", @"sub (\\server\share) (Y:)")]
    [InlineData(@"X:\", DriveKind.Network, null, null, "ネットワーク ドライブ (X:)")]
    public void DisplayName_IsLabelThenLetter_WithKindNameForEmptyLabel_AndShareForNetwork(
        string root, DriveKind kind, string? label, string? remote, string expected)
    {
        Assert.Equal(expected, DriveRules.DisplayName(new DriveEntry(root, kind, label, remote, true)));
    }

    [Theory]
    [InlineData(DriveKind.Fixed, null, true)]
    [InlineData(DriveKind.Fixed, false, true)]
    [InlineData(DriveKind.Network, false, true)]
    [InlineData(DriveKind.Removable, true, true)]
    [InlineData(DriveKind.Removable, false, false)]
    [InlineData(DriveKind.Removable, null, false)]
    [InlineData(DriveKind.Optical, true, true)]
    [InlineData(DriveKind.Optical, null, false)]
    public void ShouldShow_HidesRemovableAndOpticalUntilReady(DriveKind kind, bool? isReady, bool expected)
    {
        Assert.Equal(expected, DriveRules.ShouldShow(kind, isReady));
    }

    [Fact]
    public void Order_SortsByLetter_AndKeepsTheFirstEntryOfARoot()
    {
        var z = new DriveEntry(@"Z:\", DriveKind.Network, null, null, true);
        var c = new DriveEntry(@"C:\", DriveKind.Fixed, "Windows", null, true);
        var zRemembered = new DriveEntry("z:", DriveKind.Network, null, @"\\server\share", false);
        var e = new DriveEntry(@"e:\", DriveKind.Removable, null, null, null);

        var ordered = DriveRules.Order(new[] { z, c, zRemembered, e });

        Assert.Equal(new[] { c, e, z }, ordered.ToArray());
    }
}
