# FolderDeck のログ（logs\app.log, app.log.1）を起動ごと（セッション）に分けて、
# 「いつ起動し、どう終わったか」と警告・エラーをまとめて表示します。
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\analyze-log.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\analyze-log.ps1 -Last 30 -Events
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\analyze-log.ps1 -LogDir E:\tmp\folderdeck-data\logs
#
#   -LogDir  ログのフォルダ（既定 %LOCALAPPDATA%\FolderDeck\logs。--data-dir で起動したときはその下の logs）
#   -Last    表示するセッションの数（新しい順に数えて。既定 10）
#   -Events  Windows のイベントログ（Application）から FolderDeck.exe のクラッシュ記録も探す
#
# 終わり方の判定に使うのは、アプリが書く "lifecycle:" の行です（Services\SessionMonitor.cs）。
# このファイルは UTF-8（BOM 付き）で保存してください（Windows PowerShell 5.1 が日本語を正しく読むため）。

param(
    [string]$LogDir = (Join-Path $env:LOCALAPPDATA 'FolderDeck\logs'),
    [int]$Last = 10,
    [switch]$Events
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$files = @('app.log.1', 'app.log') | ForEach-Object { Join-Path $LogDir $_ } | Where-Object { Test-Path -LiteralPath $_ }
if (-not $files) {
    Write-Host "ログが見つかりません: $LogDir" -ForegroundColor Yellow
    exit 1
}

# ---- 行を読む（例外のスタックトレースなど、続きの行は直前の行にまとめる） -------------------

$lineRegex = '^(?<ts>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}) (?<tz>[+-]\d\d:\d\d) \[(?<lvl>[A-Z]+)\] \[(?:p(?<pid>\d+)/t)?(?<tid>\d+)\] (?<msg>.*)$'
$entries = New-Object System.Collections.Generic.List[object]
foreach ($file in $files) {
    foreach ($line in [IO.File]::ReadAllLines($file, [Text.Encoding]::UTF8)) {
        $m = [regex]::Match($line, $lineRegex)
        if ($m.Success) {
            $entries.Add([pscustomobject]@{
                Time  = [DateTimeOffset]::Parse($m.Groups['ts'].Value + ' ' + $m.Groups['tz'].Value, [Globalization.CultureInfo]::InvariantCulture)
                Level = $m.Groups['lvl'].Value
                Pid   = $m.Groups['pid'].Value
                Msg   = $m.Groups['msg'].Value
                More  = New-Object System.Collections.Generic.List[string]
            })
        }
        elseif ($entries.Count -gt 0 -and $line.Trim() -ne '') {
            $entries[$entries.Count - 1].More.Add($line)
        }
    }
}

# ---- セッションに分ける -----------------------------------------------------------------------

function New-Session($entry) {
    [pscustomobject]@{
        Start      = $entry.Time
        Pid        = $entry.Pid
        Version    = ''
        ExeBuilt   = $null
        LastTime   = $entry.Time
        Exit       = $null
        CloseHow   = $null
        Ending     = $null
        Crash      = $null
        Unclean    = $null      # 次の起動が報告した「前回は正常に終わらなかった」
        CrashLogs  = New-Object System.Collections.Generic.List[string]
        NoCrashLog = $false
        Warnings   = 0
        Errors     = New-Object System.Collections.Generic.List[object]
    }
}

$sessions = New-Object System.Collections.Generic.List[object]
$current = $null
foreach ($e in $entries) {
    $msg = $e.Msg
    $isStart = $msg -match '^lifecycle: start FolderDeck (?<ver>\S+)' -or $msg -match '^FolderDeck (?<ver>\S+) starting'
    if ($isStart) {
        $current = New-Session $e
        $current.Version = $Matches['ver']
        if ($msg -match 'built (?<built>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)') {
            $current.ExeBuilt = [DateTime]::ParseExact($Matches['built'], 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
        }
        $sessions.Add($current)
        continue
    }

    if ($null -eq $current) { continue }
    $current.LastTime = $e.Time

    if ($msg -match '^lifecycle: previous session did not exit cleanly') {
        if ($sessions.Count -ge 2) { $sessions[$sessions.Count - 2].Unclean = $msg -replace '^lifecycle: ', '' }
        continue
    }
    if ($msg -match '^lifecycle: event log crash record: (?<rec>.*)') {
        if ($sessions.Count -ge 2) { $sessions[$sessions.Count - 2].CrashLogs.Add($Matches['rec']) }
        continue
    }
    if ($msg -match '^lifecycle: the Application event log has no crash record') {
        if ($sessions.Count -ge 2) { $sessions[$sessions.Count - 2].NoCrashLog = $true }
        continue
    }

    if ($msg -match '^lifecycle: close requested (?<how>.*?); foreground app: (?<fg>.*)$') {
        $current.CloseHow = [pscustomobject]@{ How = $Matches['how']; Foreground = $Matches['fg']; Time = $e.Time }
    }
    elseif ($msg -match '^lifecycle: Windows (asks to end|is ending) the session \((?<why>.*)\)') {
        $current.Ending = $Matches['why']
    }
    elseif ($msg -match '^lifecycle: crash') {
        $current.Crash = $e
    }
    elseif ($msg -match '^lifecycle: exit' -or $msg -eq 'FolderDeck exiting') {
        $current.Exit = $e.Time
    }

    if ($e.Level -eq 'WARN' -and $msg -notmatch '^lifecycle:') { $current.Warnings++ }
    if ($e.Level -eq 'ERROR') { $current.Errors.Add($e) }
}

if ($sessions.Count -eq 0) {
    Write-Host 'ログに起動の記録がありません。' -ForegroundColor Yellow
    exit 1
}

# ---- 表示 -------------------------------------------------------------------------------------

function Format-Span([TimeSpan]$span) {
    if ($span.TotalHours -ge 1) { return ('{0} 時間 {1} 分' -f [int][Math]::Floor($span.TotalHours), $span.Minutes) }
    if ($span.TotalMinutes -ge 1) { return ('{0} 分 {1} 秒' -f [int][Math]::Floor($span.TotalMinutes), $span.Seconds) }
    return ('{0:N1} 秒' -f $span.TotalSeconds)
}

function Test-Running($procId) {
    if (-not $procId) { return $false }
    $p = Get-Process -Id ([int]$procId) -ErrorAction SilentlyContinue
    return ($null -ne $p -and $p.ProcessName -eq 'FolderDeck')
}

$first = [Math]::Max(0, $sessions.Count - $Last)
Write-Host ("ログ: {0}（全 {1} セッション、新しい {2} 件を表示）" -f $LogDir, $sessions.Count, ($sessions.Count - $first))
Write-Host ''

for ($i = $first; $i -lt $sessions.Count; $i++) {
    $s = $sessions[$i]
    $next = $null
    if ($i + 1 -lt $sessions.Count) { $next = $sessions[$i + 1] }

    $color = 'Gray'
    if ($s.Crash) {
        $how = 'クラッシュ（未処理の例外）'
        $color = 'Red'
    }
    elseif ($s.Exit) {
        if ($s.Ending) { $how = "Windows のセッション終了で閉じた（$($s.Ending)）" }
        elseif ($s.CloseHow -and $s.CloseHow.How -match '^by the user') { $how = 'ユーザーが閉じた（× / Alt+F4 / タスクバー）' }
        elseif ($s.CloseHow -and $s.CloseHow.How -match '^by another program') {
            $how = "別のプログラムが閉じた（WM_CLOSE。前面のアプリ: $($s.CloseHow.Foreground)）"
            $color = 'Yellow'
        }
        elseif ($s.CloseHow) { $how = "閉じた（$($s.CloseHow.How)）" }
        else { $how = '正常に終了（閉じた理由は記録なし：古い形式のログ）' }
    }
    elseif ($s.Unclean) {
        $how = '異常終了（終了処理が走っていない：クラッシュ・強制終了・電源断のどれか）'
        $color = 'Red'
    }
    elseif ($i -eq $sessions.Count - 1 -and (Test-Running $s.Pid)) {
        $how = '実行中'
        $color = 'Green'
    }
    else {
        $how = '終了の記録なし（強制終了された可能性。次の起動で詳しく記録されます）'
        $color = 'Red'
    }

    $end = $s.LastTime
    if ($s.Exit) { $end = $s.Exit }
    $pidText = ''
    if ($s.Pid) { $pidText = " pid $($s.Pid)" }
    Write-Host ("[{0}] {1:yyyy-MM-dd HH:mm:ss} 〜 {2:HH:mm:ss}（{3}） v{4}{5}" -f ($i + 1), $s.Start, $end, (Format-Span ($end - $s.Start)), $s.Version, $pidText)
    Write-Host ("    終わり方: {0}" -f $how) -ForegroundColor $color

    if ($s.Unclean) { Write-Host ("    次の起動の報告: {0}" -f $s.Unclean) -ForegroundColor DarkYellow }
    foreach ($rec in $s.CrashLogs) { Write-Host ("    イベントログ: {0}" -f $rec) -ForegroundColor Red }
    if ($s.NoCrashLog) { Write-Host '    イベントログ: クラッシュの記録なし（ほかのプログラムによる強制終了か電源断の可能性が高い）' -ForegroundColor DarkYellow }

    # 閉じられた直後に exe がビルドし直されていれば、ビルドスクリプトが閉じた可能性が高い。
    if ($next -and $next.ExeBuilt -and -not $s.Crash) {
        $endLocal = $end.LocalDateTime
        $gap = $next.ExeBuilt - $endLocal
        # ビルド時刻は秒単位なので 1 秒だけ手前も許す。このセッションより前のビルドは対象外。
        if ($next.ExeBuilt -gt $s.Start.LocalDateTime -and $gap.TotalSeconds -ge -1 -and $gap.TotalMinutes -le 15) {
            if ($gap.TotalSeconds -lt 0) { $gap = [TimeSpan]::Zero }
            Write-Host ("    ヒント: 次の起動の exe は終了の {0} 後にビルドされています（ビルドやコピーのために閉じられた可能性）" -f (Format-Span $gap)) -ForegroundColor DarkYellow
        }
    }

    if ($s.Warnings -gt 0 -or $s.Errors.Count -gt 0) {
        Write-Host ("    警告 {0} 件 / エラー {1} 件" -f $s.Warnings, $s.Errors.Count)
        $shown = 0
        for ($k = $s.Errors.Count - 1; $k -ge 0 -and $shown -lt 3; $k--) {
            $err = $s.Errors[$k]
            $detail = ''
            if ($err.More.Count -gt 0) { $detail = ' — ' + $err.More[0].Trim() }
            Write-Host ("      {0:HH:mm:ss} {1}{2}" -f $err.Time, $err.Msg, $detail) -ForegroundColor Red
            $shown++
        }
    }

    Write-Host ''
}

# ---- Windows のイベントログ -------------------------------------------------------------------

if ($Events) {
    $since = $sessions[$first].Start.LocalDateTime.AddMinutes(-1)
    Write-Host ("Windows イベントログ（Application、{0:yyyy-MM-dd HH:mm} 以降）で FolderDeck のクラッシュ記録を探します…" -f $since)
    $filter = @{ LogName = 'Application'; StartTime = $since; ProviderName = @('Application Error', '.NET Runtime', 'Windows Error Reporting', 'Application Hang') }
    $found = @(Get-WinEvent -FilterHashtable $filter -ErrorAction SilentlyContinue | Where-Object { $_.Message -match 'FolderDeck' })
    if ($found.Count -eq 0) {
        Write-Host '  見つかりませんでした。' -ForegroundColor Green
    }
    else {
        foreach ($ev in $found | Sort-Object TimeCreated) {
            $text = ($ev.Message -replace '\s+', ' ').Trim()
            if ($text.Length -gt 400) { $text = $text.Substring(0, 400) + '…' }
            Write-Host ("  {0:yyyy-MM-dd HH:mm:ss} {1} (event {2}): {3}" -f $ev.TimeCreated, $ev.ProviderName, $ev.Id, $text) -ForegroundColor Red
        }
    }
}
