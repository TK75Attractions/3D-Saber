param(
    [ValidateSet('A', 'B')][string]$Station = 'A',
    [string]$Unity = "$env:ProgramFiles\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe"
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Windows PC 専用です' }
if (Get-Process -Name Unity, '3D-Saber' -ErrorAction SilentlyContinue) { throw 'Unity / Player を終了してください' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
function Check-Exit { if ($LASTEXITCODE -ne 0) { throw "exit-code=$LASTEXITCODE" } }
Push-Location -LiteralPath $root
try {
    & "$PSScriptRoot\Allow-PhoneSaber-Firewall.ps1"
    py -3 -m pip install numpy
    Check-Exit
    py -3 -B -m unittest discover -s PhoneSaber/tools -p test_windows_station.py -v
    Check-Exit
    py -3 -B -m unittest discover -s PhoneSaber/tools -p test_ops_watchdog.py -v
    Check-Exit
    & $Unity -batchmode -nographics -quit -projectPath $root -executeMethod PhoneSaberPlayerBuild.BuildWindows -logFile "$PSScriptRoot\build-windows.log" | Out-Host
    Check-Exit
    if (-not (Test-Path -LiteralPath 'Builds\Windows\3D-Saber.exe')) { throw 'Player がありません' }
    Read-Host "Android: 台 $Station で送信開始、カメラを画面へ。初回FirewallはPrivate/Publicを許可。Enter" | Out-Null
    py -3 -B PhoneSaber/tools/latency_matrix.py --station $Station --only baseline --blocks 8
    $firstProbe = $LASTEXITCODE
    & "$PSScriptRoot\Allow-PhoneSaber-Firewall.ps1"
    if ($firstProbe -ne 0) {
        py -3 -B PhoneSaber/tools/latency_matrix.py --station $Station --only baseline --blocks 8
        Check-Exit
    }
    py -3 -B PhoneSaber/tools/player_probe.py Tracking --station $Station --count 66
    Check-Exit
    py -3 -B PhoneSaber/tools/tracking_report.py
    Check-Exit
} finally { Pop-Location }
