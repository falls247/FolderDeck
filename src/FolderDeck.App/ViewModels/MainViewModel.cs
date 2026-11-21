using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;
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

    /// <summary>The window handle that owns shell dialogs (Recycle Bin progress, "file in use"…).</summary>
    nint WindowHandle { get; }

    IReadOnlyList<string>? PickFolders();

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
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedKind))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private TabViewModel _selectedTab;

    [ObservableProperty]
    private int _cardSize = AppSettings.CardSizeDefault;

    /// <summary>Card shape for every tab (toolbar, right of the sort button).</summary>
    [ObservableProperty]
    private CardShape _cardShape = CardShape.Portrait;

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
                else if (e.PropertyName == nameof(TabViewModel.Cards) && ReferenceEquals(tab, SelectedTab))
                {
                    View?.CloseCascade(); // the grid rebuilds its cards: an open overlay would be left behind
                }
            };
        }

        services.Stores.Pins.Changed += OnPinsChanged;
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
        CardShape = CardShapes.FromSetting(settings.CardShape);
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
            s.CardShape = CardShapes.ToSetting(CardShape);
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

    partial void OnCardShapeChanged(CardShape value)
    {
        View?.CloseCascade(); // the overlay covers the thumbnail, whose height changes
        _services.Stores.Settings.Update(s => s.CardShape = CardShapes.ToSetting(value));
    }

    [RelayCommand]
    private void SetCardShape(CardShape shape) => CardShape = shape;

    // ---- card order: sort modes and drag & drop ---------------------------------------------

    /// <summary>Changes the selected tab's sort mode (saved per tab) and re-orders its cards in place.</summary>
    public void SetSortMode(CardSortMode mode)
    {
        View?.CloseCascade();
        var tab = SelectedTab;
        if (tab.SortMode == mode)
        {
            return;
        }

        SaveSortMode(tab, mode);
        tab.ApplyOrder();
        View?.ScrollTo(0);
    }

    /// <summary>True when <paramref name="card"/> may be dragged to a new position (top level of the selected tab).</summary>
    public bool CanReorder(CardViewModel card) =>
        card.IsTopLevel && SelectedTab.CanReorder && SelectedTab.Cards.Count > 1 && SelectedTab.Cards.Contains(card);

    /// <summary>
    /// A card was dropped at <paramref name="insertIndex"/> (0…Count, counted with the card still in
    /// place). The shown order becomes the tab's manual order; a sort mode switches to 手動.
    /// </summary>
    public void MoveCard(CardViewModel card, int insertIndex)
    {
        var tab = SelectedTab;
        if (!tab.CanReorder)
        {
            return;
        }

        var cards = tab.Cards.ToList();
        int from = cards.IndexOf(card);
        insertIndex = Math.Clamp(insertIndex, 0, cards.Count);
        if (from < 0 || insertIndex == from || insertIndex == from + 1)
        {
            return;
        }

        cards.RemoveAt(from);
        cards.Insert(insertIndex > from ? insertIndex - 1 : insertIndex, card);
        bool switched = tab.SortMode != CardSortMode.Manual;
        if (switched)
        {
            SaveSortMode(tab, CardSortMode.Manual);
        }

        tab.SetBaseOrder(cards);
        SaveManualOrder(tab, cards);
        if (switched)
        {
            Message.Info("並び順を「手動」に切り替えました。");
        }
    }

    private void SaveSortMode(TabViewModel tab, CardSortMode mode)
    {
        string key = TabKinds.ToSetting(tab.Kind);
        _services.Stores.Settings.Update(s => s.SetSortMode(key, mode));
        tab.SortMode = mode;
    }

    /// <summary>
    /// Saves <paramref name="shown"/> as the manual order of the tab's top level: 登録フォルダ in
    /// registered_folders.json, the others in card_order.json (shown paths first, then the
    /// previously saved ones that are not shown now).
    /// </summary>
    private void SaveManualOrder(TabViewModel tab, IReadOnlyList<CardViewModel> shown)
    {
        if (tab.Kind == TabKind.Registered)
        {
            _services.Stores.Registered.Reorder(shown.Select(c => c.RegisteredId).OfType<string>().ToList());
            return;
        }

        if (OrderListOf(tab.Kind) is not { } list)
        {
            return;
        }

        var paths = shown.Select(c => c.Path).ToList();
        var showing = new HashSet<string>(paths, PathTools.Comparer);
        var hidden = _services.Stores.CardOrders.Get(list).Where(p => !showing.Contains(p));
        _services.Stores.CardOrders.Set(list, paths.Concat(hidden));
    }

    /// <summary>The card_order.json list of a tab, or null for 登録フォルダ (its order is registered_folders.json).</summary>
    private static CardOrderList? OrderListOf(TabKind kind) => kind switch
    {
        TabKind.Recent => CardOrderList.Recent,
        TabKind.Frequent => CardOrderList.Frequent,
        _ => null,
    };

    /// <summary>True when the tab's top level has a saved drag &amp; drop order in card_order.json.</summary>
    private bool HasSavedOrder(TabKind kind) => OrderListOf(kind) is { } list && _services.Stores.CardOrders.HasOrder(list);

    /// <summary>
    /// 「手動の並びをリセット」 is offered on 最近の履歴 and よく使う項目, at any level (登録フォルダ has
    /// nothing to reset: its manual order is the order of registered_folders.json).
    /// </summary>
    public bool ShowResetManualOrder => OrderListOf(SelectedTab.Kind) is not null;

    /// <summary>The reset does something: the selected tab has a saved order (cards were dragged there).</summary>
    public bool CanResetManualOrder => HasSavedOrder(SelectedTab.Kind);

    /// <summary>
    /// Forgets the selected tab's saved drag &amp; drop order: its top level goes back to the default
    /// order (最近の履歴: last used first, so re-opened items move to the front again; よく使う項目:
    /// pinned items, then the most used). Reloaded now
    /// when the tab shows its top level; inside a folder, the top level is rebuilt when it is shown.
    /// </summary>
    public async Task ResetManualOrderAsync()
    {
        View?.CloseCascade();
        var tab = SelectedTab;
        if (OrderListOf(tab.Kind) is not { } list || !_services.Stores.CardOrders.Clear(list))
        {
            return;
        }

        Message.Info("手動の並びをリセットしました。");
        if (tab.IsTop && await tab.LoadAsync() == LoadOutcome.Ok && ReferenceEquals(tab, SelectedTab) && tab.IsTop)
        {
            View?.ScrollTo(0);
        }
    }

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

    /// <summary>
    /// A folder opens itself; a file opens its parent folder with the file selected. In a new
    /// Explorer tab when the setting asks for it, else a new window.
    /// </summary>
    public async Task ShowInExplorerAsync(ItemRef item)
    {
        bool newTab = Settings.OpenExplorerInNewTab;
        var result = (item.IsFolder, newTab) switch
        {
            (true, true) => await ExplorerTabs.OpenAsync(item.Path),
            (true, false) => ShellActions.OpenInExplorer(item.Path),
            (false, true) => await ExplorerTabs.ShowAsync(item.Path),
            (false, false) => ShellActions.ShowInExplorer(item.Path),
        };
        if (!result.Success)
        {
            Message.Error(result.Error!);
            return;
        }

        RecordUse(item.Path, item.IsFolder ? ItemKind.Folder : ItemKind.File);
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

    /// <summary>
    /// Items that can be moved to the Recycle Bin from their menu: what lies inside a folder (cards
    /// below a tab's top level, overlay / flyout rows). Never a tab's top-level card (a registered
    /// folder, a history entry) nor a flyout header, and not a missing / not responding card.
    /// </summary>
    public static bool CanDelete(ItemRef item) =>
        item.Context is ItemContext.InnerCard or ItemContext.Row && item.Card?.IsDisabled != true;

    /// <summary>
    /// Moves the item to the Recycle Bin after a confirmation (the cascade closes first). Never
    /// deletes permanently: an item that cannot be recycled is left as it is (<see cref="RecycleBin"/>).
    /// </summary>
    public async Task DeleteAsync(ItemRef item)
    {
        if (!CanDelete(item))
        {
            return;
        }

        string name = item.Name;
        string body = (item.IsFolder
                ? $"フォルダ「{name}」を、中のファイルやフォルダごとごみ箱に移動します。"
                : $"「{name}」をごみ箱に移動します。")
            + "\nごみ箱に移動できない場所（ネットワーク上のフォルダなど）にある項目は削除しません。";
        bool ok = View?.Confirm("ごみ箱に移動しますか？", body, item.Path, "ごみ箱に移動", danger: true) ?? false;
        if (!ok)
        {
            return;
        }

        var card = item.Card;
        if (card is not null)
        {
            card.IsBusy = true;
        }

        RecycleResult result;
        try
        {
            result = await RecycleBin.MoveAsync(item.Path, View?.WindowHandle ?? 0);
        }
        finally
        {
            if (card is not null)
            {
                card.IsBusy = false;
            }
        }

        switch (result.Outcome)
        {
            case RecycleOutcome.Recycled:
                Message.Success($"「{name}」をごみ箱に移動しました。");
                break;
            case RecycleOutcome.Canceled:
                Message.Info($"「{name}」の削除を中止しました。");
                break;
            case RecycleOutcome.NotRecyclable:
                Message.Error($"「{name}」はごみ箱に移動できない場所にあるため、削除しませんでした。");
                break;
            default:
                Message.Error($"「{name}」を削除できませんでした：{result.Error ?? "不明なエラー"}");
                break;
        }

        // Even a cancelled or failed delete may have moved part of a folder: list again.
        await ReloadAfterDeleteAsync();
    }

    /// <summary>
    /// Drops the cached listings and folder covers (the parent, the overlay's folders, covers that
    /// showed the item), marks every tab stale and reloads the shown one in place.
    /// </summary>
    private async Task ReloadAfterDeleteAsync()
    {
        View?.CloseCascade();
        _services.ClearCaches();
        foreach (var tab in Tabs)
        {
            tab.IsStale = true;
        }

        var shown = SelectedTab;
        double offset = View?.GetScrollOffset() ?? 0;
        var outcome = await shown.LoadAsync();
        if (outcome == LoadOutcome.Ok && ReferenceEquals(shown, SelectedTab))
        {
            View?.ScrollTo(offset);
        }
    }

    public bool IsPinned(string path) => _services.Stores.Pins.IsPinned(path);

    public void TogglePin(ItemRef item)
    {
        var pins = _services.Stores.Pins;
        bool reloadFrequent = true;
        if (pins.IsPinned(item.Path))
        {
            // OnPinsChanged re-sorts: the card moves to its sorted place.
            pins.Unpin(item.Path);
            Message.Success($"「{item.Name}」のピン留めを外しました。");
        }
        else
        {
            // Before Pin(): its Changed event re-sorts (OnPinsChanged), and by then the card must
            // already own its shown slot in the base order.
            bool keptInMemoryOnly = KeepInPlace(SelectedTab, item.Path);
            pins.Pin(item.Path, item.IsFolder ? ItemKind.Folder : ItemKind.File);
            Message.Success($"「{item.Name}」をピン留めしました。");

            // The card is already listed there; a reload would rebuild the base order from the
            // defaults (pinned items first) and move it.
            reloadFrequent = !(keptInMemoryOnly && SelectedTab == FrequentTab);
        }

        FrequentTab.IsStale = true;
        if (reloadFrequent && SelectedTab == FrequentTab && FrequentTab.IsTop)
        {
            _ = FrequentTab.LoadAsync();
        }
    }

    /// <summary>
    /// Pinning a card of a sorted top level: the card takes its shown index as its base-order slot
    /// (only that card moves in the base order), so it stays where it is once pinned (pinned cards
    /// sit at their base-order slot). The move is saved for 登録フォルダ (registered_folders.json)
    /// and, in card_order.json, only for a tab that already has a saved order (cards were dragged
    /// there); otherwise it is kept in memory only and a later reload puts the card at its default
    /// slot (saving would freeze the recency / usage order for good). Returns true in that last case.
    /// </summary>
    private bool KeepInPlace(TabViewModel tab, string path)
    {
        if (!tab.CanReorder || tab.SortMode == CardSortMode.Manual)
        {
            return false;
        }

        var target = new PathSet([path]);
        var shown = tab.Cards;
        int slot = -1;
        for (int i = 0; i < shown.Count; i++)
        {
            if (target.Contains(shown[i].Path))
            {
                slot = i;
                break;
            }
        }

        if (slot < 0 || !tab.PlaceInBase(shown[slot], slot))
        {
            return false;
        }

        if (tab.Kind != TabKind.Registered && !HasSavedOrder(tab.Kind))
        {
            return true;
        }

        SaveManualOrder(tab, tab.BaseCards);
        return false;
    }

    /// <summary>pins.json changed: every card of every tab gets its 📌 (and sorted views their order) updated.</summary>
    private void OnPinsChanged()
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(OnPinsChanged);
            return;
        }

        var pins = new PathSet(_services.Stores.Pins.Pins.Select(p => p.Path));
        double offset = View?.GetScrollOffset() ?? 0;
        bool moved = false;
        foreach (var tab in Tabs)
        {
            moved |= tab.ApplyPins(pins) && ReferenceEquals(tab, SelectedTab);
        }

        if (moved)
        {
            View?.ScrollTo(offset);
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
            RegisteredTab.RemoveCard(card);
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
        RecentTab.RemoveCard(card);
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
