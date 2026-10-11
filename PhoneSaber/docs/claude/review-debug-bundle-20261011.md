# PhoneSaber Debug Recording → Mac受信 調査報告（2026-10-11）

基準ディレクトリ: `<repo>`。以下のパスはここからの相対パス。
コミット・pushは実施していない。認識、UDP形式、formal corpus、Unity scenes/prefabs/.meta、ProjectSettings、他ジョブ対象のrelayソースは変更していない。

## 重大度順の発見事項

### P1・修正済み: 正常な6枚のガイドcaptureがMacの解析契約で拒否される

- `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py:55`
- 予約は5枚だが、iPhoneの選別は空き枠に6枚目も入れられる。6枚の `manual_capture` が入った正常bundleをMacがper-failure-typeエラーにしていた。HTTP受信そのものと、その後のreport/解析契約は別なので、Macには届いていても解析できない。
- ガイドだけ6枚を許可した。手動録画は3枚、7枚目の拒否、32 KiB context上限は維持。
- 回帰試験: `test_swing_only_can_include_all_six_captures_without_relaxing_context_limits`。修正前は例外、修正後は6枚のinput_plan・tracking preflight・session reportがPASS。33 KiB contextと7枚は拒否する。

### P1・修正済み: bridgeと同じframe IDの予約画像が消える

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugRecordingTriage.swift:745`、`:792`
- 静かなtracking/窓なしのガイドでbridgeを先に選ぶと、同じframe IDのmanual PNGがfilterと重複除外で落ちる。guided ledgerのcaptureは残るのに、予約したmanual PNGがbundleに入らない。
- 予約manual画像をこの除外から保護した。bridge自身のoriginal単位は維持。
- 回帰試験: `DebugBridgeDropoutTests.testGuidedReserveSurvivesOverlappingBridgeFrames`。修正前FAIL、修正後PASS。

### P1・修正済み: bridgeがbyte予算を使い、予約画像を押し出す

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugRecordingTriage.swift:688`、`:715`
- 予約manual画像5枚が各8 MiB、bridge単位が32 MiBの場合、bridgeが先に入ってmanualが脱落する。従来の予約は画像スロットだけだった。
- bridge選別時に予約画像の実ファイルサイズも先に確保した。既存の62 MiB画像予算（64 MiBからmetadata等の余裕2 MiBを引く）は変更していない。
- 回帰試験: `DebugBridgeDropoutTests.testBridgeBytesCannotEvictGuidedReservedCaptures`。修正前FAIL、修正後PASS。

### P1・提案のみ: 未送信bundleの復帰後再送と転送結果表示がない

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugBundleTransfer.swift:245`、`:272`
- 会場でMacに接続できず3回とも失敗すると、元bundleは端末に残るがactive/pending情報は捨てられる。foreground復帰時のbundle再走査・再送はない。成功/失敗はconsole出力だけで、録画UIの「完了」はMac到着を意味しない。
- receiverが保存した後にHTTP応答だけ失われた場合、再送は `phone_saber_triage_receiver.py:339` の409となり、Swiftも失敗扱い。実際のSwift uploader→loopback receiverで確認した。Mac側データは保持され、duplicateから解析は再起動しない。
- 提案: 永続outbox、Mac受領の表示、明示的再送、同一内容を検証した再POSTだけ成功応答にする。操作・再送挙動が変わるため未実装。409を無条件に成功扱いにしてはいけない。

### P1・提案のみ／実機未検証: 中断・背景化時に51秒やbundle生成を保証できない

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:996`、`:2233`
- lock/背景化/カメラ中断は録画を止め、outcomeはincomplete。Cancelはcancelledとして保存を試みる。ガイドが途中から再開する仕組みはない。
- background taskの期限到達時はtaskを終了するだけ。raw/PNG/triage確定中にOSがsuspend/killすると、完成bundleは保証できない。`DebugVideoRecorder.swift:2484` のoverlay生成を待ってから `:2507` で転送enqueueするため、bundleが既に生成済みでも転送開始が遅れる。
- no frames、raw writer失敗、必須PNG書込み失敗ではbundleまで到達しない。低diskは開始時約992 MiBの安全確認と録画中の容量確認があるが、他アプリの消費やStop時のI/O失敗を防ぐ保証ではない。thermal stateは記録・警告されるがガイド時間は延長しない。
- 提案: 保存済みbundleの復帰後回収とoverlay前の転送enqueue。変更せず、実機試験対象とした。

### P2・修正済み: manifestとsummaryのsession ID不一致を受け入れる

- `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_triage_protocol.py:221`
- manifestがA、summaryがBでもAのディレクトリとして公開され、ログ・解析対象のsessionが食い違う。
- 一致を検証し、拒否時はstagingを清掃する。
- 回帰試験: `test_manifest_session_must_match_summary_and_staging_is_removed`。修正前FAIL、修正後PASS。

### P2・修正済み: NUL入りPNG名でHTTPエラー処理から例外が漏れる

- `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_triage_protocol.py:38`
- `images/unsafe\0.png` をmanifestの許可pathとして通し、Path.openのValueErrorがreceiverのBundleError/OSError処理を外れていた。
- path検証でBundleErrorとして拒否する。
- 回帰試験: `test_nul_in_image_name_is_rejected_as_a_bundle_error`。修正前ERROR、修正後PASS。

### P2・提案のみ: capture要求の遅延とledgerの意味

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/GuidedRecording.swift:312`
- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:2432`
- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugVideoRecorder.swift:1706`、`:1832`
- 時計はsystemUptimeで、音声完了を待たずに51秒で終了する。古いcueは1秒、capture要求は0.5秒以上遅れると飛ばす。writerがframeを受理できない場合、発行済みone-shotは待ち続けるので、次のstepでcaptureされ得る。その間の後続要求はpendingのため飛ばされる。
- ledgerは実際に受理したframe/phaseと成功したcaptureだけを数える。plannedとactualの差は残るが、元の要求stepとcapture失敗理由は保存されない。schedulerのcompletedはcapture6枚成功を保証しない。
- 提案: 要求stepと期限の記録・期限切れ要求の取消、capture不足の明示。撮影挙動・表示が変わるため未実装。

### P2・提案のみ: 6枚予約、tracking、bridgeは上限との取捨選択が必要

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/GuidedRecording.swift:189`
- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugRecordingTriage.swift:694`、`:821`
- swing_onlyの予約5枚は、背景・大振り1枚・静止・速振りを先に取り、次に大振りの2枚目を取る。大振りの3枚目（時系列で4番目のcapture）が予約から外れる。これは必ず最後の速振りが落ちるという意味ではない。空き枠があれば6枚目も選ばれる。
- 5 manual + 最低5 tracking + bridge原本3 = 13枚で、12枚には同時保持できない。6予約なら14枚。1080pの圧縮しにくいPNGでは6 manual + 5 trackingが概ね88 MiB相当となり、62 MiB画像予算を超える。6固定予約の無条件の安全性は確認できないため、予約数は5のまま。
- byte不足時はtracking画像も個別にskipされ、予定した連続窓やpeakが欠け得る。この場合のprecheck失敗は実際の証拠不足なのでgateは緩めていない。
- 提案: 実サイズを踏まえた6枚目の優先度と、tracking窓をbyte単位でも一括選別する方針を決める。

### P2・提案のみ: 低速転送の総時間制限、relayのbackpressure

- `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugBundleTransfer.swift:220`、`:535`
- receiverは150秒idle timeoutだが、LAN resource/P2P transferは180秒の総時間制限。連続してbyteが進んでいても、64 MiBを約300 KiB/sで送ると180秒に収まらない。resume/range転送はなく毎回先頭から送る。
- `PhoneSaber/ios/PhoneSaberSender/Tools/p2p_bridge/PhoneSaberP2PBridge.swift:181`、`:196` ではsend完了を待たず次のreceiveを始める。receiver側が遅い場合にrelayの未送信bufferが増える構造。300秒総時間制限もある。AWDL実測・memory増加の再現は未実施。別ジョブ対象なので変更していない。
- 提案: 進捗に基づくtimeoutと、relayのsend完了後に次のreceiveを行うbackpressure。未実装。

### P2・提案のみ: uploadごとのCodex解析コスト

- `PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_triage_receiver.py:354`、`:447`
- CLI既定はautomaticで、毎回新規uploadに解析をqueueし、同モードのrepair gateも既定automatic。最大queue4・単一workerで、queue満杯時はbundleを保持する。重複POSTは解析を再queueしない。
- 既定変更は行わない。受領だけ確認する運用では既存の `--no-codex`、無課金の入力確認には `--dry-run` を提案。今回の受信smokeはanalysis_mode=disabled、PythonのCodex試験はfake CLIで、実際の有料モデルは呼んでいない。

## 追加確認

- 自動転送設定「Stop後にtriage bundleをMacへ自動転送」は既定true（XCTest実行時は抑止）。30 recorded frames未満は自動転送しない。
- manual IPならLAN直行。その他はP2P relay優先、見つからない／失敗時LAN fallback、最大3試行。station指定時は対象を絞る。station未指定でpreferred relayが見つからないとpickerは別の広告名へfallbackし得る（P2PSender.swift:92）。複数Macで同じMacへ確実に届く必要がある場合は、このfallback方針も確認対象。
- 受信はチェックサム検証とatomic公開。truncated upload・ディスク満杯時のstaging清掃、別sessionの同時受信、既存のpath traversal拒否はPASS。iOSの64 MiBとMac protocolの512 MiB受信上限は元から異なり、どちらも変更していない。
- swing_onlyのscript ID、tracking_mode/not_retainedはMacの契約で受理される。新しい6枚の回帰試験でmanual録画だけの枚数制限による拒否を解消し、32 KiB制限を維持した。

## 実施した検証と結果

1. `python3 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p 'test_*.py'`
   - 修正前: 388 tests、failure 1 / error 1 / skip 1。
   - 修正後: 392 tests、failure 1 / error 1 / skip 1。同じ既存2件が残る。
   - failure: `P2PBridgeLauncherTests.test_terminating_the_launcher_mid_build_stops_the_compiler`（compiler child startedを確認できない）。2回とも発生。原因未解決で、PASS扱いにしていない。
   - error: `TrackingPipelineE2ETests.setUpClass`。Swift実コードのcompileは成功したが、ホストの容量APIがavailable=0を返し、recorderの必要容量1,040,187,392 bytesの安全gateで停止。gateは変更していない。録画E2Eの各caseは実行できていない。
2. Toolsディレクトリで `python3 -B -m unittest test_phone_saber_triage_codex test_phone_saber_triage_protocol test_phone_saber_session_report test_phone_saber_receiver_health test_phone_saber_receiver_observability`
   - 最新差分で93 tests PASS（14.998秒）。新規Python回帰3条件は修正前にFAIL/ERROR、修正後PASS。途中切断・ENOSPC清掃試験もPASS。
3. `xcrun swiftc -O -D DEBUG -module-cache-path /tmp/phonesaber-debug-swift/cache` でDetectionCore/BGRADetection/FrameProcessor/DebugVideoRecorder/DebugRecordingTriage/GuidedRecordingをcompileし、既存XCTestの選別5caseを小さなassert/CameraIntervals/転送no-op stubと実行。
   - 修正前: 新規2case FAIL。
   - 修正後: 5case PASS（重複、byte予算、従来のguided/manual選別、heavy contextの32 KiB以内）。実XCTest runnerではなくhost harness。
4. GuidedRecordingと実コードから抽出したlimits/segment型をswiftcでcompileし、時計・境界・capture cadence・Cancel・stall・ledger JSON・reserveの10caseをhost stubで実行。
   - 10case PASS。swingOnlyV1.totalSeconds == 51を別途確認。
5. P2PProtocol/DebugBundleTransferの実ソースをswiftcでcompile（座標設定等だけstub）。P2PBundleUploaderのendpointOverrideを127.0.0.1のOS割当portに向け、Python TriageHTTPServerへ実送信。
   - 約753 KBのPNG全byte一致、HTTP 201成功、同一packageのHTTP 409失敗扱い、staging残存なしを確認。別sessionの同時受信もPASS。
   - Bonjour、実AWDL、LANのURLSession送信経路そのものの実機試験ではない。
6. `git -c filter.lfs.process= -c filter.lfs.required=false diff --check -- PhoneSaber`: PASS。
   - 通常のgit statusは共有元repoのLFS tmp書込みがsandbox拒否。LFS filterを無効にした全体diff --checkは、作業開始時から存在するAssetsのfont/LFS差分の空白を報告する。これらのAssetsを編集・修復していない。

検証用harness・ログは `/tmp/phonesaber-debug-swift/`、全体Pythonログは `/tmp/phonesaber-debug-final-python.log`。

## 実施できなかった確認／operatorに必要な確認

- xcodebuild / simulator / iOS Release build / 公式verifyは実施していない（依頼の環境制約とoperatorの実施範囲）。
- operatorは `GuidedRecordingTests` 全件、`DebugBridgeDropoutTests`（新規2case含む）、`DebugRecordingRetentionTests`、`P2PTransportTests` のbundle uploader・転送抑止・台選択caseを正式XCTest runnerで実行する必要がある。
- formal lossless corpusは変更せず、今回40/40の再実行結果を主張しない。
- iPhoneの51秒録画、lock・着信・背景化・カメラ中断・低disk・thermal状態、実LAN/Bonjour/AWDLとMac保存完了は未実機検証。ホスト試験だけでvenueでの到着保証はできない。
- 今回の修正対象はproduction 3ファイルと既存test 3ファイルのみ。commit/pushなし。
