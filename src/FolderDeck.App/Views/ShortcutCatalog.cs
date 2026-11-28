using System.Diagnostics;
using System.Windows.Markup;

namespace FolderDeck.Views;

/// <summary>
/// A shortcut that something else in the UI names (a tooltip, a menu entry). Only the entries those
/// places need have an id; the list shows every entry either way.
/// </summary>
public enum ShortcutId
{
    Open,
    Copy,
    Cut,
    Paste,
    Delete,
    Rename,
    NewFolder,
    Undo,
    Redo,
    Properties,
    Back,
    Forward,
    Up,
    Refresh,
    CloseTab,
    ReopenTab,
    SplitColumns,
    SplitRows,
    NextPane,
    Search,
    Address,
    ShortcutHelp,
}

/// <summary>One line of the shortcut list: the keys as shown ("Ctrl + C / Ctrl + Insert") and what they do.</summary>
public sealed record Shortcut(string Keys, string Description, ShortcutId? Id = null);

/// <summary>A heading of the shortcut list and its lines.</summary>
public sealed record ShortcutGroup(string Title, IReadOnlyList<Shortcut> Items);

/// <summary>
/// Every keyboard shortcut of the app, in one place (SOW §3.3-2): the list F1 / Ctrl + / shows
/// (<see cref="ShortcutHelpWindow"/>), the keys the toolbar's tooltips add (<see cref="ShortcutTipExtension"/>)
/// and the keys the app's menu shows beside its entries (ContextMenuFactory). The key branches
/// themselves stay where they are; each line names its branch so that a key added, moved or
/// dropped there is changed here too (nothing checks the two against each other).
/// <para>
/// Keys are written as in the SOW and the specification: modifiers first, " + " between keys,
/// " / " between alternatives (the first alternative is the one a menu shows). Mouse gestures
/// (Ctrl + wheel, the mouse's back button, Ctrl / Shift + click) are not listed.
/// </para>
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutGroup> Groups { get; } =
    [
        new("一覧の移動と選択",
        [
            new("↑ ↓ ← →", "現在の項目を移動してその項目を選択（画面の外ならスクロール）"), // MainWindow.HandleSelectionKey: NavKeyOf → MoveCursorByKey
            new("Home / End", "先頭 / 末尾の項目へ"), // same branch
            new("PageUp / PageDown", "1 画面分移動"), // same branch (IItemsView.PageRows)
            new("Shift + 移動キー", "起点からの範囲を選択"), // same branch, extend
            new("Ctrl + 移動キー", "選択を変えずに現在の項目だけ移動"), // same branch, select: false
            new("Ctrl + Shift + 移動キー", "起点からの範囲を選択に追加"), // same branch, select: false + extend
            new("Space", "現在の項目を選択（フォルダでは中身の一覧を開く / 閉じる。Ctrl + 移動キーで動かしたあとは、その項目も選択に加える / 外す）"), // HandleSelectionKey: Key.Space → IsCtrlMoveMode / OpenCascadeFromKeys / SelectAtCursor
            new("Ctrl + Space", "現在の項目の選択を切り替え"), // same branch, toggle
            new("文字キー", "名前がその文字で始まる次の項目へ（1 秒以内に続けて入力すると 1 つの名前として探す）"), // MainWindow.OnPreviewTextInput → JumpToTypedName
            new("Ctrl + A", "すべて選択"), // HandleSelectionKey: Ctrl + A / C / X / V switch
            new("Esc", "選択を解除"), // HandleSelectionKey: Key.Escape → ClearSelection
        ]),
        new("ファイル操作",
        [
            new("Enter", "選択した項目を開く", ShortcutId.Open), // HandleSelectionKey: Key.Enter → OpenSelection
            new("Ctrl + C / Ctrl + Insert", "コピー", ShortcutId.Copy), // HandleSelectionKey: Ctrl + A / C / X / V switch (Insert mapped before it)
            new("Ctrl + X", "切り取り", ShortcutId.Cut), // same switch
            new("Ctrl + V / Shift + Insert", "表示中のフォルダに貼り付け", ShortcutId.Paste), // same switch
            new("Delete / Ctrl + D", "削除（ごみ箱へ移動。確認あり）", ShortcutId.Delete), // HandleSelectionKey: Key.Delete / Ctrl + D → DeleteAsync
            new("F2", "名前の変更（複数の選択では連番を付ける）", ShortcutId.Rename), // HandleSelectionKey: Key.F2 → BeginRename
            new("Ctrl + Shift + N", "新しいフォルダー（名前の入力欄が開く）", ShortcutId.NewFolder), // OnPreviewKeyDown switch: Key.N → CreateFolderAndRenameAsync
            new("Ctrl + Z", "元に戻す（このアプリで行ったファイル操作）", ShortcutId.Undo), // HandleSelectionKey: Ctrl + Z → UndoAsync
            new("Ctrl + Y / Ctrl + Shift + Z", "やり直し", ShortcutId.Redo), // same branch → RedoAsync
            new("Shift + F10 / アプリケーション キー", "選択した項目のメニュー（選択がないときは表示中のフォルダのメニュー）"), // HandleSelectionKey: Key.Apps / Shift + F10 → ShowMenuFromKeys
            new("Alt + Enter", "プロパティ（選択がないときは表示中のフォルダ）", ShortcutId.Properties), // HandleSelectionKey: Alt + Enter → ShowProperties
            new("Enter", "名前の入力欄：確定"), // RenameEditor.OnBoxPreviewKeyDown
            new("Esc", "名前の入力欄：取り消し"), // same handler
        ]),
        new("表示・移動",
        [
            new("Alt + ←", "戻る", ShortcutId.Back), // OnPreviewKeyDown switch: Alt + Left / BrowserBack → GoBackCommand
            new("Alt + →", "進む", ShortcutId.Forward), // same switch: Alt + Right / BrowserForward → GoForwardCommand
            new("Alt + ↑ / Backspace", "1 つ上のフォルダへ", ShortcutId.Up), // same switch: Alt + Up, Key.Back → GoUpCommand
            new("Alt + ↓", "選択した 1 つのフォルダに入る"), // same switch: Alt + Down → FolderToEnter
            new("F5", "最新の情報に更新", ShortcutId.Refresh), // same switch: Key.F5 → RefreshCommand
        ]),
        new("タブ",
        [
            new("Ctrl + Tab / Ctrl + Shift + Tab", "次 / 前のタブ（端では反対の端へ）"), // OnPreviewKeyDown switch: Key.Tab → SelectAdjacentTab
            new("Ctrl + 1〜8", "左から n 番目のタブ"), // same switch: D1..D9, NumPad1..9 → SelectTabAt
            new("Ctrl + 9", "最後のタブ"), // same branch
            new("Ctrl + W", "表示中のタブを閉じる（分割しているタブでは操作中のペインを閉じる）", ShortcutId.CloseTab), // same switch: Key.W → ClosePaneOrTabAsync
            new("Ctrl + Shift + T", "閉じたタブを開き直す（タブの右クリックのメニューにも。複製は「タブを複製」）", ShortcutId.ReopenTab), // same switch: Key.T → ReopenClosedTabCommand
        ]),
        new("ペイン（タブの分割）",
        [
            new("Ctrl + \\", "操作中のペインを左右に分割（ツールバーのボタン・タブと一覧の空いているところの右クリックからも）", ShortcutId.SplitColumns), // OnPreviewKeyDown switch: Oem5 / OemBackslash → SplitActivePaneAsync(Columns)
            new("Ctrl + Shift + \\", "操作中のペインを上下に分割", ShortcutId.SplitRows), // same branch with Shift → SplitActivePaneAsync(Rows)
            new("F6 / Shift + F6", "次 / 前のペインへ（分割しているタブで）", ShortcutId.NextPane), // same switch: Key.F6 → SelectAdjacentPane
        ]),
        new("検索・アドレス",
        [
            new("Ctrl + F / Ctrl + E / F3", "一覧を名前で検索（検索の入力欄へ）", ShortcutId.Search), // OnPreviewKeyDown switch: F / E / F3 → BeginSearch
            new("Enter / ↓", "検索の入力欄：一覧へ戻る（検索はそのまま）"), // MainWindow.OnSearchKeyDown
            new("Esc", "検索の入力欄：検索を解除"), // same handler
            new("Ctrl + L / Alt + D / F4", "アドレス欄にパスを入力", ShortcutId.Address), // OnPreviewKeyDown switch: L / Alt + D / F4 → BeginAddressEdit
            new("Enter", "アドレス欄：入力したパスへ移動（cmd・wsl などはこのフォルダで実行）"), // MainWindow.OnAddressKeyDown → CommitAddressAsync
            new("Esc", "アドレス欄：入力をやめる"), // same handler → EndAddressEdit
            new("Ctrl + Shift + C", "アドレス欄：パスを WSL の形（/mnt/c/…）と切り替えてコピー"), // same handler → ToggleAddressWslPath
        ]),
        new("カスケード（フォルダの中身の一覧）",
        [
            new("Space", "現在の項目のフォルダの一覧を開く / 閉じる"), // HandleSelectionKey: Key.Space → OpenCascadeFromKeys
            new("→", "詳細表示：現在の項目のフォルダの一覧を開く（ファイルではプレビュー。← で閉じる）"), // HandleSelectionKey: NavKey.Right in the details view → OpenCascadeFromKeys
            new("↑ ↓", "一覧の行を選ぶ"), // CascadeController.HandleKey: Down / Up → MoveSelection
            new("→", "選んだフォルダの一覧を開く"), // HandleKey: Right → OpenSelectedChild
            new("←", "1 つ前の一覧へ戻る（キーで開いた最初の一覧では一覧を閉じる）"), // HandleKey: Left → CloseFrom; level 0 opened by keys → CloseAll
            new("Enter", "選んだ行を開く"), // HandleKey: Enter → OnRowClicked
            new("Esc", "いちばん奥の一覧を閉じる"), // HandleKey: Escape → CloseDeepest
        ]),
        new("その他",
        [
            new("F1 / Ctrl + /", "この一覧を表示 / 閉じる", ShortcutId.ShortcutHelp), // OnPreviewKeyDown switch: ShortcutHelpWindow.IsToggleKey → ShowShortcutHelp; closed by ShortcutHelpWindow.OnKeyDown
            new("Ctrl + Tab / Ctrl + PageDown", "設定：次のページ"), // SettingsWindow.OnWindowKeyDown
            new("Ctrl + Shift + Tab / Ctrl + PageUp", "設定：前のページ"), // same handler
        ]),
    ];

    private static readonly Dictionary<ShortcutId, string> s_keys = Groups
        .SelectMany(g => g.Items)
        .Where(s => s.Id is not null)
        .ToDictionary(s => s.Id!.Value, s => s.Keys);

    /// <summary>Every key of the shortcut <paramref name="id"/> ("Ctrl + C / Ctrl + Insert").</summary>
    public static string KeysOf(ShortcutId id)
    {
        if (s_keys.TryGetValue(id, out string? keys))
        {
            return keys;
        }

        Debug.Fail($"No shortcut has the id {id}"); // a tooltip without its keys beats a crash at startup
        return string.Empty;
    }

    /// <summary>The first key of <paramref name="id"/> ("Ctrl + C"): what a menu entry shows beside it.</summary>
    public static string MainKeysOf(ShortcutId id)
    {
        string keys = KeysOf(id);
        int slash = keys.IndexOf(" / ", StringComparison.Ordinal);
        return slash < 0 ? keys : keys[..slash];
    }

    /// <summary>A tooltip with the keys added: "更新（F5）".</summary>
    public static string Tip(string text, ShortcutId id)
    {
        string keys = KeysOf(id);
        return keys.Length == 0 ? text : $"{text}（{keys}）";
    }
}

/// <summary>
/// <c>ToolTip="{views:ShortcutTip Text=更新, Id=Refresh}"</c>: the text with the keys of the catalog
/// added ("更新（F5）"), and <see cref="Tail"/> after them. Keeps the toolbar's keys in step with the list.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class ShortcutTipExtension : MarkupExtension
{
    public string Text { get; set; } = string.Empty;

    public ShortcutId Id { get; set; }

    /// <summary>Text after the keys ("。中クリックでも閉じます").</summary>
    public string Tail { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => ShortcutCatalog.Tip(Text, Id) + Tail;
}
