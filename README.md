# FolderDeck

登録したフォルダをカードで並べ、マウスを乗せていくだけでサブフォルダの中身を何階層でもたどれる Windows ネイティブアプリです。表示されたファイルはクリックで開けます。
仕様は [dev/docs/SPEC_20261121_native_folder_app.md](dev/docs/SPEC_20261121_native_folder_app.md) にあります。

- タブ：「登録フォルダ」「最近の履歴」「よく使う項目」（どれも同じカード表示）
- カードにホバー → サムネイル部分に第1階層の一覧（オーバーレイ）→ フォルダ行に止まると横にフライアウト → さらに次の階層…
- フォルダはアプリ内で移動、ファイルは既定のアプリで開く。右クリックで「エクスプローラーで表示」「パスをコピー」「ピン留め」など

## 必要な環境

- Windows 10 22H2 / Windows 11（x64）
- 開発：.NET SDK 10（`dotnet --list-sdks` で 10.0.x が出ること）
- 配布物（single-file exe）を動かすだけなら .NET のインストールは不要です（self-contained）

## よく使う操作（BAT・ダブルクリック可）

リポジトリのフォルダにある BAT を、エクスプローラーからダブルクリックするか、ターミナルから実行します。

| BAT | 内容 |
|---|---|
| `build.bat [Debug\|Release]` | ソリューションをビルド（既定 Debug） |
| `test.bat [追加の引数]` | Core のテスト（例：`test.bat --filter Ranking`） |
| `run.bat [アプリへの引数]` | Debug ビルドして起動（例：`run.bat --data-dir E:\tmp\data`） |
| `publish.bat` | 配布用の single-file exe を作り、`dist\FolderDeck.exe` にコピー |

- ダブルクリックしたときは、最後に「Enter キーで閉じます」で止まるので結果を読めます（`run.bat` が成功したときはアプリが出るので止まりません）。ターミナルから実行したときは止まりません。
- 終了コードを返すので、ほかのスクリプトからも呼べます（成功 0、失敗 1、引数の誤り 2）。
- これから上書きするフォルダ（`bin\Debug`、`bin\Release`、`publish`、`dist`）から FolderDeck が起動していると exe がロックされてビルドできないため、「閉じて続けますか？ [Y/n]」と聞いてから普通に閉じます（閉じるときに状態は保存されます）。ほかの場所から起動している FolderDeck には触れません。
  - 入力できない実行（ほかのツールから呼んだときなど）では、閉じずに中止します。確認なしで閉じてよいときは、環境変数 `FOLDERDECK_AUTOCLOSE=1` を設定してください。
- FolderDeck は1つしか起動できません。ほかの場所（`dist\` など）の FolderDeck が起動していると、`run.bat` は新しいビルドを起動できない（既存のウィンドウが前に出るだけで `--data-dir` も効かない）ため、ビルドせずにその旨を表示して終了します。そちらを閉じてから実行してください。
- `test.bat` などへの追加の引数は、`-tl:off` や `-p:Name=Value` のような形もそのまま dotnet に渡ります。
- 中身は `tools\dev.ps1` にまとめてあります（BAT はそれを呼ぶだけ）。BAT を英数字だけにしてあるのは、文字コード（コードページ 932 / 65001）が違う環境でも文字化けせずに動かすためです。`tools\dev.ps1` を編集したら UTF-8（BOM 付き）で保存してください。

## ビルド・テスト・起動（コマンドで直接）

リポジトリのフォルダ（このファイルがある場所）で実行します。

```powershell
dotnet build FolderDeck.sln -c Debug
```

```powershell
dotnet test tests/FolderDeck.Core.Tests -c Debug
```

```powershell
dotnet run --project src/FolderDeck.App
```

### 配布用の exe を作る

```powershell
dotnet publish src/FolderDeck.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

出力は `src\FolderDeck.App\bin\Release\net10.0-windows\win-x64\publish\FolderDeck.exe` の **1ファイルだけ**です（約 150 MB）。`publish.bat` を使うと、同じものが `dist\FolderDeck.exe` にもコピーされます。
ReadyToRun（事前コンパイル）を有効にしてあり、起動を速くしています。

> 新しくビルドした exe を初めて起動するときは、ウイルス対策ソフトのスキャンで 30 秒ほどかかることがあります（この環境で計測）。2回目からは約 1.4 秒で最初の画面が出ます。

## データの置き場所

`%LOCALAPPDATA%\FolderDeck\`

| ファイル | 内容 |
|---|---|
| `registered_folders.json` | 登録フォルダ（imgtool と同じ形式） |
| `history.json` | 利用記録（最近の履歴・よく使う項目の元データ） |
| `pins.json` | ピン留め |
| `excluded.json` | 最近の履歴から外した項目 |
| `settings.json` | 設定、ウィンドウ位置、各タブの現在地、カードサイズ |
| `logs\app.log` | ログ（5 MB で `app.log.1` に切り替え） |

- 書き込みは一時ファイル経由で置き換えます。壊れた JSON は `<名前>.broken-<UNIX秒>.json` に退避し、空のデータで続けます。
- このアプリはファイルの削除・移動・上書きを一切しません。

### 開発用オプション

```powershell
dotnet run --project src/FolderDeck.App -- --data-dir E:\tmp\folderdeck-data
```

`--data-dir <フォルダ>` でデータの置き場所を差し替えられます（本番のデータを汚さずに試すため）。

## 検証用フォルダ

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-fixture.ps1 -OutputDir E:\tmp\fixture
```

8階層の入れ子、10,000 ファイル、5,000 フォルダ、260 文字を超えるパス、隠し・システム属性、循環ジャンクション、画像あり・なし、`test.bat`、アクセス拒否のフォルダなどを作ります（§19.3）。
出力先がすでにあると何もせずに止まります。アクセス拒否のフォルダを消す前に、スクリプトが表示する `icacls ... /remove:d ...` を実行してください。

## 操作

| 操作 | 動作 |
|---|---|
| カードをクリック | フォルダなら中へ移動、ファイルなら開く |
| カードにホバー（0.3 秒） | 子項目の一覧（オーバーレイ） |
| フォルダ行に止まる（0.15 秒） | 横に次の階層（フライアウト）。何階層でも続く |
| ↑ / Backspace | 1つ上へ |
| マウスの戻る・進む / Alt + ← → | 移動の履歴 |
| F5 | 更新（キャッシュも読み直す） |
| Ctrl + ホイール | カードの大きさ（120〜400） |
| カスケード表示中の ↑ ↓ → ← Enter Esc | 行の選択、子を開く、閉じる、開く、いちばん深い階層を閉じる |
| エクスプローラーからフォルダをドロップ | 登録（登録フォルダの最上位で） |

実行形式（`.exe .bat .ps1` など）を開く前には確認が出ます（設定で OFF にできます）。

## 構成

```
src/FolderDeck.Core/   UI に依存しないロジック（パス、列挙、キャッシュ、カバー探索、JSON 保存、順位付け）
src/FolderDeck.App/    WPF（View / ViewModel、カスケード制御、シェル連携、テーマ）
tests/FolderDeck.Core.Tests/  xUnit（Core のみ）
tools/dev.ps1                 build / test / run / publish の中身（各 BAT から呼ばれる）
tools/make-fixture.ps1        検証用フォルダの作成
build.bat test.bat run.bat publish.bat   よく使う操作（ダブルクリック可）
dist/                         publish.bat の出力（git には含めない）
```

ホバーの待ち時間（300 / 150 / 300 / 300 ms）は `src/FolderDeck.App/Views/HoverTimings.cs` にまとめてあります。

## 実装メモ（仕様からの判断・差分）

- **カードグリッド**：`VirtualizingWrapPanel`（MIT、net10.0-windows 対応を確認済み）は使わず、カードを「列数ぶんの行」にまとめて `VirtualizingStackPanel` で仮想化しています。§6.1 の列数と幅の式をそのまま再現するためです。
- **フライアウトの位置**：WPF の `Popup` を使い、位置は行が載っているモニターの作業領域と拡大率から物理ピクセルで計算して、ウィンドウを直接その位置へ動かしています（拡大率の違うモニターでもずれないようにするため。§9.5 の `CustomPopupPlacementCallback` の代わり）。
- **フォルダのサムネイル**：シェルのフォルダサムネイルの一部は「非乗算済みアルファ」の画像として返るため、色とアルファから判定して正しく透過表示しています。四隅が不透明な（四角い背景つきの）画像はアイコン表示に切り替えます。
- **メッセージ行**：壊れたデータファイルを退避したときの通知用に「警告」（エラーと同じく次の操作まで残る）を追加しました。
- **長いパス**：シェル API で開けないときは 8.3 形式の短いパスでも試します。
- **キーボード（§9.9）**：カスケードが開いているあいだの ↑ ↓ → ← Enter Esc に対応しています。カードにフォーカスを置いてキーボードだけでオーバーレイを開く機能（任意）は未対応です。
- **未対応（P2・任意）**：登録フォルダの並べ替え、フォルダへのショートカットの展開、安全三角形、「プログラムから開く」ダイアログ、Recent フォルダの監視。
