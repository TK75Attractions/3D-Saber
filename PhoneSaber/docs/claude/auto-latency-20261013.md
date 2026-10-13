# 通常プレイ中の自動遅延診断（2026-10-13）

更新したスマホアプリと PC Player を普段どおり起動して遊ぶだけで記録する。数値入力、F9、画面へカメラを向ける操作、ログのコピー・Xcode からの取得は不要。F8 は結果を見るためだけで、計測開始の操作ではない。認識、座標、カメラ設定、描画設定は変更していない。

## 診断 datagram の契約

宛先は座標送信先 PC の UDP 5007。LAN で送信中のみ5秒ごと、ベストエフォート・再送なし。ASCII、改行なしの1行、400 bytes 未満。

```text
PHONESABER_METRICS 1 station=A platform=ios app=42 fps-mode=60 capture-fps=59.8 send-red-fps=59.7 send-blue-fps=59.6 c2s-median-ms=36.9 c2s-p95-ms=44.1 c2s-n=298 rtt-ms=3.2 rtt-n=8 rtt-age-s=0.2 route=LAN thermal=nominal battery=82.0 low-latency-test=0 c2s-end=detection
```

- 共通キー・整形の期待値は `tools/fixtures/protocol_metrics.tsv`。Swift/Kotlin の formatter と C# receiver が読む。Python の契約テストがfixtureを所有する。数値は小数点 `.`、非負、有限、1桁に四捨五入。欠測は `unknown`、対応するサンプル数は0。`fps-mode` は現在の撮影設定（要求した fps と実際の撮影 fps は別）。`app` は既存の bundle version / versionCode。release 同士が同番号の場合は commit の識別には使えない。
- station（最大16文字）と app（最大24文字）は空白を含まない短い token。`platform=ios|android`、`route=LAN`、`thermal=nominal|fair|serious|critical|unknown`。battery は百分率、low-latency-test は既存カメラ比較設定（0/1）。unknown key は無視、未知version・重複key・非ASCII・不正範囲・矛盾したcountは破棄する。
- 5005/5006 の通常座標 `x1,y1,x2,y2`、既存の任意 `ts=...;`、`PHONESABER_DISCOVER 1` と `PHONESABER_UNITY 1 ...` は変更していない。旧PCの実受信Loopでも metrics を無返信で捨て、その直後の探索へ従来の返信を返すことをloopbackで検証する。旧スマホは診断を送らず、PCは欠測を表示する。Androidの開発用非Wi-Fi手動IPは探索socketを持たないため診断も利用不可。
- iOS は LANLivenessProbe の既存 5007 接続と専用queue、Android は PcDiscovery の探索socket/threadを使用。Androidの計測snapshotは既存UIの更新で得た値を再利用し、座標workerと共有するmeterの追加集計・lock待ちを増やさない。座標用queue/socket/workerに診断を入れない。Android の保留は最新1件のみ。停止・探索世代切替で保留とRTTを消す。iOS は停止・host切替後の旧接続への送信を捨てる。
- PCは400-byte受信buffer、解析は全送信元合計8件/秒、保持は最大8 IP。未知・不正datagramをログにしない。探索要求は解析制限の対象外。UDPそのものの洪水でOSの受信queueが埋まる攻撃まで防ぐものではない。

## 各項目の意味

`total-ms = c2s-median-ms + rtt-ms / 2 + pc-median-ms + display-ms`

| 項目 | 計測 / 推定 | 含むもの・限界 |
| --- | --- | --- |
| スマホ（c2s） | 既存meterの直近5秒の中央値/p95と件数 | iOS `c2s-end=detection`: capture host timestamp → 認識完了（送信直前）。**UDP queue待ち・実際のsend完了は含まない**。Android `c2s-end=os-send`: REALTIMEセンサー先頭行の露光開始 → そのframeで最後に成功したOS送信。時計がUNKNOWNの端末は欠測。カメラのtimestampより前の露光・rolling shutter全体を保証しない。両色共通のframe統計であり、色別の独立c2sではない。 |
| 通信（network-ms） | 推定 | スマホの単調時計で測る既存探索のrequest→reply RTTの中央値÷2。非同期時計でも使える。探索reply処理・OS配送を含む。往復非対称・port別処理・送信待ちの差は分離不能。 |
| PC（pc-median-ms / pc-p95-ms） | 実測 | 各色で、**新しい座標が公開されてから最初の `onBeforeRender` 直前まで**（`event=perf` の `*-receive-shown-*` と同じ）。座標が来ていない間・同じ座標を描き続けている間は標本を増やさない。GPU完了・present待ち・実際の発光は含まない。2026-10-13 の実機で、当初の定義（読み出し済み座標の年齢）は曲選択で数秒〜数十秒と出たため Claude が変更。 |
| 画面（display-ms） | 推定 | **設定refresh Hzの半周期**（500/Hz ms）。60Hzなら約8ms、30Hzなら約17ms。vsync・設定Hzはログに残すが、この項目にUnityの900fpsを使わない。glassの実Hzはunknown。低電力で設定60Hz・実30Hzなら過小評価になる。 |
| total | 上記の和の目安 | 個々の中央値の和。実際の同一frameのend-to-end実測でも、そのp95でもない。**プロジェクター固有処理、GPU/present待ちなど未計測分は加算していない**。 |

RTTは直近10秒・最大16個。iOSは0.45秒以内、Androidは1秒以内の応答を標本とし、失敗は数えない。同じrequestの重複応答を除く。既存protocolにnonceがないため、遅い旧replyと新requestを完全に対応付けられない（既存文面を維持する制約）。iOSの接続準備中のprobeはRTTに数えない。

## F8 と PC ログの読み方

色ごとに1行だけ追加する。例えば `遅延の目安 赤: 約54 ms（スマホ 37 + 通信 約2 + PC 7 + 画面 約8）/ 1秒前まで・投影機の処理は含まず`。スマホ/PCは上記定義で実測、通信/画面は推定。

初期値や15秒超の古いスマホ値は `スマホからの計測が届いていません（旧アプリ / P2P 経由）`。項目不足・10秒超のRTT・1秒超の座標無受信は `計測不足`。診断は座標の有効送信元IPと照合する。赤/青で別端末ならそれぞれの端末の値を使い、LAN→P2Pのloopbackや別IPの値は流用しない。表示の数値は10秒ごとに更新し、F8表示時にも期限を確認する。phone-age-sはPCでのmetrics受信からの経過時間で、Androidの探索workerへ渡してから最大約0.5秒の待ちと5秒の集計窓は別にある。

`Application.persistentDataPath/PhoneSaber/events.log` に色別 `event=latency-estimate` を10秒ごとに自動記録する。シーンを切り替えても10秒の記録時刻はリセットしない。`status=estimate` のみ総計を持ち、他はunknown。各項目、phone/RTT/PCの件数、phone-age-s/rtt-age-s/pc-coordinate-age-s、fps、経路、refresh/vsync、端末状態を残す。既存formatterの512文字切捨てをdetailのみ2048文字上限へ修正した（queue数・rotation容量は維持）。

PCのログだけで一日を集計する:

```bash
python3 PhoneSaber/tools/latency_report.py --estimates --since 2026-10-13T00:00:00+09:00
# 特定ファイルを使う場合（現行＋rotationを一緒に渡す）
python3 PhoneSaber/tools/latency_report.py --estimates /path/to/events.log /path/to/events.log.1
```

既定ではMac/Windowsの既存保存先から全世代を探す。台・色・fps-mode・platform・route・c2s-end別に各項目とtotalの中央値/p95を出す。これは**10秒区間の代表値を集計したp95**で、全frameのp95ではない。欠測・期限切れ・不正値は除外。`--since` で営業日/セッションを区切る。別PCのログを一緒に渡す場合は台名を重複させない。

現行ログは1MiB×5世代で、追加記録により長時間営業の先頭が回転で失われる。容量拡大または別の小さい日次集計ログは**提案のみ（未実装）**。一日の全区間を恒久保存する保証はない。

## P2P と未実装の提案

P2P bridgeは座標のみ転送する。新しいrelayは追加しない。PCにはloopback座標だけが届くので、この経路のスマホmetricsとRTTは**利用不可**。F8の欠測表示と `status=missing-phone` がその証拠。P2Pでも測るには、別versionの診断messageをbridgeまで渡し、実端末の識別と色を保って5007へ転送する必要がある。LAN probe RTTはP2P経路の遅延には使えず、P2P自身のping/replyも必要。bridge・iPhone双方のprotocol拡張になるため今回は実装しない。

真の発光までの遅延はこの仕組みだけでは得られない。数値を下げるためのカメラ・認識・vsync・queue・低電力設定変更も行っていない。F9は必要な時の光学的な比較試験として残る。

## 実行結果（このworktree）

- `check_pc_latency.py`: Unity 6000.3.9f1の実DLLでEditor Mac/Windows・Standalone Mac/Windowsの4構成compile PASS、Windows P/Invoke宣言compile PASS、実PerfLogのstandalone NUnit **20/20**。送信元切替・色別の異なる内訳・シーン切替中の10秒記録・毎frameの既存timing無割当も検証。
- `check_auto_latency.py --before-ref aacffa9`: 旧formatterの512文字切捨てを**FAILとして再現**、修正後5/5 PASS。旧応答Loopの未知datagram無返信・探索返信PASS。Swift純粋部3/3、実LANLivenessProbe→実C#受信Loopのloopback PASS（空きportのみ）。
- hostコスト: Swift formatter平均約6µs/件（1万件）、Kotlin formatter平均約14µs/件（1万件）、C# parse+store平均約8µs/件（1万件）。SwiftのsendMetrics enqueue単発約0.12ms、host loopback RTT約0.60ms（2標本）。端末上のCPU時間・無線・実送信完了のコストではない。診断は5秒ごとだけで、座標送信queueに追加しない。
- Python `test_auto_latency_contract test_ops_docs test_protocol_contracts test_latency_report test_predict_latency_eval`: **50/50 PASS**。新しい集計の3件は未実装時のERRORを先に確認した。Python3.9はnumpy未導入のため予測テストを実行できず、numpyのある既存miniconda Pythonで全対象を実行した。
- Swift `run_latency_host_tests.py`: 既存camera/DeviceHealth・deliveryのhostテストPASS（実認識部分とAVCapture setterのstubを使用、実カメラではない）。
- Android: 指定の `:app:testDebugUnitTest :app:assembleDebug :app:lintDebug` 成功。最終unit **76件、失敗/skip 0**、lint **error 0 / warning 27**、Debug APK生成。Kotlin daemonはsandboxのホーム書込み制限でfallbackし、以後は `-Pkotlin.compiler.execution.strategy=in-process` を付けて成功。
- iOS SDKでruntime全source（ContentView/PhoneSaberSenderAppを除く）を`swiftc -typecheck`、arm64 iOS17 / Swift5でPASS。全sourceのtypecheckはSwiftUI macroの `sandbox_apply: Operation not permitted` で失敗。Release archive/link/署名の確認ではない。Xcode projectは`plutil -lint` PASS。
- STAFF_MANUAL.pdf: 背面Chrome変換ではPDF生成失敗。GUIを使わないPyMuPDF Storyで既存HTML/CSSから再生成し、元の明示改ページを維持、2ページ・日本語追加文と全ページの描画を確認。`build_staff_manual_pdf.py --check` PASS。
- `git diff --check` PASS。既存.meta・scenes/prefabs/ProjectSettings・認識/座標生成/座標sender worker・formal corpusは差分なし。新規C# source/testの.metaだけ新GUIDを追加。コミット・pushなし。

## 検証とoperatorの残り作業

GUIを開かずに `check_pc_latency.py`（Unity実DLL4構成）、`check_auto_latency.py --before-ref aacffa9`（旧ログの切捨てFAIL→修正PASS、旧PC互換、共有fixture、Swift→実C# Loopのloopback）、Swift既存latency host tests、Python unit tests、Android unit tests/APK/lintを実行する。hostのCPU整形/受信/送信enqueue時間を各checkerが自動出力する。これは実機の負荷や無線遅延を証明しない。

operatorがレビュー・統合後に行うもの:

1. `PhoneSaber/tools/verify_phone_saber.sh`（公式XCTest、Detection、formal lossless 40/40、Python、iOS Release、Unity Edit/PlayMode）。この作業ではUnity/xcodebuild/Simulatorは起動していない。
2. 更新PC Playerとスマホアプリを実機へ配布し、Mac+iPhone LAN、Windows+Android LANで通常プレイ。F8表示・events.logの両色値をoperatorが確認する。ownerの計測操作は不要。
3. iPhone P2P fallbackで欠測表示、LAN復帰で自動回復、停止/端末変更で古い値が消えることを確認する。Low Power Modeの実display更新、projector固有処理、実端末での診断CPUコストは未測定。

コミット・pushはoperatorが公式verify後に行う。
