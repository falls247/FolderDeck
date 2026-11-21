---
id: codebase_overview
title: コードベース概要（改修の入口）
summary: 層構成・主要クラスの責務・処理の流れ・スレッド規約・改修レシピ・テスト対応表。改修や調査の最初に読む地図
tags: [architecture, overview, wpf, core, recipe, testing, threading, persistence]
status: 現行（実装に追従して更新する）
updated: 2026-11-21
---

# コードベース概要（改修の入口）

改修・調査はここから始める。リポジトリ全体を読まず、[§5 改修レシピ](#5-改修レシピ) → [§2 ファイルマップ](#2-ファイルマップ) で対象を絞り、該当ファイルの該当メンバーだけ読む。

## 0. このドキュメントの位置づけ

| 文書 | 内容 | 読むとき |
|---|---|---|
| 本書 | 開発者向けの地図（どこに何があり、どう流れるか） | 毎回最初 |
| [README.md](../../README.md) | 利用者向けの挙動、BAT、データファイルの表、「実装メモ」（実装上の判断） | 挙動の正解を確かめるとき |
| 旧仕様書 `SPEC_20261121_native_folder_app.md`（削除済み） | 最初の実装の元になった仕様（約 1,100 行）。コードコメントの `§9.6` などはこの節番号 | 根拠を確かめるときだけ `git show 1122384:dev/docs/SPEC_20261121_native_folder_app.md` で出し、`§9.6` などを検索する（通読しない） |
| [_topics.yml](_topics.yml) | `dev/docs` の目録と各文書の目次（YAML） | 読む文書・節を選ぶとき |

- 旧仕様書は最初の実装時点のもの。後から追加・変更した機能（タブごとの並べ替え、ドラッグでの並べ替え、他アプリへのドラッグ、Office・テキストのプレビュー、エクスプローラーの新しいタブで開く、カードの形、📌 のクリックでの解除、ごみ箱への削除、「エクスプローラーで開く」の廃止）は載っていない。挙動は README と実装が正。
- 行番号は書かない（すぐずれるため）。ファイル名とメンバー名で示す。
- 本書と実装が食い違ったら実装が正。気づいたら本書を直す（[§9](#9-本書の更新ルール)）。

## 1. 全体像

- WPF + .NET 10（`net10.0-windows`、x64）、C#（`LangVersion latest`、`Nullable enable`、`ImplicitUsings enable`。共通設定は `Directory.Build.props`）
- パッケージ：CommunityToolkit.Mvvm 8.4.2（`[ObservableProperty]` / `[RelayCommand]` のソース生成）、Microsoft.Windows.CsWin32 0.3.371（各プロジェクトの `NativeMethods.txt` から P/Invoke を生成）、テストは xUnit 2.9.3
- 配布：single-file exe（ReadyToRun）。`publish.bat` → `dist\FolderDeck.exe`（self-contained・圧縮 `EnableCompressionInSingleFile`、約 65 MB）、`publish.bat framework` → `dist\framework-dependent\FolderDeck.exe`（.NET 10 デスクトップ ランタイムが必要、約 2 MB）。圧縮は csproj で `SelfContained == true` のときだけ有効。WPF はトリミング非対応のため使わない

### 1.1 プロジェクトと依存方向

```
src/FolderDeck.App   WPF（exe 名 FolderDeck、名前空間 FolderDeck.*）
   │  参照
   ▼
src/FolderDeck.Core  UI 非依存のロジック（名前空間 FolderDeck.Core.*）。WPF を参照しない
   ▲  参照（InternalsVisibleTo あり）
tests/FolderDeck.Core.Tests  xUnit。Core のみ。App 層の自動テストはない
```

- ロジックは Core に置き、純粋関数にしてテストする方針（並べ替え・ランキング・パス・保存・プレビュー用の読み取りなど）。App は WPF とシェル連携に限る。
- 大きいファイル（全文を読まず `// ---- 見出し ----` の区切りで目的の節だけ読む）：`Views/CascadeController.cs`（約 1,400 行）、`ViewModels/MainViewModel.cs`（約 1,090 行）、`Core/Listing/SheetReader.cs`（約 820 行）、`Shell/ExplorerTabs.cs`（約 750 行）、`ViewModels/TabViewModel.cs`（約 630 行）、`Views/CardGrid.xaml.cs`（約 590 行）

### 1.2 起動と終了（`App.xaml.cs`）

1. `SingleInstance.Acquire()`：2 つ目の起動は既存ウィンドウを前面に出す合図を送って終了（名前付き Mutex + イベント）
2. `--data-dir` を解析（既定 `%LOCALAPPDATA%\FolderDeck`）→ `AppLog.Initialize` → `SessionMonitor.Start`（前回の異常終了を報告）
3. `ThemeManager.Apply`
4. `DataStores.LoadAll` を `Task.Run` で実行（UI スレッドで JSON を読まない）
5. `AppServices` → `MainViewModel` → `MainWindow` → `RestorePlacement` → `Show`
6. 各ストアの `SaveFailed` をメッセージ行のエラーに接続、`ReportLoadProblems`（`Recovered` は警告、`Failed` はエラー）
7. `MainViewModel.InitializeAsync`：各タブの現在地を `settings.json` から復元し、最後に開いていたタブを読み込む

終了（`OnExit`）：`Stores.FlushAll` → 各サービスの `Dispose` → `SessionMonitor.MarkCleanExit`。
未処理例外：UI スレッドと未監視タスクはログ＋ダイアログ（5 秒に 1 回まで）で継続、`AppDomain` の例外はログを書いて終了。

### 1.3 オブジェクトの所有関係

```
App
├─ AppServices（合成ルート。全 ViewModel で共有）
│   ├─ Stores: DataStores ─ Registered / History / Pins / Excluded / Settings / CardOrders
│   ├─ Listings: FolderListingCache（TTL 60 秒・1,500 件・VolumeThrottle）
│   ├─ Covers: TtlCache<フォルダ, カバー画像パス>（"" = なし）
│   ├─ Thumbnails: ThumbnailService（STA ワーカー 4 本・1 回 5 秒・LRU 512 件 / 192 MB）
│   ├─ Icons: IconCache（一覧の行のアイコン）
│   ├─ Shell: StaWorkerPool(1)（.lnk の読み取りなど、その他のシェル呼び出し）
│   └─ DetailGate: SemaphoreSlim(8)（カード詳細の同時読み込み数）
├─ MainViewModel
│   ├─ Tabs: TabViewModel × 3（Registered / Recent / Frequent）→ Cards: CardViewModel[]
│   └─ Message: MessageBarViewModel
└─ MainWindow（IMainView, ICardHost を実装）
    ├─ CardGrid → CardRow（列数ぶんのカード）→ CardView（DataContext = CardViewModel）
    ├─ ContextMenuFactory
    └─ CascadeController
        ├─ Level 0: OverlayView（カードのサムネイル部分に重ねる）+ EntryListViewModel
        ├─ Level 1…: Popup + FlyoutView + EntryListViewModel
        └─ Preview（最大 1 つ）: Popup + PreviewView + PreviewViewModel
```

- ViewModel から View への依存は `IMainView`（ダイアログ、スクロール位置、カスケードを閉じる、シェルのダイアログの親にするウィンドウハンドル `WindowHandle`）だけ。View からの通知は `ICardHost`（カードのホバー・クリック・右クリック・× ・📌（`CardUnpin`）・ドラッグ）を `MainWindow` が受ける。

## 2. ファイルマップ

### 2.1 Core（`src/FolderDeck.Core/`）

| フォルダ | ファイル | 責務 |
|---|---|---|
| Infrastructure | `Abstractions.cs` | `IClock` / `SystemClock`、`IFileSystem`、`FsEntry`、`PathKind`。テストで差し替える境界 |
| | `RealFileSystem.cs` | `IFileSystem` の実装 |
| | `AppLog.cs` | `logs\app.log`（5 MB で `app.log.1` へ）。例外を投げない。**ファイルの中身は書かず、パスと例外だけ**。行形式は `tools/analyze-log.ps1` が解析する |
| | `SessionMarker.cs` | `logs\session.json`（起動中だけある記録）の読み書き |
| Layout | `CardShape.cs` | カードの形（`Portrait` = サムネイル 3:4 / `Square` = 1:1）、`settings.json` の値（`portrait` / `square`）との変換、`ThumbHeight`（幅からサムネイルの高さ） |
| Listing | `FolderListing.cs` | `FolderLister.List`：直下の列挙と並び（フォルダ→ファイル、自然順）、`ListingStatus` |
| | `FolderListingCache.cs` | パス単位の TTL キャッシュ。同じパスの同時要求は 1 回の列挙を共有し、待つ側が全員キャンセルすると列挙も止める。`Clear` / `Invalidate` |
| | `VolumeThrottle.cs` | ボリュームごとに同時 I/O を制限（ハングした共有がローカルドライブを巻き込まない） |
| | `Existence.cs` | `ProbeAsync` / `CheckAsync` / `FilterProbedAsync`：タイムアウト付きの存在確認と更新日時 |
| | `CoverFinder.cs` | フォルダカードのカバー画像の探索（§6.4） |
| | `TtlCache.cs` | 汎用の TTL キャッシュ（件数上限つき） |
| | `PreviewKinds.cs` | 拡張子 → `PreviewKind`（None / Image / Presentation / WordDocument / Spreadsheet / Text）。**対象拡張子の一覧はここ** |
| | `FileContent.cs` | `IsCloudOnly`：OneDrive の「オンラインのみ」を属性だけで判定（開かない） |
| | `TextPreview.cs` | テキストの先頭 60 行 / 64 KB。BOM → UTF-8 → Shift_JIS の順に判定、バイナリは null |
| | `OfficeText.cs` | Word / Excel 抜粋の公開 API（`ReadWord` / `ReadSheet`）と結果の record |
| | `OpenXmlPackage.cs`（internal） | zip を共有モードで開く、DTD 禁止、展開量の上限（zip 爆弾対策） |
| | `WordReader.cs` / `SheetReader.cs`（internal） | 本文の段落 / 最初の表示シートの左上。`SheetFormat` が一般形式と日付の表示 |
| | `OfficeThumbnail.cs` | ファイル内に保存された縮小版（`docProps/thumbnail.*`）の読み取り |
| Ordering | `CardSortMode.cs` | 5 つの並び方と `settings.json` の値（`manual` / `name_asc` …）の変換、`CardSortModes.All` |
| | `CardOrdering.cs` | `ComparerFor`、`FoldersFirst`、`SortKeepingPinned`（ピン留めは位置を固定）、`KnownTime` |
| | `ManualOrder.cs` | 保存済みのドラッグ順を新しいリストへ適用（新規項目は元の並びで直前の項目の後ろ） |
| Paths | `PathTools.cs` | 入力の整形、正規化、比較（`PathEquals`）、表示名、`JoinRel` / `IsSafeRel`、`ForShell`（長いパス→8.3） |
| | `PathSet.cs` | 大文字小文字を区別しない正規化パスの集合（ピン判定など） |
| | `NaturalComparer.cs` | エクスプローラーと同じ自然順（`StrCmpLogicalW`） |
| Ranking | `RecentMerger.cs` | Windows の最近使った項目 + 利用記録 + 除外を統合（§10.2） |
| | `FrequentRanker.cs` | 頻度 × 新しさのスコア、ピン留めは別枠で先頭（§10.3） |
| Stores | `StoreBase.cs` | 読み込み・保存の共通処理（[§3.8](#38-永続化)） |
| | `JsonFile.cs` | JSON の読み書き（snake_case、原子的な書き込み、壊れたファイルの退避、時刻の寛容な読み取り） |
| | `DataStores.cs` | 6 つのストアの束。`All` / `LoadAll` / `FlushAll` |
| | `Models.cs` | ドメインの record と JSON の DTO |
| | `RegisteredFoldersStore.cs` | `registered_folders.json`（imgtool と同じ形式）。`Register` / `RegisterMany` / `Remove` / `Reorder` |
| | `HistoryStore.cs` | `history.json`（利用記録）。`Record` / `Remove` / `Clear` |
| | `PinStore.cs` | `pins.json`。`Pin` / `Unpin` / `IsPinned`、`Changed` イベント |
| | `ExcludedStore.cs` | `excluded.json`（最近の履歴から外した項目） |
| | `SettingsStore.cs` | `settings.json`。`AppSettings`（既定値・`Normalize`・`Clone`）。カードの形は文字列の `CardShape`（`card_shape`） |
| | `CardOrderStore.cs` | `card_order.json`（最近の履歴・よく使う項目のドラッグ順、各 1,000 件まで） |
| Text | `Formatting.cs` | 相対時刻、日時、ファイルサイズの表示 |
| Transfer | `FileDrag.cs` | 他アプリへのドラッグの規則：可否、`DropList`（260 文字以上→8.3）、自アプリ由来の判定、`IsDragGesture` |

### 2.2 App（`src/FolderDeck.App/`）

| フォルダ | ファイル | 責務 |
|---|---|---|
| （直下） | `App.xaml(.cs)` | 起動・終了・未処理例外（[§1.2](#12-起動と終了appxamlcs)）。`App.xaml` の MergedDictionaries の 0 番がテーマ |
| | `app.manifest` / `NativeMethods.txt` | マニフェスト（§4.3）/ CsWin32 で生成する Win32 API の一覧 |
| Services | `AppServices.cs` | 合成ルート（[§1.3](#13-オブジェクトの所有関係)）。`ShowHidden` を変えるとキャッシュを消す |
| | `PreviewTextCache.cs` | プレビューの抜粋（Word 段落・シートのセル・テキスト行）を裏のスレッドで読み、LRU で保持。5 秒で打ち切り、失敗はキャッシュしない |
| | `SessionMonitor.cs` | `lifecycle:` ログ（閉じた理由）と前回の異常終了の報告（イベントログも調べる） |
| Shell | `ShellInterop.cs` | Win32 / COM 呼び出しの集約：シェル画像（`IShellItemImageFactory`）、`GetDIBits`、システムイメージリスト、モニターと DPI、`SHAddToRecentDocs` など |
| | `StaWorkerPool.cs` | シェル / COM 用の STA ワーカー。タイムアウトした呼び出しは放棄し、ワーカーを作り直す |
| | `ThumbnailService.cs` | カードとプレビューの画像（`ThumbSource` / `ThumbMode` / `LruCache`） |
| | `IconCache.cs` | 一覧の行のアイコン（拡張子ごとに共有できるものは共有） |
| | `OfficeCover.cs` | `OfficeThumbnail.ReadEmbedded` を WPF の画像にする |
| | `ShellPreviewSupport.cs` | 「Windows がサムネイルを作れるか」を拡張子ごとに 1 回だけ確認（`AssocQueryString`）。`IsPreviewable` |
| | `ShellActions.cs` | 既定のアプリで開く、エクスプローラーで表示（`ShowInExplorer`、親フォルダで選択。ファイル用）/ 開く（`OpenInExplorer`。フォルダの「エクスプローラーで表示」とデータフォルダを開くとき）、パスをコピー、実行形式の判定 |
| | `RecycleBin.cs` | ごみ箱への移動（`MoveAsync`）。`IFileOperation` を操作ごとの専用 STA スレッドで実行し、進行状況シンクの `PreDeleteItem` で「ごみ箱に入らない（完全に削除される）」項目を止める。結果は `RecycleOutcome`（`Recycled` / `Canceled` / `NotRecyclable` / `Failed`） |
| | `ExplorerTabs.cs` | エクスプローラーの新しいタブで「表示」（ファイル：`ShowAsync`＝親フォルダで選択、フォルダ：`OpenAsync`＝そのフォルダ。Windows 11 22H2+、非公開の `WM_COMMAND 0xA21B`。失敗時は `ShellActions` の新しいウィンドウへ） |
| | `WindowsRecent.cs` | `%APPDATA%\Microsoft\Windows\Recent\*.lnk` の読み取り、記録が有効かの確認 |
| | `FileDragOut.cs` | ドラッグ用 `DataObject` の作成、`DoDragDrop` の実行、`IsDragging` |
| | `SingleInstance.cs` / `ThemeManager.cs` | 単一インスタンス / Windows のライト・ダーク追従 |
| ViewModels | `MainViewModel.cs` | タブ切り替え、移動と履歴（戻る・進む、最大 100）、開く、右クリックの各操作（ごみ箱への削除 `CanDelete` / `DeleteAsync` を含む）、登録、並べ替え・ドラッグ順の保存、ピン、カードの大きさと形（`CardSize` / `CardShape`）、設定ダイアログ |
| | `TabViewModel.cs` | 1 タブの現在地・カード・読み込み・並び（[§3.1](#31-タブの読み込み)） |
| | `CardViewModel.cs` | 1 枚のカード。`CardKind`、`BadgeMode`、段階読み込み（`Realize` / `Unrealize`） |
| | `EntryListViewModel.cs` | カスケード 1 階層（`EntryListViewModel`）と行（`EntryRowViewModel`、`ListState`） |
| | `PreviewViewModel.cs` | プレビューの内容（`PreviewContent`：画像 / 行 / 段落 / シート） |
| | `Navigation.cs` | `TabKind`、`NavLocation`、`NavEntry`、`ItemContext`、`ItemRef`、`BreadcrumbItem`、`TabKinds` |
| | `MessageBarViewModel.cs` | メッセージ行（`Info` / `Success` / `Warning` / `Error`、`ClearPersistent`） |
| | `SettingsViewModel.cs` | 設定ダイアログ。OK のときだけ `ApplyTo` で反映 |
| | `SortModeLabels.cs` | 並べ替えメニューの文言 |
| Views | `MainWindow.xaml(.cs)` | キー・マウス（`OnPreviewKeyDown` / `OnPreviewMouseDown`）、エクスプローラーからのドロップ登録、並べ替えメニューの組み立て、ツールバーのカードの形の切り替え（`RadioButton` 2 つ → `SetCardShapeCommand`）、`ICardHost` / `IMainView` の実装 |
| | `CardGrid.xaml(.cs)` | カードを「列数ぶんの行」にまとめて `VirtualizingStackPanel` で仮想化、列数とサイズ（`GridMetrics`、§6.1。サムネイルの高さは `CardShape` 依存プロパティから `CardShapes.ThumbHeight`）、並べ替えドラッグ（`RunReorderDrag`、自動スクロール、`DropMarkerAdorner`） |
| | `CardView.xaml(.cs)` | 1 枚のカード。ホバー・クリック・ドラッグ開始を検出して `ICardHost` へ。表示されたら `Realize`。📌 はフッターのバッジ行の左（`PinButton`、クリックで解除）、バッジは幅が足りなければ `Viewbox` で縮める |
| | `CascadeController.cs` | ホバー一覧とカスケード全体（[§3.3](#33-カスケードcascadecontroller9)） |
| | `EntryListView` / `OverlayView` / `FlyoutView` / `PreviewView` | 行の一覧（どの行にポインターがあるかを通知）/ オーバーレイ / フライアウト / プレビューの表示 |
| | `ContextMenuFactory.cs` | 右クリックメニュー（カード・行・フライアウト見出しで共通、`ItemRef.Context` で出し分け。「削除（ごみ箱へ移動）」は `MainViewModel.CanDelete` のときだけ） |
| | `ConfirmDialog` / `SettingsWindow` / `Converters.cs` | 確認ダイアログ / 設定画面 / 値コンバーター |
| | `HoverTimings.cs` | ホバーの待ち時間とアニメーション時間（**調整はここだけ**） |
| Themes | `Light.xaml` / `Dark.xaml` | 色トークン（同じキーを両方に定義） |
| | `Styles.xaml` | 共通スタイル（色は `DynamicResource` で参照） |

### 2.3 その他

| パス | 内容 |
|---|---|
| `tools/dev.ps1` | build / test / run / all / publish の中身。**UTF-8（BOM 付き）で保存**。起動中の FolderDeck による exe ロックを確認してから閉じる |
| `build.bat` `test.bat` `run.bat` `build-test-run.bat` `publish.bat` | `tools/dev.ps1` を呼ぶだけ。**ASCII のみ**（コードページ違いで文字化けさせないため） |
| `tools/make-fixture.ps1` | 検証用フォルダ（深い入れ子、大量ファイル、長いパス、循環ジャンクション、アクセス拒否など、§19.3） |
| `tools/analyze-log.ps1` | `app.log` を起動ごとに分けて終わり方を表示（`-Events`） |

## 3. 主要な処理の流れ

### 3.1 タブの読み込み

`MainViewModel.NavigateAsync(target, mode)` → `LoadTabAsync` → `TabViewModel.LoadAsync`

- 前回の読み込みを `_loadCts` でキャンセルしてから始める。5 秒を超えると `IsSlow`。
- 最上位（`Location.IsTop`）は `LoadTopAsync` でタブごとに分岐：
  - **登録フォルダ** `LoadRegisteredAsync`：`RegisteredFoldersStore.Folders` の順がそのまま「手動」の並び。カードは `CardKind.Checking` で作り、`Existence.ProbeAsync`（2 秒）を `ExistenceTask` に持たせる
  - **最近の履歴** `LoadRecentAsync`：`WindowsRecent.Read`（`AppServices.Shell`、20 秒）+ `RecentMerger.Merge` → `Existence.FilterProbedAsync`（1 秒、`RecentLimit` 件）→ `ManualOrder.Apply`（`CardOrderList.Recent`）
  - **よく使う項目** `LoadFrequentAsync`：`FrequentRanker.Rank` → ピン留め・ランキングをそれぞれ存在確認 → `ManualOrder.Apply`（`CardOrderList.Frequent`）
- フォルダの中は `LoadFolderAsync`：`Listings.GetAsync` → `ItemContext.InnerCard` のカードを作る
- `ShowCards(baseOrder, isTop)` → `Ordered`：`CardOrdering.ComparerFor(SortMode)` →（フォルダの中は `FoldersFirst`）→ `SortKeepingPinned`
  - 登録フォルダを更新日順で表示するときは、存在確認（更新日時の取得を兼ねる）がすべて終わってから 1 回だけ並べ替える（`WaitsForChecks` / `ApplyWhenCheckedAsync`）
- **`_baseCards`（並べ替え前の基準順）と `Cards`（表示順）の 2 本立て**。表示順を直接いじらず、`SetBaseOrder` / `ApplyOrder` / `PlaceInBase` / `RemoveCard` / `ApplyPins` を通す。

現在地 `NavLocation(Tab, Root, Rel)`：`Root` は登録フォルダなら**登録 ID**、ほかのタブは正規化したパス。`Root == ""` がタブの最上位。`Rel` はルートからの相対パスで、`ResolvePath` が `PathTools.IsSafeRel` で検証してから結合する。

### 3.2 カードの段階読み込み（§7.4.1）

`CardView` が表示されると `CardViewModel.Realize(thumbPx, iconPx)`、外れると `Unrealize`（CTS をキャンセルし、画像と一覧を手放す）。

`CardViewModel.LoadAsync`：
1. 登録フォルダは `ExistenceTask` の完了を待つ。無効（見つからない・応答なし）なら終わり
2. フォルダ：`DetailGate`（同時 8 件）の中で `Listings.GetAsync` → `ApplyListing`（件数バッジ、`DetailsLoaded` を発火）→ `CoverFinder.Find`（結果は `Covers` に保存）
3. 画像：カバー画像があれば `ThumbnailService.GetAsync(CoverImage)`、なければ `GetAsync(Folder)`。ファイルは `GetEntry` で大きさと日時を取ってから `GetAsync(File)`

`ThumbnailService.Produce`（STA ワーカー上）：シェルのサムネイル → Open XML なら `OfficeCover`（ファイル内の縮小版）→ アイコン（拡張子ごとにキャッシュ）。タイムアウトしたらシステムイメージリストのアイコン。
`ThumbMode`：`Image`（枠いっぱいに切り抜き）/ `Shell`（全体を収める）/ `Icon`（中央に原寸）。キャッシュのキーはパス・更新日時・大きさ・要求サイズ（256 / 512 に丸める）・種類。

### 3.3 カスケード（`CascadeController`、§9）

- 状態：`_levels`（0 = オーバーレイ、1 以降 = フライアウト）、`_preview`（最大 1 つ、常にいちばん深い位置）、タイマー 6 つ（`HoverTimings`：overlay 300 / open 150 / grace 300 / exit 300 / preview 150 ms、プレビューを閉じるタイマーは grace と同じ）
- 開く：`MainWindow.CardMouseEnter` → `CardMouseEnter` → overlay タイマー → `OpenOverlayAsync`（`CardViewModel.EnsureListingAsync`）。行に止まる → `OnRowHovered` → open タイマー → `OpenChild` → `LoadLevelAsync` → `Position` / `Place`（行が載っているモニターの作業領域と DPI から物理ピクセルで配置し、Popup のウィンドウを直接動かす）
- 閉じる：**開閉はすべてここで決める**（Popup は `StaysOpen=True`）。`CloseAll` / `CloseFrom(index)` / `CloseDeepest`（Esc）。右クリックメニュー表示中は閉じない（`ContextMenuFactory.MenuOpened` / `MenuClosed`）
- クリック：`OnRowClicked` / `OnHeaderClicked` → `MainViewModel.OpenItemAsync`
- キーボード：`MainWindow.OnPreviewKeyDown` が、カスケード表示中は先に `HandleKey`（↑ ↓ → ← Enter Esc）へ渡す
- ドラッグ：`BeginDrag` で全階層を非表示にしてタイマーを止め、`EndDrag` で本当に閉じる。ドラッグ中の `CloseAll` は無視する（理由は `BeginDrag` の XML コメント）
- 非同期の結果は、完了時に対象（`_preview` や階層）が差し替わっていないかを確かめてから反映する

### 3.4 プレビュー

`EntryRowViewModel.IsPreviewable` = `ShellPreviewSupport.IsPreviewable`（Core の `PreviewKinds` で対象か、またはシェルがサムネイルを作れる拡張子か）。
`OpenPreview` → `LoadPreviewAsync`：
1. Word / Excel / テキスト：`PreviewTextCache.GetAsync` →（`OfficeText.ReadWord` / `ReadSheet` / `TextPreview.Read`）→ `ShowExcerpt`
2. 抜粋がない・ほかの種類：`ThumbnailService.GetAsync(File)`。`Image` / `Shell` なら表示、アイコンしかなければ「プレビューできない」扱い
3. レイアウト（シートのグリッド、「（以下省略）」）は `PreviewView.xaml.cs`

### 3.5 開く・移動する

- カードのクリック → `MainViewModel.ActivateCardAsync`、行のクリック → `OpenItemAsync`
- フォルダ：`NavigateAsync(Target, NavMode.New, recordPath: …)`（移動も利用として `RecordUse`）
- ファイル：`OpenFileAsync`：実行形式の確認（`ConfirmExecutables` && `ShellActions.IsExecutable`）→ `Task.Run(ShellActions.Open)` → `RecordUse`（`History.Record` → 最近の履歴・よく使う項目を更新）→ 設定によって `SHAddToRecentDocs`
- 右クリック：`ContextMenuFactory.Show(ItemRef, target)` → `MainViewModel` の `ShowInExplorerAsync`（`ItemRef.IsFolder` ならフォルダ自体を開く `OpenInExplorer` / `ExplorerTabs.OpenAsync`、ファイルなら親フォルダで選択 `ShowInExplorer` / `ExplorerTabs.ShowAsync`。どのタブ・どの位置の項目でも同じ。設定で `ExplorerTabs`）/ `CopyPath` / `TogglePin` / `AddToRegisteredAsync` / `DeleteAsync` / `Unregister` / `ExcludeFromRecent` / `RemoveFromFrequent`
- ごみ箱への削除：`CanDelete`（`ItemContext.InnerCard` か `Row`、かつ見つからない・応答なしでない。最上位のカードとフライアウトの見出しは対象外）→ メニューで `BeforeNavigate`（カスケードを閉じる）→ `DeleteAsync`：`IMainView.Confirm`（危険操作。既定は「キャンセル」）→ `RecycleBin.MoveAsync(path, IMainView.WindowHandle)` → 結果をメッセージ行へ → `ReloadAfterDeleteAsync`（`ClearCaches`、全タブを `IsStale`、表示中のタブをスクロール位置を保って読み直す。中止・失敗でも読み直す）

### 3.6 並べ替え・ピン留め

- 並び方の変更：並べ替えメニュー（`MainWindow` が `CardSortModes.All` から組み立てる）→ `MainViewModel.SetSortMode` → `SaveSortMode`（`settings.json` の `sort_modes`）→ `TabViewModel.ApplyOrder`
- ドラッグで並べ替え：`MainWindow.CardDrag` → `FileDragOut.CreateData(reorder: true)` → `CardGrid.RunReorderDrag` → `MainViewModel.MoveCard` → `TabViewModel.SetBaseOrder` → `SaveManualOrder`（登録フォルダは `RegisteredFoldersStore.Reorder`、ほかは `CardOrderStore.Set`）。名前順・更新日順のときは「手動」に切り替える
- ピン：`TogglePin` → `PinStore.Pin` / `Unpin` → `PinStore.Changed` → `OnPinsChanged` → 各タブの `ApplyPins`。名前順・更新日順でピン留めするときは `KeepInPlace` → `PlaceInBase` で表示中の位置に固定。カードの 📌 のクリックは `CardView.OnPinClick` → `ICardHost.CardUnpin`（`MainWindow`：カスケードを閉じて `TogglePin`。見つからない・応答なしのカードでは無効）
- カードの形（全タブ共通）：ツールバーの `RadioButton` → `SetCardShapeCommand` → `MainViewModel.CardShape` → `OnCardShapeChanged`（カスケードを閉じ、`settings.json` の `card_shape` に保存）→ バインドで `CardGrid.CardShape` → `Rebuild` → `GridMetrics.ThumbHeight`
- 規則（ピン留めは位置固定、新規項目の挿入位置）は Core の `Ordering/` にある。App 側で再実装しない

### 3.7 他アプリへのドラッグ

カード・行・プレビューで、押したまま `FileDrag.IsDragGesture` のしきい値を超えたら開始 → `FileDragOut.CreateData`（`DataFormats.FileDrop` + 内部形式 `FolderDeck.DragSource`、最上位カードは `FolderDeck.CardReorder` も）→ `CascadeController.BeginDrag` → `FileDragOut.Run`（`DoDragDrop`、許可する効果は **Copy | Link のみ**）→ `EndDrag`。
ドラッグ元はメインウィンドウか `CardGrid`（ドラッグ中に破棄されない要素）。`MainViewModel.RegisterDroppedAsync` は自アプリ由来のドロップを受け付けない。

### 3.8 永続化

- `DataStores` が 6 つのファイルを持つ（一覧は README の「データの置き場所」）。
- `StoreBase`：変更はメモリ上で `Gate` をロックして行い、`RequestSave` でバックグラウンド保存を予約（連続した変更は 1 回にまとまる）。書き込みは `JsonFile.WriteAtomic`（一時ファイル経由で置き換え）。終了時は `Flush`。
- 読み込み：壊れた JSON は `<名前>.broken-<UNIX秒>.json` に退避して空のデータで続ける（`LoadStatus.Recovered`）。退避もできなければ `Failed` → `IsDisabled` になり、そのファイルには以後書き込まない。
- JSON は snake_case（`JsonNamingPolicy.SnakeCaseLower`）、インデントつき。時刻は `LenientTimeConverter`（読めない値は null）。
- `SettingsStore.Current` は**毎回コピー**を返す。変更は `Settings.Update(s => …)`（→ `Normalize` → `RequestSave`）。
- `registered_folders.json` は imgtool と同じ形式のまま保つ。

### 3.9 テーマ

`ThemeManager` が Windows のアプリモード（ライト / ダーク）を監視し、`App.xaml` の MergedDictionaries の 0 番を `Themes/Light.xaml` ↔ `Dark.xaml` に差し替える。色は `DynamicResource` で参照する。

## 4. スレッドと非同期の規約

- **UI スレッドでブロッキング I/O をしない**。列挙・存在確認・JSON 読み込みは `Task.Run` か `FolderListingCache` の中で行う。
- シェル / COM の呼び出しは `StaWorkerPool` 上でタイムアウトつき（`ThumbnailService` は 4 本・5 秒、`AppServices.Shell` は 1 本）。タイムアウトしたワーカーは放棄して作り直す（最大 16 本）。
  - 例外：ごみ箱への移動（`RecycleBin`）は操作ごとに専用の STA スレッドを作り、タイムアウトをつけない（シェルの進行状況・「使用中」などのダイアログが利用者の操作を待つため）。UI スレッドは `await` で待つだけで止まらない。
- 遅い・切断されたネットワークドライブ対策：`Existence` のタイムアウト（登録フォルダ 2 秒、履歴 1 秒）、`VolumeThrottle`、5 秒で「応答がありません」（`HoverTimings.Slow`、`TabViewModel.IsSlow`）。
- キャンセル：タブは `TabViewModel._loadCts`、カードは `Realize` / `Unrealize` の CTS、プレビューは `Preview.Cts`。`OperationCanceledException` は握りつぶして終わる。
- ストアは内部でロックしてスレッドセーフ。`SaveFailed` はワーカースレッドで発火するので、UI へは `Dispatcher.BeginInvoke`。

## 5. 改修レシピ

| やりたいこと | 触る場所（順に） | 注意・テスト |
|---|---|---|
| 設定項目を追加 | `Core/Stores/SettingsStore.cs` の `AppSettings` にプロパティ（既定値）→ 範囲があれば `Normalize` → `App/ViewModels/SettingsViewModel.cs`（フィールド、コンストラクタ、`ApplyTo`、数値なら `Validate`）→ `Views/SettingsWindow.xaml` → 必要なら `MainViewModel.OpenSettings` でタブを `IsStale` に | `Clone` は `MemberwiseClone` なので**参照型を足したら `Clone` にコピーを追加**。テストは `StoreTests.cs` の `SettingsStoreTests`。README の設定表も更新 |
| プレビュー対象の拡張子を追加 | `Core/Listing/PreviewKinds.cs` の拡張子集合 | `PreviewTests`。シェルのサムネイルで足りる形式なら追加不要（`ShellPreviewSupport` が自動判定） |
| 新しい種類のプレビュー | `PreviewKind` に追加 → 読み取りを `Core/Listing/` に → `PreviewTextCache.GetAsync` の分岐 → `CascadeController.ShowExcerpt` / `LoadPreviewAsync` → `PreviewViewModel` / `PreviewView` | 読み取りは共有モード・上限つき・キャンセル可能に。オンラインのみのファイルは開かない（`FileContent.IsCloudOnly`） |
| 右クリックメニューの項目 | `Views/ContextMenuFactory.cs`（`ItemRef.Context` / カードの種類で出し分け）→ 処理は `MainViewModel` の public メソッドに | カスケードを離れる操作は `BeforeNavigate`（= `CloseAll`）を先に通す |
| ファイルを変更する操作（削除など） | `Shell/RecycleBin.cs`（ごみ箱への移動だけ）→ `MainViewModel.CanDelete` / `DeleteAsync` → `ContextMenuFactory` | **完全削除の経路を作らない**、必ず確認ダイアログを出す。App 層なので自動テストなし → ローカルのパス（ごみ箱へ入る）と `\\wsl.localhost\…` などのパス（中止されて残る）で手動確認。終わったら `ReloadAfterDeleteAsync` で読み直す。README の「削除（ごみ箱へ移動）」も更新 |
| ホバーの待ち時間・アニメーション | `Views/HoverTimings.cs` のみ | |
| 並び方を追加 | `Core/Ordering/CardSortMode.cs`（enum、`All`、`ToSetting` / `FromSetting`、`IsByModified`）→ `CardOrdering.ComparerFor` → `App/ViewModels/SortModeLabels.cs` | メニューは `CardSortModes.All` から自動で作られる。`OrderingTests`、`OrderStoreTests`（`SortModeSettingsTests`） |
| データファイルを追加 | `StoreBase` を継承（`Load` / `Serialize`、変更後に `RequestSave`）→ `DataStores` のコンストラクタ・プロパティ・`All` に追加 | `All` に入れると `LoadAll` / `FlushAll` / 保存失敗の通知 / 読み込み問題の報告に自動で乗る。README のデータ表も更新。`StoreTests` |
| タブの中身・件数・空のときの文言 | `TabViewModel` の `LoadRegisteredAsync` / `LoadRecentAsync` / `LoadFrequentAsync` / `LoadFolderAsync` | UI 文言は日本語でコードに直書き（リソース化していない） |
| 最近の履歴・よく使う項目の規則 | `Core/Ranking/`（`RecentMerger`、`FrequentRanker`） | `RankingTests` |
| フォルダ列挙の規則（並び・隠し・フィルタ） | `Core/Listing/FolderListing.cs`（`FolderLister`） | `ListingTests`（`FolderListerTests`） |
| カバー画像の選び方 | `Core/Listing/CoverFinder.cs` | `ListingTests`（`CoverFinderTests`） |
| カードの見た目・バッジ | `Views/CardView.xaml` + `CardViewModel`（`BadgeMode` など） | 色は `Themes/` のトークンを使う。バッジ行は左が 📌（ピン留め時だけ）、右がバッジ（`Viewbox` で縮む）。最小サイズ 120 で切れないか確認 |
| カードグリッドの列数・サイズ | `Views/CardGrid.xaml.cs`（`GridMetrics`、§6.1）、サイズの範囲は `AppSettings.CardSizeMin` / `Max` / `Step` | |
| カードの形を追加・変更 | `Core/Layout/CardShape.cs`（enum、`ToSetting` / `FromSetting`、`ThumbHeight`）→ `Views/MainWindow.xaml` のツールバーの `RadioButton` | `CardShapeTests`。ツールバーの幅を増やしたら `MainWindow` の `MinWidth`（いまは 660）で更新ボタンまで収まるか確認 |
| 色・スタイル | `Themes/Light.xaml` と `Dark.xaml` の**両方**に同じキー、スタイルは `Styles.xaml` | |
| キー・マウス操作 | `MainWindow.OnPreviewKeyDown` / `OnPreviewMouseDown`、カスケード中は `CascadeController.HandleKey` | |
| 開く・エクスプローラー連携 | `Shell/ShellActions.cs`、`Shell/ExplorerTabs.cs`、`MainViewModel.OpenFileAsync` / `ShowInExplorerAsync` | `ExplorerTabs` は非公開コマンド依存。1 回のクリックでタブとウィンドウの両方を開かない |
| 他アプリへのドラッグの規則 | `Core/Transfer/FileDrag.cs`（規則）、`Shell/FileDragOut.cs`（実行） | Move を許可しない。`FileDragTests` |
| Win32 API を使う | 該当プロジェクトの `NativeMethods.txt` に名前を追加（CsWin32 が生成）→ App は `Shell/ShellInterop.cs` に集約 | |
| メッセージ行 | `MessageBarViewModel`（`Info` / `Success` / `Warning` / `Error`） | |
| ログ | `AppLog.Info` / `Warn` / `Error` | パスと例外だけ。`lifecycle:` 行の形式を変えたら `tools/analyze-log.ps1` も直す |

## 6. テスト・ビルド

- 自動テストは Core のみ（xUnit）。App 層（View / ViewModel / Shell）は自動テストなし → 手動確認：`run.bat --data-dir E:\tmp\folderdeck-data`（本番データを汚さない）、検証用フォルダは `tools/make-fixture.ps1`
- ビルド：`dotnet build FolderDeck.sln -c Debug`
- 関連テストだけ実行：`dotnet test tests/FolderDeck.Core.Tests -c Debug --filter <クラス名の一部>`（演算子なしの `--filter` は `FullyQualifiedName~` と同じ）。`test.bat --filter Ranking` でも可
- `bin\Debug` から FolderDeck が起動中だと exe がロックされてビルドできない（`tools/dev.ps1` は閉じてよいか確認する）。FolderDeck は 1 つしか起動できない

| テストファイル | テストクラス | 主な対象 |
|---|---|---|
| `ListingTests.cs` | `NaturalComparerTests` `FolderListerTests` `CoverFinderTests` `FolderListingCacheTests` `ExistenceTests` | 自然順、列挙（隠し・システム属性、循環ジャンクション）、カバー探索（深さ・フォルダ数の上限、リパースポイント）、列挙キャッシュ、存在確認 |
| `OrderingTests.cs` | `CardOrderingTests` `ManualOrderTests` `PathSetTests` `ExistenceProbeTests` | ピン固定の並べ替え、ドラッグ順の適用、パス集合、存在確認と更新日時 |
| `OrderStoreTests.cs` | `CardOrderStoreTests` `RegisteredFoldersReorderTests` `SortModeSettingsTests` | `card_order.json`、登録順の並べ替え、`sort_modes` |
| `StoreTests.cs` | `JsonFileTests` `RegisteredFoldersStoreTests` `HistoryStoreTests` `PinAndExcludedStoreTests` `SettingsStoreTests` | 原子的書き込み・退避、各ストア、設定の既定値と正規化 |
| `RankingTests.cs` | `RecentMergerTests` `FrequentRankerTests` `FormattingTests` | 最近の履歴の統合、スコア、表示用の書式 |
| `PathToolsTests.cs` | `PathToolsTests` | 入力の整形、正規化、相対パス、比較 |
| `PreviewTests.cs` | `PreviewTests` | 埋め込み縮小版（`OfficeThumbnail`）、オンラインのみの判定、`SessionMarker` |
| `TextPreviewTests.cs` | `TextPreviewTests` | 文字コード判定、行・バイト上限、バイナリ |
| `OfficeTextTests.cs` | `OfficeTextTests` | Word の段落・見出し・上限・壊れた / ロック中のファイル・キャンセル、Excel のセル・日付書式（1904 年基準を含む）・上限、オンラインのみは開かない |
| `FileDragTests.cs` | `FileDragTests` | ドラッグ可否、DropList、自アプリ判定、しきい値 |
| `CardShapeTests.cs` | `CardShapeTests` | `card_shape` の正規化（未設定・不正値は `portrait`）と保存キー、サムネイルの高さ（3:4 / 1:1） |
| `TestHelpers.cs` | `FakeClock` `FakeFileSystem` `TempDir` | テスト用の部品（新しいテストはまずこれを使う） |

## 7. 守ること・落とし穴

- **ファイルを完全に削除しない・移動しない・上書きしない**（自分のデータファイルを除く）。消すのは右クリックの「削除（ごみ箱へ移動）」だけで、確認ダイアログのあと `RecycleBin` でごみ箱へ移す（ごみ箱に入らない項目は中止）。ドラッグも Copy | Link のみ。
- **UI スレッドで待たない**（[§4](#4-スレッドと非同期の規約)）。シェル呼び出しは必ず STA ワーカー経由でタイムアウトをつける。
- パスの比較は `PathTools.PathEquals` / `PathSet`（大文字小文字を区別せず正規化）。`==` で比べない。長いパスをシェルに渡すときは `PathTools.ForShell`。
- `SettingsStore.Current` を書き換えても保存されない（コピーのため）。`Update` を使う。
- 並び順は `_baseCards` を基準に `Ordered` で作る。`Cards` を直接並べ替えない。ピン・ドラッグ順の規則は Core の関数を使う。
- カスケードの開閉判断を `CascadeController` の外に書かない。ドラッグ中にポップアップを破棄しない（`DoDragDrop` がボタンを離したことに気づかなくなる）。
- シェルが返すビットマップの上下と非乗算アルファの扱いは `ShellInterop` に閉じている（README「実装メモ」）。安易に反転しない。
- `tools/dev.ps1` は UTF-8（BOM 付き）、BAT は ASCII のみ。
- コード中のコメントは英語、UI 文言は日本語の直書き。
- コメントの記号：`§n.n` = 旧仕様書の節、`R1`〜 = 旧仕様書 §20 のリスク、`P0` / `P1` / `P2` = 優先度、`AC-nn` = 旧仕様書 §18 の受け入れ基準。旧仕様書は削除済みで、読むときは [§0](#0-このドキュメントの位置づけ) の `git show` を使う。

## 8. 調べ方のショートカット

| 知りたいこと | 方法 |
|---|---|
| 旧仕様書の節に対応するコード | `rg "§9\.6" src` |
| 画面の文言を出しているコード | UI 文言は直書きなので文言で検索（例：`rg "並び順を「手動」" src`） |
| 大きいファイルの節の位置 | `rg -n "// ----" src/FolderDeck.App/Views/CascadeController.cs` |
| 型・メンバーの定義 | `rg -n "class TabViewModel\|LoadRecentAsync" src` |
| 実行時の挙動・異常終了 | `%LOCALAPPDATA%\FolderDeck\logs\app.log`、`tools\analyze-log.ps1 -Events` |
| 旧仕様書の節の中身 | `git show 1122384:dev/docs/SPEC_20261121_native_folder_app.md` を出して `§9.6` などで検索（削除済みのため） |

## 9. 本書の更新ルール

次の変更をしたときは、同じコミット（または同じ PR）で本書を更新する。

- ファイル・クラスの追加 / 削除 / 改名、責務の移動（[§2](#2-ファイルマップ)）
- 処理の流れ・スレッド規約・永続化の方式の変更（[§3](#3-主要な処理の流れ)、[§4](#4-スレッドと非同期の規約)）
- 改修レシピの手順が変わる変更、新しく繰り返しそうな改修（[§5](#5-改修レシピ)に行を足す）
- テストファイル・テストクラスの追加（[§6](#6-テストビルド)）

更新したら、front matter の `updated` と [_topics.yml](_topics.yml) の同じ項目（`updated`、`sections` など）を揃える。
