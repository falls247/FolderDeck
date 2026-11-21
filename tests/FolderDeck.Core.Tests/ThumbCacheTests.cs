using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;

namespace FolderDeck.Core.Tests;

public class ThumbSizingTests
{
    [Theory]
    [InlineData(6000, 4000, 256, 384, 256)] // landscape: the shorter side (height) becomes the size
    [InlineData(3000, 4000, 512, 512, 683)] // portrait: the shorter side (width) becomes the size
    [InlineData(100, 60, 256, 100, 60)] // smaller than the size: never scaled up
    [InlineData(4000, 300, 256, 1024, 77)] // panorama: the longer side is capped at 4 x size
    [InlineData(1, 5000, 256, 1, 1024)] // never below one pixel
    public void Fit_CoversASquareOfTheSize_WithoutScalingUp(int width, int height, int size, int expectedWidth, int expectedHeight)
    {
        Assert.Equal((expectedWidth, expectedHeight), ThumbSizing.Fit(width, height, size));
    }
}

public class DiskCacheRulesTests
{
    [Fact]
    public void RelativePathOf_IsStable_DistinctPerKey_AndRecognizedAsACacheFile()
    {
        string a = DiskCacheRules.RelativePathOf(@"1|C:\PICS\A.JPG|638000000000000000|12345|256");
        string again = DiskCacheRules.RelativePathOf(@"1|C:\PICS\A.JPG|638000000000000000|12345|256");
        string otherSize = DiskCacheRules.RelativePathOf(@"1|C:\PICS\A.JPG|638000000000000000|12345|512");

        Assert.Equal(a, again);
        Assert.NotEqual(a, otherSize);
        string name = Path.GetFileName(a);
        Assert.Equal(name[..2], Path.GetDirectoryName(a));
        Assert.True(DiskCacheRules.IsCacheFileName(name));
    }

    [Theory]
    [InlineData("photo.jpg")]
    [InlineData("0123456789abcdef0123456789abcdef.tmp")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")] // upper case: not a name the cache makes
    [InlineData("0123456789abcdef")]
    public void IsCacheFileName_RejectsEverythingTheCacheDoesNotName(string name)
    {
        Assert.False(DiskCacheRules.IsCacheFileName(name));
    }

    [Fact]
    public void SelectForRemoval_KeepsEverythingWithinTheLimit()
    {
        var files = new[] { File("a", 40, 1), File("b", 60, 2) };
        Assert.Empty(DiskCacheRules.SelectForRemoval(files, 100));
    }

    [Fact]
    public void SelectForRemoval_DropsTheLeastRecentlyUsed_DownToTheKeepRatio()
    {
        // 150 bytes against a limit of 100: trim to 80 or less, oldest first.
        var files = new[] { File("newest", 50, 30), File("oldest", 30, 10), File("middle", 40, 20), File("recent", 30, 25) };

        var removed = DiskCacheRules.SelectForRemoval(files, 100);

        Assert.Equal(["oldest", "middle"], removed.Select(f => f.Path));
    }

    private static CacheFile File(string path, long bytes, int day) =>
        new(path, bytes, new DateTime(2026, 1, day, 0, 0, 0, DateTimeKind.Utc));
}
