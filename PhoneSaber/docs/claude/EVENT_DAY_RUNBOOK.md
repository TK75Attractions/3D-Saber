# PhoneSaber 縁日当日の運用手順(runbook)

2026-10-06 のrepo統合: Git/Unity root は `3D-Saber/`、ツールは `PhoneSaber/`。Git root から `bash PhoneSaber/tools/verify_phone_saber.sh` を実行。旧 school-festival は履歴を保って統合・アーカイブ済み。

当日に「動かす」「おかしいときに直す」ための1枚です。調査の根拠は [FINDINGS.md](FINDINGS.md)、残作業は [STATUS.md](STATUS.md) にまとめています。
迷ったら、まず Mac のデスクトップの **`PhoneSaber Status.command`** をダブルクリックしてください(読み取りのみ。何も変えません)。

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
| **近くに Mac が2台ある** | 座標: iPhone は最初に選んだ Mac(service 名 `Phone Saber Unity P2P (<Mac名>)` の辞書順で最初)に固定し、消えたときだけ乗り換える。診断 bundle も、座標で固定した Mac に送る(その Mac の relay が見つからないときだけ、ほかの Mac に送る)。LAN の Bonjour 名 `Phone Saber Unity` には Mac 名が付かない(`P2PSender.swift`、`DebugBundleTransfer.swift`、`3D-Saber/AGENTS.md`) | 本番以外の Mac では Unity を Play しない(または `PHONESABER_P2P_BRIDGE=0`)。iPhone の「経路」に出る Mac 名を確認。LAN なら手動 IP を入れる |

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
