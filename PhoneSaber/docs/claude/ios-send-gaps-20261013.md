# iPhone 両色同時停止の調査（2026-10-13）

送信・経路の不具合は再現して修正した。ただし **16:15 UTC の実セッションの原因は未確定**。
iPhone 側の同時刻のログがなく、Unity の無受信だけではカメラ／処理／無線を区別できない。
認識の ranking / eligibility / scoring / PCA / threshold、座標 `x1,y1,x2,y2`、formal corpus、Unity は変更していない。
今回は commit / push と公式 verify をオペレーターに残す。

## 中断時の状態

最初に `git log --oneline -3` と `git status` を実行。HEAD は `755ba38`（wip marker）、作業ツリーは clean。
`440e818` と `b2d4a1a` に本件の未検証実装が残っていた。そこから継続し、途中の latency / metrics の別作業は戻していない。
以下では Swift の短いファイル名は `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/` 基準。
行番号は今回の最終ソース基準。旧処理は `440e818^`、追加診断の旧処理は開始時 HEAD と比較した。

## 所見（重大度順）

| 重大度 | 場所 | 具体的な故障条件と修正 |
|---|---|---|
| 高 | `CameraViewModel.swift:1636` → `:1644` → `:1508` | 旧 NWPathMonitor callback は、自動宛先では LAN を停止し `lanConfigured=false`、手動宛先では同じ IP の接続も再生成した。広告が再解決されるまで自動宛先がなくなる。P2P が使えなければ両色無送信。前回 WIP の「既知の宛先を保持し、Network.framework の接続回復に任せる」を維持・回帰検証した。広告の実際の removed / IP 変更処理は保持。 |
| 高 | `UDPSender.swift:108` | 同じ host / ports の configure が旧接続・active・pending を捨てた。経路通知などで繰り返すと準備をやり直し、両色が途切れる。同一集合なら handler の更新だけで接続・世代・最新 pending を保つ前回修正を維持。`updateHost` の同一 host guard (`:143`) も検証。 |
| 高 | `P2PSender.swift:786`, `:821` | route 選択時には usable でも、P2P キューでの送信／前送信の完了時に pong が失効することがある。旧 pending の transmit は usability を再検査せず、座標 send のエラーでも usable を保ったため、LAN があるのに失敗する P2P を使い続けた。送信直前の再検査＋fallback、送信エラー時の link teardown＋LAN fallback の前回修正を維持・実 loopback bridge と hook で回帰検証した。 |
| 中 | `CameraViewModel.swift:2216`（callback `:2224`） | P2P connected の通知だけで LAN の pending を破棄していた。LAN は Unity 応答ありで優先中でも、P2P の接続通知が届く。LAN の waiting / in-flight 後の最新座標を失う。今回この破棄を削除。実際に P2P を使い始める `CoordinateDelivery.route` (`UDPSender.swift:1090`) の破棄だけを残した。 |
| 中（診断） | `UDPSender.swift:773`, `:787`, `:795`, `:812` | timer が遅れ、送信復帰時に frame/result/fresh の時刻を先に更新すると、カメラ停止／無検出の実際の空白を `send error` と誤分類していた。送信停止操作の直前にも timer 未実行の空白が消えた。観測更新・停止より前の poll を追加。0.5秒超、停止区間につき1行を回帰検証。 |
| 低（検証） | `UDPSender.swift:113` | 前回の同一宛先guardがDEBUG用callback履歴の更新も省き、既存のbounded/absolute indexテストが失敗した。接続・世代を保ったまま履歴だけ最新16件へ更新。開始時HEADで既存テスト4 assertion failures、修正後PASS。 |

上記はコードとテストで確定した故障であり、今回の実セッションで発生したという断定ではない。
通知だけの pending 破棄は単発の座標消失であり、それだけで毎回1秒停止するとは限らない。

## Unity の時系列をどう読むか

`input-silent` は最後の datagram から既に1秒以上経った通知なので、silent→back の長さは停止全長ではない。
通知の polling 誤差を除くと、赤の停止は少なくとも次の長さになる。青もほぼ一致。

| silent (UTC) | back (UTC) | 無受信時間の下限（約） |
|---|---|---|
| 16:15:40.316 | 16:15:43.578 | 4.262 秒 |
| 16:15:45.571 | 16:15:45.668 | 1.097 秒 |
| 16:15:47.721 | 16:15:48.883 | 2.162 秒 |
| 16:15:49.884 | 16:15:50.107 | 1.223 秒 |

赤／青の送信は同じ frame の onResult から連続 enqueue されるため、上流の共通停止は受信再開も数 ms 内に揃い得る。
同時復帰は共通経路の強い手掛かりだが、両色の認識消失（遮蔽、照明、画角）も除外はできない。

## 両色を止める全経路の棚卸し

時間はコードで決まる待機時間と、実機依存で上限が決まらない時間を区別している。実機の session 起動時間は未測定。

| 機構 / 正確な入口→出口 | 停止時間 | 両色への同時作用 / 今回との適合 |
|---|---|---|
| カメラ中断 `CameraViewModel.swift:1158` / runtime error `:1177` → recovery `:1195` → sessionRunner stop `:1221` → configure/start `:1688` / `:1831`。lifecycle `:293`, stall `:331` | OS 中断時間＋再起動。通常 stall は最後の frame から2秒、watchdog 250ms (`:1275`)、初回 backoff 1秒、続いて2/4/8/16/30秒 (`EventRecovery.swift:35`)。15分後も60秒で再試行。10秒の連続正常 frame で backoff reset (`:46`) | 同一 session なので両色共通。無通知 stall からの初回復旧は概ね3秒以上＋起動時間で、最初の4.26秒と整合。後半の1.10秒は「2秒 stall を待った復旧」だけでは説明しにくい。interruptionEnded / runtimeError 直結の復旧なら1秒＋起動で可能。 |
| 熱 fps policy `CameraViewModel.swift:1303` / `:1351` → `:1590` → restart `:1575`。`DeviceHealth.swift:182` | serious/critical で60/120→30、nominal/fair の連続120秒と変更間隔120秒で戻す。stopSynchronously (`CameraViewModel.swift:427`) → stop/start/reset/configure で実機依存の空白。120fps は熱による降格を優先する例外あり | 両色共通。1回の停止には合う。通常60fpsの熱切替だけで数秒間隔の4回の往復には合わない。policy は変更していない。 |
| FPS 自動比較 `CameraViewModel.swift:1600` → `:1622` → select `:1560` → restart `:1575`。`EventRecovery.swift:74` | DEBUG の明示 ON 時だけ30秒ごとに30/60または60/120を切替、5分で自動 OFF・保存設定へ戻す。そのたび stop/start（時間は実機依存） | 両色共通。通常 Release では経路自体がない。数秒間隔の burst は30秒周期と合わない。設定変更で比較 task が再作成されれば追加の切替はあり得るが、セッション設定不明。 |
| route 選択 `UDPSender.swift:1077`。LAN probe `:572`, `:599`, `:664`; P2P usable `P2PSender.swift:381`, `:767`; hysteresis `P2PProtocol.swift:173` | LAN は500ms周期、最後の対応応答から1.5秒以内なら verified。失効時、usable P2P → 未確認 LAN → 宛先なしの順。P2P 初回は最初の pong、失効後は500ms以内間隔の3 pong（通常約500ms）で再使用。pongなし1.5秒で使用停止、4秒で再接続、250ms～4秒 backoff | 同じ route 状態なのでほぼ共通。ただし色ごとに選択するため変化の境界では分かれ得る。1.5秒は座標を待たせる時間ではない。LAN は未確認でも送る。flap＋送信の詰まりなら今回の秒単位 burst に合う。P2Pを実際に使ったかは phone ログが必要。 |
| P2P 探索・interface交代・定期復旧 `P2PSender.swift:497`, `:576`, `:619`, `:434` | browser failed は2秒後、waiting は10秒後に探索を再生成。service未発見は無期限。usable中は探索を止め、広告消失だけでは生きた接続を捨てない。無応答1秒＋別interface発見なら再接続を試すが、再切替間隔はconnect timeout 4秒以上。foregroundと30秒refreshのrecoverは、usableならpingのみ、そうでなければbackoffをresetして接続を作り直す | link全体なので共通。LANの宛先があればその間LANへ送るため、探索待ちだけが両色停止の原因にはならない。P2Pしかない／LANが実際に届かない場合は共通空白。数秒burstと整合する余地あり。OS path通知だけではP2P connectionを再生成しない（`:655`はラベル/usability更新）。 |
| periodic Bonjour `CameraViewModel.swift:1291` → refresh `:1644` → Bonjour start `:2897` → resolve callback `:2952` → receive `:1508` / apply `:45` | 30秒 refresh、resolve timeout 5秒、探索の「未発見」表示は10秒。変更なしの解決は接続も generation も変更しない。変更 IP は `UDPSender.updateHost:141` で両接続を新規作成。selected service removed (`CameraViewModel.swift:1511`) は LAN を停止し再発見待ち（上限なし） | 同一 host の定期 refresh 単独では空白0。旧 path callback が LAN を捨てた場合は resolve待ちが共通空白になる。30秒周期だけでは後半の数秒 burst は説明できないが、path / 広告変化なら可能。 |
| generation / isCurrent `CameraViewModel.swift:1848`, `:1855`; `UDPSender.swift:1056`, `:1072`, `:1084`, `:165`, `:327`; `P2PSender.swift:464`, `:787` | processor.reset (`FrameProcessor.swift:188`) と start/stop の lifecycle 更新で旧 frame・待機 send・fallback を拒否。新世代の fresh frame が来るまで。reset は detection queue の終了を同期で待つため、処理 stall 時はその時間も加算 | 共通世代なので両色を拒否。同じ設定の `updateDeliverySettings` は世代を増やさず、`matches` は running と processor/lifecycle 世代だけ。通常の Bonjour/hostSelection didSet や `lanConfigured` 更新では generation は増えない。再起動を伴えば共通停止と整合。 |
| stale / freshness `FrameProcessor.swift:673` → `CameraViewModel.swift:1878` | missing の最初3 frame は従来の予測が fresh、それ以後の保持点（hold 180ms）は preview のみで送信しない (`FrameProcessor.swift:695`)。最新フレーム mailbox `:262` は待機旧 frame を置換する。結果の経過時間そのものを1秒で落とす規則はない | 両色に fresh がなくなれば、再検出するまで無送信。両色共通遮蔽・照明変化でも同時停止／復帰し得る。今回のログだけでは除外不可。認識と予測ルールは無変更。 |
| UDP 接続の waiting / failed / reconnect `UDPSender.swift:178`, `:237`, `:293`, watchdog `:383`, recover `:431` | waiting timeout 8秒、未完了send watchdog 2秒→再接続 backoff 500ms～8秒、ready 後は最新 pending を送る。NWConnection は path を追う。以前は path callback が接続を捨てていたが今は recoverIfNeeded のみ | ポートごとの処理なので単独なら完全同期とは限らない。共通の Wi-Fi / host 変化は両方を同時 waiting にする。2秒＋再接続なら2.16秒以上も可能。ただし今回のログから waiting やwatchdogを証明できない。 |
| UDP / P2P active と pending 置換 `UDPSender.swift:160`, `:315`, `:402`; `P2PSender.swift:456`, `:786`, `:845` | 色ごとにactive 1＋pending最新1。置換自体は待ちを増やさない。active の completion が来ないと UDP は2秒、P2P は3秒 watchdog までその色を送れない。teardown は旧 pending を破棄し、次の frame で現在の routeへ | 両色の OS completion が詰まれば共通無送信。pending を貯めて順次再生する実装ではない。今回に合う可能性あり。実機依存のNetwork completionの時間はログで確認する。 |
| detection queue の占有 `FrameProcessor.swift:81`, submit/drain `:262`/`:291`, process `:493` | CVPixelBuffer lock/access、`analyzeSabers`/`detectSabers` (`:526`/`:547`)、result callback (`:721`)、healthMeter callback (`:589`)、recording append (`:611`)、raw PNG save (`:656`) は同じ直列 queue。1処理が1秒かかれば後続をその間処理できない。mailbox は1枠に圧縮する | 共通。recording は追加解析・BGRA保持・metadata書込み・AVAssetWriter append（`DebugVideoRecorder.swift:1660`）がある。finish のmetadata確定 `:2267` も一部同期、画像export本体は `:2364` detachedで別。録画／raw保存なしでも重い検出やOSのbuffer待ちは可能。MainActorからのreset/configureThresholdsはこのqueueを待つが、通常の送信はMainActorを待たない。今回に合い得るが phone側処理時間の証拠なし。 |
| 自動露出・frame duration `CameraViewModel.swift:1756`, `:1769`, `:2261` | 通常の format 設定で min/max duration を固定。既定auto露出はdeviceを変更しない (`:2269`)。手動の exposure experiment はlockしてactiveMaxExposureDurationを設定、session stop/startはしない。既定ではOSのauto frame rate / low light / HDR等を強制変更しない | 同じカメラなので両色の明るさ／認識が一緒に変わる。通常60fpsの1 frameは約16.7msで、設定だけから秒単位無frameは導けない。OSの熱・圧力・割込みや露出変動でfreshが消える可能性は残る。既定露出・後処理・fpsを勝手に変更していない。 |
| 無線／OS suspension | ローカルsend successはPC受信保証ではない。Wi-Fiの損失・経路切替・background suspensionの空白時間はコードでは上限なし | 共通ネットワークなら両色同期と整合。phoneがfreshを処理・send successし続けたのにUnityだけsilentなら、この領域を優先して調べる。 |

定期的な設定更新は、watchdog 250ms、端末health 1秒、metrics 5秒、CAPTURE_METRICS 10秒、Bonjour30秒、DEBUG比較30秒。
healthによるthermal切替とDEBUG比較だけが内部stop/startを起こす。health metadata / metrics / UI 12Hz は世代更新を起こさない。
Bonjourの status-only / 同一IP解決では `configureLAN` を呼ばない。実host変更・初めてのLAN設定・removed後の復帰時だけLANを設定する。

## 次回は phone ログを Mac で読む

`SendGapMonitor` (`UDPSender.swift:741`) は camera / detection / UDP / MainActor と独立した utility timer（100ms）を使う。
送信の意思が ON で最後のローカル成功送信から **0.5秒超**空くと `SEND_GAP` を1行だけ記録し、送信再開で次の区間をarmする。
カメラstop/start内部でも意思を維持する。timerが遅れてもframe/result更新・送信再開・ユーザー停止の直前に判定する。
frame / result / fresh / processingのageとcamera interruption / recovery / reconfigure contextも同じ行に入る。

原因の順序は上流から `camera delivered no frame` → `processing stalled` → `no fresh detection for either colour` →
`route had no destination` / `send error` / `generation rejected`。新鮮な検出があるのに完了がない場合は `send error` と未確認完了を記す。
これはその時点のアプリの観測分類であり、OS内部の根本原因の断定ではない。全原因の重なりはage/context/detailで読む。
このmonitorは**両色を合算した完全無送信**を対象とし、片色だけの喪失検出ではない。

生成済みの Debug Recording triage bundle に、UTCの任意テキスト `connectivity.log` を1つ追加する
（`DebugRecordingTriage.swift:1016`、`DebugBundleTransfer.swift:560`）。現行と回転前のログから最大64KiB、UTF-8の行の途中を除去して読む。
PSBT v1、既存summary、PNG/context、座標datagramはそのまま。receiverの許可パス／個別size／最大43filesだけを拡張。
Python `phone_saber_triage_protocol.py:55` / `:192`、`phone_saber_triage_codex.py:447` が任意ファイルを許可し、
`phone_saber_session_report.py:433` / `:690` がUTCログを掲載する。欠落は旧bundleとして n/a。symlink / traversal / 64KiB超は拒否。
32KB preflight上限は維持。接続ログを認識・repair gateや画像数に使わない。

次の停止後、Debug Recordingを終了し「Mac受信済み」を確認。Macのinboxのbundle内 `connectivity.log`、または隣の `.report.md` を開き、
UnityのUTCと `SEND_GAP` / `PATH` / `経路` / `CAPTURE_MODE` / `CAPTURE_METRICS` を照合する。ケーブル不要。
録画終了・bundle生成時の直近tailなので、長く待つと停止行が範囲外になる。旧receiverは新ファイルを拒否するため、phoneとreceiverを両方更新する。

## 実行した検証

全コマンドはGUI起動なし。swiftcの実ソースとXCTest、loopback UDP/TCP、synthetic bundleで検証した。

- `python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_send_gap_host_tests.py`: **17/17 PASS**。
  実 UDPSender / P2PSender / liveness / routing / ConnectivityLog / package関数。カメラ、UI、Bonjour探索だけstub。
  CameraViewModelの宛先／path／P2P callback関数本体と既存callback履歴テストも実ソースを抽出。stubの固定processor世代だけではiOS lifecycle全体は検証できない。
- 同コマンド `--revision HEAD`: **17件、10 assertion failures**（開始時WIPの診断取りこぼし、LAN pending破棄、callback履歴の回帰）。
- 同コマンド `--before-gap-fixes`: **17件実行、22 assertion failures**（期待した失敗）。
  `440e818^` のconfigure / transmit / finish / callback / packageを現行のsupportに戻す比較。
  旧inline path callbackは同じ本体を現行名の関数に接続。旧SendGapMonitorは開始時HEAD。テスト対象の旧処理は書き換えていない。
  同一configure、pathの両色消失、P2P error、pending失効、connected通知、診断復帰/停止、任意ログ同梱が修正前に失敗。
- `python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py`: **27/27 + routing 6/6 PASS**。
  FrameProcessor / default camera setter / 30,60,120fps policy / DeviceHealthの既存回帰。録画export参照の追加に合わせ、host用ConnectivityLog stubを補った。
- Toolsディレクトリで `python3 -B -m unittest -v test_phone_saber_send_gaps test_phone_saber_triage_protocol test_phone_saber_triage_codex test_phone_saber_session_report`: **77/77 PASS**。
  HTTP receiverへの実upload、旧bundle、新logのbyte保持、20画像＋log、limits/unsafe paths、report、既存実Swift uploaderも含む。
  一度だけ既存 `test_fragmented_duplicate_receipts_and_old_receiver_statuses` が failed を返した。単独1/1再実行と全体77/77再実行はPASS。
  この非決定的な失敗を本件の修正で解消したとは扱わない。
- Python新規4件を一時ディレクトリにコピーし、protocol / codex input / reportを `b2d4a1a^` へ戻した比較:
  **旧実装で3 error**（connectivity.log拒否、report項目欠落）。安全性の拒否テスト1件は旧実装でもPASS。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: **Static BGRA detection PASS / DeviceHealth 13/13 PASS**。
  これはformal corpus 40/40とは別のsynthetic回帰。
- `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -sdk iphoneos -destination 'generic/platform=iOS' -derivedDataPath /tmp/phonesaber-send-gaps-release CODE_SIGNING_ALLOWED=NO 'OTHER_SWIFT_FLAGS=$(inherited) -Xfrontend -disable-sandbox' build`: **BUILD SUCCEEDED**。
  最初はmacro pluginのmalformed responseで失敗。repoの既存iOS検証記録と同じcompiler flagを使って成功した。アプリ起動／インストールなし。
- 同project/scheme、`-configuration Debug -destination 'generic/platform=iOS Simulator' -derivedDataPath /tmp/phonesaber-send-gaps-tests`、同CODE_SIGNING/OTHER_SWIFT_FLAGSで `build-for-testing`: **TEST BUILD SUCCEEDED**。Simulatorサービス警告あり。Simulator.appは開いていない。
- `git diff --check`: **PASS**。

実行ログは `/tmp/phonesaber-send-gaps.log`, `/tmp/phonesaber-send-gaps-before-wip.log`, `/tmp/phonesaber-latency-regression.log`,
`/tmp/phonesaber-gap-python-final.log`, `/tmp/phonesaber-gap-python-before.log`, `/tmp/phonesaber-gaps-static.log`,
`/tmp/phonesaber-send-gaps-release.log`, `/tmp/phonesaber-send-gaps-test-build.log`。生成バイナリ／録画はcommit対象にしていない。

## オペレーターの残検証・リスク

- 公式 `PhoneSaber/tools/verify_phone_saber.sh`、formal lossless 40/40は今回未実行。corpusは無変更。
- iOS targetでのXCTestはbuildのみ。`P2PTransportTests`（新規4件、既存LAN/P2P復帰・pending・lifecycle）、
  `CoordinateDeliveryTests`、`SendGapTests`、`DebugBundleLogTests`、`DebugRecordingTriageTests`、`DetectionCoreTests`、
  `EventRecoveryTests`、`DeviceHealthTests` を実行する。host stubで通ったものもiOS targetで確認する。
- 実iPhoneのcamera interruption、熱でのstop/start、Wi-Fi path change、background復帰、長時間処理、Macまでの実LAN/P2P無線は未実行。
  GUI禁止のためUnity/Player/Simulatorを起動していない。iPhoneの署名・インストール・実録画bundleの無線転送も未確認。
- UDP contentProcessed成功はPC到着を保証しない。phoneログにgapが無いことだけで無線正常とは言えない。
  OSがアプリを実行しない間はtimerも止まる。復帰時に履歴を記録するが、プロセスが殺された場合はその間の原因を取れない。
- route flapは次のfresh frameで現在の宛先へ送る。LAN/P2Pとも物理的に届かない場合まで救済できない。
  watchdog前の2秒/3秒待ちや、再接続時の旧pending破棄は従来の方針を保持。
- 新diagnosticsは短いlockと100ms pollを加える。実機で負荷・ログ保持範囲と時刻同期を確認する必要がある。

挙動が見える変更は提案だけ: FPS比較を本番で使わない運用の明確化、sessionを止めないfps再設定方式、camera自動後処理／露出の既定変更、
watchdog短縮、route hysteresis変更、無線不調時の別経路。これらは停止時間・画質・認識・誤座標のトレードオフがあるため実装していない。
先に新phoneログを付けた同条件の実機sessionを1本取得し、どの段階が止まったか決める。
