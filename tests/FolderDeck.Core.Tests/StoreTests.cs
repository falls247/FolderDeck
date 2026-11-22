using System.Text;
using System.Text.RegularExpressions;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Stores;

namespace FolderDeck.Core.Tests;

public class JsonFileTests
{
    [Fact]
    public void FormatTime_IsUtcToTheSecond()
    {
        var local = new DateTimeOffset(2026, 11, 21, 10, 23, 45, 678, TimeSpan.FromHours(9));
        Assert.Equal("2026-11-21T01:23:45+00:00", JsonFile.FormatTime(local));
    }

    [Fact]
    public void ParseTime_AcceptsIsoAndRejectsGarbage()
    {
        Assert.Equal(FakeClock.Default, JsonFile.ParseTime("2026-11-21T01:23:45+00:00"));
        Assert.Equal(FakeClock.Default, JsonFile.ParseTime("2026-11-21T10:23:45+09:00"));
        Assert.Null(JsonFile.ParseTime("not a date"));
        Assert.Null(JsonFile.ParseTime(""));
        Assert.Null(JsonFile.ParseTime(null));
    }

    [Fact]
    public void TruncateToSeconds()
    {
        var t = new DateTimeOffset(2026, 11, 21, 1, 23, 45, 999, TimeSpan.Zero);
        Assert.Equal(FakeClock.Default, JsonFile.TruncateToSeconds(t));
    }

    [Fact]
    public void RoundTrip_WritesUtf8WithoutBom_AndNoTmpLeft()
    {
        using var tmp = new TempDir();
        string folder = tmp.MakeDir("資料");
        string file = tmp.Combine("registered_folders.json");
        var clock = FakeClock.Fixed();

        var store = new RegisteredFoldersStore(file, clock);
        store.Load();
        Assert.Equal(LoadStatus.Missing, store.LoadStatus);
        var r = store.Register(folder, RealFileSystem.Instance);
        Assert.Equal(RegisterOutcome.Added, r.Outcome);
        store.Flush();

        byte[] bytes = File.ReadAllBytes(file);
        Assert.Equal((byte)'{', bytes[0]);
        string text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"folders\"", text);
        Assert.Contains("\"added_at\": \"2026-11-21T01:23:45+00:00\"", text);
        Assert.Contains("資料", text); // not escaped
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));

        var reloaded = new RegisteredFoldersStore(file, clock);
        reloaded.Load();
        Assert.Equal(LoadStatus.Loaded, reloaded.LoadStatus);
        var only = Assert.Single(reloaded.Folders);
        Assert.Equal(r.Folder!.Id, only.Id);
        Assert.Equal(folder, only.Path);
        Assert.Equal("2026-11-21T01:23:45+00:00", only.AddedAt);
    }

    [Fact]
    public void Load_AcceptsBom()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_folders.json");
        string json = "{\"version\":1,\"folders\":[{\"id\":\"3f9a1c0b7d2e\",\"path\":\"E:\\\\資料\",\"added_at\":\"2026-11-21T01:23:45+00:00\"}]}";
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json)]);

        var store = new RegisteredFoldersStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal("E:\\資料", Assert.Single(store.Folders).Path);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"version\":1,\"folders\":\"x\"}")]
    [InlineData("{\"version\":1,\"folders\":null}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("")]
    public void BrokenFile_IsMovedAside_AndStoreStartsEmpty(string content)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_folders.json");
        File.WriteAllText(file, content);
        var clock = FakeClock.Fixed();

        var store = new RegisteredFoldersStore(file, clock);
        store.Load();

        Assert.Equal(LoadStatus.Recovered, store.LoadStatus);
        Assert.False(store.IsDisabled);
        Assert.Empty(store.Folders);
        Assert.False(File.Exists(file));
        string expectedBackup = tmp.Combine($"registered_folders.broken-{clock.UtcNow.ToUnixTimeSeconds()}.json");
        Assert.Equal(expectedBackup, store.BackupPath);
        Assert.Equal(content, File.ReadAllText(expectedBackup));
    }

    [Fact]
    public void BrokenFile_SecondBackupGetsUniqueName()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("pins.json");
        var clock = FakeClock.Fixed();
        File.WriteAllText(file, "{bad");
        new PinStore(file, clock).Load();
        File.WriteAllText(file, "{bad again");
        var store = new PinStore(file, clock);
        store.Load();
        Assert.Equal(LoadStatus.Recovered, store.LoadStatus);
        Assert.Equal(2, Directory.GetFiles(tmp.Path, "pins.broken-*.json").Length);
    }

    [Fact]
    public void BrokenFile_ThatCannotBeMoved_DisablesTheStore_AndIsNeverOverwritten()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("history.json");
        File.WriteAllText(file, "{ broken");
        var store = new HistoryStore(file, FakeClock.Fixed());

        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            store.Load();
        }

        Assert.Equal(LoadStatus.Failed, store.LoadStatus);
        Assert.True(store.IsDisabled);
        store.Record(@"C:\a.txt", ItemKind.File);
        store.Flush();
        store.SaveNow();
        Assert.Equal("{ broken", File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.broken-*.json"));
    }

    [Fact]
    public void WriteAtomic_FailureLeavesNoTmp()
    {
        using var tmp = new TempDir();
        string target = tmp.MakeDir("settings.json"); // a folder where the file should go
        Assert.ThrowsAny<Exception>(() => JsonFile.WriteAtomic(target, [1, 2, 3]));
        Assert.Empty(Directory.GetFiles(tmp.Path, "*.tmp"));
    }

    [Fact]
    public void WriteAtomic_ReplacesExistingFile()
    {
        using var tmp = new TempDir();
        string target = tmp.MakeFile("x.json", "old");
        JsonFile.WriteAtomic(target, Encoding.UTF8.GetBytes("new"));
        Assert.Equal("new", File.ReadAllText(target));
        Assert.Single(Directory.GetFiles(tmp.Path));
    }

    [Fact]
    public void BackupPathFor_UsesUnixSeconds()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1763688225);
        string p = JsonFile.BackupPathFor(@"C:\nowhere-fd\settings.json", at);
        Assert.Equal(@"C:\nowhere-fd\settings.broken-1763688225.json", p);
    }
}

public class RegisteredFoldersStoreTests
{
    private static (TempDir Tmp, RegisteredFoldersStore Store, string Folder) Create()
    {
        var tmp = new TempDir();
        string folder = tmp.MakeDir("資料");
        tmp.MakeDir("資料", "sub");
        var store = new RegisteredFoldersStore(tmp.Combine("registered_folders.json"), FakeClock.Fixed());
        store.Load();
        return (tmp, store, folder);
    }

    [Fact]
    public void QuotedInput_IsRegisteredWithoutQuotes()
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            var r = store.Register($"\"{folder}\"", RealFileSystem.Instance);
            Assert.Equal(RegisterOutcome.Added, r.Outcome);
            Assert.Equal(folder, r.Path);
            Assert.Matches(new Regex("^[0-9a-f]{12}$"), r.Folder!.Id);
            Assert.Equal("2026-11-21T01:23:45+00:00", r.Folder.AddedAt);
            store.Flush();
        }
    }

    [Theory]
    [InlineData("{0}")]
    [InlineData("{0}\\")]
    [InlineData("{0}\\\\")]
    [InlineData("{0}\\.")]
    [InlineData("{0}\\sub\\..")]
    [InlineData("'{0}'")]
    [InlineData("  {0}  ")]
    public void SameFolderWrittenDifferently_IsAlreadyRegistered(string pattern)
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            Assert.Equal(RegisterOutcome.Added, store.Register(folder, RealFileSystem.Instance).Outcome);
            var r = store.Register(string.Format(pattern, folder), RealFileSystem.Instance);
            Assert.Equal(RegisterOutcome.AlreadyRegistered, r.Outcome);
            Assert.Single(store.Folders);
            store.Flush();
        }
    }

    [Fact]
    public void DifferentCase_IsAlreadyRegistered()
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            store.Register(folder, RealFileSystem.Instance);
            var r = store.Register(folder.ToUpperInvariant() + "\\", RealFileSystem.Instance);
            Assert.Equal(RegisterOutcome.AlreadyRegistered, r.Outcome);
            Assert.Single(store.Folders);
            store.Flush();
        }
    }

    [Fact]
    public void InvalidInputs_UseSpecMessages()
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            var empty = store.Register("  ", RealFileSystem.Instance);
            Assert.Equal(RegisterOutcome.Invalid, empty.Outcome);
            Assert.Equal("フォルダのパスを入力してください。", empty.Error);

            var relative = store.Register("資料\\2026", RealFileSystem.Instance);
            Assert.Equal(RegisterOutcome.Invalid, relative.Outcome);
            Assert.Equal("絶対パスで指定してください: 資料\\2026", relative.Error);

            string missing = Path.Combine(folder, "nope");
            var notFound = store.Register(missing, RealFileSystem.Instance);
            Assert.Equal(RegisterOutcome.NotFound, notFound.Outcome);
            Assert.Equal($"フォルダが見つかりません: {missing}", notFound.Error);

            string file = tmp.MakeFile("file.txt");
            Assert.Equal(RegisterOutcome.NotFound, store.Register(file, RealFileSystem.Instance).Outcome);
            Assert.Empty(store.Folders);
        }
    }

    [Fact]
    public void RegisterMany_SummarizesResults_AndKeepsOrder()
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            string b = tmp.MakeDir("b");
            string c = tmp.MakeDir("c");
            store.Register(folder, RealFileSystem.Instance);

            var summary = store.RegisterMany([c, folder, b, Path.Combine(folder, "missing")], RealFileSystem.Instance);
            Assert.Equal(2, summary.Added);
            Assert.Equal(1, summary.AlreadyRegistered);
            Assert.Equal(1, summary.NotFound);
            Assert.StartsWith("2 件登録しました。1 件は登録済みです。フォルダが見つかりません: ", summary.ToMessage());
            Assert.Equal([folder, c, b], store.Folders.Select(f => f.Path));
            store.Flush();

            var reloaded = new RegisteredFoldersStore(store.FilePath, FakeClock.Fixed());
            reloaded.Load();
            Assert.Equal([folder, c, b], reloaded.Folders.Select(f => f.Path));
        }
    }

    [Fact]
    public void Remove_DropsOnlyThatFolder_AndLeavesFilesAlone()
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            string marker = tmp.MakeFile(Path.Combine("資料", "keep.txt"));
            var added = store.Register(folder, RealFileSystem.Instance).Folder!;
            Assert.True(store.Remove(added.Id));
            Assert.False(store.Remove(added.Id));
            Assert.Empty(store.Folders);
            Assert.True(File.Exists(marker));
            store.Flush();
        }
    }

    [Fact]
    public void Lookup_ById_AndByPath()
    {
        var (tmp, store, folder) = Create();
        using (tmp)
        {
            var added = store.Register(folder, RealFileSystem.Instance).Folder!;
            Assert.Equal(added, store.FindById(added.Id));
            Assert.Equal(added, store.FindByPath(folder.ToLowerInvariant() + "\\"));
            Assert.Null(store.FindById("000000000000"));
            store.Flush();
        }
    }

    [Fact]
    public void Load_SkipsInvalidEntries_KeepsImgtoolFormat()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_folders.json");
        File.WriteAllText(file, """
            {"version":1,"folders":[
              {"id":"aaaaaaaaaaaa","path":"E:\\a","added_at":"2026-01-01T00:00:00+00:00"},
              {"id":"","path":"E:\\b"},
              {"path":"E:\\c"},
              {"id":"aaaaaaaaaaaa","path":"E:\\dup"},
              {"id":"bbbbbbbbbbbb","path":"E:\\d","added_at":"whatever"}
            ]}
            """);
        var store = new RegisteredFoldersStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(["E:\\a", "E:\\d"], store.Folders.Select(f => f.Path));
        Assert.Equal("whatever", store.Folders[1].AddedAt);
    }
}

public class HistoryStoreTests
{
    [Fact]
    public void Record_KeepsTenRecentOpens_NewestFirst()
    {
        using var tmp = new TempDir();
        var clock = FakeClock.Fixed();
        var store = new HistoryStore(tmp.Combine("history.json"), clock);
        store.Load();

        for (int i = 0; i < 12; i++)
        {
            store.Record(@"C:\資料\a.pdf", ItemKind.File, FakeClock.Default.AddMinutes(i));
        }

        var item = Assert.Single(store.Items);
        Assert.Equal(12, item.OpenCount);
        Assert.Equal(HistoryStore.MaxRecentOpens, item.RecentOpens.Count);
        Assert.Equal(FakeClock.Default.AddMinutes(11), item.LastOpenedAt);
        Assert.Equal(FakeClock.Default.AddMinutes(11), item.RecentOpens[0]);
        Assert.Equal(FakeClock.Default.AddMinutes(2), item.RecentOpens[^1]);
        store.Flush();
    }

    [Fact]
    public void Record_MatchesPathsCaseInsensitively_AndNormalizes()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Combine("history.json"), FakeClock.Fixed());
        store.Load();
        store.Record(@"C:\資料\2026\", ItemKind.Folder);
        store.Record(@"c:\資料\2026", ItemKind.Folder);
        var item = Assert.Single(store.Items);
        Assert.Equal(@"C:\資料\2026", item.Path);
        Assert.Equal(2, item.OpenCount);
        Assert.Equal(ItemKind.Folder, item.Kind);
        store.Flush();
    }

    [Fact]
    public void Record_TruncatesToSeconds()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Combine("history.json"), FakeClock.Fixed());
        store.Load();
        var item = store.Record(@"C:\a.txt", ItemKind.File, FakeClock.Default.AddMilliseconds(750));
        Assert.Equal(FakeClock.Default, item.LastOpenedAt);
        store.Flush();
    }

    [Fact]
    public void CapsAt2000_DroppingOldest()
    {
        using var tmp = new TempDir();
        var store = new HistoryStore(tmp.Combine("history.json"), FakeClock.Fixed());
        store.Load();
        for (int i = 0; i < HistoryStore.MaxItems + 5; i++)
        {
            store.Record($@"C:\items\f{i}.txt", ItemKind.File, FakeClock.Default.AddSeconds(i));
        }

        var items = store.Items;
        Assert.Equal(HistoryStore.MaxItems, items.Count);
        for (int i = 0; i < 5; i++)
        {
            Assert.DoesNotContain(items, h => h.Path == $@"C:\items\f{i}.txt");
        }

        Assert.Contains(items, h => h.Path == @"C:\items\f5.txt");
        Assert.Contains(items, h => h.Path == $@"C:\items\f{HistoryStore.MaxItems + 4}.txt");
        store.Flush();
    }

    [Fact]
    public void RoundTrip_AndRemove_AndClear()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("history.json");
        var store = new HistoryStore(file, FakeClock.Fixed());
        store.Load();
        store.Record(@"C:\a.txt", ItemKind.File, FakeClock.Default);
        store.Record(@"C:\a.txt", ItemKind.File, FakeClock.Default.AddDays(1));
        store.Record(@"C:\dir", ItemKind.Folder, FakeClock.Default);
        store.Flush();

        string text = File.ReadAllText(file);
        Assert.Contains("\"open_count\": 2", text);
        Assert.Contains("\"kind\": \"folder\"", text);
        Assert.Contains("\"last_opened_at\": \"2026-11-22T01:23:45+00:00\"", text);
        Assert.Contains("\"recent_opens\"", text);

        var reloaded = new HistoryStore(file, FakeClock.Fixed());
        reloaded.Load();
        var a = reloaded.Find(@"c:\A.TXT")!;
        Assert.Equal(2, a.OpenCount);
        Assert.Equal([FakeClock.Default.AddDays(1), FakeClock.Default], a.RecentOpens);
        Assert.Equal(ItemKind.Folder, reloaded.Find(@"C:\dir")!.Kind);

        Assert.True(reloaded.Remove(@"C:\A.txt"));
        Assert.Null(reloaded.Find(@"C:\a.txt"));
        reloaded.Clear();
        Assert.Empty(reloaded.Items);
        reloaded.Flush();
    }

    [Fact]
    public void Load_ToleratesBadDatesAndMissingRecentOpens()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("history.json");
        File.WriteAllText(file, """
            {"version":1,"items":[
              {"path":"C:\\a.txt","kind":"file","open_count":3,"last_opened_at":"2026-11-20T00:00:00+00:00","recent_opens":["bad", "2026-11-20T00:00:00+00:00"]},
              {"path":"C:\\b.txt","kind":"weird","open_count":1,"last_opened_at":"2026-11-19T00:00:00+00:00"},
              {"path":"C:\\c.txt","open_count":1,"last_opened_at":"nope","recent_opens":[]},
              {"kind":"file","open_count":1,"last_opened_at":"2026-11-19T00:00:00+00:00"}
            ]}
            """);
        var store = new HistoryStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], store.Items.Select(i => i.Path));
        Assert.Single(store.Items[0].RecentOpens);
        Assert.Empty(store.Items[1].RecentOpens);
        Assert.Equal(ItemKind.File, store.Items[1].Kind);
    }
}

public class PinAndExcludedStoreTests
{
    [Fact]
    public void Pins_KeepPinningOrder_AndRoundTrip()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("pins.json");
        var store = new PinStore(file, FakeClock.Fixed());
        store.Load();
        Assert.True(store.Pin(@"C:\b", ItemKind.Folder, FakeClock.Default.AddMinutes(2)));
        Assert.True(store.Pin(@"C:\a.txt", ItemKind.File, FakeClock.Default.AddMinutes(1)));
        Assert.False(store.Pin(@"c:\B\", ItemKind.Folder));
        Assert.True(store.IsPinned(@"c:\a.TXT"));
        Assert.Equal([@"C:\a.txt", @"C:\b"], store.Pins.Select(p => p.Path));
        store.Flush();

        var reloaded = new PinStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal([@"C:\a.txt", @"C:\b"], reloaded.Pins.Select(p => p.Path));
        Assert.Equal(ItemKind.Folder, reloaded.Pins[1].Kind);
        Assert.True(reloaded.Unpin(@"C:\A.txt"));
        Assert.False(reloaded.IsPinned(@"C:\a.txt"));
        reloaded.Flush();
    }

    [Fact]
    public void Excluded_UpsertsAndRoundTrips()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("excluded.json");
        var store = new ExcludedStore(file, FakeClock.Fixed());
        store.Load();
        store.Exclude(@"C:\a.zip", FakeClock.Default);
        store.Exclude(@"c:\A.zip", FakeClock.Default.AddHours(1));
        var only = Assert.Single(store.Items);
        Assert.Equal(FakeClock.Default.AddHours(1), only.ExcludedAt);
        store.Flush();
        Assert.Contains("\"excluded_at\": \"2026-11-21T02:23:45+00:00\"", File.ReadAllText(file));

        var reloaded = new ExcludedStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Single(reloaded.Items);
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void MissingFile_UsesDefaults()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Combine("settings.json"), FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(200, s.CardSize);
        Assert.Equal(100, s.TextScale);
        Assert.True(s.ConfirmExecutables);
        Assert.True(s.IncludeWindowsRecent);
        Assert.False(s.ShowHidden);
        Assert.False(s.AddToWindowsRecent);
        Assert.Equal(100, s.RecentLimit);
        Assert.Equal(50, s.FrequentLimit);
        Assert.Equal(512, s.ThumbCacheMb);
        Assert.Null(s.RecentClearedAt);
        Assert.Equal(AppSettings.SidebarWidthDefault, s.SidebarWidth);
    }

    private static string Describe(OpenTabSetting tab) => $"{tab.Kind}|{tab.Root}|{tab.Path}";

    [Fact]
    public void OpenTabs_FirstRunOpensRecent_ASavedEmptyListStaysEmpty()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load(); // no file
        Assert.Equal(["recent||"], store.Current.OpenTabs!.Select(Describe));
        Assert.Equal(0, store.Current.SelectedTab);

        // Every tab was closed: that is remembered, 最近の履歴 does not come back.
        store.Update(s =>
        {
            s.OpenTabs = [];
            s.SelectedTab = 0;
        });
        store.Flush();
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Empty(reloaded.Current.OpenTabs!);
        Assert.Equal(-1, reloaded.Current.SelectedTab);
    }

    [Fact]
    public void OpenTabs_AreNormalized_SelectionFollowsItsEntry()
    {
        AppSettings Make(int selected) => new()
        {
            OpenTabs =
            [
                new OpenTabSetting { Kind = " Recent " },
                new OpenTabSetting { Kind = "folder", Root = " ", Path = "x" },    // no root: dropped
                new OpenTabSetting { Kind = "recent", Root = @"E:\古い" },          // a second 最近の履歴: dropped
                null!,
                new OpenTabSetting { Kind = "bogus", Root = @"E:\写真", Path = null! },
                new OpenTabSetting { Kind = null, Root = @"E:\仕事", Path = "2026" },
            ],
            SelectedTab = selected,
        };

        var s = Make(5);
        s.Normalize();
        Assert.Equal(["recent||", @"folder|E:\写真|", @"folder|E:\仕事|2026"], s.OpenTabs!.Select(Describe));
        Assert.Equal(2, s.SelectedTab); // still the same entry, three removals later

        var dropped = Make(1); // the selected entry itself goes: the next one that stays
        dropped.Normalize();
        Assert.Equal(1, dropped.SelectedTab);

        var beyond = Make(99);
        beyond.Normalize();
        Assert.Equal(2, beyond.SelectedTab);
    }

    [Fact]
    public void Version1File_MigratesToOneTabStrip_AndStaysStable()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");

        // Version 1: two fixed tabs with their locations, folder tabs (blank roots dropped before the
        // index is clamped), the id of the former 登録フォルダ tab among the locations.
        File.WriteAllText(file, """
            {"version":1,"last_tab":"folder","sidebar_width":9000,"selected_folder_tab":7,
             "locations":{"registered":{"root":"3f9a1c0b7d2e","path":"2026"},"recent":{"root":"","path":""},"frequent":{"root":"E:\\写真","path":"2026"}},
             "open_tabs":[{"root":"E:\\仕事","path":""},{"root":"","path":"x"},null,{"root":"E:\\写真","path":"2026\\夏"}]}
            """);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(AppSettings.SidebarWidthMax, s.SidebarWidth);
        string[] expected = ["recent||", @"frequent|E:\写真|2026", @"folder|E:\仕事|", @"folder|E:\写真|2026\夏"];
        Assert.Equal(expected, s.OpenTabs!.Select(Describe));
        Assert.Equal(3, s.SelectedTab); // the last folder tab (index 7 clamped)
        Assert.Equal(2, s.Version);
        Assert.Null(s.LastTab);
        Assert.Null(s.Locations);
        Assert.Null(s.SelectedFolderTab);

        // Saved in the new layout; the version 1 keys are gone (a null selected_folder_tab would make
        // the version 1 program reject the file) and a reload changes nothing.
        store.Update(_ => { });
        store.Flush();
        string text = File.ReadAllText(file);
        Assert.Contains("\"version\": 2", text);
        Assert.Contains("\"selected_tab\": 3", text);
        Assert.DoesNotContain("last_tab", text);
        Assert.DoesNotContain("locations", text);
        Assert.DoesNotContain("selected_folder_tab", text);
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(expected, reloaded.Current.OpenTabs!.Select(Describe));
        Assert.Equal(3, reloaded.Current.SelectedTab);

        // Version 1 without any tab key: both fixed tabs, 最近の履歴 shown ("folder" needs a folder tab).
        var bare = new AppSettings { Version = 1, LastTab = AppSettings.TabFolder, OpenTabs = null, Locations = null, SidebarWidth = double.NaN };
        bare.Normalize();
        Assert.Equal(["recent||", "frequent||"], bare.OpenTabs!.Select(Describe));
        Assert.Equal(0, bare.SelectedTab);
        Assert.Equal(AppSettings.SidebarWidthDefault, bare.SidebarWidth);
    }

    [Fact]
    public void FileRewrittenByVersion1_MigratesAgain()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");

        // What the version 1 program leaves after it read a version 2 file with よく使う項目 (inside
        // E:\写真\2026) and one folder tab: the kinds are gone, entries without a root too, and it
        // wrote its own keys back (最近の履歴 shown).
        File.WriteAllText(file, """
            {"version":1,"last_tab":"recent","selected_folder_tab":0,
             "locations":{"recent":{"root":"","path":""},"frequent":{"root":"","path":""}},
             "open_tabs":[{"root":"E:\\写真","path":"2026"},{"root":"E:\\仕事","path":""}]}
            """);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(["recent||", "frequent||", @"folder|E:\写真|2026", @"folder|E:\仕事|"], s.OpenTabs!.Select(Describe));
        Assert.Equal(0, s.SelectedTab);
        Assert.Equal(2, s.Version);

        // The version key alone does not decide: last_tab is only ever written by version 1.
        var mislabeled = new AppSettings { Version = 2, LastTab = AppSettings.TabFrequent, OpenTabs = [new OpenTabSetting { Root = @"E:\仕事" }] };
        mislabeled.Normalize();
        Assert.Equal(["recent||", "frequent||", @"folder|E:\仕事|"], mislabeled.OpenTabs!.Select(Describe));
        Assert.Equal(1, mislabeled.SelectedTab);
    }

    [Fact]
    public void MissingKeys_AreFilled_OutOfRangeValuesClamped()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":1,"card_size":205,"recent_limit":5000,"frequent_limit":1,"thumb_cache_mb":999999,"last_tab":"bogus","recent_cleared_at":"garbage","details_columns":{" Type ":9999,"bogus":1}}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(200, s.CardSize);
        Assert.Equal(500, s.RecentLimit);
        Assert.Equal(10, s.FrequentLimit);
        Assert.Equal(AppSettings.ThumbCacheMbMax, s.ThumbCacheMb);
        Assert.Equal(0, s.SelectedTab); // an unknown last_tab: 最近の履歴
        Assert.Null(s.RecentClearedAt);
        Assert.True(s.ConfirmExecutables);
        Assert.NotNull(s.Window);

        // details_columns: exactly the four known keys, a missing one at its default (a file written
        // before the name had a width loads with the default), a value out of range clamped
        Assert.Equal(["name", "modified", "type", "size"], s.DetailsColumns!.Keys);
        Assert.Equal(DetailsColumns.Defaults[DetailsColumns.Name], s.DetailsColumns[DetailsColumns.Name]);
        Assert.Equal(DetailsColumns.Defaults[DetailsColumns.Modified], s.DetailsColumns[DetailsColumns.Modified]);
        Assert.Equal(DetailsColumns.Max, s.DetailsColumns[DetailsColumns.Type]);
    }

    [Theory]
    [InlineData(100, 120)]
    [InlineData(120, 120)]
    [InlineData(205, 200)]
    [InlineData(211, 220)]
    [InlineData(400, 400)]
    [InlineData(999, 400)]
    public void ClampCardSize(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.ClampCardSize(input));
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 100)]
    [InlineData(113, 110)]
    [InlineData(117, 120)]
    [InlineData(150, 150)]
    [InlineData(151, 150)]
    [InlineData(int.MaxValue, 150)]
    public void ClampTextScale(int input, int expected)
    {
        Assert.Equal(expected, AppSettings.ClampTextScale(input));
    }

    [Fact]
    public void TextScale_OffStepValueIsSnappedOnLoad_AndRoundTrips()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":2,"text_scale":137}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(140, store.Current.TextScale);

        store.Update(s => s.TextScale = 120);
        store.Flush();
        Assert.Contains("\"text_scale\": 120", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(120, reloaded.Current.TextScale);
        Assert.Equal(120, reloaded.Current.Clone().TextScale);
    }

    [Fact]
    public void RoundTrip()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        store.Update(s =>
        {
            s.CardSize = 260;
            s.OpenTabs =
            [
                new OpenTabSetting { Kind = AppSettings.TabFrequent, Root = @"E:\写真", Path = "2026" },
                new OpenTabSetting { Root = @"E:\仕事" },
                new OpenTabSetting { Root = @"E:\写真", Path = @"2026\夏" },
            ];
            s.SelectedTab = 2;
            s.SidebarWidth = 300;
            s.Window = new WindowPlacementSetting { Left = 10, Top = 20, Width = 900, Height = 700, Maximized = true };
            s.RecentClearedAt = FakeClock.Default;
            s.ShowHidden = true;
            s.DetailsColumns = new Dictionary<string, double> { [DetailsColumns.Size] = 120 };
        });
        store.Flush();

        string text = File.ReadAllText(file);
        Assert.Contains("\"card_size\": 260", text);
        Assert.Contains("\"details_columns\"", text);
        Assert.Contains("\"open_tabs\"", text);
        Assert.Contains("\"kind\": \"frequent\"", text);
        Assert.Contains("\"selected_tab\": 2", text);
        Assert.Contains("\"sidebar_width\": 300", text);
        Assert.Contains("\"recent_cleared_at\": \"2026-11-21T01:23:45+00:00\"", text);
        Assert.Contains("\"show_hidden\": true", text);

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        var s = reloaded.Current;
        Assert.Equal(260, s.CardSize);
        Assert.Equal([@"frequent|E:\写真|2026", @"folder|E:\仕事|", @"folder|E:\写真|2026\夏"], s.OpenTabs!.Select(Describe));
        Assert.Equal(2, s.SelectedTab);
        Assert.Equal(300, s.SidebarWidth);
        Assert.Equal(900, s.Window!.Width);
        Assert.True(s.Window.Maximized);
        Assert.Equal(FakeClock.Default, s.RecentClearedAt);
        Assert.Equal(120, s.DetailsColumns![DetailsColumns.Size]);
        Assert.Equal(DetailsColumns.Defaults[DetailsColumns.Type], s.DetailsColumns[DetailsColumns.Type]);
    }

    [Fact]
    public void Current_IsACopy()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(tmp.Combine("settings.json"), FakeClock.Fixed());
        store.Load();
        store.Update(s => s.OpenTabs = [new OpenTabSetting { Root = @"E:\写真" }]);
        var copy = store.Current;
        copy.CardSize = 380;
        copy.OpenTabs![0].Path = "x";
        copy.OpenTabs.Add(new OpenTabSetting { Root = @"E:\仕事" });
        copy.DetailsColumns![DetailsColumns.Size] = 300;
        Assert.Equal(200, store.Current.CardSize);
        Assert.Equal(DetailsColumns.Defaults[DetailsColumns.Size], store.Current.DetailsColumns![DetailsColumns.Size]);
        Assert.Equal(string.Empty, Assert.Single(store.Current.OpenTabs!).Path);
    }

    [Fact]
    public void OpenExplorerInNewTab_DefaultsOff_RoundTrips_AndIsCloned()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.False(store.Current.OpenExplorerInNewTab);

        store.Update(s => s.OpenExplorerInNewTab = true);
        store.Flush();
        Assert.Contains("\"open_explorer_in_new_tab\": true", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.True(reloaded.Current.OpenExplorerInNewTab);

        var copy = reloaded.Current.Clone();
        Assert.True(copy.OpenExplorerInNewTab);
        copy.OpenExplorerInNewTab = false;
        Assert.True(reloaded.Current.OpenExplorerInNewTab);
    }

    [Fact]
    public void OpenOnDoubleClick_DefaultsOff_MissingKeyIsOff_RoundTrips()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":2,"show_hidden":true}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.True(store.Current.ShowHidden);
        Assert.False(store.Current.OpenOnDoubleClick);

        store.Update(s => s.OpenOnDoubleClick = true);
        store.Flush();
        Assert.Contains("\"open_on_double_click\": true", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.True(reloaded.Current.OpenOnDoubleClick);
        Assert.True(reloaded.Current.Clone().OpenOnDoubleClick);
    }

    [Fact]
    public void ItemMenu_DefaultsToApp_RoundTripsWindows_AndIsCloned()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal("app", store.Current.ItemMenu);
        Assert.Equal("app", new AppSettings().ItemMenu);

        store.Update(s => s.ItemMenu = "windows");
        store.Flush();
        Assert.Contains("\"item_menu\": \"windows\"", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal("windows", reloaded.Current.ItemMenu);

        var copy = reloaded.Current.Clone();
        copy.ItemMenu = "app";
        Assert.Equal("windows", reloaded.Current.ItemMenu);
    }

    [Theory]
    [InlineData("""{"version":2,"item_menu":"Explorer"}""", "app")]
    [InlineData("""{"version":2,"item_menu":" WINDOWS "}""", "windows")]
    [InlineData("""{"version":2}""", "app")]
    public void ItemMenu_UnknownOrMissing_IsApp_CaseNormalized(string json, string expected)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, json);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(expected, store.Current.ItemMenu);
    }

    [Fact]
    public void OpenExplorerInNewTab_MissingKey_IsOff()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":1,"add_to_windows_recent":true}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.True(store.Current.AddToWindowsRecent);
        Assert.False(store.Current.OpenExplorerInNewTab);
    }

    [Fact]
    public void Sidebar_CollapsedStateRoundTrips_SectionIdsAreNormalized()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");

        // Hand-edited: unknown and repeated ids go, the spelling is forgiven.
        File.WriteAllText(file, """{"sidebar_collapsed":true,"sidebar_collapsed_sections":["pc"," Quick ","bogus",null,"quick"]}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.True(store.Current.SidebarCollapsed);
        Assert.Equal([SidebarSections.Quick, SidebarSections.Pc], store.Current.SidebarCollapsedSections);
        Assert.True(store.Current.IsSectionCollapsed(SidebarSections.Quick));
        Assert.False(store.Current.IsSectionCollapsed(SidebarSections.Registered));

        store.Update(s =>
        {
            s.SidebarCollapsed = false;
            s.SetSectionCollapsed(SidebarSections.Quick, false);
            s.SetSectionCollapsed(SidebarSections.Registered, true);
        });
        store.Flush();
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.False(reloaded.Current.SidebarCollapsed);
        Assert.Equal([SidebarSections.Registered, SidebarSections.Pc], reloaded.Current.SidebarCollapsedSections);

        // A file without the keys: the sidebar is shown and every section is open.
        var fresh = new AppSettings();
        fresh.Normalize();
        Assert.False(fresh.SidebarCollapsed);
        Assert.Empty(fresh.SidebarCollapsedSections!);
    }

    [Fact]
    public void DataStores_LoadAll_CreatesFolder()
    {
        using var tmp = new TempDir();
        string dir = tmp.Combine("data");
        var stores = new DataStores(dir, FakeClock.Fixed());
        stores.LoadAll();
        Assert.True(Directory.Exists(dir));
        Assert.All(stores.All, s => Assert.Equal(LoadStatus.Missing, s.LoadStatus));
        stores.Settings.Update(s => s.CardSize = 300);
        stores.FlushAll();
        Assert.True(File.Exists(Path.Combine(dir, "settings.json")));
    }
}
