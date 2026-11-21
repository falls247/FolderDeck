namespace FolderDeck.ViewModels;

public enum TabKind
{
    Registered,
    Recent,
    Frequent,
}

/// <summary>
/// Where a tab is (§8.1): the tab, its root item (registration id for 登録フォルダ, normalized path
/// otherwise; "" = the tab's top level) and the relative path below that root ("" = the root).
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
}

/// <summary>
/// Anything the user can open, navigate to or right-click: a card, an overlay/flyout row, a
/// flyout header. <see cref="Target"/> is where a folder leads inside the app.
/// </summary>
public sealed record ItemRef(string Path, bool IsFolder, NavLocation? Target, ItemContext Context, CardViewModel? Card = null)
{
    public string Name => FolderDeck.Core.Paths.PathTools.DisplayName(Path);
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
    public static string Title(TabKind kind) => kind switch
    {
        TabKind.Registered => "登録フォルダ",
        TabKind.Recent => "最近の履歴",
        _ => "よく使う項目",
    };

    public static string ToSetting(TabKind kind) => kind switch
    {
        TabKind.Registered => Core.Stores.AppSettings.TabRegistered,
        TabKind.Recent => Core.Stores.AppSettings.TabRecent,
        _ => Core.Stores.AppSettings.TabFrequent,
    };

    public static TabKind FromSetting(string? value) => value switch
    {
        Core.Stores.AppSettings.TabRecent => TabKind.Recent,
        Core.Stores.AppSettings.TabFrequent => TabKind.Frequent,
        _ => TabKind.Registered,
    };
}
