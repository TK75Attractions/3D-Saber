param(
    [ValidateSet('Lock', 'Run')][string]$Mode,
    [ValidateSet('None', 'Latency', 'Tracking')][string]$Probe = 'None',
    [ValidateRange(1, 600)][int]$ProbeCount = 8
)
$ErrorActionPreference = 'Stop'
if (($Probe -eq 'Latency' -and $ProbeCount -gt 8) -or ($Probe -eq 'Tracking' -and $ProbeCount -lt 10)) {
    throw 'ProbeCount が範囲外です。Latency: 1..8 / Tracking: 10..600'
}

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
        if ($Probe -ne 'None') {
            $commonStop = Join-Path ([IO.Path]::GetDirectoryName($env:STOP_FILE)) 'STOP'
            if ((Test-Path -LiteralPath $env:STOP_FILE) -or (Test-Path -LiteralPath $commonStop)) {
                Write-WatchdogLog 'not-started STOP-file'
                exit 1
            }
            # 計測も通常起動と同じ lock を保持。スマホは手動で送信開始した状態を使う。
            $info.FileName = (Get-Process -Id $PID).Path
            $info.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Mode Run -Probe ' + $Probe + ' -ProbeCount ' + $ProbeCount
        } else {
            $info.Arguments = '/d /s /c ""%PHONESABER_BATCH%" --locked"'
        }
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

# 読み取りだけ。電源プラン・Game Mode・出力のHzを毎起動ログに残す。
function Write-PresentationDiagnostics {
    $env:PHONESABER_LOW_POWER_MODE = 'unknown'
    try {
        $plan = (& powercfg /getactivescheme 2>&1 | Out-String).Trim()
        Write-WatchdogLog ('power-plan=' + $plan)
        Write-Host ('電源プラン: ' + $plan + '。AC電源で使用してください。')
        $bar = Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\GameBar' -ErrorAction SilentlyContinue
        Write-WatchdogLog ('game-mode-auto=' + $bar.AutoGameModeEnabled)
        Write-Host 'プロジェクターの出力は60 Hz以上・ゲームモード推奨。設定 > システム > ディスプレイ > ディスプレイの詳細設定で出力先を確認してください。'
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PhoneSaberDisplayRead {
    [StructLayout(LayoutKind.Explicit, Size=220)]
    public struct Mode {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
        [FieldOffset(184)] public uint Hz;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Power {
        public byte AC, Battery, Percent, Saver;
        public uint Life, FullLife;
    }
    [DllImport("user32.dll", CharSet=CharSet.Unicode)]
    public static extern bool EnumDisplaySettings(string name, int number, ref Mode mode);
    [DllImport("kernel32.dll")]
    public static extern bool GetSystemPowerStatus(out Power power);
}
'@
        $power = New-Object PhoneSaberDisplayRead+Power
        if ([PhoneSaberDisplayRead]::GetSystemPowerStatus([ref]$power)) {
            $env:PHONESABER_LOW_POWER_MODE = [string]$power.Saver
            Write-WatchdogLog ('power ac={0} saver={1} battery-percent={2}' -f $power.AC, $power.Saver, $power.Percent)
            if ($power.Saver -eq 1 -or $power.AC -eq 0) {
                Write-Host '注意: 省電力またはバッテリー駆動です。AC電源をつなぎ、省電力を解除してから起動し直してください。'
            }
        }
        foreach ($screen in [System.Windows.Forms.Screen]::AllScreens) {
            $mode = New-Object PhoneSaberDisplayRead+Mode
            $mode.Size = 220
            if ([PhoneSaberDisplayRead]::EnumDisplaySettings($screen.DeviceName, -1, [ref]$mode)) {
                $detail = 'display={0} primary={1} pixels={2}x{3} hz={4} logical-bounds={5}' -f $screen.DeviceName, $screen.Primary, $mode.Width, $mode.Height, $mode.Hz, $screen.Bounds
                Write-WatchdogLog $detail
                Write-Host ('画面: {0} {1}x{2} / {3} Hz' -f $screen.DeviceName, $mode.Width, $mode.Height, $mode.Hz)
                if ($mode.Hz -gt 1 -and $mode.Hz -lt 59) {
                    Write-Host '注意: 60 Hz未満の出力があります。プロジェクター側の出力設定を確認してください。'
                }
            }
        }
        Write-Host '表示倍率と解像度は自動変更しません。ウィンドウを重ねず全画面で使用し、F8のPC描画判定も確認してください。'
    } catch {
        Write-WatchdogLog ('presentation-check-unavailable=' + $_.Exception.Message)
        Write-Host '画面・電源の自動確認ができませんでした。F8のPC描画判定とWindowsのディスプレイ設定を確認してください。'
    }
}

Write-PresentationDiagnostics

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
    if ($Probe -ne 'None') {
        $argument = if ($Probe -eq 'Latency') { '-phonesaberLatencyProbe' } else { '-phonesaberTrackingProbe' }
        $info.Arguments += ' ' + $argument + ' ' + $ProbeCount
        if ($env:PHONESABER_PROBE_LOG) { $info.Arguments += ' -logFile "' + $env:PHONESABER_PROBE_LOG + '"' }
    }
    # 電源・画面確認中に置かれた STOP も、通常起動・計測の両方で起動直前に確認する。
    $commonStop = Join-Path ([IO.Path]::GetDirectoryName($env:STOP_FILE)) 'STOP'
    if ((Test-Path -LiteralPath $env:STOP_FILE) -or (Test-Path -LiteralPath $commonStop)) {
        Write-WatchdogLog 'not-started STOP-file'
        exit 0
    }
    # Start が返す実際の Player の handle で監視する。名前で他の process を kill しない。
    $game = [Diagnostics.Process]::Start($info)
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
        # 作成後の最初の書込みが失敗した0 byteファイルでは、監視を有効にしない。
        if ($null -ne $file -and $file.Length -eq 1) {
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
