# SOW：エクスプローラー互換ショートカットとキーボード操作の拡充

- 作成日：2026-11-27
- 対象：FolderDeck v0.7.0（`main` 96243e1）
- 状態：実装済み（未コミット）。§5 の差分は提案差分のまま残す（実装との違いは §10）。§3.3 の 4 も実装済み（§5 に提案差分はなく、内容は §10）
- 作業ブランチ：`feat/explorer-shortcuts`
- 元：デスクトップの `20261127_SOW.md`（ショートカットの SOW）に、利用者の追加依頼 3 件（§3.3）を足したもの

## 1. 目的

エクスプローラーで使えるショートカットのうち、FolderDeck で未対応・別動作のものを追加し、マウスなしで一覧の移動・選択・メニュー・タブ切り替え・検索の呼び出し・フォルダの中身の確認（カスケード）ができるようにする。あわせて、ショートカットを利用者が知る手段（一覧の表示とツールチップ）と、選択の合計サイズの表示を加える。

## 2. 背景（2026-11-27 の実機確認）

Debug ビルド、フォルダ 3・ファイル 121 のフォルダ、カード表示で確認。

| キー | 実機の動作 |
|---|---|
| ↓ →、Home、End、PageDown | 反応なし（スクロールも選択も変わらない） |
| Space、Ctrl + Space、Shift + ↓ | 反応なし |
| 文字キー | 反応なし |
| Shift + F10 | ウィンドウのシステム メニューが開く（誤動作） |
| アプリケーション キー | 反応なし |
| Ctrl + Tab / Ctrl + Shift + Tab | ツールバーのボタンへフォーカスが移る（タブ 1 つで確認） |
| Alt + Enter、Ctrl + D | 反応なし |
| Ctrl + E | 反応なし |

原因：カード・詳細行・タブが `Focusable="False"` で、一覧に「現在の項目」がない。キーは `MainWindow.OnPreviewKeyDown` で一括処理しており、該当キーの分岐がない。

元の SOW 作成後に分かったこと（`main` 96243e1 で確認）：検索は `main` に入っている（`MainWindow.BeginSearch`）。Ctrl + F と **F3 は既に** `BeginSearch` に割り当て済み。よって段階 3 の前提（検索が `main` にあること）は満たされており、追加が要るのは Ctrl + E だけ。

## 3. 範囲

### 3.1 対象（元の SOW のショートカット）

| 段階 | キー | 動作 |
|---|---|---|
| 1 | ↑ ↓ ← → | 現在の項目（カーソル）を移動し、その 1 つを選択。画面外ならスクロール。詳細表示の → は §3.3 の 1 |
| 1 | Home / End | 先頭 / 末尾へ |
| 1 | PageUp / PageDown | 1 画面分 |
| 1 | Shift + 上記 | 起点からの範囲選択 |
| 1 | Ctrl + 上記 | 選択を変えずにカーソルだけ移動 |
| 1 | Space / Ctrl + Space | カーソルの項目を選択 / 選択を切り替え。フォルダ上の Space は §3.3 の 1 |
| 1 | 文字キー | 名前が先頭一致する次の項目へ（1 秒以内の連続入力は連結） |
| 2 | Shift + F10 / アプリケーション キー | 選択項目のメニュー。選択なしは表示中のフォルダのメニュー |
| 2 | Alt + Enter | 選択項目のプロパティ |
| 2 | Ctrl + D | Delete と同じ（ごみ箱へ。確認あり） |
| 2 | Ctrl + Insert / Shift + Insert | Ctrl + C / Ctrl + V と同じ |
| 3 | Ctrl + Tab / Ctrl + Shift + Tab | 次 / 前のタブ（端で循環） |
| 3 | Ctrl + 1〜8 / Ctrl + 9 | n 番目のタブ / 最後のタブ |
| 3 | Ctrl + E | Ctrl + F・F3 と同じ（検索の入力欄へ） |

### 3.2 対象外

| 項目 | 理由 |
|---|---|
| 検索機能そのもの | 実装済み（Ctrl + F・F3） |
| Ctrl + T、Ctrl + N、F11 | 不要（2026-11-27 利用者の判断） |
| Ctrl + Shift + 1〜8、Alt + P（表示の切り替え） | 不採用（同上） |
| Shift + Delete | 「完全には削除しない」方針と衝突。Ctrl + Z で戻せない（§9 の 1：対象外のまま） |
| Ctrl + Shift + ドラッグ、Alt + ドラッグ | マウス操作。ショートカット作成機能が先に必要 |
| Tab / F6 での領域移動、サイドバー・タブ列の矢印操作 | フォーカス設計全体の変更になる。別 SOW |
| スクリーン リーダー向けの名前修正・カーソル移動の読み上げ | ショートカットではない。別 SOW |
| フォルダの中身のサイズの集計 | 再帰集計は重い（2026-11-27 利用者の判断：ファイルだけ合計） |

### 3.3 対象（追加依頼。2026-11-27）

| 番号 | 内容 | 決定事項 |
|---|---|---|
| 1 | キーボードでカスケードを開く | カーソルがフォルダにあるとき **Space** で中身の一覧（カスケード）を開く（カード表示・詳細表示とも）。**→** は詳細表示（1 列）のときだけ同じ動作。カード表示の → は横移動のまま（2026-11-27 利用者の判断）。ファイル上の Space は §3.1 のとおり選択。Ctrl + Space は常に選択の切り替え（開かない）。開いたあとは既存のカスケードのキー（↑ ↓ → ← Enter Esc）がそのまま効く |
| 2 | ショートカット一覧の表示とツールチップへのキー併記 | **F1** または **Ctrl + /** で、全ショートカットの一覧を表示（閉じるのは Esc・F1・Ctrl + / ・閉じるボタン）。ツールバーなど、対応するキーがあるボタンのツールチップに「（Ctrl + C）」の形でキーを併記 |
| 3 | ステータス表示の拡充 | 選択中の表示「n 個を選択」に、選択中の**ファイル**の合計サイズを足す（例：「3 個を選択　12.4 MB」）。フォルダを含む選択では「（フォルダを除く）」を併記。フォルダだけの選択ではサイズを出さない。サイズが分からないファイル（オンラインのみ等で長さ不明）は合計に含めない |
| 4 | 一覧の空いているところへのドロップ（Ctrl + ドラッグでのコピーを含む） | フォルダの上へのドロップ（Ctrl でコピー）は既存（`docs/specification.md` §13）。追加するのは、一覧の空いているところ（カードの間・最後の行の後ろ・詳細表示の最後の行より下）へのドロップを**表示中のフォルダ**への移動・コピーにすること。キーの規則はフォルダへのドロップと同じ（`FileDrag.DropEffectFor`：なし＝同じドライブは移動・別ドライブはコピー、Shift＝移動、Ctrl＝コピー）。同じフォルダの項目を Ctrl で落とすと複製（同名は「名前 - コピー」。貼り付けと同じ `IFileOperation` の名前の変更）、Ctrl なしで同じフォルダへは受け付けない。エクスプローラーからのドロップは、一覧の上なら移動・コピー、一覧の外（サイドバーの空き・タブ列など）は従来どおり「登録」。表示中のフォルダが実フォルダでない一覧（最近の履歴・よく使う項目・PC）の上は従来どおり（2026-11-27 利用者の判断） |

## 4. 方針

- **WPF のフォーカスは使わない**。項目は `Focusable="False"` のまま、タブごとに「現在の項目」（カーソル）をキーで持つ（ADR-1）。
- カーソルは `SelectionModel` に置く。Shift + クリックの起点（`Anchor`）と共通にし、マウスとキーで起点がずれないようにする。
- 移動先の計算は Core の純粋な関数にしてテストする。列数と 1 画面の行数は View から渡す。
- キーの受け口は既存の `MainWindow.OnPreviewKeyDown` → `HandleSelectionKey`。次の間は効かせない（既存キーと同じ規則）：入力欄の中、名前の入力中、範囲選択のドラッグ中、メニューの表示中。
- カスケード表示中の ↑ ↓ ← → Enter Esc は現状どおりカスケードが先に取る。
- カーソルの枠は、キーで動かしたときだけ表示する（マウス操作では出さない）。
- Ctrl + E は Ctrl + F・F3 と同じ `BeginSearch` を呼ぶだけにする。検索の入力欄にいる間は、文字キーのジャンプと矢印の移動を効かせない（入力欄の中では `HandleSelectionKey` を呼ばない既存の規則で満たす）。
- キーボードで開くカスケードは、ホバーで開く経路（カード起点の `CardMouseEnter` → `OpenOverlayAsync`、詳細表示の行の `AnchorMouseEnter`）と同じ開き方・同じ閉じ方にし、タイマーを待たずにすぐ開く入口を `CascadeController` に足す。ポインターの位置による自動の閉じ（exit タイマー）がキーで開いた直後に閉じないようにする。
- ショートカット一覧は、キーの表（名前・キー・説明）を 1 か所（App のデータ）に置き、一覧の表示とツールチップの併記の両方がそこを参照する。表と実際の分岐の食い違いを防ぐため、表の各行にコードの分岐の場所をコメントで残す。
- 合計サイズは、`TabViewModel` が既に持つ項目のサイズ（一覧の読み込み結果）から計算し、追加の I/O をしない。選択の変化（`SelectionModel.Changed`）のたびに再計算する（選択数が多いときの計算量は O(選択数)）。

## 5. 変更対象と提案差分

### 5.1 Core：移動先の計算（新規）

`src/FolderDeck.Core/Selection/GridNavigation.cs`（提案差分。名前は仮）

```diff
+namespace FolderDeck.Core.Selection;
+
+public enum NavKey { Left, Right, Up, Down, Home, End, PageUp, PageDown }
+
+public static class GridNavigation
+{
+    /// <summary>The index to move to. current < 0 (no cursor yet) starts at the first item.</summary>
+    public static int Move(int current, NavKey key, int count, int columns, int pageRows)
+    {
+        if (count == 0) return -1;
+        if (current < 0) return key == NavKey.End ? count - 1 : 0;
+        columns = Math.Max(1, columns);
+        int target = key switch
+        {
+            NavKey.Left => current - 1,
+            NavKey.Right => current + 1,
+            NavKey.Up => current - columns,
+            NavKey.Down => current + columns,
+            NavKey.PageUp => current - columns * Math.Max(1, pageRows),
+            NavKey.PageDown => current + columns * Math.Max(1, pageRows),
+            NavKey.Home => 0,
+            _ => count - 1,
+        };
+        // Up / Down past the edge stay in the same column; Left / Right stop at the ends.
+        ...
+    }
+
+    /// <summary>The next item whose name starts with the typed text, searching after current and wrapping.</summary>
+    public static int FindByPrefix(IReadOnlyList<string> names, int current, string typed) { ... }
+}
```

### 5.2 Core：カーソル（`SelectionModel`）

```diff
 public sealed class SelectionModel
 {
+    /// <summary>The item the keyboard is on. Not necessarily selected (Ctrl + arrows).</summary>
+    public string? Cursor { get; private set; }
+
+    /// <summary>Moves the cursor. select: only it is selected; extend: the range from the anchor.</summary>
+    public bool MoveCursor(IReadOnlyList<string> displayed, string key, bool select, bool extend) { ... }
```

`Toggle`・`SelectRange`・クリックでの選択は、同時に `Cursor` も更新する。`Clear` と別フォルダへの移動で `Cursor` を消す。`Prune` は表示から消えた `Cursor` を消す。

### 5.3 App：キーの分岐（`MainWindow.xaml.cs`）

```diff
     private bool HandleSelectionKey(Key key, KeyEventArgs e)
     {
+        if (NavKeyOf(key) is { } nav && (modifiers & ModifierKeys.Alt) == 0)
+        {
+            if (nav == NavKey.Right && modifiers == ModifierKeys.None && ActiveList.Columns == 1
+                && _vm.CursorFolder() is { } folder)
+            {
+                return OpenCascadeFromKeys(folder); // §3.3-1: the details view's → opens the folder's list
+            }
+
+            _vm.MoveCursor(nav, ActiveList.Columns, ActiveList.PageRows,
+                select: (modifiers & ModifierKeys.Control) == 0,
+                extend: (modifiers & ModifierKeys.Shift) != 0);
+            ActiveList.BringCursorIntoView();
+            return true;
+        }
+
+        if (key == Key.Space && (modifiers & ~ModifierKeys.Control) == ModifierKeys.None)
+        {
+            if (modifiers == ModifierKeys.None && _vm.CursorFolder() is { } folder)
+            {
+                return OpenCascadeFromKeys(folder); // §3.3-1
+            }
+
+            return _vm.SelectAtCursor(toggle: modifiers == ModifierKeys.Control);
+        }
+
+        if (key == Key.Apps || (key == Key.F10 && modifiers == ModifierKeys.Shift))
+        {
+            ShowKeyboardMenu(); // the selection's menu at the cursor's item; the folder's menu when nothing is selected
+            return true;       // handled: no system menu
+        }
+
+        if (key == Key.Enter && modifiers == ModifierKeys.Alt) { ... properties ... }
+        if (key == Key.D && modifiers == ModifierKeys.Control) { ... same as Delete ... }
```

```diff
         switch (key)
         {
+            case Key.Tab when (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _vm.HasTab:
+                e.Handled = true;
+                _vm.SelectAdjacentTab(back: (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
+                break;
+            case >= Key.D1 and <= Key.D9 when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
+                e.Handled = true;
+                _vm.SelectTabAt(key == Key.D9 ? int.MaxValue : key - Key.D1);
+                break;
             case Key.F when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
             case Key.F3 when Keyboard.Modifiers == ModifierKeys.None && _vm.HasTab:
+            case Key.E when Keyboard.Modifiers == ModifierKeys.Control && _vm.HasTab:
                 e.Handled = true; // like Explorer: the search box, its text selected
                 BeginSearch();
                 break;
+            case Key.F1 when Keyboard.Modifiers == ModifierKeys.None:
+            case Key.OemQuestion when Keyboard.Modifiers == ModifierKeys.Control:
+                e.Handled = true;
+                ToggleShortcutHelp(); // §3.3-2
+                break;
```

文字キーのジャンプは既存の `OnPreviewTextInput` に分岐を足す（`_creatingFolder` のときは現状どおり入力欄へ）。

### 5.4 App：キーボードで開くカスケード（§3.3-1）

```diff
 // CascadeController.cs
+    /// <summary>
+    /// Opens the folder's list at once from the keyboard (Space, the details view's →): the card's
+    /// overlay or the details row's anchor, as the hover would after its timer. The first ↓ picks
+    /// the first row (HandleKey). Not closed by the exit timer while the pointer is elsewhere.
+    /// </summary>
+    public void OpenFromKeyboard(CardViewModel card, FrameworkElement element) { ... }
```

### 5.5 App：ショートカット一覧とツールチップ（§3.3-2）

```diff
+// src/FolderDeck.App/Views/ShortcutCatalog.cs（名前は仮）
+internal static class ShortcutCatalog
+{
+    public static IReadOnlyList<ShortcutGroup> Groups { get; } = ...; // 見出し・キー・説明
+    public static string? KeysOf(string commandId) => ...;           // ツールチップの併記に使う
+}
```

一覧の表示は `MainWindow` の上に重ねるパネル（またはオーナー付きの小さいウィンドウ）。ツールチップは `MainWindow.xaml` の対応するボタンの `ToolTip` に「（キー）」を足す。

### 5.6 App：選択の合計サイズ（§3.3-3）

```diff
 // TabViewModel.cs
-            SelectionText = Selection.Count > 0 ? $"{Selection.Count:N0} 個を選択" : null;
+            SelectionText = Selection.Count > 0 ? SelectionSummary.Format(Selection.Count, files, bytes, hasFolders) : null;
```

整形（件数・サイズの単位・「フォルダを除く」）は Core の純粋な関数にしてテストする（既存のサイズ整形があれば再利用する）。

### 5.7 その他の変更対象（方針のみ）

| ファイル | 内容 |
|---|---|
| `MainViewModel.cs` | `MoveCursor`、`SelectAtCursor`、`CursorFolder`、`SelectAdjacentTab`、`SelectTabAt`。Ctrl + Insert / Shift + Insert は既存の A / C / X / V の分岐へ追加 |
| `CardGrid.xaml.cs`、`DetailsListView` | `Columns`、`PageRows`、`BringCursorIntoView`（仮想化された行へのスクロール） |
| `CardView`、`DetailsRowView` | カーソルの枠の表示 |
| `ContextMenuFactory.cs` | キーから開くときの表示位置（項目の左下） |
| シェル連携 | プロパティの表示（`SHObjectProperties` か `properties` 動詞） |

### 5.8 文書

| 文書 | 更新箇所 |
|---|---|
| `docs/specification.md` | 操作表、選択、キーボード、ステータスの記述 |
| `dev/docs/codebase_overview.md` | ファイルマップ（新規ファイル）、§3 の流れ（キーボード・カスケード）、§5 レシピ、§6 テスト対応表 |
| `README.md` | 「主な機能」にキーボード操作を追加 |

## 6. テスト

| 対象 | 件数の目安 | 内容 |
|---|---|---|
| `GridNavigation.Move` | 3〜4 | 端での停止、最終行が半端なときの ↓、PageDown、カーソルなしからの開始 |
| `GridNavigation.FindByPrefix` | 1〜2 | 折り返し、一致なし |
| `SelectionModel` のカーソル | 2〜3 | Shift での範囲、Ctrl での移動のみ、クリック後の起点の一致 |
| 選択の合計サイズの整形 | 1〜2 | フォルダを含む、サイズ不明のファイル |

App 層は自動テストがないので手動確認：`run.bat --data-dir E:\tmp\folderdeck-data` で §3.1・§3.3 の全キーを、カード表示と詳細表示の両方で確認。

## 7. リスクと懸念

| リスク | 対処 |
|---|---|
| 既存キーとの衝突（カスケードの矢印、Alt + ↑ ↓、入力欄） | 分岐の順序を現状のまま保ち、カスケードを先に通す。手動確認の項目に入れる |
| Space の意味が項目で変わる（フォルダ：開く、ファイル：選択） | 利用者の判断（2026-11-27）。フォルダの選択は矢印の移動で済む。Ctrl + Space は常に選択の切り替え |
| キーで開いたカスケードが、ポインターが外にあるため exit タイマーで即座に閉じる | キーで開いた一覧はポインターの出入りで閉じない状態にし、Esc・← ・別の操作で閉じる |
| 仮想化された一覧でのスクロール | 行単位の `ScrollIntoView`。10,000 件のフォルダで確認 |
| 文字キーと日本語入力（IME）の干渉 | IME がオンのときは変換中の文字を使わない。挙動は実機で要確認（未確認） |
| Space がボタンの実行と重なる | ボタンにフォーカスがあるときはボタンを優先 |
| Ctrl + Tab が現在はボタン間の移動として効いている | タブの切り替えに置き換える。ボタン間の移動は Tab / Shift + Tab が残る |
| F1 が他の用途（ヘルプ）と重なる | アプリに既存の F1 はない。入力欄の中でも一覧を出す（入力の妨げにならない） |
| ショートカット一覧と実装の食い違い | 表を 1 か所に置き、ツールチップと共有する。表の行に分岐の場所を残す |
| 合計サイズの再計算が選択のたびに走る | O(選択数)。Ctrl + A で 10,000 件でも追加の I/O はない |

## 8. 段階と完了条件

| 段階 | 内容 | 完了条件 |
|---|---|---|
| 1 | 一覧の移動・選択・文字ジャンプ | マウスなしで任意の項目を選び、Enter・F2・Delete まで行える |
| 2 | メニュー・プロパティ・別キー | Shift + F10 でシステム メニューが出ず、項目のメニューが出る |
| 3 | タブの切り替え、検索の別キー | Ctrl + Tab でタブが切り替わる。Ctrl + E で検索の入力欄に入る |
| 4 | キーボードのカスケード（§3.3-1） | フォルダで Space（詳細表示は → も）で一覧が出て、↓ Enter で中の項目を開ける |
| 5 | ショートカット一覧・ツールチップ・合計サイズ（§3.3-2、3） | F1・Ctrl + / で一覧が出る。ボタンのツールチップにキーが出る。選択で合計サイズが出る |

## 9. 利用者の決定（2026-11-27）

1. Shift + Delete は対象外のまま（元の SOW の提案どおり）。
2. Ctrl + Tab・Ctrl + 1〜9（タブの切り替え）は残す（元の SOW の提案どおり）。
3. キーボードのカスケード：Space は常に、→ は詳細表示だけ。
4. 合計サイズ：ファイルだけ合計（フォルダを含む選択は「フォルダを除く」と併記）。

## 10. 実装結果

状態：実装済み・未コミット（`feat/explorer-shortcuts`）。`dotnet build FolderDeck.sln -c Debug` は警告 0・エラー 0。実機での手動確認は一部（F1 の一覧とツールチップの文字だけ、UI オートメーションと SendKeys で）しか済んでいない（§3.3-4 のドロップは実機で未確認）。§5 の差分は提案差分のままで、実装との違いは下の 10.2 に文章で書く。

### 10.1 実装したもの

| 段階 | 内容 | 主な場所 |
|---|---|---|
| 1 | 矢印・Home / End・PageUp / PageDown でのカーソル移動（修飾キーなし・Shift・Ctrl・Ctrl + Shift）、Space / Ctrl + Space、文字キーのジャンプ（1 秒以内は連結）、キーで動かしたときだけ出る点線の枠 | Core：`Selection/GridNavigation.cs`（新規）、`SelectionModel`（`Cursor`・`CursorChanged`・`MoveCursor`）。App：`MainWindow.HandleSelectionKey` / `MoveCursorByKey` / `JumpToTypedName`、`MainViewModel.MoveCursor` / `MoveCursorToName` / `SelectAtCursor` / `CursorCard`、`IItemsView.ItemsPerRow` / `PageRows`、`CardViewModel.IsCursor`、`CardView` / `DetailsRowView` の点線 |
| 2 | Shift + F10・アプリケーション キーのメニュー（項目の左下、選択なしは表示中のフォルダ）、Alt + Enter のプロパティ、Ctrl + D、Ctrl + Insert / Shift + Insert | `MainWindow.ShowMenuFromKeys` / `OnPreviewKeyUp`、`ContextMenuFactory`（`keyPoint`）、`ShellContextMenu.ShowPropertiesAsync`、`MainViewModel.ShowProperties` |
| 3 | Ctrl + Tab / Ctrl + Shift + Tab、Ctrl + 1〜8 / Ctrl + 9（テンキーも）、Ctrl + E | `OnPreviewKeyDown` の switch、`MainViewModel.SelectAdjacentTab` / `SelectTabAt` |
| 4（§3.3-1） | フォルダの上の Space（両方の表示）と詳細表示の → でカスケードを開く。もう一度の Space で閉じる | `MainWindow.OpenCascadeFromKeys`、`CascadeController.OpenFromKeys` / `IsOpenOn` / `WindowPressed` / `NoteKeyMove`（`_byKeys`、`_keysPointer`、`PointerRestsSinceKeys`） |
| 5（§3.3-2、3） | F1 / Ctrl + / のショートカット一覧、ツールチップへのキーの併記、選択のファイルの合計サイズ | `Views/ShortcutCatalog.cs`・`ShortcutHelpWindow.xaml(.cs)`（新規）、`MainWindow.xaml` / `FolderTabStrip.xaml` の `{views:ShortcutTip}`、Core の `Text/SelectionSummary.cs`（新規）、`TabViewModel.ApplyItemFlags` |
| 6（§3.3-4） | 実フォルダを表示しているタブの一覧（カードの間・最後のカード・行の後ろ・詳細表示の最後の行より下、ほかに列の見出し・スクロール バー・ファイルのカード・行）へのドロップで、表示中のフォルダへ移動・コピー。キーの規則はフォルダへのドロップと同じ。全部がすでに表示中のフォルダにあるときは Ctrl だけで複製（「名前 - コピー」、Ctrl + Z で消える、複製を選択）、ほかのキーでは受け付けない。一部だけのときは Ctrl なしは残りを移動・コピーして「除きました」、Ctrl では全部コピーして既存は名前を変える。エクスプローラーからのドロップは一覧の上なら移動・コピー、一覧の外は従来どおり登録。最近の履歴・よく使う項目・PC の一覧は従来どおり | Core：`FileDrag.DropEffectForShownFolder` / `CopiesInPlace`、`TransferOptions.Drop(kind, copyInPlace)`。App：`MainWindow.FolderTargetAt`（`ShownFolderTarget`・`RefusedInFolder`、`FolderTarget` の `ShownFolder` / `OnFile` / `Refused`）・`FolderDropOf`（`FolderDrop.CopyInPlace`）・`OnDrop`、`InAppDrop.CopyInPlace`、`MainViewModel.FinishDrag` / `DropAsync(..., copyInPlace)` |

テスト：`GridNavigationTests`（新規。5 メソッド、インラインのケースを含め 19 件）、`SelectionModelTests` に 4 件（カーソルの移動、クリック後の起点、合計サイズの整形、使えない項目の上のカーソルを残す `Prune`）。`--filter "SelectionModelTests|GridNavigationTests"` で 31 件すべて成功。段階 6 では `FileDragTests` に 2 メソッド（理論 `DropEffectForShownFolder_ItemsAlreadyThere_OnlyCtrlCopies` 5 件と `DropEffectForShownFolder_SomeAlreadyThere_FollowsTheFolderRules_AndOnlyCtrlCopiesInPlace`）、`TransferRulesTests` に 1 メソッド（`Decide_DropCopyInPlace_RenamesWhatIsAlreadyThere_ButNeverForAMove`）を足し、`--filter "FullyQualifiedName~FileDragTests|FullyQualifiedName~TransferRulesTests"` で 72 件すべて成功。App 層（ドロップ先の判定）は自動テストなし。全体のテストは CI に任せた。

文書：`docs/specification.md`（§4 の操作表、§6.1、§7、§9 の選択と表示、§10、§13 のドロップ、§17、§18 の対象の操作、新しい §19 キーボード操作）、`dev/docs/codebase_overview.md`（目次、§1、§2 のファイルマップ、§3.3・§3.5・§3.6・§3.7・§3.10 の流れ、§4 のドラッグ、§5 のレシピ、§6 のテスト対応表、§7 の落とし穴）、`README.md`（主な機能）。

### 10.2 提案差分との違い

- **`SelectionModel.MoveCursor`**：2 つ目の引数は押したキーでなく行き先の項目のパス。行き先は呼ぶ側（`MainViewModel.MoveCursor`）が `GridNavigation.Move` で決める。ほかに、`SelectionSnapshot` にカーソルを足し（3 つ目の省略できる引数）、`Prune` にカーソルだけを確かめる 2 つ目の一覧を足した（見つからない・応答がない項目の上のカーソルを、再読み込み・並べ替えで消さないため）。
- **`GridNavigation.FindByPrefix`**：提案にない規則を 1 つ足した。同じ文字の繰り返し（「ss」）で始まる名前がなければ、1 文字と同じに次の項目へ進む。
- **View の名前**：`ActiveList.Columns` は `ItemsPerRow` にした（`DetailsListView.Columns` が列の幅の依存プロパティとしてすでにあるため）。`BringCursorIntoView` は作らず、既存の `IItemsView.BringItemIntoView` を使った（仮想化された行にも届く）。`PageRows` は実体化した行の高さを測る共通の `ItemsViewPaging.PageRows`。
- **`CursorFolder`**：作らず、`MainViewModel.CursorCard()` の `IsFolderLike` を `MainWindow` で確かめる。開くものがないフォルダ（見つからない・応答なし・件数で空）では、キーはファイルと同じ（Space は選択、→ は移動）。
- **詳細表示の → の判定**：`ActiveList.Columns == 1` ではなく `ReferenceEquals(ActiveList, Details)`。狭いウィンドウではカードグリッドも 1 列になり、カード表示の → は移動のままにする決定（§9 の 3）と食い違うため。
- **カスケードの入口**：`OpenFromKeyboard(card, element)` 1 つでなく、`OpenFromKeys(CardView)`（オーバーレイ）と `OpenFromKeys(hover, ICascadeAnchor)`（詳細表示の行の名前の横）の 2 つ。閉じる判定に `IsOpenOn`、一覧の外の押下で閉じる `WindowPressed`、キーのあとの止まったポインターの MouseEnter を無視する `NoteKeyMove` を足した。開いても選択は変えない。最初の行は選ばない（提案のとおり、最初の ↓ が先頭の行）。`OpenOverlayAsync` は件数が読み込まれていないカードでは空の判定をしない（キーは件数を待たない）。
- **← の扱い**：§7 の「Esc・← で閉じる」に合わせ、キーで開いた一覧の 1 段目の ← は一覧を閉じる（ホバーで開いた一覧の 1 段目の ← は今までどおり何もしない）。
- **止まったポインターの扱い**：キーで開いた一覧だけでなく、キーでの移動・文字キーのジャンプのあとは、ポインターがドラッグ距離より動くまでホバーの MouseEnter を無視する（キーのスクロールでポインターの下に来た項目の一覧が開き、矢印を奪うため）。副作用として、キーのあと動かさないポインターの下でホイールでスクロールしても、動かすまで一覧は開かない。
- **Ctrl + D**：Delete の分岐の条件を広げただけ（別のヘルパーにしていない。動作は同じ）。
- **タブのキー**：Ctrl + Tab の case は Ctrl と Shift 以外の修飾キー（Alt など）つきでは受けない。テンキーの Ctrl + 1〜9 も足した。メニューの表示中は効かせず、アドレス欄の入力はやめてから切り替える。検索欄・アドレス欄の中でも効く。
- **ショートカット一覧**：`ToggleShortcutHelp` は `ShowShortcutHelp` にした（オーナー付きのモーダルのウィンドウで、閉じるのはウィンドウ自身）。重ねるパネルにはしなかった。F1 の判定に Ctrl + テンキーの / も入れた。`KeysOf(string commandId)` は enum の `ShortcutId` にした。
- **ツールチップ以外のキーの表示**（提案になかったもの）：アプリの右クリックメニューの項目の右にキーを出す（`MenuEntry.Keys` → `InputGestureText`、`Styles.xaml` の `MenuItem` にキーの列）。キーが同じ項目に効くときだけ。副作用として、WPF の入力欄の既定のメニューにも Ctrl+C などが出る。
- **ツールチップの細部**：タブの × の「（Ctrl + W）」は表示中のタブだけ（Ctrl + W は表示中のタブを閉じるため）。検索語の表示の × のツールチップは「検索を解除（Esc）」から「検索を解除」に直した（その × の場所では Esc は検索を解除しない）。
- **合計サイズの計算**：§4 は「O(選択数)」としたが、`TabViewModel.ApplyItemFlags` の既存の 1 回のループ（O(表示中の項目数)、I/O なし）の中で足す。`Show`（再読み込み・並べ替え・自動更新）でも呼ぶので、選択したファイルのサイズが変わったときも合計が変わる。
- **提案になかった決定**：
  - `MainViewModel.ClearSelection`（Esc・普通のクリック・空白の押下）はカーソルを残す（エクスプローラーと同じ）。カーソルを消すのは別のフォルダの表示だけ。
  - 文字の名前を打っている途中（1 秒以内）の Space は選択でなく名前の一部。矢印などの移動・Esc・カスケードを開くキー・マウスの押下で、打った名前はリセットされる。
  - マウスのボタンを押すと（ツールバー・カスケードの行の上でも）、カーソルの点線は消える（位置は残る）。
  - Home / End / Page キー・文字キーは、開いているカスケードを閉じてから動く。
  - Shift + F10・アプリケーション キーは、カスケードが開いていれば閉じて一覧のメニューを出す（カスケードの行のメニューではない）。押下と離しを両方飲み込み、ウィンドウのシステム メニューを開かせない。
  - Alt + Enter で、ドライブ 1 つは背景のモードでそのドライブのプロパティ、ドライブを含む複数は案内（「ドライブのプロパティは 1 つずつ表示してください。」）。最上位の一覧で選択がないときは何もしない。
- **§3.3-4 のドロップ（決定との違い・決定になかった判断）**：
  - 先の範囲：決定は「空いているところ（カードの間・最後の行の後ろ・詳細表示の最後の行より下）」。実装は `ContentHost` の中のフォルダのカード・行以外すべて（ほかに列の見出し・スクロール バー・ファイルのカード・行。エクスプローラーと同じく、ファイルの上に落とすとそのファイルのフォルダ）。理由と未決の点は 10.3。
  - 複製のキー：Ctrl **だけ**（`FileDrag.CopiesInPlace`）。Ctrl + Shift・Alt・右ボタンは表のとおり受け付けない。キーなしで別のボリュームの元を含んでコピーになるときも、すでにある項目は複製しない（`kind == Copy` からでなく、キーから印を運ぶ）。
  - 一部だけがすでにあるとき（決定に書いていない場合）：Ctrl なしは残りを移動・コピーして「除きました」、Ctrl では全部コピーし、すでにある項目は名前を変える。
  - ドラッグした項目そのもの（ドラッグしたファイルのカード・行）の上で離すと何もしない（Ctrl でも。そうしないと Ctrl で選択全部を複製するため）。
  - 実フォルダのタブで先にならないとき（読み込み中・読み込めなかった、処理中・見つからない・応答がないフォルダのカード・行）は、受け付けない（`Refused`）。エクスプローラーからのドラッグもそこでは登録にならない（一覧の中に「登録」の穴を作らないため）。登録のままなのは固定タブ（最近の履歴・よく使う項目・PC）の一覧と、タブがないときだけ。
  - 強調表示はしない（一覧全体が先で、カードの強調は「このフォルダの中へ」と読めるため）。サイドバーの上端の案内は「「表示中のフォルダ名」へ移動 / コピー」。カーソルの効果（`e.Effects`）は計算した効果に合わせる。
  - 先の条件は貼り付けと同じ `MainViewModel.PasteFolderOf(null)`（`TabViewModel.TransferTargetPath`）。転送も貼り付けと同じ経路（`TransferIntoAsync` → `Decide`（`AllowSameFolderCopy`）→ `FileTransfer`（`FOF_RENAMEONCOLLISION`）→ `RecordFileOp` → `ReloadAndSelectArrivedAsync`）。

### 10.3 見送ったもの・残した問題

- **§3.3 の 4 の未決の点（利用者の判断待ち）**：
  - カスケード（オーバーレイ・フライアウト）の行のドラッグは、始めると一覧が消える（`CascadeController.BeginDrag`）。フォルダのタブでそのまま離すと、その項目（サブフォルダの中）を表示中のフォルダへ移動する（Ctrl + Z で戻せる）。以前もフォルダのカードの上で離せば移動していた。カスケードの行のドラッグを除く・ポインターが一覧から出てから先にする、などの規則は作っていない。
  - 先の範囲が決定（空いているところ）より広い：ファイルのカード・行、列の見出し、スクロール バーも表示中のフォルダ。カード表示では一覧のほとんどがファイルのカードなので、外すと落とせるところがほとんど残らず、null にすると外からのドラッグの「登録」の穴になるため、エクスプローラーと同じ範囲のままにした。ショートカット（`.lnk`）や実行ファイルの上に落としても、リンク先やプログラムではなく表示中のフォルダに入る。
- 外からのドラッグでは DragOver が FileDrop の生のパス（長いパスでは 8.3 形式）を比べ、`DropAsync` は長い名前に戻してから判定するので、そうした項目ではカーソルが Move でも結果が「すでにそのフォルダにあります」になりうる（フォルダへのドロップにも前からある食い違い）。
- ドラッグ中の自動スクロールは並べ替えのドラッグだけで、フォルダ・一覧へのドロップには足していない（以前からない）。
- 検索欄・アドレス欄・名前の入力欄の中の Shift + F10 は変えていない（入力欄のメニュー。システム メニューも出るなら既存の問題。必要なら `MainWindowHook` で lParam -1 の WM_CONTEXTMENU を飲み込む案）。
- 最近の履歴・よく使う項目のファイルのカードは、サイズを後から読む（`CardViewModel.LoadFileAsync`）。選んだあとにサイズが分かっても、選択か一覧が変わるまで合計に入らない（直すにはカードからタブへの通知が要り、得るものより重い）。
- ショートカット一覧を開くツールバーのボタンはない（F1 / Ctrl + / だけ）。一覧の高さはメイン ウィンドウの高さまで。
- F1 / Ctrl + / は名前の入力中は効かない（入力欄の上にモーダルを出すと確定・終了に絡むため、意図して残した）。
- Alt + Enter で表示するものがないとき（最上位の一覧で選択なし）はキーを処理しないので、`DefWindowProc` に届いて音が鳴るかもしれない（選択なしの Delete と同じ）。
- キーで開いた一覧は、ポインターを少しだけ動かしたり、ほかのカード・行に入らずに出たりしても閉じない（クリック・Esc などまで残る）。ポインターが開いたカードの上で止まっていると、オーバーレイの行が下に来てもホバーは始まらない（ポインターを動かすまで）。
- マウスの押下で点線を消したあと、キーでカスケードを開いても点線は戻らない（`ShowsCursor` は変えない）。
- スクリーン リーダー向けのカーソルの読み上げは対象外のまま（ADR-1 の不利な点）。

### 10.4 手動確認の項目（未実施のものが多い）

`run.bat --data-dir E:\tmp\folderdeck-data` で、カード表示（縦長・正方形）と詳細表示の両方、ライト・ダークの両方で確認する。

| 対象 | 確認すること | 状況 |
|---|---|---|
| カーソル | 矢印・Home / End・PageUp / PageDown（修飾キーなし・Shift・Ctrl・Ctrl + Shift）、Shift + クリック後の Shift + ↓、半端な最後の行への ↓、10,000 件のフォルダでのスクロールと `PageRows` の正確さ、見つからない項目の上のカーソル、Esc・クリック後に残る位置、別フォルダで消えること | 未確認 |
| 枠 | 点線がキーのときだけ出る、マウスの押下で消える、両テーマでの見え方 | 未確認 |
| Space | ファイルで選択、Ctrl + Space で切り替え、ツールバーのボタンにフォーカスがあるときはボタン、押し続けで 1 回だけ | 未確認 |
| 文字キー | 次の項目へ・同じ文字の繰り返し・1 秒以内の連結・名前の中の Space・日本語入力（IME）での確定した名前 | 未確認 |
| キーのカスケード | Space（両方の表示）・詳細表示の → で開く、もう一度の Space で閉じる、↓ Enter で中の項目を開く、← で 1 段目が閉じる、ポインターが別のカード・行の上で止まっていても開いたまま、キーのスクロールのあとポインターの下の一覧に替わらない、まだ実体化していない行（大きいフォルダ）で開いた直後に閉じない、詳細表示の一覧が名前の横に出る、件数の読み込み前のカード、ツールバー・サイドバーのクリックで閉じる、押し続けの Space でちらつかない | 未確認 |
| メニュー | Shift + F10 でシステム メニューが出ない、↓ Enter でメニューの項目を選べる、項目・行の左下の位置、選択なしは表示中のフォルダのメニュー、「Windows のメニュー」の設定での位置、拡張メニューにならない | 未確認 |
| プロパティ | 1 つ・複数・ドライブ 1 つ・選択なし（フォルダ）、ダイアログが開いたまま使える | 未確認 |
| 別のキー | Ctrl + D・Ctrl + Insert・Shift + Insert・Ctrl + E | 未確認 |
| タブ | Ctrl + Tab で端の循環とボタンへのフォーカス移動がないこと、Ctrl + 1〜9 とテンキー、検索欄・アドレス欄の中から | 未確認 |
| 一覧 | F1・Ctrl + / で開閉、Esc・「閉じる」、検索欄・アドレス欄の中から、日本語配列のキーボードの Ctrl + / | F1・Ctrl + / ・Esc の開閉とダークの見た目だけ確認済み |
| ツールチップ・メニューのキー | ツールバー・アドレス欄・検索語の表示・タブの × の文字、アプリのメニューの右のキー（両テーマ）、入力欄の既定のメニュー | ツールチップの文字だけ確認済み |
| 合計サイズ | ファイルだけ・フォルダを含む・フォルダだけ・サイズ不明のファイル、Ctrl + A で 10,000 件 | 未確認 |
| 既存のキーとの衝突 | カスケード表示中の矢印、Alt + ← → ↑ ↓、入力欄の中、名前の入力中、範囲選択のドラッグ中、メニューの表示中 | 未確認 |
| 一覧へのドロップ（§3.3-4） | カードの間・最後のカードの後ろ・詳細表示の最後の行より下・ファイルの上で移動・コピー（同じドライブ・別のドライブ・Shift / Ctrl）、Ctrl の複製（「- コピー」の名前、Ctrl + Z、複製の選択）、一部だけすでにあるとき（「除きました」）、ドラッグした項目そのものの上で何も起きない（Ctrl でも）、エクスプローラーから一覧の上は移動・コピーでサイドバーの空き・タブ列は登録、読み込み中・読み込めなかったタブでは受け付けず登録にもならない、最近の履歴・よく使う項目・PC は従来どおり、カーソルの効果と案内の文字、カスケードの行のドラッグをそのまま離したとき（10.3 の未決の点） | 未確認 |

## ADR-1：一覧のキーボード移動に WPF のフォーカスを使わない

- **状況**：項目は `Focusable="False"`。UI オートメーションの負荷（項目 1 つにピア 1 つ）と、ホバー主導のカスケードがこの前提で作られている。
- **決定**：項目をフォーカス可能にせず、`SelectionModel` に論理的なカーソルを持ち、キーはウィンドウで受ける。
- **理由**：仮想化で項目のコンテナが作り直されてもカーソルが失われない。既存のキー処理の一括管理を保てる。Core でテストできる。
- **不利な点**：スクリーン リーダーは WPF のフォーカスを追うので、カーソルの移動を読み上げない。読み上げには別途オートメーションの通知が必要で、本 SOW の対象外。
- **却下した案**：項目を `Focusable="True"` にする。標準の動作は得られるが、仮想化でのフォーカス消失、カスケードとの競合、オートメーションの負荷の再検証が必要で、影響範囲が大きい。

## ADR-2：キーボードのカスケードは Space（と詳細表示の →）で開く

- **状況**：利用者の依頼は「フォルダで → またはスペースで中身の一覧」。元の SOW では → はカーソルの横移動、Space は選択。
- **決定**：フォルダ上の Space は常に一覧を開く。→ は 1 列の詳細表示でだけ一覧を開き、カード表示では横移動のまま（2026-11-27 利用者の判断）。
- **理由**：カード表示の → を奪うと、フォルダの上で横に移動できなくなる。詳細表示は 1 列なので ← → に移動の意味がなく、ツリーの「展開」に近い → が自然。Space での選択は矢印の移動（選択も移る）で代わりが利く。
- **不利な点**：Space の意味が項目の種類で変わる。フォルダをカーソル位置で選び直すには矢印か Ctrl + Space が要る。
- **却下した案**：→ を常に開くにする（カード表示の一貫性が下がる）。Space だけにする（依頼の → を詳細表示でも使えない）。
