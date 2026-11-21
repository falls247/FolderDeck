using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Listing;
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

    /// <summary>Switches the colours to the theme chosen in the settings (system / light / dark).</summary>
    void ApplyTheme(AppTheme theme);
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

    private static readonly TimeSpan RegisteredCheckTimeout = TimeSpan.FromSeconds(2);

    private readonly AppServices _services;
    private readonly HashSet<TabViewModel> _restoring = [];
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>The fixed tab shown last: where closing the last folder tab leads.</summary>
    private TabViewModel _lastFixedTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoForward))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private TabViewModel _selectedTab;

    [ObservableProperty]
    private int _cardSize = AppSettings.CardSizeDefault;

    /// <summary>Card shape for every tab (toolbar, right of the sort button).</summary>
    [ObservableProperty]
    private CardShape _cardShape = CardShape.Portrait;

    /// <summary>Width of the 登録フォルダ sidebar (DIP), changed with the splitter.</summary>
    [ObservableProperty]
    private double _sidebarWidth = AppSettings.SidebarWidthDefault;

    public MainViewModel(AppServices services)
    {
        _services = services;
        RecentTab = new TabViewModel(services, TabKind.Recent);
        FrequentTab = new TabViewModel(services, TabKind.Frequent);
        Watch(RecentTab);
        Watch(FrequentTab);
        _selectedTab = RecentTab;
        _lastFixedTab = RecentTab;
        RecentTab.IsSelected = true;
        services.Stores.Pins.Changed += OnPinsChanged;
    }

    public IMainView? View { get; set; }

    public AppServices Services => _services;

    public MessageBarViewModel Message { get; } = new();

    /// <summary>最近の履歴 (fixed tab).</summary>
    public TabViewModel RecentTab { get; }

    /// <summary>よく使う項目 (fixed tab).</summary>
    public TabViewModel FrequentTab { get; }

    /// <summary>The folder tabs, left to right: opened from the sidebar, closable, movable.</summary>
    public ObservableCollection<TabViewModel> FolderTabs { get; } = [];

    /// <summary>The 登録フォルダ of the sidebar, in the order of registered_folders.json.</summary>
    public ObservableCollection<SidebarItemViewModel> Registered { get; } = [];

    public bool HasNoRegistered => Registered.Count == 0;

    public string RegisteredCountText => Registered.Count.ToString("N0");

    public bool CanGoBack => SelectedTab.BackStack.Count > 0;

    public bool CanGoForward => SelectedTab.ForwardStack.Count > 0;

    private AppSettings Settings => _services.Stores.Settings.Current;

    /// <summary>Every tab: the two fixed ones, then the folder tabs.</summary>
    private IEnumerable<TabViewModel> AllTabs => new[] { RecentTab, FrequentTab }.Concat(FolderTabs);

    private void Watch(TabViewModel tab) => tab.PropertyChanged += OnTabPropertyChanged;

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Location))
        {
            GoUpCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(TabViewModel.Cards) && ReferenceEquals(sender, SelectedTab))
        {
            View?.CloseCascade(); // the grid rebuilds its cards: an open overlay would be left behind
        }
    }

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        newValue.IsSelected = true;
        if (!newValue.IsFolderTab)
        {
            _lastFixedTab = newValue;
        }
    }

    // ---- startup / shutdown -----------------------------------------------------------------

    /// <summary>
    /// Restores the fixed tabs' locations, the folder tabs and the sidebar, and opens the last tab
    /// (§5.5). Not recorded in history (§10.4).
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = Settings;
        CardSize = settings.CardSize;
        CardShape = CardShapes.FromSetting(settings.CardShape);
        SidebarWidth = settings.SidebarWidth;
        foreach (var tab in new[] { RecentTab, FrequentTab })
        {
            if (settings.Locations is { } locations && locations.TryGetValue(TabKinds.ToSetting(tab.Kind), out var loc)
                && loc.Root.Length > 0 && PathTools.IsSafeRel(loc.Path))
            {
                tab.Location = new NavLocation(tab.Kind, loc.Root, loc.Path);
                _restoring.Add(tab);
            }
        }

        // A folder tab whose folder is gone stays, with its error banner: the user closes it.
        int selected = -1;
        var open = settings.OpenTabs ?? [];
        for (int i = 0; i < open.Count; i++)
        {
            if (open[i].Root.Length > 0 && PathTools.IsSafeRel(open[i].Path))
            {
                AddFolderTab(new NavLocation(TabKind.Folder, open[i].Root, open[i].Path));
                if (i == settings.SelectedFolderTab)
                {
                    selected = FolderTabs.Count - 1;
                }
            }
        }

        RefreshRegistered();
        SelectedTab = TabKinds.FromSetting(settings.LastTab) switch
        {
            TabKind.Frequent => FrequentTab,
            TabKind.Folder when selected >= 0 => FolderTabs[selected],
            _ => RecentTab,
        };
        await LoadTabAsync(SelectedTab);
    }

    public void SaveState(WindowPlacementSetting? window)
    {
        string lastTab = TabKinds.ToSetting(SelectedTab.Kind);
        int selectedFolderTab = Math.Max(0, FolderTabs.IndexOf(SelectedTab));
        var openTabs = FolderTabs.Select(t => new LocationSetting { Root = t.Location.Root, Path = t.Location.Rel }).ToList();
        _services.Stores.Settings.Update(s =>
        {
            if (window is not null)
            {
                s.Window = window;
            }

            s.LastTab = lastTab;
            s.SelectedFolderTab = selectedFolderTab;
            s.OpenTabs = openTabs;
            s.SidebarWidth = SidebarWidth;
            s.CardSize = CardSize;
            s.CardShape = CardShapes.ToSetting(CardShape);
            s.Locations ??= new Dictionary<string, LocationSetting>();
            foreach (var tab in new[] { RecentTab, FrequentTab })
            {
                s.Locations[TabKinds.ToSetting(tab.Kind)] = new LocationSetting { Root = tab.Location.Root, Path = tab.Location.Rel };
            }
        });
    }

    // ---- tabs and navigation (§8) -----------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectTab(TabViewModel? tab)
    {
        if (tab is null || tab == SelectedTab || (tab.IsFolderTab && !FolderTabs.Contains(tab)))
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        await ShowTabAsync(tab);
        SaveLocations();
    }

    /// <summary>
    /// Makes <paramref name="tab"/> the shown one. The tab left remembers its scroll position; the
    /// lists of 最近の履歴 / よく使う項目 are rebuilt each time they are shown, a folder is listed
    /// again only when it went stale and otherwise comes back where it was scrolled to.
    /// </summary>
    private async Task ShowTabAsync(TabViewModel tab)
    {
        bool switched = !ReferenceEquals(tab, SelectedTab);
        double offset = switched ? tab.SavedScrollOffset : View?.GetScrollOffset() ?? 0;
        if (switched)
        {
            SelectedTab.SavedScrollOffset = View?.GetScrollOffset() ?? 0;
            SelectedTab = tab;
        }

        bool top = tab.IsTop && !tab.IsFolderTab;
        bool reload = tab.IsStale || (top && switched);
        if (reload)
        {
            await LoadTabAsync(tab);
        }

        if ((switched || reload) && ReferenceEquals(tab, SelectedTab))
        {
            View?.ScrollTo(top && switched ? 0 : offset);
        }
    }

    /// <summary>
    /// Opens a folder in a folder tab of its own: what a click on a 登録フォルダ of the sidebar, or
    /// on a row / header of the cascade opened from it, does. A tab that already shows that very
    /// folder is selected instead of opening a second one.
    /// </summary>
    public async Task OpenFolderTabAsync(NavLocation target, string? recordPath = null)
    {
        View?.CloseCascade();
        Message.ClearPersistent();
        var tab = FolderTabs.FirstOrDefault(t => SameLocation(t.Location, target));
        if (tab is null)
        {
            tab = AddFolderTab(target with { Tab = TabKind.Folder });
        }

        await ShowTabAsync(tab);
        if (recordPath is not null && tab.BannerText is null)
        {
            RecordUse(recordPath, ItemKind.Folder);
        }

        SaveLocations();
    }

    private TabViewModel AddFolderTab(NavLocation location)
    {
        var tab = new TabViewModel(_services, TabKind.Folder, location);
        Watch(tab);
        FolderTabs.Add(tab);
        return tab;
    }

    private static bool SameLocation(NavLocation a, NavLocation b) =>
        PathTools.PathEquals(a.Root, b.Root) && string.Equals(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Closes a folder tab (× / middle click). The tab to its right (else the left one, else the last fixed tab) is shown.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseTab(TabViewModel? tab)
    {
        int index = tab is null ? -1 : FolderTabs.IndexOf(tab);
        if (tab is null || index < 0)
        {
            return;
        }

        bool wasSelected = ReferenceEquals(tab, SelectedTab);
        if (wasSelected)
        {
            View?.CloseCascade();
            Message.ClearPersistent();
        }

        tab.CancelLoad();
        tab.PropertyChanged -= OnTabPropertyChanged;
        FolderTabs.RemoveAt(index);
        if (wasSelected)
        {
            await ShowTabAsync(FolderTabs.Count > 0 ? FolderTabs[Math.Min(index, FolderTabs.Count - 1)] : _lastFixedTab);
        }

        SaveLocations();
    }

    /// <summary>Closes every folder tab but <paramref name="keep"/>, which is shown.</summary>
    public async Task CloseOtherTabsAsync(TabViewModel keep)
    {
        if (!FolderTabs.Contains(keep))
        {
            return;
        }

        View?.CloseCascade();
        foreach (var tab in FolderTabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            tab.CancelLoad();
            tab.PropertyChanged -= OnTabPropertyChanged;
            FolderTabs.Remove(tab);
        }

        if (!ReferenceEquals(keep, SelectedTab))
        {
            Message.ClearPersistent();
            await ShowTabAsync(keep);
        }

        SaveLocations();
    }

    /// <summary>A folder tab was dragged to <paramref name="newIndex"/> of the folder tabs.</summary>
    public void MoveFolderTab(TabViewModel tab, int newIndex)
    {
        int from = FolderTabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, FolderTabs.Count - 1);
        if (from < 0 || from == newIndex)
        {
            return;
        }

        FolderTabs.Move(from, newIndex);
        SaveLocations();
    }

    [RelayCommand(CanExecute = nameof(CanGoUpNow), AllowConcurrentExecutions = true)]
    private Task GoUp()
    {
        var loc = SelectedTab.Location;
        if (!SelectedTab.CanGoUp)
        {
            return Task.CompletedTask;
        }

        var target = loc.Rel.Length > 0 ? loc with { Rel = PathTools.ParentRel(loc.Rel) } : NavLocation.Top(loc.Tab);
        return NavigateAsync(target, NavMode.New);
    }

    private bool CanGoUpNow() => SelectedTab.CanGoUp;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task GoBack()
    {
        var tab = SelectedTab;
        if (tab.BackStack.Count == 0)
        {
            return Task.CompletedTask;
        }

        var entry = tab.BackStack[^1];
        tab.BackStack.RemoveAt(tab.BackStack.Count - 1);
        Push(tab.ForwardStack, CurrentEntry());
        return NavigateAsync(entry.Location, NavMode.Back, entry.ScrollOffset);
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task GoForward()
    {
        var tab = SelectedTab;
        if (tab.ForwardStack.Count == 0)
        {
            return Task.CompletedTask;
        }

        var entry = tab.ForwardStack[^1];
        tab.ForwardStack.RemoveAt(tab.ForwardStack.Count - 1);
        Push(tab.BackStack, CurrentEntry());
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

    /// <summary>The button on the error banner: back to the fixed tab's list; a folder tab closes.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BackToTop() => SelectedTab.IsFolderTab
        ? CloseTab(SelectedTab)
        : NavigateAsync(NavLocation.Top(SelectedTab.Kind), NavMode.New);

    [RelayCommand]
    private async Task Refresh()
    {
        View?.CloseCascade();
        Message.ClearPersistent();
        _services.ClearCaches();
        _services.Thumbnails.Clear();
        foreach (var tab in AllTabs)
        {
            tab.IsStale = true; // the hidden tabs list again when they are shown
        }

        RefreshRegistered(recheck: true);
        await LoadTabAsync(SelectedTab);
    }

    /// <summary>
    /// Moves inside the shown tab (a card, a row of its cascade, a breadcrumb, back / forward).
    /// <paramref name="recordPath"/> is set when the move counts as a use (§10.4). A folder-tab
    /// location while a fixed tab is shown opens a folder tab instead.
    /// </summary>
    public async Task NavigateAsync(NavLocation target, NavMode mode, double restoreOffset = 0, string? recordPath = null)
    {
        TabViewModel? tab = target.Tab switch
        {
            TabKind.Recent => RecentTab,
            TabKind.Frequent => FrequentTab,
            _ => SelectedTab.IsFolderTab ? SelectedTab : null,
        };
        if (tab is null)
        {
            await OpenFolderTabAsync(target, recordPath);
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        bool shown = ReferenceEquals(tab, SelectedTab);
        if (mode == NavMode.New)
        {
            Push(tab.BackStack, shown ? CurrentEntry() : new NavEntry(tab.Location, tab.SavedScrollOffset));
            tab.ForwardStack.Clear();
        }

        if (!shown)
        {
            SelectedTab.SavedScrollOffset = View?.GetScrollOffset() ?? 0;
            SelectedTab = tab;
        }

        NotifyNav();
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
        if (_restoring.Remove(tab) && outcome is LoadOutcome.NotFound or LoadOutcome.Error && !tab.IsTop)
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

    partial void OnSidebarWidthChanged(double value)
    {
        double clamped = AppSettings.ClampSidebarWidth(value);
        if (clamped != value)
        {
            SidebarWidth = clamped;
            return;
        }

        _services.Stores.Settings.Update(s => s.SidebarWidth = clamped);
    }

    // ---- card order: sort modes, filter and drag & drop -------------------------------------

    /// <summary>
    /// Changes the selected tab's sort mode (saved per tab; the folder tabs share one, so all of
    /// them follow) and re-orders the cards in place.
    /// </summary>
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
        if (tab.IsFolderTab)
        {
            foreach (var other in FolderTabs.Where(t => !ReferenceEquals(t, tab)))
            {
                other.SortMode = mode;
                other.ApplyOrder();
            }
        }

        View?.ScrollTo(0);
    }

    /// <summary>
    /// Changes what the top level of the selected tab (最近の履歴 / よく使う項目) shows: everything,
    /// folders only or files only. Saved per tab; the cards are filtered in place.
    /// </summary>
    public void SetFilter(ItemFilter filter)
    {
        View?.CloseCascade();
        var tab = SelectedTab;
        if (tab.IsFolderTab || tab.Filter == filter)
        {
            return;
        }

        string key = TabKinds.ToSetting(tab.Kind);
        _services.Stores.Settings.Update(s => s.SetFilter(key, filter));
        tab.Filter = filter;
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

        // Cards hidden by the filter keep their slots; the shown ones take the new order.
        var all = ItemFilters.MergeShownOrder(tab.OrderedCards, cards, tab.IsShown);
        bool switched = tab.SortMode != CardSortMode.Manual;
        if (switched)
        {
            SaveSortMode(tab, CardSortMode.Manual);
        }

        tab.SetBaseOrder(all);
        SaveManualOrder(tab, all);
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
    /// Saves <paramref name="shown"/> (every card of the list, in order) as the manual order of the
    /// tab's top level in card_order.json: these paths first, then the previously saved ones that
    /// are not listed now. (The order of 登録フォルダ is registered_folders.json: <see cref="MoveRegistered"/>.)
    /// </summary>
    private void SaveManualOrder(TabViewModel tab, IReadOnlyList<CardViewModel> shown)
    {
        if (OrderListOf(tab.Kind) is not { } list)
        {
            return;
        }

        var paths = shown.Select(c => c.Path).ToList();
        var showing = new HashSet<string>(paths, PathTools.Comparer);
        var hidden = _services.Stores.CardOrders.Get(list).Where(p => !showing.Contains(p));
        _services.Stores.CardOrders.Set(list, paths.Concat(hidden));
    }

    /// <summary>The card_order.json list of a fixed tab, or null for a folder tab (no top level to order).</summary>
    private static CardOrderList? OrderListOf(TabKind kind) => kind switch
    {
        TabKind.Recent => CardOrderList.Recent,
        TabKind.Frequent => CardOrderList.Frequent,
        _ => null,
    };

    /// <summary>True when the tab's top level has a saved drag &amp; drop order in card_order.json.</summary>
    private bool HasSavedOrder(TabKind kind) => OrderListOf(kind) is { } list && _services.Stores.CardOrders.HasOrder(list);

    /// <summary>
    /// 「手動の並びをリセット」 is offered on 最近の履歴 and よく使う項目, at any level (a folder tab
    /// has no manual order).
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
                // From the sidebar (a row, or its cascade): a folder tab of its own. Otherwise the shown tab moves.
                return item.NewTab
                    ? OpenFolderTabAsync(target, recordPath: item.Path)
                    : NavigateAsync(target, NavMode.New, recordPath: item.Path);
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
        foreach (var tab in AllTabs)
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
    /// Pinning a card of a sorted top level: the card takes its index in the sorted order (counted
    /// with the cards the filter hides) as its base-order slot (only that card moves in the base
    /// order), so it stays where it is once pinned (pinned cards sit at their base-order slot). The
    /// move is saved in card_order.json only for a tab that already has a saved order (cards were
    /// dragged there); otherwise it is kept in memory only and a later reload puts the card at its
    /// default slot (saving would freeze the recency / usage order for good). Returns true in that
    /// last case.
    /// </summary>
    private bool KeepInPlace(TabViewModel tab, string path)
    {
        if (!tab.CanReorder || tab.SortMode == CardSortMode.Manual)
        {
            return false;
        }

        var target = new PathSet([path]);
        var shown = tab.OrderedCards;
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

        if (!HasSavedOrder(tab.Kind))
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
        foreach (var tab in AllTabs)
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

    private void RegisteredChanged() => RefreshRegistered();

    /// <summary>
    /// Brings the sidebar rows in line with registered_folders.json (same order; rows of folders
    /// that are still registered are kept, with what is known about them) and checks whether the
    /// new folders exist. <paramref name="recheck"/> (F5) checks every folder again.
    /// </summary>
    private void RefreshRegistered(bool recheck = false)
    {
        var known = new Dictionary<string, SidebarItemViewModel>(StringComparer.Ordinal);
        foreach (var item in Registered)
        {
            known.TryAdd(item.Id, item);
        }

        var items = _services.Stores.Registered.Folders
            .Select(f => known.TryGetValue(f.Id, out var item) && string.Equals(item.Path, f.Path, StringComparison.Ordinal)
                ? item
                : new SidebarItemViewModel(f.Id, f.Path, _services.Icons))
            .ToList();

        // Minimal changes: the rows that stay keep their elements (an open cascade is anchored to one).
        for (int i = Registered.Count - 1; i >= 0; i--)
        {
            if (!items.Contains(Registered[i]))
            {
                Registered.RemoveAt(i);
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i < Registered.Count && ReferenceEquals(Registered[i], items[i]))
            {
                continue;
            }

            int at = Registered.IndexOf(items[i]);
            if (at >= 0)
            {
                Registered.Move(at, i);
            }
            else
            {
                Registered.Insert(i, items[i]);
            }
        }

        OnPropertyChanged(nameof(HasNoRegistered));
        OnPropertyChanged(nameof(RegisteredCountText));
        foreach (var item in items)
        {
            if (recheck || item.State == SidebarItemState.Checking)
            {
                _ = CheckRegisteredAsync(item);
            }
        }
    }

    /// <summary>§6.3: a folder that is gone, or does not answer within 2 s, is shown as such (and opens nothing).</summary>
    private async Task CheckRegisteredAsync(SidebarItemViewModel item)
    {
        try
        {
            var (state, _) = await Existence.ProbeAsync(_services.FileSystem, item.Path, RegisteredCheckTimeout, CancellationToken.None);
            item.State = state switch
            {
                ExistState.Folder => SidebarItemState.Ok,
                ExistState.Timeout => SidebarItemState.NoResponse,
                _ => SidebarItemState.Missing,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Existence check of {item.Path} failed", ex);
        }
    }

    /// <summary>A click on a 登録フォルダ of the sidebar: opens it in a folder tab.</summary>
    public Task OpenRegisteredAsync(SidebarItemViewModel item) =>
        item.IsDisabled ? Task.CompletedTask : OpenFolderTabAsync(item.Target, recordPath: item.Path);

    /// <summary>The sidebar row of a registered folder, by path (the right-click menu only has the path).</summary>
    public SidebarItemViewModel? FindRegistered(string path) =>
        Registered.FirstOrDefault(i => PathTools.PathEquals(PathTools.NormalizeOrSelf(i.Path), PathTools.NormalizeOrSelf(path)));

    public bool IsRegistered(string path) => FindRegistered(path) is not null;

    /// <summary>Takes a folder off the sidebar after a confirmation. Tabs that show it stay open.</summary>
    public void Unregister(SidebarItemViewModel item)
    {
        bool ok = View?.Confirm("登録を解除しますか？", "一覧から外すだけで、フォルダやファイルは削除されません。", item.Path, "登録を解除", danger: true) ?? false;
        if (!ok)
        {
            return;
        }

        if (_services.Stores.Registered.Remove(item.Id))
        {
            Message.Success($"「{item.Name}」の登録を解除しました。");
        }

        RefreshRegistered();
    }

    /// <summary>
    /// A sidebar row was dropped at <paramref name="insertIndex"/> (0…Count, counted with the row
    /// still in place): the new order is saved in registered_folders.json.
    /// </summary>
    public void MoveRegistered(SidebarItemViewModel item, int insertIndex)
    {
        var items = Registered.ToList();
        int from = items.IndexOf(item);
        insertIndex = Math.Clamp(insertIndex, 0, items.Count);
        if (from < 0 || insertIndex == from || insertIndex == from + 1)
        {
            return;
        }

        items.RemoveAt(from);
        items.Insert(insertIndex > from ? insertIndex - 1 : insertIndex, item);
        _services.Stores.Registered.Reorder(items.Select(i => i.Id).ToList());
        RefreshRegistered();
    }

    // ---- × on top-level cards (§6.7) --------------------------------------------------------

    public void RemoveCard(CardViewModel card)
    {
        View?.CloseCascade();
        if (card.OwnerTab == TabKind.Recent)
        {
            ExcludeFromRecent(card);
        }
        else if (card.OwnerTab == TabKind.Frequent)
        {
            RemoveFromFrequent(card);
        }
    }

    public void ExcludeFromRecent(CardViewModel card)
    {
        _services.Stores.Excluded.Exclude(card.Path);
        RecentTab.RemoveCard(card); // the count and the empty text follow
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
        if (before.Theme != after.Theme)
        {
            View?.ApplyTheme(AppThemes.FromSetting(after.Theme));
        }

        _services.ShowHidden = after.ShowHidden;
        if (before.ShowHidden != after.ShowHidden)
        {
            foreach (var tab in AllTabs)
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
