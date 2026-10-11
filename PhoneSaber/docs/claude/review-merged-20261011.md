# 2026-10-10〜11 main 統合分の再レビュー

2026-10-11、Codex。中断された作業を 614108c（wip）から継続した。commit / push は行っていない。認識、UDP 座標形式、正式 corpus、Unity scene / prefab / .meta / ProjectSettings は変更していない。

**起動・ログ保全・検証結果の誤判定は修正した。ただし「不具合ゼロ」とは判定できない。送信者ゲートと経路切替の相互作用、P2P での送信者識別、stale lock 回収競合が残る。** 下記で「修正済み」「仕様変更の提案」「実機未確認」を区別する。P1 は入力停止・二重起動・データ喪失・検証の偽成功、P2 は診断欠落・誤誘導を主な目安とした。

## 対象と進め方

最初に git log --oneline -3 / git status、PhoneSaber/CLAUDE.md / AGENTS.md を読んだ。開始時の未 commit 差分はなし。wip の10ファイルをレビューして保持し、その不完全な終了コード処理を修正した。

指定された git log --since=2026-10-10 --no-merges --format='%h %s' -- PhoneSaber Assets/Scripts/Managers/Inputsystem Assets/Tests/Editor/PhoneSaber* Assets/Editor/PhoneSaber* を実行し、git show と現行コードを照合した。日付だけの --since は実行時刻を補い10日早朝を落としたため、JST の --since=2026-10-10T00:00:00+09:00 でも確認した。この範囲は main 由来37 commit とレビュー自身の wip 1 commit。

| 対象 | commit と主な確認点 |
|---|---|
| iOS 開始・停止・LAN 生存確認 | 5680fa9：世代照合、停止直後の再開、IP 更新、受信 error の再接続 |
| iOS P2P / 警告 / ログ | bca4177, 8a86b0a, 0d8e411：interface rotation、usable になる前の reset、manual IP、P2P-only、stop 後の警告消去 |
| iOS pending 座標 | 51e4818：ready が in-flight send と重なる場合の最新座標保持、古い callback の無効化 |
| 診断 retention / ガイド / reserve | f98c396, f4f2700：停止境界の選択、capacity=1/2・60fps、6 capture、画像枚数/byte予算、重複、receiver の検証 |
| Unity 受信 / 運営キー | 32821c4, 6a5cab6, f916832：停止・再 bind、非有限値、sender rate limit / gate、F7 cancel、F9 queue restore / OnDisable |
| F9 / frame-cost | 0d7d0cd, 8c10cdd：ブロック数と条件、時計、計測境界、終了時の復元 |
| Android | 86a350c, 7e2dd5a：IPv6/DHCP 通知、PC の IP 照合、camera error、selected PC の reply watch、Debug/Release と fixture APK |
| P2P bridge | 903e4ba：送信完了を待つ relay、half-close、同時 upload、cache の同時 build / digest / 中断 / live pid |
| Mac launcher | b41ae3f, cff22c5：bridge env、STOP、正常終了 marker、二重起動、stale lock、log rotation |
| Windows launcher / firewall | d3d4b58：Player block rule、timeout 失敗時の ping 待機、STOP と stdin redirect |
| Python 検証・解析 | 45e8ecf, 9494244, d9d861c, 5c97af6, a9a1e53：空 XML、crash、skip、--since、idle upload、replay の壊れた JSON、soak、検証件数 |
| offline analysis | db10f03：production への接続がないこと、core-line observer / PNG parity の扱い |
| 文書のみ | 67a67fc, 5d8e49e, dd0ba3c, 4c6ac01, 04fa9c9, 41b79e9, ce06ed7, b88fa85, 4d33b58, e6a4e1c, b8b08ec, 27fbcd5, 4ae0848：実機済みと未確認の区別、launcher / LAN 優先との一致 |

## 所見（重大度順）

### P1・未修正：同じ iPhone の経路往復を2台競合と判定する

Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberInputStats.cs:24, :38。

実コードの standalone trace で、LAN IP @0.0s → 127.0.0.1 @0.1s → LAN IP @0.2s → 127.0.0.1 @0.3s を投入すると、最後は accepted=False、locked=LAN IP、rejected=1 となった。スマホが1台でも、5007 応答の途絶と復帰により LAN/P2P が往復すると成立する。最後に残った P2P は、固定された LAN の最終到着から1秒まで拒否される。単方向の切替だけを検証する既存テストはこのケースを通らない。

提案：端末と経路を別に識別する設計、または明示的な受信元選択を決める。IP の例外扱いだけでは、LAN の別端末と P2P の同一端末を区別できない。現在の競合判定・許可方針を変えるため実装していない。実機では5007だけを一時的に遮断して往復させ、sender-conflict、reject 数、実際の剣停止時間を同時に確認する。

### P1・未修正：不正座標だけの送信元が正常なスマホを締め出せる

Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberInputStats.cs:212。

parsed を判定する前に senderGate.Accept を呼ぶ。不正A @0.0s → 正常B @0.01s → 不正A @0.02s で A に固定され、正常B @0.03s は拒否される。不正Aが0.9sにも届けば B @1.02s も拒否されることを実コードで確認した。非有限値を座標へ反映しない修正は有効だが、それだけでは正常な送信元を保護しない。

提案：不正 payload は到着統計に残しつつ、所有権の取得・延長に使用するかを別に決める。既存の PhoneSaberSenderGateTests.InvalidPacketsFromKeptSenderAlsoRefreshLockLikeArrivalSilence が現在の延長を明示的に要求しているため、その契約は黙って変更していない。期待を逆にした一時テストも失敗した。保存した trace は正常送信元の拒否を確認する再現であり、修正後の合格を意味しない。

### P1・未修正：2台の P2P スマホには sender gate が効かない

PhoneSaber/ios/PhoneSaberSender/Tools/p2p_bridge/PhoneSaberP2PBridge.swift:71, :93 と PhoneSaberInputStats.cs:24。

bridge は全 peer の payload を1つの UDP socket から転送する。実 Swift bridge に異なる2 socket / session の赤座標を送り、Unity 側の recvfrom で全件が同じ 127.0.0.1:56199 から来ることを確認した（port は今回の自動割当値）。同じ色に2台が送ると座標は混ざり、gate は同じ送信者として全部受理する。UDP の source port を追加で見るだけでも解消しない。

さらに、UDPSender.swift:861 の LAN 応答、P2P の pong、iOS/Android の「PC応答あり」は、Unity がその端末の座標を gate で受理した証拠ではない。通信警告なしで剣の拒否・混在が起き得る。提案：payload を維持したまま bridge 内で peer の所有権を選ぶ案、または別の受理確認経路を設計する。2台目を拒否する新しい挙動になるため実装していない。

### P1・未修正：stale lock を2つの launcher が同時回収すると二重起動する

PhoneSaber/mac/saber-watchdog.sh:55, :56, :75。

両者が古い PID を持ち主なしと判断 → A が回収・再取得 → B が A の pid と directory を削除・再取得、という interleaving が可能。実スクリプトを一時コピーし、stale 判定直後の待合せと fake open だけを追加した再現で、A/B とも起動、simultaneous launches: 2 を確認した。cleanup も所有 PID を再確認せず、新しい持ち主の lock を削除し得る。別の kit copy では LOCK_DIR 自体も別になる。

提案：stale 削除を伴わない OS の保持型ロック（macOS の lockf 等）を、既存 Player 検出・終了時の signal と合わせて検証する。ロック方式と起動プロセス構成を変えるため実装していない。再現では game_running の処理は本番のままだが、fake open は実 Player を起動しない。実機の起動速度によって競合が隠れる場合はある。

### P1・修正済み：ConnectivityLog rotation が directory を再帰削除する

PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/UDPSender.swift:737。

ログが上限に達し、世代先 connectivity.log.1 が directory だった場合、旧 removeItem が内部の別記録まで削除する。directory を検出して rotation を見送り、現行に追記する最小修正を wip から保持した。

回帰：P2PTransportTests.swift:206、testConnectivityLogRotationDoesNotDeleteADirectoryAtTheGenerationPath。a9a1e53 の実装では失敗、現行は Debug / Release 条件の両方で合格。障害を直すまで現行ログの上限超過を許容する。

### P1・修正済み：CDPATH が Mac 起動パスを壊す

PhoneSaber/mac/Start-Saber-A.command:3、Start-Saber-B.command:3、saber-watchdog.sh:4, :24。

CDPATH=. を export して相対パスで起動すると、cd が出す directory 名と pwd が command substitution に両方入り、改行を含む SCRIPT_DIR になる。ゲームを起動できない。入口3箇所の CDPATH='' は wip から保持した。再開後、相対 PHONESABER_P2P_BRIDGE_SCRIPT を正規化するもう1箇所にも同じ問題を再現・修正した。

回帰：tools/test_mac_watchdog.py の relative_entrypoints、relative_watchdog、relative_bridge_override。wip 前の入口は失敗、追加の bridge ケースは wip HEAD で失敗、現行は合格。日本語・空白・括弧のある一時 directory、macOS /bin/bash、fake open で実行し、open --env に正しい絶対パスが1引数で渡ることまで確認した。

### P1・修正済み：Unity が crash しても PASS XML だけで成功になる

PhoneSaber/tools/unity_playmode_batches.py:84, :140。

Unity が合格 XML を書いた後に異常終了すると、旧 run_group は終了コードを捨てて成功を返す。skipped / inconclusive が残っていても failed=0 なら CLI は成功した。wip の修正を保持し、終了コードと全件合格の両方を要求する。

再開後には wip の副作用も修正：Unity Test Runner の正規の失敗終了2まで crash と分類して再試行し、失敗名を失っていた。実ローカル Test Framework の Executer.cs（Ok=0 / Failed=2 / RunError=3）を確認し、exit=2 かつ failed>0 の有効 XML はテスト失敗として保持する。crash を成功へ緩和していない。

回帰：crash_after_writing_passed_xml、skipped_or_inconclusive、test_runner_exit_two_preserves_the_failed_test_without_retry。前2件は a9a1e53 で失敗、終了2のケースは wip HEAD で失敗。現行の11テストは全合格。公式 verify 自体は operator 担当で、実行していない。

### P2・未修正：guided reserve と byte 予算で tracking の peak が落ちる

PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugRecordingTriage.swift:728, :821。

reserve の5枚は bridge の byte 消費から保護されるが、tracking window は枚数で計画した後、byte予算で1枚ずつ skip する。既存の largeImages 回帰は tracking を無効化しており、この組合せを通らない。一時コピーした既存の guidedSelection helper で tracking を有効にし、既存テストと同じ方法（sparse file）で manual / tracking 各画像を8MiBにした。実 selectImages は manual 5枚を確保する一方、tracking は frame 104 と108の2枚だけとなり、peak 105を失った。peak保持を要求する一時XCTestが失敗した。

trackingWindow.selected は実枚数2に更新されており、予定枚数の虚偽報告はない。しかし「peakを含む連続窓」の契約は満たさない。この試験は画像サイズに対する選択の再現で、実PNGのdecodeや実録画ではない。VGAの通常サイズでは再現していない。

提案：tracking unit 全体のbyte予算を先に確認し、guidedとの優先順位を決める。収まらなければ明示的に不足を記録する。画像選択の挙動を変えるため実装していない。既存の64MiB transport / 32KB preflight / メモリ枠は緩和していない。

### P2・未修正：frame-cost の receive-to-sample が負になり得る

Assets/Editor/PhoneSaberFrameCost.cs:326, :350。

sample と呼ぶ時刻は Update の開始 marker。InputPoint が値を取り込む時刻ではない。Update開始10.000s → UDP受信10.001s → InputPoint.Update が採用 → PostLateUpdate10.004s の場合、式は receive-to-sample=-1ms、sample-to-render=4ms を出す。receive-to-render=3ms 自体は同一単調時計の CPU 境界の値で、GPU/投影の遅延ではない。

提案：InputPoint の実際の取り込み点を timestamp 化する、または metric 名・説明を Update-start に合わせる。計測値の意味を変えるので今回は実装していない。これは式と PlayerLoop 順からの静的な証拠であり、実 Editor の負値を観測した結果ではない。

### P2・修正済み：rate limit が最後の経路切替を永久に隠す

Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberEventLog.cs:155, :200。

初回 LAN を記録、1秒以内に P2P へ切替、そのまま P2P が安定すると、旧実装は次の切替まで抑制件数を出さない。LAN→P2P の本当の出来事が記録されない。wip の Poll / Packet による抑制分の flush を保持し、1秒1行の上限を保った。

回帰：Assets/Tests/Editor/PhoneSaberEventLogTests.cs の FinalSuppressedRouteChangeIsLoggedEvenWithoutAnotherChange。a9a1e53 の実コードは expected 2 / actual 1 で失敗、現行は合格。定常 packet/parser/statistics/event polling と gate の割当0テストも standalone で通過した。

### P2・修正済み：Mac rotation が世代 directory 内の同名記録を上書きする

PhoneSaber/mac/saber-watchdog.sh:82。

1MiB超の Start-Saber-A.log と、directory の Start-Saber-A.log.1/Start-Saber-A.log がある場合、mv -f は directory 内へ移動し、古い記録を上書きする。世代先が directory なら rotation を見送り追記する。test_rotation_does_not_move_a_log_into_a_generation_directory は wip HEAD で失敗し、現行で合格。正常な世代 rotation は既存回帰でも合格。directory 障害中は現行ファイルの上限超過を許容する。

## 未確定・動作変更としての提案

- **retention と後半 capture**：DebugVideoRecorder.swift:2853。時間窓内の中間候補を残す改善は60fps syntheticで通った。一方、guided manual capture も motionRetainedBytes と256MiB枠を共有する。高解像度 fallback、増加する anomaly、全色＋bridge の状況で後半の6 captureが成功するかは実機未確認。メモリ枠は緩和していない。min available memory / peak retained bytes / 各 capture の成否を確認する。
- **F9 の「十分」**：PhoneSaberLatencyProbe.cs:90 は8 blockだけを数える。fps自動比較がOFF、あるいは30fps fallbackのままでも十分になる。4条件が各2 block揃った証拠ではない。latency_report の条件別件数を併用し、UI文言または条件別検証を追加する案を提案する。F9終了・OnDisable時の元 maxQueuedFrames 復元ロジックは pure NUnit で合格した。
- **手動IPの表示**：CameraViewModel.swift:954 は unused な P2P の connected を参照できるが、CoordinateDelivery.route は manual のとき P2P を使わない。手動IPでLAN応答なし、P2Pだけconnectedの状態で見出しがP2Pにならないかを実機確認する。警告のmanual分岐自体はホストテストで合格。経路表示と見出しを一致させる案を提案する。
- **main thread の残存待機**：PhoneSaberBonjourPublisher.cs:42, :139。InputPoint の networkLifecycleLock を外した変更は妥当だが、IsPublishing は Process.Kill / WaitForExit(1000) と同じ Gate を取る。socket喪失とF8サービス表示が重なると停止待ちへ巻き込まれる可能性がある。既存のnull publisherテストだけでは判断できない。実 dns-sd の停止を遅らせて計測する。非同期 snapshot 化は提案のみ。
- **Windows**：既存の二重起動防止・ハング検知不足は今回の wait修正では解消しない。Playerの名前/パス、Public hotspot、既存Block rule、stdin redirect、STOP残存/待機途中、正常Quitと強制終了を実PCで確認する。PC単位lock/heartbeatの追加は提案のみ。
- **P2P interface rotation**：ホストloopbackでAWDL/en0の実切替は代用できない。同じserviceの複数interface、MacのSSID移動、iPhone前面復帰、bridge再起動、2 pongに達する前の繰返し切断、手動IP残存を両ビルドで確認し、座標再開時間とconnectivity logを突き合わせる。connected / ready だけで再開済みと判断しない。

## 実行した検証

ログと一時harnessは /private/tmp/merged-review-20261011/ および /private/tmp/phonesaber-receiver-check/ に置いた。stub は P2Pを使わないUDP/LAN試験、UIKit依存のpure型、Unity native API等に限定しており、実機試験の代用とは扱わない。

| 実行 | 結果と範囲 |
|---|---|
| python3.12 -B -m unittest discover -s PhoneSaber/tools -p 'test_*.py' | **123件合格**。bash fake open、--since、PlayMode runner、soak集計等。resume-tools-final.log |
| python-env/bin/python -B -m unittest discover -s PhoneSaber -p 'test_*.py' | **85件合格**。root Python / camera tools。resume-python-root.log |
| python3.12 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p 'test_*.py' | **402件実行、skip 1、setUpClass error 1**。TrackingPipelineE2ETestsがdisk capacity=0で録画開始を拒否し、そのクラスは未実行。他の実行テストに失敗なし。resume-ios-tools.log |
| 同 directory の test_phone_saber_p2p_bridge.py 単独 | **27件合格**。実Swift bridge / Network.framework、loopback UDP/TCP、backpressure、同時upload、restart、cache競合/中断。resume-bridge-only.log |
| python3.12 .../run_lossless_regression.py | **正式40/40合格**、positive 23/23、negative FP 0/17。35 unique PNG。corpus/期待値を変更せず実Swift検出器で実行。resume-lossless.log |
| run_swift_host.py after / release | swiftc -O、Debug定義あり/なしで**各33件合格**。実UDPSender/LANLivenessProbe、pending、camera lifecycleのpure型、PC reply watch、ConnectivityLog、device health。P2PSenderはstub。resume-swift-debug.log / resume-swift-release.log |
| phonesaber-contract-swift-tests.py | 実UDPSender / P2PSender / P2PProtocol / CoordinateDelivery の standalone XCTest **6件合格**。resume-routing.log |
| run_p2p_host.py | **18件合格**。実P2PSender / P2PProtocol、interface rotation / backoff / liveness等のpure型、fake loopback bridgeとの配送・再接続・最新値・send watchdog・前面復帰。CameraViewModelは含めず、AWDL自体は未確認。resume-p2p-host.log |
| bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh | static BGRA checksとDeviceHealth XCTest **6件合格**。resume-static.log |
| retention / triage / guided host XCTest | **87件実行、83合格、3失敗、1skip**。3失敗はguided/manual recorderの空き容量API=0による開始拒否。skipはmacOSの録画保持統合テスト。pure retention / guide / reserveは合格。resume-triage.log |
| standalone NUnit + Unity付属Mono/Roslyn | **90ケース合格、socket作成1件skip**。実EventLog/InputStats/Parser/SenderGate、実LatencyLoop/queue restore/ProgressText、既存NUnitテスト。native GameObjectが必要なoverlay/world mapping試験は含めない。resume-nunit-expanded.log |
| Unity API compile check | **PhoneSaber*.cs 17ファイル＋InputPoint.cs、変更したEditorテスト2ファイル合格**。Unity6000.3.9f1の実engine/editor/InputSystem/NUnit参照。無関係のSwing clock/IMUだけstub。resume-unity-compile.log |
| Unity Editor EditMode（project copy） | **未完了**。Unity6000.3.9f1をbatchmode / nographics / testFilter PhoneSaberで起動。readonly database警告とIL Post ProcessorのUnix socket接続失敗を繰り返し、180秒で終了させた。結果XMLなし。resume-unity-launch.log / resume-unity-editor.log |
| Android Gradle | **56 unit合格、lintDebug、assembleDebug、assembleRelease、assembleDebugAndroidTest成功**。fixture APK内manifestは正式JSONとbyte一致。connectedAndroidTestは未実行。resume-android.log |
| sender_soak.py --seconds 3 --sample 1 | **Debug/Release × lan-reply/lan-silent の4 run成功**。実FrameProcessorからloopback両色へ送信、failed=0。各3 sample、後半は約60Hz。3秒では長時間リーク・熱・無線を評価できない。resume-soak-before.log（名前のbeforeは修正前比較を意味しない） |
| /bin/bash -n Start-Saber-A.command Start-Saber-B.command saber-watchdog.sh | 合格。実Player起動・signal・長時間監視は未実施 |
| 修正前比較 | EventLogは89合格/1失敗→90合格。Swiftログ保全は32合格/1失敗→33合格。Mac追加2件は6件中2失敗→6合格。runner終了2は11件中1失敗→11合格。wip前の入口・偽PASSも再現した。resume-*-before.log / resume-pre-wip-python.log |
| 相互作用の再現（未修正） | 実C# gate trace、実bridge2端末source trace、Mac stale回収の制御interleaving。resume-gate-trace.log / resume-bridge-identity.log / resume-mac-race.log |
| guided byte-budget の追加再現（未修正） | 実selectImagesに8MiB sparse画像を投入し、manual 5枚 / tracking 104,108 / peakなしを確認。peak保持の一時テストは失敗。resume-triage-budget.log |

Android は Android Studio同梱JDK、既存Gradle9.8.0の直接binaryを使用し、GRADLE_USER_HOME / ANDROID_USER_HOME / project cacheは一時領域。実行taskは :app:testDebugUnitTest :app:lintDebug :app:assembleDebug :app:assembleRelease :app:assembleDebugAndroidTest、--no-daemon、-Pkotlin.compiler.execution.strategy=in-process。wrapperのsandbox外書込を回避しただけでtaskは変えていない。

## 実行できなかった検証・残る制約

- iOS Release全体は xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -sdk iphoneos -destination generic/platform=iOS -derivedDataPath /private/tmp/merged-review-20261011/resume-ios-release CODE_SIGNING_ALLOWED=NO build を実行したが **BUILD FAILED**。SwiftUIMacros.StateMacroのswift-plugin-serverがmalformed response、CoreSimulatorServiceも利用不能。ホストSwiftのRelease条件合格をiOSアプリ全体の合格とは扱わない。
- disk APIの0を返す環境で失敗した録画E2E / recorder試験は、disk gateを緩めず未完了として残した。正式corpus試験は録画開始を必要とせず合格している。
- Unity Editor本体のEditModeは上記の環境エラーで未完了。native lifecycle、F7/F9キー実操作、全PlayMode、Playerビルド/ゲーム通しはstandalone compileだけでは確認できない。
- Windows cmd.exe / PowerShell / firewall、iPhone/AQUOS、実AWDL、実カメラ、投影、長時間給電・発熱は未実行。
- 公式 verify_phone_saber.sh は依頼どおりoperatorのレビュー・公式検証に委ね、commit / push を行っていない。修正済み項目が合格しても上記P1未修正項目の解消を意味しない。

## 最終確認追記

git diff --check は合格。a9a1e53からのタスク差分はwipを含む実装・テスト10ファイルと本報告書のみ。禁止対象の認識、UDP形式、正式corpus、scene / prefab / .meta / ProjectSettingsに差分なし。HEADは614108cのまま、commit / pushなし。
