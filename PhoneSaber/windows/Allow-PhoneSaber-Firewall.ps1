[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# 座標・探索の UDP 受信を、同じネットワーク（ローカルサブネット）の端末からだけ許可します。
# Windows のモバイルホットスポットは Public 扱いになることがあるため（2026-10-07 に確認）、
# Private と Public の両方で許可し、送信元をローカルサブネットに限定します。
# 5017（台同士のランキング同期 RankingSync）と 5018（診断の共有 PhoneSaberPeerLog）も相手の台から受けます。
# 初回の許可ダイアログを閉じて Block 規則だけが残った PC では、座標は届くのに同期だけ止まっていました（2026-10-30）。
Write-Host 'PhoneSaber: UDP 5005 (RED), 5006 (BLUE), 5007 (探索), 5017 (ランキング同期), 5018 (診断共有)、TCP 8765 (クリップ中継) を、同じネットワークの端末から受信できるようにします。'
Write-Host '管理者権限が必要です。Private / Public（ホットスポット）で有効、送信元は LocalSubnet のみ。再実行しても規則は増えません。'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'PowerShell を「管理者として実行」で開いて、このスクリプトを再実行してください。'
}

# USBインターネット共有のiPhoneから自動クリップも受け、LANの台Aへ中継する。
$rules = @()
foreach ($udpPort in @(5005, 5006, 5007, 5017, 5018)) { $rules += @{ Protocol = 'UDP'; Port = $udpPort } }
$rules += @{ Protocol = 'TCP'; Port = 8765 }
foreach ($rule in $rules) {
    $port = $rule.Port
    $protocol = $rule.Protocol
    $ruleName = "PhoneSaber-Event-$protocol-$port"
    $settings = @{
        PolicyStore = 'PersistentStore'
        Name = $ruleName
        DisplayName = "PhoneSaber Event $protocol $port (LocalSubnet)"
        Description = 'PhoneSaber 当日用: RED 5005 / BLUE 5006 / Android 探索 5007 / ランキング同期 5017 / 診断共有 5018 / クリップ中継 TCP 8765。Private/Public、送信元は LocalSubnet のみ。'
        Direction = 'Inbound'
        Action = 'Allow'
        Enabled = 'True'
        Profile = 'Private,Public'
        Protocol = $protocol
        LocalPort = $port
        RemotePort = 'Any'
        LocalAddress = 'Any'
        RemoteAddress = 'LocalSubnet'
        Program = 'Any'
        Service = 'Any'
        InterfaceType = 'Any'
    }
    # 固定名で作成・更新し、前回の規則をこの設定に揃えます。
    $existing = Get-NetFirewallRule -PolicyStore PersistentStore -Name $ruleName -ErrorAction SilentlyContinue
    if ($null -eq $existing) {
        New-NetFirewallRule @settings | Out-Null
        Write-Host "作成: $ruleName"
    } else {
        # 更新時は DisplayName が検索条件になるので、変更用の名前に置き換えます。
        $settings.NewDisplayName = $settings.DisplayName
        [void]$settings.Remove('DisplayName')
        Set-NetFirewallRule @settings | Out-Null
        Write-Host "更新: $ruleName"
    }
}

# 初回の「アクセスを許可しますか」を閉じる・キャンセルすると、Windows が Unity 用の受信 Block 規則を作る。
# Block は Allow より優先されるため、上の許可があっても受信できない。Unity の受信 Block 規則だけ無効にする。
# ビルド済み Player の規則の表示名は exe の説明（productName = ServTechSlash）になり 'Unity' を含まないため、
# 規則の対象プログラム名（Editor の Unity.exe / Player の 3D-Saber.exe）でも判定する。
$programNames = @('Unity.exe', '3D-Saber.exe')
$blocked = @(Get-NetFirewallRule -Direction Inbound -Action Block -ErrorAction SilentlyContinue |
    Where-Object {
        if ($_.Enabled -ne 'True') { return $false }
        if ($_.DisplayName -match 'Unity|ServTechSlash') { return $true }
        $program = [string](($_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue).Program)
        return $programNames -contains (($program -split '\\')[-1])
    })
foreach ($rule in $blocked) {
    # グループポリシー由来などで無効化できない規則があっても、残りの処理は続ける。
    try {
        Disable-NetFirewallRule -Name $rule.Name -ErrorAction Stop
        Write-Host "無効化: Unity の受信ブロック規則 '$($rule.DisplayName)' ($($rule.Profile))"
    } catch {
        Write-Warning "無効化できませんでした: '$($rule.DisplayName)' ($($rule.Profile)): $($_.Exception.Message)"
    }
}
if ($blocked.Count -eq 0) { Write-Host 'Unity の受信ブロック規則はありません。' }

Write-Host '完了。スマホと同じ Wi-Fi / ホットスポットにつないでゲームを起動してください。'
