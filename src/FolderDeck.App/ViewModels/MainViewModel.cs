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

    /// <summary>
    /// <see cref="Tabs"/> holds what settings.json said (<see cref="InitializeAsync"/> got that far).
    /// Until then <see cref="SaveState"/> leaves <c>open_tabs</c> alone: a start that failed halfway
    /// must not replace the saved tabs with a partial list.
    /// </summary>
    private bool _tabsRestored;

    /// <summary>The shown tab; <see cref="NoTab"/> (never null) while no tab is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack), nameof(CanGoForward), nameof(HasTab))]
    [NotifyCanExecuteChangedFor(nameof(GoUpCommand))]
    private TabViewModel _selectedTab;

    [ObservableProperty]
    private int _cardSize = AppSettings.CardSizeDefault;

    /// <summary>
    /// How every tab shows its items (toolbar, right of the sort button): cards of one of two
    /// shapes, or the details list.
    /// </summary>
    [ObservableProperty]
    private CardShape _cardShape = CardShape.Portrait;

    /// <summary>
    /// Widths of the columns of the details list (<c>details_columns</c>: always the four known keys,
    /// name / modified / type / size). Replaced, never changed in place, by <see cref="SetDetailsColumnWidth"/>.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, double> _detailsColumns = DetailsColumnRules.Defaults;

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
        _sidebarWidth = settings.SidebarWidth;
        _isSidebarCollapsed = settings.SidebarCollapsed;
        _detailsColumns = DetailsColumnRules.Normalize(settings.DetailsColumns);
        QuickSection = NewSection(SidebarSections.Quick, "クイック アクセス", settings);
        RegisteredSection = NewSection(SidebarSections.Registered, "登録フォルダ", settings);
        PcSection = NewSection(SidebarSections.Pc, "PC", settings);
        DriveList = new DriveListViewModel(services.Icons);

        RecentTab = new TabViewModel(services, TabKind.Recent);
        FrequentTab = new TabViewModel(services, TabKind.Frequent);
        NoTab = new TabViewModel(services, TabKind.None) { IsStale = false };
        Watch(RecentTab);
        Watch(FrequentTab);
        _selectedTab = NoTab; // until InitializeAsync has restored the tabs
        NoTab.IsSelected = true;
        services.Stores.Pins.Changed += OnPinsChanged;
    }

    public IMainView? View { get; set; }

    public AppServices Services => _services;

    public MessageBarViewModel Message { get; } = new();

    /// <summary>最近の履歴: one instance for the whole session, open only while it is in <see cref="Tabs"/>.</summary>
    public TabViewModel RecentTab { get; }

    /// <summary>よく使う項目: one instance for the whole session, open only while it is in <see cref="Tabs"/>.</summary>
    public TabViewModel FrequentTab { get; }

    /// <summary>
    /// What <see cref="SelectedTab"/> is while no tab is open (<see cref="TabKind.None"/>). Never in
    /// <see cref="Tabs"/>, never loaded, never a navigation target: commands that act on the shown
    /// tab check <see cref="HasTab"/> first.
    /// </summary>
    public TabViewModel NoTab { get; }

    /// <summary>
    /// The tab strip, left to right: every open tab, of any kind. All of them can be closed and
    /// moved; 最近の履歴 and よく使う項目 are in it at most once.
    /// </summary>
    public ObservableCollection<TabViewModel> Tabs { get; } = [];

    /// <summary>A tab is shown (false: every tab was closed and the empty state is on screen).</summary>
    public bool HasTab => !SelectedTab.IsPlaceholder;

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

    public bool CanGoBack => SelectedTab.BackStack.Count > 0;

    public bool CanGoForward => SelectedTab.ForwardStack.Count > 0;

    private AppSettings Settings => _services.Stores.Settings.Current;

    /// <summary>
    /// Every tab that holds data: the two fixed ones (open or not: a closed one is reloaded when it
    /// is opened again, so it has to go stale with the others), then the open folder tabs.
    /// </summary>
    private IEnumerable<TabViewModel> AllTabs => new[] { RecentTab, FrequentTab }.Concat(Tabs.Where(t => t.IsFolderTab));

    private void Watch(TabViewModel tab) => tab.PropertyChanged += OnTabPropertyChanged;

    private void OnTabPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TabViewModel.Location))
        {
            GoUpCommand.NotifyCanExecuteChanged();
        }
        else if (e.PropertyName == nameof(TabViewModel.Cards) && ReferenceEquals(sender, SelectedTab))
        {
            View?.CloseCardCascade(); // the grid rebuilds its cards: an open overlay would be left behind
        }
    }

    partial void OnSelectedTabChanged(TabViewModel? oldValue, TabViewModel newValue)
    {
        if (oldValue is not null)
        {
            oldValue.IsSelected = false;
        }

        newValue.IsSelected = true;
    }

    // ---- startup / shutdown -----------------------------------------------------------------

    /// <summary>
    /// Restores the tab strip (<c>open_tabs</c>, in order, each tab where it was) and the sidebar,
    /// and shows the tab that was shown (§5.5). Not recorded in history (§10.4).
    /// </summary>
    public async Task InitializeAsync()
    {
        var settings = Settings;
        CardSize = settings.CardSize;
        CardShape = CardShapes.FromSetting(settings.CardShape);
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
                    AddFolderTab(new NavLocation(TabKind.Folder, entry.Root, entry.Path));
                }

                continue;
            }

            var tab = kind == TabKind.Recent ? RecentTab : FrequentTab;
            if (tab.IsOpen)
            {
                continue;
            }

            if (located)
            {
                tab.Location = new NavLocation(kind, entry.Root, entry.Path);
                _restoring.Add(tab);
            }

            AddToStrip(tab);
        }

        RefreshRegistered();
        _ = DriveList.RefreshAsync(recheck: false); // not awaited: a drive that does not answer must not delay the first tab
        SelectedTab = Tabs.Count == 0 ? NoTab : Tabs[Math.Clamp(selected, 0, Tabs.Count - 1)];
        _tabsRestored = true; // before the first await: what the user does while the first tab loads is saved too
        if (HasTab)
        {
            await LoadTabAsync(SelectedTab);
        }
    }

    public void SaveState(WindowPlacementSetting? window)
    {
        bool tabs = _tabsRestored;
        int selectedTab = Tabs.IndexOf(SelectedTab); // -1: no tab is open
        var openTabs = Tabs
            .Select(t => new OpenTabSetting { Kind = TabKinds.ToSetting(t.Kind), Root = t.Location.Root, Path = t.Location.Rel })
            .ToList();
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
            }

            s.SidebarWidth = SidebarWidth;
            s.CardSize = CardSize;
            s.CardShape = CardShapes.ToSetting(CardShape);
        });
    }

    // ---- tabs and navigation (§8) -----------------------------------------------------------

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task SelectTab(TabViewModel? tab)
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
    /// Makes <paramref name="tab"/> the shown one (a tab of <see cref="Tabs"/>, or <see cref="NoTab"/>
    /// when the last one was closed). The tab left remembers its scroll position; the lists of
    /// 最近の履歴 / よく使う項目 are rebuilt each time they are shown, a folder is listed again only
    /// when it went stale and otherwise comes back where it was scrolled to.
    /// </summary>
    private async Task ShowTabAsync(TabViewModel tab)
    {
        bool switched = !ReferenceEquals(tab, SelectedTab);
        double offset = switched ? tab.SavedScrollOffset : View?.GetScrollOffset() ?? 0;
        if (switched)
        {
            LeaveSelectedTab();
            SelectedTab = tab;
        }

        if (tab.IsPlaceholder)
        {
            return; // nothing to load, nothing to scroll
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
    /// The shown tab is about to be replaced: it remembers where it was scrolled to. Not a tab that
    /// was just closed (a fixed tab starts fresh when it is opened again), nor the placeholder.
    /// </summary>
    private void LeaveSelectedTab()
    {
        if (SelectedTab.IsOpen)
        {
            SelectedTab.SavedScrollOffset = View?.GetScrollOffset() ?? 0;
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
        TabViewModel? tab = kind switch
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
            if (!tab.IsTop)
            {
                await NavigateAsync(NavLocation.Top(kind), NavMode.New);
            }

            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        if (!tab.IsOpen)
        {
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
        var tab = Tabs.FirstOrDefault(t => t.IsFolderTab && SameLocation(t.Location, target));
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
        var tab = new TabViewModel(_services, TabKind.Folder, location) { CutPaths = _cutPaths };
        Watch(tab);
        AddToStrip(tab);
        return tab;
    }

    /// <summary>Puts a tab at the right end of the strip (<see cref="TabViewModel.IsOpen"/> follows <see cref="Tabs"/>).</summary>
    private void AddToStrip(TabViewModel tab)
    {
        Tabs.Add(tab);
        tab.IsOpen = true;
    }

    /// <summary>
    /// Takes a tab off the strip. A folder tab is gone for good; a fixed tab lives on (still watched,
    /// still going stale with the others) and is reset, so it starts at its list when opened again.
    /// </summary>
    private void RemoveFromStrip(TabViewModel tab)
    {
        tab.CancelLoad();
        Tabs.Remove(tab);
        tab.IsOpen = false;
        if (tab.IsFolderTab)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
        }
        else
        {
            _restoring.Remove(tab);
            tab.ResetToTop();
        }
    }

    private static bool SameLocation(NavLocation a, NavLocation b) =>
        PathTools.PathEquals(a.Root, b.Root) && string.Equals(a.Rel, b.Rel, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Closes a tab of any kind (× / middle click / Ctrl + W). When it was the shown one, the tab to
    /// its right (else the left one) is shown; without any tab left, <see cref="NoTab"/>.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CloseTab(TabViewModel? tab)
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
    public async Task CloseOtherTabsAsync(TabViewModel keep)
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
    public void MoveTab(TabViewModel tab, int newIndex)
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
        var loc = SelectedTab.Location;
        if (!HasTab || !SelectedTab.CanGoUp)
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
    private Task BackToTop() => SelectedTab.Kind switch
    {
        TabKind.None => Task.CompletedTask,
        TabKind.Folder => CloseTab(SelectedTab),
        _ => NavigateAsync(NavLocation.Top(SelectedTab.Kind), NavMode.New),
    };

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
        _ = DriveList.RefreshAsync(recheck: true);
        if (HasTab)
        {
            await LoadTabAsync(SelectedTab);
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
            if (!tab.IsOpen)
            {
                AddToStrip(tab);
            }

            LeaveSelectedTab();
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

    partial void OnCardShapeChanged(CardShape oldValue, CardShape newValue)
    {
        View?.CloseCascade(); // the overlay covers the thumbnail, whose height changes; a row's list hangs on a row
        _services.Stores.Settings.Update(s => s.CardShape = CardShapes.ToSetting(newValue));
        if (CardShapes.IsCards(oldValue) != CardShapes.IsCards(newValue))
        {
            // Another view takes over (card grid ↔ details list). A scroll position is an offset of
            // the view it was read from, so every remembered one is dropped rather than applied to
            // the wrong view; the view that shows the items from now on starts at the top.
            foreach (var tab in AllTabs)
            {
                tab.ResetScrollOffsets();
            }

            View?.ScrollTo(0);
        }
    }

    /// <summary>
    /// A column of the details list was resized (<paramref name="key"/>: a <c>DetailsColumns</c> key).
    /// Saved at once, like every other layout choice; an unknown key or a width that changes
    /// nothing is ignored.
    /// </summary>
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
    /// Changes the selected tab's sort mode (saved per tab; the folder tabs share one, so all of
    /// them follow) and re-orders the cards in place.
    /// </summary>
    public void SetSortMode(CardSortMode mode)
    {
        View?.CloseCascade();
        var tab = SelectedTab;
        if (!HasTab || tab.SortMode == mode)
        {
            return;
        }

        SaveSortMode(tab, mode);
        tab.ApplyOrder();
        if (tab.IsFolderTab)
        {
            foreach (var other in Tabs.Where(t => t.IsFolderTab && !ReferenceEquals(t, tab)))
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
                // A drive row is always at hand in the PC section: opening it is not a use worth remembering.
                string? recordPath = item.Context == ItemContext.Drive ? null : item.Path;
                return item.NewTab
                    ? OpenFolderTabAsync(target, recordPath)
                    : NavigateAsync(target, NavMode.New, recordPath: recordPath);
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
    /// F2: the one selected card, or null (nothing selected: the key does nothing; several selected:
    /// said on the message line). Whether it can be renamed is <see cref="CanStartRename"/>'s call.
    /// </summary>
    public CardViewModel? RenameCandidate()
    {
        var cards = SelectedTab.SelectedCards();
        if (cards.Count > 1)
        {
            Message.Info("名前を変更する項目を 1 つだけ選んでください。");
            return null;
        }

        return cards.Count == 1 ? cards[0] : null;
    }

    /// <summary>The edit box may open for <paramref name="card"/> now; says why not on the message line.</summary>
    public bool CanStartRename(CardViewModel card)
    {
        if (card.Context != ItemContext.InnerCard)
        {
            Message.Info("この一覧の項目は名前を変更できません（変更できるのはフォルダの中の項目だけです）。");
            return false;
        }

        if (!CanRename(card.ToItemRef()) || !SelectedTab.Cards.Contains(card))
        {
            Message.Info("この項目は名前を変更できません。");
            return false;
        }

        if (SelectedTab.IsLoading)
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

        await ReloadAfterFileChangeAsync();

        // The old path left the selection with the reload. Anything selected now was chosen while
        // the rename ran (the press that committed the name was a click on another item): kept.
        if (SelectedTab.Selection.Count == 0)
        {
            SelectInShownFolder(folder, result.NewPath);
        }

        return false;
    }

    /// <summary>
    /// The card of the shown tab for <paramref name="path"/> (cards are made anew by every load, so
    /// one held since a menu opened may have been replaced), or null when it is not shown.
    /// </summary>
    public CardViewModel? ShownCard(string path) =>
        SelectedTab.Cards.FirstOrDefault(c => PathTools.PathEquals(c.Path, path));

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
            Message.Info(HasTab && SelectedTab.FolderTargetPath is not null && SelectedTab.IsLoading
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
        string name = ItemNames.NextNewFolderName(SelectedTab.Cards.Select(c => c.Name));
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

        await ReloadAfterFileChangeAsync();
        return SelectInShownFolder(folder, result.NewPath);
    }

    /// <summary>
    /// After a reload: selects <paramref name="path"/> alone when the shown tab still lists
    /// <paramref name="folder"/>, and returns its card (null otherwise).
    /// </summary>
    private CardViewModel? SelectInShownFolder(string folder, string? path)
    {
        var tab = SelectedTab;
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

        var shown = SelectedTab;
        if (shown.IsPlaceholder)
        {
            return; // no tab is open (the item was a row beside the sidebar): nothing to list again
        }

        double offset = View?.GetScrollOffset() ?? 0;
        var outcome = await shown.LoadAsync();
        if (outcome == LoadOutcome.Ok && ReferenceEquals(shown, SelectedTab))
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

    // ---- selection, clipboard and paste ---------------------------------------------------------

    private static readonly TimeSpan ClipboardReadTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TargetCheckTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SourceProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CutCheckTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A paste, a delete to the Recycle Bin, a rename or a new folder is running (one at a time: the
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

    /// <summary>A copy / move started by a paste or a move to the Recycle Bin is running (the window must not close silently).</summary>
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

        var tab = SelectedTab;
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
        var tab = SelectedTab;
        if (HasTab && !card.IsDisabled && tab.Cards.Contains(card))
        {
            tab.Selection.Replace([card.Path]);
        }
    }

    /// <summary>Ctrl+A: every enabled item of the shown list. Returns true when the selection changed.</summary>
    public bool SelectAll() => HasTab && SelectedTab.Selection.SelectAll(SelectedTab.SelectableKeys());

    /// <summary>Esc, a plain click, a press on the blank area. Returns true when something was selected.</summary>
    public bool ClearSelection() => SelectedTab.Selection.Clear();

    /// <summary>
    /// What an action on <paramref name="clicked"/> applies to: the whole selection in display order
    /// when the item is part of it, else the item alone (cascade rows and headers are never part of it).
    /// </summary>
    public IReadOnlyList<ItemRef> SelectionTargets(ItemRef clicked)
    {
        var tab = SelectedTab;
        if (clicked.Card is not { } card || !tab.Selection.Contains(card.Path) || !tab.Cards.Contains(card))
        {
            return [clicked];
        }

        return tab.SelectedCards().Select(c => c.ToItemRef()).ToList();
    }

    /// <summary>The selection of the shown list as items (empty when nothing is selected).</summary>
    public IReadOnlyList<ItemRef> SelectedItems() => SelectedTab.SelectedCards().Select(c => c.ToItemRef()).ToList();

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
    /// folder is not really on screen: see <see cref="TabViewModel.TransferTargetPath"/>).
    /// </summary>
    public string? PasteFolderOf(ItemRef? item)
    {
        if (item is null)
        {
            return SelectedTab.TransferTargetPath;
        }

        bool usable = item.IsFolder && !item.IsDisabled && item.Card?.IsDisabled != true;
        return usable && PathTools.IsFullyQualified(item.Path) ? item.Path : null;
    }

    /// <summary>
    /// The folder the shown tab lists, as an item of its own, for the menu of the blank area
    /// (エクスプローラーで表示, パスをコピー, 登録フォルダに追加 and Explorer's background menu). Null
    /// where <see cref="TabViewModel.TransferTargetPath"/> is (the top lists, no tab, loading, a
    /// failed load). No ItemContext of its own: a root is a Drive (using it is not recorded in the
    /// history, as for a drive row), any other folder a FlyoutHeader (the folder itself, never cut
    /// or deleted). Those three commands branch on no other context.
    /// </summary>
    public ItemRef? ShownFolderItem() =>
        SelectedTab.TransferTargetPath is { } path
            ? new ItemRef(path, IsFolder: true, SelectedTab.Location, PathTools.IsRoot(path) ? ItemContext.Drive : ItemContext.FlyoutHeader)
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

            string folderName = PathTools.DisplayName(folder);
            if (!await SourceProbe.FolderExistsAsync(folder, TargetCheckTimeout))
            {
                Message.Error($"貼り付け先のフォルダが見つかりません：{folder}");
                return;
            }

            var kind = read.Files.Kind;
            var sources = await SourceProbe.DescribeAsync(read.Files.Paths, SourceProbeTimeout);
            var plan = TransferRules.Decide(sources, folder, TransferOptions.Paste(kind));
            if (plan.Items.Count == 0)
            {
                Message.Info(RefusalText(plan.Refusal, kind));
                return;
            }

            var result = await FileTransfer.RunAsync(plan.Items, folder, owner);
            Message.Show(PasteMessageKind(result), PasteResultText(result, plan, folderName));

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

            await ReloadAfterFileChangeAsync();
            if (result.NewPaths.Count > 0 && PathTools.PathEquals(SelectedTab.TransferTargetPath, folder))
            {
                // What arrived is selected, as in Explorer (only what the reloaded list shows).
                var tab = SelectedTab;
                tab.Selection.Replace(result.NewPaths);
                tab.Selection.Prune(tab.SelectableKeys());
            }
        }
        finally
        {
            _fileOpBusy = false;
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

    private static string RefusalText(TransferRefusal refusal, TransferKind kind) => refusal switch
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

    /// <summary>The message after a paste: it always names the destination, and says how many items were copied instead of moved.</summary>
    private static string PasteResultText(TransferResult result, TransferPlan plan, string folderName)
    {
        int copies = result.Requested - result.MovesRequested;
        string verb = result.MovesRequested == 0 ? "コピー" : copies == 0 ? "移動" : "移動・コピー";
        string count = result.Requested == 1 ? $"「{PathTools.DisplayName(plan.Items[0].Path)}」" : $"{result.Requested:N0} 個の項目";
        string text = result.Outcome switch
        {
            TransferOutcome.Completed => $"{count}を「{folderName}」に{verb}しました。",
            TransferOutcome.Partial => $"{result.Requested:N0} 個のうち {result.Done:N0} 個を「{folderName}」に{verb}しました。",
            TransferOutcome.Canceled => $"「{folderName}」への貼り付けを中止しました。",
            _ => $"「{folderName}」に貼り付けできませんでした。",
        };

        if (result.Outcome is TransferOutcome.Completed or TransferOutcome.Partial && result.MovesRequested > 0 && copies > 0)
        {
            text += $"（移動できない場所にある {copies:N0} 個はコピーしました）";
        }

        if (plan.Skipped.Count > 0)
        {
            text += $"（貼り付けできない {plan.Skipped.Count:N0} 個は除きました）";
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
            var probe = await Existence.ProbeAsync(_services.FileSystem, item.Path, RegisteredCheckTimeout, CancellationToken.None);
            item.State = probe.State switch
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
        if (!SelectedTab.ShowClearHistory)
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

        if (before.TextScale != after.TextScale)
        {
            View?.ApplyTextScale(after.TextScale);
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
        if (HasTab && SelectedTab.IsStale)
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
