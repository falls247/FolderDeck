# FolderDeck 開発用タスク（build / test / run / all / publish）
#
#   tools\dev.ps1 build   [Debug|Release]   ソリューションをビルド（既定 Debug）
#   tools\dev.ps1 test    [dotnet test の追加引数]   Core のテスト
#   tools\dev.ps1 run     [アプリへの引数]   Debug ビルドして起動（例: --data-dir E:\tmp\data）
#   tools\dev.ps1 all     [アプリへの引数]   ソリューションを Debug ビルド → Core のテスト → 起動（別名 build-test-run）。
#                                           失敗した段階で止まる（引数は run と同じくアプリに渡す）
#   tools\dev.ps1 publish [framework]       配布用の single-file exe を作り dist\ にコピー
#                                           既定: .NET を同梱して圧縮した exe（dist\FolderDeck.exe、約 65 MB）
#                                           framework: .NET 10 デスクトップ ランタイムを別に入れて使う exe
#                                           （dist\framework-dependent\FolderDeck.exe、約 2 MB）
#
# ルートの build.bat / test.bat / run.bat / build-test-run.bat / publish.bat から呼ばれます（ダブルクリック可）。
# このフォルダの中で起動中の FolderDeck はファイルをロックするため、ビルド前に終了を確認します。
# 確認なしで終了させたいときは、環境変数 FOLDERDECK_AUTOCLOSE=1 を設定してください。
#
# このファイルは UTF-8（BOM 付き）で保存してください（Windows PowerShell 5.1 が日本語を正しく読むため）。

$ErrorActionPreference = 'Continue'
$Root = Split-Path -Parent $PSScriptRoot
$Solution = Join-Path $Root 'FolderDeck.sln'
$AppProject = Join-Path $Root 'src\FolderDeck.App\FolderDeck.App.csproj'
$TestProject = Join-Path $Root 'tests\FolderDeck.Core.Tests\FolderDeck.Core.Tests.csproj'
$DebugExe = Join-Path $Root 'src\FolderDeck.App\bin\Debug\net10.0-windows\FolderDeck.exe'
$PublishExe = Join-Path $Root 'src\FolderDeck.App\bin\Release\net10.0-windows\win-x64\publish\FolderDeck.exe'
$DistDir = Join-Path $Root 'dist'
$DistFrameworkDir = Join-Path $DistDir 'framework-dependent'

# 各タスクが上書きする exe のフォルダ。ここから起動中の FolderDeck だけが邪魔になる。
# publish はコピー先（dist または dist\framework-dependent）をこれに足して確認する。
$DebugDir = Join-Path $Root 'src\FolderDeck.App\bin\Debug\net10.0-windows'
$ReleaseDir = Join-Path $Root 'src\FolderDeck.App\bin\Release\net10.0-windows'
$PublishDirs = @(
    (Join-Path $ReleaseDir 'win-x64'),
    (Join-Path $ReleaseDir 'win-x64\publish')
)

# 引数は powershell.exe 自身のコマンドラインから取り直す。-File 経由の $args では
# -p:Foo=1 や -tl:off のような「-名前:値」が2つに分かれてしまうため（dotnet に渡すと壊れる）。
function Get-ScriptArguments([object[]]$fallback) {
    $argv = [Environment]::GetCommandLineArgs()
    for ($i = 0; $i -lt $argv.Count - 1; $i++) {
        $name = $null
        try { $name = [IO.Path]::GetFileName($argv[$i + 1]) } catch { }
        if ($argv[$i] -ieq '-File' -and $name -ieq 'dev.ps1') {
            if ($i + 2 -le $argv.Count - 1) {
                return , @($argv[($i + 2)..($argv.Count - 1)])
            }

            return , @()
        }
    }

    # tools\dev.ps1 を PowerShell の中から直接呼んだとき。
    return , @($fallback | ForEach-Object { [string]$_ })
}

$allArgs = Get-ScriptArguments $args
$task = if ($allArgs.Count -gt 0) { ([string]$allArgs[0]).ToLowerInvariant() } else { 'help' }
$rest = @()
if ($allArgs.Count -gt 1) { $rest = @($allArgs[1..($allArgs.Count - 1)]) }

# エクスプローラーからダブルクリックされたとき、cmd のコマンドラインは  cmd.exe /c ""...\x.bat" "  になる。
# そのときだけ最後に一時停止して、結果を読めるようにする（ターミナルから実行したときは止めない）。
$fromExplorer = [string]$env:FD_CMDLINE -match '\.(bat|cmd)" "\s*$'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Write-Command([string]$text) {
    Write-Host "> $text" -ForegroundColor DarkGray
}

function Test-Dotnet {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host 'dotnet が見つかりません。.NET SDK 10 をインストールしてください: https://dotnet.microsoft.com/download' -ForegroundColor Red
        return $false
    }

    $sdks = @(& dotnet --list-sdks 2>$null)
    if (-not ($sdks | Where-Object { $_ -match '^10\.' })) {
        Write-Host '.NET SDK 10 が見つかりません。インストール済みの SDK:' -ForegroundColor Red
        $sdks | ForEach-Object { Write-Host "  $_" }
        return $false
    }

    return $true
}

# これから上書きするフォルダ（$Dirs）から起動している FolderDeck だけを、確認のうえ普通に閉じる
# （閉じるときに保存処理が走る）。ほかの場所から起動している FolderDeck には触れない。
function Stop-RunningApp([string[]]$Dirs) {
    $targets = @($Dirs | ForEach-Object { $_.TrimEnd('\') })
    $procs = @(Get-Process -Name FolderDeck -ErrorAction SilentlyContinue | Where-Object {
            $path = $null
            try { $path = $_.Path } catch { }
            if (-not $path) { return $false }
            $dir = (Split-Path -Parent $path).TrimEnd('\')
            @($targets | Where-Object { $_ -ieq $dir }).Count -gt 0
        })
    if ($procs.Count -eq 0) {
        return $true
    }

    Write-Host ''
    Write-Host 'このフォルダの FolderDeck が起動中です（exe がロックされるため、閉じないとビルドできません）:' -ForegroundColor Yellow
    foreach ($p in $procs) {
        Write-Host "  $($p.Path)  (PID $($p.Id))"
    }

    if ($env:FOLDERDECK_AUTOCLOSE -ne '1') {
        # 誰も答えられない実行（入力がリダイレクトされている）では、勝手に閉じずに中止する。
        $canAsk = $true
        try {
            $canAsk = -not [Console]::IsInputRedirected
        }
        catch {
            $canAsk = $false
        }

        if (-not $canAsk) {
            Write-Host '確認できないため中止しました。FolderDeck を閉じてから実行するか、FOLDERDECK_AUTOCLOSE=1 を設定してください。' -ForegroundColor Yellow
            return $false
        }

        $answer = $null
        try {
            # 先に押されていたキー（起動待ちの間の Enter など）で勝手に答えないよう、入力を捨ててから聞く。
            Start-Sleep -Milliseconds 300
            try { $Host.UI.RawUI.FlushInputBuffer() } catch { }
            $answer = Read-Host '閉じて続けますか？ [Y/n]'
        }
        catch {
            $answer = $null
        }

        if ($null -eq $answer -or ($answer.Trim() -ne '' -and $answer.Trim() -notmatch '^(y|yes)$')) {
            Write-Host '中止しました。' -ForegroundColor Yellow
            return $false
        }
    }

    foreach ($p in $procs) {
        [void]$p.CloseMainWindow()
    }

    foreach ($p in $procs) {
        if (-not $p.WaitForExit(15000)) {
            Write-Host "PID $($p.Id) が終了しません（確認ダイアログが開いていませんか？）。閉じてからもう一度実行してください。" -ForegroundColor Red
            return $false
        }
    }

    Write-Host '閉じました。'
    return $true
}

# FolderDeck は1つしか起動できない（名前付き Mutex）。ほかの場所の FolderDeck が動いていると、新しいビルドを
# 起動しても既存のウィンドウが前に出るだけ（--data-dir も効かない）なので、ビルドの前に知らせる。閉じはしない。
function Test-OtherInstance {
    $busy = $false
    $mutex = $null
    try {
        $busy = [Threading.Mutex]::TryOpenExisting('Local\FolderDeck.SingleInstance', [ref]$mutex)
    }
    catch [UnauthorizedAccessException] {
        $busy = $true # 管理者として起動された FolderDeck
    }
    catch {
        $busy = $false
    }
    finally {
        if ($mutex) { $mutex.Dispose() }
    }

    if (-not $busy) {
        return $false
    }

    Write-Host ''
    Write-Host 'ほかの場所の FolderDeck が起動中です。FolderDeck は1つしか起動できないため、このまま起動しても新しいビルドは開かず、既存のウィンドウが前に出るだけです（--data-dir も効きません）。そちらを閉じてから実行してください:' -ForegroundColor Yellow
    foreach ($p in @(Get-Process -Name FolderDeck -ErrorAction SilentlyContinue)) {
        $path = $null
        try { $path = $p.Path } catch { }
        if (-not $path) { $path = '（場所不明）' }
        Write-Host "  $path  (PID $($p.Id))"
    }

    return $true
}

# Start-Process に渡す引数を1つの文字列にする（空白や " を含む引数を正しく引用する）。
function Join-Arguments([string[]]$list) {
    $parts = foreach ($a in $list) {
        if ($a -eq '' -or $a -match '[\s"]') {
            $escaped = $a -replace '(\\*)"', '$1$1\"'
            $escaped = $escaped -replace '(\\+)$', '$1$1'
            '"' + $escaped + '"'
        }
        else {
            $a
        }
    }

    return ($parts -join ' ')
}

# ビルド済みの Debug の exe を、アプリへの引数（$AppArgs）つきで起動する（run と all で共通）。
# 起動できたら $true。すぐに終了したら起動できていない（ほかの FolderDeck が先に起動した、起動直後に落ちた、など）ので $false。
function Start-DebugApp([string]$Title, [string[]]$AppArgs) {
    if (-not (Test-Path -LiteralPath $DebugExe)) {
        Write-Host "exe が見つかりません: $DebugExe" -ForegroundColor Red
        return $false
    }

    Write-Step $Title
    if ($AppArgs.Count -gt 0) {
        $argText = Join-Arguments $AppArgs
        Write-Command "FolderDeck.exe $argText"
        $app = Start-Process -FilePath $DebugExe -ArgumentList $argText -WorkingDirectory $Root -PassThru -ErrorAction Stop
    }
    else {
        Write-Command 'FolderDeck.exe'
        $app = Start-Process -FilePath $DebugExe -WorkingDirectory $Root -PassThru -ErrorAction Stop
    }

    if ($app.WaitForExit(2000)) {
        Write-Host "起動した FolderDeck がすぐに終了しました（終了コード $($app.ExitCode)）。ほかの FolderDeck が起動していないか、ログ（logs\app.log）を確認してください。" -ForegroundColor Red
        return $false
    }

    return $true
}

function Show-Help {
    Write-Host 'FolderDeck 開発用タスク'
    Write-Host ''
    Write-Host '  build.bat          [Debug|Release]    ソリューションをビルド（既定 Debug）'
    Write-Host '  test.bat           [追加の引数]       Core のテスト（例: test.bat --filter Ranking）'
    Write-Host '  run.bat            [アプリへの引数]   Debug ビルドして起動（例: run.bat --data-dir E:\tmp\data）'
    Write-Host '  build-test-run.bat [アプリへの引数]   ビルド（Debug）→ テスト → 起動を続けて実行。失敗した段階で止まる（引数は run.bat と同じ）'
    Write-Host '  publish.bat                           配布用の exe（.NET 同梱・圧縮、約 65 MB）を作って dist\FolderDeck.exe にコピー'
    Write-Host '  publish.bat framework                 .NET 10 デスクトップ ランタイムが必要な小さい exe（約 2 MB）を作って'
    Write-Host '                                        dist\framework-dependent\FolderDeck.exe にコピー'
}

$timer = [Diagnostics.Stopwatch]::StartNew()
$code = 0
$quietSuccess = $false
Push-Location $Root
try {
    if ($task -eq 'build') {
        $config = 'Debug'
        if ($rest.Count -gt 0) { $config = $rest[0] }
        if ($config -ieq 'debug') { $config = 'Debug' }
        elseif ($config -ieq 'release') { $config = 'Release' }
        else {
            Write-Host "構成は Debug か Release を指定してください（指定: $config）。" -ForegroundColor Red
            $code = 2
        }

        if ($code -eq 0) {
            $dirs = if ($config -eq 'Debug') { @($DebugDir) } else { @($ReleaseDir) }
            if (-not (Test-Dotnet) -or -not (Stop-RunningApp $dirs)) {
                $code = 1
            }
            else {
                Write-Step "ビルド（$config）"
                Write-Command "dotnet build FolderDeck.sln -c $config"
                & dotnet build $Solution -c $config --nologo
                $code = $LASTEXITCODE
            }
        }
    }
    elseif ($task -eq 'test') {
        if (-not (Test-Dotnet)) {
            $code = 1
        }
        else {
            Write-Step 'テスト（FolderDeck.Core.Tests）'
            Write-Command ("dotnet test tests\FolderDeck.Core.Tests -c Debug " + (Join-Arguments $rest))
            & dotnet test $TestProject -c Debug --nologo @rest
            $code = $LASTEXITCODE
        }
    }
    elseif ($task -eq 'run') {
        if (-not (Test-Dotnet) -or -not (Stop-RunningApp @($DebugDir)) -or (Test-OtherInstance)) {
            $code = 1
        }
        else {
            Write-Step 'ビルド（Debug）'
            Write-Command 'dotnet build src\FolderDeck.App -c Debug'
            & dotnet build $AppProject -c Debug --nologo
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                if (Start-DebugApp '起動' $rest) {
                    $quietSuccess = $true
                }
                else {
                    $code = 1
                }
            }
        }
    }
    elseif ($task -in @('all', 'build-test-run')) {
        # ビルド → テスト → 起動を続けて行い、失敗した段階で止まる。引数はすべてアプリに渡す（run と同じ）。
        if (-not (Test-Dotnet) -or -not (Stop-RunningApp @($DebugDir)) -or (Test-OtherInstance)) {
            $code = 1
        }
        else {
            Write-Step '1/3 ビルド（Debug）'
            Write-Command 'dotnet build FolderDeck.sln -c Debug'
            & dotnet build $Solution -c Debug --nologo
            $code = $LASTEXITCODE
            if ($code -ne 0) {
                Write-Host 'ビルドで失敗しました。テストと起動はしません。' -ForegroundColor Red
            }
            else {
                # テストのプロジェクトもソリューションに入っていて 1/3 でビルド済みなので、--no-build でビルドし直さない。
                Write-Step '2/3 テスト（FolderDeck.Core.Tests）'
                Write-Command 'dotnet test tests\FolderDeck.Core.Tests -c Debug --no-build'
                & dotnet test $TestProject -c Debug --no-build --nologo
                $code = $LASTEXITCODE
                if ($code -ne 0) {
                    Write-Host 'テストで失敗しました。起動はしません。' -ForegroundColor Red
                }
                elseif (Start-DebugApp '3/3 起動' $rest) {
                    $quietSuccess = $true
                }
                else {
                    Write-Host '起動で失敗しました。' -ForegroundColor Red
                    $code = 1
                }
            }
        }
    }
    elseif ($task -eq 'publish') {
        # 既定は .NET 同梱（self-contained）。framework は .NET を同梱しない（framework-dependent）。
        # 圧縮（EnableCompressionInSingleFile）は self-contained のときだけ csproj で有効になる。
        $framework = $false
        if ($rest.Count -gt 0) {
            if ([string]$rest[0] -ieq 'framework') { $framework = $true }
            else {
                Write-Host "publish の引数は framework だけです（指定: $($rest[0])）。" -ForegroundColor Red
                $code = 2
            }
        }

        $destDir = if ($framework) { $DistFrameworkDir } else { $DistDir }
        $selfContained = if ($framework) { 'false' } else { 'true' }
        if ($code -eq 0 -and (-not (Test-Dotnet) -or -not (Stop-RunningApp ($PublishDirs + $destDir)))) {
            $code = 1
        }

        if ($code -eq 0) {
            $kind = if ($framework) { 'framework-dependent' } else { 'self-contained / 圧縮' }
            Write-Step "配布用 exe の作成（Release / win-x64 / $kind / single-file）"
            Write-Command "dotnet publish src\FolderDeck.App -c Release -r win-x64 -p:SelfContained=$selfContained -p:PublishSingleFile=true"
            & dotnet publish $AppProject -c Release -r win-x64 "-p:SelfContained=$selfContained" -p:PublishSingleFile=true --nologo
            $code = $LASTEXITCODE
            if ($code -eq 0) {
                if (-not (Test-Path -LiteralPath $PublishExe)) {
                    Write-Host "exe が見つかりません: $PublishExe" -ForegroundColor Red
                    $code = 1
                }
                else {
                    try {
                        New-Item -ItemType Directory -Force -Path $destDir | Out-Null
                        $target = Join-Path $destDir 'FolderDeck.exe'
                        Copy-Item -LiteralPath $PublishExe -Destination $target -Force -ErrorAction Stop
                        $sizeMb = [Math]::Round((Get-Item -LiteralPath $target).Length / 1MB, 1)
                        Write-Host ''
                        Write-Host "配布用 exe: $target（$sizeMb MB）" -ForegroundColor Green
                        if ($framework) {
                            Write-Host '使う PC に .NET 10 デスクトップ ランタイム（x64）が必要です。入っていない PC では、起動時にダウンロードを促す画面が出て起動しません。'
                        }
                        Write-Host '新しい exe を初めて起動するときは、ウイルス対策ソフトのスキャンで 30 秒ほどかかることがあります。'
                    }
                    catch {
                        Write-Host "dist へのコピーに失敗しました: $($_.Exception.Message)" -ForegroundColor Red
                        $code = 1
                    }
                }
            }
        }
    }
    elseif ($task -in @('help', '-h', '--help', '/?')) {
        Show-Help
    }
    else {
        Write-Host "不明なタスクです: $task" -ForegroundColor Red
        Show-Help
        $code = 2
    }
}
catch {
    Write-Host "予期しないエラーが発生しました: $($_.Exception.Message)" -ForegroundColor Red
    $code = 1
}
finally {
    Pop-Location
}

if ($null -eq $code) { $code = 1 }
$seconds = [Math]::Round($timer.Elapsed.TotalSeconds, 1)
Write-Host ''
if ($code -eq 0) {
    Write-Host "完了しました（$seconds 秒）。" -ForegroundColor Green
}
else {
    Write-Host "失敗しました（終了コード $code、$seconds 秒）。" -ForegroundColor Red
}

# ダブルクリックで起動したときは閉じる前に止める（run / all が成功したときはアプリが出るので止めない）。
if ($fromExplorer -and -not ($quietSuccess -and $code -eq 0)) {
    try {
        try { $Host.UI.RawUI.FlushInputBuffer() } catch { }
        [void](Read-Host 'Enter キーで閉じます')
    }
    catch {
    }
}

exit $code
