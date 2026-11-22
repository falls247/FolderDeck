namespace FolderDeck.ViewModels;

public enum TabKind
{
    /// <summary>最近の履歴 (fixed tab: one instance, with a top level; open only while it is on the tab strip).</summary>
    Recent,

    /// <summary>よく使う項目 (fixed tab: one instance, with a top level; open only while it is on the tab strip).</summary>
    Frequent,

    /// <summary>A folder tab: opened from the 登録フォルダ sidebar, any number of them.</summary>
    Folder,

    /// <summary>
    /// No tab is open: the kind of the placeholder that is selected then (<c>MainViewModel.NoTab</c>).
    /// It is never on the tab strip, never loads anything and is never a navigation target.
    /// </summary>
    None,
}

/// <summary>
/// Where a tab is (§8.1): the kind of tab, its root item (a normalized path; "" = the top level of
/// 最近の履歴 / よく使う項目, a folder tab always has a root) and the relative path below that root
/// ("" = the root).
/// </summary>
public sealed record NavLocation(TabKind Tab, string Root, string Rel)
{
    public bool IsTop => Root.Length == 0;

    public static NavLocation Top(TabKind tab) => new(tab, string.Empty, string.Empty);
}

public sealed record NavEntry(NavLocation Location, double ScrollOffset);

public enum ItemContext
{
    TopCard,
    InnerCard,
    Row,
    FlyoutHeader,

    /// <summary>A 登録フォルダ row of the sidebar.</summary>
    Sidebar,

    /// <summary>
    /// A drive row of the sidebar's PC section: the root of a drive. It is never pinned, deleted or
    /// unregistered, and using it is not recorded in the history.
    /// </summary>
    Drive,
}

/// <summary>
/// Anything the user can open, navigate to or right-click: a card, an overlay/flyout row, a
/// flyout header, a sidebar row. <see cref="Target"/> is where a folder leads inside the app.
/// </summary>
public sealed record ItemRef(string Path, bool IsFolder, NavLocation? Target, ItemContext Context, CardViewModel? Card = null)
{
    public string Name => FolderDeck.Core.Paths.PathTools.DisplayName(Path);

    /// <summary>
    /// A folder that opens in a folder tab of its own rather than in the shown tab: a sidebar row
    /// and everything in the cascade opened from it.
    /// </summary>
    public bool NewTab { get; init; }

    /// <summary>
    /// The sidebar row this item stands for cannot be opened now (a missing 登録フォルダ, a drive that
    /// is not connected or does not answer). Cards carry their own state (<see cref="Card"/>).
    /// </summary>
    public bool IsDisabled { get; init; }
}

public sealed class BreadcrumbItem
{
    public BreadcrumbItem(string text, NavLocation? target, string? toolTip, bool isLast)
    {
        Text = text;
        Target = target;
        ToolTip = toolTip;
        IsLast = isLast;
    }

    public string Text { get; }

    public NavLocation? Target { get; }

    public string? ToolTip { get; }

    public bool IsLast { get; }

    public bool IsEllipsis => Target is null;

    public bool ShowSeparator => !IsLast;
}

public static class TabKinds
{
    /// <summary>Title of a fixed tab (a folder tab shows the name of its folder; the placeholder has none).</summary>
    public static string Title(TabKind kind) => kind switch
    {
        TabKind.Recent => "最近の履歴",
        TabKind.Frequent => "よく使う項目",
        TabKind.None => string.Empty,
        _ => "フォルダ",
    };

    /// <summary>
    /// Key in settings.json (sort mode, filter, kind of an open tab). Every folder tab shares one.
    /// The placeholder's key is in no settings list: nothing is saved for it.
    /// </summary>
    public static string ToSetting(TabKind kind) => kind switch
    {
        TabKind.Recent => Core.Stores.AppSettings.TabRecent,
        TabKind.Frequent => Core.Stores.AppSettings.TabFrequent,
        TabKind.None => "none",
        _ => Core.Stores.AppSettings.TabFolder,
    };

    /// <summary>
    /// Kind of an <c>open_tabs</c> entry. Anything but the two fixed kinds is a folder tab, as in
    /// <c>AppSettings.Normalize</c> (never the placeholder).
    /// </summary>
    public static TabKind FromSetting(string? value) => value switch
    {
        Core.Stores.AppSettings.TabRecent => TabKind.Recent,
        Core.Stores.AppSettings.TabFrequent => TabKind.Frequent,
        _ => TabKind.Folder,
    };
}
