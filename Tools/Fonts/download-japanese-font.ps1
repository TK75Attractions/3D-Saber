$ErrorActionPreference = 'Stop'
# 同梱Makinasを復元する場合に、作者の配布元から取得して内容を検証する。
$fontProject = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$fontDestination = Join-Path $fontProject 'Assets/Resources/Fonts/Makinas-4-Square.otf'
$fontArchive = Join-Path ([IO.Path]::GetTempPath()) ('3D-Saber-Makinas-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Invoke-WebRequest -Uri 'https://moji-waku.com/download/makinas4.zip' -OutFile $fontArchive
    $fontZipHash = (Get-FileHash -LiteralPath $fontArchive -Algorithm SHA256).Hash
    if ($fontZipHash -ne 'FD6DAF1512A6E931E9948A776FD135E50E5FFB87630F55F3C92F5205D911AF8E') {
        throw '作者の配布ファイルが更新されています。内容と使用許諾を確認してからハッシュを更新してください。'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fontZip = [IO.Compression.ZipFile]::OpenRead($fontArchive)
    try {
        $fontEntry = $fontZip.GetEntry('makinas4/Makinas-4-Square.otf')
        if ($null -eq $fontEntry) { throw 'マキナス 4 Square がアーカイブ内にありません。' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($fontEntry, $fontDestination, $true)
    } finally { $fontZip.Dispose() }
    if ((Get-FileHash -LiteralPath $fontDestination -Algorithm SHA256).Hash -ne '4409D6477708372B857CC582A2DA2B8E9F9D02B8E9DFC607E17219E9C715A4E5') {
        throw 'フォントの内容検証に失敗しました。'
    }
    Write-Output 'マキナス 4 Square を取得しました。Unityで再読み込みすると全画面の日本語へ適用されます。'
} finally {
    if (Test-Path -LiteralPath $fontArchive) { Remove-Item -LiteralPath $fontArchive -Force }
}
