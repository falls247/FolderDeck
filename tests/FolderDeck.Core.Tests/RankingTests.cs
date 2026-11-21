using FolderDeck.Core.Ranking;
using FolderDeck.Core.Stores;
using FolderDeck.Core.Text;

namespace FolderDeck.Core.Tests;

public class RecentMergerTests
{
    private static readonly DateTimeOffset T0 = FakeClock.Default;

    private static HistoryItem H(string path, DateTimeOffset last, ItemKind kind = ItemKind.File) =>
        new(path, kind, 1, last, [last]);

    [Fact]
    public void Duplicates_AreMerged_NewestTimeWins()
    {
        var merged = RecentMerger.Merge(
            [new WindowsRecentItem(@"C:\Docs\A.txt", T0.AddHours(-1)), new WindowsRecentItem(@"C:\Docs\B.txt", T0.AddHours(-5))],
            [H(@"c:\docs\a.TXT", T0), H(@"C:\Docs\B.txt", T0.AddHours(-10))],
            [],
            null);

        Assert.Equal(2, merged.Count);
        var a = merged[0];
        Assert.Equal(T0, a.LastUsed);
        Assert.Equal(RecentOrigin.App | RecentOrigin.Windows, a.Origin);
        Assert.Equal(ItemKind.File, a.Kind);
        Assert.Equal(T0.AddHours(-5), merged[1].LastUsed);
    }

    [Fact]
    public void SortedNewestFirst_ThenNaturalPath()
    {
        var merged = RecentMerger.Merge(
            [new WindowsRecentItem(@"C:\10.txt", T0), new WindowsRecentItem(@"C:\2.txt", T0), new WindowsRecentItem(@"C:\old.txt", T0.AddDays(-1))],
            [H(@"C:\new.txt", T0.AddMinutes(1))],
            [],
            null);
        Assert.Equal([@"C:\new.txt", @"C:\2.txt", @"C:\10.txt", @"C:\old.txt"], merged.Select(m => m.Path));
    }

    [Fact]
    public void Excluded_IsHidden_UntilUsedAgain()
    {
        var excluded = new[] { new ExcludedItem(@"C:\a.zip", T0) };

        var hidden = RecentMerger.Merge([new WindowsRecentItem(@"C:\A.zip", T0.AddMinutes(-1))], [], excluded, null);
        Assert.Empty(hidden);

        var sameTime = RecentMerger.Merge([], [H(@"C:\a.zip", T0)], excluded, null);
        Assert.Empty(sameTime);

        var usedAgain = RecentMerger.Merge([], [H(@"c:\a.ZIP", T0.AddSeconds(1))], excluded, null);
        Assert.Single(usedAgain);

        var windowsUsedAgain = RecentMerger.Merge([new WindowsRecentItem(@"C:\a.zip", T0.AddMinutes(3))], [H(@"C:\a.zip", T0.AddMinutes(-3))], excluded, null);
        Assert.Equal(T0.AddMinutes(3), Assert.Single(windowsUsedAgain).LastUsed);
    }

    [Fact]
    public void ClearedAt_HidesEverythingUsedBefore()
    {
        var merged = RecentMerger.Merge(
            [new WindowsRecentItem(@"C:\before.txt", T0.AddMinutes(-1)), new WindowsRecentItem(@"C:\at.txt", T0)],
            [H(@"C:\after.txt", T0.AddMinutes(1))],
            [],
            T0);
        Assert.Equal([@"C:\after.txt"], merged.Select(m => m.Path));
    }

    [Fact]
    public void NonFileSystemPaths_AreIgnored()
    {
        var merged = RecentMerger.Merge(
            [new WindowsRecentItem("", T0), new WindowsRecentItem("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", T0), new WindowsRecentItem("relative\\x.txt", T0), new WindowsRecentItem(@"\\server\share\x.txt", T0)],
            [],
            [],
            null);
        Assert.Equal([@"\\server\share\x.txt"], merged.Select(m => m.Path));
        Assert.Null(merged[0].Kind);
        Assert.Equal(RecentOrigin.Windows, merged[0].Origin);
    }

    [Fact]
    public void Paths_AreNormalized()
    {
        var merged = RecentMerger.Merge([new WindowsRecentItem(@"C:\Docs\.\x\..\A\", T0)], [H(@"C:\Docs\A", T0.AddMinutes(-1), ItemKind.Folder)], [], null);
        var only = Assert.Single(merged);
        Assert.Equal(@"C:\Docs\A", only.Path);
        Assert.Equal(ItemKind.Folder, only.Kind);
    }
}

public class FrequentRankerTests
{
    private static readonly DateTimeOffset Now = FakeClock.Default;

    [Theory]
    [InlineData(0, 100)]
    [InlineData(4, 100)]
    [InlineData(4.0001, 70)]
    [InlineData(14, 70)]
    [InlineData(14.0001, 50)]
    [InlineData(31, 50)]
    [InlineData(31.0001, 30)]
    [InlineData(90, 30)]
    [InlineData(90.0001, 10)]
    [InlineData(1000, 10)]
    [InlineData(-1, 100)]
    public void Weight_Boundaries(double days, double expected)
    {
        Assert.Equal(expected, FrequentRanker.Weight(days));
    }

    [Fact]
    public void Score_ExactBoundaryTimestamps()
    {
        var item = new HistoryItem(@"C:\a", ItemKind.File, 5, Now, [Now.AddDays(-4), Now.AddDays(-14), Now.AddDays(-31), Now.AddDays(-90), Now.AddDays(-91)]);
        // (5 / 5) × (100 + 70 + 50 + 30 + 10)
        Assert.Equal(260, FrequentRanker.Score(item, Now));
    }

    [Fact]
    public void Score_UsesOpenCountOverRecentLength()
    {
        var item = new HistoryItem(@"C:\a", ItemKind.File, 12, Now, [Now.AddDays(-1), Now.AddDays(-10)]);
        Assert.Equal(12.0 / 2 * (100 + 70), FrequentRanker.Score(item, Now));
    }

    [Fact]
    public void Score_EmptyRecentOpens_IsZero()
    {
        var item = new HistoryItem(@"C:\a", ItemKind.File, 7, Now, []);
        Assert.Equal(0, FrequentRanker.Score(item, Now));
    }

    [Fact]
    public void Rank_ByScore_ThenLastOpened_ThenNaturalPath()
    {
        var history = new[]
        {
            new HistoryItem(@"C:\low.txt", ItemKind.File, 1, Now.AddDays(-100), [Now.AddDays(-100)]),        // 10
            new HistoryItem(@"C:\10.txt", ItemKind.File, 1, Now.AddDays(-2), [Now.AddDays(-2)]),           // 100
            new HistoryItem(@"C:\2.txt", ItemKind.File, 1, Now.AddDays(-2), [Now.AddDays(-2)]),            // 100
            new HistoryItem(@"C:\newer.txt", ItemKind.File, 1, Now.AddDays(-1), [Now.AddDays(-3)]),        // 100, newer last_opened
            new HistoryItem(@"C:\top", ItemKind.Folder, 4, Now, [Now, Now.AddDays(-20)]),                    // 2 × 150 = 300
        };

        var ranking = FrequentRanker.Rank(history, [], Now);

        Assert.Empty(ranking.Pinned);
        Assert.Equal([@"C:\top", @"C:\newer.txt", @"C:\2.txt", @"C:\10.txt", @"C:\low.txt"], ranking.Ranked.Select(e => e.Path));
        Assert.Equal(300, ranking.Ranked[0].Score);
        Assert.Equal(ItemKind.Folder, ranking.Ranked[0].Kind);
        Assert.All(ranking.Ranked, e => Assert.False(e.IsPinned));
    }

    [Fact]
    public void Rank_PinsFirstInPinningOrder_AndNotRepeatedInRanking()
    {
        var history = new[]
        {
            new HistoryItem(@"C:\a.txt", ItemKind.File, 10, Now, [Now]),
            new HistoryItem(@"C:\b.txt", ItemKind.File, 1, Now, [Now]),
        };
        var pins = new[]
        {
            new PinItem(@"C:\never-opened", ItemKind.Folder, Now.AddDays(-1)),
            new PinItem(@"c:\A.TXT", ItemKind.File, Now.AddDays(-5)),
        };

        var ranking = FrequentRanker.Rank(history, pins, Now);

        Assert.Equal([@"c:\A.TXT", @"C:\never-opened"], ranking.Pinned.Select(p => p.Path));
        Assert.All(ranking.Pinned, p => Assert.True(p.IsPinned));
        Assert.Equal(1000, ranking.Pinned[0].Score);
        Assert.Null(ranking.Pinned[1].LastUsed);
        Assert.Equal(ItemKind.Folder, ranking.Pinned[1].Kind);
        Assert.Equal([@"C:\b.txt"], ranking.Ranked.Select(e => e.Path));
    }

    [Fact]
    public void Rank_IsDeterministicForFixedClock()
    {
        var history = Enumerable.Range(0, 30)
            .Select(i => new HistoryItem($@"C:\f{i}.txt", ItemKind.File, i + 1, Now.AddDays(-i), [Now.AddDays(-i * 3)]))
            .ToArray();
        var a = FrequentRanker.Rank(history, [], Now).Ranked.Select(e => e.Path).ToList();
        var b = FrequentRanker.Rank(history.Reverse(), [], Now).Ranked.Select(e => e.Path).ToList();
        Assert.Equal(a, b);
    }
}

public class FormattingTests
{
    private static readonly DateTimeOffset Now = FakeClock.Default;

    [Theory]
    [InlineData(0, "たった今")]
    [InlineData(59, "たった今")]
    [InlineData(60, "1分前")]
    [InlineData(59 * 60, "59分前")]
    [InlineData(3 * 3600, "3時間前")]
    [InlineData(23 * 3600 + 3599, "23時間前")]
    [InlineData(24 * 3600, "昨日")]
    [InlineData(47 * 3600, "昨日")]
    [InlineData(48 * 3600, "2日前")]
    [InlineData(6 * 86400 + 3600, "6日前")]
    public void RelativeTime(int secondsAgo, string expected)
    {
        Assert.Equal(expected, Formatting.RelativeTime(Now.AddSeconds(-secondsAgo), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void RelativeTime_SevenDaysOrOlder_IsDate()
    {
        Assert.Equal("2026/11/14", Formatting.RelativeTime(Now.AddDays(-7), Now, TimeZoneInfo.Utc));
        Assert.Equal("2025/01/02", Formatting.RelativeTime(new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero), Now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void DateTimeText()
    {
        Assert.Equal("2026/11/21 01:23", Formatting.DateTimeText(Now, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1258291, "1.2 MB")]
    [InlineData(157286400, "150 MB")]
    [InlineData(5368709120, "5.0 GB")]
    public void FileSize(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.FileSize(bytes));
    }
}
