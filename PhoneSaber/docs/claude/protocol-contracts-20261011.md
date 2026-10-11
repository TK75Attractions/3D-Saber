# PhoneSaber プロトコル契約照合（2026-10-11）

対象はこの worktree の iPhone、Android、Unity、Mac P2P bridge、起動キット、Python tools。認識の ranking / eligibility / scoring / PCA / thresholds、formal lossless corpus、シーン・prefab・meta・ProjectSettings は変更していない。commit / push / 公式 verify はオペレーターが行う。

**残存する問題を含む報告であり、「バグなし」の宣言ではない。** ゲームの経路選択・座標写像・警告表示を変える案は依頼の制約に従って実装していない。通常の iOS/Android の座標本文・生成・送信タイミングは変えず、既存の計測 prefix の読み取り不整合を修正した。開始時は `8114236 wip (interrupted by the Codex usage limit, unverified): protocol attempt 1`、未commit差分なし。WIP をレビューして継続し、status tool の表示変更は取り消した（開始前の `a9a1e53` と同じ）。今回 commit / push はしていない。

## ソースの読み方

以下の `ファイル名:行` は次の実ファイルを指す（同名ファイルは所属を付記）。行番号は修正後。

| 記号 | ルート |
| --- | --- |
| iOS | `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/` |
| Android | `PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/` |
| core | `PhoneSaber/android/core/` |
| JNI | `PhoneSaber/android/app/src/main/cpp/phonesaber_jni.cpp` |
| Unity | `Assets/Scripts/Managers/Inputsystem/Input/` |
| bridge | `PhoneSaber/ios/PhoneSaberSender/Tools/p2p_bridge/PhoneSaberP2PBridge.swift` |
| tools | `PhoneSaber/ios/PhoneSaberSender/Tools/`（`latency_report.py` と `saber_motion_replay.py` は `PhoneSaber/tools/`） |

## 発見事項（重大度順）

### P1・提案のみ：複数 P2P 端末の競合を Unity の sender-gate が検出できない

`PhoneSaber/ios/PhoneSaberSender/Tools/p2p_bridge/PhoneSaberP2PBridge.swift:415` は session ごとに座標を受理し、`:429` で同じ `LocalForwarder` へ渡す。`:76` / `:98` は全端末の本文を1個の UDP socket から loopback へ送る。一方 `Assets/Scripts/Managers/Inputsystem/Input/InputPoint.cs:474` は送信元 IP だけをゲートへ渡し、`PhoneSaberInputStats.cs:33` は同じ IP を常に受理する。

具体例：同じ台 A を選んだ2台の iPhone が P2P で RED 座標を同時送信すると、異なる session の値が両方 `127.0.0.1` から届き、剣が交互に飛んでも `sender-conflict` は出ない。実bridgeの追加監査 `test_two_peers_share_the_unity_sender_identity` で、別socket/sessionの2本文が同じ送信元IP・portへ集約されることを再現した。この監査は現状の制約を記録するもので、修正を示す回帰テストではない。

提案：bridge 側に色別の端末所有権を持たせるか、LAN/P2P 共通の端末識別を設計する。別々の台への送信・再接続との関係を決める必要があり、本文に session を付加する変更や loopback の無条件免除はしない。ゲームに見える受理ルール変更なので未実装。

### P1・提案のみ：iPhone が互換性のない LAN 応答を生存証拠として扱う

`PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/UDPSender.swift:653` は `hasPrefix("PHONESABER_UNITY 1")` だけを検証する。`PHONESABER_UNITY 10 ...`、prefix だけ、`red=6000 blue=6001`、重複 field、空 station も生存扱いになる。Android の `Protocol.kt:27` / `DiscoveryReply.compatible` は拒否する。実際の `UDPSender.swift` / `P2PSender.swift` / `P2PProtocol.swift` を Mac 用にコンパイルし、共通 discovery ベクトルを `recordReplyForTesting` に入れて差を確認した。

具体例：5007 に応答する受信機が 6000/6001 を広告し、同じ Mac の P2P bridge は使える。iPhone は固定 5005/5006 へ送り続けるが、`CoordinateDelivery.route`（`UDPSender.swift:857`）は LAN verified のため P2P を使わない。Unity を別ポートで起動した場合に成立する。現行の既定ポート構成では正しい応答なので発生しない。

**2026-10-12 追記（Claude）: version の区切りと port（red=5005 / blue=5006 以外）は `LANLivenessProbe.isCompatibleReply` で拒否するよう修正した（`protocol_discovery.tsv` の custom_ports・version_10 の ios_alive を 0 に更新）。重複 field・未知 field は従来どおり生存扱い。** 提案：Android と同じ version・必須 field・重複・ポート互換性の検証を LAN probe に入れる。自動選択の station 照合と手入力時の扱いは別に決定する。経路選択が変わるので未実装。共通 `protocol_discovery.tsv` は Android で全11件検証済み。iOS の現状ではそのうち `custom_ports / version_10 / bare_prefix / unknown / duplicate / empty_station` を alive とする。

### P1・提案のみ：正常な LAN/P2P 切替を Unity の sender-gate が競合と見なす

`Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberInputStats.cs:24` は同じ IP が 1秒以内に戻れば競合と判定してその IP に固定する。iPhone は LAN の IP と bridge の `127.0.0.1` が同一端末でも、経路を切り替える（`UDPSender.swift:857`）。bridge の本文には元の送信元 IP がない（`PhoneSaberP2PBridge.swift:429`）。

具体例：LAN 到着 t=0、P2P 到着 t=0.1、遅延していた LAN 到着 t=0.2 で LAN に固定、その後 P2P だけが届くと t<1.2 は拒否する。あるいは探索応答の欠落・復帰によって LAN→P2P→LAN→P2P が 1秒内に起きても同じ問題になる。`InputPoint.cs:486` が拒否パケットを座標へ反映しないため、動作中の通信でも入力の空白を作る。単方向 LAN→P2P は既存テストで無損失だが、往復は競合防止仕様そのものと衝突する。

提案：同一端末の経路変更と別端末の競合を識別する設計を決める。loopback を無条件に免除すると別 iPhone との競合防止を壊すため、単なる whitelist や 1秒定数変更はしない。ゲームに見える受理ルール変更なので未実装。

### P2・修正済み：既存 `timestamp=` 計測の LAN / P2P / 統計の不一致

`PhoneSaberPacketParser.cs:32` と `PhoneSaber/udp_receive_probe.py:535` は `ts=` と `timestamp=` を受ける。しかし Unity の統計は前者しか読まず、P2P の `P2PPayload` も後者を拒否していた。

具体例：`timestamp=1759400000.123456;1,2,3,4` は LAN では座標を更新するが片道統計が空になり、P2P bridge では転送されない。現行 iOS/Android が生成する prefix はどちらも `ts=` なので通常送信の結果は変わらない。既存の計測互換形式のみ合わせた。

変更：`PhoneSaberInputStats.cs:287` の診断用時刻 reader と `P2PProtocol.swift:129` の検証に alias を追加。UDP 本文は一切書き換えない。共通15ベクトルを Python・Swift・C#・実 bridge で使用。修正前は alias 2件が C# / Swift / bridge で失敗し、修正後は全件成功。NUnit の alias 正例と不正時刻負例、iOS XCTest の byte 保存例を追加。旧 parser の座標 oracle は保持し、診断時刻の alias 期待値のみ更新した。formal recognition corpus ではない。

### P3・提案のみ：正常な bridge 終了を status tool が障害として表示

`PhoneSaberP2PBridgeProcess.cs:277` は正常終了を `bridge exited (code 0)` と書く。`phone_saber_status.py` が全 `bridge exited (code` 行を警告にしていた。親プロセスの正常終了等の後に status を見ると、不要な再コンパイル・再起動の案内が出る。

提案：終了コード0の行を終了警告から外す。前回 WIP に含まれていた実装と回帰テストは、今回の「操作者が気づく挙動変更は提案のみ」という制約に合わせて取り消した。既存の警告表示は維持している。前回の `status-before.log` はこの誤分類の再現証拠として残っているが、修正済みとは扱わない。

## 共通契約一覧

| 契約 | iPhone / Android | Unity / bridge / tools | 照合結果 |
| --- | --- | --- | --- |
| 座標 UDP port | iOS `CameraViewModel.swift:1616` 5005 RED / 5006 BLUE、`UDPSender.swift:87` 既定配列。Android `Protocol.kt:6`、core `src/frame_processor.cpp:61` 同じ | Unity `InputPoint.cs:50`、bridge `P2PProtocol.swift:28`、Python `udp_receive_probe.py:28` 同じ。replay `saber_motion_replay.py:343` 既定も同じ | 一致。Unity/bridge/tool は port 変更可、phone は固定。変更構成の自動互換性は保証されない（P1 LAN probe） |
| discovery UDP port | iOS `UDPSender.swift:535` 5007 unicast、Android `Protocol.kt:8` / `PcDiscovery.kt:192` broadcast | Unity `PhoneSaberDiscoveryResponder.cs:14` 5007 bind、replay `saber_motion_replay.py:362` 既定5007。bridge はこのサービスを提供しない | 一致。座標 socket の応答・ACK ではない |
| 旧stereoの5007用途 | `PhoneSaber/stereo_camera.py:27` / `:260` は3D診断JSONを同じ5007へ送る | Unity `PhoneSaberDiscoveryResponder.cs:37` はJSONを要求とみなさず無視する。通常の2D座標はstereoも5005/5006 | 用途は異なるが探索応答を偽造しない。別の3D受信toolを5007へbindする構成では探索と競合する。既存toolのport変更は提案のみ |
| 座標本文・色 | iOS `DetectionCore.swift:1411`、core `src/frame_processor.cpp:12` は `x1,y1,x2,y2` ASCII。Android `Protocol.kt:49` は native 本文をそのまま送る | Unity `PhoneSaberPacketParser.cs:40` は4要素 stick / 旧2要素 point。bridge `P2PProtocol.swift:135` は4要素だけ、`PhoneSaberP2PBridge.swift:93` / `:429` は body を byte 単位で転送 | phone の4要素は一致。旧2要素の P2P 拡張はしない |
| 数値と locale | iOS `DetectionCore.swift:1414` は Int interpolation、core `src/frame_processor.cpp:14` は `std::to_string(int)`：小数点・指数・桁区切りなし、負数は `-`。計測時刻は iOS `:1434` `%.6f`、core `:19` classic locale + fixed 6桁 | Unity `PhoneSaberPacketParser.cs:70` InvariantCulture / NumberStyles.Float、bridge `P2PProtocol.swift:137` Double、Python `udp_receive_probe.py:542` float。小数・負数・指数を受理 | sender の座標は整数。reader の小数・指数互換を共通ベクトルで確認。C# は fr-FR でも成功。Swift format の通常の nil locale は decimal point、C++ は classic を明示 |
| 丸め・座標サイズ | iOS `DetectionCore.swift:1404` と core `src/frame_processor.cpp:7`：`source-1` → `output-1`、最寄り整数（半端は0から遠い側）。iOS `CameraViewModel.swift:654` 1920×1080（設定可能）、core `include/phonesaber/core.hpp:82` 同じ既定 | Unity `InputPoint.cs:188` 1920×1080、`InputPoint.cs:298` / `:313` は /width・/height。端の1919/1079は1未満 | sender 同士は一致。既存の w-1 対 w の差は維持（下記） |
| mirror と y方向 | iOS `CameraViewModel.swift:656` false/false が初期値で保存、`DetectionCore.swift:1407` で output-1 から反転。Android `MirrorSettings.kt:3` false/false、JNI `:47` / `:58` で通常・計測とも設定を渡す | Unity `InputPoint.cs:819` は y を自動反転しない。Python `smartphone_camera.py:385` は初期 flip-h/v=true、`:629` は output - scaled 方式 | iOS/Android は一致。実 JNI を通常/計測、mirrorX off/on で実行して同じ反転を確認。古い Python tracker の初期反転・写像は別設定であり変更しない |
| 未検出時 | iOS `FrameProcessor.swift:653`、core `src/frame_processor.cpp:46`：最大3 missing frame は予測送信、180ms 内の held は preview のみ。その後出力なし。iOS `CameraViewModel.swift:1605` / Android `Protocol.kt:51` は fresh のみ送る | Unity `InputPoint.cs:79` / `:83`：最近の座標の活性を既定1秒で切る。格納座標や HasValidStickEndpoints は消去せず利用側が活性を見る。bridge は無検出の通知を合成しない | 一致する silence 方式。未検出専用 sentinel はなく `(0,0,0,0)` は欠損通知ではない |
| sender pending / 鮮度 | iOS `UDPSender.swift:132` 最新pending、`P2PSender.swift:350` 色ごと最新pending、Android `LatestUdpSender.kt:35` 空frameも置換、`:68` 180ms以上で破棄 | bridge `PhoneSaberP2PBridge.swift:93` nonblocking sendto、再送なし。Unity は受信した最新値を main thread に反映（`InputPoint.cs:493`） | 無制限 FIFO なし。P2P の in-flight watchdog 3秒は鮮度制限/到着保証ではない |
| 計測 prefix | iOS `DetectionCore.swift:1433` / `CameraViewModel.swift:1611` 検出後の Unix epoch `ts=...;`。Android JNI `:51` / core `src/frame_processor.cpp:16` も同じ（**Android は送る**）。replay `saber_motion_replay.py:336` も ts | Unity parser `:32`、stats `PhoneSaberInputStats.cs:285`、bridge `P2PProtocol.swift:129`、probe `udp_receive_probe.py:535` は ts/timestamp を受理。bridge は prefix ごと本文保存 | alias の不一致を修正。timestamp は送信キュー投入前、camera timestamp ではない。片道遅延には時計同期が必要 |
| 不正値 | phone は有限・画面サイズ内の整数を通常生成 | Unity `PhoneSaberPacketParser.cs:68` ±1e6 の usability gate、P2P `:136` 座標 Double finite・本文256 byte以下、時刻は Double parse のみ、stats `PhoneSaberInputStats.cs:292` は有限正 epoch のみ。probe は finite を診断に使う | NaN座標等は拒否。P2Pの2000000座標は通るがUnityは拒否、0/非有限時刻の扱いにも差がある。通常senderは生成しないので許容範囲変更はしない |
| discovery request / version | iOS `UDPSender.swift:536`、Android `Protocol.kt:25`、replay `saber_motion_replay.py:368` は `PHONESABER_DISCOVER 1` | Unity `PhoneSaberDiscoveryResponder.cs:37`：64 byte以下、Trim 後完全一致。version2等には答えない | 一致。bridge の liveness は binary ping で別物 |
| discovery reply field/order | Android `Protocol.kt:27` 必須 red/blue/name、任意station、順不同、5〜6token、重複/未知/空field拒否。iOS `UDPSender.swift:653` は prefix のみ | Unity `PhoneSaberDiscoveryResponder.cs:30` は `PHONESABER_UNITY 1 red=.. blue=.. name=.. [station=..]` の固定順、PC名space→underscore、ASCII送信。replay `saber_motion_replay.py:351` は順不同・name任意・未知field許容・重複は最後が勝つ | 現行Unity出力は全readerに適合。iOS validation はP1。未知field追加やparser厳格化は現行契約を変えるので一括実装しない |
| station UDP | Android `Protocol.kt:17`：設定空なら全受理、A/Bはreply.station完全一致。手入力の返信watchはstationで絞らない（`PcDiscovery.kt:211`）。iOS の probe はstationを読まない | Unity `PhoneSaberDiscoveryResponder.cs:133` は短い安全なlabel、CLI→env→prefs（`:147`）、空stationはfield自体を省略。replay `:380` は省略(None)なら全、明示空は空stationのみ | A/Bは大小区別。phoneの選択UIは空/A/B、Unityは安全な他labelも許可。空stationのfieldを送るのではない |
| station Bonjour | iOS `P2PSender.swift:32` と Android `Protocol.kt:20`：空なら全、末尾独立token ` A` / ` B` 完全一致。iOS LAN browser `CameraViewModel.swift:2631` も同じ | Unity `PhoneSaberBonjourPublisher.cs:48` `Phone Saber Unity[ A/B]`、P2P `PhoneSaberP2PBridgeProcess.cs:82` `Phone Saber Unity P2P (<Mac>)[ A/B]`。bridge `:292` は渡されたnameをそのまま公開 | 一致。同名衝突時の自動 ` (2)` suffix は両phoneとも台指定で拒否する（共通の既存制約）。standalone bridge は既定Mac名なので台選択には `--name` が必要 |
| Bonjour type/domain/port | iOS LAN `CameraViewModel.swift:2605`、Android `PcDiscovery.kt:315` は `_phonesaber._udp.`。iOS P2P `P2PSender.swift:498` は `_phonesaber-p2p._udp` includePeerToPeer | Unity `PhoneSaberBonjourPublisher.cs:8` local. / red port、bridge `:290` 動的UDP listen port。Python probe `udp_receive_probe.py:29` は同typeだがname=`Phone Saber Mac` | 末尾dot表記差はAPI表記。LANのadvertised portはblue portを表さずphoneの5005/5006は固定。AndroidはP2Pをbrowseしない |
| P2P framing | iOS/bridge **同じ** `P2PProtocol.swift:6` / `:101`：PSP2、version1、20byte header、kind1座標/2ping/3pong、color0RED/1BLUE、session UInt32 BE、sequence UInt64 BE、body最大256 byte | build source共有は `tools/phone_saber_p2p_bridge.py:31`。ping/pongは空body、未知kind/color/version・短header・長body拒否。reservedは送信0、decodeは無視 | 一致。header を独立実装していない。4626 encoding differential ケース成功。reservedやpingのBLUE許容を狭める変更はしない |
| P2P session / sequence | iOS `P2PSender.swift:325` 起動ごとランダムsession、同sessionのsequence増加、ping `:760`、受信pong `:690` sessionを照合 | bridge `:410` はphone sessionとsequenceをecho。`P2PProtocol.swift:150` 色・sessionごと重複/逆順拒否、30秒で忘却、別sessionは別枠 | 一致。phoneのlivenessはpong到着時刻を使い、未対応sequenceでも同sessionなら更新する。RTTだけ outstanding を照合（`P2PSender.swift:235`）。厳格化は提案対象 |
| iOS LAN liveness / watch | `UDPSender.swift:565` 0.5秒probe、`:538` alive≤1.5秒、`:758` unproven状態が3秒続けば警告、manualはP2Pを使わない。`:857` はverified LAN優先 | Unity discovery は両receiver bind中に動く。bridgeはLAN probeに応答しない | 計算上1回・2回のprobe欠落は耐える。1.5秒超でP2Pがusableなら切替。UDP5007の応答は座標受信のACKではなく、この意味差は残る |
| Android liveness / selection | `PcDiscovery.kt:195` 2秒probe、receive timeout500ms、`:164` UDP候補8秒/NSD45秒失効、30秒refresh、NSD resolve6秒。`PcReplyWatch.kt:6` 3秒で表示警告 | Unityは5007 unicast reply。Android送信は警告が出ても継続。Bonjour再解決は選択候補を維持 | 3秒warnは2秒probeが1回欠けると発生し得るが停止ではない。手入力宛先へのunicast probeはなく、非Wi-Fi開発overrideもprobeなし。broadcast禁止時の偽警告は改善提案のみ |
| P2P stale/reconnect | iOS `P2PSender.swift:281` ping0.25秒、`P2PProtocol.swift:175` stale1.5秒/reconnect4秒/初回timeout4秒、復帰3pong・gap≤0.5秒、send watchdog3秒。backoff0.25〜4秒 | bridge `:451` 無ping>3秒でログ、`:24` peer無通信>10秒でclose、`:23` 集計10秒。timer tick≤1秒 | 3秒/10秒はphoneの切替を指示する値ではない。通常pingが続けば無検出でもpeerを閉じない。bridgeログの「falls back」は推定でありphoneの実route証拠ではない |
| Unity silence / gate | phoneは上記silence方式 | `InputPoint.cs:79` は1秒未満をactive、`PhoneSaberInputStats.cs:8` gateは固定senderの無音≥1秒で解除、`:302` 表示staleは>1秒、`PhoneSaberEventLog.cs:211` silenceイベントは>1秒 | 境界に1tickの差があるが定数変更しない。1秒gateと1.5秒LAN aliveを単純に同じ値へ揃えてもP1の送信元識別問題は解決しない |
| launcher args / quit marker | Mac `PhoneSaber/mac/saber-watchdog.sh:114` `-phonesaberStation A/B -phonesaberQuitMarker <temp>/clean-quit`、Windows `PhoneSaber/windows/Start-Saber-A.bat:35` / B同行はstationのみ | Unity `PhoneSaberDiscoveryResponder.cs:147` 引数優先、`PhoneSaberOperatorOverlay.cs:70` marker読取、`:295` 正常code0のみ `clean-quit\n` を作る | 一致。Macはopen exit0でもmarker無しならcrash扱い、再起動前に削除。WindowsはPlayer終了コードを待つ設計なのでmarkerを要求しない |
| bridge起動 / kit env | Mac watchdog `:20` script存在時 `open --env PHONESABER_P2P_BRIDGE_SCRIPT=...`、見つからなければLAN-only | Unity `PhoneSaberP2PBridgeProcess.cs:67` override→repo相対、`:125` --name/--red-port/--blue-port/--exit-with-parent。bridge `:43` が同じ引数を受理。`PHONESABER_P2P_BRIDGE=0/off` は無効化 | 一致。直接開いたbuilt .appはrepo scriptを見つけられない場合LAN-only。boot中/異常終了時の探索表示は保証ではない |
| events.log | Unity `PhoneSaberEventLog.cs:55` UTC ISO ms、station=none/A/B、colour=RED/BLUE/SYSTEM、event=...、detailのkey=value、UTF-8、1MiB×5世代 | latency `PhoneSaberLatencyProbe.cs:117` block/max-queued-frames/median-ms/p95-ms/n/red-pkt-per-s/route/platform/editor/rejected/**missesを最後**、reader `latency_report.py:29`。status toolは**events.logを読まず**Editor.logのbridge行を読む（`phone_saber_status.py:325`） | 名称・順序一致。latency readerは未知fieldを無視、misses無しの途中行を捨てる、station/platform・source file・block再開始/終了でsession分離。ログ世代間を自動結合しない既存方針 |
| bridge log | bridge `:267` `[P2P] ...`、`:464` `last 10s: RED=... BLUE=... stale=... malformed=... forwardFailed=... maxGapMsRED/BLUE=... peers=...`（0項目は省略）、`:408` peer alive、`:453` no ping | Unity `PhoneSaberP2PBridgeProcess.cs:262` `[PhoneSaber]` を前置し `[PhoneSaber][P2P]` にする、`:139` listener failed/cannot等はwarning。status `phone_saber_status.py:302` / `:317` が同じprefixと整数fieldを読む | fieldは一致。正常exit誤警告はP3提案。stdoutに時刻がないのでtoolはEditor.logファイルの更新時刻を示すだけ。sendto成功はUnity受信の証明ではない。peer alive/no pingはbridge全体の最後のpingに基づくので、別peerのpingが続けば1台の途絶はこの行で検出されない |
| triage discovery / HTTP | iOS `DebugBundleTransfer.swift:46` `_phonesaber-diag._tcp.`、name `Phone Saber Diagnostics`、LAN receiver port8765。P2Pは `P2PProtocol.swift:21` `_phonesaber-dp2p._tcp`、座標linkのMacを優先（`P2PSender.swift:92`） | receiver `phone_saber_triage_receiver.py:387` 同type/name、bridge `:131` P2P診断typeで動的TCP listener→127.0.0.1:8765。POST /v1/bundle、Content-Type `application/vnd.phonesaber.triage-v1`、Content-Length、Connection:close（Swift `DebugBundleTransfer.swift:519`、receiver `:307`） | wireは一致。優先Mac不在で探索終了するとstationに合う最小nameへfallbackするため同一Macを保証しない。LANはmanual/台指定時には座標host固定（Swift `:156`）、台未指定は最初の診断service。201/2xx成功、400不正bundle/409重複/413上限/415type不一致。Androidはtriage bundleを送らない |
| triage envelope / manifest | iOS `DebugBundleTransfer.swift:348` / `:364`：PSBT、4byte UInt32 BE manifest length、UTF-8 JSON `formatVersion=1 / sessionID / files[]`、各fileはpath/size/sha256/contentType、manifestの順にraw bytes | Python `phone_saber_triage_protocol.py:134` / `:145` 同じ、manifest≤128KiB、2〜42files、SHA256 lowercase64桁、path制限、実Content-Length一致、sessionをsummaryと照合（`:221`） | 一致。Swift filesはpathソート、receiverは順序に依存せずmanifest順に読む。PNGを再圧縮しない |
| triage summary / contexts | writer `DebugRecordingTriage.swift:914` imagesのpath/frameContextPath/frameID/timestamp/color/failureType/reason、`:970` version1/sessionID/selectedImageCount/incidentCount/redBlueDetectionSummary/dropoutSummary、色red/blue/both。contexts `:1482` 1〜5frame、frameID/timestamp/red/blue | protocol `phone_saber_triage_protocol.py:236` 必須fieldとcount/path集合、`:321` contexts形状、report `phone_saber_session_report.py:463` が同名fieldを読む。追加の bridge/motion/selection/guided/exposure/activeColors 等はoptional | 現行writer/readerのschema一致。録画全体metadataとこのcompact summaryは別schema。受信成功は解析preflight成功の保証ではなく、32KB preflight gateはそのまま |
| triage resource / timeout | writer `DebugRecordingTriage.swift:339`、transfer `DebugBundleTransfer.swift:50` 最大64MiB、20画像/42file。LAN request120秒/resource180秒（`:214`）、P2P discovery3秒/transfer180秒（`:438`） | Python protocol `:22` 最大512MiB、20画像/42file、summary512KiB/prompt64KiB/context256KiB。receiver idle150秒（`phone_saber_triage_receiver.py:55`）。bridge relay `:181` / `:200` は256KiBずつ転送しbackpressureをかける | receiver上限はsenderより広く、通常bundleを拒否しない。LAN idle120<150で一致。P2Pはtotal180とreceiver idle150なので無通信150秒ではreceiverが先に切る（独立制限）。上限・retryは変更しない |

## 既存の差を維持したもの・提案

- P2・提案：pixel / normalized の自動判別。`InputPoint.cs:300` / `:315` は両座標の絶対値≤1.5をnormalizedとみなす。phoneのpixel `(0,0)` / `(1,1)` も該当し、左上の小領域を中央付近へ写像し得る。さらにsenderの最大値はw-1/h-1、Unityはw/hで割る。両senderが同じ方式なのでsenderだけを変えない。明示pixelモード/校正の扱いを決める変更は見える動作を変えるため提案のみ。
- P2・提案：station入りBonjour名の自動衝突suffix。iOS `P2PSender.swift:32` / Android `Protocol.kt:20` はどちらも末尾の台tokenを要求する。台Aを複数公開する設置で `Phone Saber Unity A (2)` になると探索不能候補を作る。両側が同じ制約なので維持。suffixを許容する規則やTXT stationを導入するなら、両phone/Unity/bridgeの同時設計が必要。
- Python旧trackerの `smartphone_camera.py:629` は w/h写像・Python round（半端は偶数側）、`aruco_camera.py:306` はint切捨て。phoneのw-1/away-from-zeroとは異なる。`saber_motion_replay.py:336` はphone用の丸めを使う。旧tracker出力の互換を勝手に改めない。
- Android手入力先はbroadcast応答のみ監視している。broadcast遮断APや開発用非Wi-Fi経路で座標が届いても3秒警告となる。個別unicast probe追加は通知の変化になるため未実装。
- bridgeの3秒ログ、P2Pの1.5秒stale、Unityの1秒silenceは別の観測をしている。実無線での一方向UDP loss・遅延pong・5007だけのlossはloopbackでは保証できない。pong sequence検証強化、liveness閾値変更、失敗時のroute表示変更は未実装。
- diagnostics の宛先fallbackは座標linkの宛先保証ではない。例えば同じ台Aを2台のMacが公開し、座標Macのrelayだけが落ちると、`P2PSender.swift:97` の探索終了後fallbackにより別Macへbundleが届く。台未指定LANでも `DebugBundleTransfer.swift:182` は最初の診断serviceを選ぶ。宛先の固定/転送停止は操作者に見える変更なので提案のみ。
- Windowsの終了コード0で終わる異常経路はquit-marker無しでは正常Quitと区別できない。通常の非0 crashでは再起動するが、Unity Playerの実終了経路は未検証。既存の[Windowsキット報告](review-windows-kit.md)にあるmarker導入案を維持し、引数や終了判定は変えていない。

## テストと実行結果

再開後のログは `/private/tmp/phonesaber-contract-resume-*.log`。前回の修正前ログも同じディレクトリに残っており、内容を確認した。テストは固定5005/5006/5007を占有せず、実bridgeは動的loopback portを使用した。下記の成功件数は各suiteの件数で、重複実行を合算していない。

| 実行内容 | 結果 |
| --- | --- |
| 前回の修正前 `test_protocol_contracts.py` | Python成功、C#/Swift各alias2例で失敗（`phonesaber-contract-vectors-before.log`）。production sourceを変更前にして今回追加のfixtureを照合した記録 |
| 前回の修正前 `P2PBridgeTests.test_shared_coordinate_contract_vectors_are_forwarded_unchanged` | 実bridgeのalias2例が転送されず失敗（`phonesaber-contract-bridge-vector-before.log`） |
| `PYTHONPATH=PhoneSaber/tools python3 -B -m unittest test_protocol_contracts test_latency_report -v` | 初回14件成功（`resume-vectors.log`）。Swift/C# compile+run、共通15座標ベクトル、C# fr-FRで29,575 packetの旧座標oracle比較 |
| 追加後 `PYTHONPATH=PhoneSaber/tools python3 -B -m unittest test_protocol_contracts test_saber_motion_replay -v` | 30件成功（共通契約5件＋replay25件、`resume-shared.log`）。iOS実LAN probe・Android・Python replayの共通11探索ベクトルも照合。iOS/Python列は現状の受理差を記録する監査であり、不正応答を正しい契約と宣言するものではない |
| `python3 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_phone_saber_p2p_bridge.py -v` | 28件成功（`resume-bridge.log`）。実Swift bridgeをbuild/run、UDP byte保存・session/sequence・ping/pong・TCP診断relay・再起動等。後から追加した `test_two_peers_share_the_unity_sender_identity` も単独実行で1件成功（`resume-two-peers.log`） |
| `PYTHONPATH=PhoneSaber/ios/PhoneSaberSender/Tools python3 -B -m unittest test_phone_saber_status test_phone_saber_triage_protocol test_phone_saber_metadata_schema test_phone_saber_session_report test_ios_p2p_encoding -v` | status表示変更の取消し後94件成功（`resume-tools-final.log`）。4626 header encoding比較を含む。取消し前96件の結果は最終状態の件数として使用しない |
| `PYTHONPATH=PhoneSaber python3 -B -m unittest test_udp_receive_probe -v` | 38件成功（`resume-probe.log`） |
| `PhoneSaber/android` で `JAVA_HOME='/Applications/Android Studio.app/Contents/jbr/Contents/Home' ANDROID_HOME='/Users/satoshi/Library/Android/sdk' ./gradlew --no-daemon -Pkotlin.compiler.execution.strategy=in-process testDebugUnitTest assembleDebug` | 初回JVM57件成功、arm64 Debug APK build成功（`resume-android.log`）。共通座標test追加後、DiscoveryProtocolTest / PayloadRoutingTestの11件を実行して成功（`resume-android-shared.log`）、最後に全 `testDebugUnitTest` を再実行し58件成功・failure/error/skip=0（`resume-android-final.log`）。Release APKはbuildしていない |
| `make -C PhoneSaber/android/core test PYTHON=python3 BUILD=/private/tmp/phonesaber-contract-resume-core` | native core-test build/run成功、Python parity tool単体4件成功（`resume-core.log`）。formal画像parityの全実行ではない |
| `python3 -B /private/tmp/phonesaber-contract-swift-tests.py` | 実UDPSender/P2PSender/P2PProtocolと既存 `CoordinateDeliveryTests` をswiftc -Oでbuildし6件成功（`resume-swift.log`）。実HostMonotonicClock定義をコピーしたsupportを使用。iOS SimulatorのXCTestではない |
| `python3 -B /private/tmp/phonesaber-contract-resume-nunit-baseline.py` | Unity同梱Mono＋NUnit assertionを使い、reflectionで `PhoneSaberInputStatsTests` / `PhoneSaberSenderGateTests` 61件成功（`resume-nunit-baseline.log`）。Unity Editor Test Runnerではない。WIPより前のsourceでbenchmark失敗の再現も行った（下記） |
| `python3 -B /private/tmp/phonesaber-contract-audit.py` | 実iOS LAN probeの探索11例、実JNIのMac shared library＋JVM harnessをcompile/run（`resume-audit.log`）。通常/計測、mirrorX off/onを成功実行。本文off=`444,217,463,857`、on=`1475,217,1456,857`、計測は同じ座標にtsを付加。Android実機ではない |
| `bash PhoneSaber/tools/unity_alloc_bench/run_mono.sh`（前回） | parser29,575件成功の後、既存benchmarkの旧統計oracleとの比較が `Statistics mismatch` で失敗（`phonesaber-contract-mono.log`）。再開後も一時dirに `a9a1e53` のparser/stats/testsを取り出して同じ失敗を再現（`resume-nunit-baseline.log`）。sender-gate追加以前のoracleと現行統計の差で、今回のalias readerはその比較で呼ばれない。gateやoracleを緩める変更はしていない |
| `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination generic/platform=iOS -derivedDataPath /private/tmp/phonesaber-contract-ios CODE_SIGNING_ALLOWED=NO build` | 再試行もexit65で失敗（`resume-ios.log`）。`sandbox-exec: sandbox_apply: Operation not permitted` とSwiftUI macro plugin応答不正。変更したSwift sourceは別途Macでcompile/run成功だが、iOSアプリRelease成功とは報告しない |
| `git diff --check` / `git diff a9a1e53 --check` | 成功。task全体のruntime差分は統計prefix readerと共有P2P validatorのみ。認識、UDP本文生成、formal corpus、scene/prefab/meta/ProjectSettingsに差分なし |

実行していないもの：公式 `verify_phone_saber.sh`（オペレーター担当）、iOS Simulator全XCTest（CoreSimulatorService接続拒否）、Unity Editor EditMode/PlayMode/Player build、formal lossless 40件の全検証、Android instrumentation/実機、iPhone/Android→無線→Unityの実地試験、Windows起動キットの実行、Mac .appのopen/quit-marker実地試験。今回UnityプロジェクトをEditorで開いていない。

検証上の残リスクはiOS Releaseの未成功、既存Mono benchmarkの失敗、無線・Playerの未試験。`git status` は通常のLFS filterのままで成功しており、前回報告にあったfontの差分はこの再開では発生していない。

## 再実行する共通ベクトル

- `PhoneSaber/tools/fixtures/protocol_coordinates.tsv`：15例。生成済み整数・負数・小数・科学表記・ts/timestamp・不正値・旧2要素・Unityとbridgeの既存許容差。
- `PhoneSaber/tools/fixtures/protocol_discovery.tsv`：11例。必須/任意field・順序・station・version・未知/重複/空field・custom ports。
- `PhoneSaber/tools/test_protocol_contracts.py` はPython/Swift/C#を実行。Android `DiscoveryProtocolTest.sharedDiscoveryContractVectors` は同じdiscovery fixtureを読む。iOSの実LAN probeとPython replayも同じfixtureの別期待値列を読み、既存の受理差を明示する。Android `PayloadRoutingTest` は同じcoordinate fixtureで色portとbyte保存を検証する。実bridgeのPythonテストもcoordinate fixtureを直接読む。Xcode既存 `P2PTransportTests` にもalias byte保存例を追加済み。

残存P1を直すには、まず経路受理・探索validationの変更範囲をオペレーターが決める必要がある。今回のprefix互換修正だけで実地の入力途絶、不正LAN生存判定、P2P複数端末の競合が解消されたとは扱わない。
