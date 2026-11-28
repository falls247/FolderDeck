using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Listing;

namespace FolderDeck.Core.Tests;

public class DeepSearchTests
{
    private static FakeFileSystem Tree() => new FakeFileSystem()
        .AddFile(@"C:\r\report top.txt") // a direct child: the list's own, not the search's
        .AddFile(@"C:\r\a\Report 1.txt")
        .AddFile(@"C:\r\a\b\ｒｅｐｏｒｔ 2.docx") // full width: NameSearch ignores width
        .AddFile(@"C:\r\a\other.txt")
        .AddFile(@"C:\r\.git\report.pack")
        .AddFolder(@"C:\r\link", reparse: true)
        .AddFile(@"C:\r\link\report loop.txt");

    private static async Task<List<string>> Run(FakeFileSystem fs, Func<string, IReadOnlyList<string>, CancellationToken, IReadOnlyList<string>>? index)
    {
        var found = new List<string>();
        var end = await DeepSearch.RunAsync(fs, @"C:\r", "report", ListingFilter.None, index, batch =>
        {
            lock (found)
            {
                found.AddRange(batch.Select(e => e.FullPath));
            }
        }, CancellationToken.None);
        Assert.Equal(DeepSearchEnd.Complete, end);
        return found.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [Fact]
    public async Task Walk_FindsBelowTheChildren_SkipsDotFoldersAndJunctions()
    {
        var found = await Run(Tree(), index: null);

        Assert.Equal([@"C:\r\a\b\ｒｅｐｏｒｔ 2.docx", @"C:\r\a\Report 1.txt"], found);
    }

    [Fact]
    public async Task Index_IsMergedWithoutDuplicates_StaleAndFilteredHitsDropped_FailureFallsBackToTheWalk()
    {
        var fs = Tree();
        IReadOnlyList<string> Index(string root, IReadOnlyList<string> terms, CancellationToken ct) =>
        [
            @"C:\r\a\Report 1.txt", // also found by the walk: once
            @"C:\r\a\gone report.txt", // the index is behind: not on the disk
            @"C:\r\.git\report.pack", // inside a dot folder: filtered as the walk would
            @"C:\r\report top.txt", // a direct child
        ];

        Assert.Equal([@"C:\r\a\b\ｒｅｐｏｒｔ 2.docx", @"C:\r\a\Report 1.txt"], await Run(fs, Index));
        Assert.Equal(2, (await Run(fs, (_, _, _) => throw new InvalidOperationException("no indexer"))).Count);
    }

    [Fact]
    public void IndexQuery_EscapesQuotesAndWildcards_AndSearchScopeNormalizes()
    {
        string sql = DeepSearch.IndexQuery(@"C:\Users\o'neil\", ["50%_off", "[draft]"], 100);

        Assert.Equal(
            @"SELECT TOP 100 System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:C:\Users\o''neil\'" +
            " AND System.FileName LIKE '%50[%][_]off%' AND System.FileName LIKE '%[[]draft]%'",
            sql);

        var settings = new FolderDeck.Core.Stores.AppSettings { SearchScope = " Recursive " };
        settings.Normalize();
        Assert.True(settings.SearchesRecursively);
        settings.SearchScope = "everywhere";
        settings.Normalize();
        Assert.Equal("folder", settings.SearchScope);
    }
}
