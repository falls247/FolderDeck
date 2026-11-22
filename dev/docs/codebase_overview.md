# コードベース概要（改修の入口）

改修・調査はここから始める。リポジトリ全体を読まず、下の目次で節を選び、`rg -n "^##" dev/docs/codebase_overview.md` で位置を引いて、その節だけ読む。対象の絞り込みは [§5 改修レシピ](#5-改修レシピ) → [§2 ファイルマップ](#2-ファイルマップ) の順で、該当ファイルの該当メンバーだけ読む。

## 目次

| 節 | 見出し | 内容 |
|---|---|---|
| §0 | このドキュメントの位置づけ | 本書と README の役割分担、旧仕様書（削除済み）の読み方 |
| §1 | 全体像 | 技術スタック、プロジェクトの依存方向、大きいファイル |
| §1.1 | プロジェクトと依存方向 | App → Core、テストは Core のみ |
| §1.2 | 起動と終了 | 単一インスタンス、データ読み込み、未処理例外 |
| §1.3 | オブジェクトの所有関係 | AppServices（合成ルート）から View までの木構造（タブ列 `Tabs`・タブなしの `NoTab`・サイドバーの節・PC のドライブ `DriveList`） |
| §2 | ファイルマップ | ファイルごとの責務の一覧 |
| §2.1 | Core | Drives / Infrastructure / Input / Layout / Listing / Ordering / Paths / Ranking / Selection / Stores / Text / Transfer |
| §2.2 | App | Services / Shell / ViewModels / Views / Themes |
| §2.3 | その他 | tools と BAT（文字コードの注意） |
| §3 | 主要な処理の流れ | 呼び出しの順序をメンバー名でたどる |
| §3.1 | タブの読み込み | `TabViewModel.LoadAsync`、`NavLocation`、`_baseCards` / `_orderedCards` / `Cards`（フィルター） |
| §3.2 | カードの段階読み込み | `Realize` / `Unrealize`、`DetailGate`、`ThumbnailService`（画像は `ImageDecoder` + `ThumbDiskCache`、ほかはシェル）、詳細表示の行は読み込まない |
| §3.3 | カスケード | `CascadeController`：オーバーレイ・フライアウト・タイマー・開閉の判断、カード起点とアンカー起点（`ICascadeAnchor`：サイドバーの行、詳細表示の行の名前の列）、Ctrl / Shift を押したときに閉じる一覧、エクスプローラーのメニューを出している間の凍結（`SetNativeMenuOpen`） |
| §3.4 | プレビュー | `PreviewTextCache` → Office / テキスト抜粋 → サムネイル |
| §3.5 | 開く・移動する | `OpenFileAsync`、利用記録、項目のポインター操作（`ItemPointerTracker`）、右クリックの操作、右クリックのメニューの 2 つの出し方（設定 `item_menu`：WPF のアプリのメニュー / エクスプローラーのメニューの上にアプリの項目、`ShowEntries`、出せなかったときの WPF のメニュー `OpenFallback`）、空白に落ちた項目の右クリック（`RealizedItemAt`）、右ボタンを押したままの左クリック、ごみ箱への削除（メニューと Delete キー、複数は 1 つの操作）、選択（Ctrl / Shift のクリック、`SelectionModel`）、切り取り・コピー・貼り付け（`ShellClipboard`、`PasteAsync` → `FileTransfer`）、エクスプローラーのメニュー（「その他のメニュー（Windows）」、`ShellContextMenu`） |
| §3.6 | 並べ替え・ピン留め | `SetSortMode`、`SetFilter`、`MoveCard`、`MoveRegistered`、`ApplyPins`、`PlaceInBase`、📌 のクリック、表示モード（カードの形と詳細表示 `DetailsListView`：見出しでの並べ替え、列の幅、切り替え時のスクロール位置） |
| §3.7 | 他アプリへのドラッグ | `FileDragOut`、`BeginDrag` / `EndDrag`、Copy \| Link のみ、選択した項目をまとめてドラッグ |
| §3.8 | 永続化 | `StoreBase` の保存予約、原子的書き込み、壊れたファイルの退避、`settings.json` の版（version 2）と旧形式からの移行 |
| §3.9 | テーマ | 設定のテーマ（システム / ライト / ダーク）、`ThemeManager` と Light / Dark の差し替え |
| §3.10 | タブ列とサイドバー | 1 本のタブ列（`Tabs`、`OpenQuickTabAsync`、`OpenFolderTabAsync`）、タブなし（`NoTab` / `HasTab`）、`ShowTabAsync`、タブを閉じる・並べ替える、タブごとの履歴、`open_tabs` の保存、サイドバーの節と折りたたみ（`SidebarSectionViewModel`、`IsSidebarCollapsed`、`ApplySidebarLayout`）、`RefreshRegistered`、PC の節（`DriveListViewModel`、`MainWindowHook`、PC の見出しのメニュー「エクスプローラーで開く」） |
| §4 | スレッドと非同期の規約 | UI スレッドで待たない、STA ワーカーとタイムアウト、専用 STA スレッド（ごみ箱・コピー・移動・エクスプローラーのメニュー）、ドライブの確認、キャンセル |
| §5 | 改修レシピ | よくある改修ごとに触るファイルの順番と注意・関連テスト |
| §6 | テスト・ビルド | 関連テストだけ流すコマンド、テストファイルと対象の対応表 |
| §7 | 守ること・落とし穴 | 削除はごみ箱へだけ（メニューと Delete キー、複数は 1 つの操作）、コピー・移動は `FileTransfer` だけ、エクスプローラーのメニューから外す操作、「Windows のメニュー」（アプリの項目の ID の範囲、open を外す条件、WPF のメニューの経路を残す）、パス比較、設定のコピー、コメント記号（§ / R / P / AC） |
| §8 | 調べ方のショートカット | `rg` での引き方、ログの見方 |
| §9 | 本書の更新ルール | どの変更で本書と目次を更新するか |

## 0. このドキュメントの位置づけ

| 文書 | 内容 | 読むとき |
|---|---|---|
| 本書 | 開発者向けの地図（どこに何があり、どう流れるか） | 毎回最初 |
| [README.md](../../README.md) | 利用者向けの挙動、BAT、データファイルの表、「実装メモ」（実装上の判断） | 挙動の正解を確かめるとき |
| 旧仕様書 `SPEC_20261121_native_folder_app.md`（削除済み） | 最初の実装の元になった仕様（約 1,100 行）。コードコメントの `§9.6` などはこの節番号 | 根拠を確かめるときだけ `git show 1122384:dev/docs/SPEC_20261121_native_folder_app.md` で出し、`§9.6` などを検索する（通読しない） |

- 旧仕様書は最初の実装時点のもの。後から追加・変更した機能（タブごとの並べ替え、ドラッグでの並べ替え、他アプリへのドラッグ、Office・テキストのプレビュー、エクスプローラーの新しいタブで開く、カードの形、📌 のクリックでの解除、ごみ箱への削除（複数の選択と Delete キーを含む）、「エクスプローラーで開く」の廃止（PC の節の見出しのメニューだけは「エクスプローラーで開く」。PC はパスではないので項目の「エクスプローラーで表示」と区別する）、登録フォルダのサイドバー化とフォルダのタブ、表示する項目の切り替え（フィルター）、右ボタンを押したまま左クリックでの「エクスプローラーで表示」）は載っていない。挙動は README と実装が正。
- **旧仕様書の「登録フォルダ」タブはもうない**。登録フォルダはサイドバー（`SidebarView`）の行になり、そこから開いたフォルダは「フォルダのタブ」（`TabKind.Folder`、何個でも開ける）に出る。「最近の履歴」「よく使う項目」も常設ではなくなり、サイドバーの行から開いて × で閉じるタブとして、フォルダのタブと同じ 1 本のタブ列に並ぶ（[§3.10](#310-タブ列とサイドバー)）。本書の「固定タブ」はこの 2 つ（インスタンスが 1 つずつで、一覧＝最上位を持つタブ）を指す。
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
- 大きいファイル（全文を読まず `// ---- 見出し ----` の区切りで目的の節だけ読む）：`Views/CascadeController.cs`（約 1,560 行）、`ViewModels/MainViewModel.cs`（約 1,430 行）、`Core/Listing/SheetReader.cs`（約 820 行）、`Shell/ExplorerTabs.cs`（約 750 行）、`ViewModels/TabViewModel.cs`（約 630 行）、`Views/CardGrid.xaml.cs`（約 590 行）

### 1.2 起動と終了（`App.xaml.cs`）

1. `SingleInstance.Acquire()`：2 つ目の起動は既存ウィンドウを前面に出す合図を送って終了（名前付き Mutex + イベント）
2. `--data-dir` を解析（既定 `%LOCALAPPDATA%\FolderDeck`）→ `AppLog.Initialize` → `SessionMonitor.Start`（前回の異常終了を報告）
3. `ThemeManager.Apply`
4. `DataStores.LoadAll` を `Task.Run` で実行（UI スレッドで JSON を読まない）
5. `AppServices` → `MainViewModel` → `MainWindow` → `RestorePlacement` → `Show`
6. 各ストアの `SaveFailed` をメッセージ行のエラーに接続、`ReportLoadProblems`（`Recovered` は警告、`Failed` はエラー）
7. `MainViewModel.InitializeAsync`：タブ列（`open_tabs`：各タブの種類と現在地を並び順どおりに）とサイドバーの登録フォルダ（`RefreshRegistered`）を復元し、表示していたタブ（`selected_tab`）を読み込む。タブが 1 つも無ければ何も読み込まない（`NoTab`）。旧形式（version 1）の `settings.json` はストアの読み込み時に移行済み（[§3.8](#38-永続化)）。サイドバーの幅（`sidebar_width`）は `MainWindow` のコンストラクタが表示前に列へ設定する

終了（`OnExit`）：`Stores.FlushAll` → 各サービスの `Dispose` → `SessionMonitor.MarkCleanExit`。
未処理例外：UI スレッドと未監視タスクはログ＋ダイアログ（5 秒に 1 回まで）で継続、`AppDomain` の例外はログを書いて終了。

### 1.3 オブジェクトの所有関係

```
App
├─ AppServices（合成ルート。全 ViewModel で共有）
│   ├─ Stores: DataStores ─ Registered / History / Pins / Excluded / Settings / CardOrders
│   ├─ Listings: FolderListingCache（TTL 60 秒・1,500 件・VolumeThrottle）
│   ├─ Covers: TtlCache<フォルダ, カバー画像パス>（"" = なし）
│   ├─ Thumbnails: ThumbnailService（シェル用 STA ワーカー 4 本 + 画像デコード用ワーカー（コア数 − 1 本）・1 回 5 秒・LRU 512 件 / 192 MB）
│   │   └─ ThumbDiskCache（データフォルダの thumbs。書き込み用ワーカー 1 本）
│   ├─ Icons: IconCache（一覧の行のアイコン）
│   ├─ TypeNames: TypeNameCache（詳細表示の「種類」の名前。専用の STA ワーカー 1 本）
│   ├─ Shell: StaWorkerPool(1)（.lnk の読み取りなど、その他のシェル呼び出し）
│   └─ DetailGate: SemaphoreSlim(8)（カード詳細の同時読み込み数）
├─ MainViewModel
│   ├─ RecentTab / FrequentTab: TabViewModel（固定タブ。セッション中ずっと同じインスタンスで、Tabs にある間だけ開いている）→ Cards: CardViewModel[]、Selection: SelectionModel（タブごと。キーはパス）
│   ├─ Tabs: ObservableCollection<TabViewModel>（タブ列。開いているタブ全部を左から順に：固定タブと、フォルダのタブ Kind = Folder）→ Cards
│   ├─ NoTab: TabViewModel（Kind = None。タブが 1 つも無いときの SelectedTab。Tabs には入らない）
│   ├─ SelectedTab（表示中のタブ。null にならない：タブなしは NoTab で、HasTab が false）
│   ├─ QuickSection / RegisteredSection / PcSection: SidebarSectionViewModel（サイドバーの節「クイック アクセス」「登録フォルダ」「PC」。見出しと開閉の状態だけを持ち、行は持たない）
│   ├─ IsSidebarCollapsed（サイドバー全体を隠しているか）
│   ├─ Registered: ObservableCollection<SidebarItemViewModel>（サイドバーの登録フォルダ）
│   ├─ DriveList: DriveListViewModel → Drives: ObservableCollection<SidebarItemViewModel>（PC の節の行。ドライブ 1 つが 1 行で、登録フォルダの行と同じ型）
│   └─ Message: MessageBarViewModel
└─ MainWindow（IMainView, ICardHost を実装）
    ├─ SidebarView（サイドバー全体：節の見出し、クイック アクセスの行（RecentTab / FrequentTab にバインド）、登録フォルダの行、PC の行）
    ├─ MainWindowHook（ウィンドウ メッセージの受け口。ドライブの着脱 → DriveList.RefreshAsync、クリップボードの変更 → MainViewModel.OnClipboardChanged）
    ├─ FolderTabStrip（タブ列。Tabs を並べる）
    ├─ CardGrid（IItemsView。MainWindow は ActiveList 経由で使う）→ CardRow（列数ぶんのカード）→ CardView（DataContext = CardViewModel。ポインター操作は ItemPointerTracker）
    ├─ DetailsListView（IItemsView。詳細表示。CardGrid とどちらか一方だけが項目を持ち、もう一方は隠れて空）→ DetailsRowView（DataContext = 同じ CardViewModel。ポインター操作は ItemPointerTracker。Realize しない）
    ├─ ContextMenuFactory
    └─ CascadeController
        ├─ Level 0: カードから開いたとき OverlayView（サムネイル部分に重ねる）、アンカー（ICascadeAnchor：サイドバーの行、詳細表示の行の名前の列）から開いたとき Popup + FlyoutView（起点の右）。どちらも EntryListViewModel
        ├─ Level 1…: Popup + FlyoutView + EntryListViewModel
        └─ Preview（最大 1 つ）: Popup + PreviewView + PreviewViewModel
```

- ViewModel から View への依存は `IMainView`（ダイアログ、スクロール位置、カスケードを閉じる、テーマの切り替え `ApplyTheme`、シェルのダイアログの親にするウィンドウハンドル `WindowHandle`）だけ。View からの通知は `ICardHost` と、`SidebarView` / `FolderTabStrip` のイベントを `MainWindow` が受ける。`ICardHost` は、ホバーだけ要素で受け（`CardMouseEnter(CardView)`、詳細表示の行の `RowMouseEnter` / `RowMouseMove` / `RowMouseLeave`（名前の列）：カスケードは要素にぶら下がるため）、クリック・右クリック・× ・📌（`ItemUnpin`）・ドラッグ・右ボタンを押したままの左クリック（`ItemShowInExplorer`）は項目（`CardViewModel`）で受ける。項目で受けるのは、同じ項目をどのビュー（カード、詳細表示の行）が出しても `MainWindow` 側を変えずに済ませるため。
- `ICascadeAnchor`（`Views/`）は ViewModel（`SidebarItemViewModel`）が実装するが、WPF の要素は含まない（要素は `AnchorMouseEnter` の引数で別に渡す）。ViewModel に View の型を持たせないための分け方なので、このインターフェイスに要素を足さない。タブの中の項目（`CardViewModel`）は自分では実装せず、`CardAnchor`（同じファイル）が包む：カードはオーバーレイを使うので起点にならず、起点になるのは詳細表示の行だけのため。

## 2. ファイルマップ

### 2.1 Core（`src/FolderDeck.Core/`）

| フォルダ | ファイル | 責務 |
|---|---|---|
| Drives | `DriveModel.cs` | PC の節のドライブ：`DriveKind`（`Fixed` / `Removable` / `Optical` / `Network`）、`DriveEntry`（`Root`、`Label`、`RemotePath`、`IsReady`：null は未確認）、`DriveRules`（文字列だけの規則：出すかどうか `ShouldShow`、エクスプローラーと同じ形の名前 `DisplayName`、ドライブ文字順と重複の除去 `Order`） |
| Infrastructure | `Abstractions.cs` | `IClock` / `SystemClock`、`IFileSystem`、`FsEntry`、`PathKind`。テストで差し替える境界 |
| | `RealFileSystem.cs` | `IFileSystem` の実装 |
| | `AppLog.cs` | `logs\app.log`（5 MB で `app.log.1` へ）。例外を投げない。**ファイルの中身は書かず、パスと例外だけ**。行形式は `tools/analyze-log.ps1` が解析する |
| | `SessionMarker.cs` | `logs\session.json`（起動中だけある記録）の読み書き |
| | `DiskCacheRules.cs` | 画像サムネイルのディスクキャッシュの規則（I/O なし）：キー → ファイル名（`RelativePathOf`、2 文字のフォルダ + 32 桁の 16 進）、キャッシュが付けた名前かの判定（`IsCacheFileName`）、上限を超えたとき消すファイルの選択（`SelectForRemoval`、使っていない順に上限の 8 割まで）。`CacheFile` |
| Input | `RockerGesture.cs` | 「右ボタンを押したまま左クリック」の状態機械（`RightDown` / `LeftDown` / `LeftUp` / `RightUp` / `Cancel`）。ビューがボタンのイベントを渡し、右ボタンを離したとき `RightUp` が対象を返す。マウスキャプチャを使わない（[§3.5](#35-開く移動する)） |
| Layout | `CardShape.cs` | 表示モード：カードの形（`Portrait` = サムネイル 3:4 / `Square` = 1:1）と詳細表示（`Details`）。`settings.json` の値（`portrait` / `square` / `details`）との変換、カードを出すモードか `IsCards`、`ThumbHeight`（幅からサムネイルの高さ） |
| | `DetailsColumns.cs` | 詳細表示の幅を変えられる列（`modified` / `type` / `size`）の幅：既定値、`Normalize`（3 つのキーに揃え、48〜600 に収める）、`Fit`（一覧の幅に収まるよう縮めた**表示用**の幅。名前の列に 120 を残す。保存しない） |
| | `AppTheme.cs` | テーマ（`System` / `Light` / `Dark`）、`settings.json` の値（`system` / `light` / `dark`）との変換、`IsDark`（「システム」のときだけ Windows のモードを使う） |
| | `ItemMenuStyle.cs` | 項目の右クリックのメニュー（`App` = アプリの WPF のメニュー / `Windows` = エクスプローラーのメニューの上にアプリの項目）、`settings.json` の値（`app` / `windows`）との変換（`ItemMenuStyles`。大文字小文字・前後の空白は無視、不明・なしは `app`） |
| Listing | `FolderListing.cs` | `FolderLister.List`：直下の列挙と並び（フォルダ→ファイル、自然順）、`ListingStatus` |
| | `FolderListingCache.cs` | パス単位の TTL キャッシュ。同じパスの同時要求は 1 回の列挙を共有し、待つ側が全員キャンセルすると列挙も止める。`Clear` / `Invalidate` |
| | `VolumeThrottle.cs` | ボリュームごとに同時 I/O を制限（ハングした共有がローカルドライブを巻き込まない） |
| | `Existence.cs` | `ProbeAsync` / `CheckAsync` / `FilterProbedAsync`：タイムアウト付きの存在確認と更新日時・ファイルのサイズ（`ExistProbe.Length`。ファイルだけ） |
| | `CoverFinder.cs` | フォルダカードのカバー画像の探索（§6.4） |
| | `TtlCache.cs` | 汎用の TTL キャッシュ（件数上限つき） |
| | `PreviewKinds.cs` | 拡張子 → `PreviewKind`（None / Image / Presentation / WordDocument / Spreadsheet / Text）。**対象拡張子の一覧はここ** |
| | `FileContent.cs` | `IsCloudOnly`：OneDrive の「オンラインのみ」を属性だけで判定（開かない） |
| | `TextPreview.cs` | テキストの先頭 60 行 / 64 KB。BOM → UTF-8 → Shift_JIS の順に判定、バイナリは null |
| | `OfficeText.cs` | Word / Excel 抜粋の公開 API（`ReadWord` / `ReadSheet`）と結果の record |
| | `OpenXmlPackage.cs`（internal） | zip を共有モードで開く、DTD 禁止、展開量の上限（zip 爆弾対策） |
| | `WordReader.cs` / `SheetReader.cs`（internal） | 本文の段落 / 最初の表示シートの左上。`SheetFormat` が一般形式と日付の表示 |
| | `OfficeThumbnail.cs` | ファイル内に保存された縮小版（`docProps/thumbnail.*`）の読み取り |
| | `ThumbSizing.cs` | アプリが画像をデコードするときの大きさ（`Fit`：短い辺を要求サイズに合わせる、拡大しない、長い辺は要求サイズの 4 倍まで） |
| Ordering | `CardSortMode.cs` | 9 つの並び方（手動と、名前・更新日・サイズ・種類の昇順 / 降順）と `settings.json` の値（`manual` / `name_asc` … `type_desc`）の変換、`CardSortModes.All`。詳細表示の列との対応：`SortColumn`、`ColumnOf` / `IsDescending`、見出しのクリックで次に行く並び方 `Next` |
| | `CardOrdering.cs` | `ComparerFor`、`ByName` / `ByModified` / `BySize` / `ByType`、`FoldersFirst`、`SortKeepingPinned`（ピン留めは位置を固定）、`KnownTime` |
| | `ManualOrder.cs` | 保存済みのドラッグ順を新しいリストへ適用（新規項目は元の並びで直前の項目の後ろ） |
| | `ItemFilter.cs` | 最近の履歴・よく使う項目の一覧に出す項目（`All` / `Folders` / `Files`）と `settings.json` の値（`all` / `folders` / `files`）の変換、`Matches` / `Apply`、絞り込み中のドラッグ順を全体の並びへ戻す `MergeShownOrder`（隠れている項目は位置を保つ） |
| Paths | `PathTools.cs` | 入力の整形、正規化、比較（`PathEquals`）、表示名、`JoinRel` / `IsSafeRel`、`ForShell`（長いパス→8.3） |
| | `PathSet.cs` | 大文字小文字を区別しない正規化パスの集合（ピン判定など） |
| | `NaturalComparer.cs` | エクスプローラーと同じ自然順（`StrCmpLogicalW`） |
| Ranking | `RecentMerger.cs` | Windows の最近使った項目 + 利用記録 + 除外を統合（§10.2） |
| | `FrequentRanker.cs` | 頻度 × 新しさのスコア、ピン留めは別枠で先頭（§10.3） |
| Stores | `StoreBase.cs` | 読み込み・保存の共通処理（[§3.8](#38-永続化)） |
| | `JsonFile.cs` | JSON の読み書き（snake_case、原子的な書き込み、壊れたファイルの退避、時刻の寛容な読み取り） |
| | `DataStores.cs` | 6 つのストアの束。`All` / `LoadAll` / `FlushAll` |
| | `Models.cs` | ドメインの record と JSON の DTO |
| | `RegisteredFoldersStore.cs` | `registered_folders.json`（imgtool と同じ形式）。`Register` / `RegisterMany` / `Remove` / `Reorder`。配列の順がサイドバーの並び |
| | `HistoryStore.cs` | `history.json`（利用記録）。`Record` / `Remove` / `Clear` |
| | `PinStore.cs` | `pins.json`。`Pin` / `Unpin` / `IsPinned`、`Changed` イベント |
| | `ExcludedStore.cs` | `excluded.json`（最近の履歴から外した項目） |
| | `SettingsStore.cs` | `settings.json`。`AppSettings`（既定値・`Normalize`・`Clone`）。表示モードは文字列の `CardShape`（`card_shape`。カードの形 2 つと `details`）、詳細表示の列の幅は `DetailsColumns`（`details_columns`。`Layout/DetailsColumns.Normalize` で揃える）、テーマは文字列の `Theme`（`theme`）、項目の右クリックのメニューは文字列の `ItemMenu`（`item_menu`。`Layout/ItemMenuStyles` で `app` / `windows` に揃える。既定は `app`）、画像サムネイルのディスクキャッシュの上限は `ThumbCacheMb`（`thumb_cache_mb`、MB、0 で無効、起動時に読む）。タブまわり：`OpenTabs`（`OpenTabSetting`：種類 `Kind`・`Root`・`Path`。タブ列の全タブで、フォルダのタブは 50 個まで）+ `SelectedTab`、旧形式のキー `LastTab` / `Locations` / `SelectedFolderTab`（読むだけ：`MigrateLegacyTabs`、[§3.8](#38-永続化)）、`SidebarWidth`（140〜480）、サイドバーの折りたたみ `SidebarCollapsed`（全体）と `SidebarCollapsedSections`（畳んだ節の ID の一覧。ID は `SidebarSections`：`quick` / `registered` / `pc`。`IsSectionCollapsed` / `SetSectionCollapsed`）、`SortModes`（キーは `recent` / `frequent` / `folder`）、`FilterModes`（`recent` / `frequent`）。旧「登録フォルダ」タブのキー `registered` は `Normalize` が片づける（並び方は `folder` へ引き継ぎ、現在地は捨てる） |
| | `CardOrderStore.cs` | `card_order.json`（最近の履歴・よく使う項目のドラッグ順、各 1,000 件まで） |
| Text | `Formatting.cs` | 相対時刻、日時、ファイルサイズの表示 |
| | `DetailsText.cs` | 詳細表示のセルのうちシェルに聞かずに作れる文字列：サイズ（`FileSizeKb`：エクスプローラーと同じ切り上げの KB）、種類の仮の名前（`TypeFallback`：「JPG ファイル」「ファイル フォルダー」） |
| Selection | `SelectionModel.cs` | 1 つの一覧の複数選択。キーはパス（カードは読み込みのたびに作り直すので、選択はカードに持たせない）。`Toggle`（Ctrl）/ `SelectRange`（Shift。起点 `Anchor`）/ `SelectAll` / `Replace` / `Clear` / `Prune`（表示されていない項目を外す）、表示順に並べる `InOrder`、操作の対象 `TargetsOf`（押した項目が選択の中なら選択全部、外ならその項目だけ）。変更があったときだけ `Changed` |
| Transfer | `FileDrag.cs` | 他アプリへのドラッグの規則：可否、`DropList`（260 文字以上→8.3）、自アプリ由来の判定、`IsDragGesture`。内部形式の名前（`SourceFormat`、カードの並べ替え `ReorderFormat`、サイドバーの並べ替え `SidebarReorderFormat`） |
| | `TransferRules.cs` | コピー・移動の判断（文字列だけ。I/O なし）：`Decide(sources, target, options)` が項目ごとに移動かコピーか、断る理由（`TransferRefusal`：同じフォルダ・自分の中・ドライブそのもの・パスでない）を決める。貼り付けは `TransferOptions.Paste(kind)`（切り取りは移動。ネットワーク上の元も移動、削除できない場所の元はコピー、同じフォルダへのコピーは名前を変えて複製）。ほかの規則（ドラッグ用など）はない：アプリの中でファイルを動かすのは貼り付けだけ。結果の分類 `TransferOutcomes.Classify` |
| | `FileClipboard.cs` | クリップボードのファイル一覧の符号化：`CF_HDROP`（`EncodeHDrop` / `DecodeHDrop`。壊れたデータは空で返し例外を投げない）と "Preferred DropEffect"（切り取り = 移動 / コピー） |

### 2.2 App（`src/FolderDeck.App/`）

| フォルダ | ファイル | 責務 |
|---|---|---|
| （直下） | `App.xaml(.cs)` | 起動・終了・未処理例外（[§1.2](#12-起動と終了appxamlcs)）。`App.xaml` の MergedDictionaries の 0 番がテーマ |
| | `app.manifest` / `NativeMethods.txt` | マニフェスト（§4.3）/ CsWin32 で生成する Win32 API の一覧 |
| Services | `AppServices.cs` | 合成ルート（[§1.3](#13-オブジェクトの所有関係)）。`ShowHidden` を変えるとキャッシュを消す。`ThumbnailService` にディスクキャッシュの場所（データフォルダの `thumbs`）と上限（`ThumbCacheMb`）を渡す。詳細表示の種類の名前 `TypeNames` は専用のワーカーを持つ（`Shell` は Windows の最近使った項目の読み取りで数秒ふさがることがある） |
| | `PreviewTextCache.cs` | プレビューの抜粋（Word 段落・シートのセル・テキスト行）を裏のスレッドで読み、LRU で保持。5 秒で打ち切り、失敗はキャッシュしない |
| | `SessionMonitor.cs` | `lifecycle:` ログ（閉じた理由）と前回の異常終了の報告（イベントログも調べる） |
| Shell | `ShellInterop.cs` | Win32 / COM 呼び出しの集約：シェル画像（`IShellItemImageFactory`）、`GetDIBits`、システムイメージリスト、モニターと DPI、`SHAddToRecentDocs` など |
| | `StaWorkerPool.cs` | シェル / COM と画像デコード用の STA ワーカー（スレッドの優先度を指定できる）。タイムアウトは呼び出しが始まってから数える（キューで待つ時間は含めない）。タイムアウトした呼び出しは放棄し、ワーカーを作り直す |
| | `ThumbnailService.cs` | カードとプレビューの画像（`ThumbSource` / `ThumbMode` / `LruCache`）。画像ファイルは自前でデコード（`DecodePicture`）、それ以外はシェル（`Produce`）（[§3.2](#32-カードの段階読み込み741)） |
| | `ImageDecoder.cs` | 画像ファイルを WIC で直接サムネイルの大きさにデコード（`CanDecode` / `Decode`）。対象の拡張子、JPEG のコーデック内縮小、EXIF の向き、透過の判定 |
| | `ThumbDiskCache.cs`（internal） | `ImageDecoder` が作ったサムネイルをデータフォルダの `thumbs` に保存・読み出し・刈り込み（`TryRead` / `Save` / `Trim`）。規則は Core の `DiskCacheRules` |
| | `IconCache.cs` | 一覧の行のアイコン（拡張子ごとに共有できるものは共有） |
| | `TypeNameCache.cs` | 詳細表示の「種類」に出すシェルの種類名（`SHGetFileInfo` を拡張子だけで呼ぶ。ファイルは開かない）。拡張子ごとにアプリの終了までキャッシュ。`TryGet` はまだ聞いていない拡張子に仮の名前（`DetailsText.TypeFallback`）を返し、`GetAsync` が本当の名前を取る（失敗・5 秒超過は仮の名前のまま） |
| | `OfficeCover.cs` | `OfficeThumbnail.ReadEmbedded` を WPF の画像にする |
| | `ShellPreviewSupport.cs` | 「Windows がサムネイルを作れるか」を拡張子ごとに 1 回だけ確認（`AssocQueryString`）。`IsPreviewable` |
| | `ShellActions.cs` | 既定のアプリで開く、エクスプローラーで表示（`ShowInExplorer`、親フォルダで選択。ファイル用）/ 開く（`OpenInExplorer`。フォルダの「エクスプローラーで表示」とデータフォルダを開くとき）、パスをコピー、実行形式の判定 |
| | `RecycleBin.cs` | ごみ箱への移動（`MoveAsync(paths)`）。複数の項目を **1 つの `IFileOperation`**（進行状況・元に戻すも 1 つ）で、操作ごとの専用 STA スレッドで実行する。進行状況シンクの `PreDeleteItem` で「ごみ箱に入らない（完全に削除される）」項目を止めると、その項目と残り全部が取り消される（先に済んだ項目はごみ箱に入ったまま）。項目が 1 つでも見つからなければ何もしない。結果は件数付きの `RecycleResult`（`Outcome`：`Recycled` / `Canceled` / `NotRecyclable` / `Failed`、`Requested`、`Recycled`）。シンクは「ごみ箱の項目あり」を成功、成功コード（S_OK 以外）で項目なしをスキップ、S_OK で項目なしを完全削除（起きない想定。`Failed` で知らせる）と数える |
| | `ExplorerTabs.cs` | エクスプローラーの新しいタブで「表示」（ファイル：`ShowAsync`＝親フォルダで選択、フォルダ：`OpenAsync`＝そのフォルダ。Windows 11 22H2+、非公開の `WM_COMMAND 0xA21B`。失敗時は `ShellActions` の新しいウィンドウへ）。シェルの名前空間パス（`::{CLSID}`。`ShellActions.IsShellNamespace`、PC は `ShellActions.ThisPcPath`）は存在確認を飛ばして開く |
| | `WindowsRecent.cs` | `%APPDATA%\Microsoft\Windows\Recent\*.lnk` の読み取り、記録が有効かの確認 |
| | `FileDragOut.cs` | ドラッグ用 `DataObject` の作成、`DoDragDrop` の実行、`IsDragging` |
| | `DriveCatalog.cs` | ドライブの一覧（`Enumerate`：ドライブ文字・種類と、記憶されているネットワーク接続。**媒体には触らない**）と 1 台の確認（`Probe`：`IsReady`・ボリューム ラベル・割り当て先。ブロックしうる）。Win32（`WNet…`）はこのファイルの中で呼ぶ |
| | `MainWindowHook.cs` | メイン ウィンドウのメッセージの受け口（`HwndSource` のフック）。ボリュームの着脱（`WM_DEVICECHANGE`）を 500 ms まとめて `DrivesChanged` で知らせる（ネットワーク ドライブでは来ない）。クリップボードの変更（`WM_CLIPBOARDUPDATE`。自アプリの書き込みでも来る）を `ClipboardChanged` で知らせる → `MainViewModel.OnClipboardChanged` |
| | `ShellClipboard.cs` | クリップボードへのファイルの書き込み（`TryWrite`：UI スレッド。成功時のシーケンス番号を返す）と読み取り（`ReadWithSequenceAsync`：専用スレッドでタイムアウト付き。読んだ時点のシーケンス番号も返す）、`HasFiles`、移動が済んだ切り取りを片づける `ClearIfUnchanged`（読んだあと誰も書き換えていないときだけ空にする） |
| | `FileTransfer.cs` | アプリで唯一ファイルをコピー・移動するところ（`RunAsync`：`IFileOperation` を専用 STA スレッドで実行。名前の衝突・進行状況はシェルのダイアログ、確認を省くフラグは使わない）。結果 `TransferResult` の `Done` / `MovesDone` / `NewPaths` は実行後にファイル システムを見て決める |
| | `SourceProbe.cs` | コピー・移動の元と先の確認（ネットワーク上か・読み取り専用の媒体か・削除できるか `DescribeAsync`（ネットワーク上の元も開いて確かめる）、貼り付け先があるか `FolderExistsAsync`）。どちらもタイムアウト付きでワーカーで実行し、例外を投げない |
| | `ShellContextMenu.cs` | エクスプローラーの右クリックメニュー（`IContextMenu`）を出す（`ShowAsync(paths, x, y, extended, mainWindow, appEntries)` → `ShellMenuResult`：`Invoked` / `Dismissed` / `Unavailable` / `TimedOut` / `Failed` / `AppCommand`（呼び出し側の項目が選ばれた。`AppIndex`）。`Shown` は結果のときにメニューが出ていた（出た）か。Task は失敗にしない）。1 回ごとに専用の STA スレッド（`StaThread.RunPumped`）と見えないオーナー ウィンドウ。親が 1 つなら `SHBindToParent` → `GetUIObjectOf`、複数なら item array（`ShellMenuTargets.TryGroup` で判定）。delete / rename / cut / copy / paste の項目を外す。呼び出し側の項目（`ShellMenuAppEntry`：文字列だけ。グリフなし）は先頭に入れ（`InsertAppEntries`、ID は `AppFirstId` 0xE000 〜 `AppIdLimit` 0xF000）、区切りのあとにシェルの項目。そのときは copyaspath も外し、既定の項目（`IsDefault`）があれば open も外して `SetMenuDefaultItem` で差し替える。用意に 5 秒（`TimedOut`。後から出ない）、表示後は無制限 |
| | `StaThread.cs`（internal） | 1 回の処理のための専用 STA スレッド（`Run`：`FileTransfer` とクリップボードの読み取り、`RunPumped`：Dispatcher を回す版で `ShellContextMenu`）。本体の例外は `onError` へ渡し、Task は失敗にしない |
| | `SingleInstance.cs` / `ThemeManager.cs` | 単一インスタンス / テーマの適用（`Apply(AppTheme)`：設定のテーマでライト・ダークを差し替える。「システム」のときは Windows のモードに追従） |
| ViewModels | `MainViewModel.cs` | タブ列（`Tabs`：固定タブ `RecentTab` / `FrequentTab` を開く `OpenQuickTabAsync`・フォルダのタブを開く `OpenFolderTabAsync`・閉じる `CloseTab`・並べ替え `MoveTab`）、タブなしの `NoTab` / `HasTab`、タブ切り替え（`ShowTabAsync`）、移動と履歴（戻る・進む、タブごとに最大 100）、開く、右クリックの各操作（ごみ箱への削除 `CanDelete` / `DeleteAsync` を含む）、サイドバーの登録フォルダ（`Registered`、`RefreshRegistered`、登録・解除 `Unregister`・並べ替え `MoveRegistered`、幅 `SidebarWidth`）、サイドバーの節（`QuickSection` / `RegisteredSection`）と全体の表示・非表示（`IsSidebarCollapsed`、`ToggleSidebarCommand`）、並べ替え・フィルター（`SetSortMode` / `SetFilter`）・ドラッグ順の保存、ピン、カードの大きさと表示モード（`CardSize` / `CardShape`：カードの形 2 つと詳細表示）、詳細表示の列の幅（`DetailsColumns` / `SetDetailsColumnWidth`）、選択（`ClickSelect` / `SelectAll` / `ClearSelection` / `SelectionTargets`）、切り取り・コピー・貼り付け（`CanCut` / `CanCopy` / `CopyToClipboard`、`PasteFolderOf` / `CanPaste` / `PasteAsync`、切り取りの状態 `OnClipboardChanged` / `OnActivatedAsync`、貼り付け・ごみ箱への削除の実行中 `IsFileOperationRunning`）、PC をエクスプローラーで開く（`OpenPcInExplorerAsync`）、エクスプローラーのメニューの結果（`OnShellMenuClosedAsync`）（[§3.5](#35-開く移動する)）、設定ダイアログ |
| | `TabViewModel.cs` | 1 タブの現在地・カード・読み込み・並び・フィルター・戻る／進むの履歴（`BackStack` / `ForwardStack`）・離れたときのスクロール位置（[§3.1](#31-タブの読み込み)）。固定タブ（`Recent` / `Frequent`）とフォルダのタブ（`Folder`、`IsFolderTab`）で共通。`None`（`IsPlaceholder`）はタブなしの代役で、何も読み込まず何も出さない。`IsOpen`（タブ列にあるか）、`ResetToTop`（閉じた固定タブを最上位・履歴なしに戻す）、`ResetScrollOffsets`（覚えているスクロール位置を全部 0 にする。表示モードがカード ↔ 詳細で替わったとき）。選択 `Selection`（Core の `SelectionModel`）と「n 個を選択」の `SelectionText`、切り取りの印 `CutPaths`（カードの `IsSelected` / `IsCut` へ写す `ApplyItemFlags`）、貼り付け先 `FolderTargetPath` / `TransferTargetPath` |
| | `CardViewModel.cs` | タブの項目 1 つ（カードとしても、詳細表示の行としても出る）。`CardKind`（`Folder` / `File`。`Missing` / `NoResponse` は表示の仕組みだけ残っていて、今はどの一覧も作らない）、`BadgeMode`、段階読み込み（`Realize` / `Unrealize`。カードだけが呼ぶ）、詳細表示の行が読む `Icon` / `ModifiedText` / `TypeText` / `SizeColumnText`（バインドされて初めて取りに行くので、カード表示では何もしない）、選択と切り取りの表示 `IsSelected` / `IsCut`（タブが入れる。自分では変えない） |
| | `SidebarItemViewModel.cs` | サイドバーの 1 行：登録フォルダ、または PC の節のドライブ（`ForDrive` で作る。`IsDrive`）。`Id` / `Path` / `Name`、確認の結果 `SidebarItemState`、`IsDisabled`、開く先 `Target`、`ToItemRef`（ドライブは `ItemContext.Drive`）。カスケードの起点として `ICascadeAnchor` を実装（`OpensInNewTab` = true、`YieldsToModifiers` = false）。ドライブの行は名前がラベルに追従し（`UpdateDrive`）、アイコンはシェルのものではなく種類ごとのフォント グリフ（`Glyph`） |
| | `DriveListViewModel.cs` | PC の節の行（`Drives`）。`RefreshAsync(recheck)` が一覧を読んで行を揃え、ドライブごとに確認する（[§3.10](#310-タブ列とサイドバー)）。行が消えたら `RowRemoved` |
| | `SidebarSectionViewModel.cs` | サイドバーの節 1 つ（`Id`、見出し `Title`、`IsExpanded`、`ToggleCommand`）。開閉が変わると作り手（`MainViewModel`）へ通知する |
| | `EntryListViewModel.cs` | カスケード 1 階層（`EntryListViewModel`）と行（`EntryRowViewModel`、`ListState`） |
| | `PreviewViewModel.cs` | プレビューの内容（`PreviewContent`：画像 / 行 / 段落 / シート） |
| | `Navigation.cs` | `TabKind`（`Recent` / `Frequent` / `Folder`）、`NavLocation`、`NavEntry`、`ItemContext`（`Sidebar`：登録フォルダの行、`Drive`：ドライブの行）、`ItemRef`（`NewTab`：フォルダを新しいタブで開く、`IsDisabled`：サイドバーの行がいま開けない）、`BreadcrumbItem`、`TabKinds` |
| | `MessageBarViewModel.cs` | メッセージ行（`Info` / `Success` / `Warning` / `Error`、`ClearPersistent`） |
| | `SettingsViewModel.cs` | 設定ダイアログ。OK のときだけ `ApplyTo` で反映。テーマは `Theme` と、RadioButton 用の `IsThemeSystem` / `IsThemeLight` / `IsThemeDark`。右クリックのメニューも同じ形（`ItemMenu` と `IsItemMenuApp` / `IsItemMenuWindows`。チェックされた側だけが値を変える） |
| | `SortModeLabels.cs` | 並べ替えメニューの文言（`SortModeLabels`）とフィルターの文言（`FilterLabels`） |
| Views | `MainWindow.xaml(.cs)` | 全体のレイアウト（左：サイドバー（`SidebarPane`。中身は `SidebarView`、上に重ねるドロップの案内 `DropHint`）、`GridSplitter`、右：タブ列（左端にサイドバーの表示・非表示のボタン）・ツールバー・メッセージ・カード。タブなし（`HasTab` が false）のときはツールバーを隠し、案内文と固定タブを開くボタンを出す）、キー・マウス（`OnPreviewKeyDown` / `OnPreviewMouseDown`、Ctrl + W でタブを閉じる）、エクスプローラーからのドロップ登録（ウィンドウ全体）、並べ替え・フィルターのメニューの組み立て（`OnSortClick` / `OnFilterClick`）、ツールバーの表示モードの切り替え（`RadioButton` 3 つ：縦長・正方形・詳細 → `SetCardShapeCommand`。クリックなしでチェックされたときは `OnViewModeChecked`）、項目のビュー 2 つ（`Grid`：`CardGrid`、`Details`：`DetailsListView`。`CardShape` のトリガーで片方だけを表示し、`Items` も片方にだけ渡す）、サイドバーの幅と表示・非表示（`SidebarColumn`、`OnSplitterDragCompleted`、`ApplySidebarLayout`）、サイドバーのイベントの受け口（`OnSidebarClicked` / `OnSidebarDrag` など）、`ICardHost` / `IMainView` の実装。項目を出しているビューは `ActiveList`（`IItemsView`。`CardShape` から `Grid` か `Details`）で引く（スクロール位置の保存・復元と並べ替えドラッグ）。`MainWindowHook` を `SourceInitialized` で作り、ドライブの読み直しのきっかけを `DriveList` へ、クリップボードの変更を `OnClipboardChanged` へ渡す（`WatchWindowMessages`、`OnActivatedReadDrives`）。選択のキー（`HandleSelectionKey`：Ctrl + A / Esc / Ctrl + C・X・V / Delete）、項目の領域 `ContentHost` の空白の押下（選択の解除 `OnContentLeftDown`）と右クリック（`OnContentRightUp` → `ContextMenuFactory.ShowBlank`。ただしポインターの位置に実体化した項目があれば `ActiveList.RealizedItemAt` でその項目のメニュー）、貼り付け・ごみ箱への削除の実行中に閉じるときの確認（`OnClosing`） |
| | `SidebarView.xaml(.cs)` | サイドバー全体。節の見出し（スタイル `SectionHeader`：山形 + 見出し、全体がボタン）、クイック アクセスの行（スタイル `QuickRow`：表示中は塗り、開いていれば点）、登録フォルダの見出し（件数と登録ボタン）と行、PC の見出しと行（ドライブ）。登録フォルダの行と PC の行は同じテンプレート（リソース `SidebarRow`）。節の開閉に合わせた高さの配分（`ApplyRowHeights`）。登録フォルダの行と PC の行は、ホバー・クリック・右クリック・右ボタンを押したままの左クリックを同じイベントで通知し、ドラッグ開始は登録フォルダの行だけが通知する（`ItemEntered` / `ItemClicked` / `ItemContextMenu` / `ItemExplorerRequested` / `ItemDragRequested`）。PC の見出しの右クリックは `PcHeaderContextMenu`（`OnPcHeaderRightUp`）。行の並べ替えドラッグ（`RunReorderDrag`、入る位置の横線 `DropLine`。受けるのは登録フォルダの行の領域 `RegisteredBody` だけ） |
| | `FolderTabStrip.xaml(.cs)` | タブ列（`Tabs` を横に並べる。固定タブは種類ごとのアイコンと `Brush.Accent`、フォルダのタブは `Brush.Folder`）。押すと表示、左右にドラッグで並べ替え（`MoveTowards`：隣のタブの中央を越えたら入れ替え）、× ・中クリックで閉じる、右クリックのメニュー、ホイールで横スクロール |
| | `CardGrid.xaml(.cs)` | カードを「列数ぶんの行」にまとめて `VirtualizingStackPanel` で仮想化、列数とサイズ（`GridMetrics`、§6.1。サムネイルの高さは `CardShape` 依存プロパティから `CardShapes.ThumbHeight`）、並べ替えドラッグ（`RunReorderDrag`、自動スクロール、`DropMarkerAdorner`）。`IItemsView` を実装 |
| | `IItemsView.cs` | 項目を出すビュー（`CardGrid`、`DetailsListView`）に `MainWindow` が求めるもの（`VerticalOffset` / `ScrollTo` / `Scrolled` / `RunReorderDrag` / `RealizedItemAt`）。項目やサイズは XAML のバインドで渡すので含めない。`RealizedItemAt` の中身は共通の `ItemsViewHitTest.Find`（位置だけで探す。`IsHitTestVisible` に関係なく、スクロール ビューアーの表示領域の外（詳細表示の見出し、表示領域の外に残った実体化済みの項目）は null。`CardView` / `DetailsRowView` の中へは降りない） |
| | `DetailsListView.xaml(.cs)` | 詳細表示：見出しの行 + 1 項目 1 行（高さ 28 固定、`VirtualizingStackPanel` で仮想化）。`ListView` / `ListBox` ではなく素の `ItemsControl`。列の幅は `DetailsMetrics`（見出しと全部の行が共有。`Columns` を `DetailsColumns.Fit` に通した表示用の幅）、見出しのクリック `HeaderClicked`、列の左端のつまみでの幅変更 `ColumnResized`（離したとき 1 回）、並べ替えドラッグ（`RunReorderDrag`、行の間の横線、自動スクロール）。`IItemsView` を実装 |
| | `DetailsRowView.xaml(.cs)` | 詳細表示の 1 行（名前の列 = アイコン + 名前 + 📌 と、一覧のときは補足 `SubText`、更新日時、種類、サイズ、最上位では ×）。見た目は全部バインドとトリガー。マウスのボタンは `ItemPointerTracker` へ渡し、名前の列（列 0 全体の `NameColumn`。背景 Transparent でヒットテストに当てる）の出入りと移動を `ICardHost.RowMouseEnter` / `RowMouseMove` / `RowMouseLeave` へ知らせる。**`Realize` を呼ばない**。選択は `Root` のトリガーで塗り（`Brush.AccentSoft`）と、セルの上に重ねた 2 px の枠（`Brush.Accent`。枠で列がずれないよう重ねる）、切り取りは不透明度 0.5 |
| | `CardView.xaml(.cs)` | 1 枚のカード。ホバーは自分で `ICardHost` へ通知し、マウスのボタンのイベントは `ItemPointerTracker`（`_pointer`）へ渡す。`ICardHost` の実装の置き場（`CardView.Host`）と `ICardHost` の定義もここ。表示されたら `Realize`。📌 はフッターのバッジ行の左（`PinButton`、クリックで解除）、バッジは幅が足りなければ `Viewbox` で縮める。選択はトリガーで枠（`SelectionFrame`：2 px の `Brush.Accent` を上に重ねる）とフッターの塗り（`CardBorder` の `Brush.AccentSoft`）、切り取りは `UpdateOpacity` で 0.5（不透明度はホバー・ドラッグと同じくコードで決めるので、トリガーにしない） |
| | `ItemPointerTracker.cs` | 項目 1 つぶんのポインターの状態（押下・クリックの判定・ドラッグのしきい値・右ボタンを押したままの左クリック）。要素（`CardView`、`DetailsRowView`）がマウスのイベントを渡し、結果を `ICardHost` の `ItemClick` / `ItemContextMenu` / `ItemShowInExplorer` / `CanDragItem` / `ItemDrag` へ通知する（[§3.5](#35-開く移動する)） |
| | `CascadeController.cs` | ホバー一覧とカスケード全体（[§3.3](#33-カスケードcascadecontroller9)）。カードから（`CardMouseEnter`）とアンカー（`ICascadeAnchor`。サイドバーの行：登録フォルダ・ドライブと、詳細表示の行の名前の列）から（`AnchorMouseEnter` / `AnchorMouseMove` / `AnchorMouseLeave`）の両方。一覧の起点の要素が消えたときは `AnchorRemoved`。エクスプローラーのメニューの間は凍結（`SetNativeMenuOpen`） |
| | `ICascadeAnchor.cs` | カード以外のカスケードの起点が表すフォルダ（`Path` / `Target` / `IsDisabled` / `IsHoverHeld`）と、起点ごとの違い（`OpensInNewTab`、`YieldsToModifiers`）。`CardAnchor`：タブの中のフォルダ（`CardViewModel`）を起点にする包み（`For` はフォルダ以外に null。`OpensInNewTab` = false、`YieldsToModifiers` = true、`IsHoverHeld` は項目のものをそのまま使う） |
| | `EntryListView` / `OverlayView` / `FlyoutView` / `PreviewView` | 行の一覧（どの行にポインターがあるか、クリック、右クリック、右ボタンを押したままの左クリック `RowExplorerRequested` を通知）/ オーバーレイ / フライアウト / プレビューの表示 |
| | `ContextMenuFactory.cs` | 右クリックメニュー（カード・行・フライアウト見出し・サイドバーの行で共通、`ItemRef.Context` で出し分け。「削除（ごみ箱へ移動）」は `MainViewModel.CanDelete` のときだけ、「登録フォルダに追加」は未登録のフォルダだけ、「登録を解除」は登録フォルダの行だけ。ドライブの行（`ItemContext.Drive`）は「開く」「エクスプローラーで表示」「パスをコピー」「貼り付け」「登録フォルダに追加」だけ）。「パスをコピー」の後に「切り取り」（全部が `CanCut` のとき）「コピー」（全部が `CanCopy` のとき）「貼り付け」（フォルダと使えるドライブの行。`CanPaste` で有効）。複数の選択（`group`、`GroupEntries`）では「切り取り」「コピー」「パスをコピー」と、全部が `CanDelete` のときの「削除（ごみ箱へ移動）」だけ。項目のメニューは一度 `MenuEntry` の並び（`ItemEntries` / `GroupEntries`。1 項目の「開く」が `IsDefault`）に組み立て、`ShowEntries` が出し方を決める：設定が「アプリのメニュー」なら WPF のメニュー（`Render`）のいちばん下に「その他のメニュー（Windows）」（`AddNativeItem` → `ShowNativeAsync` → `ShellContextMenu.ShowAsync`。ドライブの行には出さない。1 項目は使える項目で `IsTransferable` のとき、選択は全部が `CanCopy` のとき有効）、「Windows のメニュー」（`MainViewModel.UsesWindowsItemMenu`）でエクスプローラーのメニューを出せる項目なら `ShowNativeAsync(paths, at, entries, target)` で並びをその上に載せる。出せなかったときは同じ並びの WPF のメニュー（`OpenFallback`）、時間切れの項目は 5 分間（`TimedOutSkip`、`_timedOut`）最初から WPF のメニュー。メニューを出したときのポインター位置と Shift を `Show` で覚える（`NativeMenuPoint`）。開閉は `NativeMenuChanged` で `CascadeController` へ。PC の節の見出しは `ShowPcSection`（「エクスプローラーで開く」だけ）、項目の領域の空白は `ShowBlank`（「貼り付け」「最新の情報に更新」）で、どちらも設定に関係なく WPF のメニュー |
| | `ConfirmDialog` / `SettingsWindow` / `Converters.cs` | 確認ダイアログ / 設定画面 / 値コンバーター |
| | `HoverTimings.cs` | ホバーの待ち時間とアニメーション時間（**調整はここだけ**） |
| Themes | `Light.xaml` / `Dark.xaml` | 色トークン（同じキーを両方に定義）。固定タブは `Brush.Accent`、登録フォルダとフォルダのタブは `Brush.Folder` / `Brush.FolderSoft` |
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

`MainViewModel.NavigateAsync(target, mode)`（表示中のタブの中の移動）/ `ShowTabAsync(tab)`（タブの切り替え）→ `LoadTabAsync` → `TabViewModel.LoadAsync`

- 前回の読み込みを `_loadCts` でキャンセルしてから始める。5 秒を超えると `IsSlow`。
- 最上位（`Location.IsTop`。固定タブだけにある）は `LoadTopAsync` でタブごとに分岐：
  - **最近の履歴** `LoadRecentAsync`：`WindowsRecent.Read`（`AppServices.Shell`、20 秒）+ `RecentMerger.Merge` → `Existence.FilterProbedAsync`（1 秒、`RecentLimit` 件）→ `ManualOrder.Apply`（`CardOrderList.Recent`）
  - **よく使う項目** `LoadFrequentAsync`：`FrequentRanker.Rank` → ピン留め・ランキングをそれぞれ存在確認 → `ManualOrder.Apply`（`CardOrderList.Frequent`）
- フォルダの中は `LoadFolderAsync`：`Listings.GetAsync` → `ItemContext.InnerCard` のカードを作る。フォルダのタブ（`TabKind.Folder`）は常にこれ（最上位がない）
- 最上位のカードは、存在確認の結果（`ExistProbe`）から更新日時とファイルのサイズをもらう（`MakeTopCard`）。詳細表示の行はディスクを見に行かないので（[§3.2](#32-カードの段階読み込み741)）、ここで入れないとサイズの列が空のままになり、サイズ順も効かない。フォルダの中のカードは列挙の結果から同じものをもらう
- `ShowCards(baseOrder, isTop)` → `Ordered`：`CardOrdering.ComparerFor(SortMode)`（サイズはフォルダを「なし」、種類は拡張子で渡す）→（フォルダの中は `FoldersFirst`）→ `SortKeepingPinned` → `Show`：フィルター（`ItemFilters.Apply`、固定タブの最上位だけ `FilterApplies`）→ `UpdateTopSummary`（件数と空のときの文言）
- **`_baseCards`（並べ替え前の基準順）、`_orderedCards`（並べ替え後・フィルター前の全カード、`OrderedCards`）、`Cards`（表示するカード）の 3 本立て**。表示順を直接いじらず、`SetBaseOrder` / `ApplyOrder` / `PlaceInBase` / `RemoveCard` / `ApplyPins` を通す。「ピン留めは基準順と同じ位置」という前提が成り立つのは `OrderedCards` に対して（`Cards` はフィルターで詰まる）。

現在地 `NavLocation(Tab, Root, Rel)`：`Tab` はタブの種類（`Recent` / `Frequent` / `Folder`。`None` はタブなしの代役 `NoTab` 専用で、移動先にはならない）、`Root` は正規化したパス。`Root == ""` が固定タブの最上位（フォルダのタブは必ず `Root` を持つ）。`Rel` はルートからの相対パスで、`ResolvePath` が `PathTools.IsSafeRel` で検証してから結合する。フォルダのタブの `Root` は開いたときの登録フォルダのパス（登録 ID ではないので、登録を解除してもタブは生きる）。

### 3.2 カードの段階読み込み（§7.4.1）

`CardView` が表示されると `CardViewModel.Realize(thumbPx, iconPx)`、外れると `Unrealize`（CTS をキャンセルし、画像と一覧を手放す）。

`CardViewModel.LoadAsync`：
1. 無効（見つからない・応答なし）なら終わり
2. フォルダ：`DetailGate`（同時 8 件）の中で `Listings.GetAsync` → `ApplyListing`（件数バッジ、`DetailsLoaded` を発火）→ `CoverFinder.Find`（結果は `Covers` に保存）
3. 画像：カバー画像があれば `ThumbnailService.GetAsync(CoverImage)`、なければ `GetAsync(Folder)`。ファイルは `GetEntry` で大きさと日時を取ってから `GetAsync(File)`

`ThumbnailService.GetAsync`：メモリの LRU → 画像ファイル（`ImageDecoder.CanDecode`：jpg / jpeg / jpe / jfif / png / bmp / gif / tif / tiff / webp。フォルダ自身は対象外）なら `DecodePicture` → 画像が得られなければ `Produce`。
- `DecodePicture`（デコード用ワーカー上）：`ThumbDiskCache.TryRead`（データフォルダの `thumbs`）→ なければ `ImageDecoder.Decode` → 作るのに 4 ms 以上かかったものは `ThumbDiskCache.Save`（書き込み用ワーカーで JPEG、透過があれば PNG に保存）。「オンラインのみ」のファイルは開かず `Produce` へ回す。
- `ImageDecoder.Decode`：WIC で直接サムネイルの大きさ（`ThumbSizing.Fit`：短い辺 = 要求サイズ）にデコードする。JPEG などの不透明な形式は縮小をコーデックに直結する（JPEG は 1/2・1/4・1/8 でデコードされ、シェルより数倍速い）。パレット・透過つきは先に Pbgra32 に変換してから縮小。EXIF の向き（`System.Photo.Orientation`）を反映。結果は Bgr32（透過が本当にあるときだけ Pbgra32）で `Freeze` 済み。失敗は null（→ シェル）。
- `Produce`（シェル用 STA ワーカー上）：シェルのサムネイル → Open XML なら `OfficeCover`（ファイル内の縮小版）→ アイコン（拡張子ごとにキャッシュ）。タイムアウトしたらシステムイメージリストのアイコン。動画・Office・フォルダ・HEIC / AVIF・.ico などはこちら（ディスクキャッシュは Windows のサムネイルキャッシュ）。
- `ThumbDiskCache`：キーは版（`Version`）・パス・ファイルの更新日時・サイズ・要求サイズ → `DiskCacheRules.RelativePathOf` でファイル名にする。起動 20 秒後と、上限の 1/8 を書くごとに `Trim`（上限超過なら使っていない順に 8 割まで削除。3 日以上前のファイルは読んだときに日時を更新）。**保存する画像の作り方（大きさ・品質）を変えたら `Version` を上げる。**

`ThumbMode`：`Image`（枠いっぱいに切り抜き）/ `Shell`（全体を収める）/ `Icon`（中央に原寸）。キャッシュのキーはパス・更新日時・大きさ・要求サイズ（256 / 512 に丸める）・種類。

**詳細表示の行（`DetailsRowView`）は `Realize` を呼ばない**。行が使うのは、一覧を作った時点で分かっている名前・更新日時・サイズと、拡張子ごとにキャッシュされる 16 px のアイコン（`IconCache`）・種類の名前（`TypeNameCache`）だけで、サムネイル・カバー探索・中身の件数（`DetailGate`）は動かない。数千件のフォルダでも行ごとの I/O が無い、というのが詳細表示の前提なので、行に「フォルダの中身の数」のような列を足すときは、この前提を崩さない取り方を先に決める。

### 3.3 カスケード（`CascadeController`、§9）

- 状態：`_levels`（0 = カードのオーバーレイ、またはアンカーの横のフライアウト。1 以降 = フライアウト）、起点（`_card`：カード、`_anchor` / `_anchorPlacement` / `_anchorItem`：アンカー = カード以外の起点。サイドバーの行と、詳細表示の行の名前。カードとアンカーはどちらか一方）、`_preview`（最大 1 つ、常にいちばん深い位置）、タイマー 7 つ（`HoverTimings`：overlay 300（非アクティブ時 500）/ open 150 / grace 300 / exit 300 / preview 150 ms、プレビューを閉じるタイマーは grace と同じ、前面ウィンドウの監視 250 ms）
- 開く（カード）：`MainWindow.CardMouseEnter` → `CardMouseEnter` → overlay タイマー → `OpenOverlayAsync`（`CardViewModel.EnsureListingAsync`）。行に止まる → `OnRowHovered` → open タイマー → `OpenChild` → `OpenFlyout` → `LoadLevelAsync` → `Position` / `Place`（行が載っているモニターの作業領域と DPI から物理ピクセルで配置し、Popup のウィンドウを直接動かす）
- アンカー：カード以外の起点は `AnchorMouseEnter(hover, placement, item)` の 3 つで渡す。`hover` はポインターが載る要素で、一覧はその高さに出る（配置の「行」）。`placement` は一覧を横に置く要素（配置の「親カード」）。`item`（`ICascadeAnchor`）は開くフォルダと、起点ごとの違い（`OpensInNewTab`：タブの外の起点か。`YieldsToModifiers`：Ctrl / Shift を押している間は開かない起点か。詳細表示の行が true、サイドバーの行は false）。起点の同一性は `hover` の参照で見るので、同じ起点の `AnchorMouseMove` / `AnchorMouseLeave` には同じ要素を渡す。
- 開く（サイドバー）：`SidebarView.ItemEntered` → `AnchorMouseEnter(row, row, item)` → overlay タイマー → `OpenRoot` → `OpenFlyout(0, …)`（行そのものが `hover` と `placement` の両方なので、行の右に出る）。一覧を開いている間に別の行へ入ったときは、すぐには切り替えず `_pendingAnchor` に置いて overlay タイマーを回し、そこで止まったときだけ切り替える（行が縦に並んでいて、一覧へ斜めに移動すると隣の行を横切るため。一覧に入れば `OnFlyoutEnter` が取り消す）
- 非アクティブなウィンドウでも開く（旧仕様 §9.6 規則 7「非アクティブの間は開かない」はやめた。横に並べて使うと「行に載せても出ず、どこかをクリックすると出る」不具合に見えたため）。旧規則が守っていたことは別の仕組みで守る：通過しただけでは開かない → `RestartOverlay`（非アクティブ時は `HoverTimings.OverlayInactive` = 500 ms）、最前面の Popup がほかのアプリの上に残らない → `WatchForeground`（非アクティブで階層 0 を開いたら前面ウィンドウを 250 ms ごとに見て、替わったら `CloseAll`。`Deactivated` は来ないため）、操作はアクティブなウィンドウで行う → `ActivateWindow`（一覧のクリック・右クリックの前に `Window.Activate`。Popup は `WS_EX_NOACTIVATE` でクリックしてもアクティブにならない。メニューは必ず `ShowMenu` 経由で出す）。ホバーだけではウィンドウを前面に出さない
- 再武装（サイドバー）：ホバーの起点は `MouseEnter` の 1 回だけなので、ポインターが行に載ったまま `CloseAll` されると（非アクティブ化、ドラッグ終了、前面ウィンドウの交代）出入りするまで開かない。`SidebarView.ItemMoved` → `AnchorMouseMove` が、最初に見た位置からシステムのドラッグ距離以上動いたときだけ `AnchorMouseEnter` をやり直す（位置は、起点と一緒に動かない座標系で渡す。サイドバーの行は `SidebarView` の座標）。クリック・右クリック・ドラッグした行は、出るまで再武装しない（`SidebarView._noRearm`。タブを開いた直後に同じ行の一覧が出直さないため）
- PC の行（ドライブ）も登録フォルダの行と同じ `SidebarItemViewModel` で、同じイベント（`ItemEntered` / `ItemMoved` / `ItemLeft`）から同じ経路に入る：開き方・切り替えの保留・再武装・`_noRearm` はどちらの行でも同じ。違いは行が**操作なしで消える**こと（USB を抜く、メディアを取り出す）。消えた要素は `MouseLeave` を出さないので、`DriveListViewModel.RowRemoved` → `AnchorRemoved` が、その行を起点にした一覧を閉じ、その行への切り替えの保留を取り消す。行が増減して起点の行が上下に動いたときは `MainWindow` が `Drives.CollectionChanged` で閉じる（ドライブの行から開いた一覧があるときだけ）。
- 開く（詳細表示の行）：`DetailsRowView` の名前の列（`NameColumn`）の `MouseEnter` → `ICardHost.RowMouseEnter(nameColumn, vm)` → `MainWindow` が `CardAnchor.For(vm)`（フォルダ以外は null で、何もしない）→ `AnchorMouseEnter(nameColumn, nameColumn, anchor)`。以降はサイドバーの行と同じ経路（行が縦に並ぶので、切り替えの保留も同じように効く）。
  - 起点は行全体ではなく**名前の列**（列 0 全体：アイコン・名前・📌・補足・右の余白）。hover と placement に同じ要素を渡すので、一覧は名前の列の右端（更新日時の列の上）に出る。更新日時などの列に載せても開かない（一覧が右の列を覆うので、起点を行全体に広げると、行のどこに載せても列が読めなくなるため）。placement を名前の内容（アイコン + 名前 + 📌 の部分）にしない：名前の右の余白で止まると、ポインターの真下に一覧が開く。
  - 名前の列は背景を Transparent にしてヒットテストに当てる（null だと名前の右の余白で `MouseEnter` が来ない）。行で押下があったあとの `_noRearm` は、名前の列を出るまで再武装しない。
  - `OpensInNewTab` が false なので、一覧の中のフォルダは表示中のタブの中で移動し、タブの中身の都合（`Details.Scrolled`、`Cards` の差し替え）では `CloseCardCascade` で閉じる。
  - 行のコンテナーはスクロールで再利用される（要素は同じまま項目が替わる）。起点の同一性は要素で見ているので、`DetailsRowView.OnDataContextChanged` が `RowMouseLeave` を呼んで、前の項目の一覧・待機を手放す。
  - 再武装は `RowMouseMove` → `AnchorMouseMove`（位置はウィンドウの座標）。ボタンを押した行は、ポインターが名前を出るまで再武装しない（`DetailsRowView._noRearm`。クリックで開いた直後やメニューの表示中に一覧が出直さないため）
- 閉じる：**開閉はすべてここで決める**（Popup は `StaysOpen=True`）。`CloseAll` / `CloseFrom(index)` / `CloseDeepest`（Esc）。右クリックメニュー表示中は閉じない（`ContextMenuFactory.MenuOpened` / `MenuClosed`）。項目のビュー（カードグリッド・詳細表示）の都合（スクロール、項目の作り直し = 表示中タブの `Cards` 差し替え）で閉じるときは `CloseCardCascade`（`IMainView.CloseCardCascade`）：タブの外の起点（`OpensInNewTab` が true のアンカー = サイドバーの行）の一覧は閉じない（タブの読み込み完了が、利用者が見ている一覧を消していた）。カードと、タブの中の項目を表すアンカー（`OpensInNewTab` が false：詳細表示の行）の一覧は閉じる
- クリック：`OnRowClicked` / `OnHeaderClicked` → `MainViewModel.OpenItemAsync`。`ItemOf` / `HeaderOf` は起点の `ICascadeAnchor.OpensInNewTab` を `ItemRef.NewTab` に写すので、サイドバー起点のカスケードではフォルダは新しいフォルダのタブで開く（カード起点は表示中のタブの中で移動）
- Ctrl / Shift（選択の操作）に道を譲る：一覧の項目にぶら下がる一覧（カードのオーバーレイと、`YieldsToModifiers` が true のアンカー = 詳細表示の行）は、Ctrl か Shift を押している間は overlay タイマーが来ても開かず（`OnOverlayTimer` / `OnDetailsLoaded` の `SelectionKeysDown`、切り替えの保留も同じ）、開いていれば Ctrl / Shift を押した瞬間に閉じる（`MainWindow.OnPreviewKeyDown` → `CloseForSelectionKeys`）。Ctrl + クリックで隣の項目を選ぶとき、一覧が次の項目を覆わないため。サイドバーの行の一覧は項目を覆わないので影響を受けない。閉じたあとはポインターが項目を出入りするまで開かない（カードには再武装がない）
- キーボード：`MainWindow.OnPreviewKeyDown` が、カスケード表示中は先に `HandleKey`（↑ ↓ → ← Enter Esc）へ渡す
- ドラッグ：`BeginDrag` で全階層を非表示にしてタイマーを止め、`EndDrag` で本当に閉じる。ドラッグ中の `CloseAll` は無視する（理由は `BeginDrag` の XML コメント）
- エクスプローラーのメニュー（「その他のメニュー（Windows）」）：`ContextMenuFactory.NativeMenuChanged` → `SetNativeMenuOpen`。
  - `true` は「その他のメニュー（Windows）」の Click の中で、設定が「Windows のメニュー」なら右クリック（`ContextMenuFactory.Show`）の中で、**同期的に**（`ShowAsync` より前に）来る。メニューのスレッドが前面を取ると `Deactivated` → `CloseAll` が来るので、その前に凍結しておく。凍結 = `_nativeMenu` と `_suspended` を立てて `StopTimers`。**隠さない**（メニューは一覧の行から出したもので、閉じたらそこへ戻る）。`CloseAll` は何もせず（`_dragging` と同じ）、タイマーの Tick（overlay / open / grace / exit / preview / previewClose / foreground）は先頭で戻る。`_suspended` が要る理由：`CardMouseEnter` / `AnchorMouseEnter` は `CloseAll`（凍結中は何もしない）のあと `_card` / `_anchor` を差し替えるので、開いた階層が別の起点の下に残る。
  - `false` は結果がどれでも（表示しなかった・時間切れも）来る。選ばれたアプリの項目を実行する前、代わりの WPF のメニューを開く前に来る（項目の `BeforeNavigate` の `CloseAll` が凍結で無視されないため。開いた WPF のメニューの間は `MenuOpened` / `MenuClosed` が一覧を保つ）。ウィンドウがアクティブなら `Restart(_exitTimer)`（規則 3 に戻る）。非アクティブなら、メニューのスレッドが前面を返す（前面がまだオーナーのときだけ返す）のを 300 ms 待ってから決める（`AfterNativeMenuAsync`）：アクティブ → `Restart(_exitTimer)`、まだ非アクティブ（ほかのウィンドウをクリックして閉じた、コマンドがウィンドウを開いた）→ `CloseAll`。待つ間に閉じた・別の一覧になった（`_generation` が変わった）ときは何もしない。
  - WPF のメニューの `MenuClosed` は凍結中は exit タイマーを始めない（解除のときに始める）。
- 非同期の結果は、完了時に対象（`_preview` や階層）が差し替わっていないかを確かめてから反映する

### 3.4 プレビュー

`EntryRowViewModel.IsPreviewable` = `ShellPreviewSupport.IsPreviewable`（Core の `PreviewKinds` で対象か、またはシェルがサムネイルを作れる拡張子か）。
`OpenPreview` → `LoadPreviewAsync`：
1. Word / Excel / テキスト：`PreviewTextCache.GetAsync` →（`OfficeText.ReadWord` / `ReadSheet` / `TextPreview.Read`）→ `ShowExcerpt`
2. 抜粋がない・ほかの種類：`ThumbnailService.GetAsync(File)`。`Image` / `Shell` なら表示、アイコンしかなければ「プレビューできない」扱い
3. レイアウト（シートのグリッド、「（以下省略）」）は `PreviewView.xaml.cs`

### 3.5 開く・移動する

- 項目のポインター操作：`CardView` と `DetailsRowView`（詳細表示の行。行のどこを押しても同じ）はマウスのボタンのイベントを `ItemPointerTracker` へ渡すだけで、判定はトラッカーが持つ。押下（1 回目だけを数える：ダブルクリックで 2 回開かない）→ ボタンを**離したとき**にクリックと決める（要素の上で離したときだけ。`ICardHost.ItemClick(vm, element, modifiers)`。修飾キーは離したときの `Keyboard.Modifiers`）→ 押したままシステムのドラッグ距離を越えるか要素を出たらドラッグ（`CanDragItem` が false なら押下はクリックのまま → `ItemDrag`）。右ボタンを離すとメニュー（`ItemContextMenu`）。× と 📌 はボタンなので、押下はクリックにもドラッグにもならない。
  - 処理済みにするのは、右ボタンを押したままの左クリックの押下と離し、クリックになった離し、右ボタンの離しだけ。**普通の左押下は処理済みにしない**（上へ届く）。ドラッグ開始・フォーカス・右ボタンの組み合わせへの影響が読めないので変えない。祖先で左押下に反応する処理は `e.OriginalSource` を見て、項目の上の押下かどうかを分ける。
- カードのクリック → `MainWindow.ItemClick` →（Ctrl / Shift つきなら選択だけ：`ClickSelect`。普通のクリックは選択を外して）`MainViewModel.ActivateCardAsync`、行のクリック → `OpenItemAsync`、サイドバーの行のクリック → `OpenRegisteredAsync`（ドライブの行は `OpenDriveAsync`）
- フォルダ：`ItemRef.NewTab`（サイドバーの行と、そこから出したカスケード）なら `OpenFolderTabAsync`（新しいフォルダのタブ）、それ以外は `NavigateAsync(Target, NavMode.New, recordPath: …)`（表示中のタブの中で移動）。どちらも利用として `RecordUse`。例外はドライブの行そのもの（`ItemContext.Drive`：クリック、メニューの「開く」「エクスプローラーで表示」）で、利用として記録しない（PC の節にいつも出ているので、履歴に `C:\` が並んでも役に立たない）。ドライブの行から出した一覧の行・見出しは、ほかのフォルダと同じく記録する
- ファイル：`OpenFileAsync`：実行形式の確認（`ConfirmExecutables` && `ShellActions.IsExecutable`）→ `Task.Run(ShellActions.Open)` → `RecordUse`（`History.Record` → 最近の履歴・よく使う項目を更新）→ 設定によって `SHAddToRecentDocs`
- 右クリック：`ContextMenuFactory.Show(ItemRef, target)` → `MainViewModel` の `ShowInExplorerAsync`（`ItemRef.IsFolder` ならフォルダ自体を開く `OpenInExplorer` / `ExplorerTabs.OpenAsync`、ファイルなら親フォルダで選択 `ShowInExplorer` / `ExplorerTabs.ShowAsync`。どのタブ・どの位置の項目でも同じ。設定で `ExplorerTabs`）/ `CopyPath` / `TogglePin` / `AddToRegisteredAsync`（未登録のフォルダ → サイドバーに追加）/ `DeleteAsync` / `Unregister`（サイドバーの行）/ `ExcludeFromRecent` / `RemoveFromFrequent`。PC の節の見出しは `ContextMenuFactory.ShowPcSection` → `OpenPcInExplorerAsync`（[§3.10](#310-タブ列とサイドバー)）
- 右クリックのメニューの出し方（設定の「右クリックのメニュー」、`item_menu`）：`ContextMenuFactory.Show` → `ItemEntries` / `GroupEntries`（項目を `MenuEntry` の並びにするだけ。どちらの出し方でも中身は同じ）→ `ShowEntries`。
  - 「アプリのメニュー」（既定）：`Render` で WPF のメニューにし、いちばん下に「その他のメニュー（Windows）」（`AddNativeItem`）。
  - 「Windows のメニュー」：`MainViewModel.UsesWindowsItemMenu`（右クリックのたびに設定を読む。OK のあと再起動なしで効く）が true で、エクスプローラーのメニューの対象がある（`nativePaths`：ドライブの行は null）・使える（`nativeEnabled`：1 項目は使える項目で `IsTransferable`、選択は全部が `CanCopy`）・5 分以内に時間切れになっていない（`TimedOutRecently`）とき → `ShowNativeAsync(paths, at, entries, target)`。右クリックの中で `NativeMenuChanged(true)`（カスケードを凍結）→ `ShellMenuAppEntry`（文字列・有効・区切り・既定）に写して `ShellContextMenu.ShowAsync(..., appEntries)`。ほかは WPF のメニューで、「その他のメニュー（Windows）」は付けない。用意中にもう一度右クリックしたら何も出さない（`_nativeMenus > 0`。WPF のメニューがネイティブのメニューの上に出るため）。
  - メニューのスレッド：`InsertAppEntries` が空のメニューにアプリの項目（ID は `AppFirstId + 並びの位置`）と区切りを入れ、その下の位置から `QueryContextMenu`（シェルの ID は 1〜0x7FFF のまま）→ `RemoveVerbs`（いつもの 5 つ + copyaspath。既定の項目があれば open も）→ `CollapseSeparators` → `SetMenuDefaultItem`（有効な既定の項目を太字に。シェルが付けた既定を置き換える）→ `TrackPopupMenuEx`。選ばれた ID がアプリの範囲なら、シェルの範囲の確認と verb の読み取りより前に `AppCommand`（`AppIndex`）で返す（前面をオーナーへ返してから。確認ダイアログを出す項目があるため）。ハンドラーには何も渡さない。
  - 結果（UI スレッド、`NativeMenuChanged(false)` のあと）：`AppCommand` → `RunEntry`（並びの同じ位置の項目の Action。Click の外なので例外は自分で受けてログとメッセージ行へ）。`TimedOut` → ログ、`RememberTimedOut`（パスごとに `Environment.TickCount64`、`PathTools.Comparer`）、メッセージ行に案内（`Info`）、`OpenFallback`。`Unavailable` と表示前の `Failed`（`Shown` が false）→ ログだけで `OpenFallback`（警告は出さない）。それ以外（`Invoked` / `Dismissed` / 表示後の `Failed`）は「その他のメニュー（Windows）」と同じ `OnShellMenuClosedAsync`。
  - `OpenFallback`：同じ並びの WPF のメニューを、右クリックの画面上の位置（`NativeMenuPoint`。物理ピクセルを基準の要素の DIP に直して `PlacementMode.Relative`）に、await を挟まずすぐ開く。基準は右クリックした要素（見えていて `PresentationSource` があるとき）、なければメイン ウィンドウ、どちらもなければ出さない。
  - 設定に関係なく WPF のメニューのまま：ドライブの行（「フォーマット」が並ぶため）、PC の節の見出し（`ShowPcSection`）、項目の領域の空白（`ShowBlank`）、見つからない・応答なしの項目とドライブそのもの、時間切れから 5 分以内の項目。
- 空白に落ちた項目の右クリック：タブの読み込み中は項目のビューがヒットテストを止めるので、項目の上の右クリックが `MainWindow.OnContentRightUp`（項目の領域の空白）まで来る。`IsOnItemOrControl` で弾かれなかったら、`ActiveList.RealizedItemAt(e)`（`ItemsViewHitTest.Find`：位置だけで実体化済みの `CardView` / `DetailsRowView` を探す。表示領域の外と詳細表示の見出しは対象外）で項目が見つかればログを 1 行（パスは書かない）出して `ItemContextMenu(item, view)`（普通の項目の右クリックと同じ経路。選択の扱いも同じ。`CloseAll` はしない）、見つからなければ今までどおり `CloseAll` → `ShowBlank`。
- 右ボタンを押したまま左クリック（「エクスプローラーで表示」の近道）：`ItemPointerTracker`（カード、詳細表示の行）/ `EntryListView` / `SidebarView` がそれぞれ Core の `RockerGesture<T>` を 1 つ持ち、見えたボタンのイベントを渡す。左ボタンの押下時に `e.RightButton == Pressed` なら `LeftDown` が対象（カード・行）を覚え（その左クリックは開く動作にもドラッグにもならない）、**右ボタンを離したとき**に `RightUp` が対象を返す → `ICardHost.ItemShowInExplorer` / `RowExplorerRequested` / `ItemExplorerRequested` → `ShowInExplorerAsync`。右クリックメニューは出さない。
  - 離したときに実行するのは、ボタンを押したまま別のウィンドウ（エクスプローラー）が前面に来ると、右ボタンを離した通知がそのウィンドウへ行ってしまうため。
  - **マウスキャプチャは使わない**。一覧の行（`ListBox`）は左ボタンを離すたびに、処理済みのイベントでも自分のキャプチャを手放す（WPF の `ListBox` のクラスハンドラー）ので、キャプチャで待つ方式だと行の上で待ち状態が消え、右クリックメニューが出ていた。代わりに、古い待ち状態が残りうる場面で取り消す：新しい右ボタンの押下（`PreviewMouseRightButtonDown`、処理済みでも受ける）、右ボタンなしの左クリック、ポインターがビューを出たとき（`Cancel`）。
  - カードに重なった一覧（オーバーレイ）の行の上では行が対象（行の一覧が先に受ける）。一覧の空白では、下のカードが対象になる。
- ごみ箱への削除：対象は `CanDelete`（`ItemContext.InnerCard` か `Row`、かつ見つからない・応答なしでない。最上位のカードとフライアウトの見出しは対象外）。
  - 入口は 3 つ：1 項目のメニュー（`ContextMenuFactory.Show`）、選択全部のメニュー（`GroupEntries`。全部が `CanDelete` のときだけ出す）、Delete キー（`MainWindow.HandleSelectionKey`。**選択だけ**が対象で、選択が空なら使わない（ポインターの下の項目・カスケードのキー操作で強調した行は対象にしない）。修飾キーなしのときだけ、`e.IsRepeat` は無視、テキスト ボックスの中では動かない。Shift + Delete・Ctrl + Delete は扱わない）。どれもカスケードを先に閉じる（メニューは `BeforeNavigate`、キーは `CloseAll`）。
  - `DeleteAsync(items)`：1 つでも `CanDelete` でなければ全体を断る（一部だけ消さない）→ `_fileOpBusy` なら断る → `IMainView.Confirm` を 1 回（危険操作。既定は「キャンセル」。1 項目は名前、複数は件数と先頭 5 件のパス + 「…ほか n 個」：`DeleteConfirmText`）→ 確認のあいだに別の操作が始まっていたら断る → `_fileOpBusy` を立て、カードを `IsBusy` → `RecycleBin.MoveAsync(paths, IMainView.WindowHandle)`（1 つの `IFileOperation`）→ 件数付きのメッセージ（`ShowDeleteResult`：一部だけごみ箱に入ったら「n 個をごみ箱に移動し、残りの m 個は…」）→ `ReloadAfterFileChangeAsync` を 1 回（`ClearCaches`、全タブを `IsStale`、表示中のタブをスクロール位置を保って読み直す。中止・失敗でも読み直す）。選択は読み直しの `Prune` に任せる（消えた項目は外れ、残った項目は選択のまま再試行できる）。
- 選択（タブごと。`TabViewModel.Selection` = Core の `SelectionModel`、キーはパス）：Ctrl + クリックで 1 つずつ、Shift + クリックで起点からの範囲、Ctrl + Shift + クリックで範囲を足す（`MainViewModel.ClickSelect`）。Ctrl + A で全部、Esc（カスケードが先に受ける）・普通のクリック・項目の領域の空白の押下で解除。見つからない・応答なしの項目は選べない（`SelectableKeys`）。
  - カードは読み込み・並べ替えのたびに作り直されるので、選択はパスで持ち、`TabViewModel.Show` が表示から消えた項目を外して（`Prune`）新しいカードへ `IsSelected` / `IsCut` を写す（`ApplyItemFlags`）。現在地が替わると（`OnLocationChanged`）、読み込みに失敗すると（`ClearCards`）空になる。
  - 空白の押下での解除は `MainWindow.ContentHost`（背景は透明）で受ける。カードの左押下は処理済みにならないので、`OnContentLeftDown` は `e.OriginalSource` の祖先にカード・行・スクロール バー・ボタン・つまみがあれば何もしない（無条件に外すと、離したときのクリックの判定より先に選択が消える）。スクロール ビューアーが押下を処理済みにすることがあるので `handledEventsToo` で受ける。
  - 選択の中の項目に対する操作は選択全部が対象（`SelectionTargets`。表示順）：右クリックメニュー（複数なら「切り取り」「コピー」「パスをコピー」「削除（ごみ箱へ移動）」）、ドラッグ（[§3.7](#37-他アプリへのドラッグ)）、Ctrl + C / X、Delete。選択の外を右クリックすると選択を外して、その項目だけのメニュー。カスケードの行・見出しは選択に入らず、いつも 1 項目。
- 切り取り・コピー：`MainViewModel.CopyToClipboard(items, cut)` → `ShellClipboard.TryWrite`（UI スレッド。エクスプローラーと同じ `CF_HDROP` + "Preferred DropEffect"）。Ctrl + C / X は**選択だけ**が対象で、選択が空なら何もしない（ポインターの下の項目は使わない）。切り取れるのは `CanCut`（削除と同じ規則：フォルダの中の項目だけ。最近の履歴・よく使う項目の一覧のカード、見出し、サイドバーの行は不可）、コピーは `CanCopy`（使える項目。ドライブそのものは不可）。
  - 切り取りは全タブの `CutPaths` に入れて薄く表示し、書き込んだときのシーケンス番号を覚える。クリップボードがほかの内容に替わったら（`MainWindowHook.ClipboardChanged` → `OnClipboardChanged`：番号が違う）、ウィンドウに戻ったとき切り取った項目が消えていたら（`OnActivatedAsync`。エクスプローラーで貼り付けると移動はエクスプローラーが行う。確認はワーカーで 2 秒まで）、切り取りの印を消す。一部だけ消えていたら（衝突で「スキップ」した残り）、残った項目だけを切り取りのまま薄く表示し続ける。クリップボードが替わって終わった切り取りも、次に戻ったとき一度だけ確かめ、消えていれば一覧を読み直す（エクスプローラーが移動してからクリップボードが替わった場合）。貼り付けで全件を移動し終えても、転送中に新しく切り取った分（シーケンス番号が違う）は残す。
- 貼り付け：Ctrl + V、項目の領域の空白の右クリック（`ShowBlank`）、フォルダとドライブの行のメニュー → `MainViewModel.PasteAsync(folder)`。先は `PasteFolderOf`：フォルダの項目ならそのフォルダ、項目なしなら `SelectedTab.TransferTargetPath`（そのフォルダの読み込みが成功して画面に出ている間だけ。最近の履歴・よく使う項目の一覧とタブなしでは null → 「この一覧には貼り付けできません」）。
  - 順序：`ShellClipboard.ReadWithSequenceAsync`（2 秒）→ `SourceProbe.FolderExistsAsync`（先）→ `SourceProbe.DescribeAsync`（元）→ `TransferRules.Decide(..., TransferOptions.Paste(kind))` → 全部断られたら理由をメッセージ行へ、そうでなければ `FileTransfer.RunAsync` → 結果（先のフォルダ名、移動できずコピーした数）→ `ReloadAfterFileChangeAsync` → 表示中のタブが先なら届いた項目を選択（`Selection.Replace(NewPaths)`）。アプリ自身のダイアログは出さない（名前の衝突・進行状況はシェルのもの）。
  - 切り取りの貼り付けは、移動が全部済んだとき（`MovesDone == MovesRequested`）だけクリップボードを空にし（`ClearIfUnchanged`：読んだあと誰も書き換えていなければ。ほかのアプリの切り取りでも同じ）、切り取りの印を消す。一部でもスキップ・失敗したら残す（残りをもう一度貼り付けられる）。ネットワーク上の元からの切り取りも移動のまま（エクスプローラーと同じ）。
  - 一度に 1 つ（`_fileOpBusy`。貼り付けとごみ箱への削除で共有。2 つ目は「前の操作が終わってから実行してください」。実行中は `CanPaste` も false）。押しっぱなしの Ctrl + V はキーの繰り返し（`e.IsRepeat`）を無視する。実行中（`IsFileOperationRunning`）にウィンドウを閉じようとすると確認する（`MainWindow.OnClosing`。転送・ごみ箱への移動はバックグラウンド スレッドなので、黙って閉じると途中で切れる）。
- エクスプローラーのメニュー：右クリックメニュー（アプリのメニュー）のいちばん下の「その他のメニュー（Windows）」→ `ContextMenuFactory.ShowNativeAsync`（`NativeMenuChanged(true)` → `ShellContextMenu.ShowAsync`（`Show` のときのポインター位置と Shift、オーナーにする `IMainView.WindowHandle`。アプリの項目なし）→ `NativeMenuChanged(false)` → `MainViewModel.OnShellMenuClosedAsync(result)`）。「Windows のメニュー」の経路は上の「右クリックのメニューの出し方」。
  - `Invoked` だけが読み直す（`ReloadAfterFileChangeAsync`）。さらに `_shellCommandAt` を記録し、1 秒（`ShellCommandHandBack`）より後の次の `OnActivatedAsync` でもう一度読み直す：7-Zip の展開などはメニューが戻った後に別のプロセスで続く。1 秒より前のアクティブ化はメニューのスレッドが前面を返しただけなので数えない（記録は残す）。`Dismissed`（除外した verb を選んだ・前面を取れずに出さなかったも含む）は何もしない：一覧の読み直しはカスケードを閉じるため、出した一覧が残らなくなる。
  - `Unavailable` / `TimedOut` / `Failed` はメッセージ行に「このメニューは表示できませんでした：理由」（警告）。`Invoked` の `Error`（コマンドの失敗）は出さない（ハンドラーが自分で出すことが多い。ログには残る）。
  - `NativeMenuChanged(false)` を `OnShellMenuClosedAsync` より先に出す：読み直しの `CloseCascade` は凍結中だと無視される。
  - 複数の選択は 1 つのメニュー（エクスプローラーで複数選んだときと同じ）。親が違う項目（最近の履歴の一覧）は item array で、取れなければ `Unavailable`。

### 3.6 並べ替え・ピン留め

- 並び方の変更：並べ替えメニュー（`MainWindow` が `CardSortModes.All` から組み立てる）→ `MainViewModel.SetSortMode` → `SaveSortMode`（`settings.json` の `sort_modes`。キーは `TabKinds.ToSetting`：`recent` / `frequent` / `folder`）→ `TabViewModel.ApplyOrder`。フォルダのタブは 1 つのキーを共有するので、開いているほかのフォルダのタブにも同じ並び方を適用する
  - 詳細表示では列の見出しのクリックでも変わる：`DetailsListView.HeaderClicked` → `MainWindow` → `SetSortMode(CardSortModes.Next(いまの並び方, 列))`。同じ列なら向きを反転、別の列なら名前・種類は昇順、更新日時・サイズは降順から始める。見出しからは「手動」に戻れない（戻すのはツールバーのメニュー）。並び方はタブのもので、表示モードとは独立（カード表示でもサイズ順・種類順を選べる）
  - サイズ順はフォルダとサイズの分からない項目を、どちらの向きでも最後に置く。種類順のキーは**拡張子**で、行に出している種類の名前ではない（名前は `TypeNameCache` から後で届くので、それをキーにすると読み込み中に行が動く）。どちらも同順は名前の昇順（降順にしても逆にならない）
- フィルター（固定タブの最上位）：`MainWindow.OnFilterClick`（`ItemFilters.Values` からメニュー）→ `MainViewModel.SetFilter` → `settings.json` の `filter_modes` → `TabViewModel.Filter` → `OnFilterChanged` → `Show`（`_orderedCards` を絞り込んで `Cards` へ）
- ドラッグで並べ替え（固定タブの最上位）：`ItemPointerTracker` → `MainWindow.ItemDrag` → `FileDragOut.CreateData(reorder: true)` → `ActiveList.RunReorderDrag`（`IItemsView`。`CardGrid` はカードの間の縦線、`DetailsListView` は行の間の横線で入る位置を示す）→ `MainViewModel.MoveCard` →（フィルター中は `ItemFilters.MergeShownOrder` で、隠れているカードの位置を保ったまま全体の並びに戻す）→ `TabViewModel.SetBaseOrder` → `SaveManualOrder`（`CardOrderStore.Set`）。「手動」以外の並び方のときは「手動」に切り替える
- サイドバーの並べ替え：`SidebarView.ItemDragRequested` → `MainWindow.OnSidebarDrag` → `SidebarView.RunReorderDrag`（ドロップを受けるのは登録フォルダの行の領域だけ。見出しやクイック アクセスの上では、ウィンドウが自アプリ由来のドラッグとして断る）→ `MainViewModel.MoveRegistered` → `RegisteredFoldersStore.Reorder` → `RefreshRegistered`
- ピン：`TogglePin` → `PinStore.Pin` / `Unpin` → `PinStore.Changed` → `OnPinsChanged` → 各タブ（固定タブとフォルダのタブ全部）の `ApplyPins`。「手動」以外の並び方でピン留めするときは `KeepInPlace` → `PlaceInBase` で表示中の位置（`OrderedCards` での位置）に固定。カード・詳細表示の行の 📌 のクリックは `CardView.OnPinClick` / `DetailsRowView.OnPinClick` → `ICardHost.ItemUnpin`（`MainWindow`：カスケードを閉じて `TogglePin`。見つからない・応答なしのカードでは無効）
- 表示モード（全タブ共通。`CardShape`：縦長 / 正方形 / 詳細）：ツールバーの `RadioButton` → `SetCardShapeCommand` → `MainViewModel.CardShape` → `OnCardShapeChanged`（カスケードを閉じ、`settings.json` の `card_shape` に保存）。カードの形どうしの切り替えは、バインドで `CardGrid.CardShape` → `Rebuild` → `GridMetrics.ThumbHeight`
  - カード ↔ 詳細の切り替えでは項目のビューが入れ替わる：`MainWindow.xaml` のトリガーが `CardGrid` と `DetailsListView` の片方だけを表示し、`Items`（`SelectedTab.Cards`）も片方にだけ渡す（隠れた側は null）。`MainWindow.ActiveList` は `CardShape` から同じ側を返す
  - スクロール位置はビューごとの単位（カードの行の高さと 28 px の行は対応しない）なので、切り替えのとき `OnCardShapeChanged` が全タブの `ResetScrollOffsets`（離れたときの位置と、戻る・進むの履歴の位置）を呼び、表示中のビューを先頭へ戻す。順序に意味がある：`OnCardShapeChanged` は `CardShape` の変更通知より**前**に走るので、`View.ScrollTo(0)` は新しい側（まだ項目なし）に届く
  - 詳細表示ではカードの大きさのスライダーを無効にする（値は保持）。`RadioButton` の `Checked`（`OnViewModeChecked`）でも `CardShape` を入れる：UI オートメーションの「選択」はコマンドを実行せずにチェックだけを入れるため
- 詳細表示の列の幅：見出しのつまみ（列の**左端**。列は右寄せで並び、名前が残りを取るので、動くのは左の境界）をドラッグ → 離したとき `DetailsListView.ColumnResized` → `MainViewModel.SetDetailsColumnWidth` → `DetailsColumns`（差し替え）と `settings.json` の `details_columns` → バインドで `DetailsListView.Columns` → `UpdateColumns`（`DetailsColumns.Fit` → 見出しの列と `DetailsMetrics`）。保存するのは利用者が決めた幅だけで、ウィンドウが狭くて縮めた表示用の幅は保存しない。名前の列は保存しない（幅を変えられない）
- 規則（ピン留めは位置固定、新規項目の挿入位置）は Core の `Ordering/` にある。App 側で再実装しない

### 3.7 他アプリへのドラッグ

カード・行・プレビュー・サイドバーの行で、押したまま `FileDrag.IsDragGesture` のしきい値を超えたら開始 → `FileDragOut.CreateData`（`DataFormats.FileDrop` + 内部形式 `FolderDeck.DragSource`、最上位カードは `FolderDeck.CardReorder` も、サイドバーの行は `FolderDeck.SidebarReorder` も。見つからない登録フォルダの行は FileDrop なしで並べ替えだけ）→ `CascadeController.BeginDrag` → `FileDragOut.Run`（`DoDragDrop`、許可する効果は **Copy | Link のみ**）→ `EndDrag`。
選択の中の項目をドラッグすると選択全部（表示順。`MainViewModel.SelectionTargets`）を渡す。並べ替えの形式を付けるのは最上位のカード 1 つのときだけ。アプリの中でファイルを動かすドラッグはない（アプリの中のコピー・移動は貼り付けだけ）。
ドラッグ元はメインウィンドウか `CardGrid` / `SidebarView`（ドラッグ中に破棄されない要素）。エクスプローラーからのドロップ登録（`MainWindow.OnDrop` → `MainViewModel.RegisterDroppedAsync`）はウィンドウ全体で受けるが、自アプリ由来のドラッグ（`IsOwnDrag`）は受け付けない。

### 3.8 永続化

- `DataStores` が 6 つのファイルを持つ（一覧は README の「データの置き場所」）。
- `StoreBase`：変更はメモリ上で `Gate` をロックして行い、`RequestSave` でバックグラウンド保存を予約（連続した変更は 1 回にまとまる）。書き込みは `JsonFile.WriteAtomic`（一時ファイル経由で置き換え）。終了時は `Flush`。
- 読み込み：壊れた JSON は `<名前>.broken-<UNIX秒>.json` に退避して空のデータで続ける（`LoadStatus.Recovered`）。退避もできなければ `Failed` → `IsDisabled` になり、そのファイルには以後書き込まない。
- JSON は snake_case（`JsonNamingPolicy.SnakeCaseLower`）、インデントつき。時刻は `LenientTimeConverter`（読めない値は null）。
- `SettingsStore.Current` は**毎回コピー**を返す。変更は `Settings.Update(s => …)`（→ `Normalize` → `RequestSave`）。
- **`settings.json` の版**：`version` 2 が現在の形（`open_tabs` にタブ列の全タブ、`selected_tab` に表示中のタブの位置）。version 1（常設の固定タブ 2 つ + フォルダのタブ：`last_tab` / `locations` / `selected_folder_tab`）は `AppSettings.Normalize` が `MigrateLegacyTabs` で移す（最近の履歴・よく使う項目・フォルダのタブの順に並べ、表示していたタブを選ぶ）。
  - 移行の条件は「`version` が 2 未満、**または** `last_tab` がある」。旧版の exe は version 2 のファイルを読むと `kind` を落として自分のキーを書き戻すので、そのあと新しい版で開いたときにもう一度移行できる（ダウングレードしてもタブは失われない。フォルダの中にいた固定タブはフォルダのタブになる）。
  - 旧形式のキーは移行後 null にし、**null のときは書き出さない**（`JsonIgnore(WhenWritingNull)`）。旧版は `selected_folder_tab: null` を数値として読めず、ファイル全体を壊れた扱いにするため。
  - `open_tabs` が無い（null）ときだけ初回起動として「最近の履歴」を開く。保存された空の配列は「全部閉じた」なので空のまま（`selected_tab` は −1）。`Normalize` は種類を正規化し（不明・なしは `folder`）、ルートのないフォルダのタブと 2 つ目以降の固定タブを落とし、落とした分だけ `selected_tab` をずらす。
- `details_columns`（詳細表示の列の幅）は `Normalize` が `DetailsColumns.Normalize` に通し、いつも `modified` / `type` / `size` の 3 つだけにする（無いキーは既定値、範囲外は 48〜600 に収める、知らないキーは捨てる）。
- `registered_folders.json` は imgtool と同じ形式のまま保つ。
- ストア以外に、画像サムネイルのキャッシュ `thumbs\`（`ThumbDiskCache`、[§3.2](#32-カードの段階読み込み741)）がある。消えても作り直すだけのデータで、`DataStores` には入らない。

### 3.9 テーマ

`ThemeManager` が `App.xaml` の MergedDictionaries の 0 番を `Themes/Light.xaml` ↔ `Dark.xaml` に差し替える。色は `DynamicResource` で参照する。

- どちらを使うかは設定のテーマ（`AppSettings.Theme`：`system` / `light` / `dark`）で決まる：`AppThemes.IsDark(Mode, Windows のモード)`。「システム」のときだけ Windows のアプリモードを使い、`SystemEvents.UserPreferenceChanged` で追従する（固定のテーマでは変わらない）。
- 起動時：`App.OnStartup` がまず `Theme.Apply()`（設定を読む前なので Windows のモード）、ストアを読み込んだあとウィンドウを作る前に `Theme.Apply(AppThemes.FromSetting(settings.Theme))`。
- 設定ダイアログ：`SettingsViewModel.Theme` → OK → `MainViewModel.OpenSettings` が変更を見て `IMainView.ApplyTheme` → `MainWindow` → `ThemeManager.Apply(AppTheme)`（開いているウィンドウのタイトルバーも切り替える）。

### 3.10 タブ列とサイドバー

- **タブ列は 1 本**：`MainViewModel.Tabs` が開いているタブを左から順に全部持つ（`FolderTabStrip` が表示）。「最近の履歴」「よく使う項目」（固定タブ `RecentTab` / `FrequentTab`）はセッション中ずっと同じインスタンスで、`Tabs` にある間だけ開いている（`TabViewModel.IsOpen`）。フォルダのタブ（`TabKind.Folder`）は何個でも開ける。どの種類も閉じる・並べ替えができる。
- **タブなし**：タブを全部閉じると `SelectedTab` は代役の `NoTab`（`TabKind.None`、`IsPlaceholder`）になる。`SelectedTab` を null にしないための代役で（XAML のバインドと `SelectedTab.…` の参照がそのまま通る）、`Tabs` に入れず、読み込まず、移動先にもしない。表示中のタブを相手にする処理は `HasTab` で弾く（`NavigateAsync` / `BackToTop` / `GoUp` / `SetSortMode` / `SetFilter` / `ClearHistory` / `Refresh` / `ReloadAfterFileChangeAsync`）。`MainWindow` は `HasTab` が false の間ツールバーを隠し、案内文と「最近の履歴を開く」「よく使う項目を開く」のボタンを出す。
- **固定タブを開く** `OpenQuickTabAsync(kind)`（サイドバーの「クイック アクセス」の行、タブなしの画面のボタン）：開いていなければタブ列の右端に足して表示、開いていれば表示、表示中でフォルダの中にいれば一覧（最上位）へ戻る。
- **サイドバーの節**：`SidebarView` が「クイック アクセス」「登録フォルダ」「PC」の 3 つの節を縦に並べる。節の状態は `SidebarSectionViewModel`（`MainViewModel.QuickSection` / `RegisteredSection` / `PcSection`）が持ち、見出しのクリック（`ToggleCommand`）で開閉する。開閉は `MainViewModel.OnSectionChanged` がすぐ保存し（`sidebar_collapsed_sections`：**畳んだ節の ID だけ**を持つので、あとから増えた節は開いた状態で始まる）、`View?.CloseCascade()` を呼ぶ（下の行が動くので、行の横の一覧が置き去りになる）。高さの配分は `SidebarView.ApplyRowHeights`：登録フォルダを開いている間はその行の領域が残りを全部取り（中でスクロール）、PC の行はその下に必要な高さだけ、サイドバーの高さの 4 割まで（超えたら中でスクロール）。登録フォルダを畳んでいる間は PC の行が残りを取り、両方畳んでいる間は末尾の空き行が取る（見出しが上に詰まったままになる）。4 割の上限は行（`RowDefinition`）ではなく `PcScroller` の `MaxHeight` に付ける（Auto の行は中身を高さ無制限で測るので、行に付けても `ScrollViewer` がスクロールしない）。
- **クイック アクセスの行**：`RecentTab` / `FrequentTab` を `DataContext` にしたボタン。`TabViewModel.IsSelected` で塗り、`IsOpen` で右端に点を出す（タブ列と同じ状態を見ているので、別に同期するものはない）。カスケードの起点にはならない。
- **サイドバー全体の表示・非表示**：タブ列の左端のボタン（`ToggleSidebarCommand`）→ `IsSidebarCollapsed` → 保存（`sidebar_collapsed`）と `CloseCascade` → `MainWindow.ApplySidebarLayout` が列の幅・`SidebarPane`・`GridSplitter` を切り替える。隠している間も `SidebarWidth`（`sidebar_width`）は元の幅のまま持ち、戻すときに使う。隠している間のドロップ登録は受けるが、案内（`DropHint`）は出さない。`MainViewModel` はサイドバーの幅と折りたたみを**コンストラクタで**読む（`MainWindow` のコンストラクタが最初のレイアウトに使うため。`InitializeAsync` では遅い）。
- **登録フォルダの行**：`MainViewModel.Registered`（`SidebarItemViewModel`）を `SidebarView` が 1 行ずつ表示。`RefreshRegistered` が `RegisteredFoldersStore.Folders` と同じ順に揃える（残る行のインスタンスと要素は保つ：開いているカスケードがその要素に付いているため、`Clear` して作り直さない）。新しい行は `CheckRegisteredAsync`（`Existence.ProbeAsync`、2 秒）で `SidebarItemState` を決める。F5（`Refresh`）は全部の行を確認し直す。
- **PC の節（ドライブ）**：`MainViewModel.DriveList`（`DriveListViewModel`）の `Drives` を、登録フォルダと同じ行のテンプレートで表示する。
  - `RefreshAsync(recheck)`：`Task.Run(DriveCatalog.Enumerate)`（文字と種類だけ。媒体に触らない）→ ドライブ文字で突き合わせて行を揃える（残る行のインスタンスは保つ。登録フォルダと同じ理由）→ 新しいドライブと、前回使えなかったドライブを `DriveCatalog.Probe` で確認する。`recheck` が true なら全部を確認し直す（ラベルやディスクの入れ替え、ネットワーク ドライブの切断を拾う）。
  - 呼ぶのは 4 か所：起動（`InitializeAsync`。**待たない**：応答しないドライブが最初のタブを遅らせない）、F5（`Refresh`、recheck）、ボリュームの着脱（`MainWindowHook.DrivesChanged`、recheck：カード リーダーは文字が同じままメディアだけ替わる）、ウィンドウのアクティブ化（`OnActivatedReadDrives`、5 秒に 1 回まで、recheck なし：ネットワーク ドライブの割り当て・再接続では Windows がメッセージを送らないため）。
  - 確認は 1 ドライブにつき同時に 1 つ（`Slot.Probing`。走っている間に来た要求は終わってからもう 1 回）。2 秒で答えなければ行を「応答がありません」（`SidebarItemState.NoResponse`）にし、答えは来たときに反映する。古い結果を捨てる仕組みは 2 つ：一覧は `_generation`（あとから始めた読み直しがあれば捨てる。捨てられた側の recheck は `_recheckPending` で引き継ぐ）、確認は「そのドライブの `Slot` がまだ同じものか」（`IsCurrent`）。
  - 出すかどうかは `DriveRules.ShouldShow`：固定ディスクとネットワーク ドライブは確認の前から出す（確認中は `Checking` で、使える行と同じ見た目）。リムーバブルと光学ドライブは、確認でメディアがあると分かってから出す（空のカード リーダーは出さない）。使えないドライブ（接続されていないネットワーク ドライブなど）は `SidebarItemState.Missing` で、登録フォルダの「見つからない」と同じ見た目・同じ扱い（一覧もタブも開かない）。ツールチップの文言だけ違う。
  - クリックは `OpenDriveAsync` → `OpenFolderTabAsync(Target)`（`recordPath` なし）。ドラッグはしない（`SidebarView.TryStartDrag` がドライブの行では何もせず、押下はクリックのまま）。右クリックは `ItemContext.Drive` のメニュー（ピン留め・削除・登録の解除は出さない）。接続されていないドライブでも「エクスプローラーで表示」と右ボタン + 左クリックは通す（エクスプローラーが再接続を案内する）。
  - 見出しの右クリックは「エクスプローラーで開く」だけのメニュー：`SidebarView.OnPcHeaderRightUp` → `PcHeaderContextMenu` → `MainWindow.OnPcHeaderContextMenu`（カスケードを閉じる）→ `ContextMenuFactory.ShowPcSection` → `MainViewModel.OpenPcInExplorerAsync`。PC はパスではなく `ShellActions.ThisPcPath`（`::{20D04FE0-…}`）。設定（`OpenExplorerInNewTab`）がオンなら `ExplorerTabs.OpenAsync`、オフなら `Task.Run` で `ShellActions.OpenInExplorer`（explorer.exe）。利用記録には残さない。行で構えた右ボタン + 左クリック（`RockerGesture`）を見出しの上で離したときは、メニューを出さずにその行をエクスプローラーで表示する（`ItemExplorerRequested`）。
- **登録・解除**：登録フォルダの見出しの右のボタン（`BrowseCommand`。節を畳んでいても押せる）、ウィンドウへのドロップ（`RegisterDroppedAsync`）、右クリックの「登録フォルダに追加」（`AddToRegisteredAsync`）→ どれも `RegisteredChanged` → `RefreshRegistered`。解除は行の右クリック →`Unregister`（確認あり）。
- **フォルダのタブを開く**：行のクリック（`OpenRegisteredAsync`）と、サイドバー起点のカスケードの行・見出し（`ItemRef.NewTab`）→ `OpenFolderTabAsync(target)`：同じ場所（`SameLocation`）を表示している**フォルダのタブ**があればそれを選び（固定タブが同じフォルダの中にいても別扱い）、なければ `AddFolderTab` で作って `ShowTabAsync`。`Root` が空の移動先では何も開かない。フォルダのタブの中のカード・パンくず・戻る／進むは `NavigateAsync`（そのタブの中で移動。`target.Tab == Folder` で表示中がフォルダのタブならそのタブ、固定タブやタブなしなら `OpenFolderTabAsync` へ回す。移動先が開いていない固定タブなら、タブ列に足してから移動する）。
- **タブの切り替え** `ShowTabAsync`：離れるタブのスクロール位置を `SavedScrollOffset` に保存（`LeaveSelectedTab`。閉じたばかりのタブと `NoTab` では保存しない）→ `SelectedTab` を替える（`OnSelectedTabChanged` が `IsSelected` を更新）→ 固定タブの最上位は毎回読み直し、フォルダは `IsStale` のときだけ読み直す → スクロール位置を戻す。`NoTab` へ替えるときは読み込みもスクロールもしない。
- **閉じる・並べ替え**：`CloseTabCommand`（× ・中クリック・Ctrl + W・エラー表示の「タブを閉じる」= `BackToTopCommand`）はどの種類のタブも閉じ、表示中だったら右隣（なければ左隣、タブが無くなれば `NoTab`）を表示。`RemoveFromStrip` がタブ列から外す：フォルダのタブは監視（`Watch`）ごと捨て、固定タブは `TabViewModel.ResetToTop`（最上位へ戻し、戻る／進むの履歴・カード・スクロール位置を捨てる）で次に開いたとき一覧から始まるようにする。閉じた固定タブもインスタンスは生きているので、監視は外さず、`AllTabs`（固定タブ 2 つ + 開いているフォルダのタブ）経由で `IsStale` も付け続ける。`CloseOtherTabsAsync`、`MoveTab`（`FolderTabStrip` のドラッグ）。
- **履歴**：戻る・進むはタブごと（`TabViewModel.BackStack` / `ForwardStack`）。タブをまたいでは戻らない。
- **保存**：`SaveState` が `open_tabs`（各タブの種類 + `Root` + `Rel`、タブ列の順）・`selected_tab`（タブなしは −1）・`sidebar_width` を書く（サイドバーの折りたたみは変えたときにその場で保存するので、ここでは書かない）。`open_tabs` / `selected_tab` を書くのは `InitializeAsync` がタブ列を復元し終えたあと（`_tabsRestored`）だけ：起動が途中で失敗したときに、作りかけのタブ列で保存済みの内容を上書きしないため。起動時に復元したフォルダのタブは、フォルダが無くなっていてもタブを残してエラー表示にする（固定タブは最上位へ戻す：`_restoring`）。
- **表示**：`FolderTabStrip` のテンプレートが `Kind` でアイコンと色を分ける（固定タブは `Brush.Accent` / `Brush.AccentSoft`、フォルダのタブは `Brush.Folder` / `Brush.FolderSoft`）。ツールバーのボタンは `SelectedTab` の `ShowFilter` / `ShowClearHistory` / `CanGoUp` / `BackText` で出し分ける（`NoTab` ではどれも出ない）。

## 4. スレッドと非同期の規約

- **UI スレッドでブロッキング I/O をしない**。列挙・存在確認・JSON 読み込みは `Task.Run` か `FolderListingCache` の中で行う。
- シェル / COM の呼び出しは `StaWorkerPool` 上でタイムアウトつき（`ThumbnailService` は 4 本・5 秒、`AppServices.Shell` は 1 本、詳細表示の種類の名前 `TypeNameCache` は専用の 1 本・5 秒）。タイムアウトしたワーカーは放棄して作り直す（最大 16 本）。タイムアウトは**呼び出しが始まってから**数える（キューで待つ時間は含めない。画像の多いフォルダで待ち行列が長いだけの要求を打ち切らないため）。60 秒たっても始まらない要求は `TimeoutException`。
  - 画像のデコード（`ImageDecoder`）は COM の STA を必要としないが、同じ `StaWorkerPool` を使う：`ThumbnailService` のデコード用ワーカー（コア数 − 1 本を 2〜8 本に丸める、優先度 BelowNormal、5 秒）。ディスクキャッシュの書き込み・刈り込みは `ThumbDiskCache` の 1 本（優先度 Lowest、タイムアウトなし）。
  - 例外：ごみ箱への移動（`RecycleBin`）とコピー・移動（`FileTransfer`、`StaThread.Run`）は操作ごとに専用の STA スレッドを作り、タイムアウトをつけない（シェルの進行状況・名前の衝突・「使用中」などのダイアログが利用者の操作を待つため）。UI スレッドは `await` で待つだけで止まらない。このスレッドはバックグラウンド スレッドなので、実行中にウィンドウを閉じると途中で切れる（`MainWindow.OnClosing` が確認する）。
  - エクスプローラーのメニュー（`ShellContextMenu`）は 1 回ごとに専用の STA スレッド（`StaThread.RunPumped`：Dispatcher を回す）と、そのスレッドのオーナー ウィンドウ（メイン ウィンドウに所有させない：スレッドをまたぐ所有は入力キューをつなぎ、応答しない拡張機能が UI の入力まで止める）。用意（`QueryContextMenu`）は 5 秒まで（呼び出し側のタイマーが止める旗を立て、表示の直前に見る。時間切れのメニューは出ない）、表示後とコマンドは無制限。コマンドが 1 秒以上戻らなければ先に `Invoked` を返す。コマンドの後も 2 秒以上と、そのスレッドのウィンドウが見えている間はメッセージを回し続ける。一度に 1 つ（2 つ目は `Unavailable`）。応答しない拡張機能はスレッド 1 本を捨てるだけで済むが、異常終了する拡張機能はプロセスごと落とす。アプリの項目（「Windows のメニュー」）はこのスレッドで実行せず、`AppCommand` で返して UI スレッドで実行する。「Windows のメニュー」では右クリックのたびにこのスレッドを作るので、時間切れになった項目は 5 分間ネイティブのメニューを使わない（`ContextMenuFactory.TimedOutRecently`。毎回 5 秒待たされ、応答しないスレッドが 1 本ずつ溜まるため）。
  - クリップボード：書き込み（`ShellClipboard.TryWrite`）は UI スレッドで、ほかのアプリが開いていれば 100 ms で諦める。読み取り（`ReadWithSequenceAsync`）は専用スレッドで 2 秒まで（エクスプローラーは読まれたときに一覧を作るので、応答しない持ち主に読み手が止められる）。待つのをやめた読み取りが終わるまで、次の読み取りは即 null。
  - 貼り付けの元と先の確認（`SourceProbe`）はワーカーでタイムアウト付き（先 2 秒、元 5 秒）。元の確認が間に合わなかった項目は「削除できない」扱いになり、切り取りでもコピーになる。
- 遅い・切断されたネットワークドライブ対策：`Existence` のタイムアウト（登録フォルダ 2 秒、履歴 1 秒）、`VolumeThrottle`、5 秒で「応答がありません」（`HoverTimings.Slow`、`TabViewModel.IsSlow`）。
- ドライブの確認（`DriveCatalog.Probe`）は中断できない呼び出しなので、`Task.Run` + `WaitAsync(2 秒)` で**待つのをやめるだけ**にする（スレッド プールのスレッドは、システムが諦めるまで数十秒つかまることがある）。積み上がらないよう、同じドライブの確認は前のものが戻るまで始めない（`DriveListViewModel.ProbeAsync`）。`DriveListViewModel` の状態（`_slots`、`Drives`）は UI スレッドだけが触る：`RefreshAsync` は UI スレッドから呼ぶ。
- キャンセル：タブは `TabViewModel._loadCts`、カードは `Realize` / `Unrealize` の CTS、プレビューは `Preview.Cts`。`OperationCanceledException` は握りつぶして終わる。
- ストアは内部でロックしてスレッドセーフ。`SaveFailed` はワーカースレッドで発火するので、UI へは `Dispatcher.BeginInvoke`。

## 5. 改修レシピ

| やりたいこと | 触る場所（順に） | 注意・テスト |
|---|---|---|
| 設定項目を追加 | `Core/Stores/SettingsStore.cs` の `AppSettings` にプロパティ（既定値）→ 範囲があれば `Normalize` → `App/ViewModels/SettingsViewModel.cs`（フィールド、コンストラクタ、`ApplyTo`、数値なら `Validate`）→ `Views/SettingsWindow.xaml` → 必要なら `MainViewModel.OpenSettings` でタブを `IsStale` に。選択肢の設定は `Core/Layout/` に enum と `ToSetting` / `FromSetting`（`AppTheme` / `ItemMenuStyle` が手本。保存は文字列で、`Normalize` で揃える）、画面は `Toggle.Choice` の `RadioButton` と `Is…` のプロパティ | `Clone` は `MemberwiseClone` なので**参照型を足したら `Clone` にコピーを追加**。テストは `StoreTests.cs` の `SettingsStoreTests`。README の設定表も更新。読む側は保存した `AppSettings` を毎回読む（`UsesWindowsItemMenu` のように）と、OK のあと再起動なしで効く |
| プレビュー対象の拡張子を追加 | `Core/Listing/PreviewKinds.cs` の拡張子集合 | `PreviewTests`。シェルのサムネイルで足りる形式なら追加不要（`ShellPreviewSupport` が自動判定） |
| 新しい種類のプレビュー | `PreviewKind` に追加 → 読み取りを `Core/Listing/` に → `PreviewTextCache.GetAsync` の分岐 → `CascadeController.ShowExcerpt` / `LoadPreviewAsync` → `PreviewViewModel` / `PreviewView` | 読み取りは共有モード・上限つき・キャンセル可能に。オンラインのみのファイルは開かない（`FileContent.IsCloudOnly`） |
| 右クリックメニューの項目 | `Views/ContextMenuFactory.cs` の `ItemEntries`（1 項目。`ItemRef.Context` / カードの種類で出し分け）/ `GroupEntries`（選択全部）に `MenuEntry` を足す → 処理は `MainViewModel` の public メソッドに | 1 つの並びが **2 通りに出る**：WPF のメニュー（`Render`。グリフあり）と、「Windows のメニュー」でのエクスプローラーのメニューの上（文字列だけ。グリフなし、`&` は `InsertAppEntries` が `&&` に直すのでニーモニックにならない、太字の既定は `IsDefault` の 1 つ）。`MenuItem` を直接作らず `MenuEntry` で足す（直接作ると片方にしか出ない）。項目の Action は WPF の Click の中と、`RunEntry`（ネイティブのメニューが閉じたあと、Click の外）の両方から呼ばれる：対象は組み立てたときの `item` / `group` を閉じ込めて使い、カスケードを離れる操作は `BeforeNavigate`（= `CloseAll`）を先に通す。「その他のメニュー（Windows）」（`AddNativeItem`）はアプリのメニューのときだけ、いつもいちばん下。エクスプローラーのメニューに出したくない verb は `ShellContextMenu.RemovedVerbs` に足す（外すだけ。消してよい verb を戻さない）。アプリの項目と重なるだけの verb は `RemovedWithAppEntries`。手動確認は両方の設定で（WPF の項目の並びと、ネイティブのメニューの上の並び・太字の既定・選んだあとの動作） |
| ファイルを変更する操作（削除など） | `Shell/RecycleBin.cs`（ごみ箱への移動だけ）→ `MainViewModel.CanDelete` / `DeleteAsync` → `ContextMenuFactory`（`ItemEntries` / `GroupEntries`）→ キー：`MainWindow.HandleSelectionKey`（Delete） | **完全削除の経路を作らない**（Shift + Delete も作らない）、必ず確認ダイアログを 1 回出す。複数は 1 つの `IFileOperation` のまま（項目ごとに呼び出しを分けない）。実行中の印 `_fileOpBusy` は貼り付けと共有。App 層なので自動テストなし → 手動確認：ローカルのパス（ごみ箱へ入る）、`\\wsl.localhost\…` などのパス（中止されて残る）、複数選択（確認 1 回・件数付きのメッセージ）、ローカルと WSL のパスを混ぜた選択（WSL の項目とその後ろは残る）、シェルのダイアログでのスキップ、エクスプローラーでの Ctrl + Z（まとめて 1 回で戻る）。終わったら `ReloadAfterFileChangeAsync` で 1 回読み直す。README の「削除（ごみ箱へ移動）」も更新 |
| コピー・移動（貼り付け）の規則・経路 | 規則：`Core/Transfer/TransferRules.cs`（`Decide`、`TransferOptions.Paste`）→ 元と先の確認：`Shell/SourceProbe.cs` → 実行：`Shell/FileTransfer.cs` → 流れ：`MainViewModel.PasteAsync`（先は `PasteFolderOf`、メニューは `ContextMenuFactory`、キーは `MainWindow.HandleSelectionKey`） | **コピー・移動は `FileTransfer` だけ**、確認を省くフラグを足さない、ドライブそのものを元にしない。切り取りの後片づけは `MovesDone == MovesRequested` のときだけ。`TransferRulesTests` / `FileClipboardTests`。App 層なので手動確認：同じフォルダへの貼り付け（「x - コピー」）、名前の衝突でスキップ（切り取りが残る）、エクスプローラーとの往復、長いコピー中にウィンドウを閉じる |
| 選択の規則・見た目 | 規則：`Core/Selection/SelectionModel.cs` → タブ：`TabViewModel.Selection` / `SelectableKeys` / `ApplyItemFlags` → 操作：`MainViewModel.ClickSelect` / `SelectionTargets`、`MainWindow.ItemClick` / `HandleSelectionKey` / `OnContentLeftDown` → 見た目：`CardView.xaml`（`SelectionFrame`）、`DetailsRowView.xaml`（`Root` のトリガー） | `SelectionModelTests`。色は `Brush.Accent` / `Brush.AccentSoft`（両テーマにある）。項目の祖先で左押下に反応しない（[§7](#7-守ること落とし穴)）。Ctrl / Shift のクリック・空白の押下は手動確認 |
| サイドバーの節を追加・変更 | `Core/Stores/SettingsStore.cs` の `SidebarSections`（ID を足す。`All` の順が保存の順）→ `MainViewModel` に `SidebarSectionViewModel` のプロパティ（`NewSection`）→ `Views/SidebarView.xaml`（見出しは `SectionHeader` のボタンに `DataContext` を渡すだけ。行の領域は `IsExpanded` に `Visibility` をバインド。末尾の空き行の手前に入れる）→ `SidebarView.ApplyRowHeights`（残りの高さを誰が取るか）と `Attach`（その節の `PropertyChanged` を購読） | 節の開閉・サイドバーの表示切り替えでは必ず `CloseCascade`（行が動く・消える）。並べ替えドラッグを受けるのは `RegisteredBody` だけにしておく。`SettingsStoreTests` |
| サイドバーの行（登録フォルダ・ドライブ）の見た目・操作 | 見た目：`Views/SidebarView.xaml`（行のテンプレート `SidebarRow`。登録フォルダと PC で共用）+ `SidebarItemViewModel` → 操作：`SidebarView.xaml.cs` がイベントで通知 → `MainWindow` の `OnSidebar…` → `MainViewModel`（`OpenRegisteredAsync` / `OpenDriveAsync` / `Unregister` / `MoveRegistered`） | 行の一覧を変えるときは `RefreshRegistered` を通す（`Registered` を `Clear` しない）。ホバーの一覧は `CascadeController.AnchorMouseEnter`（行は `ICascadeAnchor`）。テンプレートとイベントは 2 種類の行で共用なので、片方だけの動作は `SidebarItemViewModel.IsDrive` で分ける（`MainWindow.OnSidebarClicked` / `OnSidebarExplorer` / `OnSidebarDrag`、`SidebarView.TryStartDrag`）。App 層なので手動確認 |
| PC の節（出すドライブ・名前・確認） | 規則：`Core/Drives/DriveModel.cs` の `DriveRules`（`ShouldShow` / `DisplayName` / `Order`）→ 一覧と確認：`Shell/DriveCatalog.cs` → 行の更新：`ViewModels/DriveListViewModel.cs` → 行の見た目：`SidebarItemViewModel.ForDrive` / `Glyph` / `ToolTipText` → メニュー：`ContextMenuFactory`（`ItemContext.Drive`。見出しのメニューは `ShowPcSection`）→ 読み直しのきっかけ：`MainWindow.WatchWindowMessages` / `OnActivatedReadDrives`、`MainViewModel.Refresh` | `DriveRulesTests`。`Enumerate` に媒体へ触る呼び出し（`IsReady`、`VolumeLabel`、サイズ）を足さない。`Drives` を `Clear` して作り直さない。App 層は自動テストなし → USB の抜き差し、空のカード リーダー、切断されたネットワーク ドライブで手動確認（UI が止まらないこと） |
| カスケードの起点（アンカー）を増やす | 起点が表すフォルダに `Views/ICascadeAnchor.cs` を実装（タブの外の起点なら `OpensInNewTab` = true）→ 起点の要素の `MouseEnter` / `MouseMove` / `MouseLeave` から `CascadeController.AnchorMouseEnter` / `AnchorMouseMove` / `AnchorMouseLeave`（`MainWindow` が中継）→ 要素が操作なしで消えうるなら `AnchorRemoved` | 3 つとも同じ `hover` 要素を渡す。開閉の判断は足さない（`CascadeController` の中だけ）。起点が動く・消える操作（スクロール、節の開閉、一覧の差し替え）では `CloseAll`、タブの中身の都合なら `CloseCardCascade`。クリック・右クリック・ドラッグした起点を再武装しない仕組み（`SidebarView._noRearm` に当たるもの）は起点のビュー側で持つ。App 層なので手動確認（非アクティブなウィンドウの上でも開くこと、隣の起点を横切っても切り替わらないこと） |
| 項目のポインター操作（クリック・ドラッグ開始の判定） | `Views/ItemPointerTracker.cs`（判定）→ 通知先は `ICardHost`（`Views/CardView.xaml.cs` に定義、実装は `MainWindow`） | 処理済みにするイベントを増やさない（[§3.5](#35-開く移動する)）。項目を出すビューを足すときは、マウスのイベントを同じトラッカーへ渡し（`DetailsRowView` が手本）、`MainWindow` が使うメンバーは `IItemsView` に揃える（`RealizedItemAt` は `ItemsViewHitTest.Find` に自分のスクロール ビューアーと行の親を渡す。項目のビューの型を増やしたら `Find` の型の判定と `OnContentRightUp` の ViewModel の取り出しにも足す）。実際のマウス操作は手動確認（クリック、ダブルクリック、他アプリへのドラッグ、最上位の並べ替え、右ボタンを押したままの左クリック） |
| タブ（開き方・閉じ方・見た目） | `MainViewModel` の `OpenQuickTabAsync` / `OpenFolderTabAsync` / `ShowTabAsync` / `CloseTab` / `MoveTab` → 見た目と操作：`Views/FolderTabStrip.xaml(.cs)` → 保存：`SaveState` と `AppSettings.OpenTabs`（`OpenTabSetting`） | 種類の違いは `TabViewModel.IsFolderTab` / `IsPlaceholder` で分ける。`Kind` で分岐する処理や、表示中のタブを相手にするコマンドを足すときは、タブなし（`TabKind.None` / `HasTab`）の扱いを決める。保存する項目を足したら `AppSettings.Normalize` / `Clone` と `SettingsStoreTests` |
| 表示する項目（フィルター）を追加・変更 | `Core/Ordering/ItemFilter.cs`（enum、`Values`、`ToSetting` / `FromSetting`、`Matches`）→ `App/ViewModels/SortModeLabels.cs` の `FilterLabels` | メニューは `ItemFilters.Values` から自動で作られる。`ItemFilterTests`。フィルターを効かせる範囲は `TabViewModel.FilterApplies` / `ShowFilter` |
| ホバーの待ち時間・アニメーション | `Views/HoverTimings.cs` のみ | |
| 並び方を追加 | `Core/Ordering/CardSortMode.cs`（enum、`All`、`ToSetting` / `FromSetting`、`IsByModified`。詳細表示の列に対応するなら `SortColumn`・`ColumnOf`・`IsDescending`・`Next` も）→ `CardOrdering.ComparerFor`（キーが要るなら引数を足し、`TabViewModel.Ordered` から渡す）→ `App/ViewModels/SortModeLabels.cs` | メニューは `CardSortModes.All` から自動で作られる。あとから届く値（シェルの種類名など）をキーにしない。`OrderingTests`、`OrderStoreTests`（`SortModeSettingsTests`） |
| 詳細表示の列を追加・変更 | セルの文字列：`CardViewModel`（`ModifiedText` / `TypeText` / `SizeColumnText` に並べる。シェルに聞かない文字列は `Core/Text/DetailsText.cs`）→ 行：`Views/DetailsRowView.xaml`（列と `TextBlock`）→ 見出し：`Views/DetailsListView.xaml`（同じ並びの列、タイトルのボタン、幅を変えられる列ならつまみ）と `.xaml.cs`（`DetailsMetrics`、`UpdateColumns`、`UpdateSortMarks`、`OnHeaderClick`）→ 幅を保存する列なら `Core/Layout/DetailsColumns.cs` のキーと既定値 → 並べ替えられる列なら上の「並び方を追加」 | 見出しと行は同じ列の並び・同じ幅でなければならない（幅は `DetailsMetrics` の 1 か所から。行の左右の余白とスクロール バーの幅は見出しの `Margin` と `HorizontalChrome` に写してある）。値は一覧を作った時点で分かるものか、拡張子ごとにキャッシュできるものに限る（[§3.2](#32-カードの段階読み込み741)）。`DetailsColumnsTests`。見出しと行の揃いは、ライト・ダーク、スクロール バーの出る長い一覧、最上位の一覧（× の列がある）で目で確かめる |
| データファイルを追加 | `StoreBase` を継承（`Load` / `Serialize`、変更後に `RequestSave`）→ `DataStores` のコンストラクタ・プロパティ・`All` に追加 | `All` に入れると `LoadAll` / `FlushAll` / 保存失敗の通知 / 読み込み問題の報告に自動で乗る。README のデータ表も更新。`StoreTests` |
| タブの中身・件数・空のときの文言 | `TabViewModel` の `LoadRecentAsync` / `LoadFrequentAsync` / `LoadFolderAsync`、最上位の件数と空のときの文言は `UpdateTopSummary`（フィルターで変わるため 1 か所） | UI 文言は日本語でコードに直書き（リソース化していない） |
| 最近の履歴・よく使う項目の規則 | `Core/Ranking/`（`RecentMerger`、`FrequentRanker`） | `RankingTests` |
| フォルダ列挙の規則（並び・隠し・フィルタ） | `Core/Listing/FolderListing.cs`（`FolderLister`） | `ListingTests`（`FolderListerTests`） |
| カバー画像の選び方 | `Core/Listing/CoverFinder.cs` | `ListingTests`（`CoverFinderTests`） |
| 画像サムネイルの作り方・速さ | アプリがデコードする形式：`Shell/ImageDecoder.cs` の `Extensions` → 大きさの規則：`Core/Listing/ThumbSizing.cs` → ディスクキャッシュ：`Shell/ThumbDiskCache.cs`（規則は `Core/Infrastructure/DiskCacheRules.cs`）→ 振り分けとワーカー数：`ThumbnailService.GetAsync` / `DecodePicture` | `ThumbCacheTests`。保存する画像が変わる変更は `ThumbDiskCache.Version` を上げる。App 層は自動テストなし → 画像の多いフォルダで手動確認（向きつきの JPEG、透過 PNG、壊れた画像、シェルに回る HEIC など）。刈り込みが消すのは `DiskCacheRules.IsCacheFileName` に合うファイルだけ、という条件を外さない |
| カードの見た目・バッジ | `Views/CardView.xaml` + `CardViewModel`（`BadgeMode` など） | 色は `Themes/` のトークンを使う。バッジ行は左が 📌（ピン留め時だけ）、右がバッジ（`Viewbox` で縮む）。最小サイズ 120 で切れないか確認 |
| カードグリッドの列数・サイズ | `Views/CardGrid.xaml.cs`（`GridMetrics`、§6.1）、サイズの範囲は `AppSettings.CardSizeMin` / `Max` / `Step` | |
| 表示モード（カードの形・詳細表示）を追加・変更 | `Core/Layout/CardShape.cs`（enum、`ToSetting` / `FromSetting`、`IsCards`、`ThumbHeight`）→ `Views/MainWindow.xaml` のツールバーの `RadioButton`（`Checked="OnViewModeChecked"` も付ける）→ カードでないモードなら、項目のビュー（`IItemsView`）と `MainWindow.xaml` の表示・`Items` のトリガー、`MainWindow.ActiveList` | `CardShapeTests`。ツールバーの幅を増やしたら、`MainWindow.xaml` の右の列の `MinWidth`（いまは 724）とウィンドウの `MinWidth`（いまは 992）で、最近の履歴の最上位（ボタンがいちばん多い）でも更新ボタンまで収まるか確認。ビューが入れ替わる切り替えではスクロール位置を持ち越さない（`ResetScrollOffsets`） |
| 色・スタイル | `Themes/Light.xaml` と `Dark.xaml` の**両方**に同じキー、スタイルは `Styles.xaml` | |
| キー・マウス操作 | `MainWindow.OnPreviewKeyDown` / `OnPreviewMouseDown`、カスケード中は `CascadeController.HandleKey`、選択・クリップボードのキーは `HandleSelectionKey`。右ボタンを押したままの左クリックは `Core/Input/RockerGesture.cs`（規則）と `ItemPointerTracker`（カード）/ `EntryListView` / `SidebarView` の `_rocker`（[§3.5](#35-開く移動する)） | 右ボタンを離したときに実行する（押している間に別ウィンドウを前面に出さない）。マウスキャプチャで待たない。`RockerGestureTests`。実際のマウス操作は手動確認 |
| テーマ（配色）の選択肢・適用 | `Core/Layout/AppTheme.cs`（enum、`ToSetting` / `FromSetting`、`IsDark`）→ `Shell/ThemeManager.cs`（`Apply`）→ `SettingsViewModel`（`IsTheme…`）→ `Views/SettingsWindow.xaml`（`Toggle.Choice` の `RadioButton`） | `AppThemeTests`。色そのものは `Themes/Light.xaml` / `Dark.xaml` |
| 開く・エクスプローラー連携 | `Shell/ShellActions.cs`、`Shell/ExplorerTabs.cs`、`MainViewModel.OpenFileAsync` / `ShowInExplorerAsync` | `ExplorerTabs` は非公開コマンド依存。1 回のクリックでタブとウィンドウの両方を開かない |
| 他アプリへのドラッグの規則 | `Core/Transfer/FileDrag.cs`（規則）、`Shell/FileDragOut.cs`（実行） | Move を許可しない。`FileDragTests` |
| Win32 API を使う | 該当プロジェクトの `NativeMethods.txt` に名前を追加（CsWin32 が生成）→ App は `Shell/ShellInterop.cs` に集約（`DriveCatalog` / `MainWindowHook` など、あとから足した Shell のファイルは自分の Win32 呼び出しを自分の中に持つ） | 定数（`WM_…` など）も `NativeMethods.txt` に書けば生成される |
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
| `OrderingTests.cs` | `CardOrderingTests` `ManualOrderTests` `PathSetTests` `ExistenceProbeTests` | ピン固定の並べ替え、サイズ順（サイズなしは最後）・種類順（拡張子、大文字小文字を区別しない）と同順のときの名前順、並び方の設定値（9 つ）と見出しのクリックで次に行く並び方（`Next`）、ドラッグ順の適用、パス集合、存在確認と更新日時・ファイルのサイズ |
| `OrderStoreTests.cs` | `CardOrderStoreTests` `RegisteredFoldersReorderTests` `SortModeSettingsTests` | `card_order.json`、登録順の並べ替え、`sort_modes` |
| `StoreTests.cs` | `JsonFileTests` `RegisteredFoldersStoreTests` `HistoryStoreTests` `PinAndExcludedStoreTests` `SettingsStoreTests` | 原子的書き込み・退避、各ストア、設定の既定値と正規化（タブ列 `open_tabs` / `selected_tab`・サイドバーの幅と折りたたみ・詳細表示の列の幅 `details_columns`・右クリックのメニュー `item_menu`（既定 `app`、不明・なしは `app`、大文字小文字と空白の正規化、保存と `Clone`）・旧 `registered` キーの片づけを含む）、version 1 からの移行と、旧版が書き戻したファイルの再移行 |
| `ItemFilterTests.cs` | `ItemFilterTests` | フィルターの設定値の変換、絞り込み、絞り込み中のドラッグ順を全体へ戻す `MergeShownOrder`、`filter_modes` の正規化と保存 |
| `RankingTests.cs` | `RecentMergerTests` `FrequentRankerTests` `FormattingTests` | 最近の履歴の統合、スコア、表示用の書式 |
| `PathToolsTests.cs` | `PathToolsTests` | 入力の整形、正規化、相対パス、比較 |
| `PreviewTests.cs` | `PreviewTests` | 埋め込み縮小版（`OfficeThumbnail`）、オンラインのみの判定、`SessionMarker` |
| `TextPreviewTests.cs` | `TextPreviewTests` | 文字コード判定、行・バイト上限、バイナリ |
| `OfficeTextTests.cs` | `OfficeTextTests` | Word の段落・見出し・上限・壊れた / ロック中のファイル・キャンセル、Excel のセル・日付書式（1904 年基準を含む）・上限、オンラインのみは開かない |
| `FileDragTests.cs` | `FileDragTests` | ドラッグ可否、DropList、自アプリ判定、しきい値 |
| `ThumbCacheTests.cs` | `ThumbSizingTests` `DiskCacheRulesTests` | 画像をデコードする大きさ（短い辺・拡大しない・長い辺の上限）、ディスクキャッシュのファイル名、刈り込みで消すファイルの選択 |
| `CardShapeTests.cs` | `CardShapeTests` | `card_shape` の正規化（未設定・不正値は `portrait`。`details` は詳細表示）と保存キー、カードを出すモードか（`IsCards`）、サムネイルの高さ（3:4 / 1:1） |
| `AppThemeTests.cs` | `AppThemeTests` | テーマの設定値の変換（未設定・不正値は `system`）、`IsDark`、`theme` の正規化と保存 |
| `RockerGestureTests.cs` | `RockerGestureTests` | 右ボタンを押したままの左クリック：右ボタンを離したとき 1 回だけ対象を返す、左ボタンを離しても待ち状態が残る、普通のクリックと区別する、古い待ち状態を捨てる |
| `DriveRulesTests.cs` | `DriveRulesTests` | PC の節のドライブ：出すかどうか（リムーバブル・光学は確認でメディアがあるときだけ）、名前（ラベルなしの既定名、ネットワーク ドライブの `共有名 (\\サーバー) (Z:)`）、ドライブ文字順と重複の除去 |
| `DetailsColumnsTests.cs` | `DetailsColumnsTests` | 詳細表示の列の幅：`Normalize`（既定値・範囲・知らないキー）、`Fit`（名前の列に幅を残して縮める、最小幅より下げない）。セルの文字列：サイズの KB 表示（切り上げ・桁区切り）、種類の仮の名前 |
| `SelectionModelTests.cs` | `SelectionModelTests` | 選択：Ctrl の切り替えと起点、Shift の範囲（両方向・足す・起点なし）、表示から消えた項目を外す `Prune`、表示順 `InOrder`、操作の対象 `TargetsOf` |
| `TransferRulesTests.cs` | `TransferRulesTests` | コピー・移動の判断 `Decide`：同じフォルダ、自分の中（`E:\a` と `E:\ab` を区別）、ドライブそのもの、項目ごとの種別（読み取り専用・削除できない元はコピー）、一部だけ断る・全部断る（どれも貼り付けの規則 `TransferOptions.Paste`）。メニュー用の共通の親 `ShellMenuTargets.TryGroup` |
| `FileClipboardTests.cs` | `FileClipboardTests` | クリップボードの `CF_HDROP` の符号化と壊れたデータ、"Preferred DropEffect"（移動はコピーのビットがないときだけ） |
| `TestHelpers.cs` | `FakeClock` `FakeFileSystem` `TempDir` | テスト用の部品（新しいテストはまずこれを使う） |

## 7. 守ること・落とし穴

- **ファイルを完全に削除しない**（自分のデータファイルを除く）。消す経路は右クリックの「削除（ごみ箱へ移動）」（1 項目・選択全部）と Delete キー（選択だけ）だけで、どれも確認ダイアログのあと `RecycleBin` でごみ箱へ移す（ごみ箱に入らない項目があれば、その項目と残りは中止）。複数は 1 つの `IFileOperation` で扱い、項目ごとに別の呼び出しに分けない（シンクの拒否で残り全部が止まる、という不変条件が 1 回の `PerformOperations` に頼っているため）。Shift + Delete の完全削除は作らない。
- **エクスプローラーのメニューから delete / rename / cut / copy / paste を外す**（`ShellContextMenu.RemovedVerbs`）。シェルの削除はごみ箱に入らない場所では完全に削除する。外し損ねた項目（サブメニューの中、verb が後で読めた）も、選ばれたときに verb を見て実行しない（`Dismissed`）。親が 1 つの経路では verb が読めない項目は残る（外せない）ので、親が複数の経路で verb が 1 つも読めなければメニューを出さない（`Unavailable`）。ドライブの行には出さない（「フォーマット」が並ぶ）、`ShowAsync` もドライブそのもの（`IsTransferable` が false）を断る。
- **「Windows のメニュー」のアプリの項目の ID はシェルの範囲の外に置く**（`AppFirstId` 0xE000 〜 `AppIdLimit` 0xF000。シェルに渡すのは 1〜0x7FFF で、ハンドラーには少しはみ出すものがある。0xF000 以上は `SC_*` のシステム コマンド）。選ばれた ID は、シェルの範囲の確認と `GetCommandString` より**先に**アプリの範囲かを見る（ハンドラーが配っていないオフセットで verb を聞かない・`InvokeCommand` しない）。並びの位置がそのまま `AppIndex` なので、`ShowEntries` に渡した並びと実行する並びを別々に作らない。
- **シェルの open を外すのは、「Windows のメニュー」で既定の項目（1 項目の「開く」）があるときだけ**。copyaspath もアプリの項目を載せたときだけ外す。どちらも `RemovedWithAppEntries` / その場の追加で外すだけで、`RemovedVerbs` に入れない（選んだあとに断る対象になり、「その他のメニュー（Windows）」のシェルの「開く」まで動かなくなる）。選択全部のメニューには既定の項目を付けない（アプリは複数を開けないので、シェルの open を残す）。
- **WPF のメニューの経路を消さない**。「Windows のメニュー」でも、ドライブの行・PC の見出し・空白・使えない項目・時間切れから 5 分以内の項目は WPF のメニューで、ネイティブのメニューが出なかったとき（`Unavailable`、表示前の `Failed`、`TimedOut`）も `OpenFallback` で同じ並びを出す。`OpenFallback` は結果を受けたらその場で開く（間に await を入れると、右クリックから遅れて関係ない場所に出る・カスケードの状態が変わる）。`TimedOut` のメニューは後から出ない（`TrackPopupMenuEx` の前で段階を取り消す）ので、代わりを出しても二重にならない。表示後の `Failed` には代わりを出さない（利用者は一度メニューを見ている）。
- 項目の領域の空白の右クリック（`OnContentRightUp`）を項目のメニューに回すのは、`ItemsViewHitTest.Find` が**位置で**見つけた、表示領域の中の実体化済みの項目だけ。ヒットテストには頼れない（読み込み中は項目のビューがヒットテストを止めている）。読み込み中に出したメニューの項目は、読み直しで置き換わる前の `CardViewModel` のパスを対象にする（残っているリスク）。
- **コピー・移動は `FileTransfer` だけ**（貼り付け：`MainViewModel.PasteAsync`）。`IFileOperation` でシェルの衝突・確認のダイアログを必ず出し、`FOF_NOCONFIRMATION` などの確認を省くフラグを使わない。ドライブそのもの（`C:\`、`\\server\share`）は元にしない（`TransferRules`）。上書きするかはいつもシェルのダイアログで利用者が決める。他アプリへのドラッグは Copy | Link のみで、アプリの中でファイルを動かすドラッグはない。アプリが切り取った項目をエクスプローラーで貼り付けたときの移動はエクスプローラーが行う。自分のデータのうち画像サムネイルのキャッシュ（`thumbs\`）は上限を超えると古いものを消すが、消すのは自分が付けた名前（`DiskCacheRules.IsCacheFileName`）のファイルだけ。
- **UI スレッドで待たない**（[§4](#4-スレッドと非同期の規約)）。シェル呼び出しは必ず STA ワーカー経由でタイムアウトをつける。
- パスの比較は `PathTools.PathEquals` / `PathSet`（大文字小文字を区別せず正規化）。`==` で比べない。長いパスをシェルに渡すときは `PathTools.ForShell`。
- `SettingsStore.Current` を書き換えても保存されない（コピーのため）。`Update` を使う。
- 並び順は `_baseCards` を基準に `Ordered` で作る。`Cards` を直接並べ替えない。ピン・ドラッグ順の規則は Core の関数を使う。`Cards` はフィルターで項目が減っていることがあるので、位置（何番目か）を基準順と突き合わせる処理は `OrderedCards` を使う。
- タブは 1 つの種類に 1 つとは限らない（フォルダのタブは何個でも）。`TabKind` からタブを引かず、`TabViewModel` の参照（`SelectedTab`、`Tabs`）で扱う。全タブに効かせる処理は `MainViewModel.AllTabs`（固定タブ 2 つは閉じていても含む。`Tabs` は開いているタブだけ）。
- `SelectedTab` はタブなしの代役 `NoTab`（`TabKind.None`）のことがある。`Kind` の `switch` には `None` の腕を、表示中のタブを読み込む・動かす・設定を保存する処理には `HasTab` の確認を入れる。抜けると、空の `Root` のフォルダのタブができたり、代役のタブの並び方が `settings.json` に残ったりする。
- 一覧の行（`EntryListView` の `ListBox`）でマウスキャプチャを使って何かを待たない。`ListBox` は左ボタンを離すたびに自分のキャプチャを手放す（処理済みのイベントでも）。待ち状態は `RockerGesture` のようにフラグで持ち、取り消す条件を決める。
- サイドバーを隠すときは列の `MinWidth` を 0 にしてから幅を 0 にする（`MainWindow.ApplySidebarLayout`。`MinWidth` が残ると空の帯が残る）。隠している間は列の実幅（0）を `SidebarWidth` に書かない（`OnSplitterDragCompleted` のガード）。
- サイドバーのトグルはサイドバーの列の右にあり、押すと自分が横へ動く。ダブルクリックの 2 回目以降の押下が、その位置に来た節の見出しやタブに届かないよう、`MainWindow.OnPreviewMouseDown` が切り替え直後の `ClickCount > 1` の左押下を捨てる（`_sidebarJustToggled`。次の 1 回目の押下で解除）。トグルの位置を変えるとき・切り替えの経路を増やすときはこのガードを確かめる。
- サイドバーの行（`SidebarItemViewModel`）は登録フォルダとドライブの 2 種類ある。登録フォルダだけの処理（`FindRegistered`、`Unregister`、`MoveRegistered`、並べ替えドラッグ）にドライブの行を渡さない。行で分岐するときは `IsDrive`、`ItemRef` では `ItemContext.Drive` を見る。
- ドライブの一覧（`DriveCatalog.Enumerate`）で媒体に触らない。空のカード リーダー・眠っているディスク・届かないサーバーで一覧そのものが止まる。触るのは `Probe` だけで、呼ぶ側（`DriveListViewModel`）が 2 秒で待つのをやめる。
- ドライブの行は利用者の操作なしで消える。行の要素に何かを付けるとき（カスケードなど）は、`DriveListViewModel.RowRemoved` で片づける（消えた要素は `MouseLeave` を出さない。カスケードは `CascadeController.AnchorRemoved`）。
- カスケードを開く条件に `Window.IsActive` を戻さない（[§3.3](#33-カスケードcascadecontroller9)）。項目のビュー（カードグリッド・詳細表示）の都合で閉じるときは `CloseAll` ではなく `CloseCardCascade` を使う。
- 項目のビューは 2 つ（`CardGrid`、`DetailsListView`）あり、**隠れている側には `Items` を渡さない**（null）。両方に渡すと、見えていないカードが `Realize` してサムネイルを読み、隠れた一覧がコンテナーを抱え続ける。`MainWindow` からビューを触るときは `Grid` / `Details` を直接使わず `ActiveList` を通す（隠れた側のスクロール位置を読み書きしない）。スクロール位置はビューをまたいで使わない（`ResetScrollOffsets`）。隠れていた側は `Collapsed` のまま `Items` を受け取り（その時点の `ActualWidth` は 0）、前と同じ大きさで再表示されると `SizeChanged` は来ない。幅に依存する計算（`CardGrid.Rebuild` の列数、`DetailsListView.UpdateColumns`）は `IsVisibleChanged`（true）でもやり直す。`Details` はタブなし（`HasTab` が false）のときも隠す（列の見出しだけが残るため）。
- 詳細表示の行で `CardViewModel.Realize` を呼ばない（[§3.2](#32-カードの段階読み込み741)）。行のセルに足してよいのは、一覧を作った時点で分かる値と、拡張子ごとにキャッシュされる値だけ。
- 詳細表示の見出しと行の列は別々の `Grid` で、幅を `DetailsMetrics` で揃えているだけ。片方の列・余白だけを変えるとずれる（スクロール バーは常に表示して幅を見出し側で空けてある：`Auto` に戻すと、一覧が長くなった瞬間に列が動く）。
- カード・詳細表示の行の普通の左押下は処理済みにならない（`ItemPointerTracker`。クリックは離したときに決まる）。ウィンドウや項目の親で左押下に反応する処理を足すときは、`e.OriginalSource` から項目（`CardView` / `DetailsRowView`）の上の押下かどうかを確かめる。確かめずに押下の時点で何かを変えると、離したときのクリック・ドラッグの判定より先に効いてしまう（例：`MainWindow.OnContentLeftDown` の選択の解除。無条件に外すと、選択した項目を押してドラッグしようとした時点で選択が消える）。
- 選択・切り取りの印はカード（`CardViewModel`）に持たせず、タブの `Selection` / `CutPaths`（パス）から写す。カードは読み込み・並べ替えで作り直されるので、カードの `IsSelected` を直接書き換えても次の表示で消える。
- 貼り付け先に `FolderTargetPath` をそのまま使わない。読み込み中・失敗したフォルダへ貼り付けないよう、項目なしの貼り付けは `TransferTargetPath` を使う。`TransferTargetPath` に `IsStale` の条件を足さない：`RecordUse` / `TogglePin` は表示中の最近の履歴・よく使う項目のタブ（中のフォルダを表示中）にも `IsStale` を付けるので、貼り付けが F5 まで止まる。
- Ctrl + C / X / V と Delete はキーの繰り返し（`e.IsRepeat`）で動かさず（押しっぱなしの Delete で確認が続けて出ない）、テキスト ボックスの中では何もしない。Ctrl + C / X と Delete はポインターの下の項目を対象にしない（選択だけ）。
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

見出しを追加・改名・番号替えしたら、冒頭の[目次](#目次)（節番号・見出し・1 行説明）も同じコミットで直す。別の目録（`_topics.yml` や front matter）は作らない。文書が増えたら、[§0](#0-このドキュメントの位置づけ) の表に行を足す。
