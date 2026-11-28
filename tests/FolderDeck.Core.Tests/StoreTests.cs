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

public class RegisteredFoldersFollowTests
{
    [Fact]
    public void Repath_MovesTheFolderAndTheOnesBelow_KeepsIdsOrderAndIdentity()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_folders.json");
        string a = tmp.MakeDir("A");
        string below = tmp.MakeDir("A", "below");
        string taken = tmp.MakeDir("A", "taken");
        string other = tmp.MakeDir("AB");
        string newTaken = tmp.MakeDir("A2", "taken");
        var store = new RegisteredFoldersStore(file, FakeClock.Fixed());
        store.Load();
        store.RegisterMany([below, a, other, taken, newTaken], RealFileSystem.Instance);
        var before = store.Folders;
        Assert.True(store.SetFileId(before[1].Id, "v1:a"));
        Assert.False(store.SetFileId(before[1].Id, "v1:a")); // unchanged: no save

        Assert.Equal(2, store.Repath(a, tmp.Combine("A2")));
        store.Flush();

        var reloaded = new RegisteredFoldersStore(file, FakeClock.Fixed());
        reloaded.Load();
        var after = reloaded.Folders;
        Assert.Equal(before.Select(f => f.Id), after.Select(f => f.Id));
        Assert.Equal(
            [tmp.Combine("A2", "below"), tmp.Combine("A2"), other, taken, newTaken], // "taken" is registered at its new path already: it stays
            after.Select(f => f.Path));
        Assert.Equal("v1:a", after[1].FileId);
        Assert.Null(after[0].FileId);
        Assert.Single(Regex.Matches(File.ReadAllText(file), "file_id")); // unknown ids are not written
    }
}

public class RegisteredPanesStoreTests
{
    private static PaneNodeSetting Split(string split, PaneNodeSetting first, PaneNodeSetting second, double? ratio = null) =>
        new() { Split = split, Ratio = ratio, First = first, Second = second };

    private static PaneNodeSetting Folder(string root, string path = "") =>
        new() { Tab = new OpenTabSetting { Kind = AppSettings.TabFolder, Root = root, Path = path } };

    private static PaneNodeSetting Recent() => new() { Tab = new OpenTabSetting { Kind = AppSettings.TabRecent } };

    private static string Describe(PaneNodeSetting node) => node.Split is not null
        ? $"({Describe(node.First!)} {(node.Split == "columns" ? "|" : "/")} {Describe(node.Second!)})"
        : node.Tab is { } tab ? $"{tab.Kind}:{tab.Root}:{tab.Path}" : "_";

    [Fact]
    public void Panes_RoundTripWithTabsAndEmptyPanes_AndAreHandedOutAsCopies()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_panes.json");
        var clock = FakeClock.Fixed();
        var store = new RegisteredPanesStore(file, clock);
        store.Load();
        var work = Split("columns", Folder(@"E:\仕事", "2026"), Split("rows", PaneNodeSetting.EmptyLeaf(), Recent()), 0.3);
        work.First!.Tab!.FileId = "v1:a";
        work.First.Tab.CardShape = " DETAILS ";
        work.First.Tab.Kind = " Folder ";
        string id = store.Add("  仕事  ", work, 2);
        work.First.Tab.Root = @"E:\changed"; // the store keeps its own copy
        clock.UtcNow += TimeSpan.FromMinutes(1);
        string photos = store.Add("写真", Split("rows", Folder(@"E:\写真"), PaneNodeSetting.EmptyLeaf()), 7);
        store.Flush();

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal(1, (int)json["version"]!);
        var first = json["panes"]![0]!;
        Assert.Equal(["id", "name", "added_at", "updated_at", "layout", "active"], first.AsObject().Select(kv => kv.Key));
        Assert.Equal(["empty"], first["layout"]!["second"]!["first"]!.AsObject().Select(kv => kv.Key));

        var reloaded = new RegisteredPanesStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(LoadStatus.Loaded, reloaded.LoadStatus);
        var panes = reloaded.Panes;
        Assert.Equal([id, photos], panes.Select(p => p.Id));
        Assert.Equal(["仕事", "写真"], panes.Select(p => p.Name));
        Assert.Equal([2, 0], panes.Select(p => p.Active)); // out of range: the first pane
        Assert.Equal(@"(folder:E:\仕事:2026 | (_ / recent::))", Describe(panes[0].Layout));
        Assert.Equal(0.3, panes[0].Layout.Ratio);
        Assert.Equal("v1:a", panes[0].Layout.First!.Tab!.FileId);
        Assert.Equal("details", panes[0].Layout.First!.Tab!.CardShape);
        Assert.Equal([JsonFile.FormatTime(FakeClock.Default), JsonFile.FormatTime(FakeClock.Default.AddMinutes(1))], panes.Select(p => p.AddedAt));
        Assert.Equal(panes.Select(p => p.AddedAt), panes.Select(p => p.UpdatedAt));

        // Copies: changing what was handed out does not change the store.
        panes[0].Layout.First!.Tab!.Root = @"E:\other";
        reloaded.Find(id)!.Layout.Ratio = 0.9;
        Assert.Equal(@"(folder:E:\仕事:2026 | (_ / recent::))", Describe(reloaded.Find(id)!.Layout));
        Assert.Equal(0.3, reloaded.Find(id)!.Layout.Ratio);
        Assert.Null(reloaded.Find("bogus"));

        // A blank name, a layout without a tab.
        Assert.Throws<ArgumentException>(() => store.Add(" ", work, 0));
        Assert.Throws<ArgumentException>(() => store.Add("x", Split("rows", PaneNodeSetting.EmptyLeaf(), PaneNodeSetting.EmptyLeaf()), 0));
    }

    [Fact]
    public void Load_SkipsInvalidEntries()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_panes.json");
        string ok = """{"split":"columns","first":{"tab":{"root":"E:\\a","path":""}},"second":{"empty":true}}""";
        File.WriteAllText(file, """
            {"version":1,"panes":[
              {"id":"a1","name":"ok","layout":OK_LAYOUT,"active":1},
              null,
              {"id":" ","name":"blank id","layout":OK_LAYOUT},
              {"id":"a1","name":"same id","layout":OK_LAYOUT},
              {"id":"b1","name":" ","layout":OK_LAYOUT},
              {"id":"c1","name":"no layout"},
              {"id":"c2","name":"one pane","layout":{"tab":{"kind":"recent"}}},
              {"id":"c3","name":"empty panes only","layout":{"split":"rows","first":{"empty":true},"second":{"empty":true}}},
              {"id":"c4","name":"folder without root","layout":{"split":"rows","first":{"tab":{"kind":"folder","root":" "}},"second":{"empty":true}}},
              {"id":"c5","name":"climbs out","layout":{"split":"rows","first":{"tab":{"root":"E:\\a","path":"..\\b"}},"second":{"empty":true}}},
              {"id":"c6","name":"unknown kind","layout":{"split":"rows","first":{"tab":{"kind":"bogus","root":"E:\\a"}},"second":{"empty":true}}},
              {"id":"c7","name":"recent twice","layout":{"split":"rows","first":{"tab":{"kind":"recent"}},"second":{"tab":{"kind":"recent"}}}},
              {"id":"c8","name":"tab and empty","layout":{"split":"rows","first":{"tab":{"kind":"recent"},"empty":true},"second":{"tab":{"kind":"frequent"}}}},
              {"id":"c9","name":"an index","layout":{"split":"rows","first":{"pane":0},"second":{"tab":{"kind":"frequent"}}}}
            ]}
            """.Replace("OK_LAYOUT", ok));
        var store = new RegisteredPanesStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        var pane = Assert.Single(store.Panes);
        Assert.Equal("a1", pane.Id);
        Assert.Equal(1, pane.Active);
        Assert.Equal(@"(folder:E:\a: | _)", Describe(pane.Layout)); // a tab without a kind is a folder tab
        Assert.Equal(string.Empty, pane.UpdatedAt);
    }

    [Fact]
    public void Overwrite_Rename_Remove_Reorder_SaveOnlyWhenSomethingChanged()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_panes.json");
        var clock = FakeClock.Fixed();
        var store = new RegisteredPanesStore(file, clock);
        store.Load();
        var layout = Split("columns", Folder(@"E:\a"), Recent());
        string a = store.Add("A", layout, 0);
        string b = store.Add("B", layout, 1);
        store.Flush();

        // The same content (written differently) and the same name: true, nothing to save.
        File.Delete(file);
        clock.UtcNow += TimeSpan.FromHours(1);
        Assert.True(store.Overwrite(a, Split(" COLUMNS ", Folder(@"E:\a"), Recent(), 0.5), 0));
        Assert.True(store.Rename(a, " A "));
        store.Flush();
        Assert.False(File.Exists(file));
        Assert.Equal(store.Find(a)!.AddedAt, store.Find(a)!.UpdatedAt);

        Assert.True(store.Overwrite(a, Split("rows", PaneNodeSetting.EmptyLeaf(), Folder(@"E:\b")), 1));
        Assert.True(store.Rename(b, "  B2 "));
        Assert.False(store.Rename(b, " "));
        Assert.False(store.Rename("bogus", "x"));
        Assert.False(store.Overwrite("bogus", layout, 0));
        Assert.Throws<ArgumentException>(() => store.Overwrite(a, Folder(@"E:\a"), 0)); // one pane
        store.Reorder([b, "bogus"]);
        store.Flush();

        var reloaded = new RegisteredPanesStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal([b, a], reloaded.Panes.Select(p => p.Id));
        Assert.Equal(["B2", "A"], reloaded.Panes.Select(p => p.Name));
        var overwritten = reloaded.Find(a)!;
        Assert.Equal(@"(_ / folder:E:\b:)", Describe(overwritten.Layout));
        Assert.Equal(1, overwritten.Active);
        Assert.Equal(JsonFile.FormatTime(FakeClock.Default.AddHours(1)), overwritten.UpdatedAt);
        Assert.Equal(JsonFile.FormatTime(FakeClock.Default), overwritten.AddedAt);

        Assert.True(reloaded.Remove(a));
        Assert.False(reloaded.Remove(a));
        Assert.Equal([b], reloaded.Panes.Select(p => p.Id));
    }

    [Fact]
    public void Repath_MovesTheTabLeavesAtOrBelowTheFolder()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("registered_panes.json");
        var store = new RegisteredPanesStore(file, FakeClock.Fixed());
        store.Load();
        string moved = store.Add("moved", Split("columns", Folder(@"E:\A", @"below\x"), Split("rows", Folder(@"E:\P", @"A\c"), Recent())), 0);
        string other = store.Add("other", Split("columns", Folder(@"E:\AB"), PaneNodeSetting.EmptyLeaf()), 0);
        store.Flush();

        Assert.True(store.Repath(@"E:\A", @"E:\A2"));
        Assert.True(store.Repath(@"E:\P\A", @"E:\P\Z"));
        Assert.False(store.Repath(@"E:\nothing", @"E:\else"));
        store.Flush();

        var reloaded = new RegisteredPanesStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(@"(folder:E:\A2:below\x | (folder:E:\P:Z\c / recent::))", Describe(reloaded.Find(moved)!.Layout));
        Assert.Equal(@"(folder:E:\AB: | _)", Describe(reloaded.Find(other)!.Layout));
        Assert.Equal(reloaded.Find(moved)!.AddedAt, reloaded.Find(moved)!.UpdatedAt);
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
        Assert.False(s.ShowDotItems);
        Assert.False(s.AddToWindowsRecent);
        Assert.Equal(100, s.RecentLimit);
        Assert.Equal(50, s.FrequentLimit);
        Assert.Equal(512, s.ThumbCacheMb);
        Assert.Null(s.RecentClearedAt);
        Assert.Equal(AppSettings.SidebarWidthDefault, s.SidebarWidth);
        Assert.True(s.AutoRefresh);
        Assert.Equal(5, s.AutoRefreshSeconds);
        Assert.True(s.OpenOnDoubleClick);
        Assert.Equal("top", s.MessagePosition);
        Assert.Empty(s.PaneGroups!);
        Assert.Equal(-1, s.ShownGroup);
        Assert.False(s.AllowDuplicateTabs);
        Assert.False(s.PaneAutoRecord);
        Assert.True(s.PaneReuseOpenTabs);
        Assert.False(s.PaneCloseClosesTab);
        Assert.False(s.DetailsCascadeOnNameColumn);
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
    public void OpenTabs_KeepTheFolderIdentity()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        store.Update(s => s.OpenTabs =
        [
            new OpenTabSetting { Kind = "folder", Root = @"E:\仕事", Path = "2026", FileId = "v1:x" },
            new OpenTabSetting { Kind = "recent" },
        ]);
        store.Flush();

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(["v1:x", null], reloaded.Current.OpenTabs!.Select(t => t.FileId));
        Assert.Equal("v1:x", reloaded.Current.Clone().OpenTabs![0].FileId);
        Assert.Single(Regex.Matches(File.ReadAllText(file), "file_id"));
    }

    [Fact]
    public void OpenTabs_WithoutViewState_TakeTheSharedShapeAndTheirKindsSort()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """
            {"version":2,"card_shape":"details","sort_modes":{"recent":"name_asc","folder":"modified_desc"},
             "open_tabs":[{"kind":"recent","root":"","path":""},{"kind":"folder","root":"E:\\仕事","path":""}],
             "selected_tab":1}
            """);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(["details", "details"], s.OpenTabs!.Select(t => t.CardShape));
        Assert.Equal(["name_asc", "modified_desc"], s.OpenTabs!.Select(t => t.SortMode));
        Assert.Equal("modified_desc", s.LastSortMode); // the selected tab's
    }

    [Fact]
    public void OpenTabs_ViewState_IsKeptPerTab_AndNormalized()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        store.Update(s =>
        {
            s.CardShape = "square";
            s.OpenTabs =
            [
                new OpenTabSetting { Kind = AppSettings.TabRecent, CardShape = "DETAILS", SortMode = "size_desc" },
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\写真", CardShape = "bogus", SortMode = "bogus" },
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\仕事", CardShape = " " },
            ];
        });
        store.Flush();

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        var tabs = reloaded.Current.Clone().OpenTabs!; // Clone keeps them too
        Assert.Equal(["details", "portrait", "square"], tabs.Select(t => t.CardShape));
        Assert.Equal(["size_desc", "manual", "manual"], tabs.Select(t => t.SortMode));
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
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.Null(s.LastTab);
        Assert.Null(s.Locations);
        Assert.Null(s.SelectedFolderTab);

        // Saved in the new layout; the version 1 keys are gone (a null selected_folder_tab would make
        // the version 1 program reject the file) and a reload changes nothing.
        store.Update(_ => { });
        store.Flush();
        string text = File.ReadAllText(file);
        Assert.Contains("\"version\": 3", text);
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
        Assert.Equal(AppSettings.CurrentVersion, s.Version);

        // The version key alone does not decide: last_tab is only ever written by version 1.
        var mislabeled = new AppSettings { Version = 2, LastTab = AppSettings.TabFrequent, OpenTabs = [new OpenTabSetting { Root = @"E:\仕事" }] };
        mislabeled.Normalize();
        Assert.Equal(["recent||", "frequent||", @"folder|E:\仕事|"], mislabeled.OpenTabs!.Select(Describe));
        Assert.Equal(1, mislabeled.SelectedTab);
    }

    [Fact]
    public void Version2File_LoadsUnsplit_WithoutTheVersion1Migration_AndIsWrittenAsVersion3()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");

        // No よく使う項目 and 最近の履歴 not first: the version 1 migration would add and reorder tabs.
        File.WriteAllText(file, """
            {"version":2,"selected_tab":2,
             "open_tabs":[{"kind":"folder","root":"E:\\仕事","path":"2026","file_id":"v1:a","card_shape":"details","sort_mode":"name_desc"},
                          {"kind":"recent","root":"","path":"","card_shape":"portrait","sort_mode":"manual"},
                          {"kind":"folder","root":"E:\\写真","path":"","card_shape":"square","sort_mode":"size_asc"}]}
            """);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        string[] expected = [@"folder|E:\仕事|2026", "recent||", @"folder|E:\写真|"];
        Assert.Equal(expected, s.OpenTabs!.Select(Describe));
        Assert.Equal(2, s.SelectedTab);
        Assert.Equal(["details", "portrait", "square"], s.OpenTabs!.Select(t => t.CardShape));
        Assert.Equal("v1:a", s.OpenTabs![0].FileId);
        Assert.Empty(s.PaneGroups!);
        Assert.Equal(-1, s.ShownGroup);

        store.Update(_ => { });
        store.Flush();
        string text = File.ReadAllText(file);
        Assert.Contains("\"version\": 3", text);
        Assert.DoesNotContain("split_layout", text);
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal(expected, reloaded.Current.OpenTabs!.Select(Describe));
        Assert.Equal(2, reloaded.Current.SelectedTab);
    }

    /// <summary>
    /// The leaves of a tree as text: "(first | second)" for columns, "(first / second)" for rows, "_" for an
    /// empty pane; "none" for no tree.
    /// </summary>
    private static string Leaves(PaneNodeSetting? node) => node is null
        ? "none"
        : node.Split is null
            ? node.Empty == true ? "_" : $"{node.Pane}"
            : $"({Leaves(node.First)} {(node.Split == "columns" ? "|" : "/")} {Leaves(node.Second)})";

    private static PaneNodeSetting Split(string split, PaneNodeSetting first, PaneNodeSetting second, double? ratio = null) =>
        new() { Split = split, Ratio = ratio, First = first, Second = second };

    private static PaneNodeSetting Tab(int index) => new() { Pane = index };

    private static PaneNodeSetting Empty() => PaneNodeSetting.EmptyLeaf();

    /// <summary>Five tabs, tab 4 selected, as 0.8.0 wrote a split of three of them: 2 | (0 / 4) at 0.3 and 0.6.</summary>
    private static AppSettings SplitWindow() => new()
    {
        OpenTabs =
        [
            new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\a" },
            new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\b" },
            new OpenTabSetting { Kind = AppSettings.TabRecent },
            new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\d" },
            new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\e", Path = "x" },
        ],
        SelectedTab = 4,
        SplitLayout = Split("columns", Tab(2), Split("rows", Tab(0), Tab(4), 0.6), 0.3),
    };

    private static readonly string[] SplitWindowTabs = [@"folder|E:\a|", @"folder|E:\b|", "recent||", @"folder|E:\d|", @"folder|E:\e|x"];

    [Fact]
    public void PaneGroups_RoundTrip_WithEmptyPanes_TheShownGroupAndARegisteredPane_AndAnOlderExeOpensTheSelectedTabAlone()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var window = SplitWindow();
        store.Update(s =>
        {
            s.OpenTabs = window.OpenTabs;
            s.SelectedTab = window.SelectedTab;
            s.PaneGroups =
            [
                new PaneGroupSetting { Layout = window.SplitLayout, Active = 2 },
                new PaneGroupSetting { Layout = Split("rows", Empty(), Tab(3)), Active = 0, RegisteredPane = "3f9a1c0b7d2e" },
            ];
            s.ShownGroup = 1;
        });
        store.Flush();

        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Null(json["split_layout"]);
        Assert.Equal(1, (int)json["shown_group"]!);
        var groups = json["pane_groups"]!.AsArray();
        Assert.Equal(["layout", "active"], groups[0]!.AsObject().Select(kv => kv.Key)); // no registered_pane when there is none
        Assert.Equal(["empty"], groups[1]!["layout"]!["first"]!.AsObject().Select(kv => kv.Key));
        // Every entry is a plain tab, as version 2 wrote it.
        Assert.All(json["open_tabs"]!.AsArray(), e => Assert.Equal(
            ["kind", "root", "path", "card_shape", "sort_mode"],
            e!.AsObject().Select(kv => kv.Key).Where(k => k != "file_id")));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        var s = reloaded.Current;
        Assert.Equal(SplitWindowTabs, s.OpenTabs!.Select(Describe));
        Assert.Equal(4, s.SelectedTab);
        Assert.Equal(["(2 | (0 / 4))", "(_ / 3)"], s.PaneGroups!.Select(g => Leaves(g.Layout)));
        Assert.Equal([2, 0], s.PaneGroups!.Select(g => g.Active));
        Assert.Equal([null, "3f9a1c0b7d2e"], s.PaneGroups!.Select(g => g.RegisteredPane));
        Assert.Equal(1, s.ShownGroup);
        var first = s.PaneGroups![0].Layout!;
        Assert.Equal(0.3, first.Ratio);
        Assert.Equal(0.6, first.Second!.Ratio);
        Assert.Null(first.First!.Ratio); // leaves carry only their index

        // The copies are deep: changing one does not change the store.
        first.Ratio = 0.9;
        first.Second.First!.Pane = 1;
        s.PaneGroups[1].Layout!.First!.Empty = null;
        Assert.Equal(0.3, reloaded.Current.PaneGroups![0].Layout!.Ratio);
        Assert.Equal(["(2 | (0 / 4))", "(_ / 3)"], reloaded.Current.Clone().PaneGroups!.Select(g => Leaves(g.Layout)));

        // An exe without the groups drops the keys and writes its version: the same tabs, unsplit.
        json.AsObject().Remove("pane_groups");
        json.AsObject().Remove("shown_group");
        json["version"] = 2;
        File.WriteAllText(file, json.ToJsonString());
        var downgraded = new SettingsStore(file, FakeClock.Fixed());
        downgraded.Load();
        Assert.Equal(SplitWindowTabs, downgraded.Current.OpenTabs!.Select(Describe));
        Assert.Equal(4, downgraded.Current.SelectedTab);
        Assert.Empty(downgraded.Current.PaneGroups!);
        Assert.Equal(-1, downgraded.Current.ShownGroup);
    }

    [Fact]
    public void SplitLayout_Of080_IsMigratedIntoOneShownGroup_AndNoLongerWritten()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """
            {"version":3,"selected_tab":4,
             "open_tabs":[{"kind":"folder","root":"E:\\a","path":""},{"kind":"folder","root":"E:\\b","path":""},{"kind":"recent","root":"","path":""},
                          {"kind":"folder","root":"E:\\d","path":""},{"kind":"folder","root":"E:\\e","path":"x"}],
             "split_layout":{"split":"columns","ratio":0.3,"first":{"pane":2},"second":{"split":"rows","ratio":0.6,"first":{"pane":0},"second":{"pane":4}}}}
            """);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Null(s.SplitLayout);
        Assert.Equal(SplitWindowTabs, s.OpenTabs!.Select(Describe));
        Assert.Equal(4, s.SelectedTab);
        var group = Assert.Single(s.PaneGroups!);
        Assert.Equal("(2 | (0 / 4))", Leaves(group.Layout));
        Assert.Equal(0.3, group.Layout!.Ratio);
        Assert.Equal(2, group.Active); // the selected tab's pane: the third leaf in reading order
        Assert.Null(group.RegisteredPane);
        Assert.Equal(0, s.ShownGroup);

        store.Update(_ => { });
        store.Flush();
        string text = File.ReadAllText(file);
        Assert.DoesNotContain("split_layout", text);
        Assert.Contains("pane_groups", text);
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.Equal("(2 | (0 / 4))", Leaves(Assert.Single(reloaded.Current.PaneGroups!).Layout));
        Assert.Equal(0, reloaded.Current.ShownGroup);

        // pane_groups present (an exe of this layout wrote it): split_layout is not read.
        var both = SplitWindow();
        both.PaneGroups = [];
        both.Normalize();
        Assert.Empty(both.PaneGroups);
        Assert.Null(both.SplitLayout);

        // 0.8.0 never wrote a split without the selected tab: the group is kept, the tab shown alone.
        var unshown = SplitWindow();
        unshown.SelectedTab = 1;
        unshown.Normalize();
        Assert.Equal("(2 | (0 / 4))", Leaves(Assert.Single(unshown.PaneGroups!).Layout));
        Assert.Equal(0, unshown.PaneGroups![0].Active);
        Assert.Equal(-1, unshown.ShownGroup);
    }

    public static TheoryData<string> InvalidSplitLayouts => new()
    {
        "one pane",
        "five panes",
        "an index twice",
        "an index out of range",
        "an unknown split",
        "a split without its second part",
        "a leaf with children",
    };

    [Theory]
    [MemberData(nameof(InvalidSplitLayouts))]
    public void SplitLayout_Invalid_IsNotMigrated(string broken)
    {
        var s = SplitWindow();
        switch (broken)
        {
            case "one pane":
                s.SplitLayout = Tab(4);
                break;
            case "five panes":
                s.SplitLayout = Split("columns", Split("rows", Tab(0), Tab(1)), Split("rows", Tab(2), Split("columns", Tab(3), Tab(4))));
                break;
            case "an index twice":
                s.SplitLayout!.First!.Pane = 4;
                break;
            case "an index out of range":
                s.SplitLayout!.First!.Pane = 5;
                break;
            case "an unknown split":
                s.SplitLayout!.Second!.Split = "diagonal";
                break;
            case "a split without its second part":
                s.SplitLayout!.Second!.Second = null;
                break;
            case "a leaf with children":
                s.SplitLayout!.First!.First = Tab(1);
                break;
        }

        int selected = s.SelectedTab;
        s.Normalize();
        Assert.Null(s.SplitLayout);
        Assert.Empty(s.PaneGroups!);
        Assert.Equal(-1, s.ShownGroup);
        Assert.Equal(SplitWindowTabs, s.OpenTabs!.Select(Describe)); // the tabs stay as they are
        Assert.Equal(selected, s.SelectedTab);
    }

    [Fact]
    public void PaneGroups_FollowTheirTabs_ADroppedOrRepeatedTabLeavesAnEmptyPane_AndAreWrittenCanonically()
    {
        AppSettings Make(int shownGroup) => new()
        {
            OpenTabs =
            [
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\a" },  // 0 → 0
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = " " },      // 1: dropped
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\b" },  // 2 → 1
                new OpenTabSetting { Kind = AppSettings.TabRecent },                  // 3 → 2
                null!,                                                                // 4: dropped
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\e" },  // 5 → 3
                new OpenTabSetting { Kind = AppSettings.TabRecent },                  // 6: a second 最近の履歴, dropped
                new OpenTabSetting { Kind = AppSettings.TabFolder, Root = @"E:\g" },  // 7 → 4
            ],
            SelectedTab = 5,
            PaneGroups =
            [
                new PaneGroupSetting { Layout = Split("columns", Tab(3), Split(" ROWS ", Tab(0), Tab(5), double.NaN), 3), Active = 9, RegisteredPane = " " },
                new PaneGroupSetting { Layout = Split("rows", Tab(1), Tab(2)), Active = 1, RegisteredPane = "b" },
                new PaneGroupSetting { Layout = Split("rows", Tab(6), Tab(0)) }, // 6 dropped, 0 in the first group: no tab left
                null!,
                new PaneGroupSetting { Layout = Split("columns", Split("rows", Tab(7), Tab(7)), Empty()), Active = -1 },
            ],
            ShownGroup = shownGroup,
        };

        var s = Make(4);
        s.Normalize();
        Assert.Equal([@"folder|E:\a|", @"folder|E:\b|", "recent||", @"folder|E:\e|", @"folder|E:\g|"], s.OpenTabs!.Select(Describe));
        Assert.Equal(["(2 | (0 / 3))", "(_ / 1)", "((4 / _) | _)"], s.PaneGroups!.Select(g => Leaves(g.Layout)));
        Assert.Equal([0, 1, 0], s.PaneGroups!.Select(g => g.Active)); // out of range: the first pane
        Assert.Equal([null, "b", null], s.PaneGroups!.Select(g => g.RegisteredPane));
        Assert.Equal(2, s.ShownGroup); // follows its group past the two dropped before it
        var layout = s.PaneGroups![0].Layout!;
        Assert.Equal("rows", layout.Second!.Split);
        Assert.Equal(PaneRules.MaxRatio, layout.Ratio);
        Assert.Equal(PaneRules.DefaultRatio, layout.Second.Ratio);
        Assert.Null(s.PaneGroups[1].Layout!.First!.Pane); // an empty pane holds nothing else

        var shownDropped = Make(2);
        shownDropped.Normalize();
        Assert.Equal(-1, shownDropped.ShownGroup);
        var shownBeyond = Make(9);
        shownBeyond.Normalize();
        Assert.Equal(-1, shownBeyond.ShownGroup);
    }

    public static TheoryData<string> GroupsOfTheWrongShape => new()
    {
        """{"pane":0}""",                                                                                   // one pane
        """{"split":"columns","first":{"empty":true},"second":{"empty":true}}""",                          // no tab
        """{"split":"columns","first":{"pane":0,"empty":true},"second":{"pane":1}}""",                     // a leaf that is both
        """{"split":"columns","first":{"tab":{"kind":"recent"}},"second":{"pane":1}}""",                    // a tab of registered_panes.json
        """{"split":"columns","first":{},"second":{"pane":1}}""",                                           // a leaf of nothing
        """{"split":"diagonal","first":{"pane":0},"second":{"pane":1}}""",                                  // unknown split
        """{"split":"rows","first":{"pane":0}}""",                                                          // a child missing
        """{"split":"rows","empty":true,"first":{"pane":0},"second":{"pane":1}}""",                         // a split that is a leaf too
        """{"split":"rows","first":{"pane":0},"second":{"split":"rows","first":{"pane":1},"second":{"split":"rows","first":{"empty":true},"second":{"split":"rows","first":{"pane":2},"second":{"empty":true}}}}}""", // five panes
    };

    [Theory]
    [MemberData(nameof(GroupsOfTheWrongShape))]
    public void PaneGroups_OfTheWrongShape_AreDropped(string layout)
    {
        var s = SplitWindow();
        s.SplitLayout = null;
        s.PaneGroups = [new PaneGroupSetting { Layout = System.Text.Json.JsonSerializer.Deserialize<PaneNodeSetting>(layout, JsonFile.Options) }];
        s.ShownGroup = 0;
        s.Normalize();
        Assert.Empty(s.PaneGroups);
        Assert.Equal(-1, s.ShownGroup);
        Assert.Equal(SplitWindowTabs, s.OpenTabs!.Select(Describe));
    }

    [Fact]
    public void PaneGroups_KeepTheirFolderTabs_WhenTheCapOfFolderTabsDropsOthers()
    {
        // 51 folder tabs (the last ones in a group): the cap drops one folder tab, not one of a group.
        var tabs = Enumerable.Range(0, AppSettings.OpenTabsMax + 1)
            .Select(i => new OpenTabSetting { Kind = AppSettings.TabFolder, Root = $@"E:\f{i}" })
            .ToList();
        var s = new AppSettings
        {
            OpenTabs = tabs,
            SelectedTab = AppSettings.OpenTabsMax,
            PaneGroups = [new PaneGroupSetting { Layout = Split("rows", Tab(AppSettings.OpenTabsMax - 1), Split("columns", Tab(AppSettings.OpenTabsMax), Empty())) }],
            ShownGroup = 0,
        };
        s.Normalize();
        Assert.Equal(AppSettings.OpenTabsMax, s.OpenTabs!.Count);
        Assert.Equal($@"folder|E:\f{AppSettings.OpenTabsMax}|", Describe(s.OpenTabs[s.SelectedTab]));
        Assert.Equal($"({AppSettings.OpenTabsMax - 2} / ({AppSettings.OpenTabsMax - 1} | _))", Leaves(Assert.Single(s.PaneGroups!).Layout));
        Assert.Equal(@"folder|E:\f47|", Describe(s.OpenTabs[^3])); // the one dropped is the rightmost tab not in a group (f48)
        Assert.DoesNotContain(@"folder|E:\f48|", s.OpenTabs.Select(Describe));
    }

    [Fact]
    public void MissingKeys_AreFilled_OutOfRangeValuesClamped()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":1,"card_size":205,"recent_limit":5000,"frequent_limit":1,"thumb_cache_mb":999999,"auto_refresh_seconds":0,"last_tab":"bogus","recent_cleared_at":"garbage","details_columns":{" Type ":9999,"bogus":1}}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        var s = store.Current;
        Assert.Equal(LoadStatus.Loaded, store.LoadStatus);
        Assert.Equal(200, s.CardSize);
        Assert.Equal(500, s.RecentLimit);
        Assert.Equal(10, s.FrequentLimit);
        Assert.Equal(AppSettings.ThumbCacheMbMax, s.ThumbCacheMb);
        Assert.Equal(AppSettings.AutoRefreshSecondsMin, s.AutoRefreshSeconds);
        Assert.Equal(0, s.SelectedTab); // an unknown last_tab: 最近の履歴
        Assert.Null(s.RecentClearedAt);
        Assert.True(s.ConfirmExecutables);
        Assert.NotNull(s.Window);

        // details_columns: exactly the four known keys, a missing one at its default (a file written
        // before the name had a width loads with the default), a value out of range clamped
        Assert.Equal(["name", "modified", "type", "size", .. DetailsPropertyColumns.All.Select(c => c.Id)], s.DetailsColumns!.Keys);
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
            s.ShowDotItems = true;
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
        Assert.Contains("\"show_dot_items\": true", text);

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
    public void OpenOnDoubleClick_DefaultsOn_MissingKeyIsOn_SavedOffStaysOff()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, """{"version":2,"show_hidden":true}""");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.True(store.Current.ShowHidden);
        Assert.True(store.Current.OpenOnDoubleClick);

        store.Update(s => s.OpenOnDoubleClick = false);
        store.Flush();
        Assert.Contains("\"open_on_double_click\": false", File.ReadAllText(file));

        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.False(reloaded.Current.OpenOnDoubleClick);
        Assert.False(reloaded.Current.Clone().OpenOnDoubleClick);
    }

    [Fact]
    public void AutoRefresh_RoundTrips_AndKeepsTheIntervalWhileOff()
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();

        store.Update(s =>
        {
            s.AutoRefresh = true;
            s.AutoRefreshSeconds = 30;
        });
        store.Flush();
        string text = File.ReadAllText(file);
        Assert.Contains("\"auto_refresh\": true", text);
        Assert.Contains("\"auto_refresh_seconds\": 30", text);

        store.Update(s => s.AutoRefresh = false);
        store.Flush();
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.False(reloaded.Current.AutoRefresh);
        Assert.Equal(30, reloaded.Current.Clone().AutoRefreshSeconds);
    }

    [Theory]
    [InlineData("""{"version":2,"show_hidden":true}""", true)]
    [InlineData("""{"version":2,"auto_refresh":false}""", false)]
    public void AutoRefresh_MissingKeyInAnOldFileIsOn_SavedOffStaysOff(string json, bool expected)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, json);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(expected, store.Current.AutoRefresh);
    }

    [Theory]
    [InlineData("""{"version":2}""", "top")]
    [InlineData("""{"version":2,"message_position":" BOTTOM "}""", "bottom")]
    [InlineData("""{"version":2,"message_position":"off"}""", "off")]
    [InlineData("""{"version":2,"message_position":"middle"}""", "top")]
    public void MessagePosition_UnknownOrMissing_IsTop_CaseNormalized(string json, string expected)
    {
        using var tmp = new TempDir();
        string file = tmp.Combine("settings.json");
        File.WriteAllText(file, json);
        var store = new SettingsStore(file, FakeClock.Fixed());
        store.Load();
        Assert.Equal(expected, store.Current.MessagePosition);

        store.Update(s => s.MessagePosition = "bottom");
        store.Flush();
        Assert.Contains("\"message_position\": \"bottom\"", File.ReadAllText(file));
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
            s.SetSectionCollapsed(SidebarSections.Panes, true);
        });
        store.Flush();
        var reloaded = new SettingsStore(file, FakeClock.Fixed());
        reloaded.Load();
        Assert.False(reloaded.Current.SidebarCollapsed);
        Assert.Equal([SidebarSections.Registered, SidebarSections.Panes, SidebarSections.Pc], reloaded.Current.SidebarCollapsedSections); // in the sections' order
        Assert.Equal("panes", SidebarSections.Panes);

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
