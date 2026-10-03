param(
    [Parameter(Mandatory=$true)][string]$Blender,
    [Parameter(Mandatory=$true)][string]$FFmpeg,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference='Stop'
$repository=Split-Path (Split-Path $PSScriptRoot)
$source=Join-Path $repository 'Tools/SongSelectHuman/RightHandHuman.blend'
$output=[IO.Path]::GetFullPath($OutputDirectory)
$frames=Join-Path $output 'Poses'
New-Item -ItemType Directory -Path $frames -Force | Out-Null
& $Blender --background --factory-startup --python (Join-Path $PSScriptRoot 'render_frames.py') -- $source $frames
if($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $frames 'pose-23.png'))) { throw '人体模型の書き出しに失敗しました。' }
& $FFmpeg -hide_banner -loglevel error -y -framerate 24 -i (Join-Path $frames 'pose-%02d.png') -vf 'tile=6x4:nb_frames=24:padding=0:margin=0' -frames:v 1 -update 1 (Join-Path $output 'RightHandGuide.png')
if($LASTEXITCODE -ne 0) { throw 'アトラスの作成に失敗しました。' }
Write-Output (Join-Path $output 'RightHandGuide.png')
