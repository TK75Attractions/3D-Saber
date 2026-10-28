[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# 台同士のランキング同期(UDP 5017)と、スマホから譜面に印を付ける操作盤(TCP 5020〜5029)の受信を、
# 同じネットワーク(ローカルサブネット)の端末からだけ許可します(2026-10-28)。
# PhoneSaber の受信(UDP 5005〜5007)は PhoneSaber/windows/Allow-PhoneSaber-Firewall.ps1 で別に許可します。
# Windows のモバイルホットスポットは Public 扱いになることがあるので、Private と Public の両方で許可します。
Write-Host 'ゲーム: UDP 5017(ランキング同期)と TCP 5020〜5029(スマホで譜面に印)を、同じネットワークの端末から受信できるようにします。'
Write-Host '管理者権限が必要です。Private / Public(ホットスポット)で有効、送信元は LocalSubnet のみ。再実行しても規則は増えません。'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'PowerShell を「管理者として実行」で開いて、このスクリプトを再実行してください。'
}

$rules = @(
    @{ Name = 'BeatTraceSlash-UDP-5017'; Port = '5017'; Protocol = 'UDP'; What = 'ランキング同期' },
    # 5020 が使えないときは 5021〜5029 で待ち受けます。名前は前の版のまま(再実行で範囲を広げます)。
    @{ Name = 'BeatTraceSlash-TCP-5020'; Port = '5020-5029'; Protocol = 'TCP'; What = 'スマホで譜面に印' }
)
foreach ($rule in $rules) {
    $ruleName = $rule.Name
    $settings = @{
        PolicyStore = 'PersistentStore'
        Name = $ruleName
        DisplayName = "Beat Trace Slash $($rule.Protocol) $($rule.Port) $($rule.What) (LocalSubnet)"
        Description = "台同士のランキング同期 UDP 5017 / スマホの印 TCP 5020〜5029。Private/Public、送信元は LocalSubnet のみ。"
        Direction = 'Inbound'
        Action = 'Allow'
        Enabled = 'True'
        Profile = 'Private,Public'
        Protocol = $rule.Protocol
        LocalPort = $rule.Port
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

Write-Host '完了。ゲームの受信を Windows がブロックしているときは、PhoneSaber/windows/Allow-PhoneSaber-Firewall.ps1 も実行してください(Unity の受信ブロック規則を無効にします)。'
