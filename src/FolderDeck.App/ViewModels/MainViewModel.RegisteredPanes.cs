using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Paths;
using FolderDeck.Core.Stores;

namespace FolderDeck.ViewModels;

// ---- 登録ペイン: saved pane layouts (registered_panes.json) --------------------------------------------
//
// A registered pane is a group's layout with what each pane showed (its location, view and sort mode;
// empty panes stay empty). Opening one builds a new group from it (an open tab of the same folder is
// used when the setting says so; fixed tabs always are). The group remembers where it came from
// (PaneGroup.RegisteredPaneId): 上書き writes the group back there, and with 自動記録 every save does.

public sealed partial class MainViewModel
{
    /// <summary>The rows of the sidebar section 登録ペイン, in the order of registered_panes.json.</summary>
    public ObservableCollection<RegisteredPaneItemViewModel> RegisteredPanes { get; } = [];

    /// <summary>Sidebar section 登録ペイン: the rows of <see cref="RegisteredPanes"/>.</summary>
    public SidebarSectionViewModel PanesSection { get; }

    public bool HasNoRegisteredPanes => RegisteredPanes.Count == 0;

    public string RegisteredPanesCountText => RegisteredPanes.Count.ToString("N0");

    private RegisteredPanesStore PanesStore => _services.Stores.Panes;

    /// <summary>A pane group is on screen: it can be registered (the settings' ペイン tab, the section's button).</summary>
    public bool CanRegisterPanes => _shownGroup is not null;

    /// <summary>The 登録ペイン the group on screen came from (what 「現在のペインを上書きする」 overwrites); null for none.</summary>
    public string? CurrentRegisteredPaneName =>
        _shownGroup?.RegisteredPaneId is { } id ? PanesStore.Find(id)?.Name : null;

    /// <summary>
    /// The rows follow the store (same order; the rows that stay keep their instances, as the 登録フォルダ
    /// rows do), and a group whose entry is gone forgets it.
    /// </summary>
    private void RefreshRegisteredPanes()
    {
        var panes = PanesStore.Panes;
        var ids = panes.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        for (int i = RegisteredPanes.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(RegisteredPanes[i].Id))
            {
                RegisteredPanes.RemoveAt(i);
            }
        }

        for (int i = 0; i < panes.Count; i++)
        {
            var entry = panes[i];
            int at = IndexOfPaneRow(entry.Id);
            RegisteredPaneItemViewModel row;
            if (at < 0)
            {
                row = new RegisteredPaneItemViewModel(entry.Id);
                RegisteredPanes.Insert(i, row);
            }
            else
            {
                row = RegisteredPanes[at];
                if (at != i)
                {
                    RegisteredPanes.Move(at, i);
                }
            }

            row.Name = entry.Name;
            row.ToolTipText = Describe(entry);
        }

        foreach (var group in _groups.Where(g => g.RegisteredPaneId is { } id && !ids.Contains(id)))
        {
            group.RegisteredPaneId = null;
        }

        OnPropertyChanged(nameof(HasNoRegisteredPanes));
        OnPropertyChanged(nameof(RegisteredPanesCountText));
        OnRegisteredPanesStateChanged();
    }

    private int IndexOfPaneRow(string id)
    {
        for (int i = 0; i < RegisteredPanes.Count; i++)
        {
            if (RegisteredPanes[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The tooltip of a row: its name, then one line per pane (reading order).</summary>
    private static string Describe(RegisteredPane entry)
    {
        var lines = new List<string> { entry.Name };
        void Walk(PaneNodeSetting node)
        {
            if (node.First is { } first && node.Second is { } second)
            {
                Walk(first);
                Walk(second);
                return;
            }

            lines.Add(node.Tab is { } tab
                ? TabKinds.FromSetting(tab.Kind) switch
                {
                    TabKind.Recent => "・最近の履歴" + (string.IsNullOrEmpty(tab.Root) ? string.Empty : $"（{PathTools.Combine(tab.Root, tab.Path ?? string.Empty)}）"),
                    TabKind.Frequent => "・よく使う項目" + (string.IsNullOrEmpty(tab.Root) ? string.Empty : $"（{PathTools.Combine(tab.Root, tab.Path ?? string.Empty)}）"),
                    _ => "・" + PathTools.Combine(tab.Root ?? string.Empty, tab.Path ?? string.Empty),
                }
                : "・（空のペイン）");
        }

        Walk(entry.Layout);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The rows' highlight, and what the settings and the menus offer, follow the group on screen.</summary>
    private void OnRegisteredPanesStateChanged()
    {
        string? current = _shownGroup?.RegisteredPaneId;
        foreach (var row in RegisteredPanes)
        {
            row.IsCurrent = current is not null && row.Id == current;
        }

        OnPropertyChanged(nameof(CanRegisterPanes));
        OnPropertyChanged(nameof(CurrentRegisteredPaneName));
        RegisterPanesFromSidebarCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The group as registered_panes.json keeps it: each pane's tab as an open_tabs entry, empty panes empty.</summary>
    private PaneNodeSetting SavedLayout(PaneGroup group) =>
        PaneLayoutSettings.ToSetting(group.Layout, p =>
            IsEmptyPane(p) || TabOf(p) is not { } tab ? PaneNodeSetting.EmptyLeaf() : new PaneNodeSetting { Tab = TabSetting(tab) });

    private int SavedActive(PaneGroup group) =>
        Math.Max(0, IndexIn(group.Layout.Panes, ReferenceEquals(group, _shownGroup) ? ActivePane : group.LastActive));

    /// <summary>
    /// 自動記録 (<see cref="AppSettings.PaneAutoRecord"/>): every group opened from (or registered as) a
    /// 登録ペイン writes itself back there; the store writes only what changed. Called by <see cref="SaveState"/>.
    /// </summary>
    private void RecordRegisteredPanes()
    {
        if (!_tabsRestored || !Settings.PaneAutoRecord)
        {
            return;
        }

        bool changed = false;
        foreach (var group in _groups)
        {
            if (group.RegisteredPaneId is { } id)
            {
                changed |= PanesStore.Overwrite(id, SavedLayout(group), SavedActive(group));
            }
        }

        if (changed)
        {
            RefreshRegisteredPanes();
        }
    }

    /// <summary>
    /// 「現在のペインを登録する」 (the settings' ペイン tab): the group on screen becomes a new 登録ペイン
    /// named <paramref name="name"/> and remembers it. The text for the settings' status line.
    /// </summary>
    public string? RegisterCurrentPanes(string name)
    {
        name = name.Trim();
        if (_shownGroup is not { } group)
        {
            Message.Info("ペインを分割しているときに登録できます。");
            return null;
        }

        if (name.Length == 0)
        {
            Message.Info("登録ペインの名前を入力してください。");
            return null;
        }

        group.RegisteredPaneId = PanesStore.Add(name, SavedLayout(group), SavedActive(group));
        RefreshRegisteredPanes();
        string text = $"現在のペインを「{name}」として登録しました。";
        Message.Success(text);
        return text;
    }

    /// <summary>The sidebar section's button: asks for a name, then <see cref="RegisterCurrentPanes"/>.</summary>
    [RelayCommand(CanExecute = nameof(CanRegisterPanes))]
    private void RegisterPanesFromSidebar()
    {
        if (_shownGroup is not { } group)
        {
            return;
        }

        string initial = string.Join(" | ", TabsOf(group).Select(t => t.Title));
        if (View?.AskName("現在のペインを登録", "登録ペインの名前", initial) is { Length: > 0 } name)
        {
            RegisterCurrentPanes(name);
        }
    }

    /// <summary>
    /// 「現在のペインを上書きする」 (the settings' ペイン tab, while 自動記録 is off): the group on screen is
    /// written over the 登録ペイン it came from. The text for the settings' status line.
    /// </summary>
    public string? OverwriteCurrentRegisteredPane()
    {
        if (_shownGroup is not { RegisteredPaneId: { } id } group || PanesStore.Find(id) is not { } entry)
        {
            Message.Info("上書きできる登録ペインがありません（登録ペインから開いたペインで使えます）。");
            return null;
        }

        PanesStore.Overwrite(id, SavedLayout(group), SavedActive(group));
        RefreshRegisteredPanes();
        string text = $"登録ペイン「{entry.Name}」を現在のペインで上書きしました。";
        Message.Success(text);
        return text;
    }

    /// <summary>The row's menu 「現在のペインで上書き」: a group is on screen and 自動記録 is off.</summary>
    public bool CanOverwriteRegisteredPane(RegisteredPaneItemViewModel row) => _shownGroup is not null && !Settings.PaneAutoRecord;

    /// <summary>
    /// The row's menu 「現在のペインで上書き」: <paramref name="row"/>'s entry takes the group on screen,
    /// which now belongs to it (asked first when the group came from another entry, or from none).
    /// </summary>
    public void OverwriteRegisteredPane(RegisteredPaneItemViewModel row)
    {
        if (_shownGroup is not { } group || !CanOverwriteRegisteredPane(row) || PanesStore.Find(row.Id) is not { } entry)
        {
            return;
        }

        if (group.RegisteredPaneId != row.Id
            && View?.Confirm("登録ペインを上書きしますか？", $"「{entry.Name}」の内容を、現在のペインの並びとフォルダに置き換えます。", null, "上書き", danger: false) != true)
        {
            return;
        }

        PanesStore.Overwrite(row.Id, SavedLayout(group), SavedActive(group));
        foreach (var other in _groups.Where(g => g.RegisteredPaneId == row.Id))
        {
            other.RegisteredPaneId = null; // one group per entry
        }

        group.RegisteredPaneId = row.Id;
        RefreshRegisteredPanes();
        Message.Success($"登録ペイン「{entry.Name}」を現在のペインで上書きしました。");
    }

    /// <summary>The row's menu 「名前の変更」.</summary>
    public void RenameRegisteredPane(RegisteredPaneItemViewModel row)
    {
        if (PanesStore.Find(row.Id) is not { } entry
            || View?.AskName("登録ペインの名前を変更", "新しい名前", entry.Name) is not { Length: > 0 } name
            || name == entry.Name)
        {
            return;
        }

        PanesStore.Rename(row.Id, name);
        RefreshRegisteredPanes();
    }

    /// <summary>The row's menu 「削除」 (asked first). The tabs and the group on screen stay.</summary>
    public void DeleteRegisteredPane(RegisteredPaneItemViewModel row)
    {
        if (PanesStore.Find(row.Id) is not { } entry
            || View?.Confirm("登録ペインを削除しますか？", $"「{entry.Name}」を登録ペインから削除します。開いているタブとペインはそのまま残ります。", null, "削除", danger: true) != true)
        {
            return;
        }

        PanesStore.Remove(row.Id);
        RefreshRegisteredPanes(); // the groups that came from it forget it
        Message.Success($"登録ペイン「{entry.Name}」を削除しました。");
    }

    /// <summary>
    /// A click on a row: the 登録ペイン's group comes on screen. Already open (a group remembers it):
    /// that group is shown. Otherwise a new group is built from it: each pane's folder in an open tab
    /// of that folder when <see cref="AppSettings.PaneReuseOpenTabs"/> is on (taken out of its group
    /// if it is in one; 最近の履歴 / よく使う項目 always are their one tab), else in a new tab; a folder
    /// that cannot be found is listed with its error, as a restored tab is.
    /// </summary>
    public async Task OpenRegisteredPaneAsync(RegisteredPaneItemViewModel row)
    {
        if (PanesStore.Find(row.Id) is not { } entry)
        {
            RefreshRegisteredPanes();
            return;
        }

        View?.CloseCascade();
        Message.ClearPersistent();
        if (_groups.FirstOrDefault(g => g.RegisteredPaneId == entry.Id) is { } open)
        {
            await ShowGroupAsync(open, open.LastActive);
            return;
        }

        foreach (var shown in VisiblePanes)
        {
            shown.SavedScrollOffset = View?.GetScrollOffset(shown) ?? 0;
        }

        bool reuse = Settings.PaneReuseOpenTabs;
        var used = new HashSet<TabLayoutViewModel>();
        PaneGroup group;
        try
        {
            // A reused tab leaves its group on the way (SetGroupLayout): the empty panes made so far are kept for this layout.
            var layout = PaneLayoutSettings.FromSetting<PaneViewModel>(entry.Layout, leaf =>
            {
                if (leaf.Tab is { } saved && TabFor(saved, reuse, used) is { } tab)
                {
                    return tab.ActivePane;
                }

                var empty = NewEmptyPane().ActivePane;
                _pendingEmptyPanes.Add(empty);
                return empty;
            });
            if (layout is null)
            {
                _pendingEmptyPanes.Clear();
                ForgetUnusedEmptyPanes();
                Message.Error($"登録ペイン「{entry.Name}」を開けませんでした（内容が壊れています）。");
                return;
            }

            group = new PaneGroup(layout) { RegisteredPaneId = entry.Id };
            if (entry.Active >= 0 && entry.Active < layout.Panes.Count)
            {
                group.LastActive = layout.Panes[entry.Active];
            }

            _pendingEmptyPanes.Clear(); // SetGroupLayout puts the group in _groups before it forgets anything
            if (!SetGroupLayout(group, layout))
            {
                Message.Info($"登録ペイン「{entry.Name}」に開けるフォルダがありません。");
                return;
            }
        }
        finally
        {
            _pendingEmptyPanes.Clear();
        }

        await ShowGroupAsync(group, group.LastActive);
    }

    /// <summary>
    /// The tab for a pane of a 登録ペイン: a fixed tab (opened if closed, moved to the saved place), an
    /// open folder tab of that folder (<paramref name="reuse"/>), or a new folder tab with the saved view.
    /// Taken out of the group it was in. Null for an entry that names no place.
    /// </summary>
    private TabLayoutViewModel? TabFor(OpenTabSetting saved, bool reuse, HashSet<TabLayoutViewModel> used)
    {
        var kind = TabKinds.FromSetting(saved.Kind);
        bool located = !string.IsNullOrEmpty(saved.Root) && PathTools.IsSafeRel(saved.Path ?? string.Empty);
        var location = located ? new NavLocation(kind, saved.Root!, saved.Path ?? string.Empty) : NavLocation.Top(kind);
        TabLayoutViewModel? tab;
        if (kind is TabKind.Recent or TabKind.Frequent)
        {
            tab = FixedTabOf(kind);
            if (!used.Add(tab))
            {
                return null; // twice in one layout: the second pane stays empty
            }

            var pane = tab.ActivePane;
            if (!tab.IsOpen)
            {
                RestoreViewState(pane, saved);
                AddToStrip(tab);
            }

            if (pane.Location != location)
            {
                Push(pane.BackStack, new NavEntry(pane.Location, pane.SavedScrollOffset));
                pane.ForwardStack.Clear();
                pane.Location = location;
                pane.SavedScrollOffset = 0;
                pane.IsStale = true;
            }
            else if (pane.IsTop)
            {
                pane.IsStale = true; // a fixed tab's list is rebuilt each time it comes on screen
            }
        }
        else if (!located)
        {
            return null;
        }
        else
        {
            // not a tab of another 登録ペイン's group: that group would lose a pane (and, with 自動記録, its entry with it)
            tab = reuse
                ? Tabs.FirstOrDefault(t => t.IsFolderTab && !used.Contains(t) && SameLocation(t.ActivePane.Location, location)
                    && GroupOf(t.ActivePane)?.RegisteredPaneId is null)
                : null;
            if (tab is null)
            {
                tab = AddFolderTab(location);
                tab.ActivePane.SetIdentity(location, saved.FileId);
                RestoreViewState(tab.ActivePane, saved);
            }

            used.Add(tab);
        }

        if (Settings.PaneAutoRecord && GroupOf(tab.ActivePane) is { RegisteredPaneId: not null } other)
        {
            other.RegisteredPaneId = null; // a fixed tab taken from another 登録ペイン's group: that entry is not rewritten without it
        }

        TakeOutOfGroup(tab.ActivePane, keepEmpty: false, out _);
        return tab;
    }

    /// <summary>
    /// <paramref name="group"/> comes on screen with <paramref name="active"/> active; its panes that
    /// went stale are listed and the ones new on screen come back where they were scrolled to.
    /// </summary>
    private async Task ShowGroupAsync(PaneGroup group, PaneViewModel active)
    {
        var before = VisiblePanes.ToHashSet();
        foreach (var leaving in before)
        {
            leaving.SavedScrollOffset = View?.GetScrollOffset(leaving) ?? 0; // the panes that leave the screen come back where they were
        }

        var tab = TabOf(active) ?? TabsOf(group).FirstOrDefault();
        if (tab is null)
        {
            return;
        }

        if (!ReferenceEquals(tab, SelectedTab))
        {
            LeaveSelectedTab();
        }

        SetShownGroup(group);
        if (ReferenceEquals(tab, SelectedTab))
        {
            AnnounceLayout();
        }
        else
        {
            SelectedTab = tab; // announces the layout, then the active pane
        }

        SaveLocations();
        var shown = VisiblePanes;
        foreach (var pane in shown.Where(p => !before.Contains(p) && !p.IsStale))
        {
            View?.ScrollTo(pane, pane.SavedScrollOffset);
        }

        await Task.WhenAll(shown.Where(p => p.IsStale).Select(async p =>
        {
            bool fresh = p.IsTop && !p.IsFolderTab; // a fixed tab's list is rebuilt when shown
            await LoadTabAsync(p);
            if (IsVisible(p))
            {
                View?.ScrollTo(p, fresh ? 0 : p.SavedScrollOffset);
            }
        }).ToList());
    }
}
