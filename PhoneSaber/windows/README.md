# Windows 当日用キット (台 A / B)

台 A / B の送信端末は iPhone を使用します。Windows + iPhone は iPhone の「手動IP」に PC の IPv4 アドレスを指定してください (Windows では Bonjour / Apple P2P bridge は動きません)。Mac + iPhone の手順は [当日 runbook](../docs/claude/EVENT_DAY_RUNBOOK.md) を参照してください。

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
- bat はゲーム終了まで待機し、**終了コード0なら監視終了、非0なら5秒後に自動再起動**します。ウィンドウに再起動回数を表示し、同じフォルダの `Start-Saber-A.log` / `B.log` に時刻・終了コード・回数を追記します。正常終了はゲームの Quit。監視停止は **Ctrl+C → バッチ終了に Y**、または空の `Start-Saber-A.STOP` / `B.STOP` (両台なら `STOP`) を作成します。STOP は現在のゲームを強制終了せず、終了後の再起動を止めます (再起動待ちの5秒間も1秒ごとに確認)。次回起動前に削除してください。STOP が残ったまま bat を起動すると「停止ファイルがあるため起動しません:」とパスを表示して止まります。表示されたSTOPを削除してから起動してください。ゲーム実行中は Ctrl+C の確認がゲーム終了まで出ないことがあります。すぐ監視だけ止めたいときは bat のコンソールウィンドウを閉じます (ゲームは終了しません)。Ctrl+C やウィンドウを閉じた停止はログに残りません。
- 二重起動防止: A/B 共通の OS 排他ロックを監視中と再起動待ちの間保持し、同じ PC の別監視を拒否します。`3D-Saber.exe` が既に動いている場合も日本語の案内と Enter 待ちで起動を止めます。OS が生存確認を行うため、前回の異常終了 / 電源断で残った `%TEMP%\phonesaber-watchdog.lock` は次の起動を妨げません。`saber-watchdog.ps1` も bat と同じフォルダに保ちます。PowerShell は `-NoProfile` で既存コンソールを使い、追加の監視ウィンドウは開きません。
- ハング監視: run ごとの `-phonesaberHeartbeat <一時ファイル>` を Player の main thread が約1秒ごとに更新します。一度確認後、45秒以上停止するとログに `hang detected` を残して今回の Player を強制終了し、終了コード1として通常の5秒後の再起動へ進みます。初回更新が無い旧 build は強制終了しません。数秒のシーン読込みに余裕を取った45秒ですが、会場用 build で実測してください。bat の先頭で `set "PHONESABER_HANG_SECONDS=90"` とすると90秒、`0` で無効化できます（または起動元の環境変数で指定）。STOP がある間はゲームを強制終了しません。固まった場合は既定設定なら45秒検知＋5秒待機の後に再起動開始（起動時間は別）。60秒で戻らなければ旧build・STOP・監視停止／無効化を技術担当が確認します。正常 Quit の終了コード0 / 非0の再起動は従来どおりです。**今回の C# を含む Player の再ビルドが必要です**。
- 画面オフ・セッションロック中、または蓋閉で有効な外部画面がない間は、heartbeat の古さによるハング判定を休止します。休止ログ `hang-check paused <理由>` は連続した休止につき1回、復帰ログ `hang-check resumed ... grace=45s` の後は設定秒数の猶予を取り直します。その後も heartbeat が止まっていれば通常どおり再起動します。暗くなっただけの画面（dim）や、蓋閉でも外部画面を使用中なら監視を続けます。管理者権限は不要です。画面/蓋は Windows の `PowerSettingRegisterNotification`（`GUID_SESSION_DISPLAY_STATUS` / `GUID_LIDSWITCH_STATE_CHANGE`）、ロックは同じセッションの `LogonUI`、外部画面は `Get-CimInstance root/wmi WmiMonitorConnectionParams` で確認します。WMI の `Active` は画面電源状態としては使いません。API 未対応・取得失敗・状態不明では、その検知を根拠に休止せず従来の監視を続けます。休止中から不明になった場合も一度だけ猶予を取り直します。[通知 API](https://learn.microsoft.com/en-us/windows/win32/api/powersetting/nf-powersetting-powersettingregisternotification) / [状態値](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids)。
- bat は `-phonesaberStation A` / `B` を渡します。設定の優先順は **起動引数 → 環境変数 `PHONESABER_STATION` → PlayerPrefs** です。
- iPhone の接続設定の **「台」** も同じ **A / B** にします。Windows＋iPhoneは手動IP必須です。台を変更しても手動IPは残るため、必ず該当 PC の現在の IPv4 アドレスへ更新してから送信を開始します。台設定は UDP の受信を拒否する機能ではありません。
- **Editor とビルド済み Player を同時起動しないでください**。同じ PC 上で UDP 5005 / 5006 が競合します。

開場チェックのheartbeat更新確認は、batでゲームを起動したままPowerShellで次を2秒あけて2回実行します。現在時刻に近いファイルの `LastWriteTimeUtc` が進むことを確認します。ファイルが無い／時刻が止まる場合は旧build・書込失敗を確認し、監視による自動復旧を前提に開場しないでください。

```powershell
Get-ChildItem -Path "$env:TEMP\phonesaber-heartbeat-*" | Select-Object FullName, LastWriteTimeUtc
```

## 3. ファイアウォール (最初に1回・管理者)

信頼できる当日用 Wi-Fi / ホットスポットを使います。下の許可スクリプトはPrivate／Publicの両方に対応するため、**Public扱いのホットスポットをPrivateへ変更する必要はありません**。
**管理者として PowerShell を起動**し、repo root から実行します。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\PhoneSaber\windows\Allow-PhoneSaber-Firewall.ps1
```

この実行だけ PowerShell の実行制限を回避します。スクリプトは PhoneSaber 専用の受信規則を作成し、**UDP 5005 (RED) / 5006 (BLUE) / 5007 (Android 探索) / 5017 (台同士のランキング同期) / 5018 (診断の共有)** を、**Private と Public の両方・送信元はローカルサブネットのみ**で許可します。Windows のモバイルホットスポットは Public 扱いになることがあるためです（2026-10-07 に確認）。固定名の同じ規則を更新するので再実行しても増えません。初回の許可ダイアログを閉じたとき、またはチェックしなかったネットワーク (例: パブリック) 向けに Windows が作る **Unity の受信ブロック規則も無効化**します（Block は Allow より優先されるため）。対象は表示名に `Unity` / `ServTechSlash` (Player の productName) を含む規則と、対象プログラムが `Unity.exe` / `3D-Saber.exe` の規則です。規則は exe 限定ではなく該当ポートへの許可なので、Editor と Player の両方で使えます。

ブロック規則はダイアログに答えた時点で作られるため、**ビルド済み Player (と Editor) を一度起動してダイアログに答えた後に**このスクリプトを実行します。ダイアログではプライベートとパブリックの両方にチェックして許可するのが確実です。exe を別の場所に作り直した・別名にしたなどで新しいダイアログに答えたときは、スクリプトを再実行します。

削除する場合は管理者 PowerShell で以下を実行します。

```powershell
Get-NetFirewallRule -Name 'PhoneSaber-Event-UDP-50*' | Remove-NetFirewallRule
```

## 4. 当日の接続とホットスポット予備経路

1. その PC の台に対応する bat を起動 (Editor なら台設定後に Play)。
2. iPhone と PC を同じ Wi-Fi に接続。「台」を合わせ、`ipconfig` の Wi-Fi 側 **IPv4 アドレス**を「手動IP」に入力して送信を開始。
3. 学校 Wi-Fi が端末間通信を禁止している場合は Windows の **設定 > ネットワークとインターネット > モバイル ホットスポット**で Wi-Fi の共有を ON にし、スマホをその SSID に接続します。上のスクリプトを実行済みなら Public 扱いのままで受信できます。ホットスポット側の PC の IPv4 は通常 **192.168.137.1** です（`ipconfig` で確認）。必要なら新しい IP を手動入力します。A / B の SSID は区別できる名前にします。

## 5. 受信 Overlay (Windows / Mac 共通)

ゲーム画面 (Editor は Game view) にフォーカスし **F8** を押すと運営表示が開き、もう一度で閉じます。Mac のキーボード設定によっては **Fn + F8**。起動時は非表示で、シーン遷移後も表示を維持します。F8 は既存のゲーム操作に割り当てがないキーです。

- 台名、RED 5005 / BLUE 5006 の **直近1秒の packet 数**、最後の受信からの秒数、最後の送信元 IP、経路、最後の「payload解析: OK」/「payload解析: NG」 を表示します。NG も packet 数と最終受信時刻には含まれます。初回受信前は開始からの経過を表示します。
- **1秒を超えて datagram が届かない色**には日本語の警告が出ます。両色の数字と送信元 IP を確認してから剣を振り、ゲーム内の動作も確認します。packet の受信・解析 OK は認識品質の保証ではありません。
- Android の **探索 UDP 5007**、Bonjour、P2P bridge、各色の UDP 受信機の ON / 停止も表示します。Windows の Bonjour / P2P は **未対応**。Mac の送信元 `127.0.0.1` は **P2P bridge**、それ以外は **LAN** と表示します (IP による経路の目安です)。
- 数字が 0 のままなら、スマホの送信開始、台・手動 IP、同じネットワーク、ファイアウォールスクリプトの実行（Unity の Block 規則が残っていないか）を確認します。受信機が停止なら、もう一つの Unity / Player がポートを使っていないか確認します。

- **送信元競合**: 現行F8には専用の競合警告はありません。同じ色の最後の送信元IPが交互に変わる場合は、他のスマホ・練習送信を止め、本番スマホ1台だけで確認します。MacのP2P経由は複数端末も同じ `127.0.0.1` に見えるので、IPだけでは競合を否定できません。
- **F7** は4隅の位置補正、**F4** はプロジェクターモードの切替（既定ON・保存）。詳細は[当日runbook](../docs/claude/EVENT_DAY_RUNBOOK.md)。

F8 にイベントログのパスも表示します。`Application.persistentDataPath/PhoneSaber/events.log` (Windows Player では通常 `%USERPROFILE%\AppData\LocalLow\DefaultCompany\ServTechSlash\PhoneSaber\events.log`、Unity の `Player.log` はその1つ上) と4世代、各1 MiBまでに受信機の開始・停止・再試行、1秒超の途絶と復帰、送信元・経路・台名を記録します。ディスクへの書込は main thread だけで行い、書込失敗時も受信を続けます。ゲーム起動時にバックグラウンド実行とスリープ防止を設定します。

## 6. 遅延テスト（F9、Windows / Mac 共通）

ゲーム画面で **F9** を押すと画面が黒くなり、赤い棒が左右に交互に出ます。スマホのカメラをこの画面に向け、赤い棒だけが映るように置くと、棒が切り替わってから赤の受信位置が切り替わるまでの時間を測ります。画面下に **画面→受信 の中央値 / p95** が出ます。「比較用の記録 n/8 ブロック」を8ブロック（約2分、受信不足なら長くなる）まで待ち、もう一度 F9 で終了します。結果は20回ごと・ブロックごと・終了時にイベントログへ残ります。検査後はカメラをプレイヤー側へ戻し、箱を固定してF7の位置補正を確認します。モニタ表示・カメラ・認識・Wi-Fi・受信を含むので、プレイヤーが感じる遅延にほぼ等しく、iPhone / Android・Mac / Windows を同じ方法で比べられます。スマホの画面の「撮影→送信」は、このうちスマホ内部の分です。

会場に持ち込む前に、実際の Windows PC + スマホで両色の受信・探索・ホットスポットを確認してください。ゲーム側のスリープ防止が Windows で効くかは未確認のため、念のため Windows の電源設定で画面オフ・スリープを「なし」にし、AC 電源につないでおきます。

Windows 用の自動回帰テストと実機確認項目は [運用強化記録](../docs/claude/ops-hardening-20261011.md)。

zen の Windows 台 B 実機確認（通常ユーザーで実行）:

- repo root で `py -3 -B -m unittest discover -s PhoneSaber/tools -p test_ops_watchdog.py -v`。偽の画面・時計・Player による休止/復帰猶予と、Windows 専用の起動/再起動テストを確認します。
- iPhone の手動IPを台 B に合わせて送信し、画面オフ、Win+L、外部画面なしの蓋閉をそれぞれ45秒超維持。Player が再起動せず、休止ログが各休止につき1回になることを確認します。復帰後は猶予ログと heartbeat 更新を確認します。
- 外部画面を起こしたまま蓋を閉じた場合は、ロックされていなければ監視が続くことを確認します。画面オン/ロック解除後にテスト用 Player を意図的に停止させ、復帰猶予を満了した後の `hang detected` と再起動も確認します。STOP と `PHONESABER_HANG_SECONDS=0` の従来動作も確認してください。
- モニター本体の電源ボタンなど、Windows に消灯が通知されない機種/操作では検知できません。その場合は従来どおりハング判定されるため、会場機で上記ログを確認してください。この watchdog 変更だけなら Unity Player の再ビルドは不要です。
