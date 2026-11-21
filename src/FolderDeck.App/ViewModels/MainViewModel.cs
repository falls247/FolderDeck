using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Stores;
using FolderDeck.Services;
using FolderDeck.Shell;

namespace FolderDeck.ViewModels;

/// <summary>What the view model needs from the window (dialogs, scroll position, cascade).</summary>
public interface IMainView
{
    void CloseCascade();

    double GetScrollOffset();

    void ScrollTo(double offset);

    bool Confirm(string title, string body, string? path, string primaryText, bool danger);

    IReadOnlyList<string>? PickFolders();

    string? PickImportFile();

    bool ShowSettings(SettingsViewModel settings);
}

public enum NavMode
{
    New,
    Back,
    Forward,
    Reload,
    Restore,
}

public sealed partial class MainViewModel : ObservableObject
{
    public const int MaxHistory = 100;

    private readonly AppServices _services;
    private readonly List<NavEntry> _back = [];
    private readonly List<NavEntry> _forward = [];
    private readonly HashSet<TabKind> _restoring = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedKind))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private TabViewModel _selectedTab;

    [ObservableProperty]
    private int _cardSize = AppSettings.CardSizeDefault;

    [ObservableProperty]
    private string _registerInput = string.Empty;

    public MainViewModel(AppServices services)
    {
        _services = services;
        RegisteredTab = new TabViewModel(services, TabKind.Registered);
        RecentTab = new TabViewModel(services, TabKind.Recent);
        FrequentTab = new TabViewModel(services, TabKind.Frequent);
        Tabs = [RegisteredTab, RecentTab, FrequentTab];
        _selectedTab = RegisteredTab;
        foreach (var tab in Tabs)
        {
            tab.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TabViewModel.Location))
                {
                    GoUpCommand.NotifyCanExecuteChanged();
                }
            };
        }
    }

    public IMainView? View { get; set; }

    public AppServices Services => _services;

    public MessageBarViewModel Message { get; } = new();

    public TabViewModel RegisteredTab { get; }

    public TabViewModel RecentTab { get; }

    public TabViewModel FrequentTab { get; }

    public IReadOnlyList<TabViewModel> Tabs { get; }

    public TabKind SelectedKind => SelectedTab.Kind;

    public bool CanGoBack => _back.Count > 0;

    public bool CanGoForward => _forward.Count > 0;

    private AppSettings Settings => _services.Stores.Settings.Current;

    public TabViewModel TabOf(TabKind kind) => kind switch
    {
        TabKind.Registered => RegisteredTab,
        TabKind.Recent => RecentTab,
        _ => FrequentTab,
    };

    // ---- startup / shutdown -----------------------------------------------------------------

    /// <summary>Restores tabs' locations and opens the last tab (§5.5). Not recorded in history (§10.4).</summary>
    public async Task InitializeAsync()
    {
        var settings = Settings;
        CardSize = settings.CardSize;
        foreach (var tab in Tabs)
        {
            if (settings.Locations is { } locations && locations.TryGetValue(TabKinds.ToSetting(tab.Kind), out var loc)
                && loc.Root.Length > 0 && PathTools.IsSafeRel(loc.Path))
            {
                tab.Location = new NavLocation(tab.Kind, loc.Root, loc.Path);
                _restoring.Add(tab.Kind);
            }
        }

        SelectedTab = TabOf(TabKinds.FromSetting(settings.LastTab));
        await LoadTabAsync(SelectedTab);
    }

    public void SaveState(WindowPlacementSetting? window)
    {
        _services.Stores.Settings.Update(s =>
        {
            if (window is not null)
            {
                s.Window = window;
            }

            s.LastTab = TabKinds.ToSetting(SelectedTab.Kind);
            s.CardSize = CardSize;
            s.Locations ??= new Dictionary<string, LocationSetting>();
            foreach (var tab in Tabs)
            {
                s.Locations[TabKinds.ToSetting(tab.Kind)] = new LocationSetting { Root = tab.Location.Root, Path = tab.Location.Rel };
            }
        });
    }

    // ---- tabs and navigation (§8) -----------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectTab(TabKind kind)
    {
        var tab = TabOf(kind);
        if (tab == SelectedTab)
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        SelectedTab = tab;
        bool reload = tab.IsStale || (tab.IsTop && tab.Kind != TabKind.Registered);
        if (reload)
        {
            await LoadTabAsync(tab);
        }

        View?.ScrollTo(0);
        SaveLocations();
    }

    [RelayCommand(CanExecute = nameof(CanGoUpNow), AllowConcurrentExecutions = true)]
    private Task GoUp()
    {
        var loc = SelectedTab.Location;
        if (loc.IsTop)
        {
            return Task.CompletedTask;
        }

        var target = loc.Rel.Length > 0 ? loc with { Rel = PathTools.ParentRel(loc.Rel) } : NavLocation.Top(loc.Tab);
        return NavigateAsync(target, NavMode.New);
    }

    private bool CanGoUpNow() => !SelectedTab.Location.IsTop;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task GoBack()
    {
        if (_back.Count == 0)
        {
            return Task.CompletedTask;
        }

        var entry = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        Push(_forward, CurrentEntry());
        return NavigateAsync(entry.Location, NavMode.Back, entry.ScrollOffset);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task GoForward()
    {
        if (_forward.Count == 0)
        {
            return Task.CompletedTask;
        }

        var entry = _forward[^1];
        _forward.RemoveAt(_forward.Count - 1);
        Push(_back, CurrentEntry());
        return NavigateAsync(entry.Location, NavMode.Forward, entry.ScrollOffset);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task NavigateCrumb(BreadcrumbItem? crumb)
    {
        if (crumb?.Target is null || crumb.Target == SelectedTab.Location)
        {
            return Task.CompletedTask;
        }

        return NavigateAsync(crumb.Target, NavMode.New);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BackToTop() => NavigateAsync(NavLocation.Top(SelectedTab.Kind), NavMode.New);

    [RelayCommand]
    private async Task Refresh()
    {
        View?.CloseCascade();
        Message.ClearPersistent();
        _services.ClearCaches();
        _services.Thumbnails.Clear();
        await LoadTabAsync(SelectedTab);
    }

    /// <summary>Moves inside the app. <paramref name="recordPath"/> is set when the move counts as a use (§10.4).</summary>
    public async Task NavigateAsync(NavLocation target, NavMode mode, double restoreOffset = 0, string? recordPath = null)
    {
        View?.CloseCascade();
        Message.ClearPersistent();
        var tab = TabOf(target.Tab);
        if (mode == NavMode.New)
        {
            Push(_back, CurrentEntry());
            _forward.Clear();
        }

        NotifyNav();
        if (SelectedTab != tab)
        {
            SelectedTab = tab;
        }

        tab.Location = target;
        View?.ScrollTo(0);
        var outcome = await LoadTabAsync(tab);
        if (outcome == LoadOutcome.Ok)
        {
            if (restoreOffset > 0 && mode is NavMode.Back or NavMode.Forward)
            {
                View?.ScrollTo(restoreOffset);
            }

            if (recordPath is not null)
            {
                RecordUse(recordPath, ItemKind.Folder);
            }
        }

        SaveLocations();
    }

    private async Task<LoadOutcome> LoadTabAsync(TabViewModel tab)
    {
        var outcome = await tab.LoadAsync();
        if (_restoring.Remove(tab.Kind) && outcome is LoadOutcome.NotFound or LoadOutcome.Error && !tab.IsTop)
        {
            // The remembered location is gone: show the tab's top level instead (§5.5).
            tab.Location = NavLocation.Top(tab.Kind);
            outcome = await tab.LoadAsync();
        }

        return outcome;
    }

    private NavEntry CurrentEntry() => new(SelectedTab.Location, View?.GetScrollOffset() ?? 0);

    private void Push(List<NavEntry> stack, NavEntry entry)
    {
        if (stack.Count > 0 && stack[^1].Location == entry.Location)
        {
            stack[^1] = entry;
        }
        else
        {
            stack.Add(entry);
        }

        if (stack.Count > MaxHistory)
        {
            stack.RemoveAt(0);
        }

        NotifyNav();
    }

    private void NotifyNav()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void SaveLocations() => SaveState(null);

    partial void OnCardSizeChanged(int value)
    {
        int clamped = AppSettings.ClampCardSize(value);
        if (clamped != value)
        {
            CardSize = clamped;
            return;
        }

        View?.CloseCascade();
        _services.Stores.Settings.Update(s => s.CardSize = clamped);
    }

    public void StepCardSize(int direction) =>
        CardSize = AppSettings.ClampCardSize(CardSize + (Math.Sign(direction) * AppSettings.CardSizeStep));

    // ---- opening items (§6.8, §9.7, §12) ----------------------------------------------------

    public Task ActivateCardAsync(CardViewModel card)
    {
        if (card.IsDisabled || card.IsBusy)
        {
            return Task.CompletedTask;
        }

        return OpenItemAsync(card.ToItemRef());
    }

    public Task OpenItemAsync(ItemRef item)
    {
        if (item.IsFolder)
        {
            if (item.Target is { } target)
            {
                return NavigateAsync(target, NavMode.New, recordPath: item.Path);
            }

            return Task.CompletedTask;
        }

        return OpenFileAsync(item.Path, item.Card);
    }

    public async Task OpenFileAsync(string path, CardViewModel? card)
    {
        Message.ClearPersistent();
        string name = PathTools.DisplayName(path);
        var settings = Settings;
        if (settings.ConfirmExecutables && ShellActions.IsExecutable(path))
        {
            bool run = View?.Confirm("プログラムの実行", $"プログラムを実行しようとしています。{name} を実行しますか？", path, "実行", danger: true) ?? false;
            if (!run)
            {
                return;
            }
        }

        if (card is not null)
        {
            card.IsBusy = true;
        }

        OpenResult result;
        try
        {
            result = await Task.Run(() => ShellActions.Open(path));
        }
        finally
        {
            if (card is not null)
            {
                card.IsBusy = false;
            }
        }

        if (!result.Success)
        {
            Message.Error(result.Error ?? "開けませんでした。");
            return;
        }

        Message.Success($"{name} を開きました。");
        RecordUse(path, ItemKind.File);
        if (settings.AddToWindowsRecent)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    ShellInterop.AddToRecentDocs(path);
                }
                catch (Exception ex)
                {
                    AppLog.Warn("SHAddToRecentDocs failed", ex);
                }
            });
        }
    }

    /// <summary>Counts a use (§10.4) and refreshes the tabs built from usage.</summary>
    public void RecordUse(string path, ItemKind kind)
    {
        _services.Stores.History.Record(path, kind);
        RefreshUsageTabs();
    }

    private void RefreshUsageTabs()
    {
        RecentTab.IsStale = true;
        FrequentTab.IsStale = true;
        if (SelectedTab.IsTop && SelectedTab.Kind is TabKind.Recent or TabKind.Frequent && !SelectedTab.IsLoading)
        {
            _ = SelectedTab.LoadAsync();
        }
    }

    // ---- context menu actions (§11) ---------------------------------------------------------

    public void ShowInExplorer(ItemRef item)
    {
        var result = ShellActions.ShowInExplorer(item.Path);
        if (!result.Success)
        {
            Message.Error(result.Error!);
            return;
        }

        RecordUse(item.Path, item.IsFolder ? ItemKind.Folder : ItemKind.File);
    }

    public void OpenInExplorer(ItemRef item)
    {
        var result = ShellActions.OpenInExplorer(item.Path);
        if (!result.Success)
        {
            Message.Error(result.Error!);
        }
    }

    public void CopyPath(ItemRef item)
    {
        if (ShellActions.CopyPath(item.Path))
        {
            Message.Success("パスをコピーしました。");
        }
        else
        {
            Message.Error("クリップボードにコピーできませんでした。");
        }
    }

    public bool IsPinned(string path) => _services.Stores.Pins.IsPinned(path);

    public void TogglePin(ItemRef item)
    {
        var pins = _services.Stores.Pins;
        if (pins.IsPinned(item.Path))
        {
            pins.Unpin(item.Path);
            Message.Success($"「{item.Name}」のピン留めを外しました。");
        }
        else
        {
            pins.Pin(item.Path, item.IsFolder ? ItemKind.Folder : ItemKind.File);
            Message.Success($"「{item.Name}」をピン留めしました。");
        }

        if (item.Card is { } card)
        {
            card.IsPinned = pins.IsPinned(item.Path);
        }

        FrequentTab.IsStale = true;
        if (SelectedTab == FrequentTab && FrequentTab.IsTop)
        {
            _ = FrequentTab.LoadAsync();
        }
    }

    public async Task AddToRegisteredAsync(ItemRef item)
    {
        var result = await Task.Run(() => _services.Stores.Registered.Register(item.Path, _services.FileSystem));
        ReportRegisterResult(result);
    }

    // ---- 登録フォルダ (§10.1) -----------------------------------------------------------------

    [RelayCommand]
    private async Task Register()
    {
        Message.ClearPersistent();
        string input = RegisterInput;
        var result = await Task.Run(() => _services.Stores.Registered.Register(input, _services.FileSystem));
        if (result.Outcome == RegisterOutcome.Added)
        {
            RegisterInput = string.Empty;
        }

        ReportRegisterResult(result);
    }

    [RelayCommand]
    private async Task Browse()
    {
        Message.ClearPersistent();
        var folders = View?.PickFolders();
        if (folders is null || folders.Count == 0)
        {
            Message.Info("フォルダは選択されませんでした。");
            return;
        }

        var summary = await Task.Run(() => _services.Stores.Registered.RegisterMany(folders, _services.FileSystem));
        ReportSummary(summary, extra: null);
    }

    /// <summary>Folders dropped from Explorer (§10.1, P1). Files are ignored and counted.</summary>
    public async Task RegisterDroppedAsync(IReadOnlyList<string> paths)
    {
        Message.ClearPersistent();
        var (folders, files) = await Task.Run(() =>
        {
            var dirs = new List<string>();
            int fileCount = 0;
            foreach (string p in paths)
            {
                if (Directory.Exists(p))
                {
                    dirs.Add(p);
                }
                else
                {
                    fileCount++;
                }
            }

            return (dirs, fileCount);
        });

        string? extra = files > 0 ? $"ファイル {files} 件は無視しました。" : null;
        if (folders.Count == 0)
        {
            Message.Info(extra ?? "フォルダは選択されませんでした。");
            return;
        }

        var summary = await Task.Run(() => _services.Stores.Registered.RegisterMany(folders, _services.FileSystem));
        ReportSummary(summary, extra);
    }

    private void ReportRegisterResult(RegisterResult result)
    {
        string name = result.Path is null ? result.Input : PathTools.DisplayName(result.Path);
        switch (result.Outcome)
        {
            case RegisterOutcome.Added:
                Message.Success($"「{name}」を登録しました。");
                RegisteredChanged();
                break;
            case RegisterOutcome.AlreadyRegistered:
                Message.Info($"「{name}」は登録済みです。");
                break;
            default:
                Message.Error(result.Error ?? "登録できませんでした。");
                break;
        }
    }

    private void ReportSummary(RegisterSummary summary, string? extra)
    {
        string text = summary.ToMessage() + (extra ?? string.Empty);
        if (summary.Errors.Any())
        {
            Message.Error(text);
        }
        else if (summary.Added > 0)
        {
            Message.Success(text);
        }
        else
        {
            Message.Info(text);
        }

        if (summary.Added > 0)
        {
            RegisteredChanged();
        }
    }

    private void RegisteredChanged()
    {
        RegisteredTab.IsStale = true;
        if (SelectedTab == RegisteredTab && RegisteredTab.IsTop)
        {
            _ = RegisteredTab.LoadAsync();
        }
    }

    public void Unregister(CardViewModel card)
    {
        if (card.RegisteredId is null)
        {
            return;
        }

        bool ok = View?.Confirm("登録を解除しますか？", "一覧から外すだけで、フォルダやファイルは削除されません。", card.Path, "登録を解除", danger: true) ?? false;
        if (!ok)
        {
            return;
        }

        if (_services.Stores.Registered.Remove(card.RegisteredId))
        {
            Message.Success($"「{card.Name}」の登録を解除しました。");
            RegisteredTab.Cards = RegisteredTab.Cards.Where(c => c != card).ToList();
            RegisteredTab.CountText = $"登録 {RegisteredTab.Cards.Count:N0}";
            if (RegisteredTab.Cards.Count == 0)
            {
                RegisteredChanged();
            }
        }
    }

    // ---- × on top-level cards (§6.7) --------------------------------------------------------

    public void RemoveCard(CardViewModel card)
    {
        View?.CloseCascade();
        switch (card.OwnerTab)
        {
            case TabKind.Registered:
                Unregister(card);
                break;
            case TabKind.Recent:
                ExcludeFromRecent(card);
                break;
            default:
                RemoveFromFrequent(card);
                break;
        }
    }

    public void ExcludeFromRecent(CardViewModel card)
    {
        _services.Stores.Excluded.Exclude(card.Path);
        RecentTab.Cards = RecentTab.Cards.Where(c => c != card).ToList();
        RecentTab.CountText = $"履歴 {RecentTab.Cards.Count:N0}";
        if (RecentTab.Cards.Count == 0)
        {
            RecentTab.EmptyText = "最近の履歴はありません。";
        }

        Message.Info($"「{card.Name}」を履歴から外しました。");
    }

    public void RemoveFromFrequent(CardViewModel card)
    {
        if (_services.Stores.Pins.IsPinned(card.Path))
        {
            _services.Stores.Pins.Unpin(card.Path);
            Message.Info($"「{card.Name}」のピン留めを外しました。");
        }
        else
        {
            _services.Stores.History.Remove(card.Path);
            RecentTab.IsStale = true;
            Message.Info($"「{card.Name}」を一覧から外しました。");
        }

        _ = FrequentTab.LoadAsync();
    }

    // ---- 最近の履歴 (§10.2) -------------------------------------------------------------------

    [RelayCommand]
    private async Task ClearHistory()
    {
        bool ok = View?.Confirm("履歴をクリアしますか？", "この一覧を空にします。ファイルや Windows の履歴は削除されません。", null, "履歴をクリア", danger: true) ?? false;
        if (!ok)
        {
            return;
        }

        var now = JsonFile.TruncateToSeconds(_services.Clock.UtcNow);
        _services.Stores.Settings.Update(s => s.RecentClearedAt = now);
        await RecentTab.LoadAsync();
        Message.Success("最近の履歴をクリアしました。");
    }

    // ---- settings (§14) ---------------------------------------------------------------------

    [RelayCommand]
    private async Task OpenSettings()
    {
        View?.CloseCascade();
        var before = Settings;
        var vm = new SettingsViewModel(before)
        {
            ImportAction = ImportImgtoolAsync,
            ResetStatsAction = ResetStats,
            OpenDataFolderAction = OpenDataFolder,
        };
        if (View?.ShowSettings(vm) != true)
        {
            return;
        }

        _services.Stores.Settings.Update(vm.ApplyTo);
        var after = Settings;
        _services.ShowHidden = after.ShowHidden;
        if (before.ShowHidden != after.ShowHidden)
        {
            foreach (var tab in Tabs)
            {
                tab.IsStale = true;
            }
        }

        if (before.IncludeWindowsRecent != after.IncludeWindowsRecent || before.RecentLimit != after.RecentLimit)
        {
            RecentTab.IsStale = true;
        }

        if (before.FrequentLimit != after.FrequentLimit)
        {
            FrequentTab.IsStale = true;
        }

        Message.Success("設定を保存しました。");
        if (SelectedTab.IsStale)
        {
            await SelectedTab.LoadAsync();
        }
    }

    /// <summary>§13.4: imports imgtool's registered_folders.json. Returns the result text.</summary>
    public async Task<string?> ImportImgtoolAsync()
    {
        string? file = View?.PickImportFile();
        if (file is null)
        {
            return null;
        }

        List<string> paths;
        try
        {
            paths = await Task.Run(() => ReadImgtoolPaths(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            string error = "取り込めませんでした：" + ex.Message;
            Message.Error(error);
            return error;
        }

        var summary = await Task.Run(() => _services.Stores.Registered.RegisterMany(paths, _services.FileSystem));
        int notFound = summary.Results.Count(r => r.Outcome is RegisterOutcome.NotFound or RegisterOutcome.Invalid);
        string text = $"{summary.Added} 件取り込みました。{summary.AlreadyRegistered} 件は登録済み、{notFound} 件は見つかりません。";
        Message.Success(text);
        if (summary.Added > 0)
        {
            RegisteredChanged();
        }

        return text;
    }

    internal static List<string> ReadImgtoolPaths(string file)
    {
        byte[] bytes = File.ReadAllBytes(file);
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        var reader = new Utf8JsonReader(span, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("folders", out var folders)
            || folders.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("registered_folders.json の形式ではありません。");
        }

        var paths = new List<string>();
        foreach (var f in folders.EnumerateArray())
        {
            if (f.ValueKind == JsonValueKind.Object && f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
            {
                paths.Add(p.GetString()!);
            }
        }

        return paths;
    }

    public string? ResetStats()
    {
        bool ok = View?.Confirm("よく使う項目の統計をリセットしますか？", "利用記録をすべて削除します。ピン留めは残ります。ファイルは削除されません。", null, "リセット", danger: true) ?? false;
        if (!ok)
        {
            return null;
        }

        _services.Stores.History.Clear();
        RecentTab.IsStale = true;
        FrequentTab.IsStale = true;
        const string text = "よく使う項目の統計をリセットしました。";
        Message.Success(text);
        return text;
    }

    public void OpenDataFolder()
    {
        var result = ShellActions.OpenInExplorer(_services.Stores.DataDirectory);
        if (!result.Success)
        {
            Message.Error(result.Error!);
        }
    }
}
