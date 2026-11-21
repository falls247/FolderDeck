using FolderDeck.Core.Ordering;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Tests;

public class ItemFilterTests
{
    private sealed record Item(string Name, bool IsFolder);

    private static readonly Item[] Mixed =
    [
        new("a", true),
        new("1.txt", false),
        new("b", true),
        new("2.txt", false),
        new("3.txt", false),
    ];

    [Theory]
    [InlineData("all", ItemFilter.All)]
    [InlineData("folders", ItemFilter.Folders)]
    [InlineData("files", ItemFilter.Files)]
    [InlineData("FILES", ItemFilter.All)]
    [InlineData(null, ItemFilter.All)]
    public void FromSetting_UnknownIsAll_AndRoundTrips(string? value, ItemFilter expected)
    {
        Assert.Equal(expected, ItemFilters.FromSetting(value));
        Assert.Equal(expected, ItemFilters.FromSetting(ItemFilters.ToSetting(expected)));
    }

    [Fact]
    public void Apply_KeepsOrder_AllReturnsTheSameList()
    {
        Assert.Same(Mixed, ItemFilters.Apply(Mixed, ItemFilter.All, i => i.IsFolder));
        Assert.Equal(["a", "b"], ItemFilters.Apply(Mixed, ItemFilter.Folders, i => i.IsFolder).Select(i => i.Name));
        Assert.Equal(["1.txt", "2.txt", "3.txt"], ItemFilters.Apply(Mixed, ItemFilter.Files, i => i.IsFolder).Select(i => i.Name));
    }

    [Fact]
    public void MergeShownOrder_HiddenItemsKeepTheirSlots()
    {
        // Files only: the three files were dragged into the order 3, 1, 2. The folders stay put.
        Item[] dragged = [Mixed[4], Mixed[1], Mixed[3]];
        var merged = ItemFilters.MergeShownOrder(Mixed, dragged, i => !i.IsFolder);
        Assert.Equal(["a", "3.txt", "b", "1.txt", "2.txt"], merged.Select(i => i.Name));

        // No filter: the new order is the whole order.
        Item[] all = [Mixed[4], Mixed[0], Mixed[1], Mixed[2], Mixed[3]];
        Assert.Equal(all, ItemFilters.MergeShownOrder(Mixed, all, _ => true));
    }

    [Fact]
    public void MergeShownOrder_CountMismatch_LeavesTheListAlone()
    {
        Item[] tooFew = [Mixed[1]];
        Assert.Same(Mixed, ItemFilters.MergeShownOrder(Mixed, tooFew, i => !i.IsFolder));
    }

    [Fact]
    public void Settings_FilterModes_DefaultAll_InvalidReplaced_RoundTrip()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":1,"filter_modes":{"recent":"bogus"}}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(ItemFilter.All, store.Current.FilterOf(AppSettings.TabRecent));
        Assert.Equal(ItemFilter.All, store.Current.FilterOf(AppSettings.TabFrequent));

        store.Update(s => s.SetFilter(AppSettings.TabFrequent, ItemFilter.Folders));
        store.Flush();
        Assert.Contains("\"frequent\": \"folders\"", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        var copy = reloaded.Current;
        Assert.Equal(ItemFilter.Folders, copy.FilterOf(AppSettings.TabFrequent));
        copy.SetFilter(AppSettings.TabFrequent, ItemFilter.Files); // a copy: the store keeps its own
        Assert.Equal(ItemFilter.Folders, reloaded.Current.FilterOf(AppSettings.TabFrequent));
    }
}
