param([ValidateSet('Lock', 'Run')][string]$Mode)
$ErrorActionPreference = 'Stop'

# bat から渡すパスを PowerShell / cmd のコード文字列に埋め込まない。
function Write-WatchdogLog([string]$Message) {
    $line = '[{0}] station={1} {2} restart={3}' -f (Get-Date), $env:PHONESABER_STATION, $Message, $env:RESTART_COUNT
    try { Add-Content -LiteralPath $env:LOG_FILE -Value $line -Encoding UTF8 }
    catch { Write-Host $line } # ログを保存できなくてもハング検出 / 二重起動防止は続ける。
}

if ($Mode -eq 'Lock') {
    $lock = $null
    $mutex = $null
    $owned = $false
    try {
        # Global mutex は別ユーザー / キットのコピーも含めて PC 全体で共有する。
        # ファイルの存在ではなく OS の排他 handle が生存確認。異常終了 / 電源断後も自動で取得できる。
        # A/B とキットのコピー間で共有する(同じ Windows ユーザーで運用)。
        $lockPath = Join-Path $env:TEMP 'phonesaber-watchdog.lock'
        try {
            $mutex = New-Object Threading.Mutex -ArgumentList $false, 'Global\PhoneSaber-watchdog'
            try { $owned = $mutex.WaitOne(0) }
            catch [Threading.AbandonedMutexException] { $owned = $true }
            if (-not $owned) { throw 'watchdog already running' }
            $lock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        } catch {
            Write-Host 'この PC では既に監視が動いているか、監視ロックを開けません。先に開いた画面を確認してください。'
            Write-WatchdogLog 'not-started already-running-or-lock-unavailable'
            Read-Host 'Enter キーで終了' | Out-Null
            exit 1
        }
        if (Get-Process -Name '3D-Saber' -ErrorAction SilentlyContinue) {
            Write-Host '3D-Saber.exe が既に起動しています。ゲームを Quit してから、もう一度起動してください。'
            Write-WatchdogLog 'not-started game-already-running'
            Read-Host 'Enter キーで終了' | Out-Null
            exit 1
        }
        # 同じコンソールを使う子 cmd の終了まで lock を保持。再起動の5秒間も二重起動を防ぐ。
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $env:ComSpec
        $info.UseShellExecute = $false
        $info.Arguments = '/d /s /c ""%PHONESABER_BATCH%" --locked"'
        $batch = [Diagnostics.Process]::Start($info)
        $batch.WaitForExit()
        $code = $batch.ExitCode
        $batch.Dispose()
        exit $code
    } finally {
        if ($null -ne $lock) { $lock.Dispose() }
        if ($owned) { $mutex.ReleaseMutex() }
        if ($null -ne $mutex) { $mutex.Dispose() }
        # 削除しない。他の起動が取得した handle を消してしまう競合を避ける。
    }
}

$heartbeat = Join-Path $env:TEMP ('phonesaber-heartbeat-' + [Guid]::NewGuid().ToString('N'))
$game = $null
$seen = $false
$hung = $false
$lastWrite = [DateTime]::MinValue
$hangSeconds = 45
$value = 0
if ([int]::TryParse($env:PHONESABER_HANG_SECONDS, [ref]$value) -and $value -ge 0 -and $value -le 999999) {
    $hangSeconds = $value
}
try {
    $exe = [IO.Path]::GetFullPath($env:GAME_EXE)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = '-phonesaberStation ' + $env:PHONESABER_STATION + ' -phonesaberHeartbeat "' + $heartbeat + '"'
    # Start が返す実際の Player の handle で監視する。名前で他の process を kill しない。
    $game = [Diagnostics.Process]::Start($info)
    $commonStop = Join-Path ([IO.Path]::GetDirectoryName($env:STOP_FILE)) 'STOP'
    $lastTick = [DateTime]::UtcNow
    $graceUntil = [DateTime]::MinValue
    while (-not $game.WaitForExit(1000)) {
        # 監視自身が止まっていた（PC のスリープ復帰など）間は、ゲームも heartbeat を書けていない。
        # 復帰直後に誤って正常なゲームを終了させないよう、判定をいったん休む。
        $tick = [DateTime]::UtcNow
        if (($tick - $lastTick).TotalSeconds -gt 10) {
            $graceUntil = $tick.AddSeconds($hangSeconds)
            Write-WatchdogLog ('watchdog resumed after {0}s; hang check paused' -f [int]($tick - $lastTick).TotalSeconds)
        }
        $lastTick = $tick
        if ($tick -lt $graceUntil) { continue }
        # STOP はゲームを強制終了しないという従来の操作を維持する。
        if ((Test-Path -LiteralPath $env:STOP_FILE) -or (Test-Path -LiteralPath $commonStop)) { continue }
        if ($hangSeconds -eq 0) { continue }
        # 旧 build / 書込み失敗は heartbeat が無いので強制終了しない。
        $file = Get-Item -LiteralPath $heartbeat -ErrorAction SilentlyContinue
        if ($null -ne $file) {
            $seen = $true
            $lastWrite = $file.LastWriteTimeUtc
        }
        if ($seen) {
            $age = ([DateTime]::UtcNow - $lastWrite).TotalSeconds
            if ($age -ge $hangSeconds) {
                Write-WatchdogLog ('hang detected pid={0} heartbeat-age={1}' -f $game.Id, [int]$age)
                $game.Kill()
                $hung = $true
                $game.WaitForExit()
                break
            }
        }
    }
    $game.WaitForExit()
    $code = $game.ExitCode
    if ($hung) { $code = 1 }
    exit $code
} catch {
    Write-Host ('ゲームの起動 / 監視に失敗しました: ' + $_.Exception.Message)
    # 監視だけ失敗して Player が残ったときは再起動して二重起動しない。
    if ($null -ne $game) {
        $game.WaitForExit()
        exit $game.ExitCode
    }
    exit 1
} finally {
    if ($null -ne $game) { $game.Dispose() }
    Remove-Item -LiteralPath $heartbeat -Force -ErrorAction SilentlyContinue
}
