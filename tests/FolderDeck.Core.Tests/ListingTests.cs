using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Paths;

namespace FolderDeck.Core.Tests;

public class NaturalComparerTests
{
    [Fact]
    public void NumbersCompareAsNumbers()
    {
        var names = new List<string> { "10.txt", "2.txt", "1.txt", "file_10000.txt", "file_9.txt", "file_100.txt" };
        names.Sort(NaturalComparer.Instance);
        Assert.Equal(["1.txt", "2.txt", "10.txt", "file_9.txt", "file_100.txt", "file_10000.txt"], names);
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        Assert.Equal(0, NaturalComparer.Instance.Compare("abc", "ABC"));
        Assert.True(NaturalComparer.Instance.Compare("a", "B") < 0);
    }

    [Fact]
    public void HandlesNulls()
    {
        Assert.Equal(0, NaturalComparer.Instance.Compare(null, null));
        Assert.True(NaturalComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalComparer.Instance.Compare("a", null) > 0);
    }
}

public class FolderListerTests
{
    [Fact]
    public void RealFileSystem_FoldersFirst_NaturalOrder_SkipsHiddenSystemAndDotNames()
    {
        using var tmp = new TempDir();
        tmp.MakeFile("10.txt");
        tmp.MakeFile("2.txt");
        tmp.MakeFile("B.pdf");
        tmp.MakeFile(".dotfile");
        tmp.MakeFile("hidden.txt", attributes: FileAttributes.Hidden);
        tmp.MakeFile("system.txt", attributes: FileAttributes.System);
        tmp.MakeDir("sub10");
        tmp.MakeDir("sub2");
        tmp.MakeDir(".dotdir");
        string hiddenDir = tmp.MakeDir("hiddendir");
        File.SetAttributes(hiddenDir, File.GetAttributes(hiddenDir) | FileAttributes.Hidden);

        var listing = FolderLister.List(RealFileSystem.Instance, tmp.Path, includeHidden: false, CancellationToken.None);

        Assert.Equal(ListingStatus.Ok, listing.Status);
        Assert.Equal(["sub2", "sub10", "2.txt", "10.txt", "B.pdf"], listing.Entries.Select(e => e.Name));
        Assert.Equal(2, listing.FolderCount);
        Assert.Equal(3, listing.FileCount);
        Assert.True(listing.Entries[0].IsDirectory);
        Assert.Equal(Path.Combine(tmp.Path, "sub2"), listing.Entries[0].FullPath);
    }

    [Fact]
    public void RealFileSystem_IncludeHidden_ShowsHiddenAndSystemButNeverDotNames()
    {
        using var tmp = new TempDir();
        tmp.MakeFile("a.txt");
        tmp.MakeFile(".dotfile");
        tmp.MakeFile("hidden.txt", attributes: FileAttributes.Hidden);
        tmp.MakeFile("system.txt", attributes: FileAttributes.System);
        string hiddenDir = tmp.MakeDir("hiddendir");
        File.SetAttributes(hiddenDir, File.GetAttributes(hiddenDir) | FileAttributes.Hidden);

        var listing = FolderLister.List(RealFileSystem.Instance, tmp.Path, includeHidden: true, CancellationToken.None);

        Assert.Equal(["hiddendir", "a.txt", "hidden.txt", "system.txt"], listing.Entries.Select(e => e.Name));
        Assert.Equal(1, listing.FolderCount);
        Assert.Equal(3, listing.FileCount);
    }

    [Fact]
    public void RealFileSystem_FileSizesAreReported()
    {
        using var tmp = new TempDir();
        tmp.MakeFile("five.txt", "12345");
        var listing = FolderLister.List(RealFileSystem.Instance, tmp.Path, false, CancellationToken.None);
        Assert.Equal(5, listing.Entries.Single().Length);
    }

    [Fact]
    public void MissingFolder_IsNotFound()
    {
        using var tmp = new TempDir();
        var listing = FolderLister.List(RealFileSystem.Instance, tmp.Combine("nope"), false, CancellationToken.None);
        Assert.Equal(ListingStatus.NotFound, listing.Status);
        Assert.Equal("見つかりません", listing.StatusMessage);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void FileInsteadOfFolder_IsNotOk()
    {
        using var tmp = new TempDir();
        string file = tmp.MakeFile("a.txt");
        var listing = FolderLister.List(RealFileSystem.Instance, file, false, CancellationToken.None);
        Assert.NotEqual(ListingStatus.Ok, listing.Status);
    }

    [Fact]
    public void EmptyFolder_IsOkAndEmpty()
    {
        using var tmp = new TempDir();
        var listing = FolderLister.List(RealFileSystem.Instance, tmp.Path, false, CancellationToken.None);
        Assert.True(listing.IsOk);
        Assert.Empty(listing.Entries);
        Assert.Equal(0, listing.FolderCount);
        Assert.Equal(0, listing.FileCount);
    }

    [Fact]
    public void Cancellation_Throws()
    {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => FolderLister.List(RealFileSystem.Instance, tmp.Path, false, cts.Token));
    }

    [Fact]
    public void StatusMessages()
    {
        Assert.Equal("アクセスが拒否されました", FolderListing.Failed("x", ListingStatus.AccessDenied).StatusMessage);
        Assert.Equal("読み込めません：boom", FolderListing.Failed("x", ListingStatus.Error, "boom").StatusMessage);
    }

    [Fact]
    public void RealFileSystem_GetKind()
    {
        using var tmp = new TempDir();
        string file = tmp.MakeFile("a.txt");
        Assert.Equal(PathKind.Folder, RealFileSystem.Instance.GetKind(tmp.Path));
        Assert.Equal(PathKind.File, RealFileSystem.Instance.GetKind(file));
        Assert.Equal(PathKind.Missing, RealFileSystem.Instance.GetKind(tmp.Combine("missing")));
    }

    [Fact]
    public void FakeFileSystem_SortedByLister()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\r\10.txt")
            .AddFile(@"C:\r\2.txt")
            .AddFolder(@"C:\r\z")
            .AddFolder(@"C:\r\a");
        var listing = FolderLister.List(fs, @"C:\r", false, CancellationToken.None);
        Assert.Equal(["a", "z", "2.txt", "10.txt"], listing.Entries.Select(e => e.Name));
    }
}

public class CoverFinderTests
{
    [Theory]
    [InlineData("a.JPG", true)]
    [InlineData("a.jpeg", true)]
    [InlineData("a.png", true)]
    [InlineData("a.gif", true)]
    [InlineData("a.webp", true)]
    [InlineData("a.Bmp", true)]
    [InlineData("a.tiff", false)]
    [InlineData("a.txt", false)]
    [InlineData("png", false)]
    public void IsImageName(string name, bool expected)
    {
        Assert.Equal(expected, CoverFinder.IsImageName(name));
    }

    [Fact]
    public void DirectImage_FirstInNaturalOrder()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\r\10.png")
            .AddFile(@"C:\r\2.png")
            .AddFile(@"C:\r\1.txt")
            .AddFile(@"C:\r\sub\0.png");
        Assert.Equal(@"C:\r\2.png", CoverFinder.Find(fs, @"C:\r", null, false, CancellationToken.None));
    }

    [Fact]
    public void DepthFirst_FirstSubfolderBranchWins()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\r\note.txt")
            .AddFile(@"C:\r\a\x\deep.png")
            .AddFile(@"C:\r\b\shallow.png");
        Assert.Equal(@"C:\r\a\x\deep.png", CoverFinder.Find(fs, @"C:\r", null, false, CancellationToken.None));
    }

    [Fact]
    public void NoImage_ReturnsNull()
    {
        var fs = new FakeFileSystem()
            .AddFile(@"C:\r\a.txt")
            .AddFile(@"C:\r\s\b.pdf");
        Assert.Null(CoverFinder.Find(fs, @"C:\r", null, false, CancellationToken.None));
    }

    [Fact]
    public void Depth6_IsFound_Depth7_IsNot()
    {
        var found = new FakeFileSystem().AddFile(@"C:\r\1\2\3\4\5\6\img.png");
        Assert.Equal(@"C:\r\1\2\3\4\5\6\img.png", CoverFinder.Find(found, @"C:\r", null, false, CancellationToken.None));

        var tooDeep = new FakeFileSystem().AddFile(@"C:\r\1\2\3\4\5\6\7\img.png");
        Assert.Null(CoverFinder.Find(tooDeep, @"C:\r", null, false, CancellationToken.None));
        Assert.DoesNotContain(@"C:\r\1\2\3\4\5\6\7", tooDeep.Enumerated, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void FolderBudget_StopsAfter64Folders()
    {
        // Start folder counts as 1, so 63 sub folders can be listed.
        var within = new FakeFileSystem();
        for (int i = 1; i <= 62; i++)
        {
            within.AddFolder($@"C:\r\f{i}");
        }

        within.AddFile(@"C:\r\f63\img.png");
        Assert.Equal(@"C:\r\f63\img.png", CoverFinder.Find(within, @"C:\r", null, false, CancellationToken.None));
        Assert.Equal(64, within.EnumerateCalls);

        var beyond = new FakeFileSystem();
        for (int i = 1; i <= 63; i++)
        {
            beyond.AddFolder($@"C:\r\f{i}");
        }

        beyond.AddFile(@"C:\r\f64\img.png");
        Assert.Null(CoverFinder.Find(beyond, @"C:\r", null, false, CancellationToken.None));
        Assert.Equal(CoverFinder.MaxFolders, beyond.EnumerateCalls);
    }

    [Fact]
    public void FolderBudget_CountsNestedFolders()
    {
        var fs = new FakeFileSystem();
        for (int i = 1; i <= 70; i++)
        {
            fs.AddFolder($@"C:\r\a\n{i}");
        }

        fs.AddFile(@"C:\r\b\img.png");
        Assert.Null(CoverFinder.Find(fs, @"C:\r", null, false, CancellationToken.None));
        Assert.True(fs.EnumerateCalls <= CoverFinder.MaxFolders);
    }

    [Fact]
    public void ReparsePoints_AreNotFollowed()
    {
        var fs = new FakeFileSystem()
            .AddFolder(@"C:\r\loop", reparse: true)
            .AddFile(@"C:\r\loop\img.png")
            .AddFile(@"C:\r\real\other.png");
        Assert.Equal(@"C:\r\real\other.png", CoverFinder.Find(fs, @"C:\r", null, false, CancellationToken.None));
        Assert.DoesNotContain(@"C:\r\loop", fs.Enumerated, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void HiddenFolders_FollowTheFilter()
    {
        var fs = new FakeFileSystem()
            .AddFolder(@"C:\r\h", extra: FileAttributes.Hidden)
            .AddFile(@"C:\r\h\img.png");
        Assert.Null(CoverFinder.Find(fs, @"C:\r", null, false, CancellationToken.None));
        Assert.Equal(@"C:\r\h\img.png", CoverFinder.Find(fs, @"C:\r", null, true, CancellationToken.None));
    }

    [Fact]
    public void RootEntries_AreReused()
    {
        var fs = new FakeFileSystem().AddFile(@"C:\r\a.png");
        var entries = FolderLister.List(fs, @"C:\r", false, CancellationToken.None).Entries;
        int before = fs.EnumerateCalls;
        Assert.Equal(@"C:\r\a.png", CoverFinder.Find(fs, @"C:\r", entries, false, CancellationToken.None));
        Assert.Equal(before, fs.EnumerateCalls);
    }

    [Fact]
    public void RealFileSystem_JunctionLoop_DoesNotHang()
    {
        using var tmp = new TempDir();
        string root = tmp.MakeDir("root");
        string loop = Path.Combine(root, "loop");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{loop}\" \"{root}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            p.WaitForExit(10000);
        }

        if (!Directory.Exists(loop))
        {
            return; // junctions unavailable on this volume
        }

        try
        {
            var listing = FolderLister.List(RealFileSystem.Instance, root, false, CancellationToken.None);
            Assert.True(listing.Entries.Single().IsReparsePoint);
            Assert.True(listing.Entries.Single().IsDirectory);
            Assert.Null(CoverFinder.Find(RealFileSystem.Instance, root, null, false, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(loop); // removes the junction only
        }
    }
}

public class FolderListingCacheTests
{
    [Fact]
    public async Task CachesWithinTtl_ReloadsAfter()
    {
        long ticks = 1_000;
        var fs = new FakeFileSystem().AddFile(@"C:\r\a.txt");
        var cache = new FolderListingCache(fs, () => false, tickSource: () => ticks);

        var first = await cache.GetAsync(@"C:\r", CancellationToken.None);
        var second = await cache.GetAsync(@"c:\R", CancellationToken.None);
        Assert.Same(first, second);
        Assert.Equal(1, fs.EnumerateCalls);
        Assert.True(cache.TryGetFresh(@"C:\r", out _));

        ticks += 60_000;
        Assert.True(cache.TryGetFresh(@"C:\r", out _));
        ticks += 1;
        Assert.False(cache.TryGetFresh(@"C:\r", out _));

        var third = await cache.GetAsync(@"C:\r", CancellationToken.None);
        Assert.NotSame(first, third);
        Assert.Equal(2, fs.EnumerateCalls);
    }

    [Fact]
    public async Task ClearOrInvalidate_DuringAListing_StartsANewOne_AndTheOldOneIsNotCached()
    {
        using var gate = new ManualResetEventSlim(false);
        var fs = new FakeFileSystem().AddFile(@"C:.txt");
        fs.Gate = gate;
        var cache = new FolderListingCache(fs, () => false);

        var before = cache.GetAsync(@"C:", CancellationToken.None); // e.g. an auto refresh, still running
        while (Volatile.Read(ref fs.EnumerateCalls) == 0)
        {
            await Task.Delay(5);
        }

        cache.Clear(); // a file operation changed the folder: what runs now is from before it
        var after = cache.GetAsync(@"C:", CancellationToken.None);
        cache.Invalidate(@"C:");
        var again = cache.GetAsync(@"C:", CancellationToken.None);
        gate.Set();
        await Task.WhenAll(before, after, again);

        Assert.Equal(3, fs.EnumerateCalls); // none joined an older listing
        Assert.NotSame(await before, await after);
        Assert.True(cache.TryGetFresh(@"C:", out var cached));
        Assert.Same(await again, cached); // only the latest listing is cached
    }

    [Fact]
    public async Task Clear_ForcesReload()
    {
        var fs = new FakeFileSystem().AddFile(@"C:\r\a.txt");
        var cache = new FolderListingCache(fs, () => false);
        await cache.GetAsync(@"C:\r", CancellationToken.None);
        cache.Clear();
        Assert.False(cache.TryGetFresh(@"C:\r", out _));
        await cache.GetAsync(@"C:\r", CancellationToken.None);
        Assert.Equal(2, fs.EnumerateCalls);
    }

    [Fact]
    public async Task Invalidate_ForcesReloadOfOnePath()
    {
        var fs = new FakeFileSystem().AddFile(@"C:\r\a.txt").AddFile(@"C:\s\b.txt");
        var cache = new FolderListingCache(fs, () => false);
        await cache.GetAsync(@"C:\r", CancellationToken.None);
        await cache.GetAsync(@"C:\s", CancellationToken.None);
        cache.Invalidate(@"C:\r");
        Assert.False(cache.TryGetFresh(@"C:\r", out _));
        Assert.True(cache.TryGetFresh(@"C:\s", out _));
    }

    [Fact]
    public async Task HiddenSettingChange_IsNotServedFromOldInFlight()
    {
        bool hidden = false;
        var fs = new FakeFileSystem().AddFile(@"C:\r\h.txt", extra: FileAttributes.Hidden);
        var cache = new FolderListingCache(fs, () => hidden);
        var a = await cache.GetAsync(@"C:\r", CancellationToken.None);
        Assert.Empty(a.Entries);
        hidden = true;
        cache.Clear();
        var b = await cache.GetAsync(@"C:\r", CancellationToken.None);
        Assert.Single(b.Entries);
    }

    [Fact]
    public async Task ConcurrentRequests_ShareOneEnumeration()
    {
        var fs = new BlockingFileSystem();
        var cache = new FolderListingCache(fs, () => false);

        var t1 = cache.GetAsync(@"C:\r", CancellationToken.None);
        var t2 = cache.GetAsync(@"C:\r", CancellationToken.None);
        Assert.True(fs.Started.Wait(TimeSpan.FromSeconds(5)));
        fs.Release.Set();

        var r1 = await t1;
        var r2 = await t2;
        Assert.Same(r1, r2);
        Assert.Equal(1, fs.Calls);
    }

    [Fact]
    public async Task EnumerationIsCancelled_OnlyWhenEveryCallerLeaves()
    {
        var fs = new BlockingFileSystem();
        var cache = new FolderListingCache(fs, () => false);
        using var cts1 = new CancellationTokenSource();
        using var cts2 = new CancellationTokenSource();

        var t1 = cache.GetAsync(@"C:\r", cts1.Token);
        var t2 = cache.GetAsync(@"C:\r", cts2.Token);
        Assert.True(fs.Started.Wait(TimeSpan.FromSeconds(5)));

        cts1.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t1);
        Assert.False(fs.Cancelled.Wait(TimeSpan.FromMilliseconds(200)));

        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t2);
        Assert.True(fs.Cancelled.Wait(TimeSpan.FromSeconds(5)));

        // A cancelled enumeration is not cached; the next request lists again.
        fs.Release.Set();
        var r = await cache.GetAsync(@"C:\r", CancellationToken.None);
        Assert.True(r.IsOk);
        Assert.Equal(2, fs.Calls);
    }

    [Fact]
    public async Task Errors_AreReturnedAsListings()
    {
        var fs = new FakeFileSystem();
        var cache = new FolderListingCache(fs, () => false);
        var r = await cache.GetAsync(@"C:\missing", CancellationToken.None);
        Assert.Equal(ListingStatus.NotFound, r.Status);
    }

    private sealed class BlockingFileSystem : IFileSystem
    {
        private int _calls;

        public int Calls => _calls;

        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public ManualResetEventSlim Cancelled { get; } = new();

        public IReadOnlyList<FsEntry> EnumerateDirectory(string path, bool includeHidden, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Started.Set();
            try
            {
                Release.Wait(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.Set();
                throw;
            }

            return [new FsEntry("a.txt", path + "\\a.txt", false, false, 1, DateTime.UtcNow, FileAttributes.Archive)];
        }

        public PathKind GetKind(string path) => PathKind.Folder;

        public FsEntry? GetEntry(string path) => null;
    }
}

public class ExistenceTests
{
    [Fact]
    public async Task CheckAsync_ReportsKind()
    {
        var fs = new FakeFileSystem().AddFile(@"C:\r\a.txt");
        Assert.Equal(ExistState.Folder, await Existence.CheckAsync(fs, @"C:\r", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(ExistState.File, await Existence.CheckAsync(fs, @"C:\r\a.txt", TimeSpan.FromSeconds(2), CancellationToken.None));
        Assert.Equal(ExistState.Missing, await Existence.CheckAsync(fs, @"C:\nope", TimeSpan.FromSeconds(2), CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_TimesOut()
    {
        using var release = new ManualResetEventSlim();
        var fs = new HangingFileSystem(release);
        try
        {
            var state = await Existence.CheckAsync(fs, @"\\server\share", TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.Equal(ExistState.Timeout, state);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task FilterAsync_KeepsOrder_DropsMissing_KeepsTimeouts_RespectsLimit()
    {
        var items = Enumerable.Range(1, 50).Select(i => $"p{i}").ToList();
        Task<ExistState> Check(string p, CancellationToken _)
        {
            int n = int.Parse(p[1..]);
            return Task.FromResult(n % 3 == 0 ? ExistState.Missing : n % 5 == 0 ? ExistState.Timeout : ExistState.File);
        }

        var kept = await Existence.FilterAsync(items, s => s, 10, Check, CancellationToken.None, parallelism: 4);
        Assert.Equal(["p1", "p2", "p4", "p5", "p7", "p8", "p10", "p11", "p13", "p14"], kept.Select(k => k.Item));
        Assert.Equal(ExistState.Timeout, kept.Single(k => k.Item == "p5").State);
    }

    [Fact]
    public async Task FilterAsync_FewerThanLimit()
    {
        var kept = await Existence.FilterAsync(
            new[] { "a", "b" },
            s => s,
            100,
            (_, _) => Task.FromResult(ExistState.Folder),
            CancellationToken.None);
        Assert.Equal(2, kept.Count);
    }

    private sealed class HangingFileSystem(ManualResetEventSlim release) : IFileSystem
    {
        public IReadOnlyList<FsEntry> EnumerateDirectory(string path, bool includeHidden, CancellationToken cancellationToken) => [];

        public PathKind GetKind(string path)
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return PathKind.Folder;
        }

        public FsEntry? GetEntry(string path) => null;
    }
}

public class ListingChangesTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 9, 0, 0, DateTimeKind.Utc);

    private static FsEntry Dir(string name, DateTime? at = null) =>
        new(name, @"E:\f\" + name, true, false, 0, at ?? T0, FileAttributes.Directory);

    private static FsEntry File(string name, long length = 10, DateTime? at = null, FileAttributes attrs = FileAttributes.Archive, bool reparse = false) =>
        new(name, @"E:\f\" + name, false, reparse, length, at ?? T0, attrs);

    [Fact]
    public void Compare_IdenticalOrOnlyAttributesAndReparseDiffer_IsEmpty()
    {
        FsEntry[] shown = [Dir("sub"), File("a.png"), File("b.txt")];
        var same = ListingChanges.Compare(shown, [Dir("sub"), File("a.png"), File("b.txt")]);
        Assert.True(same.IsEmpty);
        Assert.Equal(3, same.Reusable.Count);

        // An online-only file downloaded for its thumbnail: only the cloud-state bits change.
        var hydrated = ListingChanges.Compare(
            shown,
            [Dir("sub"), File("a.png", attrs: FileAttributes.Offline | (FileAttributes)0x80000 | (FileAttributes)0x400000, reparse: true), File("b.txt")]);
        Assert.True(hydrated.IsEmpty);
        Assert.Contains(@"E:\f\a.png", hydrated.Reusable);
    }

    [Fact]
    public void Compare_AddRemoveModifyAndCaseRename_ReusesOnlyUnchanged()
    {
        FsEntry[] shown = [File("keep.txt"), File("gone.txt"), File("grow.txt"), File("touch.txt"), File("name.txt")];
        FsEntry[] fresh =
        [
            File("keep.txt"),
            File("grow.txt", length: 99),
            File("touch.txt", at: T0.AddMinutes(1)),
            File("Name.txt"),
            File("new.txt"),
        ];

        var delta = ListingChanges.Compare(shown, fresh);
        Assert.False(delta.IsEmpty);
        Assert.Equal(2, delta.Added);   // new.txt, Name.txt (a case-only rename shows the new name)
        Assert.Equal(2, delta.Removed); // gone.txt, name.txt
        Assert.Equal(2, delta.Changed); // grow.txt, touch.txt
        Assert.Equal([@"E:\f\keep.txt"], delta.Reusable);
        Assert.Empty(delta.ChangedFolders);
    }

    [Fact]
    public void Compare_FolderWithNewWriteTime_IsChangedFolder()
    {
        FsEntry[] shown = [Dir("busy"), Dir("quiet"), File("doc.txt")];
        FsEntry[] fresh = [Dir("busy", T0.AddSeconds(5)), Dir("quiet"), File("doc.txt", at: T0.AddSeconds(5))];

        var delta = ListingChanges.Compare(shown, fresh);
        Assert.Equal([@"E:\f\busy"], delta.ChangedFolders);
        Assert.Equal(2, delta.Changed);
        Assert.Equal([@"E:\f\quiet"], delta.Reusable);
    }
}
