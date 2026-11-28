using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Listing;
using FolderDeck.Core.Ordering;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Selection;
using FolderDeck.Core.Stores;
using FolderDeck.Core.Transfer;
using FolderDeck.Services;
using FolderDeck.Shell;
using DetailsColumnRules = FolderDeck.Core.Layout.DetailsColumns;

namespace FolderDeck.ViewModels;

/// <summary>What the view model needs from the window (dialogs, scroll position, cascade).</summary>
public interface IMainView
{
    void CloseCascade();

    /// <summary>Closes the cascade of a card only: the card grid changed, a list beside a sidebar row is not affected.</summary>
    void CloseCardCascade();

    double GetScrollOffset();

    void ScrollTo(double offset);

    bool Confirm(string title, string body, string? path, string primaryText, bool danger);

    /// <summary>The window handle that owns shell dialogs (Recycle Bin progress, "file in use"…).</summary>
    nint WindowHandle { get; }

    IReadOnlyList<string>? PickFolders();

    bool ShowSettings(SettingsViewModel settings);

    /// <summary>Switches the colours to the theme chosen in the settings (system / light / dark).</summary>
    void ApplyTheme(AppTheme theme);

    /// <summary>Resizes the text of the content areas to 文字の大きさ (<see cref="AppSettings.TextScale"/>, percent).</summary>
    void ApplyTextScale(int percent);

    /// <summary>The edit box of a name is open, or a new folder / a typed name is being processed: no reload meanwhile.</summary>
    bool IsNameBusy { get; }

    /// <summary>Something on screen that a replaced item list would break is under way (see MainWindow): the auto refresh waits.</summary>
    bool IsAutoRefreshBlocked { get; }
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

    /// <summary>How long a search for a renamed or moved folder may take (<see cref="FolderIdentity.Locate"/>).</summary>
    private static readonly TimeSpan LocateTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long the address bar waits for the disk to say what a typed path is.</summary>
    private static readonly TimeSpan AddressCheckTimeout = TimeSpan.FromSeconds(5);

    private readonly AppServices _services;
    private readonly HashSet<PaneViewModel> _restoring = [];
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    /// <summary>
    /// <see cref="Tabs"/> holds what settings.json said (<see cref="InitializeAsync"/> got that far).
    /// Until then <see cref="SaveState"/> leaves <c>open_tabs</c> alone: a start that failed halfway
    /// must not replace the saved tabs with a partial list.
    /// </summary>
    private bool _tabsRestored;

    /// <summary>CardShape is being set to the shown tab's (tab switch, startup): not the user's choice, nothing is saved.</summary>
    private bool _syncingView;

    /// <summary>
    /// Sort mode of the tab used last (the shown one; kept while no tab is open). A tab that joins the
    /// strip starts with it (<see cref="InheritViewState"/>); saved as <c>last_sort_mode</c>.
    /// </summary>
    private CardSortMode _lastSortMode;

    /// <summary>The shown tab; <see cref="NoTab"/> (never null) while no tab is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivePane), nameof(CanGoBack), nameof(CanGoForward), nameof(HasTab))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private TabLayoutViewModel _selectedTab;

    /// <summary>
    /// The pane of the shown tab that the toolbar, the address bar, the keys and the menus act on
    /// (<see cref="TabLayoutViewModel.ActivePane"/>); never null: the placeholder's pane while no tab is open.
    /// Announced with <see cref="SelectedTab"/>.
    /// </summary>
    public PaneViewModel ActivePane => SelectedTab.ActivePane;

    [ObservableProperty]
    private int _cardSize = AppSettings.CardSizeDefault;

    /// <summary>
    /// How the shown tab shows its items (toolbar, right of the sort button): cards of one of two
    /// shapes, or the details list. Mirrors <see cref="PaneViewModel.CardShape"/> of <see cref="ActivePane"/>
    /// (<see cref="OnSelectedTabChanged"/>); kept while no tab is open: what a new tab starts with.
    /// </summary>
    [ObservableProperty]
    private CardShape _cardShape = CardShape.Portrait;

    /// <summary>
    /// Widths of the columns of the details list (<c>details_columns</c>: always the known keys,
    /// name / modified / type / size and the extra columns). Replaced, never changed in place, by <see cref="SetDetailsColumnWidth"/>.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, double> _detailsColumns = DetailsColumnRules.Defaults;

    /// <summary>
    /// The extra columns shown in the details list (<c>details_extra_columns</c>, ids of
    /// <see cref="DetailsPropertyColumns"/>), chosen in the header's menu. Replaced by <see cref="SetDetailsColumnShown"/>.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _detailsExtraColumns = [];

    /// <summary>Width of the sidebar (DIP), changed with the splitter; kept while the sidebar is hidden.</summary>
    [ObservableProperty]
    private double _sidebarWidth = AppSettings.SidebarWidthDefault;

    /// <summary>The whole sidebar is hidden (the toggle at the left end of the tab row).</summary>
    [ObservableProperty]
    private bool _isSidebarCollapsed;

    public MainViewModel(AppServices services)
    {
        _services = services;

        // The stores are loaded before this is created. The sidebar's shape is read here, not in
        // InitializeAsync: the window lays itself out in its constructor, before the first frame.
        var settings = services.Stores.Settings.Current;
        Message.Placement = MessagePlacements.FromSetting(settings.MessagePosition); // before any message (startup warnings too)
        _sidebarWidth = settings.SidebarWidth;
        _isSidebarCollapsed = settings.SidebarCollapsed;
        _detailsColumns = DetailsColumnRules.Normalize(settings.DetailsColumns);
        _detailsExtraColumns = DetailsPropertyColumns.NormalizeShown(settings.DetailsExtraColumns);
        QuickSection = NewSection(SidebarSections.Quick, "クイック アクセス", settings);
        RegisteredSection = NewSection(SidebarSections.Registered, "登録フォルダ", settings);
        PcSection = NewSection(SidebarSections.Pc, "PC", settings);
        DriveList = new DriveListViewModel(services.Icons);

        RecentTab = new TabLayoutViewModel(new PaneViewModel(services, TabKind.Recent));
        FrequentTab = new TabLayoutViewModel(new PaneViewModel(services, TabKind.Frequent));
        NoTab = new TabLayoutViewModel(new PaneViewModel(services, TabKind.None) { IsStale = false });
        Watch(RecentPane);
        Watch(FrequentPane);
        _selectedTab = NoTab; // until InitializeAsync has restored the tabs
        NoTab.IsSelected = true;
        services.Stores.Pins.Changed += OnPinsChanged;
    }

    public IMainView? View { get; set; }

    public AppServices Services => _services;

    public MessageBarViewModel Message { get; } = new();

    /// <summary>最近の履歴: one instance for the whole session, open only while it is in <see cref="Tabs"/>.</summary>
    public TabLayoutViewModel RecentTab { get; }

    /// <summary>よく使う項目: one instance for the whole session, open only while it is in <see cref="Tabs"/>.</summary>
    public TabLayoutViewModel FrequentTab { get; }

    /// <summary>The one pane of <see cref="RecentTab"/> (a fixed tab is never split).</summary>
    private PaneViewModel RecentPane => RecentTab.ActivePane;

    /// <summary>The one pane of <see cref="FrequentTab"/> (a fixed tab is never split).</summary>
    private PaneViewModel FrequentPane => FrequentTab.ActivePane;

    /// <summary>
    /// What <see cref="SelectedTab"/> is while no tab is open (<see cref="TabKind.None"/>). Never in
    /// <see cref="Tabs"/>, never loaded, never a navigation target: commands that act on the shown
    /// tab check <see cref="HasTab"/> first.
    /// </summary>
    public TabLayoutViewModel NoTab { get; }

    /// <summary>
    /// The tab strip, left to right: every open tab, of any kind. All of them can be closed and
    /// moved; 最近の履歴 and よく使う項目 are in it at most once.
    /// </summary>
    public ObservableCollection<TabLayoutViewModel> Tabs { get; } = [];

    /// <summary>A tab is shown (false: every tab was closed and the empty state is on screen).</summary>
    public bool HasTab => !ActivePane.IsPlaceholder;

    /// <summary>The 登録フォルダ of the sidebar, in the order of registered_folders.json.</summary>
    public ObservableCollection<SidebarItemViewModel> Registered { get; } = [];

    public bool HasNoRegistered => Registered.Count == 0;

    public string RegisteredCountText => Registered.Count.ToString("N0");

    /// <summary>Sidebar section クイック アクセス: the rows that open 最近の履歴 and よく使う項目.</summary>
    public SidebarSectionViewModel QuickSection { get; }

    /// <summary>Sidebar section 登録フォルダ: the rows of <see cref="Registered"/>.</summary>
    public SidebarSectionViewModel RegisteredSection { get; }

    /// <summary>Sidebar section PC: the rows of <see cref="DriveList"/>.</summary>
    public SidebarSectionViewModel PcSection { get; }

    /// <summary>The drives of the PC section (read at startup, on F5, when a device comes or goes and when the window is activated).</summary>
    public DriveListViewModel DriveList { get; }

    public bool CanGoBack => ActivePane.BackStack.Count > 0;

    public bool CanGoForward => ActivePane.ForwardStack.Count > 0;

    private AppSettings Settings => _services.Stores.Settings.Current;

    /// <summary>常駐モード: closing the window only hides it (read at every close).</summary>
    public bool KeepRunningOnClose => Settings.KeepRunningOnClose;

    /// <summary>A setting of 常駐モード changed on OK in the settings (the app shows or removes the tray icon).</summary>
    public event Action? ResidentSettingsChanged;

    /// <summary>
    /// Every pane that holds data: the panes of the two fixed tabs (open or not: a closed one is
    /// reloaded when it is opened again, so it has to go stale with the others), then every pane of
    /// the open folder tabs.
    /// </summary>
    private IEnumerable<PaneViewModel> AllTabs => new[] { RecentPane, FrequentPane }.Concat(Tabs.Where(t => t.IsFolderTab).SelectMany(t => t.Panes));

    private void Watch(PaneViewModel tab)
    {
        tab.PropertyChanged += OnTabPropertyChanged;

        // A batch of the subfolders' matches replaces the list: the user may be scrolling through it.
        tab.AroundDeepBatch = show =>
        {
            if (!ReferenceEquals(tab, ActivePane) || View is not { } view)
            {
                show();
                return;
            }

            double offset = view.GetScrollOffset();
            show();
            view.ScrollTo(offset);
        };
    }

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PaneViewModel.Location))
        {
            GoUpCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(PaneViewModel.Cards) && ReferenceEquals(sender, ActivePane))
        {
            View?.CloseCardCascade(); // the grid rebuilds its cards: an open overlay would be left behind
        }
    }

    partial void OnSelectedTabChanged(TabLayoutViewModel? oldValue, TabLayoutViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        newValue.IsSelected = true;
        if (newValue.IsPlaceholder)
        {
            return; // no tab: the toolbar is hidden; the last tab's view stays, new tabs start with it
        }

        // Runs before SelectedTab (and ActivePane) is announced (generated setter), so the view swaps with
        // the new tab and ActiveList is already its view for the scroll position ShowTabAsync restores.
        var pane = newValue.ActivePane;
        _lastSortMode = pane.SortMode;
        if (CardShape != pane.CardShape)
        {
            View?.CloseCardCascade(); // the overlay was sized for the other shape
            SyncCardShape(pane.CardShape);
        }
    }

    /// <summary>Sets <see cref="CardShape"/> to the shown tab's without saving it or dropping scroll positions.</summary>
    private void SyncCardShape(CardShape shape)
    {
        _syncingView = true;
        try
        {
            CardShape = shape;
        }
        finally
        {
            _syncingView = false;
        }
    }

    /// <summary>A tab restored from open_tabs comes back with its own view (Normalize filled older entries).</summary>
    private static void RestoreViewState(PaneViewModel tab, OpenTabSetting entry) =>
        tab.SetViewState(CardShapes.FromSetting(entry.CardShape), CardSortModes.FromSetting(entry.SortMode));

    /// <summary>
    /// A tab that joins the strip now (not restored) starts with the view of the tab used last: the shown
    /// one, or while no tab is open the last shown (across a restart too: card_shape / last_sort_mode).
    /// Call before the tab is shown. The three callers: OpenFolderTabAsync, OpenQuickTabAsync, NavigateAsync.
    /// </summary>
    private void InheritViewState(PaneViewModel tab) =>
        tab.SetViewState(HasTab ? ActivePane.CardShape : CardShape, HasTab ? ActivePane.SortMode : _lastSortMode);

    // ---- startup / shutdown -----------------------------------------------------------------

    /// <summary>
    /// Restores the tab strip (<c>open_tabs</c>, in order, each tab where it was) and the sidebar,
    /// and shows the tab that was shown (§5.5). Not recorded in history (§10.4).
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = Settings;
        CardSize = settings.CardSize;
        SyncCardShape(CardShapes.FromSetting(settings.CardShape)); // the selected tab's follows below
        _lastSortMode = CardSortModes.FromSetting(settings.LastSortMode);
        SidebarWidth = settings.SidebarWidth;

        // A folder tab whose folder is gone stays, with its error banner: the user closes it. A fixed
        // tab falls back to its top level instead (_restoring). An entry that cannot be shown is
        // dropped; the selection then moves to the tab after it.
        int selected = -1;
        var open = settings.OpenTabs ?? [];
        for (int i = 0; i < open.Count; i++)
        {
            if (i == settings.SelectedTab)
            {
                selected = Tabs.Count;
            }

            var entry = open[i];
            bool located = entry.Root.Length > 0 && PathTools.IsSafeRel(entry.Path);
            TabKind kind = TabKinds.FromSetting(entry.Kind);
            if (kind == TabKind.Folder)
            {
                if (located)
                {
                    var location = new NavLocation(TabKind.Folder, entry.Root, entry.Path);
                    var restored = AddFolderTab(location).ActivePane;
                    restored.SetIdentity(location, entry.FileId);
                    RestoreViewState(restored, entry);
                }

                continue;
            }

            var fixedTab = FixedTabOf(kind);
            if (fixedTab.IsOpen)
            {
                continue;
            }

            var tab = fixedTab.ActivePane;
            if (located)
            {
                tab.Location = new NavLocation(kind, entry.Root, entry.Path);
                tab.SetIdentity(tab.Location, entry.FileId);
                _restoring.Add(tab);
            }

            RestoreViewState(tab, entry);
            AddToStrip(fixedTab);
        }

        RefreshRegistered();
        _ = DriveList.RefreshAsync(recheck: false); // not awaited: a drive that does not answer must not delay the first tab
        OpenInTools.Refresh(); // in the background: the right-click menus' 「… で開く」
        SelectedTab =Tabs.Count == 0 ? NoTab : Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)];
        _tabsRestored = true; // before the first await: what the user does while the first tab loads is saved too
        ApplyAutoRefreshSetting(settings);
        if (HasTab)
        {
            await LoadTabAsync(ActivePane);
        }
    }

    public void SaveState(WindowPlacementSetting? window)
    {
        bool tabs = _tabsRestored;
        int selectedTab = Tabs.IndexOf(SelectedTab); // -1: no tab is open
        var openTabs = Tabs
            .Select(t => t.ActivePane) // one pane per tab: its entry is that pane's
            .Select(t => new OpenTabSetting
            {
                Kind = TabKinds.ToSetting(t.Kind),
                Root = t.Location.Root,
                Path = t.Location.Rel,
                FileId = t.Identity,
                CardShape = CardShapes.ToSetting(t.CardShape),
                SortMode = CardSortModes.ToSetting(t.SortMode),
            })
            .ToList();
        string lastShape = CardShapes.ToSetting(HasTab ? ActivePane.CardShape : CardShape);
        string lastSort = CardSortModes.ToSetting(HasTab ? ActivePane.SortMode : _lastSortMode);
        _services.Stores.Settings.Update(s =>
        {
            if (window is not null)
            {
                s.Window = window;
            }

            if (tabs)
            {
                s.OpenTabs = openTabs;
                s.SelectedTab = selectedTab;

                // The view of the tab used last: before the tabs are restored it is not known yet (defaults).
                s.CardShape = lastShape;
                s.LastSortMode = lastSort;
            }

            s.SidebarWidth = SidebarWidth;
            s.CardSize = CardSize;
        });
    }

    // ---- tabs and navigation (§8) -----------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectTab(TabLayoutViewModel? tab)
    {
        if (tab is null || tab == SelectedTab || !Tabs.Contains(tab))
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        await ShowTabAsync(tab);
        SaveLocations();
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab (<paramref name="back"/>): the next / previous tab of the strip,
    /// wrapping at the ends, through the same command as a click on the tab. Nothing with one tab.
    /// </summary>
    public void SelectAdjacentTab(bool back)
    {
        int index = Tabs.IndexOf(SelectedTab);
        if (Tabs.Count < 2 || index < 0)
        {
            return;
        }

        int next = (index + (back ? Tabs.Count - 1 : 1)) % Tabs.Count;
        SelectTabCommand.Execute(Tabs[next]);
    }

    /// <summary>
    /// Ctrl+1…8: the tab at <paramref name="index"/> (0-based) of the strip, nothing when there are
    /// fewer tabs; <see cref="int.MaxValue"/> (Ctrl+9, as Explorer and browsers) the last tab.
    /// </summary>
    public void SelectTabAt(int index)
    {
        if (Tabs.Count == 0 || index < 0 || (index >= Tabs.Count && index != int.MaxValue))
        {
            return;
        }

        SelectTabCommand.Execute(Tabs[Math.Min(index, Tabs.Count - 1)]);
    }

    /// <summary>
    /// Makes <paramref name="tab"/> the shown one (a tab of <see cref="Tabs"/>, or <see cref="NoTab"/>
    /// when the last one was closed). The tab left remembers its scroll position; the lists of
    /// 最近の履歴 / よく使う項目 are rebuilt each time they are shown, a folder is listed again only
    /// when it went stale and otherwise comes back where it was scrolled to.
    /// </summary>
    private async Task ShowTabAsync(TabLayoutViewModel tab)
    {
        bool switched = !ReferenceEquals(tab, SelectedTab);
        var pane = tab.ActivePane;
        double offset = switched ? pane.SavedScrollOffset : View?.GetScrollOffset() ?? 0;
        if (switched)
        {
            LeaveSelectedTab();
            SelectedTab = tab;
        }

        if (tab.IsPlaceholder)
        {
            return; // nothing to load, nothing to scroll
        }

        bool top = pane.IsTop && !pane.IsFolderTab;
        bool reload = pane.IsStale || (top && switched);
        if (reload)
        {
            await LoadTabAsync(pane);
        }

        if ((switched || reload) && ReferenceEquals(pane, ActivePane))
        {
            View?.ScrollTo(top && switched ? 0 : offset);
        }
    }

    /// <summary>
    /// The shown tab is about to be replaced: it remembers where it was scrolled to. Not a tab that
    /// was just closed (a fixed tab starts fresh when it is opened again), nor the placeholder.
    /// </summary>
    private void LeaveSelectedTab()
    {
        if (SelectedTab.IsOpen)
        {
            ActivePane.SavedScrollOffset = View?.GetScrollOffset() ?? 0;
        }
    }

    /// <summary>
    /// Opens 最近の履歴 or よく使う項目 (the quick rows of the sidebar, the buttons of the empty
    /// state). Not open: it joins the strip at the right end and is shown. Open: it is shown. Already
    /// shown and inside a folder: back to its list.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    public async Task OpenQuickTabAsync(TabKind kind)
    {
        TabLayoutViewModel? tab = kind switch
        {
            TabKind.Recent => RecentTab,
            TabKind.Frequent => FrequentTab,
            _ => null,
        };
        if (tab is null)
        {
            return;
        }

        if (ReferenceEquals(tab, SelectedTab))
        {
            if (!tab.ActivePane.IsTop)
            {
                await NavigateAsync(NavLocation.Top(kind), NavMode.New);
            }

            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        if (!tab.IsOpen)
        {
            InheritViewState(tab.ActivePane); // SelectedTab is still the tab used last
            AddToStrip(tab);
        }

        await ShowTabAsync(tab);
        SaveLocations();
    }

    /// <summary>
    /// Opens a folder in a folder tab of its own: what a click on a 登録フォルダ of the sidebar, or
    /// on a row / header of the cascade opened from it, does. A folder tab that already shows that
    /// very folder is selected instead of opening a second one (a fixed tab inside the same folder
    /// does not count). A target without a root is no folder: nothing opens.
    /// </summary>
    public async Task OpenFolderTabAsync(NavLocation target, string? recordPath = null)
    {
        if (target.Root.Length == 0)
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        var tab = Tabs.FirstOrDefault(t => t.IsFolderTab && SameLocation(t.ActivePane.Location, target));
        if (tab is null)
        {
            tab = AddFolderTab(target with { Tab = TabKind.Folder });
            InheritViewState(tab.ActivePane); // SelectedTab is still the tab used last
        }

        await ShowTabAsync(tab);
        if (recordPath is not null && tab.ActivePane.BannerText is null)
        {
            RecordUse(recordPath, ItemKind.Folder);
        }

        SaveLocations();
    }

    private TabLayoutViewModel AddFolderTab(NavLocation location, int? at = null)
    {
        var pane = new PaneViewModel(_services, TabKind.Folder, location) { CutPaths = _cutPaths };
        Watch(pane);
        var tab = new TabLayoutViewModel(pane);
        AddToStrip(tab, at);
        return tab;
    }

    /// <summary>
    /// Puts a tab at the right end of the strip, or at <paramref name="at"/> (a reopened or
    /// duplicated tab). <see cref="TabLayoutViewModel.IsOpen"/> follows <see cref="Tabs"/>.
    /// </summary>
    private void AddToStrip(TabLayoutViewModel tab, int? at = null)
    {
        if (at is int index)
        {
            Tabs.Insert(ClosedTabStack.InsertIndex(index, Tabs.Count), tab);
        }
        else
        {
            Tabs.Add(tab);
        }

        tab.IsOpen = true;
    }

    // ---- 閉じたタブを開き直す・タブを複製 ------------------------------------------------------

    /// <summary>
    /// What a closed tab was: enough to open it again where it was, as it was (its back / forward
    /// history and scroll position too). Not its search or selection (a move clears those as well).
    /// Kept in memory only, as Explorer and the browsers do.
    /// </summary>
    private sealed record ClosedTab(
        TabKind Kind, NavLocation Location, string? Identity, CardShape Shape, CardSortMode Sort,
        IReadOnlyList<NavEntry> Back, IReadOnlyList<NavEntry> Forward, double ScrollOffset);

    private readonly ClosedTabStack<ClosedTab> _closedTabs = new();

    /// <summary>A closed tab can be opened again (the tab strip's menu).</summary>
    public bool CanReopenClosedTab => _closedTabs.CanPop;

    /// <summary>
    /// Records <paramref name="tab"/> before it leaves the strip (every way of closing passes
    /// <see cref="RemoveFromStrip"/>). A copy of the history: <see cref="PaneViewModel.ResetToTop"/>
    /// clears a fixed tab's lists right after. A fixed tab at its list with no history is not
    /// recorded: reopening it would be the same as opening it from the sidebar.
    /// </summary>
    private void RecordClosed(TabLayoutViewModel closing, int index)
    {
        var tab = closing.ActivePane; // one pane per tab: the record is that pane's
        if (tab.IsPlaceholder || (!tab.IsFolderTab && tab.Location.IsTop && tab.BackStack.Count == 0 && tab.ForwardStack.Count == 0))
        {
            return;
        }

        double offset = ReferenceEquals(tab, ActivePane) ? View?.GetScrollOffset() ?? 0 : tab.SavedScrollOffset;
        _closedTabs.Push(
            new ClosedTab(tab.Kind, tab.Location, tab.Identity, tab.CardShape, tab.SortMode, [.. tab.BackStack], [.. tab.ForwardStack], offset),
            index);
    }

    /// <summary>
    /// 「閉じたタブを開き直す」 (Ctrl + Shift + T, the tab strip's menu): the last closed tab comes back at
    /// its place with its location, view, sort mode, history and scroll position. A folder tab is a
    /// new tab even when another one shows that folder (as in Explorer and the browsers); a fixed tab
    /// that is open again is skipped (it exists once), and the one closed before it comes back. A
    /// folder that moved meanwhile is followed by its identity, as at startup.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ReopenClosedTab()
    {
        if (!_closedTabs.TryPop(c => c.Kind == TabKind.Folder || !FixedTabOf(c.Kind).IsOpen, out var closed, out int index))
        {
            Message.Info("開き直せる閉じたタブはありません。");
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        TabLayoutViewModel tab;
        if (closed.Kind == TabKind.Folder)
        {
            tab = AddFolderTab(closed.Location, index);
        }
        else
        {
            tab = FixedTabOf(closed.Kind);
            tab.ActivePane.Location = closed.Location;
            AddToStrip(tab, index);
        }

        await ShowRestoredTabAsync(tab, closed.Location, closed.Identity, closed.Shape, closed.Sort, closed.Back, closed.Forward, closed.ScrollOffset);
    }

    /// <summary>「タブを複製」 can copy <paramref name="tab"/>: a folder tab, or a fixed tab inside a folder (its list exists once).</summary>
    public static bool CanDuplicate(TabLayoutViewModel tab) =>
        !tab.IsPlaceholder && (tab.IsFolderTab || tab.ActivePane.FolderTargetPath is not null);

    /// <summary>
    /// 「タブを複製」 (the tab strip's menu, as Explorer's タブの複製): a new folder tab right of
    /// <paramref name="source"/> at the same folder, with its view, sort mode, history and scroll
    /// position, and shown. A fixed tab inside a folder gives a folder tab of that folder without
    /// the history: its entries belong to 最近の履歴 / よく使う項目 and Back would leave the tab.
    /// </summary>
    public async Task DuplicateTabAsync(TabLayoutViewModel sourceTab)
    {
        int index = Tabs.IndexOf(sourceTab);
        if (index < 0 || !CanDuplicate(sourceTab))
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        var source = sourceTab.ActivePane; // one pane per tab: the copy is of that pane
        double offset = ReferenceEquals(source, ActivePane) ? View?.GetScrollOffset() ?? 0 : source.SavedScrollOffset;
        bool folderTab = source.IsFolderTab;
        var location = folderTab ? source.Location : new NavLocation(TabKind.Folder, source.Location.Root, source.Location.Rel);
        var tab = AddFolderTab(location, index + 1);
        await ShowRestoredTabAsync(
            tab, location, source.Identity, source.CardShape, source.SortMode,
            folderTab ? [.. source.BackStack] : [], folderTab ? [.. source.ForwardStack] : [], offset);
    }

    private TabLayoutViewModel FixedTabOf(TabKind kind) => kind == TabKind.Recent ? RecentTab : FrequentTab;

    /// <summary>
    /// The common end of reopening and duplicating: the tab's own view and sort mode (not
    /// <see cref="InheritViewState"/>), history and scroll position, then it is shown (and listed).
    /// </summary>
    private async Task ShowRestoredTabAsync(
        TabLayoutViewModel tab, NavLocation location, string? identity, CardShape shape, CardSortMode sort,
        IReadOnlyList<NavEntry> back, IReadOnlyList<NavEntry> forward, double offset)
    {
        var pane = tab.ActivePane;
        pane.SetIdentity(location, identity); // a folder renamed or moved meanwhile is followed on load
        pane.SetViewState(shape, sort);
        pane.BackStack.Clear();
        pane.BackStack.AddRange(back);
        pane.ForwardStack.Clear();
        pane.ForwardStack.AddRange(forward);
        pane.SavedScrollOffset = offset;
        pane.IsStale = true;
        await ShowTabAsync(tab);
        SaveLocations();
    }

    /// <summary>
    /// Takes a tab off the strip. A folder tab is gone for good; a fixed tab lives on (still watched,
    /// still going stale with the others) and is reset, so it starts at its list when opened again.
    /// </summary>
    private void RemoveFromStrip(TabLayoutViewModel tab)
    {
        foreach (var pane in tab.Panes)
        {
            pane.CancelLoad();
        }

        RecordClosed(tab, Tabs.IndexOf(tab));
        Tabs.Remove(tab);
        tab.IsOpen = false;
        foreach (var pane in tab.Panes)
        {
            if (pane.IsFolderTab)
            {
                pane.PropertyChanged -= OnTabPropertyChanged;
            }
            else
            {
                _restoring.Remove(pane);
                pane.ResetToTop();
            }
        }
    }

    /// <summary>The same folder, however the two split it into a root and a path below it (a tab that went above its root folder).</summary>
    private static bool SameLocation(NavLocation a, NavLocation b) =>
        PathTools.PathEquals(PathTools.Combine(a.Root, a.Rel), PathTools.Combine(b.Root, b.Rel));

    /// <summary>
    /// Closes a tab of any kind (× / middle click / Ctrl + W). When it was the shown one, the tab to
    /// its right (else the left one) is shown; without any tab left, <see cref="NoTab"/>.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseTab(TabLayoutViewModel? tab)
    {
        int index = tab is null ? -1 : Tabs.IndexOf(tab);
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

        RemoveFromStrip(tab);
        if (wasSelected)
        {
            await ShowTabAsync(Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : NoTab);
        }

        SaveLocations();
    }

    /// <summary>Closes every tab but <paramref name="keep"/>, which is shown.</summary>
    public async Task CloseOtherTabsAsync(TabLayoutViewModel keep)
    {
        if (!Tabs.Contains(keep))
        {
            return;
        }

        View?.CloseCascade();
        foreach (var tab in Tabs.Where(t => !ReferenceEquals(t, keep)).ToList())
        {
            RemoveFromStrip(tab);
        }

        if (!ReferenceEquals(keep, SelectedTab))
        {
            Message.ClearPersistent();
            await ShowTabAsync(keep);
        }

        SaveLocations();
    }

    /// <summary>A tab was dragged to <paramref name="newIndex"/> of the strip.</summary>
    public void MoveTab(TabLayoutViewModel tab, int newIndex)
    {
        int from = Tabs.IndexOf(tab);
        newIndex = Math.Clamp(newIndex, 0, Tabs.Count - 1);
        if (from < 0 || from == newIndex)
        {
            return;
        }

        Tabs.Move(from, newIndex);
        SaveLocations();
    }

    [RelayCommand(CanExecute = nameof(CanGoUpNow), AllowConcurrentExecutions = true)]
    private Task GoUp()
    {
        var loc = ActivePane.Location;
        if (!HasTab || !ActivePane.CanGoUp)
        {
            return Task.CompletedTask;
        }

        // Above the root of a folder tab (a 登録フォルダ, a folder opened from the sidebar), its parent
        // becomes the tab's root; a fixed tab goes back to its list.
        NavLocation target = loc.Rel.Length > 0
            ? loc with { Rel = PathTools.ParentRel(loc.Rel) }
            : ActivePane.IsFolderTab
                ? new NavLocation(TabKind.Folder, PathTools.ParentOf(loc.Root), string.Empty)
                : NavLocation.Top(loc.Tab);
        return NavigateAsync(target, NavMode.New);
    }

    private bool CanGoUpNow() => ActivePane.CanGoUp;

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task GoBack()
    {
        var tab = ActivePane;
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
        var tab = ActivePane;
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
        if (crumb?.Target is null || crumb.Target == ActivePane.Location)
        {
            return Task.CompletedTask;
        }

        return NavigateAsync(crumb.Target, NavMode.New);
    }

    /// <summary>The button on the error banner: back to the fixed tab's list; a folder tab closes.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task BackToTop() => ActivePane.Kind switch
    {
        TabKind.None => Task.CompletedTask,
        TabKind.Folder => CloseTab(SelectedTab),
        _ => NavigateAsync(NavLocation.Top(ActivePane.Kind), NavMode.New),
    };

    [RelayCommand]
    private async Task Refresh()
    {
        View?.CloseCascade();
        Message.ClearPersistent();
        AppLog.Info(_services.DescribeMemory()); // before the caches are dropped: what they held
        _services.ClearCaches();
        _services.Thumbnails.Clear();
        foreach (var tab in AllTabs)
        {
            tab.IsStale = true; // the hidden tabs list again when they are shown
        }

        RefreshRegistered(recheck: true);
        _ = DriveList.RefreshAsync(recheck: true);
        if (HasTab)
        {
            await LoadTabAsync(ActivePane);
        }
    }

    /// <summary>
    /// Moves inside the shown tab (a card, a row of its cascade, a breadcrumb, back / forward).
    /// <paramref name="recordPath"/> is set when the move counts as a use (§10.4). A folder-tab
    /// location while anything but a folder tab is shown (a fixed tab, or no tab at all) opens a
    /// folder tab instead: the placeholder is never the tab that moves. A fixed tab that is the
    /// target but not open joins the strip.
    /// </summary>
    public async Task NavigateAsync(NavLocation target, NavMode mode, double restoreOffset = 0, string? recordPath = null)
    {
        if (target.Tab == TabKind.None)
        {
            return; // the placeholder has no locations
        }

        TabLayoutViewModel? owner = target.Tab switch
        {
            TabKind.Recent => RecentTab,
            TabKind.Frequent => FrequentTab,
            _ => ActivePane.IsFolderTab ? SelectedTab : null,
        };
        if (owner is null)
        {
            await OpenFolderTabAsync(target, recordPath);
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        var tab = owner.ActivePane; // the pane that moves: the active one of a folder tab, a fixed tab's one
        bool shown = ReferenceEquals(owner, SelectedTab);
        if (mode == NavMode.New)
        {
            Push(tab.BackStack, shown ? CurrentEntry() : new NavEntry(tab.Location, tab.SavedScrollOffset));
            tab.ForwardStack.Clear();
        }

        if (!shown)
        {
            if (!owner.IsOpen)
            {
                InheritViewState(tab); // SelectedTab is still the tab used last
                AddToStrip(owner);
            }

            LeaveSelectedTab();
            SelectedTab = owner;
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

    private async Task<LoadOutcome> LoadTabAsync(PaneViewModel tab)
    {
        var location = tab.Location;
        UiWatchdog.Note($"loading {tab.CurrentFolderPath ?? tab.Title}");
        var outcome = await tab.LoadAsync();
        int generation = tab.LoadGeneration;
        UiWatchdog.Note($"showing {tab.CurrentFolderPath ?? tab.Title}: {tab.Cards.Count:N0} items ({outcome})");

        // Not found: the folder may have been renamed or moved. The tab follows it when its identity
        // finds it, and lists it there. A check of the 登録フォルダ may move the tab first (before this
        // load ended, or while it looked for its folder): listed there too, unless the user went on
        // meanwhile (a navigation starts a load of its own: the generation moved).
        if (outcome == LoadOutcome.NotFound && !tab.IsTop
            && (tab.Location != location
                || await FollowMovedFolderAsync(tab)
                || (tab.Location != location && tab.LoadGeneration == generation)))
        {
            outcome = await tab.LoadAsync();
        }

        if (_restoring.Remove(tab) && outcome is LoadOutcome.NotFound or LoadOutcome.Error && !tab.IsTop)
        {
            // The remembered location is gone: show the tab's top level instead (§5.5).
            tab.Location = NavLocation.Top(tab.Kind);
            outcome = await tab.LoadAsync();
        }

        return outcome;
    }

    private NavEntry CurrentEntry() => new(ActivePane.Location, View?.GetScrollOffset() ?? 0);

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

    // ---- the address bar ----------------------------------------------------------------------

    /// <summary>
    /// Enter in the address bar: a folder is shown in this tab, a file opens, anything else runs as a
    /// command with the shown folder as its current directory (<c>cmd</c>, <c>wsl</c>, <c>code .</c>;
    /// at a top list or a missing folder in the user's folder), as in Explorer. Returns false when the
    /// edit box should stay open with the text (nothing at that path, the program did not start:
    /// said on the message line). <paramref name="leaving"/> runs just before a folder loads or a file
    /// opens (the view closes the edit box then, not after the load).
    /// </summary>
    public async Task<bool> GoToAddressAsync(string typed, Func<bool>? stillWanted = null, Action? leaving = null)
    {
        if (!HasTab)
        {
            return true;
        }

        var tab = ActivePane;
        var location = tab.Location;
        string? shown = tab.BannerText is null ? tab.FolderTargetPath : null; // a folder that is not there is no place to start in
        AddressTarget target;
        try
        {
            target = await Task.Run(() => AddressInput.Resolve(typed, shown, _services.FileSystem.GetKind)).WaitAsync(AddressCheckTimeout);
        }
        catch (TimeoutException)
        {
            if (stillWanted?.Invoke() == false || !ReferenceEquals(tab, ActivePane))
            {
                return true; // given up meanwhile: nothing to say about it
            }

            Message.Warning("応答がありません。ネットワークの接続とパスを確かめてください。");
            return false;
        }

        if (stillWanted?.Invoke() == false || !ReferenceEquals(tab, ActivePane) || tab.Location != location)
        {
            return true; // given up (Esc) or the user went elsewhere meanwhile: what was typed is not wanted here any more
        }

        switch (target.Action)
        {
            case AddressAction.None:
                return true;
            case AddressAction.NotFound:
                Message.Info($"見つかりません：{target.Path}");
                return false;
            case AddressAction.OpenFolder:
                leaving?.Invoke(); // the edit box goes before the folder loads
                await NavigateAsync(AddressLocation(tab, target.Path!), NavMode.New, recordPath: target.Path);
                return true;
            case AddressAction.OpenFile:
                leaving?.Invoke();
                await OpenFileAsync(target.Path!, null);
                return true;
            default:
                return await RunFromAddressAsync(target, shown);
        }
    }

    /// <summary>
    /// Where a typed folder goes in <paramref name="tab"/>: below the tab's root when it is inside it
    /// (the breadcrumb keeps the root), else it becomes the root (of a folder tab, or a folder opened
    /// inside 最近の履歴 / よく使う項目).
    /// </summary>
    private static NavLocation AddressLocation(PaneViewModel tab, string path)
    {
        var location = tab.Location;
        return !location.IsTop && PathTools.RelativeTo(location.Root, path) is { } rel
            ? location with { Rel = rel }
            : new NavLocation(tab.Kind, path, string.Empty);
    }

    private async Task<bool> RunFromAddressAsync(AddressTarget target, string? shown)
    {
        string folder = shown ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var spec = await Task.Run(() => TerminalCommand.Build(target.Program!, target.Arguments, folder, Environment.SystemDirectory,
            name => ShellActions.FindProgram(name, excludedFolder: folder)));
        if (spec.Refusal is not null)
        {
            Message.Error(spec.Refusal);
            return false;
        }

        var result = await Task.Run(() => ShellActions.Launch(spec));
        if (!result.Success)
        {
            Message.Error(result.Error ?? "実行できませんでした。");
            return false;
        }

        if (spec.Note is not null)
        {
            Message.Info(spec.Note);
        }
        else
        {
            Message.Success($"{target.Program} を起動しました。");
        }

        return true;
    }

    // ---- following renamed and moved folders --------------------------------------------------

    /// <summary>
    /// A folder was renamed or moved (<paramref name="oldPath"/> → <paramref name="newPath"/>): the
    /// 登録フォルダ at or below it and every tab whose location, or a back / forward entry, is at or
    /// below it follow (in place: same row order, same tab). A tab that moved keeps its folder's
    /// identity and is listed again when shown; the caller lists the shown one. Pins, the history and
    /// the manual orders keep their old paths. True when anything moved.
    /// </summary>
    private bool ApplyRename(string oldPath, string newPath)
    {
        bool moved = false;
        if (_services.Stores.Registered.Repath(oldPath, newPath) > 0)
        {
            RefreshRegistered();
            moved = true;
        }

        NavLocation? Rebased(NavLocation location) =>
            PathRebase.RebaseLocation(location.Root, location.Rel, oldPath, newPath) is { } at
                ? location with { Root = at.Root, Rel = at.Rel }
                : null;

        foreach (var tab in AllTabs)
        {
            if (Rebased(tab.Location) is { } location)
            {
                tab.MoveTo(location);
                tab.IsStale = true;
                moved = true;
            }

            foreach (var stack in new[] { tab.BackStack, tab.ForwardStack })
            {
                for (int i = 0; i < stack.Count; i++)
                {
                    if (Rebased(stack[i].Location) is { } entry)
                    {
                        stack[i] = stack[i] with { Location = entry };
                    }
                }
            }
        }

        // The closed tabs follow too (their history has no identity to be found by).
        _closedTabs.ReplaceAll(c => c with
        {
            Location = Rebased(c.Location) ?? c.Location,
            Back = c.Back.Select(e => Rebased(e.Location) is { } at ? e with { Location = at } : e).ToList(),
            Forward = c.Forward.Select(e => Rebased(e.Location) is { } at ? e with { Location = at } : e).ToList(),
        });

        if (moved)
        {
            AppLog.Info($"Followed {oldPath} => {newPath}");
            View?.CloseCascade(); // its rows (and the sidebar row it may hang on) show the old paths
            SaveLocations();
        }

        return moved;
    }

    /// <summary>
    /// The folder last seen at <paramref name="oldPath"/> was found at <paramref name="newPath"/> by its
    /// identity: <see cref="ApplyRename"/>. When it looks like an ancestor was renamed in place (the
    /// names below it are the same), everything below that ancestor follows too, but only when that is
    /// proven: the old ancestor is gone and an identity known for it (a 登録フォルダ or a tab there) is
    /// the new folder's. Names alone do not tell a rename from a move into an existing folder whose
    /// old parent was deleted afterwards.
    /// </summary>
    private async Task<bool> FollowAsync(string oldPath, string newPath)
    {
        var (from, to) = PathRebase.WidestRename(oldPath, newPath);
        if (!PathTools.PathEquals(from, oldPath))
        {
            string? known = IdentityKnownAt(from);
            bool proven = known is not null
                && await OffThreadAsync(() => !Directory.Exists(from) && FolderIdentity.Capture(to) == known, RegisteredCheckTimeout);
            if (!proven)
            {
                (from, to) = (oldPath, newPath);
            }
        }

        return ApplyRename(from, to);
    }

    /// <summary>The identity last seen at <paramref name="path"/>: a 登録フォルダ's, or a tab's that shows that folder.</summary>
    private string? IdentityKnownAt(string path) =>
        _services.Stores.Registered.FindByPath(path)?.FileId
        ?? AllTabs.Where(t => t.CurrentFolderPath is { } shown && PathTools.PathEquals(shown, path))
            .Select(t => t.Identity)
            .FirstOrDefault(identity => identity is not null);

    /// <summary>
    /// The folder of <paramref name="tab"/> is not found: looked for by the identity it had there, or
    /// by the identity of the 登録フォルダ that holds it (the tab was opened from it, or below it).
    /// Found, the tab and everything else at or below the old path follow. True when the tab moved
    /// (the caller lists it again).
    /// </summary>
    private async Task<bool> FollowMovedFolderAsync(PaneViewModel tab)
    {
        var location = tab.Location;
        int generation = tab.LoadGeneration;
        if (location.IsTop || tab.CurrentFolderPath is not { } oldPath)
        {
            return false;
        }

        (string From, string To)? move = null;
        if (tab.IdentityOf(location) is { } identity && await LocateAsync(identity, oldPath) is { } found)
        {
            move = (oldPath, found);
        }
        else if (RegisteredHolding(oldPath) is { FileId: { } rootIdentity } registered
            && await OffThreadAsync(() => !Directory.Exists(registered.Path), RegisteredCheckTimeout) // still there: its rename explains nothing
            && await LocateAsync(rootIdentity, registered.Path) is { } foundRoot)
        {
            move = (registered.Path, foundRoot);
        }

        if (move is not { } m || tab.Location != location
            || PathRebase.RebaseLocation(location.Root, location.Rel, m.From, m.To) is null)
        {
            return false;
        }

        await FollowAsync(m.From, m.To);
        if (tab.Location == location || tab.LoadGeneration != generation)
        {
            return false; // nothing moved it, or the user went elsewhere meanwhile (that load is theirs)
        }

        Message.Info($"フォルダの名前か場所が変わっていたため、新しい場所を開きました：{tab.CurrentFolderPath}");
        return true;
    }

    /// <summary>The deepest 登録フォルダ with a known identity at or above <paramref name="path"/>.</summary>
    private RegisteredFolder? RegisteredHolding(string path) =>
        _services.Stores.Registered.Folders
            .Where(f => f.FileId is not null && PathTools.RelativeTo(f.Path, path) is not null)
            .MaxBy(f => f.Path.Length);

    /// <summary>
    /// A 登録フォルダ was found missing: looked for by its identity. Found, it (and what else was at or
    /// below its old path) follows, and its row is replaced by one at the new path. True when it moved.
    /// </summary>
    private async Task<bool> FollowRegisteredAsync(SidebarItemViewModel item)
    {
        var registered = _services.Stores.Registered;
        if (registered.FindById(item.Id) is not { FileId: { } identity } folder || !PathTools.PathEquals(folder.Path, item.Path)
            || await LocateAsync(identity, folder.Path) is not { } found)
        {
            return false;
        }

        if (registered.FindById(item.Id) is not { } now || !PathTools.PathEquals(now.Path, folder.Path))
        {
            return false; // moved by another check meanwhile, or unregistered
        }

        if (!await FollowAsync(folder.Path, found))
        {
            return false;
        }

        Message.Info($"登録フォルダ「{PathTools.DisplayName(folder.Path)}」の名前か場所が変わっていたため、新しい場所に合わせました：{found}");
        return true;
    }

    /// <summary>Remembers the identity of a 登録フォルダ that was just found where it is registered (saved only when it changed).</summary>
    private async Task RememberRegisteredIdentityAsync(SidebarItemViewModel item)
    {
        string? identity = await OffThreadAsync(() => FolderIdentity.Capture(item.Path), RegisteredCheckTimeout);
        if (identity is not null && _services.Stores.Registered.FindById(item.Id) is { } folder && PathTools.PathEquals(folder.Path, item.Path))
        {
            _services.Stores.Registered.SetFileId(item.Id, identity);
        }
    }

    private static Task<string?> LocateAsync(string identity, string lastKnownPath) =>
        OffThreadAsync(() => FolderIdentity.Locate(identity, lastKnownPath), LocateTimeout);

    /// <summary>Runs blocking file system work on the thread pool; gives up waiting after <paramref name="timeout"/> (default value then).</summary>
    private static async Task<T?> OffThreadAsync<T>(Func<T> work, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(work).WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return default;
        }
        catch (Exception ex)
        {
            AppLog.Warn("A folder check failed", ex);
            return default;
        }
    }

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

    /// <summary>
    /// The user changed the view mode (toolbar, UI Automation): it is the shown tab's from now on, saved in
    /// its open_tabs entry and as card_shape (the tab used last). Not while <see cref="SyncCardShape"/> runs.
    /// </summary>
    partial void OnCardShapeChanged(CardShape oldValue, CardShape newValue)
    {
        if (_syncingView)
        {
            return; // the shown tab brought its own view: nothing to save, its scroll positions fit it
        }

        View?.CloseCascade(); // the overlay covers the thumbnail, whose height changes; a row's list hangs on a row
        if (!HasTab)
        {
            SaveLocations(); // no tab (toolbar hidden, UI Automation): what a new tab starts with (card_shape)
            return;
        }

        var tab = ActivePane;
        tab.CardShape = newValue;
        SaveLocations(); // this tab's card_shape in open_tabs, and card_shape (the tab used last)
        if (CardShapes.IsCards(oldValue) != CardShapes.IsCards(newValue))
        {
            // Another view takes over this tab (card grid ↔ details list). A scroll position is an
            // offset of the view it was read from, so this tab's remembered ones (saved, back / forward)
            // are dropped rather than applied to the wrong view. Other tabs keep theirs: each is shown
            // in its own view. Runs before CardShape is announced, so ScrollTo(0) reaches the new view.
            tab.ResetScrollOffsets();
            View?.ScrollTo(0);
        }
    }

    /// <summary>
    /// A column of the details list was resized (<paramref name="key"/>: a <c>DetailsColumns</c> key).
    /// Saved at once, like every other layout choice; an unknown key or a width that changes
    /// nothing is ignored.
    /// </summary>
    /// <summary>The header's menu showed or hid the extra column <paramref name="id"/> (saved at once; the width it had comes back).</summary>
    public void SetDetailsColumnShown(string id, bool shown)
    {
        var ids = DetailsExtraColumns.Where(c => c != id).ToList();
        if (shown)
        {
            ids.Add(id);
        }

        var normalized = DetailsPropertyColumns.NormalizeShown(ids);
        DetailsExtraColumns = normalized;
        _services.Stores.Settings.Update(s => s.DetailsExtraColumns = [.. normalized]);
    }

    public void SetDetailsColumnWidth(string key, double width)
    {
        var widths = new Dictionary<string, double>(DetailsColumns, StringComparer.Ordinal);
        if (!widths.ContainsKey(key) || !double.IsFinite(width))
        {
            return;
        }

        widths[key] = width;
        var normalized = DetailsColumnRules.Normalize(widths);
        if (normalized[key] == DetailsColumns[key])
        {
            return;
        }

        DetailsColumns = normalized;
        _services.Stores.Settings.Update(s => s.DetailsColumns = new Dictionary<string, double>(normalized));
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

    // ---- sidebar: sections and the whole column -----------------------------------------------

    private SidebarSectionViewModel NewSection(string id, string title, AppSettings settings) =>
        new(id, title, !settings.IsSectionCollapsed(id), OnSectionChanged);

    /// <summary>
    /// A section was folded or unfolded: the rows below it move, so a list that hangs beside a row
    /// is in the wrong place. Saved at once, like every other layout choice.
    /// </summary>
    private void OnSectionChanged(SidebarSectionViewModel section)
    {
        View?.CloseCascade();
        bool collapsed = !section.IsExpanded;
        _services.Stores.Settings.Update(s => s.SetSectionCollapsed(section.Id, collapsed));
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        View?.CloseCascade(); // the rows are gone, or everything right of the sidebar moves
        _services.Stores.Settings.Update(s => s.SidebarCollapsed = value);
    }

    // ---- card order: sort modes, filter and drag & drop -------------------------------------

    /// <summary>
    /// Changes the selected tab's sort mode (saved per tab: the other tabs keep theirs) and re-orders
    /// the cards in place.
    /// </summary>
    public void SetSortMode(CardSortMode mode)
    {
        View?.CloseCascade();
        var tab = ActivePane;
        if (!HasTab || tab.SortMode == mode)
        {
            return;
        }

        SaveSortMode(tab, mode);
        tab.ApplyOrder();
        View?.ScrollTo(0);
    }

    /// <summary>
    /// Changes what the top level of the selected tab (最近の履歴 / よく使う項目) shows: everything,
    /// folders only or files only. Saved per tab; the cards are filtered in place.
    /// </summary>
    public void SetFilter(ItemFilter filter)
    {
        View?.CloseCascade();
        var tab = ActivePane;
        if (tab.IsFolderTab || tab.IsPlaceholder || tab.Filter == filter)
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
        card.IsTopLevel && ActivePane.CanReorder && ActivePane.Cards.Count > 1 && ActivePane.Cards.Contains(card);

    /// <summary>
    /// A card was dropped at <paramref name="insertIndex"/> (0…Count, counted with the card still in
    /// place). The shown order becomes the tab's manual order; a sort mode switches to 手動.
    /// </summary>
    public void MoveCard(CardViewModel card, int insertIndex)
    {
        var tab = ActivePane;
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

    /// <summary>
    /// Saves a tab's new sort mode: in its open_tabs entry, as last_sort_mode when it is the shown tab,
    /// and per kind in sort_modes (only an earlier exe reads that).
    /// </summary>
    private void SaveSortMode(PaneViewModel tab, CardSortMode mode)
    {
        tab.SortMode = mode; // first: SaveLocations reads it
        if (ReferenceEquals(tab, ActivePane))
        {
            _lastSortMode = mode;
        }

        string key = TabKinds.ToSetting(tab.Kind);
        _services.Stores.Settings.Update(s => s.SetSortMode(key, mode));
        SaveLocations();
    }

    /// <summary>
    /// Saves <paramref name="shown"/> (every card of the list, in order) as the manual order of the
    /// tab's top level in card_order.json: these paths first, then the previously saved ones that
    /// are not listed now. (The order of 登録フォルダ is registered_folders.json: <see cref="MoveRegistered"/>.)
    /// </summary>
    private void SaveManualOrder(PaneViewModel tab, IReadOnlyList<CardViewModel> shown)
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

    /// <summary>The card_order.json list of a fixed tab, or null for a folder tab and the placeholder (no top level to order).</summary>
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
    public bool ShowResetManualOrder => OrderListOf(ActivePane.Kind) is not null;

    /// <summary>The reset does something: the selected tab has a saved order (cards were dragged there).</summary>
    public bool CanResetManualOrder => HasSavedOrder(ActivePane.Kind);

    /// <summary>
    /// Forgets the selected tab's saved drag &amp; drop order: its top level goes back to the default
    /// order (最近の履歴: last used first, so re-opened items move to the front again; よく使う項目:
    /// pinned items, then the most used). Reloaded now
    /// when the tab shows its top level; inside a folder, the top level is rebuilt when it is shown.
    /// </summary>
    public async Task ResetManualOrderAsync()
    {
        View?.CloseCascade();
        var tab = ActivePane;
        if (OrderListOf(tab.Kind) is not { } list || !_services.Stores.CardOrders.Clear(list))
        {
            return;
        }

        Message.Info("手動の並びをリセットしました。");
        if (tab.IsTop && await tab.LoadAsync() == LoadOutcome.Ok && ReferenceEquals(tab, ActivePane) && tab.IsTop)
        {
            View?.ScrollTo(0);
        }
    }

    // ---- opening items (§6.8, §9.7, §12) ----------------------------------------------------

    /// <param name="card">The card or details row to open.</param>
    /// <param name="askBeforeProgram">
    /// false only for the double click of ダブルクリックで開く (on a card / details row): already
    /// deliberate, so a program runs without 実行形式の確認. Every other path (Enter, cascade rows and
    /// headers, previews, menus, the address bar, several items) keeps the default true.
    /// </param>
    public Task ActivateCardAsync(CardViewModel card, bool askBeforeProgram = true)
    {
        if (card.IsDisabled || card.IsBusy)
        {
            return Task.CompletedTask;
        }

        return OpenItemAsync(card.ToItemRef(), askBeforeProgram);
    }

    /// <summary>
    /// Enter: opens the selection of the shown list (Explorer's rule, <see cref="OpenPlan"/>). One item
    /// opens as a click on it (a folder moves the shown tab; a top-list folder opens its tab; a file opens
    /// and its use is recorded). Several: every file, then every folder in a folder tab of its own.
    /// Returns false when nothing is selected (the key is not used).
    /// </summary>
    public bool OpenSelection()
    {
        if (!HasTab)
        {
            return false;
        }

        var cards = ActivePane.SelectedCards();
        if (cards.Count == 0)
        {
            return false;
        }

        var plan = OpenPlan.Of(cards, c => c.IsFolderLike, c => !c.IsDisabled && !c.IsBusy);
        if (plan.Single is { } one)
        {
            _ = ActivateCardAsync(one);
        }
        else if (!plan.IsEmpty
            && (!plan.NeedsConfirm
                || (View?.Confirm("まとめて開く", $"{plan.Count} 個の項目を開きます。よろしいですか？", null, "開く", danger: false) ?? false)))
        {
            _ = OpenManyAsync(plan.Files, plan.Folders);
        }

        return true;
    }

    /// <summary>
    /// Alt+↓ (Explorer): the one selected folder, to enter as a click on it (the shown tab moves into it;
    /// a folder of the 最近の履歴 / よく使う項目 list opens its tab, <see cref="CardViewModel.ToItemRef"/>).
    /// Null unless exactly one item is selected and it is a usable folder (a file, several items: the key does nothing).
    /// </summary>
    public CardViewModel? FolderToEnter() =>
        HasTab && ActivePane.SelectedCards() is [{ IsFolderLike: true, IsDisabled: false, IsBusy: false } one] ? one : null;

    private async Task OpenManyAsync(IReadOnlyList<CardViewModel> files, IReadOnlyList<CardViewModel> folders)
    {
        foreach (var file in files)
        {
            await OpenFileAsync(file.Path, file); // one at a time: one confirmation per program, messages in order
        }

        foreach (var folder in folders)
        {
            var item = folder.ToItemRef();
            if (item.Target is { } target)
            {
                // The shown tab cannot move into two folders: each opens a folder tab (as Explorer opens windows).
                await OpenFolderTabAsync(target, item.Context == ItemContext.Drive ? null : item.Path);
            }
        }
    }

    /// <param name="item">The item to open.</param>
    /// <param name="askBeforeProgram">Passed to <see cref="OpenFileAsync"/> for a file (see <see cref="ActivateCardAsync"/>).</param>
    public Task OpenItemAsync(ItemRef item, bool askBeforeProgram = true)
    {
        if (item.IsFolder)
        {
            if (item.Target is { } target)
            {
                if (!item.NewTab && target == ActivePane.Location)
                {
                    return Task.CompletedTask; // the shown folder (the header of the list under the last crumb): as NavigateCrumb
                }

                // From the sidebar (a row, or its cascade): a folder tab of its own. Otherwise the shown tab moves.
                // A drive row is always at hand in the PC section: opening it is not a use worth remembering.
                string? recordPath = item.Context == ItemContext.Drive ? null : item.Path;
                return item.NewTab
                    ? OpenFolderTabAsync(target, recordPath)
                    : NavigateAsync(target, NavMode.New, recordPath: recordPath);
            }

            return Task.CompletedTask;
        }

        return OpenFileAsync(item.Path, item.Card, askBeforeProgram);
    }

    /// <param name="path">The file to open.</param>
    /// <param name="card">Its card, if any (busy while it opens).</param>
    /// <param name="askBeforeProgram">
    /// false skips 実行形式の確認 (<see cref="Settings"/>.ConfirmExecutables): only the double click of
    /// ダブルクリックで開く passes it (see <see cref="ActivateCardAsync"/>).
    /// </param>
    public async Task OpenFileAsync(string path, CardViewModel? card, bool askBeforeProgram = true)
    {
        Message.ClearPersistent();
        string name = PathTools.DisplayName(path);
        var settings = Settings;
        if (askBeforeProgram && settings.ConfirmExecutables && ShellActions.IsExecutable(path))
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
        RecentPane.IsStale = true;
        FrequentPane.IsStale = true;
        if (ActivePane.IsTop && ActivePane.Kind is TabKind.Recent or TabKind.Frequent && !ActivePane.IsLoading)
        {
            _ = ActivePane.LoadAsync();
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

        if (item.Context != ItemContext.Drive)
        {
            RecordUse(item.Path, item.IsFolder ? ItemKind.Folder : ItemKind.File);
        }
    }

    /// <summary>
    /// 「エクスプローラーで開く」 of the PC section: This PC in Explorer, in a new Explorer tab when the
    /// setting asks for it, else a new window. Nothing is recorded (not a path).
    /// </summary>
    public async Task OpenPcInExplorerAsync()
    {
        var result = Settings.OpenExplorerInNewTab
            ? await ExplorerTabs.OpenAsync(ShellActions.ThisPcPath)
            : await Task.Run(() => ShellActions.OpenInExplorer(ShellActions.ThisPcPath));
        if (!result.Success)
        {
            Message.Error(result.Error!);
        }
    }

    /// <summary>
    /// 「ターミナルで開く」「PowerShell で開く」 (開く / 管理者として開く), 「Code で開く」 and 「WSL で開く」 of
    /// the blank area and the item / selection menus: <paramref name="tool"/> in <paramref name="folder"/>, as <paramref name="programs"/>
    /// found it when the menu was built. Elevated (UAC) with <paramref name="elevated"/>. A SUBST drive
    /// goes as the folder it stands for and a mapped network drive as its UNC path when elevated or to WSL
    /// (neither sees the user's SUBST or mapped drives).
    /// Declining the UAC prompt says nothing; a failure says why on the message line; a start that
    /// worked says nothing (the window is the answer, as in Explorer). Not recorded as a use.
    /// </summary>
    public async Task OpenInAsync(OpenInTool tool, bool elevated, string folder, OpenInPrograms programs)
    {
        string target = elevated || tool == OpenInTool.Wsl
            ? await Task.Run(() =>
            {
                string real = DriveCatalog.SubstTargetOf(folder) ?? folder;
                return DriveCatalog.UncOf(real) ?? real;
            })
            : folder;
        if (OpenInCommand.Build(tool, elevated, target, programs, Environment.SystemDirectory) is not { } spec)
        {
            return; // not installed (the menu offers only what was found)
        }

        if (spec.Refusal is not null)
        {
            Message.Error(spec.Refusal);
            return;
        }

        // Off the UI thread: with runas ShellExecuteEx returns only once the UAC prompt is answered.
        var result = await Task.Run(() => ShellActions.Launch(spec, elevated));
        if (result.Cancelled)
        {
            return; // the UAC prompt was declined
        }

        if (!result.Success)
        {
            Message.Error(result.Error ?? "起動できませんでした。");
            return;
        }

        if (spec.Note is not null)
        {
            Message.Info(spec.Note); // WSL on another share: opened in the home directory
        }
    }

    public void CopyPath(ItemRef item) => CopyPaths([item]);

    /// <summary>パスをコピー: the paths as text, one per line (a selection gives several).</summary>
    public void CopyPaths(IReadOnlyList<ItemRef> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (ShellActions.CopyPath(string.Join(Environment.NewLine, items.Select(i => i.Path))))
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
    /// The same rule holds for the Delete key and for a selection of several items (every item must pass).
    /// </summary>
    public static bool CanDelete(ItemRef item) =>
        item.Context is ItemContext.InnerCard or ItemContext.Row && item.Card?.IsDisabled != true;

    /// <summary>How many paths the delete confirmation lists before "…ほか n 個".</summary>
    private const int DeleteConfirmPathLimit = 5;

    /// <summary>
    /// Moves the selection (or the one item of a menu) to the Recycle Bin after one confirmation,
    /// with one shell operation (one progress, one undo in Explorer). The cascade is closed by the
    /// caller. Never deletes permanently: when an item cannot be recycled, it and the rest are left
    /// as they are (<see cref="RecycleBin"/>). Refused as a whole when any item fails
    /// <see cref="CanDelete"/> (no partial delete) or while a paste / another delete runs.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<ItemRef> items)
    {
        int n = items.Count;
        if (n == 0)
        {
            return;
        }

        if (!items.All(CanDelete))
        {
            Message.Info("この一覧の項目は削除できません（ごみ箱に移動できるのはフォルダの中の項目だけです）。");
            return;
        }

        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return;
        }

        var (body, paths) = DeleteConfirmText(items);
        bool ok = View?.Confirm("ごみ箱に移動しますか？", body, paths, "ごみ箱に移動", danger: true) ?? false;
        if (!ok || _fileOpBusy)
        {
            if (ok)
            {
                Message.Info("前の操作が終わってから実行してください。");
            }

            return;
        }

        _fileOpBusy = true;
        var cards = items.Select(i => i.Card).OfType<CardViewModel>().ToList();
        RecycleResult result;
        try
        {
            foreach (var card in cards)
            {
                card.IsBusy = true;
            }

            result = await RecycleBin.MoveAsync(items.Select(i => i.Path).ToList(), View?.WindowHandle ?? 0);
        }
        finally
        {
            foreach (var card in cards)
            {
                card.IsBusy = false;
            }

            _fileOpBusy = false;
        }

        RecordFileOp(FileOpStep.Deleted(result.Items), changed: result.Recycled > 0); // only what reached the Recycle Bin
        ShowDeleteResult(result, items);

        // Even a cancelled or failed delete may have moved part of it: list again. The selection is
        // left to the reload (what is gone drops out; what stayed stays selected for another try).
        await ReloadAfterFileChangeAsync();
    }

    // ---- rename and new folder (F2, Ctrl+Shift+N) ------------------------------------------------

    /// <summary>
    /// The item can be renamed in its edit box: a card inside a folder (the rule of
    /// <see cref="CanDelete"/> without the cascade's rows, which have no edit box), usable, not busy
    /// and not a drive root.
    /// </summary>
    public static bool CanRename(ItemRef item) =>
        item.Context is ItemContext.InnerCard
        && item.Card is { IsDisabled: false, IsBusy: false }
        && !PathTools.IsRoot(item.Path);

    /// <summary>
    /// F2: the card whose edit box opens, or null (nothing selected: the key does nothing; a
    /// selection that cannot be renamed together: said on the message line). Whether it can be
    /// renamed is <see cref="CanStartRename"/>'s call. With several selected, <paramref name="batch"/>
    /// is their paths in display order (Explorer's "name (1)", "name (2)"…, <see cref="RenameManyAsync"/>)
    /// and the card is the focused one (the selection's <see cref="SelectionModel.Focus"/>: the item
    /// clicked last, else the first); otherwise empty.
    /// </summary>
    public CardViewModel? RenameCandidate(out IReadOnlyList<string> batch)
    {
        batch = [];
        var cards = ActivePane.SelectedCards();
        if (cards.Count <= 1)
        {
            return cards.Count == 1 ? cards[0] : null;
        }

        if (!CanRenameTogether(cards))
        {
            Message.Info("選択した項目には、名前を変更できないものが含まれています。");
            return null;
        }

        batch = cards.Select(c => c.Path).ToList();
        string? focus = ActivePane.Selection.Focus;
        return cards.FirstOrDefault(c => PathTools.PathEquals(c.Path, focus)) ?? cards[0];
    }

    /// <summary>Every card can be renamed (<see cref="CanRename"/>) and all are in one folder.</summary>
    private static bool CanRenameTogether(IReadOnlyList<CardViewModel> cards)
    {
        string folder = PathTools.ParentOf(cards[0].Path);
        return folder.Length > 0
            && cards.All(c => CanRename(c.ToItemRef()) && PathTools.PathEquals(PathTools.ParentOf(c.Path), folder));
    }

    /// <summary>The edit box may open for <paramref name="card"/> now; says why not on the message line.</summary>
    public bool CanStartRename(CardViewModel card)
    {
        if (card.Context != ItemContext.InnerCard)
        {
            Message.Info("この一覧の項目は名前を変更できません（変更できるのはフォルダの中の項目だけです）。");
            return false;
        }

        if (!CanRename(card.ToItemRef()) || !ActivePane.Cards.Contains(card))
        {
            Message.Info("この項目は名前を変更できません。");
            return false;
        }

        if (ActivePane.IsLoading)
        {
            Message.Info("フォルダの読み込みが終わってから変更してください。");
            return false;
        }

        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return false;
        }

        return true;
    }

    /// <summary>
    /// The edit box of <paramref name="card"/> closed with <paramref name="typed"/>: renames the item
    /// when the name changed (one shell operation, on Explorer's undo stack), then lists the folder
    /// again with the renamed item selected. Returns true when the edit box should open again with
    /// the typed name: another item has it, or the user declined to change the extension. A name
    /// that cannot be used is said on the message line and the edit ends (the caller checks it on
    /// Enter with <see cref="ItemNames.Check"/> and keeps the box open instead).
    /// </summary>
    public async Task<bool> RenameAsync(CardViewModel card, string typed)
    {
        // Compared before cleaning too: an untouched name with a leading space (made by another
        // tool) would otherwise come out "changed" by the cleaning alone.
        string name = ItemNames.Clean(typed);
        if (string.Equals(typed, card.Name, StringComparison.Ordinal)
            || string.Equals(name, card.Name, StringComparison.Ordinal)
            || name.Length == 0)
        {
            return false; // unchanged, or emptied: the old name stays (as in Explorer)
        }

        var problem = ItemNames.Check(name);
        if (problem != NameProblem.None)
        {
            Message.Info(ItemNames.MessageOf(problem));
            return false;
        }

        if (!CanRename(card.ToItemRef()))
        {
            return false;
        }

        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return false;
        }

        if (card.Kind == CardKind.File && ItemNames.ExtensionChanged(card.Name, name))
        {
            bool change = View?.Confirm(
                "拡張子を変更しますか？",
                "拡張子を変更すると、ファイルが使えなくなる可能性があります。",
                $"{card.Name} → {name}",
                "変更する",
                danger: false) ?? false;
            if (!change)
            {
                return true;
            }

            if (_fileOpBusy)
            {
                Message.Info("前の操作が終わってから実行してください。");
                return false;
            }
        }

        string folder = PathTools.ParentOf(card.Path);
        _fileOpBusy = true;
        ShellOpResult result;
        try
        {
            card.IsBusy = true;
            result = await ShellFileOps.RenameAsync(card.Path, name, View?.WindowHandle ?? 0);
        }
        finally
        {
            card.IsBusy = false;
            _fileOpBusy = false;
        }

        switch (result.Outcome)
        {
            case ShellOpOutcome.NameTaken:
                Message.Info($"同じ名前の項目がすでにあります：{name}");
                return true;
            case ShellOpOutcome.Canceled:
                Message.Info($"「{card.Name}」の名前の変更を中止しました。");
                return false;
            case ShellOpOutcome.Failed:
                Message.Error($"「{card.Name}」の名前を変更できませんでした：{result.Error ?? "不明なエラー"}");
                return false;
        }

        if (result.NewPath is { } now)
        {
            RecordFileOp(FileOpStep.Renamed([new PathMove(card.Path, now)])); // Ctrl+Z (MainViewModel.FileHistory)
        }

        if (card.Kind == CardKind.Folder && result.NewPath is { } renamed)
        {
            ApplyRename(card.Path, renamed); // the 登録フォルダ and tabs at or below it keep up with it
        }

        await ReloadAfterFileChangeAsync();

        // The old path left the selection with the reload. Anything selected now was chosen while
        // the rename ran (the press that committed the name was a click on another item): kept.
        if (ActivePane.Selection.Count == 0)
        {
            SelectInShownFolder(folder, result.NewPath);
        }

        return false;
    }

    /// <summary>
    /// The edit box of <paramref name="edited"/> closed with <paramref name="typed"/> for a batch of
    /// <paramref name="paths"/> (display order, <see cref="RenameCandidate"/>): renames them all
    /// "base (1)", "base (2)"… in one shell operation (one undo), lists the folder again and selects
    /// them, as Explorer does. The base is the typed name without the edited file's extension; each
    /// file keeps its own extension, so nothing asks about extensions. Nothing happens when the name
    /// did not change or was emptied; the edit box never opens again (each item keeps its name on a
    /// failure).
    /// </summary>
    public async Task RenameManyAsync(CardViewModel edited, string typed, IReadOnlyList<string> paths)
    {
        string name = ItemNames.Clean(typed);
        if (string.Equals(typed, edited.Name, StringComparison.Ordinal)
            || string.Equals(name, edited.Name, StringComparison.Ordinal)
            || name.Length == 0)
        {
            return; // unchanged, or emptied: every name stays (as in Explorer)
        }

        var problem = ItemNames.Check(name);
        string baseName = problem == NameProblem.None ? ItemNames.BatchBaseName(name, edited.Name, edited.Kind == CardKind.Folder) : string.Empty;
        if (problem != NameProblem.None || baseName.Length == 0)
        {
            Message.Info(ItemNames.MessageOf(problem == NameProblem.None ? NameProblem.Empty : problem));
            return;
        }

        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return;
        }

        // The cards shown now (a reload meanwhile made new instances), in display order.
        var wanted = new HashSet<string>(paths, PathTools.Comparer);
        var tab = ActivePane;
        var cards = tab.Cards.Where(c => wanted.Contains(c.Path)).ToList();
        string folder = PathTools.ParentOf(edited.Path);
        if (cards.Count == 0 || !CanRenameTogether(cards) || !PathTools.PathEquals(PathTools.ParentOf(cards[0].Path), folder))
        {
            Message.Info("選択した項目には、名前を変更できないものが含まれています。");
            return;
        }

        var items = cards.Select(c => new BatchRenameItem(c.Path, c.Name, c.Kind == CardKind.Folder)).ToList();
        var shownNames = tab.Cards.Select(c => c.Name).ToList();
        _fileOpBusy = true;
        BatchRenameResult result;
        try
        {
            foreach (var c in cards)
            {
                c.IsBusy = true;
            }

            result = await ShellFileOps.RenameManyAsync(folder, items, baseName, shownNames, View?.WindowHandle ?? 0);
        }
        finally
        {
            foreach (var c in cards)
            {
                c.IsBusy = false;
            }

            _fileOpBusy = false;
        }

        // One step for the whole batch (empty: nothing renamed, not recorded).
        RecordFileOp(FileOpStep.Renamed(result.Renamed.Select(r => new PathMove(r.OldPath, r.NewPath))));
        foreach (var r in result.Renamed)
        {
            if (cards.FirstOrDefault(c => PathTools.PathEquals(c.Path, r.OldPath)) is { Kind: CardKind.Folder })
            {
                ApplyRename(r.OldPath, r.NewPath); // the 登録フォルダ and tabs at or below it keep up with it
            }
        }

        if (result.Renamed.Count > 0)
        {
            await ReloadAfterFileChangeAsync();
        }

        // Explorer keeps the items selected, the edited one first (it stays the anchor). After the
        // reload only items of the batch that kept their name (skipped, failed) can still be
        // selected; anything else selected now was chosen while the rename ran (a click that
        // committed the name): kept.
        tab = ActivePane;
        if (tab.Selection.Snapshot().Keys.All(wanted.Contains) && PathTools.PathEquals(tab.TransferTargetPath, folder))
        {
            var newPaths = result.Renamed.ToDictionary(r => r.OldPath, r => r.NewPath, PathTools.Comparer);
            string Now(string path) => newPaths.TryGetValue(path, out var p) ? p : path;
            var keys = cards.Select(c => Now(c.Path)).Prepend(Now(edited.Path))
                .Where(p => tab.Cards.Any(c => PathTools.PathEquals(c.Path, p)));
            tab.Selection.Replace(keys);
        }

        int done = result.Renamed.Count;
        if (result.Error is { } error)
        {
            // Skipped items were left as they were on purpose: not failures.
            int failed = Math.Max(1, result.Requested - done - result.Skipped);
            Message.Error($"{failed} 個の項目の名前を変更できませんでした：{error}");
        }
        else if (result.Canceled)
        {
            Message.Info($"名前の変更を中止しました（{done} / {result.Requested} 個を変更済み）。");
        }
        else
        {
            Message.Info(result.Skipped > 0
                ? $"{done} 個の項目の名前を変更しました（{result.Skipped} 個はそのまま）。"
                : $"{done} 個の項目の名前を変更しました。");
        }
    }

    /// <summary>
    /// The card of the shown tab for <paramref name="path"/> (cards are made anew by every load, so
    /// one held since a menu opened may have been replaced), or null when it is not shown.
    /// </summary>
    public CardViewModel? ShownCard(string path) =>
        ActivePane.Cards.FirstOrDefault(c => PathTools.PathEquals(c.Path, path));

    /// <summary>
    /// Ctrl+Shift+N and the blank area's 新しいフォルダー: creates 「新しいフォルダー」 (or the next free
    /// 「新しいフォルダー (n)」) in the folder the shown tab lists, lists it again and selects the new
    /// folder. Returns its card for the edit box, or null (no folder on screen, a running file
    /// operation, a failure: said on the message line; the user went elsewhere meanwhile).
    /// </summary>
    public async Task<CardViewModel?> CreateFolderAsync()
    {
        string? folder = PasteFolderOf(null);
        if (folder is null)
        {
            Message.Info(HasTab && ActivePane.FolderTargetPath is not null && ActivePane.IsLoading
                ? "フォルダの読み込みが終わってから作成してください。"
                : "この一覧には新しいフォルダーを作成できません。");
            return null;
        }

        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return null;
        }

        // The name the shown items leave free; a hidden item with it makes the shell pick the next one.
        string name = ItemNames.NextNewFolderName(ActivePane.Cards.Select(c => c.Name));
        _fileOpBusy = true;
        ShellOpResult result;
        try
        {
            result = await ShellFileOps.NewFolderAsync(folder, name, View?.WindowHandle ?? 0);
        }
        finally
        {
            _fileOpBusy = false;
        }

        if (result.Outcome != ShellOpOutcome.Done || result.NewPath is null)
        {
            if (result.Outcome == ShellOpOutcome.Canceled)
            {
                Message.Info("新しいフォルダーの作成を中止しました。");
            }
            else
            {
                Message.Error($"新しいフォルダーを作成できませんでした：{result.Error ?? "不明なエラー"}");
            }

            return null;
        }

        RecordFileOp(FileOpStep.NewFolder(result.NewPath));
        await ReloadAfterFileChangeAsync();
        return SelectInShownFolder(folder, result.NewPath);
    }

    /// <summary>
    /// After a reload: selects <paramref name="path"/> alone when the shown tab still lists
    /// <paramref name="folder"/>, and returns its card (null otherwise).
    /// </summary>
    private CardViewModel? SelectInShownFolder(string folder, string? path)
    {
        var tab = ActivePane;
        if (path is null || !PathTools.PathEquals(tab.TransferTargetPath, folder))
        {
            return null;
        }

        var card = tab.Cards.FirstOrDefault(c => PathTools.PathEquals(c.Path, path));
        if (card is null)
        {
            return null;
        }

        tab.Selection.Replace([card.Path]);
        return card;
    }

    /// <summary>The confirmation's body and path lines: as before for one item, the count and the first paths for several.</summary>
    private static (string Body, string Paths) DeleteConfirmText(IReadOnlyList<ItemRef> items)
    {
        const string Notice = "\nごみ箱に移動できない場所（ネットワーク上のフォルダなど）にある項目は削除しません。";
        int n = items.Count;
        if (n == 1)
        {
            var item = items[0];
            string single = item.IsFolder
                ? $"フォルダ「{item.Name}」を、中のファイルやフォルダごとごみ箱に移動します。"
                : $"「{item.Name}」をごみ箱に移動します。";
            return (single + Notice, item.Path);
        }

        string body = $"{n:N0} 個の項目をごみ箱に移動します。"
            + (items.Any(i => i.IsFolder) ? "フォルダは中のファイルやフォルダごと移動します。" : "")
            + Notice;
        string paths = string.Join("\n", items.Take(DeleteConfirmPathLimit).Select(i => i.Path));
        if (n > DeleteConfirmPathLimit)
        {
            paths += $"\n…ほか {n - DeleteConfirmPathLimit:N0} 個";
        }

        return (body, paths);
    }

    /// <summary>The message line after a delete: the one-item wording as before, counts when only part went to the Recycle Bin.</summary>
    private void ShowDeleteResult(RecycleResult result, IReadOnlyList<ItemRef> items)
    {
        int n = items.Count;
        string what = n == 1 ? $"「{items[0].Name}」" : $"{n:N0} 個の項目";
        int r = result.Recycled;
        int rest = n - r;
        string err = result.Error ?? "不明なエラー";
        switch (result.Outcome)
        {
            case RecycleOutcome.Recycled:
                Message.Success($"{what}をごみ箱に移動しました。");
                break;
            case RecycleOutcome.Canceled:
                Message.Info(r == 0
                    ? $"{what}の削除を中止しました。"
                    : $"{r:N0} 個をごみ箱に移動し、残りの {rest:N0} 個は中止しました。");
                break;
            case RecycleOutcome.NotRecyclable:
                Message.Error(r == 0
                    ? $"{what}はごみ箱に移動できない場所にあるため、削除しませんでした。"
                    : $"{r:N0} 個をごみ箱に移動しました。残りの {rest:N0} 個はごみ箱に移動できないため、削除しませんでした。");
                break;
            default:
                Message.Error(r == 0
                    ? $"{what}を削除できませんでした：{err}"
                    : $"{r:N0} 個をごみ箱に移動しましたが、残りの {rest:N0} 個は削除できませんでした：{err}");
                break;
        }
    }

    /// <summary>
    /// After a delete, a paste or a move done elsewhere: drops the cached listings and folder covers
    /// (the parent, the overlay's folders, covers that showed the item), marks every tab stale and
    /// reloads the shown one in place.
    /// </summary>
    /// <param name="cardCascadeOnly">
    /// From an activation: close only a cascade that lives in the tab's content (as a tab load
    /// does), so a sidebar list the user is opening over the window that just came back stays.
    /// </param>
    private async Task ReloadAfterFileChangeAsync(bool cardCascadeOnly = false)
    {
        if (cardCascadeOnly)
        {
            View?.CloseCardCascade();
        }
        else
        {
            View?.CloseCascade();
        }

        _services.ClearCaches();
        foreach (var tab in AllTabs)
        {
            tab.IsStale = true;
        }

        var shown = ActivePane;
        if (shown.IsPlaceholder)
        {
            return; // no tab is open (the item was a row beside the sidebar): nothing to list again
        }

        double offset = View?.GetScrollOffset() ?? 0;
        var outcome = await shown.LoadAsync();
        if (outcome == LoadOutcome.Ok && ReferenceEquals(shown, ActivePane))
        {
            View?.ScrollTo(offset);
        }
    }

    /// <summary>
    /// How long after a command of Explorer's menu an activation is taken for the menu handing the
    /// foreground back (not for the user coming back from what the command started).
    /// </summary>
    private static readonly TimeSpan ShellCommandHandBack = TimeSpan.FromSeconds(1);

    /// <summary>
    /// When a command of Explorer's menu was handed over (Environment.TickCount64), until the next
    /// activation after <see cref="ShellCommandHandBack"/> reloads the lists once more; null: none.
    /// </summary>
    private long? _shellCommandAt;

    /// <summary>
    /// Items get Explorer's menu with the app's entries on top of it (setting 右クリックのメニュー).
    /// Read at every right click, so OK in the settings applies without a restart.
    /// </summary>
    public bool UsesWindowsItemMenu => ItemMenuStyles.FromSetting(Settings.ItemMenu) == ItemMenuStyle.Windows;

    /// <summary>
    /// Explorer's menu (「その他のメニュー（Windows）」) closed. A menu that could not be shown says so.
    /// Only a command reloads: it may have changed the folder (7-Zip's 展開, a new item…), and some
    /// work in another process after the menu returns, so the lists are read again now and once
    /// more when the user comes back to the window. A dismissed menu changes nothing, so the
    /// cascade it came from stays.
    /// </summary>
    public async Task OnShellMenuClosedAsync(ShellMenuResult result)
    {
        switch (result.Outcome)
        {
            case ShellMenuOutcome.Invoked:
                // A failure the handler reported has usually been shown by the handler itself (and is logged).
                _shellCommandAt = Environment.TickCount64;
                await ReloadAfterFileChangeAsync();
                break;
            case ShellMenuOutcome.Unavailable:
            case ShellMenuOutcome.TimedOut:
            case ShellMenuOutcome.Failed:
                Message.Warning(result.Error is { Length: > 0 } error
                    ? $"このメニューは表示できませんでした：{error}"
                    : "このメニューは表示できませんでした。");
                break;
        }
    }

    /// <summary>
    /// Alt+Enter: Explorer's プロパティ of the selection (one sheet for several items), through the
    /// same shell command as the entry of 「Windows のメニュー」. Nothing selected: the folder shown
    /// (its background menu's プロパティ, so a drive root gets the drive's sheet); a top list shows
    /// none. One selected drive or share root goes the same way. Returns false when there is
    /// nothing to show (the key is not used).
    /// </summary>
    public bool ShowProperties()
    {
        if (!HasTab)
        {
            return false;
        }

        var paths = SelectedItems().Select(i => i.Path).ToList();
        bool background;
        if (paths.Count == 0)
        {
            if (ActivePane.FolderTargetPath is not { } folder)
            {
                return false;
            }

            paths = [folder];
            background = true;
        }
        else if (paths.TrueForAll(path => TransferRules.IsTransferable(path)))
        {
            background = false;
        }
        else if (paths.Count == 1)
        {
            background = true; // a root: the item menu refuses it (its menu would offer フォーマット)
        }
        else
        {
            Message.Info("ドライブのプロパティは 1 つずつ表示してください。");
            return true;
        }

        _ = ShowPropertiesAsync(paths, background);
        return true;
    }

    private async Task ShowPropertiesAsync(IReadOnlyList<string> paths, bool background)
    {
        var result = await ShellContextMenu.ShowPropertiesAsync(paths, background, View?.WindowHandle ?? 0);
        switch (result.Outcome)
        {
            case ShellMenuOutcome.Invoked:
                // The sheet can rename the item or change its attributes: the lists are read again
                // when the window is activated after it (as after a command of Explorer's menu).
                _shellCommandAt = Environment.TickCount64;
                break;
            case ShellMenuOutcome.Unavailable:
            case ShellMenuOutcome.TimedOut:
            case ShellMenuOutcome.Failed:
                Message.Warning(result.Error is { Length: > 0 } error
                    ? $"プロパティを表示できませんでした：{error}"
                    : "プロパティを表示できませんでした。");
                break;
        }
    }

    // ---- selection, clipboard and paste ---------------------------------------------------------

    private static readonly TimeSpan ClipboardReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TargetCheckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SourceProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CutCheckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A paste, a drop on a folder, a delete to the Recycle Bin, a rename or a new folder is running (one at a time: the
    /// window asks before it closes meanwhile).
    /// </summary>
    private bool _fileOpBusy;

    /// <summary>The paths of this app's pending cut (null: none) and the clipboard sequence number it was written with.</summary>
    private HashSet<string>? _cutPaths;
    private uint _cutSequence;

    /// <summary>
    /// The paths of a cut that ended because the clipboard changed (Explorer may have moved them
    /// first): the next activation checks them once and reloads the lists when any is gone.
    /// </summary>
    private HashSet<string>? _endedCutPaths;

    /// <summary>The check of <see cref="OnActivatedAsync"/> is running.</summary>
    private bool _cutCheckBusy;

    /// <summary>A copy / move started by a paste or a drop, or a move to the Recycle Bin, is running (the window must not close silently).</summary>
    public bool IsFileOperationRunning => _fileOpBusy;

    /// <summary>
    /// A click on an item of the shown list with Ctrl and / or Shift: Ctrl flips the item, Shift
    /// selects the range from the anchor (added to the selection with Ctrl+Shift). A missing or
    /// unanswering item is never selected. Returns false when the click is not a selection gesture.
    /// </summary>
    public bool ClickSelect(CardViewModel card, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        bool shift = (modifiers & ModifierKeys.Shift) != 0;
        if (!ctrl && !shift)
        {
            return false;
        }

        var tab = ActivePane;
        if (!HasTab || card.IsDisabled || !tab.Cards.Contains(card))
        {
            return true; // a selection gesture all the same: it never opens the item
        }

        if (shift)
        {
            tab.Selection.SelectRange(tab.SelectableKeys(), card.Path, additive: ctrl);
        }
        else
        {
            tab.Selection.Toggle(card.Path);
        }

        return true;
    }

    /// <summary>
    /// Items of a tab open on a double click and a plain click only selects (setting ダブルクリックで開く).
    /// Read at every click, so OK in the settings applies without a restart.
    /// </summary>
    public bool OpensOnDoubleClick => Settings.OpenOnDoubleClick;

    /// <summary>
    /// A plain click when items open on a double click: the item alone becomes the selection and
    /// the anchor of a following Shift+click. A missing or unanswering item is never selected.
    /// </summary>
    public void SelectOnly(CardViewModel card)
    {
        var tab = ActivePane;
        if (HasTab && !card.IsDisabled && tab.Cards.Contains(card))
        {
            tab.Selection.Replace([card.Path]);
        }
    }

    /// <summary>Ctrl+A: every enabled item of the shown list. Returns true when the selection changed.</summary>
    public bool SelectAll() => HasTab && ActivePane.Selection.SelectAll(ActivePane.SelectableKeys());

    /// <summary><paramref name="card"/> is an item of the shown list (not a row of a cascade or the sidebar).</summary>
    public bool IsShownCard(CardViewModel card) => HasTab && ActivePane.Cards.Contains(card);

    /// <summary>「ファイル種別選択」: the kinds in the shown list (as filtered and searched), usable items only.</summary>
    public IReadOnlyList<TypeGroup> TypeGroupsShown() =>
        HasTab
            ? TypeGroups.Of(ActivePane.Cards.Where(c => !c.IsDisabled).Select(c => (Path.GetFileName(c.Path), c.Kind == CardKind.Folder)))
            : [];

    /// <summary>
    /// 「ファイル種別選択」: the shown items of kind <paramref name="key"/> (<see cref="TypeGroups.KeyOf"/>)
    /// become the selection (it is replaced, as Ctrl+A replaces it).
    /// </summary>
    public void SelectByType(string key)
    {
        if (!HasTab)
        {
            return;
        }

        var tab = ActivePane;
        var keys = tab.Cards
            .Where(c => !c.IsDisabled && TypeGroups.KeyOf(Path.GetFileName(c.Path), c.Kind == CardKind.Folder) == key)
            .Select(c => c.Path)
            .ToList();
        tab.Selection.Replace(keys);
    }

    /// <summary>
    /// Esc, a plain click, a press on the blank area. The keyboard cursor stays where it was (as
    /// Explorer's focus), so the next arrow goes on from there; only another folder forgets it
    /// (<see cref="PaneViewModel"/> clears the model). Returns true when something was selected.
    /// </summary>
    public bool ClearSelection()
    {
        var selection = ActivePane.Selection;
        string? cursor = selection.Cursor;
        bool changed = selection.Clear();
        if (cursor is not null)
        {
            selection.MoveCursor(ActivePane.Cards.Select(c => c.Path).ToList(), cursor, select: false, extend: false);
        }

        return changed;
    }

    // ---- the keyboard cursor (arrows, Home / End, Page keys, Space, typed names) ----------------

    /// <summary>
    /// An arrow, Home / End or a Page key on the shown list: the cursor moves by
    /// <see cref="GridNavigation.Move"/> over the shown cards (<paramref name="columns"/> per row,
    /// <paramref name="pageRows"/> rows per screen) and, by <see cref="SelectionModel.MoveCursor"/>,
    /// selects the target alone (<paramref name="select"/>), the range from the anchor
    /// (<paramref name="extend"/>, Shift; added to the selection with Ctrl) or nothing (Ctrl).
    /// Returns the card the cursor is on now, for the view to scroll to (null: an empty list).
    /// </summary>
    public CardViewModel? MoveCursor(NavKey key, int columns, int pageRows, bool select, bool extend)
    {
        if (!HasTab)
        {
            return null;
        }

        var cards = ActivePane.Cards;
        int index = GridNavigation.Move(CursorIndex(), key, cards.Count, columns, pageRows);
        if (index < 0)
        {
            return null;
        }

        MoveCursorTo(cards[index], select, extend);
        return cards[index];
    }

    /// <summary>
    /// A typed name (letters within a second of each other are joined): the cursor goes to the next
    /// shown card whose name starts with <paramref name="typed"/> (<see cref="GridNavigation.FindByPrefix"/>)
    /// and selects it alone, as a plain arrow. Returns that card, or null when no name matches.
    /// </summary>
    public CardViewModel? MoveCursorToName(string typed)
    {
        if (!HasTab)
        {
            return null;
        }

        var cards = ActivePane.Cards;
        int index = GridNavigation.FindByPrefix(cards.Select(c => c.Name).ToList(), CursorIndex(), typed);
        if (index < 0)
        {
            return null;
        }

        MoveCursorTo(cards[index], select: true, extend: false);
        return cards[index];
    }

    /// <summary>
    /// Space: the cursor's card alone is selected; Ctrl+Space (<paramref name="toggle"/>) flips it in
    /// the selection. Both make it the anchor of a following Shift + arrow. Returns false when there
    /// is no cursor (the key is not used); a missing or unanswering card is never selected.
    /// </summary>
    public bool SelectAtCursor(bool toggle)
    {
        if (CursorCard() is not { } card)
        {
            return false;
        }

        var tab = ActivePane;
        tab.ShowsCursor = true;
        if (!card.IsDisabled)
        {
            if (toggle)
            {
                tab.Selection.Toggle(card.Path);
            }
            else
            {
                tab.Selection.Replace([card.Path]);
            }
        }

        return true;
    }

    /// <summary>The shown card the keyboard cursor is on, or null (no cursor yet, another folder was shown, the item went away).</summary>
    public CardViewModel? CursorCard() =>
        HasTab && ActivePane.Selection.Cursor is { } path ? ShownCard(path) : null;

    /// <summary>A mouse press: the cursor's frame is no longer drawn (the cursor itself stays, see <see cref="PaneViewModel.ShowsCursor"/>).</summary>
    public void HideKeyboardCursor()
    {
        if (HasTab)
        {
            ActivePane.ShowsCursor = false;
        }
    }

    /// <summary>The cursor's index in the shown cards (its anchor without one, e.g. after Ctrl+A), or -1.</summary>
    private int CursorIndex()
    {
        var selection = ActivePane.Selection;
        if ((selection.Cursor ?? selection.Anchor) is not { } path)
        {
            return -1;
        }

        var cards = ActivePane.Cards;
        for (int i = 0; i < cards.Count; i++)
        {
            if (PathTools.PathEquals(cards[i].Path, path))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Moves the cursor onto <paramref name="card"/> with the selection rule of
    /// <see cref="SelectionModel.MoveCursor"/>, over the selectable cards (a range skips missing and
    /// unanswering ones). Such a card is never selected: the cursor rests on it alone, and a plain
    /// arrow leaves nothing selected (Enter, Delete must not act on the item it left).
    /// </summary>
    private void MoveCursorTo(CardViewModel card, bool select, bool extend)
    {
        var tab = ActivePane;
        tab.ShowsCursor = true;
        if (!card.IsDisabled)
        {
            tab.Selection.MoveCursor(tab.SelectableKeys(), card.Path, select, extend);
            return;
        }

        if (select && !extend)
        {
            tab.Selection.Clear();
        }

        tab.Selection.MoveCursor(tab.Cards.Select(c => c.Path).ToList(), card.Path, select: false, extend: false);
    }

    /// <summary>
    /// A marquee drag over the shown list: the tab and the cards it was started on, the selection
    /// at the press, and whether the box flips it (Ctrl) or adds to it (Shift; no key: the press
    /// cleared the selection, so the box alone is selected).
    /// </summary>
    public sealed record SelectionMarquee(PaneViewModel Tab, IReadOnlyList<CardViewModel> Cards, SelectionSnapshot Base, bool Toggle);

    /// <summary>Starts a marquee on the shown list with the modifier keys held at the press (null without a tab).</summary>
    public SelectionMarquee? BeginMarquee(ModifierKeys modifiers) =>
        HasTab
            ? new SelectionMarquee(ActivePane, ActivePane.Cards, ActivePane.Selection.Snapshot(), (modifiers & ModifierKeys.Control) != 0)
            : null;

    /// <summary>The cards in the marquee's box changed: the selection is the press's selection combined with them (missing and unanswering items are never selected).</summary>
    public void UpdateMarquee(SelectionMarquee marquee, IReadOnlyList<CardViewModel> box)
    {
        if (IsCurrent(marquee))
        {
            marquee.Tab.Selection.SelectBox(marquee.Base, box.Where(c => !c.IsDisabled).Select(c => c.Path), marquee.Toggle);
        }
    }

    /// <summary>Esc during a marquee: the selection of the press comes back.</summary>
    public void CancelMarquee(SelectionMarquee marquee)
    {
        if (IsCurrent(marquee))
        {
            marquee.Tab.Selection.Restore(marquee.Base);
        }
    }

    /// <summary>The marquee's tab is still shown with the same cards (a reload or another tab ends what it may change).</summary>
    private bool IsCurrent(SelectionMarquee marquee) =>
        ReferenceEquals(ActivePane, marquee.Tab) && ReferenceEquals(marquee.Tab.Cards, marquee.Cards);

    /// <summary>
    /// What an action on <paramref name="clicked"/> applies to: the whole selection in display order
    /// when the item is part of it, else the item alone (cascade rows and headers are never part of it).
    /// </summary>
    public IReadOnlyList<ItemRef> SelectionTargets(ItemRef clicked)
    {
        var tab = ActivePane;
        if (clicked.Card is not { } card || !tab.Selection.Contains(card.Path) || !tab.Cards.Contains(card))
        {
            return [clicked];
        }

        return tab.SelectedCards().Select(c => c.ToItemRef()).ToList();
    }

    /// <summary>The selection of the shown list as items (empty when nothing is selected).</summary>
    public IReadOnlyList<ItemRef> SelectedItems() => ActivePane.SelectedCards().Select(c => c.ToItemRef()).ToList();

    /// <summary>
    /// The item can be cut: the rule of <see cref="CanDelete"/> (what lies inside a folder, not a
    /// top-list card, a header or a sidebar row) and a path that may be moved (not a drive root).
    /// </summary>
    public static bool CanCut(ItemRef item) => CanDelete(item) && TransferRules.IsTransferable(item.Path);

    /// <summary>The item can be copied: it is usable and its path may be copied (not a drive root).</summary>
    public static bool CanCopy(ItemRef item) =>
        !item.IsDisabled && item.Card?.IsDisabled != true && TransferRules.IsTransferable(item.Path);

    /// <summary>
    /// Puts <paramref name="items"/> on the clipboard as files, like Explorer's 切り取り / コピー.
    /// A cut dims the items on every tab until it is pasted, replaced or found moved.
    /// </summary>
    public void CopyToClipboard(IReadOnlyList<ItemRef> items, bool cut)
    {
        if (items.Count == 0)
        {
            return;
        }

        if (cut ? !items.All(CanCut) : !items.All(CanCopy))
        {
            Message.Info(cut ? "この一覧の項目は切り取れません（コピーはできます）。" : "この項目はコピーできません。");
            return;
        }

        var paths = items.Select(i => i.Path).ToList();
        nint owner = View?.WindowHandle ?? 0;
        if (!ShellClipboard.TryWrite(paths, cut ? TransferKind.Move : TransferKind.Copy, owner, out uint sequence))
        {
            Message.Error("クリップボードを使用できませんでした。少し待ってからもう一度お試しください。");
            return;
        }

        if (cut)
        {
            SetCut(new HashSet<string>(paths, PathTools.Comparer), sequence);
        }
        else
        {
            ClearCut();
        }

        string what = items.Count == 1 ? $"「{items[0].Name}」" : $"{items.Count:N0} 個の項目";
        Message.Success(cut
            ? $"{what}を切り取りました。貼り付け先のフォルダで Ctrl+V を押してください。"
            : $"{what}をコピーしました。");
    }

    /// <summary>
    /// The folder a paste on <paramref name="item"/> goes into: the folder item itself (a card, a
    /// row, a header, a 登録フォルダ or drive row that is usable), or with no item the folder the
    /// shown tab lists (null on 最近の履歴 / よく使う項目's own lists, without a tab, and while the
    /// folder is not really on screen: see <see cref="PaneViewModel.TransferTargetPath"/>).
    /// </summary>
    public string? PasteFolderOf(ItemRef? item)
    {
        if (item is null)
        {
            return ActivePane.TransferTargetPath;
        }

        bool usable = item.IsFolder && !item.IsDisabled && item.Card?.IsDisabled != true;
        return usable && PathTools.IsFullyQualified(item.Path) ? item.Path : null;
    }

    /// <summary>
    /// The folder the shown tab lists, as an item of its own, for the menu of the blank area
    /// (エクスプローラーで表示, パスをコピー, 登録フォルダに追加 and Explorer's background menu). Null
    /// where <see cref="PaneViewModel.TransferTargetPath"/> is (the top lists, no tab, loading, a
    /// failed load). No ItemContext of its own: a root is a Drive (using it is not recorded in the
    /// history, as for a drive row), any other folder a FlyoutHeader (the folder itself, never cut
    /// or deleted). Those three commands branch on no other context.
    /// </summary>
    public ItemRef? ShownFolderItem() =>
        ActivePane.TransferTargetPath is { } path
            ? new ItemRef(path, IsFolder: true, ActivePane.Location, PathTools.IsRoot(path) ? ItemContext.Drive : ItemContext.FlyoutHeader)
            : null;

    /// <summary>A paste into <paramref name="folder"/> can start now (the clipboard offers files).</summary>
    public bool CanPaste(string? folder) => folder is not null && !_fileOpBusy && ShellClipboard.HasFiles();

    /// <summary>
    /// Ctrl+V and the menus' 貼り付け: copies or moves the clipboard's files into
    /// <paramref name="folder"/> as Explorer does (a cut moves, also from a network folder; what
    /// cannot be deleted at its place is copied). The shell shows its own dialogs (name conflicts,
    /// progress); this app shows none. One paste at a time.
    /// </summary>
    public async Task PasteAsync(string folder)
    {
        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return;
        }

        _fileOpBusy = true;
        try
        {
            View?.CloseCascade();
            Message.ClearPersistent();
            nint owner = View?.WindowHandle ?? 0;
            var read = await ShellClipboard.ReadWithSequenceAsync(ClipboardReadTimeout);
            if (read is null || read.Files.Paths.Count == 0)
            {
                Message.Info("貼り付けるファイルやフォルダがクリップボードにありません。");
                return;
            }

            var kind = read.Files.Kind;
            if (await TransferIntoAsync(read.Files.Paths, folder, TransferOptions.Paste(kind), TransferGesture.Paste, owner) is not { } done)
            {
                return;
            }

            var result = done.Result;

            // A cut is over only when every move really happened; otherwise it stays on the
            // clipboard (and dimmed) so that what was left behind can be pasted again.
            if (kind == TransferKind.Move && result.MovesRequested > 0 && result.MovesDone == result.MovesRequested)
            {
                ShellClipboard.ClearIfUnchanged(read.Sequence, owner);
                if (_cutPaths is not null && _cutSequence == read.Sequence)
                {
                    ClearCut(); // a cut made while this paste ran stays
                }
            }

            await ReloadAndSelectArrivedAsync(folder, result);
        }
        finally
        {
            _fileOpBusy = false;
        }
    }

    private enum TransferGesture
    {
        Paste,
        Drop,
    }

    /// <summary>
    /// Probes, plans, runs and reports one transfer of <paramref name="paths"/> into
    /// <paramref name="folder"/> (a paste or a drop: <see cref="FileTransfer"/> is the only way the
    /// app copies or moves files). The caller holds <see cref="_fileOpBusy"/>. Null when nothing
    /// ran (the reason is on the message line).
    /// </summary>
    private async Task<(TransferPlan Plan, TransferResult Result)?> TransferIntoAsync(
        IReadOnlyList<string> paths, string folder, TransferOptions options, TransferGesture gesture, nint owner)
    {
        string folderName = PathTools.DisplayName(folder);
        if (!await SourceProbe.FolderExistsAsync(folder, TargetCheckTimeout))
        {
            Message.Error(gesture == TransferGesture.Paste
                ? $"貼り付け先のフォルダが見つかりません：{folder}"
                : $"移動・コピー先のフォルダが見つかりません：{folder}");
            return null;
        }

        var sources = await SourceProbe.DescribeAsync(paths, SourceProbeTimeout);
        var plan = TransferRules.Decide(sources, folder, options);
        if (plan.Items.Count == 0)
        {
            Message.Info(RefusalText(plan.Refusal, options.Requested, gesture));
            return null;
        }

        var result = await FileTransfer.RunAsync(plan.Items, folder, owner);

        // Ctrl+Z: only results that are new items (a merge or a replace would move or recycle what
        // was there before with them). Where a merge or a replace landed, the older steps naming that
        // place (or a folder holding it, or something inside it) are taken out first: undoing a copy
        // or a new folder there would recycle what was merged in.
        // A merge or a replace, done or stopped part-way: older steps at those items would move or
        // recycle what came into them.
        InvalidateFileOps(result.TakenTargets.Concat(result.Items.Where(i => !i.Fresh).Select(i => i.NewPath)));
        var fresh = result.Items.Where(i => i.Fresh).ToList();
        RecordFileOp(FileOpStep.Transferred(
            fresh.Where(i => i.Kind == TransferKind.Move).Select(i => new PathMove(i.Source, i.NewPath)),
            fresh.Where(i => i.Kind == TransferKind.Copy).Select(i => i.NewPath)),
            changed: true); // the transfer ran: even with nothing done in full it may have changed files (part of a merge)
        Message.Show(PasteMessageKind(result), PasteResultText(result, plan, folderName, gesture));
        return (plan, result);
    }

    /// <summary>
    /// Reads the lists again after a transfer and, when <paramref name="folder"/> is the folder
    /// shown, selects what arrived there (as Explorer does; only what the reloaded list shows).
    /// </summary>
    private async Task ReloadAndSelectArrivedAsync(string folder, TransferResult result)
    {
        await ReloadAfterFileChangeAsync();
        if (result.NewPaths.Count > 0 && PathTools.PathEquals(ActivePane.TransferTargetPath, folder))
        {
            var tab = ActivePane;
            tab.Selection.Replace(result.NewPaths);
            tab.Selection.Prune(tab.SelectableKeys());
        }
    }

    // ---- drag and drop into folders ---------------------------------------------------------------

    /// <summary>
    /// A drag the app started has ended (DoDragDrop returned): a drop on a folder of the app starts
    /// its transfer now, outside the drag's modal loop; a drag that offered Move to another app is
    /// watched for the items going away (Explorer moves them itself). Deletes nothing.
    /// </summary>
    public void FinishDrag(OutgoingDrag drag)
    {
        if (!drag.Ran)
        {
            return;
        }

        if (drag.Dropped is { } dropped)
        {
            _ = dropped.Link
                ? LinkDropAsync(drag.Paths, dropped.Folder, fromOtherApp: false)
                : DropAsync(drag.Paths, dropped.Folder, dropped.Kind, fromOtherApp: false, dropped.CopyInPlace);
            return;
        }

        if (drag.MoveAllowed)
        {
            WatchDraggedOut(drag.Paths);
        }
        else
        {
            _ = CheckDraggedOutAsync(); // an earlier drag-out whose checks this drag held back
        }
    }

    /// <summary>
    /// Items dropped on a folder of the app (a folder card or row, a 登録フォルダ or drive row) are
    /// moved or copied into it through <see cref="FileTransfer"/>, as a paste is: the shell shows
    /// its dialogs (name conflicts, progress), nothing already in the folder is copied again, and
    /// what cannot be moved from its place is copied. One file operation at a time. A drop from
    /// another app may carry 8.3 short names: they are expanded first (the same-folder and
    /// into-itself checks compare spellings). A drop on the shown folder's list (its blank part or
    /// a file) goes here too, with that folder; <paramref name="copyInPlace"/> (Ctrl) duplicates
    /// what is already in it ("x - コピー", the paste's rename on collision), so the result, the
    /// undo step and the selection of what arrived are those of a paste.
    /// </summary>
    public async Task DropAsync(IReadOnlyList<string> paths, string folder, TransferKind kind, bool fromOtherApp, bool copyInPlace = false)
    {
        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return;
        }

        _fileOpBusy = true;
        try
        {
            View?.CloseCascade();
            Message.ClearPersistent();
            nint owner = View?.WindowHandle ?? 0;
            IReadOnlyList<string> sources = paths;
            if (fromOtherApp)
            {
                string[] copy = [.. paths];
                try
                {
                    sources = await Task.Run(() => copy.Select(ShellClipboard.ToLongPath).ToArray()).WaitAsync(SourceProbeTimeout);
                }
                catch (TimeoutException)
                {
                    Message.Error("ドロップした項目を確認できませんでした。");
                    return;
                }
            }

            AppLog.Info($"drop: {(fromOtherApp ? "other app" : "app")}, {kind}{(copyInPlace ? " in place" : "")}, {sources.Count} items");
            if (await TransferIntoAsync(sources, folder, TransferOptions.Drop(kind, copyInPlace), TransferGesture.Drop, owner) is not { } done)
            {
                return;
            }

            // A cut of this app among the moved items is cleared by the next activation's check.
            await ReloadAndSelectArrivedAsync(folder, done.Result);
        }
        finally
        {
            _fileOpBusy = false;
        }
    }

    /// <summary>
    /// Alt + drop (<see cref="FileDrag.MakesLink"/>): a shortcut to each item in <paramref name="folder"/>,
    /// as Explorer's Alt + drag (<see cref="ShellLinks"/>: "名前 - ショートカット.lnk"). One file
    /// operation at a time, as a drop. Undone with Ctrl + Z (the shortcuts go to the Recycle Bin).
    /// The new shortcuts are selected when the folder is the one shown.
    /// </summary>
    public async Task LinkDropAsync(IReadOnlyList<string> paths, string folder, bool fromOtherApp)
    {
        if (_fileOpBusy)
        {
            Message.Info("前の操作が終わってから実行してください。");
            return;
        }

        _fileOpBusy = true;
        try
        {
            View?.CloseCascade();
            Message.ClearPersistent();
            IReadOnlyList<string> sources = paths;
            if (fromOtherApp)
            {
                string[] copy = [.. paths];
                try
                {
                    sources = await Task.Run(() => copy.Select(ShellClipboard.ToLongPath).ToArray()).WaitAsync(SourceProbeTimeout);
                }
                catch (TimeoutException)
                {
                    Message.Error("ドロップした項目を確認できませんでした。");
                    return;
                }
            }

            // Not onto itself (FileDrag.DropEffectFor refused it already; a drop from another app is checked again).
            sources = sources.Where(p => !PathTools.PathEquals(p.TrimEnd('\\'), folder.TrimEnd('\\'))).ToList();
            if (sources.Count == 0)
            {
                return;
            }

            if (!await SourceProbe.FolderExistsAsync(folder, TargetCheckTimeout))
            {
                Message.Error($"ショートカットの作成先のフォルダが見つかりません：{folder}");
                return;
            }

            AppLog.Info($"drop: {(fromOtherApp ? "other app" : "app")}, link, {sources.Count} items");
            var result = await ShellLinks.CreateAsync(sources, folder);
            if (result.Made.Count > 0)
            {
                RecordFileOp(FileOpStep.ShortcutsMade(result.Made));
            }

            string folderName = PathTools.DisplayName(folder);
            if (result.Error is null)
            {
                Message.Success($"「{folderName}」にショートカットを作成しました（{result.Made.Count:N0} 個）。");
            }
            else if (result.Made.Count > 0)
            {
                Message.Warning($"ショートカットを {result.Made.Count:N0} / {result.Requested:N0} 個作成しました：{result.Error}");
            }
            else
            {
                Message.Error($"ショートカットを作成できませんでした：{result.Error}");
            }

            await ReloadAfterFileChangeAsync();
            if (result.Made.Count > 0 && PathTools.PathEquals(ActivePane.TransferTargetPath, folder))
            {
                var tab = ActivePane;
                tab.Selection.Replace(result.Made);
                tab.Selection.Prune(tab.SelectableKeys());
            }
        }
        finally
        {
            _fileOpBusy = false;
        }
    }

    /// <summary>Items handed to another app with Move offered, checked on the next activation once the timed checks gave up.</summary>
    private string[]? _draggedOutPaths;

    /// <summary>Generation of the drag-out watch: a newer drag-out ends the older watch.</summary>
    private int _dragOutWatch;

    /// <summary>Waits between the checks of a drag-out (0.5, 2 and 5 seconds after the drag in all).</summary>
    private static readonly int[] DragOutCheckDelaysMs = [500, 1500, 3000];

    /// <summary>
    /// Items were dragged to another app with Move offered: Explorer (on the same drive, or with
    /// Shift) moves them itself and the source is not told reliably (an optimized move answers None
    /// or Copy). The lists are read again once some of them are gone: checked after 0.5, 2 and 5
    /// seconds, then once more on the next activation.
    /// </summary>
    public void WatchDraggedOut(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        // An earlier drag-out still waiting for its last check is watched along with this one.
        string[] watched = _draggedOutPaths is { } earlier ? [.. paths, .. earlier] : [.. paths];
        _draggedOutPaths = null;
        _ = WatchDraggedOutAsync(watched, ++_dragOutWatch);
    }

    private async Task WatchDraggedOutAsync(string[] paths, int generation)
    {
        foreach (int delay in DragOutCheckDelaysMs)
        {
            await Task.Delay(delay);
            if (generation != _dragOutWatch)
            {
                return;
            }

            if (!CanReloadForDragOut() || await RemainingAsync(paths) is not { } remaining)
            {
                continue;
            }

            if (generation != _dragOutWatch)
            {
                return;
            }

            // Checked again after the await: a paste, a name's edit box or another drag may have
            // started meanwhile, and none may have the list reloaded under it.
            if (remaining.Length < paths.Length && CanReloadForDragOut())
            {
                _draggedOutPaths = remaining.Length > 0 ? remaining : null;
                await ReloadAfterFileChangeAsync(cardCascadeOnly: true);
                return;
            }
        }

        if (generation == _dragOutWatch)
        {
            _draggedOutPaths = paths;
        }
    }

    /// <summary>
    /// A drag-out check may reload the lists now: no file operation, no name being edited, and no
    /// drag running (DoDragDrop pumps the dispatcher; a reload would replace the cards a reorder
    /// drag holds, and its drop would be lost).
    /// </summary>
    private bool CanReloadForDragOut() => !_fileOpBusy && View?.IsNameBusy != true && !FileDragOut.IsDragging;

    /// <summary>
    /// The items of a drag-out that the timed checks did not see go (or skipped) are checked once
    /// more: on the next activation, and right after a later drag of the app ended.
    /// </summary>
    private async Task CheckDraggedOutAsync()
    {
        if (_draggedOutPaths is not { } draggedOut || !CanReloadForDragOut())
        {
            return;
        }

        _draggedOutPaths = null;
        if (await RemainingAsync(draggedOut) is { } left && left.Length < draggedOut.Length)
        {
            if (!CanReloadForDragOut())
            {
                _draggedOutPaths = left.Length > 0 ? draggedOut : null; // busy again: next time
                return;
            }

            await ReloadAfterFileChangeAsync(cardCascadeOnly: true);
        }
    }

    /// <summary>The paths that still exist, checked off the UI thread (a network path may hang); null on a timeout.</summary>
    private static async Task<string[]?> RemainingAsync(string[] paths)
    {
        try
        {
            return await Task.Run(() => paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray()).WaitAsync(CutCheckTimeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The clipboard changed (any program, this one included): a pending cut of this app is over
    /// once the clipboard holds something else.
    /// </summary>
    public void OnClipboardChanged()
    {
        if (_cutPaths is { } paths && ShellClipboard.Sequence != _cutSequence)
        {
            ClearCut();
            _endedCutPaths = paths; // Explorer may have moved them: checked on the next activation
        }
    }

    /// <summary>
    /// The window came back to the front. A cut of this app pasted in Explorer is moved by Explorer,
    /// which may leave the clipboard as it was: when a cut item is gone, the cut is over and the
    /// lists are read again. The check runs off the UI thread (a network path may hang).
    /// After a command of Explorer's menu the lists are read again once (see <see cref="OnShellMenuClosedAsync"/>).
    /// </summary>
    public async Task OnActivatedAsync()
    {
        if (_shellCommandAt is { } at && !_fileOpBusy
            && TimeSpan.FromMilliseconds(Environment.TickCount64 - at) >= ShellCommandHandBack)
        {
            _shellCommandAt = null;

            // Not inside Activated itself: a click that activated the window (a row of a cascade
            // opened over the inactive window) is handled first, then the reload closes the cascade.
            await Task.Yield();
            await ReloadAfterFileChangeAsync(cardCascadeOnly: true);
        }

        // Dragged to another app: Explorer may have moved them after the timed checks gave up.
        await CheckDraggedOutAsync();

        if ((_cutPaths ?? _endedCutPaths) is not { } paths || _cutCheckBusy || _fileOpBusy)
        {
            return;
        }

        _cutCheckBusy = true;
        try
        {
            string[] copy = paths.ToArray();
            Task<string[]> check = Task.Run(() => copy.Where(p => File.Exists(p) || Directory.Exists(p)).ToArray());
            string[] remaining;
            try
            {
                remaining = await check.WaitAsync(CutCheckTimeout);
            }
            catch (TimeoutException)
            {
                return; // unknown: the cut stays (an ended cut is checked again next time)
            }

            bool gone = remaining.Length < copy.Length;
            if (ReferenceEquals(paths, _endedCutPaths))
            {
                _endedCutPaths = null;
            }
            else if (!ReferenceEquals(paths, _cutPaths))
            {
                return; // the cut changed meanwhile
            }
            else if (remaining.Length == 0)
            {
                ClearCut();
            }
            else if (gone)
            {
                // Partly pasted (a skipped conflict): what is left stays cut and dimmed.
                SetCut(new HashSet<string>(remaining, PathTools.Comparer), _cutSequence);
            }

            if (gone)
            {
                await ReloadAfterFileChangeAsync(cardCascadeOnly: true);
            }
        }
        finally
        {
            _cutCheckBusy = false;
        }
    }

    private void SetCut(HashSet<string> paths, uint sequence)
    {
        _cutPaths = paths;
        _cutSequence = sequence;
        _endedCutPaths = null;
        foreach (var tab in AllTabs)
        {
            tab.CutPaths = paths;
        }
    }

    private void ClearCut()
    {
        if (_cutPaths is null)
        {
            return;
        }

        _cutPaths = null;
        _cutSequence = 0;
        foreach (var tab in AllTabs)
        {
            tab.CutPaths = null;
        }
    }

    private static string RefusalText(TransferRefusal refusal, TransferKind kind, TransferGesture gesture) => gesture == TransferGesture.Drop
        ? refusal switch
        {
            TransferRefusal.SameFolder => "すでにそのフォルダにあります。",
            TransferRefusal.IntoItself => "フォルダを、そのフォルダ自身の中には移動・コピーできません。",
            TransferRefusal.Root => "ドライブそのものは移動・コピーできません。",
            TransferRefusal.NoTarget => "そこには移動・コピーできません。",
            _ => "ドロップした項目は移動・コピーできません。",
        }
        : refusal switch
    {
        TransferRefusal.SameFolder => kind == TransferKind.Move
            ? "切り取った項目は、すでにこのフォルダにあります。"
            : "同じフォルダには貼り付けできません。",
        TransferRefusal.IntoItself => "フォルダを、そのフォルダ自身の中には貼り付けできません。",
        TransferRefusal.Root => "ドライブそのものは貼り付けできません。",
        TransferRefusal.NoTarget => "この一覧には貼り付けできません。",
        _ => "クリップボードの項目は貼り付けできません。",
    };

    private static MessageKind PasteMessageKind(TransferResult result) => result.Outcome switch
    {
        TransferOutcome.Completed => MessageKind.Success,
        TransferOutcome.Canceled => MessageKind.Info,
        TransferOutcome.Partial => MessageKind.Warning,
        _ => MessageKind.Error,
    };

    /// <summary>The message after a paste or a drop: it always names the destination, and says how many items were copied instead of moved.</summary>
    private static string PasteResultText(TransferResult result, TransferPlan plan, string folderName, TransferGesture gesture)
    {
        int copies = result.Requested - result.MovesRequested;
        string verb = result.MovesRequested == 0 ? "コピー" : copies == 0 ? "移動" : "移動・コピー";
        string count = result.Requested == 1 ? $"「{PathTools.DisplayName(plan.Items[0].Path)}」" : $"{result.Requested:N0} 個の項目";
        string text = result.Outcome switch
        {
            TransferOutcome.Completed => $"{count}を「{folderName}」に{verb}しました。",
            TransferOutcome.Partial => $"{result.Requested:N0} 個のうち {result.Done:N0} 個を「{folderName}」に{verb}しました。",
            TransferOutcome.Canceled => gesture == TransferGesture.Paste
                ? $"「{folderName}」への貼り付けを中止しました。"
                : $"「{folderName}」への{verb}を中止しました。",
            _ => gesture == TransferGesture.Paste
                ? $"「{folderName}」に貼り付けできませんでした。"
                : $"「{folderName}」に{verb}できませんでした。",
        };

        if (result.Outcome is TransferOutcome.Completed or TransferOutcome.Partial && result.MovesRequested > 0 && copies > 0)
        {
            text += $"（移動できない場所にある {copies:N0} 個はコピーしました）";
        }

        if (plan.Skipped.Count > 0)
        {
            text += gesture == TransferGesture.Paste
                ? $"（貼り付けできない {plan.Skipped.Count:N0} 個は除きました）"
                : $"（{verb}できない {plan.Skipped.Count:N0} 個は除きました）";
        }

        if (result.Error is { Length: > 0 } error && result.Outcome != TransferOutcome.Completed)
        {
            text += "：" + error;
        }

        return text;
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
            bool keptInMemoryOnly = KeepInPlace(ActivePane, item.Path);
            pins.Pin(item.Path, item.IsFolder ? ItemKind.Folder : ItemKind.File);
            Message.Success($"「{item.Name}」をピン留めしました。");

            // The card is already listed there; a reload would rebuild the base order from the
            // defaults (pinned items first) and move it.
            reloadFrequent = !(keptInMemoryOnly && ActivePane == FrequentPane);
        }

        FrequentPane.IsStale = true;
        if (reloadFrequent && ActivePane == FrequentPane && FrequentPane.IsTop)
        {
            _ = FrequentPane.LoadAsync();
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
    private bool KeepInPlace(PaneViewModel tab, string path)
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
            moved |= tab.ApplyPins(pins) && ReferenceEquals(tab, ActivePane);
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
            var probe = await Existence.ProbeAsync(_services.FileSystem, item.Path, RegisteredCheckTimeout, CancellationToken.None);
            item.State = probe.State switch
            {
                ExistState.Folder => SidebarItemState.Ok,
                ExistState.Timeout => SidebarItemState.NoResponse,
                _ => SidebarItemState.Missing,
            };
            if (item.State == SidebarItemState.Ok)
            {
                await RememberRegisteredIdentityAsync(item);
            }
            else if (item.State == SidebarItemState.Missing)
            {
                await FollowRegisteredAsync(item); // renamed or moved: the row is replaced by one at the new path
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Existence check of {item.Path} failed", ex);
        }
    }

    /// <summary>
    /// A click on a 登録フォルダ of the sidebar: opens it in a folder tab. A folder shown as missing is
    /// looked for first (renamed or moved since it was checked): found, it opens at its new path.
    /// </summary>
    public async Task OpenRegisteredAsync(SidebarItemViewModel item)
    {
        if (item.State == SidebarItemState.Missing && await FollowRegisteredAsync(item)
            && Registered.FirstOrDefault(r => r.Id == item.Id) is { } moved)
        {
            item = moved;
        }

        if (!item.IsDisabled)
        {
            await OpenFolderTabAsync(item.Target, recordPath: item.Path);
        }
    }

    /// <summary>
    /// A click on a drive of the PC section: opens its root in a folder tab. Unlike a 登録フォルダ it
    /// is not recorded in the history (the drive is always listed in the sidebar).
    /// </summary>
    public Task OpenDriveAsync(SidebarItemViewModel item) =>
        item.IsDisabled ? Task.CompletedTask : OpenFolderTabAsync(item.Target);

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
        RecentPane.RemoveCard(card); // the count and the empty text follow
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
            RecentPane.IsStale = true;
            Message.Info($"「{card.Name}」を一覧から外しました。");
        }

        _ = FrequentPane.LoadAsync();
    }

    // ---- 最近の履歴 (§10.2) -------------------------------------------------------------------

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (!ActivePane.ShowClearHistory)
        {
            return; // offered on the list of 最近の履歴 only
        }

        bool ok = View?.Confirm("履歴をクリアしますか？", "この一覧を空にします。ファイルや Windows の履歴は削除されません。", null, "履歴をクリア", danger: true) ?? false;
        if (!ok)
        {
            return;
        }

        var now = JsonFile.TruncateToSeconds(_services.Clock.UtcNow);
        _services.Stores.Settings.Update(s => s.RecentClearedAt = now);
        await RecentPane.LoadAsync();
        Message.Success("最近の履歴をクリアしました。");
    }

    // ---- auto refresh ---------------------------------------------------------------------------

    /// <summary>How often the auto refresh looks whether a check is due (the interval itself is the setting).</summary>
    private static readonly TimeSpan AutoRefreshBeat = TimeSpan.FromSeconds(1);

    /// <summary>A check that takes longer is given up (the listing goes on in the cache, and is waited for before the next check).</summary>
    private static readonly TimeSpan AutoRefreshWait = TimeSpan.FromSeconds(15);

    /// <summary>A folder that fails is checked less and less often, at most this far apart (or the interval, if longer).</summary>
    private const long AutoRefreshMaxHoldMs = 300_000;

    private DispatcherTimer? _autoRefreshTimer;

    /// <summary>The interval in milliseconds; 0 while the auto refresh is off.</summary>
    private long _autoRefreshMs;

    /// <summary>The wait of the last check: one check at a time (it gives up after <see cref="AutoRefreshWait"/>, which also cancels the listing).</summary>
    private Task<FolderListing>? _autoRefreshFlight;

    private string? _autoRefreshFailPath;
    private int _autoRefreshFailures;
    private long _autoRefreshHoldUntil;
    private int _autoRefreshMissing;
    private string? _autoRefreshMissingPath;

    /// <summary>
    /// フォルダの自動更新 (settings <c>auto_refresh</c> / <c>auto_refresh_seconds</c>): a heartbeat
    /// on the UI thread checks once a second whether the shown folder is due (listed longer ago
    /// than the interval); a load, F5 or a file operation count as a listing too.
    /// </summary>
    private void ApplyAutoRefreshSetting(AppSettings settings)
    {
        _autoRefreshMs = settings.AutoRefresh ? settings.AutoRefreshSeconds * 1000L : 0;
        if (_autoRefreshTimer is null)
        {
            _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = AutoRefreshBeat };
            _autoRefreshTimer.Tick += (_, _) => _ = AutoRefreshTickAsync();
        }

        if (_autoRefreshMs > 0)
        {
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    /// <summary>
    /// 常駐モード: the window was hidden to the notification area. The state is saved (the stores
    /// write on their own threads, waited for off the UI thread) and the checks for the screen stop.
    /// </summary>
    public void OnWindowHidden(WindowPlacementSetting? window)
    {
        SaveState(window);
        _autoRefreshTimer?.Stop();
        var stores = _services.Stores;
        _ = Task.Run(stores.FlushAll);
    }

    /// <summary>
    /// 常駐モード: the hidden window is shown again. The auto refresh resumes and the shown tab is
    /// brought up to date at once (a folder: compared with a fresh listing, the cards stay where they
    /// are; a list of 最近の履歴 / よく使う項目: read again). The other tabs read again when shown.
    /// </summary>
    public async Task OnWindowShownAsync()
    {
        ApplyAutoRefreshSetting(Settings);
        foreach (var tab in AllTabs)
        {
            if (!ReferenceEquals(tab, ActivePane))
            {
                tab.IsStale = true;
            }
        }

        if (!HasTab)
        {
            return;
        }

        if (ActivePane.AutoRefreshPath is not null)
        {
            await AutoRefreshTickAsync(force: true);
        }
        else
        {
            await LoadTabAsync(ActivePane);
        }
    }

    /// <summary>The window closes: no more checks.</summary>
    public void StopAutoRefresh()
    {
        _autoRefreshMs = 0;
        _autoRefreshTimer?.Stop();
    }

    /// <summary>
    /// A check may run, and its result may replace the list, now: no file operation, no check of
    /// a cut, no drag of the app, and nothing on screen that a replaced list would break
    /// (<see cref="IMainView.IsAutoRefreshBlocked"/>: a name's edit box, a marquee, the cascade or a
    /// menu, a dialog, a button held, input just now, the window minimized).
    /// </summary>
    private bool CanAutoRefresh(bool force = false) =>
        (force || _autoRefreshMs > 0) && !_fileOpBusy && !_cutCheckBusy && !FileDragOut.IsDragging && View is { IsAutoRefreshBlocked: false };

    /// <param name="force">Now, whatever the interval and the setting (the window was shown again).</param>
    private async Task AutoRefreshTickAsync(bool force = false)
    {
        try
        {
            if (_autoRefreshFlight is { IsCompleted: false })
            {
                return; // the previous check still waits for its folder
            }

            var tab = ActivePane;
            if (!HasTab || tab.AutoRefreshPath is not { } path)
            {
                return;
            }

            long now = Environment.TickCount64;
            if ((!force && now - tab.LastListedAt < _autoRefreshMs)
                || (PathTools.Comparer.Equals(path, _autoRefreshFailPath) && now < _autoRefreshHoldUntil)
                || !CanAutoRefresh(force))
            {
                return; // not due, held back after failures, or busy: the next beat decides again
            }

            int generation = tab.LoadGeneration;
            var filter = _services.Listings.Filter;
            _services.Listings.Invalidate(path); // else the 60 s cache answers

            // The only waiter: giving up cancels the listing (a hung share does not keep a check, or
            // a slot of its volume, forever; the hold below keeps the next checks apart).
            using var wait = new CancellationTokenSource(AutoRefreshWait);
            var flight = _services.Listings.GetAsync(path, wait.Token);
            _autoRefreshFlight = flight;
            FolderListing fresh;
            try
            {
                fresh = await flight;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or UnauthorizedAccessException)
            {
                _autoRefreshMissing = 0;
                HoldAutoRefresh(path, ex is OperationCanceledException ? "timeout" : ex.GetType().Name);
                return;
            }

            if (!fresh.IsOk)
            {
                // A failed check is not left in the cache for 60 s: the next load would show its error banner.
                _services.Listings.Invalidate(path);
            }

            // Checked again after the await: the user may have moved, reloaded or started something.
            if (!CanAutoRefresh(force) || !ReferenceEquals(ActivePane, tab) || tab.LoadGeneration != generation
                || !PathTools.Comparer.Equals(tab.AutoRefreshPath, path) || _services.Listings.Filter != filter)
            {
                return;
            }

            switch (fresh.Status)
            {
                case ListingStatus.Ok:
                    if (_autoRefreshFailures > 0 && PathTools.Comparer.Equals(path, _autoRefreshFailPath))
                    {
                        AppLog.Info("Auto refresh: the folder answers again");
                    }

                    _autoRefreshFailures = 0;
                    _autoRefreshFailPath = null;
                    _autoRefreshMissing = 0;
                    _autoRefreshMissingPath = null;
                    double offset = View?.GetScrollOffset() ?? 0;
                    if (tab.ApplyPolledListing(fresh, now))
                    {
                        View?.ScrollTo(offset); // the items view rebuilt its rows: stay where the user was
                    }

                    break;
                case ListingStatus.NotFound:
                    if (!PathTools.Comparer.Equals(path, _autoRefreshMissingPath))
                    {
                        _autoRefreshMissingPath = path;
                        _autoRefreshMissing = 0;
                    }

                    // Gone twice in a row (this folder, nothing else in between): an ordinary load
                    // shows the "not found" banner (F5 or a move brings it back). A network folder
                    // only waits: an unreachable share also answers "not found".
                    if (++_autoRefreshMissing >= 2 && !IsOnNetwork(path))
                    {
                        _autoRefreshMissing = 0;
                        _autoRefreshMissingPath = null;
                        await LoadTabAsync(tab); // the banner, unless the folder was renamed or moved: then the tab follows it
                    }
                    else
                    {
                        HoldAutoRefresh(path, fresh.Status.ToString());
                    }

                    break;
                default:
                    _autoRefreshMissing = 0;
                    HoldAutoRefresh(path, fresh.Status.ToString()); // the cards stay; no banner for a hiccup
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Auto refresh failed", ex);
        }
    }

    /// <summary>A UNC path or a path on a network drive (the drive type is read from the root only: no network I/O).</summary>
    private static bool IsOnNetwork(string path)
    {
        if (TransferRules.IsNetworkPath(path))
        {
            return true;
        }

        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    /// <summary>A check of <paramref name="path"/> failed: the next one waits longer (doubling, see <see cref="AutoRefreshMaxHoldMs"/>).</summary>
    private void HoldAutoRefresh(string path, string reason)
    {
        if (!PathTools.Comparer.Equals(path, _autoRefreshFailPath))
        {
            _autoRefreshFailPath = path;
            _autoRefreshFailures = 0;
        }

        if (++_autoRefreshFailures == 1)
        {
            AppLog.Info($"Auto refresh: the folder did not answer ({reason}); checking less often"); // no path in the log
        }

        long interval = Math.Max(_autoRefreshMs, 1000);
        long hold = Math.Min(interval << Math.Min(_autoRefreshFailures, 10), Math.Max(interval, AutoRefreshMaxHoldMs));
        _autoRefreshHoldUntil = Environment.TickCount64 + hold;
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
            ManagesRunKey = _services.ManagesRunKey,
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

        if (before.TextScale != after.TextScale)
        {
            View?.ApplyTextScale(after.TextScale);
        }

        Message.Placement = MessagePlacements.FromSetting(after.MessagePosition); // before 設定を保存しました below: it shows in the new place
        _services.Filter = after.ToListingFilter();
        if (before.ToListingFilter() != after.ToListingFilter())
        {
            foreach (var tab in AllTabs)
            {
                tab.IsStale = true;
            }
        }

        if (before.IncludeWindowsRecent != after.IncludeWindowsRecent || before.RecentLimit != after.RecentLimit)
        {
            RecentPane.IsStale = true;
        }

        if (before.FrequentLimit != after.FrequentLimit)
        {
            FrequentPane.IsStale = true;
        }

        if (before.AutoRefresh != after.AutoRefresh || before.AutoRefreshSeconds != after.AutoRefreshSeconds)
        {
            ApplyAutoRefreshSetting(after); // turned on: the shown folder is checked about a second after OK
        }

        if (before.SearchScope != after.SearchScope)
        {
            foreach (var tab in AllTabs)
            {
                tab.UpdateDeepSearch(); // a search under way follows the new scope
            }
        }

        if (before.StartAtLogin != after.StartAtLogin && _services.ManagesRunKey)
        {
            bool wanted = after.StartAtLogin;
            string? error = await Task.Run(() => RunKeyRegistry.Apply(wanted));
            if (error is not null)
            {
                _services.Stores.Settings.Update(s => s.StartAtLogin = before.StartAtLogin); // the registry stays the truth
                Message.Error($"Windows のスタートアップに{(wanted ? "登録" : "解除")}できませんでした：{error}");
                ResidentSettingsChanged?.Invoke();
                return;
            }
        }

        if (before.StartAtLogin != after.StartAtLogin || before.KeepRunningOnClose != after.KeepRunningOnClose)
        {
            ResidentSettingsChanged?.Invoke();
        }

        Message.Success("設定を保存しました。");
        if (HasTab && ActivePane.IsStale)
        {
            await ActivePane.LoadAsync();
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
        RecentPane.IsStale = true;
        FrequentPane.IsStale = true;
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
