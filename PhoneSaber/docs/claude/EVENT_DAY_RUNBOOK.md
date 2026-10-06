# PhoneSaber 縁日当日の運用手順(runbook)

2026-10-06 のrepo統合: Git/Unity root は `3D-Saber/`、ツールは `PhoneSaber/`。Git root から `bash PhoneSaber/tools/verify_phone_saber.sh` を実行。旧 school-festival は履歴を保って統合・アーカイブ済み。

当日に「動かす」「おかしいときに直す」ための1枚です。調査の根拠は [FINDINGS.md](FINDINGS.md)、残作業は [STATUS.md](STATUS.md) にまとめています。
迷ったら、まず Mac のデスクトップの **`PhoneSaber Status.command`** をダブルクリックしてください(読み取りのみ。何も変えません)。

## 0. 端末の組み合わせと使い方(Mac / Windows / iPhone / Android)

PC(Mac か Windows)で Unity のゲームを動かし、スマホ(iPhone か Android)で剣を認識して座標を UDP 5005(赤)/ 5006(青)へ送る。
リポジトリは 3D-Saber の1つだけ(PhoneSaber/ はその中)。

### 最初に1回だけ
- **Mac**: `git lfs install` → `git clone https://github.com/TK75Attractions/3D-Saber.git` → `3D-Saber/PhoneSaber/setup_mac.command` をダブルクリック(デスクトップに PhoneSaber のアイコン)。Unity Hub で 3D-Saber を開く(6000.3.9f1)。
- **Windows**: Git LFS を入れて同じく clone → Unity Hub で開く(6000.3.9f1)。初めて Play したときのファイアウォールの画面で「プライベート ネットワーク」を許可する(UDP 5005 / 5006 / 5007)。
- **iPhone**: Mac の Xcode で `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj` を開き、iPhone をつないで ▶。
- **Android(AQUOS sense9)**: 開発者向けオプションで USB デバッグを ON。Android Studio で `PhoneSaber/android` を開き、つないで ▶。

### 同じ Wi-Fi で 2 台(A / B)を並べるとき
- **各 PC の Unity Editor(Mac / Windows)**: Play 前に `Tools > PhoneSaber > Station > A` または `B` を選ぶ。PlayerPrefs の `PhoneSaber.Station` に保存され、次の Play から適用される。
- **ビルドしたゲーム**: 起動引数 `-phonesaberStation A`(もう一方は `B`)を付ける。Windows なら `Game.exe -phonesaberStation A`、Mac なら `open -a "/path/to/Game.app" --args -phonesaberStation A`。環境変数 `PHONESABER_STATION=A` でも指定できる。優先順は **起動引数 → 環境変数 → PlayerPrefs**。台名は16文字以内の英数字・`-`・`_`(通常は A / B)。Unity Console の探索/Bonjour/P2P 公開ログで台名を確認する。
- **各スマホ(iPhone / Android)**: アプリの接続設定の **「台」**を、その PC と同じ **A / B** にしてから送信を開始する。設定は保存される。指定した台の PC だけを自動探索する。iPhone の P2P と診断 relay も同じ台に限定される。
- **手動 IP は常に優先**。自動探索で見つからないときは、その台の PC の IP を確認して入力する(Windows + iPhone は従来どおり手動 IP が必要)。台設定は座標の受信を拒否する仕組みではなく、スマホの自動送信先を選ぶための設定。
- **1 台だけで従来どおり使うとき**: PC は `Station > None`、スマホは **指定なし**、起動引数/環境変数も未設定にする。どこにも台を設定しなければ従来と同じ動作。環境変数/起動引数を使った PC は、それを外してから None に戻す。

### 毎回
1. PC で Unity を開いて **Play**(Mac では P2P ブリッジと診断の受信側も自動で起動する)。
2. スマホと PC を同じ Wi-Fi につなぐ(Mac + iPhone は P2P で直接つながるので不要)。
3. スマホのアプリで送信を開始する。

| 組み合わせ | 開始の操作 | PC の見つけ方 | 診断録画 |
| --- | --- | --- | --- |
| Mac + iPhone(推奨) | 「通常送信を開始」 | 自動(P2P、なければ Wi-Fi の Bonjour) | ○ |
| Mac + Android | 「開始」 | 自動(UDP 5007 の探索 / NSD) | × |
| Windows + Android | 「開始」 | 自動(UDP 5007 の探索) | × |
| Windows + iPhone | 「通常送信を開始」 | **Windows の IP を「手動IP」に入力**(iPhone は Bonjour で探すが Windows は出していない) | × |

- Windows の IP は、コマンドプロンプトで `ipconfig` を実行し「IPv4 アドレス」を見る。
- Android で見つからないときは、PC の IP を入れて「手入力を保存」。
- 学校などの Wi-Fi で届かないとき(端末どうしの通信が禁止されている): PC のモバイルホットスポットを ON にして、スマホをそこにつなぐ。
- 診断(Debug Recording・ガイド付き録画・自動解析)は iPhone + Mac だけ。
- 2026-10-06 時点で未確認: Android アプリの実機(AQUOS)での動作、Windows での座標受信。最初の試験では、アプリの「送信 fps」と Unity で剣が動くかを確認する。

## 1. 全体の流れ

```text
[ゲームの座標]
iPhone PhoneSaberSender(認識 → "x1,y1,x2,y2")
  ├─ P2P 優先: peer-to-peer Wi-Fi(AWDL)UDP ─→ Mac の P2P bridge ─→ 127.0.0.1:5005(RED)/ 5006(BLUE)
  │                                          (Unity の Play で自動起動)
  └─ fallback: LAN の UDP(Bonjour `_phonesaber._udp` / 手動 IP)─→ Mac:5005 / 5006
                                                                      ↓
                                                     Unity InputPoint → SaberInputBridge → ゲーム

[診断(Debug Recording)]
iPhone で Stop ─→ triage bundle
  ├─ P2P 優先: bridge 内の「診断 relay」(`_phonesaber-dp2p._tcp`)─→ 127.0.0.1:8765
  └─ fallback: LAN(Bonjour `_phonesaber-diag._tcp`)─→ Mac:8765
        ↓
  受信側(Unity Play で自動起動 / Start PhoneSaber)→ inbox に保存 → 1ページ要約 `.report.md`(無料・すぐ)→ Codex 解析
```

- P2P は追加の経路です。bridge が無くても、iPhone と Mac が同じ LAN なら従来どおり LAN で届きます(`ios/PhoneSaberSender/P2P_BRIDGE.md`)。
- bridge は Unity が **5005 と 5006 の両方を受信できている間だけ** 動きます(`3D-Saber/.../InputPoint.cs` の `SetReceiverAlive`)。
- 受信側は診断専用です。Unity Play で自動起動し、止まっていてもゲームの座標は届きます。Unity を閉じても解析のため残ります。Start PhoneSaber からの単独起動もできます。
- inbox: `~/Library/Application Support/PhoneSaber/diagnostics-inbox/`。受信側のログ: `~/Library/Logs/PhoneSaber/latest.log`。

## 2. 前日までの準備(チェックリスト)

| | 項目 | 根拠 |
|---|---|---|
| ☐ | iPhone に main の最新の PhoneSaberSender を Xcode で入れる | `docs/claude/STATUS.md` |
| ☐ | Mac で単一の `3D-Saber` repo があり、Unity root 内の `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py` を起動できる。違う場所なら環境変数 `PHONESABER_P2P_BRIDGE_SCRIPT` | `PhoneSaberP2PBridgeProcess.cs` |
| ☐ | bridge を一度 build しておく(Unity を開くと裏で1回 build。初回は数秒〜十数秒。`~/Library/Caches/PhoneSaber/p2p-bridge/` に保存) | `P2P_BRIDGE.md`「起動方法」 |
| ☐ | デスクトップに Start PhoneSaber と PhoneSaber Status などを置く: `Tools/install_phone_saber_launcher.command` を1回ダブルクリック(以前に入れた Mac でも、もう一度実行すれば足りない Status だけ追加。既存のリンクと動いている受信側はそのまま) | `PHONE_SABER_TRIAGE.md` |
| ☐ | Codex CLI がログイン済み(`PhoneSaber Status.command` で `[OK] Codex CLI`)。無くても受信と要約は動く | `PHONE_SABER_TRIAGE.md` |
| ☐ | **iPhone のローカルネットワーク許可**: 設定 > PhoneSaberSender > ローカルネットワーク を ON。拒否中は `P2P Failed (ローカルネットワークの許可が必要: 設定 > PhoneSaberSender)` と出る | `P2P_BRIDGE.md`「表示とログ」 |
| ☐ | **Mac のローカルネットワーク許可**: 初回に macOS が聞いてきたら、bridge を起動したアプリ(通常は Unity。単独起動なら Terminal)を許可。システム設定 > プライバシーとセキュリティ > ローカルネットワーク で確認 | `P2P_BRIDGE.md`「起動方法」 |
| ☐ | iPhone の **Wi-Fi は ON**(学校 Wi-Fi に参加しなくてよい。参加していなければ AWDL `awdl0` になる)。**インターネット共有(Personal Hotspot)は OFF**。モバイルデータは関係ない(P2P は cellular を使わない) | `P2P_BRIDGE.md`「実機での確認手順」A・F |
| ☐ | Mac は学校 Wi-Fi のままでよい(Codex などのネットは学校 Wi-Fi で続く) | `P2P_BRIDGE.md` 冒頭 |
| ☐ | iPhone の「**P2P優先**」toggle を ON(既定 ON、設定は保存される)。OFF にすると従来の LAN だけの動作 | `P2P_BRIDGE.md`「fallback の条件」 |
| ☐ | **カメラ**: 正立・固定。レンズに指をかけない。saberなしで肌・照明・画面・布の誤検出を確認し、必要なら画角・照明を調整する。赤い物の配置・隠蔽は不要 | [FINDINGS](FINDINGS.md)、[STATUS](STATUS.md) |
| ☐ | **露出**: 既定は自動。暗所の 1/50 秒では速い振りがぶれる。Debug Recording に露出上限（1/100・1/120・1/240 秒）の実験スイッチはあるが、既定を変える根拠は未確認。当日に設定を試行錯誤しない | [FINDINGS](FINDINGS.md) |

## 3. 当日の起動順

1. Mac: Unity で 3D-Saber を開き **Play**。bridge と診断受信側が自動で起動する。
2. Mac: **PhoneSaber Status.command** で受信側・bridge・UDP 5005/5006 を確認。診断だけ単独で使うときは **Start PhoneSaber** を開く。
3. iPhone: PhoneSaberSender を起動 →「P2P優先」ON →「通常送信を開始」。
4. 剣を振って、下の「正常な状態」と送信先 Mac 名を確認する。

終わるとき: Unity の Play を止める（bridge も止まる）。診断受信側は残る。
Start PhoneSaber から単独起動した受信側を止める場合は、そのウィンドウで Ctrl+C。

## 4. 正常な状態

| どこ | 正常 | 根拠 |
|---|---|---|
| iPhone「経路」 | `P2P Connected (awdl0 · Phone Saber Unity P2P (<Mac名>))`。同じ Wi-Fi にいると `en0` になることもある | `P2P_BRIDGE.md`、`P2PSender.swift` |
| iPhone の RTT 行 | `P2P RTT 中央値 … ms / p95 … ms / 最大 … ms / ping欠落 …%`(直近 40 回。欠落 0% が理想) | `ContentView.swift`、`P2P_BRIDGE.md` |
| Unity Console(起動時) | `[PhoneSaber][P2P] listening on UDP …`、`Bonjour registered …` | `P2P_BRIDGE.md`「実機での確認手順」B |
| Unity Console(接続時) | `peer connected …`、`RED received …` / `BLUE received …`(session ごとに1回) | 同上 D |
| Unity Console(10 秒ごと) | `[PhoneSaber][P2P] last 10s: BLUE=… RED=… maxGapMsRED=… maxGapMsBLUE=… peers=1`。座標は約 32 個/秒(10 秒で約 300)。`maxGapMs` は座標の届く間隔の最大(30 fps の本来は 33 ms) | `P2P_BRIDGE.md`「表示とログ」「遅延について」 |
| Unity Console(復帰時) | `peer alive (session …); iPhone can use P2P` | `PhoneSaberP2PBridge.swift` |
| Status ツール | 全部 `[OK]`。`maxGapMs` は 150 ms 未満で OK、150〜499 で WARN、500 以上で NG(この tool 独自の目安) | `Tools/phone_saber_status.py` |

## 5. 症状 → 原因 → 対処

| 症状 | 主な原因 | 対処 |
|---|---|---|
| **剣がラグい・カクつく** | AWDL の一時的な詰まり。Mac が学校 Wi-Fi と AWDL のチャンネルを行き来するため。実測で `maxGapMs` 170〜300 ms、ときに約 1 秒(座標数は正常)。iPhone の認識時間(中央値約 28 ms)は原因ではなかった(`P2P_BRIDGE.md`「遅延について」) | Status ツールか Unity Console の `maxGapMs`、iPhone の `P2P RTT` を見る。続くなら iPhone と Mac を**同じ Wi-Fi** に入れて「P2P優先」を **OFF**(LAN で送る) |
| **saber が無いのに剣が跳ぶ・出る** | 肌・照明・画面・布などの誤検出。warmNoDeepRed 後も残る（[FINDINGS](FINDINGS.md)） | カメラを固定し、レンズを覆わず、照明・画角を調整する。背景だけでなく人が入った状態も確認する。閾値は当日いじらない |
| **速く振ると剣が消える・別の場所に飛ぶ** | ブレで本物が不採用になり、背景が勝つ。1/50 秒で先端約 85px のブレがあった（[FINDINGS](FINDINGS.md)） | 会場の照明・画角を確認する。露出上限は事前の実機比較で確認した設定だけ使う。swing の lossless を残す |
| **P2P がつながらない**(`P2P Searching` / `P2P Failed` のまま) | ① iPhone のローカルネットワーク許可が無い ② Mac 側の許可が無い ③ bridge が動いていない(Unity が Play 前、5005/5006 のどちらかを受信できていない、`PHONESABER_P2P_BRIDGE=0`、一度異常終了すると次の script compile まで自動起動しない)④ iPhone の Wi-Fi が OFF、Hotspot が ON(`P2P_BRIDGE.md`、`PhoneSaberP2PBridgeProcess.cs`) | Status ツールで `P2P bridge` と `UDP 5005/5006` を見る。Unity Console の `[PhoneSaber][P2P] bridge …` 警告を確認。異常終了後は Unity で script を再 compile するか Editor を再起動。だめなら「P2P優先」OFF + 同じ Wi-Fi |
| **LAN に戻ってしまう**(Unity に `no ping for 3s; iPhone falls back to LAN`) | iPhone は最後の pong から 1.5 秒で LAN に切り替える。座標の送信が 3 秒詰まっても LAN に戻して張り直す。pong が 0.5 秒以内の間隔で 3 回続けば自動で P2P に戻る(`P2P_BRIDGE.md`「fallback の条件」) | 待てば戻る。iPhone が学校 Wi-Fi にいない構成では LAN の経路が無いので `Reconnecting` で待つ。頻発するなら同じ Wi-Fi + P2P OFF |
| **診断(bundle)が Mac に届かない** | ① 診断受信側が止まっている(relay は接続を閉じ、iPhone は LAN を試す)② 録画が約 1 秒未満(30 frame 未満は送らない)③「Stop後にtriage bundleをMacへ自動転送」が OFF ④ P2P relay は bridge の一部なので Unity の Play 中だけ(`PHONE_SABER_TRIAGE.md`、`DebugBundleTransfer.swift`) | Status ツールで `受信側` を確認。Unity Console の `diag relay: upload from … closed after N bytes (…)`、`latest.log` の `[triage] received` を見る。3 回失敗しても bundle は iPhone に残る |
| **Codex が timeout する** | 解析は最大 25 分。timeout なら effort high で1回だけ再試行(`phone_saber_triage_codex.py` の `CODEX_TIMEOUT_SECONDS`)。それでも失敗すると bundle を残して終わる。受信側を再起動しても自動で再解析しない | `.report.md`(無料の1ページ要約)は先にできているのでそれを見る。後で手動: `python3 ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py "<inbox>/phone_saber_triage_<session>"` |
| **413 / precheck で止まる** | 413: 受信側の上限(512 MiB)を超えたか壊れた upload(iPhone 側は 64 MiB・20 枚に制限しているので普通は出ない)。precheck: 選ばれた画像に時間方向の証拠が足りない(例 `temporalEvidenceMissing`。1 frame の録画で実際に起きた)。context 1 件 32 KiB の上限は変えない(`phone_saber_triage_receiver.py`、`phone_saber_triage_protocol.py` の `MAX_BUNDLE_BYTES`、`DebugBundleTransfer.swift`、`phone_saber_tracking_diagnostics.py` の `tracking_preflight`、`CLAUDE.md` §1) | Status ツールの `Codex 解析` に reasonCodes が出る。saber を映して数秒以上振る録画を撮り直す |
| **Unity に座標が届かない** | 5005/5006 を別の process が使っている(2つ目の Unity、古い受信 script など)。Unity は失敗すると `[PhoneSaber][RED] receiver failed: …` を出し、間隔を伸ばしながら bind をやり直す。どちらかが受信できない間は bridge も止まる(`InputPoint.cs`) | Status ツールの `Unity 受信 UDP` に、port を使っている process 名と pid が出る。その process を止めて Play し直す |
| **近くに PC が2台ある** | 台未指定では、従来どおり最初の PC が選ばれる | §0 の手順で PC とスマホを同じ **台 A / B** にする。Unity の Play を開始し直し、スマホの送信先を確認。手動 IP を使う場合はその台の PC の IP を指定する |

## 6. 状態チェックツール(`PhoneSaber Status.command`)

`ios/PhoneSaberSender/Tools/PhoneSaber Status.command`(中身は `phone_saber_status.py`)。デスクトップのリンクは `install_phone_saber_launcher.command` が置きます。読み取りのみで、数秒で終わります。

- 受信側: `127.0.0.1:8765/health` が `ready` か。受信側が読み込んだコードが起動後に変わっていれば WARN。待機中なら Start PhoneSaber をもう一度ダブルクリックすると自動で起動し直す(解析中・受信中は止めない)。
- Unity: UDP 5005/5006 を誰が受信しているか(`lsof`)。Unity 以外なら NG(port の取り合い)。
- P2P bridge: `PhoneSaberP2PBridge` の process があるか。
- Unity Console(`~/Library/Logs/Unity/Editor.log`): 直近の Play 以降の `last 10s` 集計(直近5回の `maxGapMs`)、最後の接続イベント(`peer alive` / `no ping` など)、警告、診断 relay。Editor.log の行には時刻が無いので、ファイルの最終更新時刻を出します。
- 診断: inbox の最新 bundle、`.report.md` の有無、Codex 解析の結果(完了 / precheck 中止 / 実行中 / timeout)。
- git: 単一の 3D-Saber repo の branch、未commit、origin との差(fetch はしない)。
- Codex CLI があるか。

終了コードは NG あり 2、WARN のみ 1、すべて OK 0。Terminal から `python3 ios/PhoneSaberSender/Tools/phone_saber_status.py` でも動きます。

> 診断受信側の自動起動を無効にする場合は `PHONESABER_TRIAGE_RECEIVER=0`。Start PhoneSaber を別に開いても重複起動しません。
