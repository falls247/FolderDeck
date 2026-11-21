using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Tests;

public class CardOrderingTests
{
    private sealed record Item(string Name, bool Pinned = false, bool Folder = false, DateTime? Modified = null);

    private static readonly IComparer<Item> NameAsc = CardOrdering.ByName<Item>(i => i.Name, descending: false);
    private static readonly IComparer<Item> NameDesc = CardOrdering.ByName<Item>(i => i.Name, descending: true);

    private static DateTime T(int day) => new(2026, 11, day, 0, 0, 0, DateTimeKind.Utc);

    private static List<string> Names(IEnumerable<Item> items) => items.Select(i => i.Name).ToList();

    [Fact]
    public void SortKeepingPinned_PinnedKeepTheirSlots()
    {
        Item[] items = [new("A"), new("B", Pinned: true), new("C"), new("D", Pinned: true), new("E")];
        var sorted = CardOrdering.SortKeepingPinned(items, i => i.Pinned, NameDesc);
        Assert.Equal(["E", "B", "C", "D", "A"], Names(sorted));
    }

    [Fact]
    public void SortKeepingPinned_AllPinned_KeepsBaseOrder()
    {
        Item[] items = [new("c", true), new("a", true), new("b", true)];
        Assert.Equal(["c", "a", "b"], Names(CardOrdering.SortKeepingPinned(items, i => i.Pinned, NameAsc)));
    }

    [Fact]
    public void SortKeepingPinned_NonePinned_SortsEverything()
    {
        Item[] items = [new("c"), new("a"), new("b")];
        Assert.Equal(["a", "b", "c"], Names(CardOrdering.SortKeepingPinned(items, i => i.Pinned, NameAsc)));
        Assert.Empty(CardOrdering.SortKeepingPinned(Array.Empty<Item>(), i => i.Pinned, NameAsc));
    }

    [Fact]
    public void SortKeepingPinned_IsStable()
    {
        // Same time stamp: base order wins.
        Item[] items = [new("x1", Modified: T(2)), new("x2", Modified: T(1)), new("x3", Modified: T(2)), new("x4", Modified: T(2))];
        var byTime = CardOrdering.ByModified<Item>(i => i.Modified, descending: true);
        Assert.Equal(["x1", "x3", "x4", "x2"], Names(CardOrdering.SortKeepingPinned(items, i => i.Pinned, byTime)));
    }

    [Fact]
    public void SortKeepingPinned_FoldersFirst_InsideFolders()
    {
        // Default order inside a folder: folders first, natural order.
        Item[] items =
        [
            new("a", Folder: true), new("b", Pinned: true, Folder: true), new("c", Folder: true),
            new("x.txt"), new("y.txt", Pinned: true), new("z.txt"),
        ];
        var comparer = CardOrdering.FoldersFirst<Item>(i => i.Folder, NameDesc);
        var sorted = CardOrdering.SortKeepingPinned(items, i => i.Pinned, comparer);
        Assert.Equal(["c", "b", "a", "z.txt", "y.txt", "x.txt"], Names(sorted));
    }

    [Fact]
    public void ByName_IsNaturalAndCaseInsensitive_WithOrdinalTieBreak()
    {
        Item[] items = [new("file10"), new("File2"), new("file1"), new("b"), new("B")];
        Assert.Equal(["B", "b", "file1", "File2", "file10"], Names(items.OrderBy(i => i, NameAsc)));
        Assert.Equal(["file10", "File2", "file1", "b", "B"], Names(items.OrderBy(i => i, NameDesc)));
    }

    [Fact]
    public void ByModified_UnknownTimesGoLast_InBothDirections()
    {
        Item[] items = [new("none1"), new("old", Modified: T(1)), new("none2"), new("new", Modified: T(3)), new("mid", Modified: T(2))];
        var asc = CardOrdering.ByModified<Item>(i => i.Modified, descending: false);
        var desc = CardOrdering.ByModified<Item>(i => i.Modified, descending: true);
        Assert.Equal(["old", "mid", "new", "none1", "none2"], Names(items.OrderBy(i => i, asc)));
        Assert.Equal(["new", "mid", "old", "none1", "none2"], Names(items.OrderBy(i => i, desc)));
    }

    [Fact]
    public void ComparerFor_ManualIsNull_OthersSort()
    {
        Assert.Null(CardOrdering.ComparerFor<Item>(CardSortMode.Manual, i => i.Name, i => i.Modified));
        Item[] items = [new("b", Modified: T(1)), new("a", Modified: T(2))];
        Assert.Equal(["a", "b"], Names(items.OrderBy(i => i, CardOrdering.ComparerFor<Item>(CardSortMode.NameAsc, i => i.Name, i => i.Modified))));
        Assert.Equal(["b", "a"], Names(items.OrderBy(i => i, CardOrdering.ComparerFor<Item>(CardSortMode.NameDesc, i => i.Name, i => i.Modified))));
        Assert.Equal(["b", "a"], Names(items.OrderBy(i => i, CardOrdering.ComparerFor<Item>(CardSortMode.ModifiedAsc, i => i.Name, i => i.Modified))));
        Assert.Equal(["a", "b"], Names(items.OrderBy(i => i, CardOrdering.ComparerFor<Item>(CardSortMode.ModifiedDesc, i => i.Name, i => i.Modified))));
    }

    [Fact]
    public void KnownTime_TreatsUnsetAsUnknown()
    {
        Assert.Null(CardOrdering.KnownTime(default));
        Assert.Null(CardOrdering.KnownTime(DateTime.FromFileTimeUtc(0)));
        Assert.Equal(T(5), CardOrdering.KnownTime(T(5)));
    }

    [Theory]
    [InlineData(CardSortMode.Manual, "manual")]
    [InlineData(CardSortMode.NameAsc, "name_asc")]
    [InlineData(CardSortMode.NameDesc, "name_desc")]
    [InlineData(CardSortMode.ModifiedAsc, "modified_asc")]
    [InlineData(CardSortMode.ModifiedDesc, "modified_desc")]
    public void SortModes_RoundTripSettingValues(CardSortMode mode, string value)
    {
        Assert.Equal(value, CardSortModes.ToSetting(mode));
        Assert.Equal(mode, CardSortModes.FromSetting(value));
        Assert.Equal(mode, CardSortModes.FromSetting(" " + value.ToUpperInvariant() + " "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("random")]
    public void SortModes_UnknownIsManual(string? value)
    {
        Assert.Equal(CardSortMode.Manual, CardSortModes.FromSetting(value));
    }
}

public class ManualOrderTests
{
    private static List<string> Apply(string[] defaultOrder, string[] saved) => ManualOrder.Apply(defaultOrder, saved, s => s);

    [Fact]
    public void SavedOrderWins_NewItemsFollowTheirDefaultPredecessor()
    {
        Assert.Equal(["n1", "c", "a", "n2", "b"], Apply(["n1", "a", "n2", "b", "c"], ["c", "a", "b"]));
    }

    [Fact]
    public void EmptySaved_KeepsDefaultOrder()
    {
        Assert.Equal(["a", "b", "c"], Apply(["a", "b", "c"], []));
        Assert.Empty(Apply([], ["a"]));
    }

    [Fact]
    public void SavedItemsNoLongerPresent_AreIgnored()
    {
        Assert.Equal(["c", "a"], Apply(["a", "c"], ["gone", "c", "x", "a"]));
    }

    [Fact]
    public void ConsecutiveNewItems_KeepDefaultOrder()
    {
        Assert.Equal(["n1", "n2", "b", "a", "n3", "n4"], Apply(["n1", "n2", "a", "n3", "n4", "b"], ["b", "a"]));
    }

    [Fact]
    public void DuplicateSavedKeys_FirstOccurrenceCounts()
    {
        Assert.Equal(["b", "a", "c"], Apply(["a", "b", "c"], ["b", "a", "b", "c", "a"]));
    }

    [Fact]
    public void KeysAreCaseInsensitive()
    {
        var order = ManualOrder.Apply<string>([@"C:\Data\a.txt", @"C:\Data\B"], [@"c:\data\b", @"C:\DATA\A.TXT"], s => s);
        Assert.Equal([@"C:\Data\B", @"C:\Data\a.txt"], order);
    }
}

public class PathSetTests
{
    [Fact]
    public void Contains_IgnoresCase_AndNormalizes()
    {
        var set = new PathSet([@"C:\Data\Pinned", @"C:\Data\file.txt\", ""]);
        Assert.Equal(2, set.Count);
        Assert.True(set.Contains(@"c:\data\pinned"));
        Assert.True(set.Contains(@"C:\Data\Pinned\"));
        Assert.True(set.Contains(@"C:\Data\sub\..\file.txt"));
        Assert.False(set.Contains(@"C:\Data\Other"));
        Assert.False(set.Contains(null));
        Assert.False(new PathSet([]).Contains(@"C:\Data\Pinned"));
    }
}

public class ExistenceProbeTests
{
    [Fact]
    public async Task ProbeAsync_ReportsKindAndTime()
    {
        var fs = new FakeFileSystem().AddFile(@"C:\r\a.txt");
        var folder = await Existence.ProbeAsync(fs, @"C:\r", TimeSpan.FromSeconds(2), CancellationToken.None);
        var file = await Existence.ProbeAsync(fs, @"C:\r\a.txt", TimeSpan.FromSeconds(2), CancellationToken.None);
        var missing = await Existence.ProbeAsync(fs, @"C:\nope", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(ExistState.Folder, folder.State);
        Assert.NotNull(folder.LastWriteTimeUtc);
        Assert.Equal(ExistState.File, file.State);
        Assert.NotNull(file.LastWriteTimeUtc);
        Assert.Equal(new ExistProbe(ExistState.Missing, null), missing);
    }

    [Fact]
    public async Task ProbeAsync_FallsBackToKind_WhenEntryIsUnavailable()
    {
        var fs = new KindOnlyFileSystem(null);
        var probe = await Existence.ProbeAsync(fs, @"C:\locked", TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(new ExistProbe(ExistState.Folder, null), probe);
    }

    [Fact]
    public async Task ProbeAsync_TimesOut()
    {
        using var release = new ManualResetEventSlim();
        var fs = new KindOnlyFileSystem(release);
        try
        {
            var probe = await Existence.ProbeAsync(fs, @"\\server\share", TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.Equal(new ExistProbe(ExistState.Timeout, null), probe);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task FilterProbedAsync_KeepsOrderAndProbes_DropsMissing()
    {
        var items = Enumerable.Range(1, 30).Select(i => $"p{i}").ToList();
        Task<ExistProbe> Probe(string p, CancellationToken _)
        {
            int n = int.Parse(p[1..]);
            return Task.FromResult(n % 3 == 0 ? new ExistProbe(ExistState.Missing, null) : new ExistProbe(ExistState.File, new DateTime(2026, 1, n, 0, 0, 0, DateTimeKind.Utc)));
        }

        var kept = await Existence.FilterProbedAsync(items, s => s, 5, Probe, CancellationToken.None, parallelism: 4);
        Assert.Equal(["p1", "p2", "p4", "p5", "p7"], kept.Select(k => k.Item));
        Assert.Equal(new DateTime(2026, 1, 7, 0, 0, 0, DateTimeKind.Utc), kept[^1].Probe.LastWriteTimeUtc);
    }

    /// <summary>GetEntry always fails; GetKind reports a folder (optionally after waiting).</summary>
    private sealed class KindOnlyFileSystem(ManualResetEventSlim? release) : IFileSystem
    {
        public IReadOnlyList<FsEntry> EnumerateDirectory(string path, bool includeHidden, CancellationToken cancellationToken) => [];

        public PathKind GetKind(string path)
        {
            release?.Wait(TimeSpan.FromSeconds(10));
            return PathKind.Folder;
        }

        public FsEntry? GetEntry(string path) => null;
    }
}
