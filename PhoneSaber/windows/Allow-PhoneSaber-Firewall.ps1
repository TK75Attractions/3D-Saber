[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# 当日用の信頼できるネットワークだけに、座標・探索の UDP 受信を許可します。
Write-Host 'PhoneSaber: Private profile の UDP 5005 (RED), 5006 (BLUE), 5007 (探索) を許可します。'
Write-Host '管理者権限が必要です。Public profile は許可せず、再実行しても規則は増えません。'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'PowerShell を「管理者として実行」で開いて、このスクリプトを再実行してください。'
}

foreach ($port in @(5005, 5006, 5007)) {
    $ruleName = "PhoneSaber-Event-UDP-$port"
    $settings = @{
        PolicyStore = 'PersistentStore'
        Name = $ruleName
        DisplayName = "PhoneSaber Event UDP $port (Private)"
        Description = 'PhoneSaber 当日用: RED 5005 / BLUE 5006 / Android 探索 5007。Private のみ。'
        Direction = 'Inbound'
        Action = 'Allow'
        Enabled = 'True'
        Profile = 'Private'
        Protocol = 'UDP'
        LocalPort = $port
        RemotePort = 'Any'
        LocalAddress = 'Any'
        RemoteAddress = 'Any'
        Program = 'Any'
        Service = 'Any'
        InterfaceType = 'Any'
    }
    # 固定名で作成・更新し、前回の規則を有効な Private 限定設定に戻します。
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

Write-Host '完了。接続先 Wi-Fi / ホットスポットがプライベートであることを確認してゲームを起動してください。'
