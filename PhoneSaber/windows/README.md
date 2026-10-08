# Windows 当日用キット (台 A / B)

Windows + Android を基本構成にします。Windows + iPhone は iPhone の「手動IP」に PC の IPv4 アドレスを指定してください (Windows では Bonjour / Apple P2P bridge は動きません)。Mac + iPhone の手順は [当日 runbook](../docs/claude/EVENT_DAY_RUNBOOK.md) を参照してください。

## 1. 最初の準備とビルド

1. Git for Windows と Git LFS、Unity Hub をインストールします。PowerShell で以下を実行します。

   ```powershell
   git lfs install
   git clone https://github.com/TK75Attractions/3D-Saber.git
   cd 3D-Saber
   git lfs pull
   ```

2. Unity Hub で **6000.3.9f1** と Windows Build Support (使用するバックエンドに対応したモジュール) を入れます。`Assets/`、`Packages/`、`ProjectSettings/` が並ぶ **repo root** を Unity project として開きます。`PhoneSaber/` はスマホアプリ・ツールの置き場です。
3. Unity の **Tools > PhoneSaber > Build > Windows Player**（Windows Build Support が必要）、または **File > Build Profiles** で Windows の profile を選択・有効化し、既存の Scene List を使って **Build** します。保存先の例は `Builds/Windows/3D-Saber.exe`。exe、`*_Data`、UnityPlayer.dll など出力一式を同じフォルダに保ちます。Overlay は通常ビルドにも入るので Development Build は不要です。

## 2. 台 A / B の設定と起動

- **Editor**: Play 前に **Tools > PhoneSaber > Station > A** または **B** を選びます。設定は保存され、次の Play に適用されます。
- **ビルド済み Player**: `Start-Saber-A.bat` / `Start-Saber-B.bat` を使います。それぞれ先頭の `GAME_EXE` を実際の exe のパスに合わせてください。既定は、このキットから相対指定した `../../Builds/Windows/3D-Saber.exe` です。空白・日本語を含むパスも引用符付きで起動します。
- bat はゲーム終了まで待機し、**終了コード0なら監視終了、非0なら5秒後に自動再起動**します。ウィンドウに再起動回数を表示し、同じフォルダの `Start-Saber-A.log` / `B.log` に時刻・終了コード・回数を追記します。正常終了はゲームの Quit。監視停止は **Ctrl+C → バッチ終了に Y**、または空の `Start-Saber-A.STOP` / `B.STOP` (両台なら `STOP`) を作成します。STOP は現在のゲームを強制終了せず、終了後の再起動を止めます。次回起動前に削除してください。
- bat は `-phonesaberStation A` / `B` を渡します。設定の優先順は **起動引数 → 環境変数 `PHONESABER_STATION` → PlayerPrefs** です。
- Android / iPhone の接続設定の **「台」** も同じ **A / B** にして送信を開始します。手動 IP は台の自動探索より優先されるため、必ず該当 PC の IP を使います。台設定は UDP の受信を拒否する機能ではありません。
- **Editor とビルド済み Player を同時起動しないでください**。同じ PC 上で UDP 5005 / 5006 が競合します。

## 3. ファイアウォール (最初に1回・管理者)

信頼できる当日用 Wi-Fi / ホットスポットを Windows の **プライベート ネットワーク** に設定します。
**管理者として PowerShell を起動**し、repo root から実行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\PhoneSaber\windows\Allow-PhoneSaber-Firewall.ps1
```

この実行だけ PowerShell の実行制限を回避します。スクリプトは PhoneSaber 専用の受信規則を作成し、**UDP 5005 (RED) / 5006 (BLUE) / 5007 (Android 探索)** を、**Private と Public の両方・送信元はローカルサブネットのみ**で許可します。Windows のモバイルホットスポットは Public 扱いになることがあるためです（2026-10-07 に確認）。固定名の同じ規則を更新するので再実行しても増えません。初回の許可ダイアログを閉じたときに Windows が作る **Unity の受信ブロック規則も無効化**します（Block は Allow より優先されるため）。規則は exe 限定ではなく該当ポートへの許可なので、Editor と Player の両方で使えます。

削除する場合は管理者 PowerShell で以下を実行します。

```powershell
Get-NetFirewallRule -Name 'PhoneSaber-Event-UDP-500*' | Remove-NetFirewallRule
```

## 4. 当日の接続とホットスポット予備経路

1. その PC の台に対応する bat を起動 (Editor なら台設定後に Play)。
2. スマホと PC を同じ Wi-Fi に接続。Android で「台」を合わせて「開始」。Windows + iPhone は `ipconfig` の Wi-Fi 側 **IPv4 アドレス**を「手動IP」に入力。
3. 自動探索が失敗したら Android でも PC の IP を入力して「手入力を保存」。
4. 学校 Wi-Fi が端末間通信を禁止している場合は Windows の **設定 > ネットワークとインターネット > モバイル ホットスポット**で Wi-Fi の共有を ON にし、スマホをその SSID に接続します。上のスクリプトを実行済みなら Public 扱いのままで受信できます。ホットスポット側の PC の IPv4 は通常 **192.168.137.1** です（`ipconfig` で確認）。必要なら新しい IP を手動入力します。A / B の SSID は区別できる名前にします。

## 5. 受信 Overlay (Windows / Mac 共通)

ゲーム画面 (Editor は Game view) にフォーカスし **F8** を押すと運営表示が開き、もう一度で閉じます。Mac のキーボード設定によっては **Fn + F8**。起動時は非表示で、シーン遷移後も表示を維持します。F8 は既存のゲーム操作に割り当てがないキーです。

- 台名、RED 5005 / BLUE 5006 の **直近1秒の packet 数**、最後の受信からの秒数、最後の送信元 IP、経路、最後の payload の解析 **OK / NG** を表示します。NG も packet 数と最終受信時刻には含まれます。初回受信前は開始からの経過を表示します。
- **1秒を超えて datagram が届かない色**には日本語の警告が出ます。両色の数字と送信元 IP を確認してから剣を振り、ゲーム内の動作も確認します。packet の受信・解析 OK は認識品質の保証ではありません。
- Android の **探索 UDP 5007**、Bonjour、P2P bridge、各色の UDP 受信機の ON / 停止も表示します。Windows の Bonjour / P2P は **未対応**。Mac の送信元 `127.0.0.1` は **P2P bridge**、それ以外は **LAN** と表示します (IP による経路の目安です)。
- 数字が 0 のままなら、スマホの送信開始、台・手動 IP、同じネットワーク、ファイアウォールスクリプトの実行（Unity の Block 規則が残っていないか）を確認します。受信機が停止なら、もう一つの Unity / Player がポートを使っていないか確認します。

F8 にイベントログのパスも表示します。`Application.persistentDataPath/PhoneSaber/events.log` と4世代、各1 MiBまでに受信機の開始・停止・再試行、1秒超の途絶と復帰、送信元・経路・台名を記録します。ディスクへの書込は main thread だけで行い、書込失敗時も受信を続けます。ゲーム起動時にバックグラウンド実行とスリープ防止を設定します。

## 6. 遅延テスト（F9、Windows / Mac 共通）

ゲーム画面で **F9** を押すと画面が黒くなり、赤い棒が左右に交互に出ます。スマホのカメラをこの画面に向け、赤い棒だけが映るように置くと、棒が切り替わってから赤の受信位置が切り替わるまでの時間を測ります。画面下に **画面→受信 の中央値 / p95** が出ます。もう一度 F9 で終了し、結果をイベントログに残します。モニタ表示・カメラ・認識・Wi-Fi・受信を含むので、プレイヤーが感じる遅延にほぼ等しく、iPhone / Android・Mac / Windows を同じ方法で比べられます。スマホの画面の「撮影→送信」は、このうちスマホ内部の分です。

会場に持ち込む前に、実際の Windows PC + スマホで両色の受信・探索・ホットスポットを確認してください。
