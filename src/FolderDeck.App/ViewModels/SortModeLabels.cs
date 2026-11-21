using FolderDeck.Core.Ordering;

namespace FolderDeck.ViewModels;

/// <summary>User-facing names of the sort modes (toolbar button and its menu).</summary>
public static class SortModeLabels
{
    /// <summary>Last menu item on 最近の履歴 / よく使う項目: forgets the drag &amp; drop order.</summary>
    public const string ResetManual = "手動の並びをリセット";

    public const string ResetManualTip = "ドラッグで並べた順を破棄して、既定の順（最近の履歴は使った順、よく使う項目はピン留め→よく使う順）に戻します。";
    /// <summary>Short text on the toolbar button.</summary>
    public static string Short(CardSortMode mode) => mode switch
    {
        CardSortMode.NameAsc => "名前 ↑",
        CardSortMode.NameDesc => "名前 ↓",
        CardSortMode.ModifiedAsc => "更新日 ↑",
        CardSortMode.ModifiedDesc => "更新日 ↓",
        _ => "手動",
    };

    /// <summary>Menu item text.</summary>
    public static string Long(CardSortMode mode) => mode switch
    {
        CardSortMode.NameAsc => "名前（昇順）",
        CardSortMode.NameDesc => "名前（降順）",
        CardSortMode.ModifiedAsc => "更新日（昇順）",
        CardSortMode.ModifiedDesc => "更新日（降順）",
        _ => "手動（ドラッグで並べた順）",
    };
}

/// <summary>User-facing names of the item filter (toolbar button left of the sort button, and its menu).</summary>
public static class FilterLabels
{
    /// <summary>Short text on the toolbar button.</summary>
    public static string Short(ItemFilter filter) => filter switch
    {
        ItemFilter.Folders => "フォルダのみ",
        ItemFilter.Files => "ファイルのみ",
        _ => "すべて",
    };

    /// <summary>Menu item text.</summary>
    public static string Long(ItemFilter filter) => filter switch
    {
        ItemFilter.Folders => "フォルダのみ表示",
        ItemFilter.Files => "ファイルのみ表示",
        _ => "すべて表示",
    };
}
