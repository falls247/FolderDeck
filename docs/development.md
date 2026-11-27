# 開発ガイド（コマンドとエラー対応）

FolderDeck をビルド・テスト・起動し、配布用の exe を作るときのコマンドと、困ったときの対処をまとめます。利用者から見える挙動・設定・データファイルは [詳細仕様](specification.md)、コードの地図は [コードベース概要](../dev/docs/codebase_overview.md)、アプリの紹介は [README](../README.md) にあります。

## 目次

| 節 | 内容 |
|---|---|
| [1. 必要なもの](#1-必要なもの) | .NET SDK 10、配布物を動かす PC に必要なもの |
| [2. よく使う操作（BAT・ダブルクリック可）](#2-よく使う操作batダブルクリック可) | BAT の一覧、終了コード、起動中の FolderDeck の扱い、`tools\dev.ps1` |
| [3. ビルド・テスト・起動（コマンドで直接）](#3-ビルドテスト起動コマンドで直接) | `dotnet build` / `test`（`--filter`）/ `run`、`--data-dir` |
| [3.1 開発用オプション（データの置き場所を差し替える）](#31-開発用オプションデータの置き場所を差し替える) | `--data-dir` で本番のデータを汚さずに試す |
| [4. 配布用の exe を作る](#4-配布用の-exe-を作る) | `dotnet publish` の 3 種類、ReadyToRun、サイズ・起動時間・メモリ |
| [5. 検証用フォルダ](#5-検証用フォルダ) | `tools\make-fixture.ps1` |
| [6. 構成](#6-構成) | リポジトリのフォルダの配置 |
| [7. エラー対応](#7-エラー対応) | 症状ごとの原因と対処 |
| [7.1 「閉じて続けますか？」と聞かれる・exe がロックされてビルドできない](#71-閉じて続けますかと聞かれるexe-がロックされてビルドできない) | 起動中の FolderDeck が exe をロックしている |
| [7.2 run.bat が新しいビルドを起動せずに終わる](#72-runbat-が新しいビルドを起動せずに終わる) | ほかの場所の FolderDeck が起動している |
| [7.3 framework 版の exe が起動直後に落ちる（0xc0000602）](#73-framework-版の-exe-が起動直後に落ちる0xc0000602) | ReadyToRun の中間出力の取り違え |
| [7.4 framework 版の exe が起動せず、ダウンロードを促す画面が出る](#74-framework-版の-exe-が起動せずダウンロードを促す画面が出る) | .NET 10 デスクトップ ランタイムがない |
| [7.5 .NET を同梱しないはずの exe が約 150 MB になる](#75-net-を同梱しないはずの-exe-が約-150-mb-になる) | `--self-contained false` を使った |
| [7.6 新しい exe の初回起動に 30〜40 秒かかる](#76-新しい-exe-の初回起動に-3040-秒かかる) | ウイルス対策ソフトのスキャン |
| [7.7 BAT・スクリプトの出力が文字化けする](#77-batスクリプトの出力が文字化けする) | 文字コード |
| [7.8 データファイルが .broken-….json になった](#78-データファイルが-broken-json-になった) | 壊れた JSON の退避 |
| [7.9 エクスプローラーの新しいタブで開かなくなった](#79-エクスプローラーの新しいタブで開かなくなった) | 公開されていないコマンドが Windows Update で変わった |
| [7.10 検証用フォルダを消せない](#710-検証用フォルダを消せない) | アクセス拒否のフォルダ |
| [7.11 FolderDeck が勝手に終了した](#711-folderdeck-が勝手に終了した) | `tools\analyze-log.ps1` と終わり方の表示 |
| [7.12 FolderDeck が応答しなくなる（ファイルの多いフォルダ）](#712-folderdeck-が応答しなくなるファイルの多いフォルダ) | UI の応答なしの記録、メモリ ダンプの取り方、IME と UI オートメーション |
| [7.13 メモリを多く使う（タブをたくさん開いたとき）](#713-メモリを多く使うタブをたくさん開いたとき) | `app.log` の `memory:` の行の読み方 |
| [7.14 エクスプローラーのメニューが時間切れになる](#714-エクスプローラーのメニューが時間切れになる) | `Shell menu timed out in …` の段階の読み方、拡張機能の切り分け（ShellExView） |
| [7.15 画面構成の変化のあと、前面に出ない](#715-画面構成の変化のあと前面に出ない) | `app.log` の `display:` の行の読み方、再現のしかた（Win + P、解像度・拡大率、HDMI の抜き差し） |

## 1. 必要なもの

- Windows 10 22H2 / Windows 11（x64）
- .NET SDK 10（`dotnet --list-sdks` で 10.0.x が出ること）
- 配布物（`publish.bat` で作る single-file exe）を動かすだけなら .NET のインストールは不要です（self-contained）
  - `publish.bat framework` で作る小さい exe だけは、使う PC に .NET 10 デスクトップ ランタイム（x64）が必要です

## 2. よく使う操作（BAT・ダブルクリック可）

リポジトリのフォルダにある BAT を、エクスプローラーからダブルクリックするか、ターミナルから実行します（PowerShell では `.\run.bat` のように `.\` を付けます）。

| BAT | 内容 |
|---|---|
| `build.bat [Debug\|Release]` | ソリューションをビルド（既定 Debug） |
| `test.bat [追加の引数]` | Core のテスト（例：`test.bat --filter Ranking`） |
| `run.bat [アプリへの引数]` | Debug ビルドして起動（例：`run.bat --data-dir E:\tmp\data`） |
| `build-test-run.bat [アプリへの引数]` | ソリューションを Debug ビルド → Core のテスト → 起動 を続けて実行。失敗した段階で止まる（引数は `run.bat` と同じ。例：`build-test-run.bat --data-dir E:\tmp\data`） |
| `publish.bat` | 配布用の single-file exe（.NET 同梱・圧縮、約 65 MB）を作り、`dist\FolderDeck.exe` にコピー |
| `publish.bat uncompressed` | .NET 同梱・圧縮なしの single-file exe（約 145 MB）を作り、`dist\uncompressed\FolderDeck.exe` にコピー。起動直後のメモリが圧縮版より少ない |
| `publish.bat framework` | .NET を同梱しない single-file exe（約 3.5 MB）を作り、`dist\framework-dependent\FolderDeck.exe` にコピー。使う PC に .NET 10 デスクトップ ランタイム（x64）が必要（入っていないと起動時にダウンロードを促す画面が出て起動しない） |
| `publish-all.bat` | 上の 3 種類（圧縮・圧縮なし・framework）を続けて作る（`publish.bat all` と同じ）。失敗したところで止まり、残りは作らない。1 分ほどかかる |

- ダブルクリックしたときは、最後に「Enter キーで閉じます」で止まるので結果を読めます（`run.bat` / `build-test-run.bat` が成功したときはアプリが出るので止まりません）。ターミナルから実行したときは止まりません。
- 終了コードを返すので、ほかのスクリプトからも呼べます（成功 0、失敗 1、引数の誤り 2）。
- これから上書きするフォルダ（`bin\Debug`、`bin\Release`、`publish`、コピー先の `dist`・`dist\uncompressed`・`dist\framework-dependent`）から FolderDeck が起動していると exe がロックされてビルドできないため、「閉じて続けますか？ [Y/n]」と聞いてから普通に閉じます（閉じるときに状態は保存されます）。ほかの場所から起動している FolderDeck には触れません。
  - 入力できない実行（ほかのツールから呼んだときなど）では、閉じずに中止します。確認なしで閉じてよいときは、環境変数 `FOLDERDECK_AUTOCLOSE=1` を設定してください（[7.1](#71-閉じて続けますかと聞かれるexe-がロックされてビルドできない)）。
- FolderDeck は 1 つしか起動できません。ほかの場所（`dist\` など）の FolderDeck が起動していると、`run.bat` / `build-test-run.bat` は新しいビルドを起動できない（既存のウィンドウが前に出るだけで `--data-dir` も効かない）ため、ビルドせずにその旨を表示して終了します。そちらを閉じてから実行してください（[7.2](#72-runbat-が新しいビルドを起動せずに終わる)）。
- `test.bat` などへの追加の引数は、`-tl:off` や `-p:Name=Value` のような形もそのまま dotnet に渡ります。
- `build-test-run.bat` は、ビルド（Debug・ソリューション全体）→ テスト（ビルド済みのものを `--no-build` で実行）→ 起動（`run.bat` と同じ）を一度に行います。途中で失敗するとそこで止まり、「テストで失敗しました。起動はしません。」のように、どの段階で失敗したかを表示します。引数はすべてアプリに渡されます（テストを絞り込みたいときは `test.bat --filter ...` を使ってください）。`tools\dev.ps1` では `all`（別名 `build-test-run`）というタスクです。
- 中身は `tools\dev.ps1` にまとめてあります（BAT はそれを呼ぶだけ）。BAT を英数字だけにしてあるのは、文字コード（コードページ 932 / 65001）が違う環境でも文字化けせずに動かすためです。`tools\dev.ps1` を編集したら UTF-8（BOM 付き）で保存してください（[7.7](#77-batスクリプトの出力が文字化けする)）。

## 3. ビルド・テスト・起動（コマンドで直接）

リポジトリのフォルダ（`FolderDeck.sln` がある場所）で実行します。

```powershell
dotnet build FolderDeck.sln -c Debug
```

```powershell
dotnet test tests/FolderDeck.Core.Tests -c Debug
```

関連するテストだけを流すときは `--filter` にテストクラス名の一部を渡します（演算子なしの `--filter` は `FullyQualifiedName~` と同じ。テストファイルと対象の対応表は [コードベース概要 §6](../dev/docs/codebase_overview.md#6-テストビルド)）。

```powershell
dotnet test tests/FolderDeck.Core.Tests -c Debug --filter SettingsStoreTests
```

```powershell
dotnet run --project src/FolderDeck.App
```

### 3.1 開発用オプション（データの置き場所を差し替える）

```powershell
dotnet run --project src/FolderDeck.App -- --data-dir E:\tmp\folderdeck-data
```

`--data-dir <フォルダ>` でデータの置き場所を差し替えられます（本番のデータを汚さずに試すため）。`run.bat --data-dir E:\tmp\folderdeck-data` でも同じです。自動テストは Core だけなので、App 層（画面・シェル連携）の変更はこの方法で手で確かめます。

## 4. 配布用の exe を作る

```powershell
dotnet publish src/FolderDeck.App -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true
```

出力は `src\FolderDeck.App\bin\Release\net10.0-windows\win-x64\publish\FolderDeck.exe` の **1 ファイルだけ**です（約 65 MB）。`publish.bat` を使うと、同じものが `dist\FolderDeck.exe` にもコピーされます。
ReadyToRun（事前コンパイル）を有効にしてあり、起動を速くしています。.NET を同梱する（self-contained）ときは exe の中身を圧縮しています（`EnableCompressionInSingleFile`）。圧縮しない exe（約 145 MB）は `-p:EnableCompressionInSingleFile=false` を足すと作れます（`publish.bat uncompressed` と同じ）。

.NET を同梱しない小さい exe（約 3.5 MB）は `-p:SelfContained=false` で作れます（`publish.bat framework` と同じ）。使う PC に .NET 10 デスクトップ ランタイム（x64）が必要です。

- `--self-contained false` ではなく `-p:SelfContained=false` を使ってください。この環境（.NET SDK 10）では `--self-contained false` を付けても .NET が同梱され、約 150 MB になりました（[7.5](#75-net-を同梱しないはずの-exe-が約-150-mb-になる)）。
- self-contained と framework-dependent を続けて作っても大丈夫です。ReadyToRun の作り方が 2 つで違う（self-contained は .NET ごと 1 つにまとめる composite）ため、切り替えたときは事前コンパイルをやり直します（ログに `ReadyToRun: mode changed ... recompiling.` と出ます。self-contained へ戻すときは 45 秒ほど）。以前は切り替えても作り直さず、`publish.bat` の直後の `publish.bat framework` が起動直後に必ず落ちる exe を作っていました（[7.3](#73-framework-版の-exe-が起動直後に落ちる0xc0000602)）。
- WPF はトリミング（`PublishTrimmed`、使わないコードを削る機能）に対応していないため使っていません。

> 新しくビルドした exe を初めて起動するときは、ウイルス対策ソフトのスキャンで 30〜40 秒ほどかかることがあります（この環境で計測）。2 回目からは約 1.4 秒（`publish.bat framework` の exe は約 0.9 秒）で最初の画面が出ます。
> 圧縮した exe は展開した中身をメモリに置くため、起動直後のメモリ（ワーキングセット）が約 200 MB と、圧縮しない場合・`publish.bat framework` の exe（約 130 MB）より約 70 MB 多くなります。

## 5. 検証用フォルダ

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\make-fixture.ps1 -OutputDir E:\tmp\fixture
```

8 階層の入れ子、10,000 ファイル、5,000 フォルダ、260 文字を超えるパス、隠し・システム属性、循環ジャンクション、画像あり・なし、`test.bat`、アクセス拒否のフォルダなどを作ります。
出力先がすでにあると何もせずに止まります。アクセス拒否のフォルダを消す前に、スクリプトが表示する `icacls ... /remove:d ...` を実行してください（[7.10](#710-検証用フォルダを消せない)）。

## 6. 構成

```
src/FolderDeck.Core/   UI に依存しないロジック（パス、列挙、キャッシュ、カバー探索、JSON 保存、順位付け、並べ替え、プレビュー用の読み取り、ドラッグで渡す・落とすときの判定、範囲選択の幾何）
src/FolderDeck.App/    WPF（View / ViewModel、カスケード制御、シェル連携、テーマ）
tests/FolderDeck.Core.Tests/  xUnit（Core のみ）
tools/dev.ps1                 build / test / run / all / publish の中身（各 BAT から呼ばれる）
tools/make-fixture.ps1        検証用フォルダの作成
tools/analyze-log.ps1         ログを起動ごとに分けて終わり方を表示
build.bat test.bat run.bat build-test-run.bat publish.bat publish-all.bat   よく使う操作（ダブルクリック可）
docs/                         利用者・開発者向けの資料（本書と詳細仕様）
dev/docs/codebase_overview.md コードの地図（改修の入口）
dist/                         publish.bat の出力（git には含めない）
```

ファイルごとの責務は [コードベース概要 §2](../dev/docs/codebase_overview.md#2-ファイルマップ) にあります。

## 7. エラー対応

症状ごとに、原因と対処をまとめます。実行時の記録は `%LOCALAPPDATA%\FolderDeck\logs\app.log`（`--data-dir` で起動したときはその下の `logs\app.log`）にあります。

### 7.1 「閉じて続けますか？」と聞かれる・exe がロックされてビルドできない

- **原因**：これから上書きするフォルダ（`bin\Debug`、`bin\Release`、`publish`、`dist`・`dist\uncompressed`・`dist\framework-dependent`）から FolderDeck が起動していて、exe がロックされている。
- **対処**：BAT（`tools\dev.ps1`）が「閉じて続けますか？ [Y/n]」と聞くので、Y で普通に閉じてから続けます（状態は保存されます）。
  - ほかのツールから呼んだときなど入力できない実行では、閉じずに中止し「確認できないため中止しました。…」と表示します。FolderDeck を閉じてから実行し直すか、確認なしで閉じてよいなら環境変数 `FOLDERDECK_AUTOCLOSE=1` を設定します。

  ```powershell
  $env:FOLDERDECK_AUTOCLOSE = '1'; .\build.bat
  ```

  - `dotnet build` を直接実行したときは確認がないので、先に FolderDeck を閉じてください。

### 7.2 run.bat が新しいビルドを起動せずに終わる

- **原因**：FolderDeck は 1 つしか起動できない。ほかの場所（`dist\` など）の FolderDeck が起動していると、新しいビルドを起動しても既存のウィンドウが前に出るだけで、`--data-dir` も効かない。そのため `run.bat` / `build-test-run.bat` はビルドせずにその旨を表示して終了する。
- **対処**：そちらの FolderDeck を閉じてから実行します。

### 7.3 framework 版の exe が起動直後に落ちる（0xc0000602）

- **症状**：`publish.bat framework` の exe が起動直後に落ちる。`app.log` には何も出ず、イベント ログ（Application）に coreclr.dll の 0xc0000602 が残る。
- **原因**：以前の版の不具合。self-contained（composite）と framework-dependent は ReadyToRun の中間出力を共有していて、`publish.bat` の直後の `publish.bat framework` が作り直さずに composite の部品を同梱していた。今は作り方が切り替わったときに事前コンパイルをやり直します（ビルドのログに `ReadyToRun: mode changed ... recompiling.` と出ます）。
- **対処**：今のリポジトリで作り直します。.NET SDK を上げたあとなどにまた起きたときは、2 回目の publish のログに `ReadyToRun: mode changed` が出ているかを確かめます（出ていなければ、作り直しの仕組みが SDK の変更で効かなくなっています。[コードベース概要 §7](../dev/docs/codebase_overview.md#7-守ること落とし穴) の ReadyToRun の項）。環境変数 `DOTNET_ReadyToRun=0` を設定して起動すると落ちないなら、ReadyToRun が原因です。

### 7.4 framework 版の exe が起動せず、ダウンロードを促す画面が出る

- **原因**：`publish.bat framework` の exe は .NET を同梱していない。使う PC に .NET 10 デスクトップ ランタイム（x64）がない。
- **対処**：.NET 10 デスクトップ ランタイム（x64）を入れるか、.NET を同梱した `publish.bat`（または `publish.bat uncompressed`）の exe を使います。

### 7.5 .NET を同梱しないはずの exe が約 150 MB になる

- **原因**：`dotnet publish` に `--self-contained false` を付けた。この環境（.NET SDK 10）では、これを付けても .NET が同梱された。
- **対処**：`-p:SelfContained=false` を使います（`publish.bat framework` と同じ。[4. 配布用の exe を作る](#4-配布用の-exe-を作る)）。

### 7.6 新しい exe の初回起動に 30〜40 秒かかる

- **原因**：新しくビルドした exe を初めて起動するときの、ウイルス対策ソフトのスキャン（この環境で計測）。
- **対処**：不要です。2 回目からは約 1.4 秒（`publish.bat framework` の exe は約 0.9 秒）で最初の画面が出ます。

### 7.7 BAT・スクリプトの出力が文字化けする

- **原因**：BAT に英数字以外の文字を入れた、または `tools\dev.ps1`・`tools\analyze-log.ps1` を BOM なしで保存した。BAT は文字コード（コードページ 932 / 65001）が違う環境でも動くよう英数字だけにしてあり、日本語の表示は `tools\dev.ps1` が行う。Windows PowerShell 5.1 は BOM のない UTF-8 の日本語を正しく読まない。
- **対処**：BAT は英数字だけに戻し、`tools\*.ps1` は UTF-8（BOM 付き）で保存し直します。

### 7.8 データファイルが .broken-….json になった

- **症状**：データフォルダ（`%LOCALAPPDATA%\FolderDeck\` か `--data-dir` のフォルダ）に `<名前>.broken-<UNIX秒>.json` ができ、そのデータ（登録フォルダ、ピン留めなど）が空になっている。メッセージ行に警告が出る。
- **原因**：起動時に読んだ JSON が壊れていた（手で編集して書き間違えた、など）。壊れたファイルはその名前に退避し、空のデータで続けます。退避もできなかったときは、そのファイルにはその後書き込みません。
- **対処**：FolderDeck を閉じてから、退避したファイルを直して元の名前に戻します。JSON の形は [詳細仕様の「settings.json」「ほかのデータファイル」](specification.md#2-settingsjson) にあります。

### 7.9 エクスプローラーの新しいタブで開かなくなった

- **症状**：設定の「エクスプローラーで表示するとき、開いているエクスプローラーに新しいタブで開く」をオンにしているのに、新しいウィンドウで開く（または「エクスプローラーの新しいタブでフォルダを開けませんでした。」と出る）。
- **原因**：新しいタブを開くには、エクスプローラーの公開されていないコマンド（`WM_COMMAND 0xA21B`）を使っている。Windows Update で動かなくなった場合は、約 2 秒待ってから新しいウィンドウで開く動作に戻る。エクスプローラーが開いていない、別の仮想デスクトップにしかない、Windows 11 22H2（ビルド 22621）より前のときも新しいウィンドウで開く。
- **対処**：どちらで開いたかとその理由は `logs\app.log` に記録されるので、そこで確かめます。挙動の詳細は [詳細仕様の「設定」](specification.md#15-設定)。

### 7.10 検証用フォルダを消せない

- **原因**：`tools\make-fixture.ps1` はアクセス拒否のフォルダを作る。
- **対処**：消す前に、スクリプトが表示した `icacls ... /remove:d ...` を実行します。

### 7.11 FolderDeck が勝手に終了した

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\analyze-log.ps1 -Events
```

ログを起動ごとに分けて、いつ起動してどう終わったかを表示します（`-Last 30` で件数（既定 10）、`-LogDir` で `--data-dir` のログも見られます。`-Events` は Windows のイベント ログからクラッシュの記録も探します）。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\analyze-log.ps1 -LogDir E:\tmp\folderdeck-data\logs -Last 30 -Events
```

アプリは開始・終了と閉じた理由を `lifecycle:` で始まる行に書きます。

| 終わり方の表示 | 意味 |
|---|---|
| ユーザーが閉じた | × ボタン、Alt+F4、タスクバーから閉じた |
| 別のプログラムが閉じた（WM_CLOSE） | ほかのプログラムがウィンドウを閉じさせた（ビルドスクリプトの `CloseMainWindow`、`/F` なしの `taskkill`、インストーラーなど） |
| Windows のセッション終了で閉じた | サインアウト・シャットダウン・更新（Restart Manager） |
| クラッシュ（未処理の例外） | 例外の内容がログに残っています |
| UI が応答しないまま終わった | 「UI の応答なし」の記録が戻らないまま終了処理が走っていない（固まったので強制終了されたと推定。[7.12](#712-folderdeck-が応答しなくなるファイルの多いフォルダ)） |
| 異常終了 | 終了処理が走っていない。次の起動時に、イベントログ（Application）の FolderDeck.exe のクラッシュ記録も調べて書き出します。記録がなければ強制終了（`taskkill /F`、タスクマネージャー）か電源断 |

- UI スレッドが 5 秒以上応答しなかったときは `lifecycle: the UI thread has not answered for <秒> s (since <固まり始めた時刻>)` が、30 秒・2 分・10 分では `lifecycle: the UI thread has still not answered after <秒> s` が記録され、戻ったときに `lifecycle: the UI thread answered again after <秒> s` が記録されます（戻った直後に閉じたときも、終了処理の中で書きます。ログでは `lifecycle: exit` の行のあとになります）。どれにも直前の操作（`loading <フォルダ>`・`showing <フォルダ>: <件数> items (<結果>)`。一覧ではフォルダの代わりにタブの名前、起動の直後は `starting`）が付きます。`analyze-log.ps1` は起動ごとに「UI の応答なし: N 回（戻ったものの最長 X 秒）」と、戻らなかったときは固まり始めた時刻と直前の操作を表示します。
- 「前回は正常に終わらなかった」の判断には `logs\session.json`（起動中だけある記録）を使います（[詳細仕様の「データの置き場所」](specification.md#1-データの置き場所)）。
- エクスプローラーのメニューを使ったあとに落ちたときは、拡張機能の異常終了の可能性があります。拡張機能はアプリの中で動くため、拡張機能が異常終了すると FolderDeck も一緒に終了することがあります（[詳細仕様の「エクスプローラーのメニュー」](specification.md#10-エクスプローラーのメニューその他のメニューwindows)の制約）。

### 7.12 FolderDeck が応答しなくなる（ファイルの多いフォルダ）

- **症状**：ファイルの多いフォルダを開いたときなどに「応答なし」になり、戻らない。CPU はほとんど使っていない。`app.log` にエラーは出ない（記録するはずの UI スレッドが止まっているため）。
- **記録**：今の版は、UI スレッドが 5 秒以上応答しないと `lifecycle: the UI thread has not answered for …` を書きます（[7.11](#711-folderdeck-が勝手に終了した)）。戻らなかったときは、`analyze-log.ps1` でいつから固まったか・直前に何を開いていたかが分かります。
- **原因の調べ方**：固まっている間に、タスク マネージャーの「詳細」タブで FolderDeck.exe を右クリック →「メモリ ダンプ ファイルの作成」（1 GB 前後。C: の空きに注意）。ダンプは Visual Studio・WinDbg・`dotnet-dump analyze`（`clrstack -all`）で開き、UI スレッド（managed thread 2 / `FolderDeck.App.Main` がいちばん下にあるスレッド）の呼び出し履歴を見ます。ダンプには開いていたフォルダの名前などが入るので、共有するときは注意します。
- **2026-11-25 に見つかった原因**：入力欄（アドレス欄）のレイアウトが変わると、WPF はそれを IME（TSF。このときは Google 日本語入力）に知らせる。IME が文字の位置を問い合わせると、WPF はその場でウィンドウ全体のレイアウトを確定する。UI オートメーションのクライアント（支援技術・自動化ツールなど、ほかのアプリ）がイベントを購読していると、WPF はレイアウトのたびに画面の要素の木（カード数千枚ならその数倍）をたどって変更を知らせる。その途中の COM 呼び出しが、メッセージを処理しない待ちのまま戻らなかった。対策（[コードベース概要 §7](../dev/docs/codebase_overview.md#7-守ること落とし穴)）：メイン ウィンドウの入力中でない入力欄はレイアウトから外す（Collapsed）、入力中は入力欄の幅を動かさず自動更新も待つ、カード・詳細表示の行は UI オートメーションに 1 項目 1 要素だけを見せる。
- **対処**：固まったら、タスク マネージャーで終了します（設定・登録フォルダなどは変更のたびに裏で保存しているので、失うのは保存が終わっていなかった直前の変更だけです）。再発するときは、上のダンプと `analyze-log.ps1 -Events` の出力を添えて報告します。

### 7.13 メモリを多く使う（タブをたくさん開いたとき）

- **記録**：`app.log` に次の 1 行が出ます。F5 のたび（キャッシュを捨てる直前の量）と、1 分ごとの記録（`SessionMonitor`）で作業セットかプライベート バイトが前回の行から 64 MB 以上動いたときです。

  ```
  memory: ws 412 MB, managed 96 MB, thumbs 384 (171 MB), listings 640 (152311 entries); private 260 MB, committed image 282 / mapped 254 / private 230 MB, gc committed 120 MB, handles 980, gdi 28, user 66, threads 44
  ```

- **読み方**：
  - `ws`：作業セット（タスク マネージャーの「メモリ」に近い値）。`managed`：.NET のヒープ（GC が管理する分。回収前のごみを含む）。
  - `thumbs`：サムネイルのメモリ キャッシュ（LRU）の件数と、画素数から見積もった大きさ。上限は 512 件・192 MB。表示中のカードの絵と、WPF が描画のために持つ複製は、ここに入りません。
  - `private`（`;` の後の最初）：プライベート バイト（コミット済みで、ほかのプロセスと共有しない分）。`ws` は Windows が使われていないページを外すと下がる（最小化・メモリ不足のとき。950 MB → 150 MB のように）ので、リークかどうかはこちらで見る。
  - `committed image / mapped / private`：コミット済みのアドレス空間の種類ごとの合計。`image` は exe と DLL（大きく増えたなら拡張機能・セキュリティ ソフトなどの DLL）、`mapped` はファイルのマッピングと共有メモリ（single-file の exe 自身、フォント、GPU と共有するメモリ）、`private` はネイティブのヒープ・GC のヒープ・スレッドのスタック。`gc committed` は GC がコミットしている量（`managed` は生きているオブジェクトだけ）。
  - `handles` / `gdi` / `user` / `threads`：カーネルのハンドル、GDI・USER のオブジェクト、スレッドの数。操作を繰り返すたびに増え続けるならリーク。
  - `listings`：フォルダの中身の一覧のキャッシュの一覧数と項目の合計。上限は 1,500 件・20 万項目（巨大な一覧 1 つは上限の 1/4 までと数えるので、項目の合計は上限を超えて見えることがあります）。期限（60 秒）の切れた一覧は、次に一覧を入れるときに 60 秒に 1 回捨てます。
- **切り分け**：`managed` が大きく伸び続けるなら .NET 側（一覧・カードの部品）、`private`（プライベート バイト）と `gc committed` の差が大きいならネイティブ側（サムネイルの画素、描画、拡張機能）です。`ws` が大きくても `private` が小さいなら、使われていないページか共有の分で、Windows が必要に応じて外します（リークではない）。タブの切り替えを 20 回ほど繰り返したり、ウィンドウの幅を何度も変えたりしたあとに F5 を押し、その前後の行を比べます。同じ操作を繰り返すたびに `managed` が増え続けるなら、ビューが残っている（リーク）可能性があります（`dev/docs/codebase_overview.md` §7 の「ビューから VM のイベントを購読するのは読み込まれている間だけ」）。
- `--data-dir` で起動したときは、その下の `logs\app.log` です。

### 7.14 エクスプローラーのメニューが時間切れになる

- **症状**：右クリックの「Windows のメニュー」で「エクスプローラーのメニューが時間内に準備できなかったため、アプリのメニューを出しました。」が出る。または「その他のメニュー（Windows）」で「このメニューは表示できませんでした：メニューの準備が時間内に終わりませんでした。」が出る。メニューの用意（表示する前）が 8 秒を超えたときです（0.7.0 までは 5 秒）。
- **記録**：`app.log` に次の行が出ます。時間切れにならなくても、用意に 1 秒以上かかったときは段階ごとの時間を INFO で書きます（時間切れのあとで用意が終わったときは `ready too late`）。

  ```
  WARN Shell menu timed out in query after 8003 ms (menu threads alive: 2): C:\Users\…\a.png
  INFO Shell menu slow to build, 1840 ms (thread 3, owner 1, bind 25, query 1790, verbs 21 ms): C:\Users\…\a.png
  INFO Shell menu diagnostics (timed out in query, background): attributes 0x10; handlers (3): Directory\Background: OneDrive = FileSyncShell64.dll; …; modules from outside Windows and the app (4): C:\Program Files\7-Zip\7-zip.dll; …
  ```

- **段階の読み方**：
  - `thread`（スレッドの起動）・`owner`（見えないオーナー ウィンドウの作成）：アプリ側。ここで止まるならアプリの不具合として調べる（メモリ ダンプでメニューのスレッド `FolderDeck shell menu` の呼び出し履歴を見る。[7.12](#712-folderdeck-が応答しなくなるファイルの多いフォルダ)）。
  - `bind`（項目の `IContextMenu` の取得）・`query`（`QueryContextMenu`：拡張機能が項目を足す）・`verbs`（各項目の verb の読み取り）：シェルと拡張機能の側。ほとんどは `query` で、どれかの拡張機能が遅いか止まっている。
  - `menu threads alive`：まだ終わっていないメニューのスレッドの数（今の 1 本を含む）。2 以上なら、前のメニューのスレッド（コマンドの後の待ち、または時間切れで放棄されて拡張機能の中で止まったまま）が残っている。前のスレッドが止まっている間の次のメニューは、同じ拡張機能を待って何十秒も `ready too late` になりやすい（時間切れのあとは少し待ってから右クリックし直す）。
  - `Shell menu diagnostics`：遅い用意（1 秒以上）か時間切れのとき、種類（`background` = 空白の右クリック、`folder`、拡張子）ごとにプロセスで 1 回だけ書く。`attributes` は項目のファイル属性（`offline` / `recall on open` / `recall on data access` / `pinned` / `unpinned` が付いていれば OneDrive などのクラウドのファイル）、`handlers` はその種類に登録された右クリックの拡張機能（レジストリの `shellex\ContextMenuHandlers`。名前 = DLL）、`modules` は Windows・アプリ・.NET の外から読み込まれている DLL（前の行で挙げたものは除く）。ここに出た DLL が切り分けの候補になる。
- **切り分け**：NirSoft の ShellExView で「種類」が Context Menu の拡張機能（Microsoft 以外から）を 1 つずつ無効にし、同じ項目で右クリックを試す。同じ種類のファイル（画像・PDF など）だけで起きるならその種類に登録された拡張機能、どの項目でも起きるなら全体の拡張機能を疑う。「Windows のメニュー」では時間切れの項目に 5 分間エクスプローラーのメニューを使わないので、試すときは 5 分待つか FolderDeck を起動し直す（「その他のメニュー（Windows）」は毎回試す）。

### 7.15 画面構成の変化のあと、前面に出ない

- **症状**：HDMI を別の外部モニターへ差し替えた・元のディスプレイに戻したあと、タスク バーや Alt + Tab で FolderDeck を選んでも最前面に出ない。原因は未確認です。画面構成が変わったとき、アプリは次の回復をします：カスケード（一覧・プレビュー）を閉じる、タイトルバーがどのモニターにも載らない通常の状態のウィンドウ（メイン ウィンドウ・確認ダイアログ・設定画面）を画面内へ戻す、描画先を作り直させる、変化から 2 分以内にアクティブになったら z-order を一番上に戻す。
- **記録**：画面構成が変わるたび（最後の変化から 700 ms 後に 1 回）、`app.log` に次の行が出ます。

  ```
  INFO display: changed; state Normal, active True, enabled True, render tier 2, cascade open False, levels 0, nativeMenu False, dragging False; main rect 120,80,1400,880, topmost False, iconic False, onMonitor True, foreground self, above 0x0 pid 0 (self pid 1234)
  INFO display: owned ConfirmDialog, state Normal, visible True; rect …
  INFO display: MainWindow was on no monitor (2040,100); moved to 320,140
  INFO display: activated after a change; raised to the top; rect …
  ```

- **読み方**（現象が起きた直後の行を見る）：
  - `foreground self` で `above 0x0`（上に見えるウィンドウがない）なのに中身が古い・黒い：描画先が止まっている（別の GPU に出力が移った場合を疑う）。`render tier` が 0 ならソフトウェア描画に落ちている。
  - `above` は自分より上にある最初の見えるウィンドウ。最前面（タスク バーなど）と隠れた（cloaked）ウィンドウは数えない。
  - `above` がほかのプロセスのウィンドウ：アクティブになっても z-order が上がっていない。そのあと `display: activated after a change` の行が出ていれば、引き上げは試みている。
  - `foreground` が自分以外で、選んでも `activated after a change` が出ない：Windows がアクティブ化を断っている（タスク バーのボタンが点滅するだけ）。アプリからは前面を奪わない。
  - `topmost True`：最前面の設定が残っている（アプリはメイン ウィンドウを最前面のままにしないので、不具合として調べる）。
  - `onMonitor False`：タイトルバーがどのモニターにも載っていない。通常の状態なら `was on no monitor` の行で戻したことが分かる。最大化・最小化のときは Windows に任せている。モニターが列挙されたまま入力だけ切り替えた場合は、見えなくても `onMonitor True` になる。
  - `enabled False` と `display: owned …`：確認ダイアログか設定画面が開いている。そのダイアログの `onMonitor` を見る（ダイアログが見えないと、メイン ウィンドウはクリックに反応しない）。
  - `nativeMenu True` / `dragging True`：エクスプローラーのメニューかドラッグの途中で、カスケードは閉じていない（それぞれが終わったときに閉じる）。
- **再現のしかた**（`run.bat --data-dir E:\tmp\folderdeck-data` で起動し、変えるたびにタスク バーと Alt + Tab で選んで、中身ごと最前面に出るかを見る）：
  - Win + P で「PC 画面のみ」→「拡張」→「セカンド スクリーンのみ」→「拡張」と切り替える。
  - 設定 → ディスプレイで、片方のモニターの解像度と拡大率を変える。
  - HDMI を抜く・別のモニターへ差し替えて戻す。GPU が 2 つある PC では、別の GPU のポートをまたぐ組み合わせも試す。
  - 通常の状態のウィンドウをセカンダリに置いてセカンダリを外す（プライマリに戻ることを見る。Windows が先に動かした場合は `was on no monitor` の行は出ない）。
  - サイドバーの行の一覧やファイルの行のプレビューを開いたまま切り替え、閉じることを見る。
