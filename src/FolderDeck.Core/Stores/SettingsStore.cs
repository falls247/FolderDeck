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
}

/// <summary>Ids of the sidebar's collapsible sections (<c>sidebar_collapsed_sections</c>).</summary>
public static class SidebarSections
{
    /// <summary>クイック アクセス: 最近の履歴 and よく使う項目.</summary>
    public const string Quick = "quick";

    /// <summary>登録フォルダ.</summary>
    public const string Registered = "registered";

    /// <summary>PC: the drives.</summary>
    public const string Pc = "pc";

    public static IReadOnlyList<string> All { get; } = [Quick, Registered, Pc];
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
    /// Version of the file layout. 2: one tab strip (<see cref="OpenTabs"/> holds every tab and
    /// <see cref="SelectedTab"/> says which is shown). 1: two fixed tabs plus folder tabs
    /// (<see cref="LastTab"/>, <see cref="Locations"/>, <see cref="SelectedFolderTab"/>).
    /// </summary>
    public const int CurrentVersion = 2;

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
    /// How every tab shows its items: cards, <c>portrait</c> (3:4 thumbnail) or <c>square</c> (1:1),
    /// or the <c>details</c> list.
    /// </summary>
    public string? CardShape { get; set; } = CardShapes.Portrait;

    /// <summary>
    /// Widths (DIP) of the columns of the details list: <c>name</c>, <c>modified</c>, <c>type</c>,
    /// <c>size</c> (see <see cref="Layout.DetailsColumns"/>; a missing key loads as its default).
    /// </summary>
    public Dictionary<string, double>? DetailsColumns { get; set; }

    /// <summary>Colour theme: <c>system</c> (follow Windows), <c>light</c> or <c>dark</c>.</summary>
    public string? Theme { get; set; } = AppThemes.System;

    /// <summary>
    /// 文字の大きさ in percent of the standard size (<see cref="TextScaleMin"/>–<see cref="TextScaleMax"/>,
    /// steps of <see cref="TextScaleStep"/>): the text of the sidebar, the cards, the details list
    /// and the cascade lists, and the rows that hold it. Applied at startup and on OK in the settings.
    /// </summary>
    public int TextScale { get; set; } = TextScaleDefault;

    /// <summary>
    /// Sort mode per tab: <see cref="TabRecent"/> / <see cref="TabFrequent"/> / <see cref="TabFolder"/> →
    /// <c>manual</c>, <c>name_asc</c>, <c>name_desc</c>, <c>modified_asc</c>, <c>modified_desc</c>,
    /// <c>size_asc</c>, <c>size_desc</c>, <c>type_asc</c>, <c>type_desc</c>.
    /// </summary>
    public Dictionary<string, string>? SortModes { get; set; } = new();

    /// <summary>
    /// What the top level of <see cref="TabRecent"/> / <see cref="TabFrequent"/> shows:
    /// <c>all</c>, <c>folders</c> or <c>files</c>.
    /// </summary>
    public Dictionary<string, string>? FilterModes { get; set; } = new();

    public bool ShowHidden { get; set; }

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
    /// without Ctrl / Shift selects only that item. Off: a single click opens (the default).
    /// Read at every click, so OK in the settings applies without a restart.
    /// </summary>
    public bool OpenOnDoubleClick { get; set; }

    /// <summary>
    /// Right-click menu of items: <c>app</c> (the app's own menu) or <c>windows</c> (Explorer's
    /// menu with the app's entries on top of it).
    /// </summary>
    public string? ItemMenu { get; set; } = ItemMenuStyles.App;

    /// <summary>
    /// Upper limit, in megabytes, of the thumbnail cache kept in the data folder (<c>thumbs</c>);
    /// 0 turns the disk cache off. Read at startup.
    /// </summary>
    public int ThumbCacheMb { get; set; } = ThumbCacheMbDefault;

    /// <summary>
    /// The folder shown is listed again every <see cref="AutoRefreshSeconds"/> and its list is
    /// updated when something changed (off: only F5 and the app's own operations reload it).
    /// Applied on OK in the settings, without a restart.
    /// </summary>
    public bool AutoRefresh { get; set; }

    /// <summary>The interval of <see cref="AutoRefresh"/>, in seconds (kept while it is off).</summary>
    public int AutoRefreshSeconds { get; set; } = AutoRefreshSecondsDefault;

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
        if (Version >= CurrentVersion && LastTab is null)
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
        Version = CurrentVersion;
    }

    /// <summary>
    /// Brings <see cref="OpenTabs"/> into shape and keeps <see cref="SelectedTab"/> on the entry it
    /// pointed at while entries are dropped (when that very entry goes: on the one after it).
    /// </summary>
    private void NormalizeOpenTabs()
    {
        if (OpenTabs is null)
        {
            OpenTabs = [new OpenTabSetting { Kind = TabRecent }]; // first run
            SelectedTab = 0;
            return;
        }

        var kept = new List<OpenTabSetting>(OpenTabs.Count);
        int selected = -1;
        bool recent = false;
        bool frequent = false;
        int folders = 0;
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
                _ => !blankRoot && folders < OpenTabsMax,
            };
            if (!keep)
            {
                continue;
            }

            recent |= kind == TabRecent;
            frequent |= kind == TabFrequent;
            folders += kind == TabFolder ? 1 : 0;
            tab.Kind = kind;
            tab.Root = blankRoot ? string.Empty : tab.Root;
            tab.Path = blankRoot ? string.Empty : tab.Path ?? string.Empty;
            tab.FileId = blankRoot || string.IsNullOrWhiteSpace(tab.FileId) ? null : tab.FileId;
            kept.Add(tab);
        }

        OpenTabs = kept;
        SelectedTab = kept.Count == 0 ? -1 : Math.Clamp(selected < 0 ? SelectedTab : selected, 0, kept.Count - 1);
    }

    /// <summary>Fills gaps and pulls out-of-range values back into range.</summary>
    public void Normalize()
    {
        if (Version < CurrentVersion || LastTab is not null)
        {
            MigrateLegacyTabs();
        }

        LastTab = null;
        Locations = null;
        SelectedFolderTab = null;
        Version = CurrentVersion;
        NormalizeOpenTabs();
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
        Theme = AppThemes.ToSetting(AppThemes.FromSetting(Theme));
        TextScale = ClampTextScale(TextScale);
        ItemMenu = ItemMenuStyles.ToSetting(ItemMenuStyles.FromSetting(ItemMenu));
        RecentLimit = Math.Clamp(RecentLimit, 10, 500);
        FrequentLimit = Math.Clamp(FrequentLimit, 10, 200);
        ThumbCacheMb = Math.Clamp(ThumbCacheMb, 0, ThumbCacheMbMax);
        AutoRefreshSeconds = Math.Clamp(AutoRefreshSeconds, AutoRefreshSecondsMin, AutoRefreshSecondsMax);
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
        copy.OpenTabs = OpenTabs?.Select(t => new OpenTabSetting { Kind = t.Kind, Root = t.Root, Path = t.Path, FileId = t.FileId }).ToList();
        copy.SidebarCollapsedSections = SidebarCollapsedSections?.ToList();
        copy.DetailsColumns = DetailsColumns is null ? null : new Dictionary<string, double>(DetailsColumns);
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
