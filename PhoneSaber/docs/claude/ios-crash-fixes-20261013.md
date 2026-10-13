# iPhone crash / hang 監査の再検証・修正 — 2026-10-13

## 対象と結果

作業開始時の HEAD は `e9e14c5`。この worktree にある `ios-crash-audit-20261013.md`（監査基点 `0fcb3e7`）を読み、指摘箇所と実際の呼出しを現行ソースで再確認した。今日の latency / send-gap 修正は保持した。今回の製品コード差分は `DebugVideoRecorder.swift` の7行だけ。

- **P1: 実在する相互待ちの経路。未修正。** 通知キュー変更は停止・再開・復旧との順序に影響し得る。実機で順序を確認できず、挙動変更は提案に留めるという今回の指示に従った。
- **P2: 実在する終了待ちの欠陥。修正。** 終端 writer を既存の `appendFailed` と任意 overlay の catch に流す。新しいタイムアウト、録画終了基準、証拠削減は追加しない。
- その他は監査ノートの結論を覆す成立例を確認しなかった。未確認リスクを「修正済み」「安全保証」とは扱わない。

認識（DetectionCore / BGRADetection / ranking / eligibility / scoring / PCA / thresholds）、UDP本文 `x1,y1,x2,y2`、formal lossless corpus、Unity scenes / prefabs / .meta / ProjectSettings は変更していない。commit / push、公式 verify、GUI / Dock アイコンを伴うアプリ起動は行っていない。

以下の `*.swift:行` は `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/` 相対。行番号は今回の修正後。

## finding → verdict → change → test

| 重大度・監査項目 | 現行コードでの再判定・具体的な成立条件 | 変更 / 提案 | 検証 |
| --- | --- | --- | --- |
| **P1 カメラ通知と同期 stop** | **実在・順序変更のリスクがあるため保留。** `CameraViewModel.swift:1102`, `:1111`, `:1118` は依然 `queue: .main`。`:426` の session `queue.sync` を MainActor の `stop`（`:1684`）が待つ。session 開始 / 停止の完了経路から通知を post し、main が同期停止待ちなら双方が進まない。 | 製品コードは維持。提案は3 observer の `queue: nil`。通知内容を値に取り出す既存処理と `Task { @MainActor ... }` は維持。採用前に Stop→即Start、FPS切替、interruption / runtime error の順序を実機で確認する。 | 登録メソッドを現行ソースから抽出し、main を止めて別キューから実 NotificationCenter で3通知を post。各250ms以内の復帰を要求すると **現行3/3 FAIL**、`/tmp` の提案コピーは **3/3 PASS**。`-O` / `-O -D DEBUG` 双方。実 session start/stop 障害の再現ではない。 |
| **P2 overlay の終端 writer 待ち** | **実在・修正対象。** `DebugVideoRecorder.swift:3135` は、ready=false の間 writer が failed / cancelled になっても sleep を続けていた。容量枯渇や media service 障害でこの組合せになると finish completion が戻らず、`FrameProcessor.swift:463` の finalizing が解除されず次の録画を始められない。 | 待機中に `.writing` を検査。違えば reader / writer をキャンセルして既存 `appendFailed` を throw。`:2493` の既存 catch は overlay だけを削除し、publish済みmetadata / raw / PNG / triage は保持して completion へ進む。 | 新規 `Tools/test_ios_overlay_liveness.py`。実ループを抽出・コンパイルし、AVFoundation subclass で status / readiness を注入。failed / cancelled / writing→failed は **修正前3ケース FAIL → 修正後3ケース PASS**。即ready / backpressure→ready は前後ともPASS。双方のキャンセルが各1回、正常時0回、エラー内容保持も検査。DEBUG有無の計10ケース。 |
| CameraViewModel の lifecycle / main構成 | **P1以外の確定不具合なし。長いmainブロックは実機リスク。** `CameraViewModel.swift:1398` で timer / watchdog / recovery / path / observer を解除。`:1670` 以降で世代を進めて停止。`:1698` 以降の構成はMainActor上。 | 構成全体を別キューへ移す変更は操作順序が変わるため提案のみ。 | 世代・停止・FPS復旧の呼出しを照合。latency host と既存 DeviceHealth tests がPASS。実AVCapture構成時間は未計測。 |
| FrameProcessor の mailbox / lock / finish | **指摘の条件では不具合なし。** `FrameProcessor.swift:52` は pending 1枠。`:151` の世代読み出しはlock、`:193` のresetはlock解放後にqueue.sync。`:501` はimageBuffer / lock成功を確認しdeferでunlock。寸法変更でtrackを初期化。`:458` のfinishは完了時に合流callbackを排出。 | 変更なし。 | 現行コードのロック順とcallbackを照合。latency hostのmailbox / BGRA寸法・同一frame認識 / 古い世代拒否がPASS。終了要求の同時競合は未実行。 |
| DetectionCore / BGRADetection の範囲・unwrap | **妥当なカメラBGRA入力で確定不具合なし。** `BGRADetection.swift:502` は正寸法とstride、配列入口`:1320` は総バイト数を確認。`DetectionCore.swift:257` 等のmask境界も要素数を確認。非空buffer内unwrapをInt.max等の人工入力だけで製品欠陥と分類しない。 | 認識コードを変更しない。 | 現行guardを照合し、既存static BGRA suite（odd寸法、padding、sample step、境界、morphology等）がPASS。formal 40件は未実行。 |
| DebugVideoRecorder の制限 / I/O / unwrap | **P2以外の確定不具合なし。** `DebugVideoRecorder.swift:259` の5分・disk / metadata制限、`:1951` のBGRA保持制限は現存。寸法違いはappendをskip。固定CodingUserInfoKey、nil検査後best、Encoderが生成するobjectのcastに成立可能なnil経路を見つけなかった。 | 上限・保持方針は変更なし。 | append / finish / independentCopy / overlay呼出しを照合。修正後のファイル全体はlatency hostでコンパイル成功。実encoderの容量枯渇は未実行。 |
| DebugRecordingTriage のmetadata / unwrap | **指摘の経路では不具合なし。** `DebugRecordingTriage.swift:1029` でframes配列のobject型を確認してから`:539` でcast。directoryなしなら画像選択ループへ進まず、`:830` はeventIndexのnil分岐後にunwrap。候補geometryは内部生成の契約。 | 外部から任意geometryを注入する本番経路がなく、変更なし。 | decode / selectionの分岐を照合。既存Python metadata schema testsがPASS。任意破損geometryの製品方針は追加しない。 |
| DebugBundleTransfer のmain / outbox / FileHandle | **指摘の呼出しでは不具合なし。** `DebugBundleTransfer.swift:242` 等のpreconditionへMainActorまたはmain dispatchから到達。`:266` のenqueueもmainへ渡す。`:330` のguard後のactivePackageURLは同じmain処理内。Documents.first!はiOS containerの前提。 | 容量検査・永続化・保持を維持。 | 呼出し / dispatch / shutdownを照合。packageにconnectivity.logを含める既存host XCTestがPASS。ENOSPC / EACCES、再起動・fd測定は未実行。 |
| UDPSender のsync再入 / 停止資源 | **現行appにsync再入の成立経路なし。** `UDPSender.swift:110`, `:487` のsyncに対しapp callbackはUI mailbox / MainActor Taskへ渡す。stopでreconnect / waiting / watchdogを取消、LAN probeもstop。今日のconfigure同一宛先保持を維持。 | 変更なし。 | 最新send-gap host: 同一宛先更新 / callback履歴 / send-gap / delivery / packageはPASS。実NWListenerを使う3件はEPERMで失敗（下表）。 |
| P2PSender / P2PProtocol のsync再入 / buffer / sequence | **現行appに成立経路なし。** `P2PSender.swift:407` にある通りcallbackからstopすれば同一queue.syncは危険だが、appのonState / onStatsはMainActorへ渡す。`P2PProtocol.swift:76` はheader+bodyを確保、`:104` は最短長を検査。sequenceはwrapping加算。 | 変更なし。今日の送信失敗時LAN fallbackも維持。 | コードを再照合。`test_ios_p2p_encoding` の4,626 byte列比較がPASS。実AWDL / 全cancelパターンでの解放保証は未検証。 |
| CoordinateDelivery / ConnectivityLog のlock・容量 | **現行callbackに逆順取得の成立経路なし。directory衝突は条件付きリスク。** `UDPSender.swift:1076` のlock内routingと`:1096` のfallbackを確認。logは200件、通常256KiBで1世代回転。`:950` の回転先がdirectoryなら安全のため削除せず追記し、上限を超え得る。 | directoryを削除 / 別名へ回転する挙動変更は提案のみ。 | CoordinateDelivery 6件PASS、ログpackage 1件PASS。通常appが回転先directoryを作る経路は確認せず。全lock競合のTSanは未実行。 |
| DeviceHealth / EventRecovery の長時間カウンタ | **8時間での桁あふれ・無期限窓という欠陥なし。** `DeviceHealth.swift:125` 等は窓に件数上限。`EventRecovery.swift:39`, `:50` はnilを補ってからunwrap。iPhoneのInt / UInt64の範囲は8時間frame数を十分収容。 | 変更なし。 | `testRatesAfterEightHoursUseOnlyTheRecentWindow` を含むhost DeviceHealth 13件PASS。8時間の実時間soakではない。 |
| GuidedRecording の固定script / step / 音声 | **指摘の経路では不具合なし。** `GuidedRecording.swift:157` のvalidation、`:432` のstep範囲検査、`:574` のcancel音声解放、`:586` のMainActorへのdelegate移送を確認。`CameraViewModel.swift:2579`, `:2751`, `:1601` にduration / guided / FPS task取消あり。 | 変更なし。 | source / metadata互換性を照合。Python guided schemaがPASS。実音声delegateと終了競合は未実行。 |
| ContentView / CameraPreview / app入口 | **指摘のcastは不具合ではない。** `CameraPreview.swift:27` のlayerClassがAVCaptureVideoPreviewLayerを保証。`ContentView.swift:6` は単一StateObjectを保持しscenePhaseを渡す。app入口にfatalErrorなし。 | 変更なし。 | 現行ソースで確認。UI実行は今回の禁止条件により未実行。 |

## 補足事項の判定

| 監査の補足 finding | verdict → change → test |
| --- | --- |
| DEBUG / Releaseのtrap差・明示continuation | 全SwiftのfatalError / 明示assert / checked・unsafe continuationを再検索し該当なし。main precondition / assumeIsolatedは現存するが、読んだapp経路に違反例なし。変更なし。新規P2回帰は最適化あり・DEBUG有無でPASS（Xcode Debug `-Onone` の代替ではない）。 |
| deinitだけで全所有資源を停止できるか | **実機 / 所有寿命のリスクで保留。** CameraViewModel.deinitの解除範囲は限定的。単一StateObjectを捨てる製品シナリオは確認できず、自動stop追加は提案のみ。weak解放 / カメラ・socket解放の結合テストは未実行。 |
| 想定外pixel format | **未確認リスク。** `CameraViewModel.swift:1782` は32BGRAを要求し、FrameProcessor入口でformatの再検査はしない。妥当な異formatが入った実例なし。拒否・変換は認識と復旧の挙動に触れるため未実装。 |
| メモリ警告と保持上限 | **実機リスク。** メモリ警告observerなし、retained BGRAは256MiBのまま。writer / camera pool / PNG一時メモリは別。jetsam未再現。上限縮小・自動停止・間引きは提案のみ。 |
| raw PNG / session / 未受信outboxの総容量 | **運用保持方針。** 1session制限を総容量制限と読み替えない。未受信bundleを自動削除する変更はしない。連続録画と再起動・容量枯渇は実機確認が必要。 |
| writerがwritingのまま停止 / finishWriting callback未返却 | **今回の修正対象外のFramework livenessリスク。** P2 guardでは検出できない。期限や強制cancelは新しい終了基準なので提案のみ。 |
| connection handlerの参照循環 | **確定リークではない。** 現行stop / tearDownでconnectionをcancelして参照を外す。旧監査のmacOS実験結果をiOS全状態の保証へ拡張しない。今回、実NWConnectionの解放実験は実行していない。 |

## 実行した検証

コマンドはrepo root基準。ログは `/tmp/phonesaber-crash-*.log`。コンパイルしたhost binaryのみを起動し、カメラ・GUI・Simulator・Unityは起動していない。

| 実行 | 実結果 / 範囲 | ログ |
| --- | --- | --- |
| `python3 -B PhoneSaber/ios/PhoneSaberSender/Tools/test_ios_overlay_liveness.py -v`（製品コード修正前） | exit 1。1 unittest内の2 subtest（DEBUG無/有）が各FAIL。各fixtureで終端3ケースが500ms以内に戻らず、正常2ケースはPASS。 | `/tmp/phonesaber-crash-p2-baseline.log` |
| 同コマンド（修正後） | exit 0。2 subtest、各5ケースPASS。AVAssetWriter / Input / Readerはsubclassで状態を注入。実encoder / ディスク障害ではない。 | `/tmp/phonesaber-crash-p2-fixed.log` |
| `python3 -B /tmp/phonesaber_crash_audit_probe.py IOSCrashLivenessTests.test_camera_notifications_do_not_wait_for_main_queue -v` | exit 1。監査ノートのテストdiffから抽出しSOURCEを現worktreeへ指定。3通知×DEBUG有無すべてFAIL。 | `/tmp/phonesaber-crash-p1-baseline.log` |
| `python3 -B /tmp/phonesaber_crash_proposal_probe.py IOSCrashLivenessTests.test_camera_notifications_do_not_wait_for_main_queue -v` | exit 0。`/tmp/phonesaber-crash-p1-proposal/CameraViewModel.swift` だけ3箇所をnilへ変更し3通知×DEBUG有無PASS。製品ソースへの適用なし。UI handlerは空fixture、実session開始 / 停止なし。 | `/tmp/phonesaber-crash-p1-proposal.log` |
| `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh` | exit 0。Static BGRA suite PASS、DeviceHealth XCTest 13件・失敗0。修正前に実行（認識 / DeviceHealthは今回変更なし）。 | `/tmp/phonesaber-crash-static.log` |
| `python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py` | exit 0。修正後のDebugVideoRecorder / FrameProcessor / 認識 / triage / guidedを含むmacOS `swiftc -O` コンパイル成功。camera設定はstub、transferはstub。CameraLatency 14 + DeviceHealth 13 + CoordinateDelivery 6 = **33件PASS**。録画encoderを成功動作させた結果ではない。 | `/tmp/phonesaber-crash-latency-host.log` |
| `python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_send_gap_host_tests.py` | コンパイル成功、exit 1。**17件中14 PASS / 3失敗**。失敗はSendGapNetworkTestsの `testCoordinateSendFailureInvalidatesP2PAndFallsBackToLAN`, `testP2PConnectedNotificationPreservesPendingVerifiedLANCoordinates`, `testPendingP2PCoordinateFallsBackWhenPongsExpireBeforeCompletion`。いずれもNWListenerの `Network.NWError error 1 - Operation not permitted`。sandbox制約による未検証でありPASS扱いしない。 | `/tmp/phonesaber-crash-send-gap-host.log` |
| `cd PhoneSaber/tools` で `python3 -B -m unittest -v test_protocol_contracts test_sender_soak test_latency_report` | exit 0、**27件PASS**。Swift / C# / Python座標契約をCLIで照合。sender_soakは集計toolの単体テストであり長時間実運転ではない。 | `/tmp/phonesaber-crash-tools.log` |
| `cd PhoneSaber/ios/PhoneSaberSender/Tools` で `python3 -B -m unittest -v test_phone_saber_send_gaps test_ios_p2p_encoding test_phone_saber_metadata_schema test_phone_saber_device_health test_xctest_result_classifier` | exit 1、**26件中25 PASS / 1 ERROR**。`test_http_receiver_preserves_optional_log_and_accepts_old_bundle` のlocalhost bindが `PermissionError: [Errno 1] Operation not permitted`。encoding / schema / health / result分類はPASS。 | `/tmp/phonesaber-crash-python-ios.log` |
| `git diff --check` と差分自己レビュー | PASS。製品差分はoverlay待機guardのみ。禁止領域・当日のlatency / gapコードに差分なし。 | git diff / status |

新規テストはToolsのPython unittestであり、新規XCTestファイルはない。Xcode projectの登録変更は不要。実ソースのループを再実装せず抽出するため、抽出境界の消失・重複はテスト失敗になる。UIKit / SwiftUI、writer正常生成、finalizing UI解除、MainActorの全順序はこのfixtureの対象外。

## operatorが実行する検証と未充足項目

公式 `bash PhoneSaber/tools/verify_phone_saber.sh` はoperator担当のため実行していない。iOS Release build、Simulator XCTest全体、formal lossless 40/40も未実行。今回のsandboxではxcodebuild / Simulatorを使わない指示に従い、起動を試していない。

公式verifyに加え、上表の新規P2 Pythonテストと、制限で失敗したsend-gap host / Python suiteを制限のない環境で再実行する。特に以下の**既存**XCTestを確認する（targetは `PhoneSaberSenderTests`、suiteは `DetectionCoreTests`）。

1. `testOverlayFailureKeepsValidMetadataPngAndTriage`
2. `testFinalizedBundleIsRegisteredWhileOverlayIsStillBlocked`
3. `testDebugRecordingCreatesSynchronizedRawOverlayAndMetadata`
4. `testRecordingFinishReasonsUseOneFinalizePath`
5. `testRawMetadataAndPngFailureDoNotPublishOrTransfer`
6. `testDebugRecordingAutomaticallyStopsAtDurationLimit`
7. `testDimensionChangeClearsHeldFrameBeforeProcessing`
8. `testLateFrameAfterStopCannotRestoreOverlayOrSendErrors`

これはP2の実encoder終端状態を直接注入する新規iOSテストではない。実機ではwriter失敗・ready=falseを含む終了処理が、完成済み証拠を保持しfinalizing解除・次の録画開始まで進むことを確認する。P1は未修正なので、通知postと同期停止の競合確認・通知順序の製品判断が残る。

監査末尾の未充足テスト10件は欠陥の断定ではなく結合・実資源テストの提案として保持する。

| 提案テスト | 今回の充足範囲 / 残る確認 |
| --- | --- |
| 1 通知postと同期stop競合 | mainを止めた通知postは再現。実CameraViewModel / session queueのstop競合、handler一度実行は未検証。P1は保留。 |
| 2 writer終端とoverlay後の再録画 | 実ループの終端・cleanupはPASS。実encoder、完成済み証拠、UI finalizing解除・再録画の結合は未検証。 |
| 3 寸法 / stride切替とFPS復旧 | 静的odd / paddingとmailboxの同一frameはPASS。CMSampleBufferの連続切替＋録画＋復旧の同時試験、ASanは未実行。 |
| 4 空 / 異formatの入口 | 入口guardを照合。異format拒否方針は未変更、製品判断後の実buffer試験が必要。 |
| 5 lock / sync競合 | 現行callbackの呼出しを照合。全キュー同時発火・TSanは未実行。 |
| 6 録画終了要求の競合 | 単一finishのcallback合流を照合。同時duration / error / user Stop / backgroundは未実行。 |
| 7 background / foreground全状態 | 世代と前面guardを照合。実AVFoundation・転送・packagingとbackground task猶予失効の状態表は未実行。 |
| 8 容量 / 権限エラーと再起動回復 | 各I/Oのthrow / catchを照合。全保存地点のENOSPC / EACCES、fd測定、実再起動は未実行。 |
| 9 低メモリ警告と停止ピーク | メモリ警告時の変更なし。小容量iPhoneの警告 / jetsam / camera pool / PNG・overlayピークは未測定。 |
| 10 実アプリ8時間soak | 時刻を進める単体テストのみ。Release / Debugの実カメラ・AWDL・UI・録画を含む8時間、thread / fd / task / timer / observer / package / weak解放測定は未実行。 |

既知のP1とFramework liveness・資源ピークが残るため、8時間の実機安定性を保証する結果ではない。
