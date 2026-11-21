using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Tests;

public class CardOrderStoreTests
{
    [Fact]
    public void RoundTrip_KeepsBothListsSeparately()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("card_order.json");
        var store = new CardOrderStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Missing, store.LoadStatus);
        Assert.Empty(store.Get(CardOrderList.Recent));

        Assert.True(store.Set(CardOrderList.Recent, [@"C:\資料\b.txt", @"C:\a"]));
        Assert.True(store.Set(CardOrderList.Frequent, [@"D:\x"]));
        store.Flush();

        string text = File.ReadAllText(file);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"recent\"", text);
        Assert.Contains("\"frequent\"", text);
        Assert.Contains("資料", text);

        var reloaded = new CardOrderStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(LoadStatus.Loaded, reloaded.LoadStatus);
        Assert.Equal([@"C:\資料\b.txt", @"C:\a"], reloaded.Get(CardOrderList.Recent));
        Assert.Equal([@"D:\x"], reloaded.Get(CardOrderList.Frequent));
    }

    [Fact]
    public void Set_NormalizesAndDeduplicates_CaseInsensitively()
    {
        using var tmp = new TempDir();
        var store = new CardOrderStore(tmp.Combine("card_order.json"), FakeClock.Fixed());
        store.Load();
        store.Set(CardOrderList.Frequent, [@"C:\Data\A\", @"c:\data\a", @"C:\Data\sub\..\b.txt", "", "  ", @"C:\Data\B.TXT"]);
        Assert.Equal([@"C:\Data\A", @"C:\Data\b.txt"], store.Get(CardOrderList.Frequent));
        Assert.False(store.Set(CardOrderList.Frequent, [@"C:\Data\A", @"C:\Data\b.txt"]));
        store.Flush();
    }

    [Fact]
    public void Set_CapsEachList()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("card_order.json");
        var store = new CardOrderStore(file, FakeClock.Fixed());
        store.Load();
        store.Set(CardOrderList.Recent, Enumerable.Range(0, CardOrderStore.MaxEntries + 50).Select(i => $@"C:\items\f{i}.txt"));
        var saved = store.Get(CardOrderList.Recent);
        Assert.Equal(CardOrderStore.MaxEntries, saved.Count);
        Assert.Equal(@"C:\items\f0.txt", saved[0]);
        Assert.Equal($@"C:\items\f{CardOrderStore.MaxEntries - 1}.txt", saved[^1]);
        store.Flush();
    }

    [Fact]
    public void Clear_ForgetsOneList_AndPersists()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("card_order.json");
        var store = new CardOrderStore(file, FakeClock.Fixed());
        store.Load();
        Assert.False(store.HasOrder(CardOrderList.Recent));
        Assert.False(store.Clear(CardOrderList.Recent));

        store.Set(CardOrderList.Recent, [@"C:\a", @"C:\b"]);
        store.Set(CardOrderList.Frequent, [@"D:\x"]);
        Assert.True(store.HasOrder(CardOrderList.Recent));
        Assert.True(store.HasOrder(CardOrderList.Frequent));

        Assert.True(store.Clear(CardOrderList.Recent));
        Assert.False(store.HasOrder(CardOrderList.Recent));
        Assert.Empty(store.Get(CardOrderList.Recent));
        Assert.Equal([@"D:\x"], store.Get(CardOrderList.Frequent));
        Assert.False(store.Clear(CardOrderList.Recent));
        store.Flush();

        var reloaded = new CardOrderStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(LoadStatus.Loaded, reloaded.LoadStatus);
        Assert.False(reloaded.HasOrder(CardOrderList.Recent));
        Assert.Equal([@"D:\x"], reloaded.Get(CardOrderList.Frequent));
    }

    [Fact]
    public void Load_CleansHandEditedFile()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("card_order.json");
        File.WriteAllText(file, """{"version":1,"recent":["C:\\a\\", null, "c:\\A", "C:\\b"]}""");
        var store = new CardOrderStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal([@"C:\a", @"C:\b"], store.Get(CardOrderList.Recent));
        Assert.Empty(store.Get(CardOrderList.Frequent));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":1,\"recent\":\"x\",\"frequent\":[]}")]
    [InlineData("{\"version\":1,\"recent\":null,\"frequent\":[]}")]
    [InlineData("[]")]
    public void BrokenFile_IsMovedAside(string content)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("card_order.json");
        File.WriteAllText(file, content);
        var clock = FakeClock.Fixed();
        var store = new CardOrderStore(file, clock);
        store.Load();

        Assert.Equal(LoadStatus.Recovered, store.LoadStatus);
        Assert.Empty(store.Get(CardOrderList.Recent));
        Assert.False(File.Exists(file));
        string backup = tmp.Combine($"card_order.broken-{clock.UtcNow.ToUnixTimeSeconds()}.json");
        Assert.Equal(backup, store.BackupPath);
        Assert.Equal(content, File.ReadAllText(backup));
    }

    [Fact]
    public void DataStores_IncludesCardOrders()
    {
        using var tmp = new TempDir();
        string dir = tmp.Combine("data");
        var stores = new DataStores(dir, FakeClock.Fixed());
        stores.LoadAll();
        Assert.Contains(stores.CardOrders, stores.All);
        stores.CardOrders.Set(CardOrderList.Recent, [@"C:\a.txt"]);
        stores.FlushAll();
        Assert.True(File.Exists(Path.Combine(dir, CardOrderStore.DefaultFileName)));
    }
}

public class RegisteredFoldersReorderTests
{
    [Fact]
    public void Reorder_ListedFirst_OthersKeepOrder_UnknownIgnored_AndPersists()
    {
        using var tmp = new TempDir();
        string a = tmp.MakeDir("a");
        string b = tmp.MakeDir("b");
        string c = tmp.MakeDir("c");
        string d = tmp.MakeDir("d");
        var store = new RegisteredFoldersStore(tmp.Combine("registered_folders.json"), FakeClock.Fixed());
        store.Load();
        var summary = store.RegisterMany([a, b, c, d], RealFileSystem.Instance);
        var ids = summary.Results.Select(r => r.Folder!.Id).ToArray();

        Assert.True(store.Reorder([ids[2], "000000000000", ids[0], ids[2]]));
        Assert.Equal([c, a, b, d], store.Folders.Select(f => f.Path));
        Assert.False(store.Reorder([ids[2], ids[0]]));
        Assert.False(store.Reorder([]));
        store.Flush();

        var reloaded = new RegisteredFoldersStore(store.FilePath, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal([c, a, b, d], reloaded.Folders.Select(f => f.Path));
        Assert.Equal(ids[2], reloaded.Folders[0].Id);
    }
}

public class SortModeSettingsTests
{
    [Fact]
    public void Defaults_AreManualForEveryTab()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Combine("settings.json"), FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(3, s.SortModes!.Count);
        Assert.All(s.SortModes.Values, v => Assert.Equal("manual", v));
        Assert.Equal(CardSortMode.Manual, s.SortModeOf(AppSettings.TabRecent));
    }

    [Fact]
    public void Normalize_FillsMissing_AndReplacesInvalidValues()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":1,"sort_modes":{"registered":"NAME_DESC","recent":"bogus","frequent":null}}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);

        // "registered" (the former 登録フォルダ tab) is carried over to the folder tabs.
        Assert.Equal("name_desc", s.SortModes![AppSettings.TabFolder]);
        Assert.False(s.SortModes.ContainsKey("registered"));
        Assert.Equal("manual", s.SortModes[AppSettings.TabRecent]);
        Assert.Equal("manual", s.SortModes[AppSettings.TabFrequent]);
        Assert.Equal(CardSortMode.NameDesc, s.SortModeOf(AppSettings.TabFolder));

        var missing = new AppSettings { SortModes = null };
        missing.Normalize();
        Assert.Equal(3, missing.SortModes!.Count);
    }

    [Fact]
    public void RoundTrip_UsesSortModesKey()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        store.Update(s =>
        {
            s.SetSortMode(AppSettings.TabFolder, CardSortMode.ModifiedDesc);
            s.SetSortMode(AppSettings.TabFrequent, CardSortMode.NameAsc);
        });
        store.Flush();

        string text = File.ReadAllText(file);
        Assert.Contains("\"sort_modes\"", text);
        Assert.Contains("\"folder\": \"modified_desc\"", text);
        Assert.Contains("\"frequent\": \"name_asc\"", text);

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        var s = reloaded.Current;
        Assert.Equal(CardSortMode.ModifiedDesc, s.SortModeOf(AppSettings.TabFolder));
        Assert.Equal(CardSortMode.Manual, s.SortModeOf(AppSettings.TabRecent));
        Assert.Equal(CardSortMode.NameAsc, s.SortModeOf(AppSettings.TabFrequent));
    }

    [Fact]
    public void Clone_CopiesSortModesDeeply()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Combine("settings.json"), FakeClock.Fixed());
        store.Load();
        var copy = store.Current;
        copy.SetSortMode(AppSettings.TabRecent, CardSortMode.NameAsc);
        Assert.Equal(CardSortMode.Manual, store.Current.SortModeOf(AppSettings.TabRecent));
    }
}
