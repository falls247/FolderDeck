using System.Text.Json.Serialization;
using FolderDeck.Core.Infrastructure;
using FolderDeck.Core.Layout;
using FolderDeck.Core.Ordering;

namespace FolderDeck.Core.Stores;

public sealed class WindowPlacementSetting
{
    public double? Left { get; set; }

    public double? Top { get; set; }

    public double Width { get; set; } = 1280;

    public double Height { get; set; } = 800;

    public bool Maximized { get; set; }
}

public sealed class LocationSetting
{
    public string Root { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// One open tab of the tab strip (<c>open_tabs</c>): its kind (<see cref="AppSettings.TabRecent"/>,
/// <see cref="AppSettings.TabFrequent"/> or <see cref="AppSettings.TabFolder"/>), its root folder
/// (a normalized path; "" = the top level of 最近の履歴 / よく使う項目) and the path below that root.
/// </summary>
public sealed class OpenTabSetting
{
    /// <summary>An entry without a kind is a folder tab: that is what the version 1 files hold.</summary>
    public string? Kind { get; set; } = AppSettings.TabFolder;

    public string Root { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The <see cref="Paths.FolderIdentity"/> of the folder the tab shows (null while unknown, and at a
    /// top level): the tab follows the folder when it was renamed or moved while the app was closed.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; set; }

    /// <summary>
    /// How the tab shows its items (<see cref="CardShapes"/>: portrait / square / details). Null in files
    /// written before it was kept per tab: <see cref="AppSettings.Normalize"/> fills it from <see cref="AppSettings.CardShape"/>.
    /// </summary>
    public string? CardShape { get; set; }

    /// <summary>
    /// The tab's sort mode (<see cref="CardSortModes"/>). Null in files written before it was kept per tab:
    /// <see cref="AppSettings.Normalize"/> fills it from the kind's <see cref="AppSettings.SortModes"/>.
    /// </summary>
    public string? SortMode { get; set; }

    /// <summary>A copy of every member (what <see cref="AppSettings.Clone"/> and <see cref="PaneNodeSetting.Clone"/> hand out).</summary>
    public OpenTabSetting Clone() => new()
    {
        Kind = Kind,
        Root = Root,
        Path = Path,
        FileId = FileId,
        CardShape = CardShape,
        SortMode = SortMode,
    };
}

/// <summary>
/// A node of a pane layout (see <see cref="PaneLayoutSettings"/>): a split <c>{ "split": "columns", "ratio": 0.5, "first": …, "second": … }</c>
/// (<c>columns</c>: side by side, <c>rows</c>: stacked; <c>ratio</c> is the first part's share) or a leaf
/// holding exactly one of <c>{ "pane": 0 }</c> (an index into <see cref="AppSettings.OpenTabs"/>: the tab that
/// pane shows; settings.json), <c>{ "tab": { … } }</c> (the tab itself; registered_panes.json) or
/// <c>{ "empty": true }</c> (an empty pane; both files).
/// </summary>
public sealed class PaneNodeSetting
{
    /// <summary><see cref="PaneLayoutSettings.Columns"/> or <see cref="PaneLayoutSettings.Rows"/>; null for a leaf.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Split { get; set; }

    /// <summary>Of a split: the share of <see cref="First"/>. Missing: <see cref="PaneRules.DefaultRatio"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Ratio { get; set; }

    /// <summary>Of a leaf: the index in <see cref="AppSettings.OpenTabs"/> of the tab the pane shows.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Pane { get; set; }

    /// <summary>Of a leaf: true for an empty pane (one that shows no tab). Not written otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Empty { get; set; }

    /// <summary>Of a leaf of a registered pane: the tab the pane opens (its location and view).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenTabSetting? Tab { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaneNodeSetting? First { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaneNodeSetting? Second { get; set; }

    /// <summary>A leaf for an empty pane: <c>{ "empty": true }</c>.</summary>
    public static PaneNodeSetting EmptyLeaf() => new() { Empty = true };

    public PaneNodeSetting Clone() => new()
    {
        Split = Split,
        Ratio = Ratio,
        Pane = Pane,
        Empty = Empty,
        Tab = Tab?.Clone(),
        First = First?.Clone(),
        Second = Second?.Clone(),
    };
}

/// <summary>
/// One pane group of <c>pane_groups</c>: a window layout of 2 to <see cref="PaneRules.MaxPanes"/> panes, each
/// showing an open tab (<c>pane</c>, a tab is in at most one group) or empty, at least one of them a tab.
/// </summary>
public sealed class PaneGroupSetting
{
    /// <summary>The tree (see <see cref="PaneLayoutSettings.IsValidGroup"/>).</summary>
    public PaneNodeSetting? Layout { get; set; }

    /// <summary>The active pane: its index among the leaves in reading order (0-based).</summary>
    public int Active { get; set; }

    /// <summary>
    /// Id in registered_panes.json (<see cref="RegisteredPanesStore"/>) of the registered pane the group
    /// was opened from; null when it was not (or is no longer known to come from one).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RegisteredPane { get; set; }

    public PaneGroupSetting Clone() => new()
    {
        Layout = Layout?.Clone(),
        Active = Active,
        RegisteredPane = RegisteredPane,
    };
}

/// <summary>Ids of the sidebar's collapsible sections (<c>sidebar_collapsed_sections</c>).</summary>
public static class SidebarSections
{
    /// <summary>クイック アクセス: 最近の履歴 and よく使う項目.</summary>
    public const string Quick = "quick";

    /// <summary>登録フォルダ.</summary>
    public const string Registered = "registered";

    /// <summary>登録ペイン (registered_panes.json), below 登録フォルダ.</summary>
    public const string Panes = "panes";

    /// <summary>PC: the drives.</summary>
    public const string Pc = "pc";

    public static IReadOnlyList<string> All { get; } = [Quick, Registered, Panes, Pc];
}

/// <summary>settings.json (§13.2). Missing keys keep these defaults.</summary>
public sealed class AppSettings
{
    public const int CardSizeMin = 120;
    public const int CardSizeMax = 400;
    public const int CardSizeStep = 20;
    public const int CardSizeDefault = 200;

    /// <summary>Range of <see cref="TextScale"/> (percent of the standard text size).</summary>
    public const int TextScaleMin = 100;
    public const int TextScaleMax = 150;
    public const int TextScaleStep = 10;
    public const int TextScaleDefault = 100;

    public const int ThumbCacheMbDefault = 512;
    public const int ThumbCacheMbMax = 8192;

    /// <summary>Range of <see cref="AutoRefreshSeconds"/>: below 2 s a share would not answer faster (redirector cache) and big folders would be listed all the time.</summary>
    public const int AutoRefreshSecondsMin = 2;
    public const int AutoRefreshSecondsMax = 3600;
    public const int AutoRefreshSecondsDefault = 5;

    public const double SidebarWidthMin = 140;
    public const double SidebarWidthMax = 480;
    public const double SidebarWidthDefault = 220;

    /// <summary>
    /// Version of the file layout. 3: the window may be split into panes, each showing one open tab or
    /// empty (<see cref="PaneGroups"/>; 0.8.0 wrote one split as <see cref="SplitLayout"/>, migrated when
    /// <c>pane_groups</c> is missing; a file with neither is not split, so a version 2 file needs no conversion). 2: one tab
    /// strip (<see cref="OpenTabs"/> holds every tab and <see cref="SelectedTab"/> says which is shown).
    /// 1: two fixed tabs plus folder tabs (<see cref="LastTab"/>, <see cref="Locations"/>, <see cref="SelectedFolderTab"/>).
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>The first version with one tab strip: files below it go through <see cref="MigrateLegacyTabs"/>.</summary>
    private const int TabStripVersion = 2;

    /// <summary>Folder tabs remembered across restarts (more are simply not saved).</summary>
    public const int OpenTabsMax = 50;

    public const string TabRecent = "recent";
    public const string TabFrequent = "frequent";

    /// <summary>The folder tabs: one key for all of them.</summary>
    public const string TabFolder = "folder";

    /// <summary>Key of the former 登録フォルダ tab (now the sidebar): only read to carry its sort mode over.</summary>
    private const string LegacyTabRegistered = "registered";

    /// <summary>A file without the key is one this layout wrote (the version 1 program always wrote 1).</summary>
    public int Version { get; set; } = CurrentVersion;

    public WindowPlacementSetting? Window { get; set; } = new();

    /// <summary>
    /// The open tabs, left to right: 最近の履歴 and よく使う項目 (each at most once) and the folder
    /// tabs. Null only until <see cref="Normalize"/>: settings that never held the key open
    /// 最近の履歴; a saved empty list stays empty (every tab was closed).
    /// </summary>
    public List<OpenTabSetting>? OpenTabs { get; set; }

    /// <summary>Index in <see cref="OpenTabs"/> of the shown tab; -1 when no tab is open.</summary>
    public int SelectedTab { get; set; }

    /// <summary>
    /// Read for migration only: how 0.8.0 split the window (each leaf's <c>pane</c> the index in
    /// <see cref="OpenTabs"/> of the tab that pane shows, <see cref="SelectedTab"/> the active pane). When
    /// <see cref="PaneGroups"/> is missing, <see cref="Normalize"/> turns it into the one group; after
    /// <see cref="Normalize"/> it is always null, so it is no longer written.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PaneNodeSetting? SplitLayout { get; set; }

    /// <summary>
    /// The pane groups, each a window layout of 2 to <see cref="PaneRules.MaxPanes"/> panes (see
    /// <see cref="PaneGroupSetting"/>); a tab is in at most one of them. Null only until <see cref="Normalize"/>
    /// (a file without the key migrates <see cref="SplitLayout"/>), then a list, empty when nothing is split.
    /// An exe that does not know it drops the key and shows <see cref="SelectedTab"/> alone.
    /// </summary>
    public List<PaneGroupSetting>? PaneGroups { get; set; }

    /// <summary>Index in <see cref="PaneGroups"/> of the group on screen; -1: none (<see cref="SelectedTab"/> is shown alone).</summary>
    public int ShownGroup { get; set; } = -1;

    /// <summary>
    /// Version 1 only: which tab was shown (<see cref="TabRecent"/>, <see cref="TabFrequent"/> or
    /// <see cref="TabFolder"/>). Read by <see cref="MigrateLegacyTabs"/>, then null and not written.
    /// The version 1 program writes it back when it saves a file of this layout, which is how an
    /// upgrade after a downgrade is noticed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastTab { get; set; }

    /// <summary>Version 1 only: where 最近の履歴 and よく使う項目 were (root "" = their top level).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, LocationSetting>? Locations { get; set; }

    /// <summary>
    /// Version 1 only: index of the shown folder tab among the folder tabs. Never written as null:
    /// the version 1 program reads it as a plain number and would reject the whole file.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SelectedFolderTab { get; set; }

    /// <summary>Width of the sidebar (DIP); kept while the sidebar is hidden.</summary>
    public double SidebarWidth { get; set; } = SidebarWidthDefault;

    /// <summary>The whole sidebar is hidden (the toggle at the left end of the tab row).</summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary>
    /// The sidebar sections that are folded to their header (<see cref="SidebarSections"/> ids).
    /// A section that is not listed is expanded, so a section added later starts expanded.
    /// </summary>
    public List<string>? SidebarCollapsedSections { get; set; }

    public int CardSize { get; set; } = CardSizeDefault;

    /// <summary>
    /// View mode of the tab used last: cards, <c>portrait</c> (3:4 thumbnail) or <c>square</c> (1:1),
    /// or the <c>details</c> list. Each tab keeps its own (<see cref="OpenTabSetting.CardShape"/>); a tab
    /// that is opened (not restored) starts with this one, and open_tabs entries without one take it.
    /// </summary>
    public string? CardShape { get; set; } = CardShapes.Portrait;

    /// <summary>
    /// Widths (DIP) of the columns of the details list: <c>name</c>, <c>modified</c>, <c>type</c>,
    /// <c>size</c> (see <see cref="Layout.DetailsColumns"/>; a missing key loads as its default).
    /// </summary>
    public Dictionary<string, double>? DetailsColumns { get; set; }

    /// <summary>
    /// The extra columns shown in the details view (ids of <see cref="Layout.DetailsPropertyColumns"/>,
    /// chosen with the header's right-click menu). Empty by default: only the four base columns.
    /// </summary>
    public List<string>? DetailsExtraColumns { get; set; }

    /// <summary>Colour theme: <c>system</c> (follow Windows), <c>light</c> or <c>dark</c>.</summary>
    public string? Theme { get; set; } = AppThemes.System;

    /// <summary>
    /// 文字の大きさ in percent of the standard size (<see cref="TextScaleMin"/>–<see cref="TextScaleMax"/>,
    /// steps of <see cref="TextScaleStep"/>): the text of the sidebar, the cards, the details list
    /// and the cascade lists, and the rows that hold it. Applied at startup and on OK in the settings.
    /// </summary>
    public int TextScale { get; set; } = TextScaleDefault;

    /// <summary>
    /// Sort mode per kind of tab, as earlier versions kept it: <see cref="TabRecent"/> / <see cref="TabFrequent"/> /
    /// <see cref="TabFolder"/> → <c>manual</c>, <c>name_asc</c>, <c>name_desc</c>, <c>modified_asc</c>, <c>modified_desc</c>,
    /// <c>size_asc</c>, <c>size_desc</c>, <c>type_asc</c>, <c>type_desc</c>. Each tab keeps its own now
    /// (<see cref="OpenTabSetting.SortMode"/>): this is read only to fill open_tabs entries without one, and
    /// still written (the last sort mode set in a tab of that kind) for an earlier exe.
    /// </summary>
    public Dictionary<string, string>? SortModes { get; set; } = new();

    /// <summary>
    /// Sort mode of the tab used last (the shown one; the last shown while no tab is open): a tab that is
    /// opened, not restored, starts with it and with <see cref="CardShape"/>. Missing: the selected open tab's.
    /// </summary>
    public string? LastSortMode { get; set; }

    /// <summary>
    /// What the top level of <see cref="TabRecent"/> / <see cref="TabFrequent"/> shows:
    /// <c>all</c>, <c>folders</c> or <c>files</c>.
    /// </summary>
    public Dictionary<string, string>? FilterModes { get; set; } = new();

    public bool ShowHidden { get; set; }

    /// <summary>
    /// List names starting with '.' (.vscode, .gitignore). Independent of <see cref="ShowHidden"/>: a '.'
    /// entry that is also hidden (Git for Windows marks .git so) needs both.
    /// </summary>
    public bool ShowDotItems { get; set; }

    /// <summary>What folder listings include, from <see cref="ShowHidden"/> and <see cref="ShowDotItems"/>.</summary>
    public ListingFilter ToListingFilter() =>
        (ShowHidden ? ListingFilter.Hidden : ListingFilter.None) | (ShowDotItems ? ListingFilter.DotNames : ListingFilter.None);

    /// <summary>
    /// Ask before a program (ShellActions.IsExecutable) runs. Not asked for the double click of
    /// <see cref="OpenOnDoubleClick"/> (on a card / details row); every other way of opening still asks.
    /// </summary>
    public bool ConfirmExecutables { get; set; } = true;

    public bool IncludeWindowsRecent { get; set; } = true;

    public int RecentLimit { get; set; } = 100;

    public int FrequentLimit { get; set; } = 50;

    public DateTimeOffset? RecentClearedAt { get; set; }

    public bool AddToWindowsRecent { get; set; }

    /// <summary>"Show in Explorer" uses a new tab of the running File Explorer window (Windows 11 22H2+).</summary>
    public bool OpenExplorerInNewTab { get; set; }

    /// <summary>
    /// Items of a tab (cards, rows of the details list) open on a double click; a single click
    /// without Ctrl / Shift selects only that item (the default, as Explorer). Off: a single click opens.
    /// Read at every click, so OK in the settings applies without a restart. A settings.json that
    /// already has the key keeps its value (a missing key is on).
    /// </summary>
    public bool OpenOnDoubleClick { get; set; } = true;

    /// <summary>
    /// Right-click menu of items: <c>app</c> (the app's own menu) or <c>windows</c> (Explorer's
    /// menu with the app's entries on top of it).
    /// </summary>
    public string? ItemMenu { get; set; } = ItemMenuStyles.App;

    /// <summary>
    /// Where the message line is shown: <c>top</c> (under the toolbar), <c>bottom</c> (under the
    /// items) or <c>off</c> (success messages are not shown; info, warnings and errors still are, at
    /// the top). Applied on OK in the settings, without a restart.
    /// </summary>
    public string? MessagePosition { get; set; } = MessagePlacements.Top;

    /// <summary>
    /// Upper limit, in megabytes, of the thumbnail cache kept in the data folder (<c>thumbs</c>);
    /// 0 turns the disk cache off. Read at startup.
    /// </summary>
    public int ThumbCacheMb { get; set; } = ThumbCacheMbDefault;

    /// <summary>
    /// The folder shown is listed again every <see cref="AutoRefreshSeconds"/> and its list is
    /// updated when something changed (off: only F5 and the app's own operations reload it).
    /// Applied on OK in the settings, without a restart. On by default; a settings.json that already
    /// has the key keeps its value (a missing key is on).
    /// </summary>
    public bool AutoRefresh { get; set; } = true;

    /// <summary>The interval of <see cref="AutoRefresh"/>, in seconds (kept while it is off).</summary>
    public int AutoRefreshSeconds { get; set; } = AutoRefreshSecondsDefault;

    /// <summary>
    /// 常駐モード: start at the Windows sign-in, hidden in the notification area (the Run key of
    /// HKCU holds the exe and <c>--background</c>, see <see cref="Infrastructure.LoginStartup"/>).
    /// Off by default. The registry is the truth: at startup this follows what is registered
    /// (not with <c>--data-dir</c>, which never touches the Run key).
    /// </summary>
    public bool StartAtLogin { get; set; }

    /// <summary>
    /// 常駐モード: closing the window hides it in the notification area instead of quitting (the
    /// tray's 終了 quits). Off by default.
    /// </summary>
    public bool KeepRunningOnClose { get; set; }

    /// <summary>名前で検索 (Ctrl+F) looks only in the shown folder.</summary>
    public const string SearchScopeFolder = "folder";

    /// <summary>名前で検索 (Ctrl+F) also looks in the subfolders (Windows Search and a walk, see <see cref="Listing.DeepSearch"/>).</summary>
    public const string SearchScopeRecursive = "recursive";

    /// <summary>
    /// Where the search of a folder looks: <see cref="SearchScopeFolder"/> (the default, the list
    /// shown) or <see cref="SearchScopeRecursive"/>. Unknown values are the default.
    /// </summary>
    public string? SearchScope { get; set; } = SearchScopeFolder;

    [JsonIgnore]
    public bool SearchesRecursively => SearchScope == SearchScopeRecursive;

    /// <summary>
    /// タブバーの重複を許可する: opening a folder (a 登録フォルダ, a drive, the cascade opened from them) always
    /// opens a new folder tab. Off by default: a folder tab that already shows that folder is shown instead.
    /// </summary>
    public bool AllowDuplicateTabs { get; set; }

    /// <summary>
    /// 登録ペインの内容を自動記録する: a pane group opened from a registered pane writes its layout back to
    /// it whenever it changes (現在のペインを上書きする is then not needed). Off by default.
    /// </summary>
    public bool PaneAutoRecord { get; set; }

    /// <summary>
    /// 登録ペインを開くとき同じ場所のタブを使う: opening a registered pane uses an open folder tab at the same
    /// location instead of opening a new one (最近の履歴 / よく使う項目 are always reused). On by default.
    /// </summary>
    public bool PaneReuseOpenTabs { get; set; } = true;

    /// <summary>ペインを閉じたときにタブも閉じる: closing a pane closes its tab too (off: the tab stays on the strip).</summary>
    public bool PaneCloseClosesTab { get; set; }

    /// <summary>
    /// グループの外へ出したタブのペインを空のペインとして残す: a group's tab dragged out of the group's block on
    /// the strip leaves an empty pane (off: its pane closes).
    /// </summary>
    public bool PaneDragOutKeepsEmpty { get; set; }

    public static int ClampCardSize(int value) => Snap(value, CardSizeMin, CardSizeMax, CardSizeStep);

    public static int ClampTextScale(int value) => Snap(value, TextScaleMin, TextScaleMax, TextScaleStep);

    /// <summary>Pulls <paramref name="value"/> into min…max, onto the nearest step from min.</summary>
    private static int Snap(int value, int min, int max, int step)
    {
        int clamped = Math.Clamp(value, min, max);
        int steps = (int)Math.Round((clamped - min) / (double)step);
        return min + (steps * step);
    }

    /// <summary>Sort mode of a tab; missing or unknown values are <see cref="CardSortMode.Manual"/>.</summary>
    public CardSortMode SortModeOf(string tab) =>
        SortModes is not null && SortModes.TryGetValue(tab, out string? mode) ? CardSortModes.FromSetting(mode) : CardSortMode.Manual;

    public void SetSortMode(string tab, CardSortMode mode)
    {
        SortModes ??= new Dictionary<string, string>();
        SortModes[tab] = CardSortModes.ToSetting(mode);
    }

    /// <summary>Filter of a tab's top level; missing or unknown values are <see cref="ItemFilter.All"/>.</summary>
    public ItemFilter FilterOf(string tab) =>
        FilterModes is not null && FilterModes.TryGetValue(tab, out string? filter) ? ItemFilters.FromSetting(filter) : ItemFilter.All;

    public void SetFilter(string tab, ItemFilter filter)
    {
        FilterModes ??= new Dictionary<string, string>();
        FilterModes[tab] = ItemFilters.ToSetting(filter);
    }

    public static double ClampSidebarWidth(double value) =>
        double.IsFinite(value) ? Math.Clamp(Math.Round(value), SidebarWidthMin, SidebarWidthMax) : SidebarWidthDefault;

    /// <summary>The sidebar section <paramref name="id"/> (<see cref="SidebarSections"/>) is folded to its header.</summary>
    public bool IsSectionCollapsed(string id) => SidebarCollapsedSections?.Contains(id) == true;

    public void SetSectionCollapsed(string id, bool collapsed)
    {
        SidebarCollapsedSections ??= [];
        SidebarCollapsedSections.Remove(id);
        if (collapsed)
        {
            SidebarCollapsedSections.Add(id);
        }
    }

    /// <summary>
    /// Turns the version 1 tab keys into <see cref="OpenTabs"/> / <see cref="SelectedTab"/>: 最近の履歴
    /// and よく使う項目 (both were always there, at their saved locations), then the folder tabs, with
    /// the tab that was shown selected. Every entry of a version 1 <c>open_tabs</c> is a folder tab,
    /// whatever kind it carries. Does nothing to settings that are already in the current layout.
    /// </summary>
    public void MigrateLegacyTabs()
    {
        if (Version >= TabStripVersion && LastTab is null)
        {
            return;
        }

        // Same order as version 1: entries without a root go first, then the index is clamped.
        var folders = (OpenTabs ?? [])
            .Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Root))
            .Take(OpenTabsMax)
            .Select(t => new OpenTabSetting { Kind = TabFolder, Root = t.Root, Path = t.Path ?? string.Empty })
            .ToList();
        int selectedFolder = Math.Clamp(SelectedFolderTab ?? 0, 0, Math.Max(0, folders.Count - 1));

        OpenTabSetting Fixed(string kind)
        {
            LocationSetting? loc = null;
            Locations?.TryGetValue(kind, out loc);
            return loc is null || string.IsNullOrWhiteSpace(loc.Root)
                ? new OpenTabSetting { Kind = kind }
                : new OpenTabSetting { Kind = kind, Root = loc.Root, Path = loc.Path ?? string.Empty };
        }

        OpenTabs = [Fixed(TabRecent), Fixed(TabFrequent), .. folders];
        SelectedTab = LastTab switch
        {
            TabFrequent => 1,
            TabFolder when folders.Count > 0 => 2 + selectedFolder,
            _ => 0,
        };
        LastTab = null;
        Locations = null;
        SelectedFolderTab = null;
        SplitLayout = null; // its indexes were into the tabs that are now rebuilt
        PaneGroups = null;
        ShownGroup = -1;
        Version = CurrentVersion;
    }

    /// <summary>
    /// Turns the split of 0.8.0 (<see cref="SplitLayout"/>) into one pane group when the file has no
    /// <see cref="PaneGroups"/>: the selected tab's pane is the active one and the group is shown. 0.8.0 never
    /// wrote a split without the selected tab; such a group is kept but not shown (the tab is shown alone, as
    /// 0.8.0 did). Runs before the open tabs are normalized: the leaves are still indexes into the tabs as
    /// written. <see cref="SplitLayout"/> is null afterwards in every case.
    /// </summary>
    private void MigrateSplitLayout()
    {
        var split = SplitLayout;
        SplitLayout = null;
        if (PaneGroups is not null || OpenTabs is null || !PaneLayoutSettings.IsValid(split, OpenTabs.Count))
        {
            return;
        }

        int active = PaneLayoutSettings.LeavesOf(split!).FindIndex(leaf => leaf.Pane == SelectedTab);
        PaneGroups = [new PaneGroupSetting { Layout = split, Active = Math.Max(active, 0) }];
        ShownGroup = active >= 0 ? 0 : -1;
    }

    /// <summary>
    /// Brings <see cref="OpenTabs"/> into shape and keeps <see cref="SelectedTab"/> on the entry it
    /// pointed at while entries are dropped (when that very entry goes: on the one after it). Returns,
    /// for each entry it was given, its index now (-1: dropped); null on a first run (there were none).
    /// </summary>
    private int[]? NormalizeOpenTabs()
    {
        if (OpenTabs is null)
        {
            OpenTabs = [new OpenTabSetting { Kind = TabRecent }]; // first run
            SelectedTab = 0;
            return null;
        }

        var newIndex = new int[OpenTabs.Count];
        Array.Fill(newIndex, -1);
        var kept = new List<OpenTabSetting>(OpenTabs.Count);
        int selected = -1;
        bool recent = false;
        bool frequent = false;
        int folders = 0;

        // The folder tabs in a pane group are kept whatever their place: the cap of OpenTabsMax drops
        // folder tabs from the right, which would otherwise take the groups' panes with them.
        var shown = new HashSet<int>();
        foreach (var group in PaneGroups ?? [])
        {
            if (group is not null && PaneLayoutSettings.IsGroupShape(group.Layout))
            {
                foreach (var leaf in PaneLayoutSettings.LeavesOf(group.Layout!))
                {
                    if (leaf.Pane is int pane && pane >= 0 && pane < OpenTabs.Count)
                    {
                        shown.Add(pane);
                    }
                }
            }
        }

        int reserved = shown.Count(i => OpenTabs[i] is { } t && !string.IsNullOrWhiteSpace(t.Root)
            && t.Kind?.Trim().ToLowerInvariant() is not (TabRecent or TabFrequent));
        for (int i = 0; i < OpenTabs.Count; i++)
        {
            if (i == SelectedTab)
            {
                selected = kept.Count;
            }

            var tab = OpenTabs[i];
            if (tab is null)
            {
                continue;
            }

            // An entry of unknown kind is a folder tab (with a root) or nothing, never 最近の履歴.
            string kind = tab.Kind?.Trim().ToLowerInvariant() switch
            {
                TabRecent => TabRecent,
                TabFrequent => TabFrequent,
                _ => TabFolder,
            };
            bool blankRoot = string.IsNullOrWhiteSpace(tab.Root);
            bool keep = kind switch
            {
                TabRecent => !recent,
                TabFrequent => !frequent,
                _ => !blankRoot && (shown.Contains(i) || folders < OpenTabsMax - reserved),
            };
            if (!keep)
            {
                continue;
            }

            recent |= kind == TabRecent;
            frequent |= kind == TabFrequent;
            folders += kind == TabFolder && !shown.Contains(i) ? 1 : 0;
            tab.Kind = kind;
            tab.Root = blankRoot ? string.Empty : tab.Root;
            tab.Path = blankRoot ? string.Empty : tab.Path ?? string.Empty;
            tab.FileId = blankRoot || string.IsNullOrWhiteSpace(tab.FileId) ? null : tab.FileId;
            newIndex[i] = kept.Count;
            kept.Add(tab);
        }

        OpenTabs = kept;
        SelectedTab = kept.Count == 0 ? -1 : Math.Clamp(selected < 0 ? SelectedTab : selected, 0, kept.Count - 1);
        return newIndex;
    }

    /// <summary>
    /// Makes <see cref="PaneGroups"/> point at the entries <see cref="NormalizeOpenTabs"/> kept
    /// (<paramref name="newIndex"/>: where each entry it was given is now; null on a first run: no groups).
    /// A leaf whose tab was dropped, or whose tab an earlier group or leaf shows already, becomes an empty
    /// pane; a group of the wrong shape or without any tab left is dropped (see
    /// <see cref="PaneLayoutSettings.NormalizeGroup"/>). <see cref="PaneGroupSetting.Active"/> out of range is
    /// the first pane, a blank <see cref="PaneGroupSetting.RegisteredPane"/> none, and <see cref="ShownGroup"/>
    /// follows its group (-1 when that group was dropped).
    /// </summary>
    private void NormalizePaneGroups(int[]? newIndex)
    {
        var groups = PaneGroups ?? [];
        int shownGroup = ShownGroup;
        PaneGroups = [];
        ShownGroup = -1;
        if (newIndex is null)
        {
            return;
        }

        var used = new HashSet<int>();
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i] is not { } group || PaneLayoutSettings.NormalizeGroup(group.Layout, newIndex, used) is not { } layout)
            {
                continue;
            }

            int leaves = PaneLayoutSettings.LeavesOf(layout).Count;
            if (i == shownGroup)
            {
                ShownGroup = PaneGroups.Count;
            }

            PaneGroups.Add(new PaneGroupSetting
            {
                Layout = layout,
                Active = group.Active >= 0 && group.Active < leaves ? group.Active : 0,
                RegisteredPane = string.IsNullOrWhiteSpace(group.RegisteredPane) ? null : group.RegisteredPane,
            });
        }
    }

    /// <summary>Fills gaps and pulls out-of-range values back into range.</summary>
    public void Normalize()
    {
        if (Version < TabStripVersion || LastTab is not null)
        {
            MigrateLegacyTabs();
        }

        LastTab = null;
        Locations = null;
        SelectedFolderTab = null;
        Version = CurrentVersion;
        MigrateSplitLayout(); // before the open tabs: its leaves are indexes into them as written
        NormalizePaneGroups(NormalizeOpenTabs());
        Window ??= new WindowPlacementSetting();
        if (Window.Width < 200 || double.IsNaN(Window.Width))
        {
            Window.Width = 1280;
        }

        if (Window.Height < 150 || double.IsNaN(Window.Height))
        {
            Window.Height = 800;
        }

        SortModes ??= new Dictionary<string, string>();
        if (SortModes.Remove(LegacyTabRegistered, out string? legacy) && !SortModes.ContainsKey(TabFolder))
        {
            SortModes[TabFolder] = legacy; // folders opened from 登録フォルダ keep their sort mode
        }

        foreach (string tab in new[] { TabRecent, TabFrequent, TabFolder })
        {
            SortModes.TryGetValue(tab, out string? mode);
            SortModes[tab] = CardSortModes.ToSetting(CardSortModes.FromSetting(mode));
        }

        FilterModes ??= new Dictionary<string, string>();
        foreach (string tab in new[] { TabRecent, TabFrequent })
        {
            FilterModes.TryGetValue(tab, out string? filter);
            FilterModes[tab] = ItemFilters.ToSetting(ItemFilters.FromSetting(filter));
        }

        SidebarWidth = ClampSidebarWidth(SidebarWidth);

        // Known ids only, each once, in the order of the sections (a typed-in "Quick " still counts).
        var collapsed = (SidebarCollapsedSections ?? []).Select(id => id?.Trim().ToLowerInvariant()).ToHashSet();
        SidebarCollapsedSections = SidebarSections.All.Where(collapsed.Contains).ToList();
        CardSize = ClampCardSize(CardSize);
        CardShape = CardShapes.ToSetting(CardShapes.FromSetting(CardShape));
        DetailsColumns = Layout.DetailsColumns.Normalize(DetailsColumns);
        DetailsExtraColumns = Layout.DetailsPropertyColumns.NormalizeShown(DetailsExtraColumns);
        Theme = AppThemes.ToSetting(AppThemes.FromSetting(Theme));
        TextScale = ClampTextScale(TextScale);
        ItemMenu = ItemMenuStyles.ToSetting(ItemMenuStyles.FromSetting(ItemMenu));
        MessagePosition = MessagePlacements.ToSetting(MessagePlacements.FromSetting(MessagePosition));
        RecentLimit = Math.Clamp(RecentLimit, 10, 500);
        FrequentLimit = Math.Clamp(FrequentLimit, 10, 200);
        ThumbCacheMb = Math.Clamp(ThumbCacheMb, 0, ThumbCacheMbMax);
        AutoRefreshSeconds = Math.Clamp(AutoRefreshSeconds, AutoRefreshSecondsMin, AutoRefreshSecondsMax);
        SearchScope = string.Equals(SearchScope?.Trim(), SearchScopeRecursive, StringComparison.OrdinalIgnoreCase) ? SearchScopeRecursive : SearchScopeFolder;
        NormalizeTabViews(); // last: reads the normalized open_tabs, card_shape and sort_modes
    }

    /// <summary>
    /// Gives every open tab its own view mode and sort mode. Entries written before they were kept per tab
    /// take the shared card_shape and the sort mode of their kind (sort_modes).
    /// </summary>
    private void NormalizeTabViews()
    {
        foreach (var tab in OpenTabs!)
        {
            tab.CardShape = CardShapes.ToSetting(CardShapes.FromSetting(string.IsNullOrWhiteSpace(tab.CardShape) ? CardShape : tab.CardShape));
            tab.SortMode = CardSortModes.ToSetting(tab.SortMode is null ? SortModeOf(tab.Kind!) : CardSortModes.FromSetting(tab.SortMode));
        }

        LastSortMode ??= SelectedTab >= 0 && SelectedTab < OpenTabs.Count ? OpenTabs[SelectedTab].SortMode : SortModes![TabFolder];
        LastSortMode = CardSortModes.ToSetting(CardSortModes.FromSetting(LastSortMode));
    }

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Window = Window is null ? null : new WindowPlacementSetting
        {
            Left = Window.Left,
            Top = Window.Top,
            Width = Window.Width,
            Height = Window.Height,
            Maximized = Window.Maximized,
        };
        copy.Locations = Locations?.ToDictionary(kv => kv.Key, kv => new LocationSetting { Root = kv.Value.Root, Path = kv.Value.Path });
        // Every member of OpenTabSetting: Current is handed out through this copy.
        copy.OpenTabs = OpenTabs?.Select(t => t.Clone()).ToList();
        copy.SplitLayout = SplitLayout?.Clone();
        copy.PaneGroups = PaneGroups?.Select(g => g?.Clone()!).ToList();
        copy.SidebarCollapsedSections = SidebarCollapsedSections?.ToList();
        copy.DetailsColumns = DetailsColumns is null ? null : new Dictionary<string, double>(DetailsColumns);
        copy.DetailsExtraColumns = DetailsExtraColumns?.ToList();
        copy.SortModes = SortModes is null ? null : new Dictionary<string, string>(SortModes);
        copy.FilterModes = FilterModes is null ? null : new Dictionary<string, string>(FilterModes);
        return copy;
    }
}

public sealed class SettingsStore : StoreBase
{
    public const string DefaultFileName = "settings.json";

    private AppSettings _settings = new();

    public SettingsStore(string filePath, IClock clock)
        : base(filePath, clock)
    {
        _settings.Normalize();
    }

    /// <summary>A copy of the current settings.</summary>
    public AppSettings Current
    {
        get
        {
            lock (Gate)
            {
                return _settings.Clone();
            }
        }
    }

    public override void Load()
    {
        var result = JsonFile.Load(FilePath, () => new AppSettings(), null, Clock);
        ApplyLoad(result);
        var loaded = result.Data;
        loaded.Normalize();
        lock (Gate)
        {
            _settings = loaded;
        }
    }

    /// <summary>Applies <paramref name="change"/> to the stored settings and schedules a save.</summary>
    public void Update(Action<AppSettings> change)
    {
        lock (Gate)
        {
            change(_settings);
            _settings.Normalize();
        }

        RequestSave();
    }

    protected override byte[] Serialize() => JsonFile.Serialize(_settings);
}
