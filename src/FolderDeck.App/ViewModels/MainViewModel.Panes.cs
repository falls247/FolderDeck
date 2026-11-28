using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Stores;

namespace FolderDeck.ViewModels;

// ---- panes: groups of tabs shown side by side ------------------------------------------------------
//
// A pane group is one layout of the window (2 to PaneRules.MaxPanes panes). Its leaves are the panes of
// tabs of the strip, or empty panes (made by a split, filled by the next tab picked while one is
// active). There may be several groups; a tab is in at most one. A group's tabs stand together on the
// strip in the reading order of its leaves (ArrangeGroupTabs). Picking a tab of a group shows its group;
// any other tab is shown alone, unless the group on screen has an empty pane active: then the tab fills it.

public sealed partial class MainViewModel
{
    /// <summary>One layout of tabs on screen together (see the comment at the top of this file).</summary>
    private sealed class PaneGroup(PaneLayout<PaneViewModel> layout)
    {
        public PaneLayout<PaneViewModel> Layout { get; set; } = layout;

        /// <summary>The pane that was active when the group was last on screen (it is active again when the group comes back through a tab menu).</summary>
        public PaneViewModel LastActive { get; set; } = layout.Panes[0];

        /// <summary>The 登録ペイン (registered_panes.json) the group was opened from or registered as; null for none.</summary>
        public string? RegisteredPaneId { get; set; }
    }

    private readonly List<PaneGroup> _groups = [];

    /// <summary>The group on screen; null while one tab fills the items area. Changed only by <see cref="SetShownGroup"/>.</summary>
    private PaneGroup? _shownGroup;

    /// <summary>The group shown last (on screen or not): where 「選択中のペインに入れる」 puts a tab while no group is shown.</summary>
    private PaneGroup? _lastShownGroup;

    /// <summary>
    /// The stand-in tab of each empty pane, by its pane. Never in <see cref="Tabs"/>; selected while its
    /// pane is the active one (then <see cref="HasTab"/> is false). One per leaf: a layout tells its panes
    /// apart by reference. Dropped when no group holds the pane any more (<see cref="ForgetUnusedEmptyPanes"/>).
    /// </summary>
    private readonly Dictionary<PaneViewModel, TabLayoutViewModel> _emptyTabs = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The pane being activated is on screen already (a press in it, F6): its view does not change,
    /// so a cascade on it stays when its view mode differs from the one the toolbar showed.
    /// </summary>
    private bool _activatingShownPane;

    /// <summary>
    /// What the items area shows: the group on screen, or the shown tab's pane alone. The view builds
    /// the panes from it. Announced with <see cref="SelectedTab"/> (before <see cref="ActivePane"/>) and when the group changes.
    /// </summary>
    public PaneLayout<PaneViewModel> ShownLayout => _shownGroup?.Layout ?? PaneLayout<PaneViewModel>.Single(ActivePane);

    /// <summary>A group is on screen (more than one pane).</summary>
    public bool IsSplit => _shownGroup is not null;

    /// <summary>Something is in the items area: a tab, a group (whose active pane may be empty), or the blank page of ＋. False only for the empty window.</summary>
    public bool HasPanes => HasTab || IsSplit || IsBlankPane;

    /// <summary>The empty pane the tab strip's ＋ opened last (shown alone, in no group: <see cref="IsBlankPane"/> while it is the active pane).</summary>
    private PaneViewModel? _blankPane;

    /// <summary>The tab that was shown when ＋ opened the blank page: 「このペインを閉じる」 goes back to it.</summary>
    private TabLayoutViewModel? _beforeBlank;

    /// <summary>
    /// The blank page of ＋ is shown: one empty pane alone, in no group. The next tab picked is shown
    /// alone (or with its group) in its place; it does not split.
    /// </summary>
    public bool IsBlankPane => _shownGroup is null && _blankPane is { } blank && ReferenceEquals(blank, ActivePane);

    /// <summary>The tab strip's ＋ can open a blank page (not while one is shown).</summary>
    public bool CanAddBlankPane => !IsBlankPane;

    /// <summary>The group on screen has <see cref="PaneRules.MaxPanes"/> panes: the toolbar's split buttons close a pane instead.</summary>
    public bool IsPaneLimitReached => _shownGroup is { } group && group.Layout.Panes.Count >= PaneRules.MaxPanes;

    /// <summary>The active pane can split now (the menus): something is on screen and the group has room (the blank page of ＋ does not split).</summary>
    public bool CanSplit => HasPanes && !IsPaneLimitReached && !IsBlankPane;

    /// <summary>The panes on screen that show a tab (empty panes have nothing to load or refresh); none while no tab is open.</summary>
    public IReadOnlyList<PaneViewModel> VisiblePanes =>
        _shownGroup is { } group ? group.Layout.Panes.Where(p => !IsEmptyPane(p)).ToList() : HasTab ? [ActivePane] : [];

    private bool IsVisible(PaneViewModel pane) => _shownGroup?.Layout.Contains(pane) ?? ((HasTab || IsBlankPane) && ReferenceEquals(pane, ActivePane));

    /// <summary><paramref name="pane"/> is an empty pane of a group (no tab in it).</summary>
    public bool IsEmptyPane(PaneViewModel pane) => _emptyTabs.ContainsKey(pane);

    /// <summary>The tab whose pane <paramref name="pane"/> is (a closed fixed tab too, an empty pane's stand-in).</summary>
    private TabLayoutViewModel? TabOf(PaneViewModel pane) =>
        Tabs.FirstOrDefault(t => ReferenceEquals(t.ActivePane, pane))
        ?? new[] { RecentTab, FrequentTab }.FirstOrDefault(t => ReferenceEquals(t.ActivePane, pane))
        ?? _emptyTabs.GetValueOrDefault(pane);

    /// <summary>The colour of the pane of <paramref name="pane"/> (0 to <see cref="PaneRules.MaxPanes"/> − 1; −1 outside a group).</summary>
    public int SlotOf(PaneViewModel pane) => TabOf(pane)?.PaneSlot ?? -1;

    private PaneGroup? GroupOf(PaneViewModel pane) => _groups.FirstOrDefault(g => g.Layout.Contains(pane));

    /// <summary>The tabs of <paramref name="group"/> (its empty panes left out), in reading order.</summary>
    private List<TabLayoutViewModel> TabsOf(PaneGroup group) =>
        group.Layout.Panes.Where(p => !IsEmptyPane(p)).Select(TabOf).OfType<TabLayoutViewModel>().ToList();

    private TabLayoutViewModel NewEmptyPane()
    {
        var tab = new TabLayoutViewModel(new PaneViewModel(_services, TabKind.None) { IsStale = false });
        _emptyTabs[tab.ActivePane] = tab;
        return tab;
    }

    /// <summary>
    /// Empty panes made for a layout that is still being built (a 登録ペイン being opened): no group
    /// holds them yet, and taking a reused tab out of its group must not forget them meanwhile.
    /// </summary>
    private readonly HashSet<PaneViewModel> _pendingEmptyPanes = new(ReferenceEqualityComparer.Instance);

    private void ForgetUnusedEmptyPanes()
    {
        foreach (var pane in _emptyTabs.Keys.Where(p => GroupOf(p) is null && !ReferenceEquals(p, ActivePane) && !_pendingEmptyPanes.Contains(p)).ToList())
        {
            _emptyTabs.Remove(pane);
        }
    }

    /// <summary>
    /// <paramref name="group"/> takes <paramref name="layout"/>; a layout of fewer than two panes, or of
    /// empty panes only, ends the group (its tabs stay on the strip). Puts the group's tabs together on
    /// the strip and gives them their colours. Does not announce <see cref="ShownLayout"/> and does not
    /// change <see cref="SelectedTab"/>: the caller does. False when the group ended.
    /// </summary>
    private bool SetGroupLayout(PaneGroup group, PaneLayout<PaneViewModel>? layout)
    {
        bool keeps = layout is { Panes.Count: > 1 } && layout.Panes.Any(p => !IsEmptyPane(p));
        if (keeps)
        {
            group.Layout = layout!;
            if (!group.Layout.Contains(group.LastActive))
            {
                group.LastActive = group.Layout.Panes.FirstOrDefault(p => !IsEmptyPane(p)) ?? group.Layout.Panes[0];
            }

            if (!_groups.Contains(group))
            {
                _groups.Add(group);
            }
        }
        else
        {
            _groups.Remove(group);
            if (ReferenceEquals(_lastShownGroup, group))
            {
                _lastShownGroup = null;
            }

            if (ReferenceEquals(_shownGroup, group))
            {
                _shownGroup = null; // SetShownGroup's notifications follow below
            }
        }

        ForgetUnusedEmptyPanes();
        ArrangeGroupTabs();
        OnGroupsChanged();
        return keeps;
    }

    /// <summary>The group on screen becomes <paramref name="group"/> (null: one tab alone). The caller sets <see cref="SelectedTab"/> to a pane of it.</summary>
    private void SetShownGroup(PaneGroup? group)
    {
        _shownGroup = group;
        if (group is not null)
        {
            _lastShownGroup = group;
        }

        OnGroupsChanged();
    }

    private void OnGroupsChanged()
    {
        UpdatePaneSlots();
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(HasPanes));
        OnPropertyChanged(nameof(IsPaneLimitReached));
        OnPropertyChanged(nameof(CanSplit));
        OnPropertyChanged(nameof(IsBlankPane));
        OnPropertyChanged(nameof(CanAddBlankPane));
        SplitPaneCommand.NotifyCanExecuteChanged();
        OnRegisteredPanesStateChanged();
    }

    /// <summary>
    /// The colour of each pane, the same on its tab (<see cref="TabLayoutViewModel.PaneSlot"/>), per
    /// group: a tab keeps its colour while it stays in its group, a tab new in it takes the lowest free
    /// one; −1 outside the groups. <see cref="TabLayoutViewModel.IsOnScreen"/> marks the group on screen.
    /// </summary>
    private void UpdatePaneSlots()
    {
        var grouped = new HashSet<TabLayoutViewModel>();
        foreach (var group in _groups)
        {
            var tabs = group.Layout.Panes.Select(TabOf).OfType<TabLayoutViewModel>().ToList();
            var used = new HashSet<int>();
            foreach (var tab in tabs)
            {
                if (tab.PaneSlot >= PaneRules.MaxPanes || !used.Add(tab.PaneSlot))
                {
                    tab.PaneSlot = -1;
                }
            }

            used.Remove(-1);
            foreach (var tab in tabs.Where(t => t.PaneSlot < 0))
            {
                int slot = 0;
                while (!used.Add(slot))
                {
                    slot++;
                }

                tab.PaneSlot = slot;
            }

            bool onScreen = ReferenceEquals(group, _shownGroup);
            foreach (var tab in tabs)
            {
                tab.IsOnScreen = onScreen;
            }

            grouped.UnionWith(tabs);
        }

        foreach (var tab in Tabs.Concat([RecentTab, FrequentTab]).Concat(_emptyTabs.Values).Where(t => !grouped.Contains(t)))
        {
            tab.PaneSlot = -1;
            tab.IsOnScreen = false;
        }
    }

    /// <summary>
    /// Each group's tabs together on the strip, where its leftmost tab is, in the reading order of its
    /// leaves (a tab dragged between them is put after them). Marks the ends of each group for the strip.
    /// </summary>
    private void ArrangeGroupTabs()
    {
        var order = new List<TabLayoutViewModel>(Tabs.Count);
        var placed = new HashSet<PaneGroup>();
        foreach (var tab in Tabs)
        {
            if (GroupOf(tab.ActivePane) is not { } group)
            {
                order.Add(tab);
            }
            else if (placed.Add(group))
            {
                order.AddRange(TabsOf(group).Where(Tabs.Contains));
            }
        }

        for (int i = 0; i < order.Count; i++)
        {
            int from = Tabs.IndexOf(order[i]);
            if (from != i)
            {
                Tabs.Move(from, i);
            }
        }

        for (int i = 0; i < Tabs.Count; i++)
        {
            var group = GroupOf(Tabs[i].ActivePane);
            Tabs[i].IsGroupStart = group is not null && (i == 0 || GroupOf(Tabs[i - 1].ActivePane) != group);
            Tabs[i].IsGroupEnd = group is not null && (i == Tabs.Count - 1 || GroupOf(Tabs[i + 1].ActivePane) != group);
        }
    }

    /// <summary>
    /// <paramref name="tab"/> is about to be the shown tab: a tab of a group brings its group on screen;
    /// while the group on screen has an empty pane active, any other tab fills that pane (and joins the
    /// group); otherwise the tab is shown alone. The placeholder shows nothing.
    /// </summary>
    private void PlaceInSplit(TabLayoutViewModel tab)
    {
        if (ReferenceEquals(tab, NoTab))
        {
            SetShownGroup(null);
            return;
        }

        var pane = tab.ActivePane;
        if (GroupOf(pane) is { } group)
        {
            if (!ReferenceEquals(group, _shownGroup))
            {
                SetShownGroup(group);
            }

            return;
        }

        if (_shownGroup is { } shown && IsEmptyPane(ActivePane) && shown.Layout.Contains(ActivePane) && !tab.IsPlaceholder)
        {
            SetGroupLayout(shown, shown.Layout.Replace(ActivePane, pane));
            return;
        }

        if (_shownGroup is not null)
        {
            SetShownGroup(null);
        }
    }

    // ---- splitting and closing ---------------------------------------------------------------------

    /// <summary>The toolbar's split buttons (Ctrl + \ / Ctrl + Shift + \): the active pane splits; at the pane limit they close the pane beside it instead.</summary>
    [RelayCommand(CanExecute = nameof(CanSplitPane), AllowConcurrentExecutions = true)]
    private Task SplitPane(SplitAxis axis)
    {
        if (IsPaneLimitReached)
        {
            ClosePaneAlong(axis);
            return Task.CompletedTask;
        }

        return SplitActivePaneAsync(axis);
    }

    private bool CanSplitPane(SplitAxis axis) => HasPanes && !IsBlankPane && (!IsPaneLimitReached || PaneAlong(axis) is not null);

    /// <summary>
    /// The pane the split button of <paramref name="axis"/> closes at the pane limit: the one right of
    /// the active pane (else left) for <see cref="SplitAxis.Columns"/>, below it (else above) for rows.
    /// </summary>
    private PaneViewModel? PaneAlong(SplitAxis axis)
    {
        if (_shownGroup is not { } group || !group.Layout.Contains(ActivePane))
        {
            return null;
        }

        var (first, second) = axis == SplitAxis.Columns ? (PaneDirection.Right, PaneDirection.Left) : (PaneDirection.Down, PaneDirection.Up);
        return group.Layout.Neighbor(ActivePane, first) ?? group.Layout.Neighbor(ActivePane, second);
    }

    /// <summary>At the pane limit: closes the pane beside the active one along <paramref name="axis"/> (<see cref="PaneAlong"/>); the active pane stays.</summary>
    private void ClosePaneAlong(SplitAxis axis)
    {
        if (PaneAlong(axis) is { } pane)
        {
            ClosePane(pane);
        }
        else
        {
            Message.Info(axis == SplitAxis.Columns ? "左右に閉じられるペインはありません。" : "上下に閉じられるペインはありません。");
        }
    }

    /// <summary>
    /// 「このペインを閉じる」 of an empty pane (a press in it made it the active pane first), Ctrl + W on it.
    /// The blank page of ＋ goes back to the tab shown before it.
    /// </summary>
    [RelayCommand]
    private void CloseActivePane()
    {
        if (IsBlankPane)
        {
            CloseBlankPane();
            return;
        }

        ClosePane(ActivePane);
    }

    /// <summary>
    /// The tab strip's ＋: a blank page, one empty pane alone (the group on screen, if any, leaves the
    /// screen and stays on the strip). The next tab or folder picked is shown alone in its place, as a new
    /// browser tab is filled; the toolbar's split buttons split no blank page.
    /// </summary>
    [RelayCommand]
    private void AddEmptyPane()
    {
        if (IsBlankPane)
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        foreach (var shown in VisiblePanes)
        {
            shown.SavedScrollOffset = View?.GetScrollOffset(shown) ?? 0; // they come back where they were
        }

        _beforeBlank = Tabs.Contains(SelectedTab) ? SelectedTab : _shownGroup is { } group ? TabsOf(group).FirstOrDefault() : null;
        LeaveSelectedTab();
        var blank = NewEmptyPane();
        _blankPane = blank.ActivePane;
        _shownGroup = null; // SetShownGroup's notifications come with the new shown tab
        SelectedTab = blank; // announces the layout (the blank pane alone), then the active pane
        OnGroupsChanged();
        SaveLocations();
    }

    /// <summary>
    /// Where the blank page goes back to (closed, or saved as selected_tab): the tab shown before it while
    /// it is still open, else the last tab; null without tabs.
    /// </summary>
    private TabLayoutViewModel? BlankBackTab => _beforeBlank is { } before && Tabs.Contains(before) ? before : Tabs.LastOrDefault();

    /// <summary>The blank page closes: the tab shown before it comes back (else the last tab, else no tab).</summary>
    private void CloseBlankPane()
    {
        var back = BlankBackTab;
        _beforeBlank = null;
        if (back is not null)
        {
            _ = SelectTabCommand.ExecuteAsync(back);
            return;
        }

        SelectedTab = NoTab;
        OnGroupsChanged();
        SaveLocations();
    }

    /// <summary>
    /// Splits the active pane along <paramref name="axis"/>: an empty pane takes the half right of
    /// (<see cref="SplitAxis.Columns"/>) or below (<see cref="SplitAxis.Rows"/>) it and becomes the active
    /// pane; the next tab picked fills it. A tab shown alone starts a new group with it. Any pane can
    /// split, whatever it shows and however small it is (the panes shrink together); refused only at
    /// <see cref="PaneRules.MaxPanes"/> panes.
    /// </summary>
    public Task SplitActivePaneAsync(SplitAxis axis)
    {
        if (!HasPanes || IsBlankPane)
        {
            return Task.CompletedTask;
        }

        if (IsPaneLimitReached)
        {
            Message.Info($"ペインは {PaneRules.MaxPanes} 個まで並べられます。");
            return Task.CompletedTask;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        var source = ActivePane;
        var empty = NewEmptyPane();
        LeaveSelectedTab(); // the source stays on screen; its position is kept all the same
        if (_shownGroup is { } group)
        {
            SetGroupLayout(group, group.Layout.Split(source, axis, empty.ActivePane));
        }
        else
        {
            var created = new PaneGroup(PaneLayout<PaneViewModel>.Single(source).Split(source, axis, empty.ActivePane));
            SetGroupLayout(created, created.Layout);
            SetShownGroup(created);
        }

        SelectedTab = empty; // announces the layout, then the new active pane
        SaveLocations();
        return Task.CompletedTask;
    }

    /// <summary>The tab strip's menu can split <paramref name="tab"/>'s pane: its group (if any) has room.</summary>
    public bool CanSplitTab(TabLayoutViewModel tab)
    {
        if (!Tabs.Contains(tab))
        {
            return false;
        }

        // the group it is in, or the one it would fill when it is shown (an empty pane is active)
        var group = GroupOf(tab.ActivePane)
            ?? (_shownGroup is { } shown && IsEmptyPane(ActivePane) && shown.Layout.Contains(ActivePane) ? shown : null);
        return group is null || group.Layout.Panes.Count < PaneRules.MaxPanes;
    }

    /// <summary>The tab strip's menu: <paramref name="tab"/> is shown first, then its pane splits.</summary>
    public async Task SplitTabAsync(TabLayoutViewModel tab, SplitAxis axis)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

        if (!CanSplitTab(tab))
        {
            Message.Info($"ペインは {PaneRules.MaxPanes} 個まで並べられます。");
            return;
        }

        if (!ReferenceEquals(tab, SelectedTab))
        {
            await SelectTabCommand.ExecuteAsync(tab);
            if (!ReferenceEquals(tab, SelectedTab) || !Tabs.Contains(tab))
            {
                return; // the user went to another tab (or closed this one) while it loaded
            }
        }

        await SplitActivePaneAsync(axis);
    }

    /// <summary>
    /// Closes <paramref name="pane"/> of the group on screen (「ペインを閉じる」, the split buttons at the
    /// limit): the rest of the group takes its space, and when it was the active pane the nearest pane
    /// becomes active (<see cref="PaneLayout{T}.Close"/>). Its tab stays on the strip, outside the group,
    /// unless the setting closes the tab with it (<see cref="AppSettings.PaneCloseClosesTab"/>). A group
    /// left with one pane, or with empty panes only, ends: the tab that remains is shown alone.
    /// </summary>
    public void ClosePane(PaneViewModel pane)
    {
        if (_shownGroup is not { } group || !group.Layout.Contains(pane))
        {
            return;
        }

        if (!IsEmptyPane(pane) && Settings.PaneCloseClosesTab && TabOf(pane) is { } owner && Tabs.Contains(owner))
        {
            _ = CloseTabCommand.ExecuteAsync(owner); // its pane goes with it
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        foreach (var shown in VisiblePanes)
        {
            shown.SavedScrollOffset = View?.GetScrollOffset(shown) ?? 0; // the panes that stay, and the tab that leaves
        }

        bool wasActive = ReferenceEquals(pane, ActivePane);
        var rest = group.Layout.Close(pane, out var next);
        bool keeps = SetGroupLayout(group, rest);
        TabLayoutViewModel? show = keeps
            ? (wasActive && next is not null ? TabOf(next) : null)
            : (IsEmptyPane(ActivePane) || wasActive ? rest?.Panes.Where(p => !IsEmptyPane(p)).Select(TabOf).FirstOrDefault() ?? TabOf(pane) : null);
        ActivateOnScreen(show);
        SaveLocations();
    }

    /// <summary>
    /// After the group on screen changed: <paramref name="tab"/> (a pane that stayed on screen, or the
    /// placeholder of an empty one) becomes the shown tab without being listed again; null keeps the
    /// shown tab. Either way the layout is announced, and the shown group follows the shown tab.
    /// </summary>
    private void ActivateOnScreen(TabLayoutViewModel? tab)
    {
        var target = tab ?? SelectedTab;
        var group = GroupOf(target.ActivePane);
        if (!ReferenceEquals(group, _shownGroup))
        {
            SetShownGroup(group);
        }

        if (ReferenceEquals(target, SelectedTab))
        {
            AnnounceLayout();
            return;
        }

        _activatingShownPane = true;
        try
        {
            SelectedTab = target; // announces the layout, then the active pane
        }
        finally
        {
            _activatingShownPane = false;
        }
    }

    /// <summary>Every group ends (「ほかのタブを閉じる」); the tabs stay on the strip.</summary>
    private void EndAllGroups()
    {
        foreach (var group in _groups.ToList())
        {
            SetGroupLayout(group, null);
        }
    }

    /// <summary>
    /// The tab leaves its group (it was closed, or it leaves the strip): its pane closes. True when it
    /// was in a group; <paramref name="next"/> is then the pane nearest to it that stays, null when the group ended.
    /// </summary>
    private bool TakeOutOfGroup(PaneViewModel pane, bool keepEmpty, out PaneViewModel? next)
    {
        next = null;
        if (GroupOf(pane) is not { } group)
        {
            return false;
        }

        PaneLayout<PaneViewModel>? layout;
        if (keepEmpty)
        {
            next = NewEmptyPane().ActivePane; // the pane stays where it was, empty
            layout = group.Layout.Replace(pane, next);
        }
        else
        {
            layout = group.Layout.Close(pane, out next);
        }

        if (!SetGroupLayout(group, layout))
        {
            next = null;
        }

        return true;
    }

    // ---- putting tabs in panes -----------------------------------------------------------------------

    /// <summary>
    /// The pane 「選択中のペインに入れる」 puts a tab in: the active pane of the group on screen, else
    /// the pane that was active in the group shown last. Null without any group.
    /// </summary>
    private PaneViewModel? PutTarget =>
        _shownGroup is not null ? ActivePane : _lastShownGroup is { } last && _groups.Contains(last) ? last.LastActive : null;

    /// <summary>「選択中のペインに入れる」 can put <paramref name="tab"/> in <see cref="PutTarget"/> (a group exists and the tab is not that pane's).</summary>
    public bool CanPutInActivePane(TabLayoutViewModel tab) =>
        Tabs.Contains(tab) && PutTarget is { } target && !ReferenceEquals(target, tab.ActivePane);

    /// <summary>The tab strip's menu 「選択中のペインに入れる」.</summary>
    public Task PutInActivePaneAsync(TabLayoutViewModel tab) =>
        PutTarget is { } target ? PutInPaneAsync(tab, target) : Task.CompletedTask;

    /// <summary>
    /// <paramref name="tab"/> takes the place of <paramref name="target"/> (a pane of a group: a tab
    /// dropped on a pane, 「選択中のペインに入れる」), and that group is shown with it active. The tab that
    /// was there leaves the group (it stays on the strip); an empty pane is filled. A tab of the same
    /// group swaps places with it; a tab of another group leaves that one (as when it is dragged out of it).
    /// </summary>
    public async Task PutInPaneAsync(TabLayoutViewModel tab, PaneViewModel target)
    {
        var pane = tab.ActivePane;
        if (Tabs.Contains(tab) && IsBlankPane && ReferenceEquals(target, ActivePane))
        {
            await SelectTabCommand.ExecuteAsync(tab); // the blank page of ＋: the tab is shown in its place
            return;
        }

        if (!Tabs.Contains(tab) || ReferenceEquals(pane, target) || GroupOf(target) is not { } group)
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        foreach (var shown in VisiblePanes)
        {
            shown.SavedScrollOffset = View?.GetScrollOffset(shown) ?? 0;
        }

        if (ReferenceEquals(GroupOf(pane), group))
        {
            SetGroupLayout(group, group.Layout.Map(p => ReferenceEquals(p, pane) ? target : ReferenceEquals(p, target) ? pane : p));
        }
        else
        {
            TakeOutOfGroup(pane, keepEmpty: false, out _);
            if (!_groups.Contains(group) || !group.Layout.Contains(target))
            {
                return; // cannot happen: the two groups are different
            }

            SetGroupLayout(group, group.Layout.Replace(target, pane));
        }

        bool switched = !ReferenceEquals(tab, SelectedTab);
        if (switched)
        {
            LeaveSelectedTab();
        }

        group.LastActive = pane; // also when it was the shown tab already (OnSelectedTabChanged does not run)
        SetShownGroup(group);
        if (switched)
        {
            SelectedTab = tab; // announces the layout, then the active pane
        }
        else
        {
            AnnounceLayout();
        }

        SaveLocations();
        if (pane.IsStale)
        {
            await LoadTabAsync(pane);
        }

        if (IsVisible(pane))
        {
            View?.ScrollTo(pane, pane.SavedScrollOffset);
        }
    }

    /// <summary>
    /// A tab dragged to the top of the panes was dropped on zone <paramref name="zone"/> of the layout
    /// <paramref name="preset"/> (<see cref="PanePresets"/>, as Windows 11's snap layouts): the group on
    /// screen takes that layout (a tab shown alone starts one), with the tab in that zone and the panes
    /// that showed a tab in the other zones in reading order; zones left over are empty panes, panes
    /// left over leave the group (their tabs stay on the strip). A tab of another group leaves that one.
    /// </summary>
    public async Task PlaceInPresetAsync(TabLayoutViewModel tab, int preset, int zone)
    {
        if (!Tabs.Contains(tab) || preset < 0 || preset >= PanePresets.Count || zone < 0 || zone >= PanePresets.ZoneCount(preset))
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        foreach (var shown in VisiblePanes)
        {
            shown.SavedScrollOffset = View?.GetScrollOffset(shown) ?? 0;
        }

        var pane = tab.ActivePane;
        var group = _shownGroup;
        var others = VisiblePanes.Where(p => !ReferenceEquals(p, pane)).ToList();
        if (GroupOf(pane) is { } own && !ReferenceEquals(own, group))
        {
            TakeOutOfGroup(pane, keepEmpty: false, out _);
            if (group is not null && !_groups.Contains(group))
            {
                group = null; // cannot happen: the two groups are different
            }
        }

        var leaves = new List<PaneViewModel>(PanePresets.ZoneCount(preset));
        int next = 0;
        for (int i = 0; i < PanePresets.ZoneCount(preset); i++)
        {
            leaves.Add(i == zone ? pane : next < others.Count ? others[next++] : NewEmptyPane().ActivePane);
        }

        var layout = PanePresets.Build(preset, leaves);
        group ??= new PaneGroup(layout);
        SetGroupLayout(group, layout); // the empty panes made above are in it before anything is forgotten
        bool switched = !ReferenceEquals(tab, SelectedTab);
        if (switched)
        {
            LeaveSelectedTab();
        }

        group.LastActive = pane;
        SetShownGroup(group);
        if (switched)
        {
            SelectedTab = tab; // announces the layout, then the active pane
        }
        else
        {
            AnnounceLayout();
        }

        SaveLocations();
        if (pane.IsStale)
        {
            await LoadTabAsync(pane);
        }

        foreach (var shown in VisiblePanes)
        {
            View?.ScrollTo(shown, shown.SavedScrollOffset); // each view may now show another pane
        }
    }

    /// <summary>
    /// A folder dropped on an empty pane (a folder dragged from another pane, a 登録フォルダ, Explorer):
    /// it opens there in a new folder tab, also when another tab shows it (the drop says where).
    /// </summary>
    public async Task OpenInEmptyPaneAsync(PaneViewModel pane, string folder)
    {
        if (!IsEmptyPane(pane) || !IsVisible(pane))
        {
            return;
        }

        ActivatePane(pane);
        if (!ReferenceEquals(ActivePane, pane))
        {
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        var tab = AddFolderTab(new NavLocation(TabKind.Folder, PathTools.NormalizeOrSelf(folder), string.Empty));
        InheritViewState(tab.ActivePane);
        await ShowTabAsync(tab); // fills the active empty pane (PlaceInSplit)
        SaveLocations();
    }

    // ---- moving a whole group on the strip ----------------------------------------------------------

    /// <summary>
    /// The strip in blocks: each pane group's tabs (they stand together) as one block, every other
    /// tab as a block of its own; in strip order. What a group dragged by its bar moves past.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TabLayoutViewModel>> StripBlocks()
    {
        var blocks = new List<List<TabLayoutViewModel>>();
        PaneGroup? current = null;
        foreach (var tab in Tabs)
        {
            var group = GroupOf(tab.ActivePane);
            if (group is not null && ReferenceEquals(group, current))
            {
                blocks[^1].Add(tab);
            }
            else
            {
                blocks.Add([tab]);
                current = group;
            }
        }

        return blocks;
    }

    /// <summary>
    /// The group of <paramref name="tab"/> (dragged by its bar) moves past the block next to it on the
    /// right (<paramref name="right"/>) or left: a tab, or another group as a whole. False at the end of
    /// the strip or outside a group. The panes do not change. Saved when the drag ends (<see cref="CommitGroupMove"/>).
    /// </summary>
    public bool MoveGroupBlock(TabLayoutViewModel tab, bool right)
    {
        if (GroupOf(tab.ActivePane) is null)
        {
            return false;
        }

        var blocks = StripBlocks().ToList();
        int at = blocks.FindIndex(b => b.Contains(tab));
        int to = right ? at + 1 : at - 1;
        if (at < 0 || to < 0 || to >= blocks.Count)
        {
            return false;
        }

        (blocks[at], blocks[to]) = (blocks[to], blocks[at]);
        var order = blocks.SelectMany(b => b).ToList();
        for (int i = 0; i < order.Count; i++)
        {
            int from = Tabs.IndexOf(order[i]);
            if (from != i)
            {
                Tabs.Move(from, i);
            }
        }

        ArrangeGroupTabs(); // the ends of each group again
        return true;
    }

    /// <summary>A group dragged by its bar was released: the strip's order is saved.</summary>
    public void CommitGroupMove()
    {
        ArrangeGroupTabs();
        SaveLocations();
    }

    /// <summary>
    /// A drag of <paramref name="tab"/> on the strip ended (the moves themselves only reorder the
    /// strip). A tab of a group that is still next to the others of its group reorders the panes to the
    /// strip's order; one dragged away from them leaves the group: its pane closes and the tab is shown
    /// alone (the rest of the group stays on the strip). A tab dragged in between a group's tabs is
    /// put after them (it joins a group only by a drop on a pane or the menu).
    /// </summary>
    public void CommitTabMove(TabLayoutViewModel tab)
    {
        if (!Tabs.Contains(tab))
        {
            return;
        }

        bool layoutChanged = false;
        bool shownAlone = false;
        bool wasVisible = IsVisible(tab.ActivePane);
        if (GroupOf(tab.ActivePane) is { } group)
        {
            var members = TabsOf(group);
            // Where the others are on the strip without the dragged tab, and where it went among them:
            // inside their run or at either end of it, it stays (and the panes follow the strip).
            var rest = Tabs.Where(t => !ReferenceEquals(t, tab)).ToList();
            var others = members.Where(t => !ReferenceEquals(t, tab)).Select(t => rest.IndexOf(t)).Order().ToList();
            int at = Tabs.IndexOf(tab);
            bool away = others.Count > 0 && (others[^1] - others[0] != others.Count - 1 || at < others[0] || at > others[^1] + 1);
            if (away)
            {
                foreach (var shown in VisiblePanes)
                {
                    shown.SavedScrollOffset = View?.GetScrollOffset(shown) ?? 0;
                }

                TakeOutOfGroup(tab.ActivePane, keepEmpty: false, out _);
                layoutChanged = true;
                shownAlone = true;
            }
            else
            {
                var byStrip = members.OrderBy(t => Tabs.IndexOf(t)).Select(t => t.ActivePane).ToList();
                if (!byStrip.SequenceEqual(members.Select(t => t.ActivePane)))
                {
                    var queue = new Queue<PaneViewModel>(byStrip);
                    SetGroupLayout(group, group.Layout.Map(p => IsEmptyPane(p) ? p : queue.Dequeue()));
                    layoutChanged = true;
                }
            }
        }

        ArrangeGroupTabs();
        if (layoutChanged && shownAlone && !wasVisible)
        {
            // a tab of a group that was not on screen: shown alone through SelectTab, which loads a
            // stale pane and brings back its scroll position (ActivateOnScreen only rearranges the screen)
            ActivateOnScreen(null);
            _ = SelectTabCommand.ExecuteAsync(tab);
        }
        else if (layoutChanged)
        {
            ActivateOnScreen(shownAlone ? tab : null); // a tab dragged out of its group (on screen) is shown alone
        }

        SaveLocations();
    }

    // ---- moving between panes ------------------------------------------------------------------------

    /// <summary>
    /// A press in <paramref name="pane"/> (or a click in a cascade opened from it): its tab becomes the
    /// shown tab, which is what an active pane is (an empty pane's stand-in for an empty pane). Nothing is
    /// loaded or scrolled: it is on screen.
    /// </summary>
    /// <param name="closeCardCascade">
    /// False from the cascade itself (a right click on its row opens a menu over it): the cascade
    /// belongs to the pane being activated and must stay.
    /// </param>
    public void ActivatePane(PaneViewModel pane, bool closeCardCascade = true)
    {
        if (ReferenceEquals(pane, ActivePane) || !IsVisible(pane) || TabOf(pane) is not { } tab)
        {
            return;
        }

        if (closeCardCascade)
        {
            View?.CloseCardCascade(); // the cards' lists hang on the pane that was active
        }

        _activatingShownPane = true;
        try
        {
            LeaveSelectedTab();
            SelectedTab = tab;
        }
        finally
        {
            _activatingShownPane = false;
        }

        SaveLocations();
    }

    /// <summary>F6 / Shift + F6: the next / previous pane of the group on screen (reading order, wrapping). False without a group.</summary>
    public bool SelectAdjacentPane(bool back)
    {
        if (_shownGroup is not { } group)
        {
            return false;
        }

        ActivatePane(group.Layout.Next(ActivePane, back));
        return true;
    }

    /// <summary>Alt + Shift + an arrow: the pane next to the active one that way (none at the edge). False without a group.</summary>
    public bool MoveToPane(PaneDirection direction)
    {
        if (_shownGroup is not { } group)
        {
            return false;
        }

        if (group.Layout.Neighbor(ActivePane, direction) is { } next)
        {
            ActivatePane(next);
        }

        return true;
    }

    /// <summary>A splitter was dragged: <paramref name="split"/> (a node of <see cref="ShownLayout"/>) keeps <paramref name="ratio"/>.</summary>
    public void SetSplitRatio(PaneSplit<PaneViewModel> split, double ratio)
    {
        if (_shownGroup is not { } group)
        {
            return;
        }

        PaneLayout<PaneViewModel> changed;
        try
        {
            changed = group.Layout.WithRatio(split, ratio);
        }
        catch (ArgumentException)
        {
            return; // the layout changed meanwhile
        }

        SetGroupLayout(group, changed);
        AnnounceLayout();
        SaveLocations();
    }

    private void AnnounceLayout() => OnPropertyChanged(nameof(ShownLayout));

    // ---- settings.json: pane_groups / shown_group ------------------------------------------------------

    /// <summary>
    /// The groups as settings.json keeps them (leaves are positions in <paramref name="order"/>, the
    /// open tabs as written, or empty), and the position of the group on screen (−1: none).
    /// </summary>
    private (List<PaneGroupSetting> Groups, int Shown) GroupSettings(IReadOnlyList<PaneViewModel> order)
    {
        var groups = new List<PaneGroupSetting>(_groups.Count);
        int shown = -1;
        foreach (var group in _groups)
        {
            var layout = PaneLayoutSettings.ToSetting(group.Layout, p =>
            {
                int index = IndexIn(order, p);
                return index < 0 ? PaneNodeSetting.EmptyLeaf() : new PaneNodeSetting { Pane = index };
            });
            if (ReferenceEquals(group, _shownGroup))
            {
                shown = groups.Count;
            }

            var active = ReferenceEquals(group, _shownGroup) ? ActivePane : group.LastActive;
            groups.Add(new PaneGroupSetting { Layout = layout, Active = Math.Max(0, IndexIn(group.Layout.Panes, active)), RegisteredPane = group.RegisteredPaneId });
        }

        return (groups, shown);
    }

    private static int IndexIn(IReadOnlyList<PaneViewModel> list, PaneViewModel pane)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], pane))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Brings back the groups of settings.json at startup (<paramref name="restoredOf"/>: the pane of
    /// each open_tabs entry, null when it did not come back: that leaf is empty). Returns the tab to show
    /// for <c>shown_group</c> (its active pane's tab, or an empty pane's stand-in); null when none.
    /// </summary>
    private TabLayoutViewModel? RestoreGroups(AppSettings settings, IReadOnlyList<PaneViewModel?> restoredOf)
    {
        TabLayoutViewModel? show = null;
        var saved = settings.PaneGroups ?? [];
        for (int i = 0; i < saved.Count; i++)
        {
            var entry = saved[i];
            var layout = PaneLayoutSettings.FromSetting<PaneViewModel>(entry.Layout, leaf =>
                leaf.Pane is int index && index >= 0 && index < restoredOf.Count && restoredOf[index] is { } pane && GroupOf(pane) is null
                    ? pane
                    : NewEmptyPane().ActivePane);
            if (layout is null)
            {
                continue;
            }

            var group = new PaneGroup(layout) { RegisteredPaneId = entry.RegisteredPane };
            if (entry.Active >= 0 && entry.Active < layout.Panes.Count)
            {
                group.LastActive = layout.Panes[entry.Active];
            }

            if (SetGroupLayout(group, layout) && i == settings.ShownGroup)
            {
                show = TabOf(group.LastActive);
            }
        }

        ForgetUnusedEmptyPanes();
        return show;
    }
}
