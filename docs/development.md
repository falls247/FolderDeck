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
src/FolderDeck.Core/   UI に依存しないロジック（パス、列挙、キャッシュ、カバー探索、JSON 保存、順位付け、並べ替え、プレビュー用の読み取り、ドラッグで渡すときの判定）
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
| 異常終了 | 終了処理が走っていない。次の起動時に、イベントログ（Application）の FolderDeck.exe のクラッシュ記録も調べて書き出します。記録がなければ強制終了（`taskkill /F`、タスクマネージャー）か電源断 |

- 「前回は正常に終わらなかった」の判断には `logs\session.json`（起動中だけある記録）を使います（[詳細仕様の「データの置き場所」](specification.md#1-データの置き場所)）。
- エクスプローラーのメニューを使ったあとに落ちたときは、拡張機能の異常終了の可能性があります。拡張機能はアプリの中で動くため、拡張機能が異常終了すると FolderDeck も一緒に終了することがあります（[詳細仕様の「エクスプローラーのメニュー」](specification.md#10-エクスプローラーのメニューその他のメニューwindows)の制約）。
