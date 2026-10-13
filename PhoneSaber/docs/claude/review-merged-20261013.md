# 2026-10-11 夜〜2026-10-13 merge レビュー

対象 worktree: `cx/review2-20261013`。開始時 HEAD は `c339d2f`、作業ツリーは clean。
`PhoneSaber/CLAUDE.md` / `PhoneSaber/AGENTS.md` を読んだ。
今回の明示指示を優先し、commit / push / 公式 verify は実施していない。
Unity、Player、Simulator、Chrome、ブラウザーの GUI は起動していない。

`bbe818a` / `8102e2e` / `c339d2f` の中断作業を確認して保持した。
録画開始と Unity 終了コードの回帰は、旧ソースを一時ディレクトリへ取り出して FAIL を再確認した。
認識の ranking / eligibility / scoring / PCA / thresholds、UDP 本文、formal corpus、
Unity scene / prefab / .meta / ProjectSettings に今回の変更はない。

## 所見と修正（重大度順）

行番号はこの作業の最終ソース。P1 は座標・ゲームの停止に直結する条件付き不具合、
P2 は録画・配布・検証の破綻、P3 は診断・テストの問題。
異常状態を注入したホスト回帰と、実機での障害再現は区別する。

### 1. P1 — カメラ通知と同期停止の相互待ち

- 場所: `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:1103`。
- 条件: main が `stopSynchronously()` で session queue を待つ一方、その開始・停止経路がカメラ通知を post する。`queue: .main` の同期配信は main が observer を実行するまで post 元を待たせる。FPS 切替・復旧中にも成立し、カメラ・UI・座標が進まなくなる。
- 修正: 3つの camera observer を `queue: nil` にした。通知内容を値として取り出し、既存の `Task { @MainActor ... }` へ渡す。同期 stop、UI handler、復旧 policy は維持。
- 回帰: `test_ios_crash_liveness.IOSCrashLivenessTests.test_camera_notifications_do_not_wait_for_main_queue`。main を待機させ、実 NotificationCenter の3通知を別 queue から post。Debug / Release とも旧版 FAIL、修正版 PASS。端末の AVFoundation がこの順序で通知する実測は未実施。
- 既存の [iOS crash audit](ios-crash-audit-20261013.md) に提案のみ残っていた項目を再検証して適用した。

### 2. P1 — 最初の heartbeat 書込み失敗で正常なゲームを kill

- 場所: `PhoneSaber/mac/saber-watchdog.sh:112`、`PhoneSaber/windows/saber-watchdog.ps1:157`。生成側は `Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberHeartbeat.cs:66`。
- 条件: OpenOrCreate でファイル作成は成功するが、容量不足等で最初の1 byteを書けない。0 byteのファイルの mtime だけで監視が有効になり、45秒後に正常な Player をハングとして終了させる。
- 修正: 生成側の形式どおり、長さ1 byteのファイルだけで監視を有効にする。しきい値、STOP、sleep/wake の猶予、旧 build の扱いは変更していない。
- 回帰: `test_ops_watchdog.MacWatchdogTests.test_created_but_unwritten_heartbeat_does_not_arm_hang_detection`。偽 open + 実 child process。旧版は起動回数2、修正版は1で正常終了。Windows の同等回帰も追加したが、この Mac では実行不能・skip。Windows の PASS は主張しない。
- 一度成功した heartbeat が後から書込み不能になる条件は、下記の未解決 policy リスクに残る。

### 3. P2 — 配布コピー失敗で旧 Player を起動パスから失う

- 場所: `PhoneSaber/tools/unity_copy.sh:149`。
- 条件: 旧 `Builds/Mac/3D-Saber.app` を backup へ移動した後、ditto が容量不足・I/O errorで途中失敗。起動パスには不完全な新 app だけが残り、次の launcher 起動が失敗する。
- 修正: 同じ volume の隠し app へ完全コピーしてから差し替える。昇格の mv が失敗した場合は旧 app を戻す。build と smoke 成功の条件は維持。
- 回帰: `test_unity_copy_failures.UnityCopyFailureTests.test_failed_distribution_keeps_the_previous_player_at_its_launch_path`。旧版 FAIL、修正版 PASS。成功時の差替え、昇格失敗時の復元もホストの実ファイル操作と ditto/mv stub で PASS。実 app は起動しない。
- 同じ配布経路の追加不具合: 最初の起動中確認後、smoke/コピー待ちの間に運営側がゲームを起動しても、差し替え直前には再確認しなかった。`unity_copy.sh:152` に再確認を加え、既存の「起動中ならexit 3で差替え延期」をここでも適用。`test_game_started_during_smoke_is_not_replaced` は旧版が稼働中appを差し替えてexit 0となりFAIL、修正版は旧appを残してexit 3でPASS。launcherとinstallの間にOS共有lockはなく、最後の確認直後の非常に短い競合窓まで原子的に防ぐ変更ではない。

### 4. P2 — 発熱切替が未作成 recorder を終了扱いにする

- 場所: `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:2459`。
- 条件: 手動・guided 録画の開始時、active を予約した直後の health 更新が熱による stop/start を起こす。まだ recorder がないのに stopDebugRecording が投入され、その後に作成された recorder と UI/finalizing/transfer の状態が食い違う。
- 修正: 中断 commit `c339d2f` の修正を保持。health 更新を active 予約より前に実行し、停止した場合は guide を解放して戻る。
- 回帰: `test_ios_recording_lifecycle.RecordingLifecycleTests` が実 startDebugRecording メソッドを抽出して host XCTest 2件を実行。`c339d2f^` では両件 FAIL（9 assertion）、現行 PASS。camera/recorder/transfer は stub。実機の熱イベントは未注入。

### 5. P2 — 失敗済み overlay writer の readiness を永久に待つ

- 場所: `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugVideoRecorder.swift:3135`。
- 条件: overlay の input が not-ready の間に writer が failed/cancelled に遷移し、ready が戻らない。保存中が解除されず、次の録画ができない。outbox へ完成 bundle を早期登録しても、finalizing は残る。
- 修正: readiness 待機中にも writer.status を確認し、終端では reader/writer を取消して既存 appendFailed へ戻す。呼出し元の既存 catch が partial overlay を除去し、完成済み raw/metadata/PNG/bundle を保持する。新しい時間制限は導入していない。
- 回帰: `test_ios_crash_liveness.IOSCrashLivenessTests.test_terminal_overlay_writer_does_not_wait_forever`。実 AVFoundation subclass で failed/cancelled + not-ready を注入。Debug / Release とも旧版 FAIL、修正版 PASS。実 encoder の容量枯渇は未試験。
- これも既存 iOS crash audit の提案を再検証して適用した。

### 6. P2 — Unity 異常終了を成功 XML / build log で隠す

- 場所: `PhoneSaber/tools/unity_copy.sh:128`、`:138`、`PhoneSaber/tools/unity_editmode_results.py:93`。
- 条件: Unity が PASS XML / Success log を書いた後に exit 1/139 等で終了。従来は成果物だけを読んで成功扱い、または失敗 build の smoke へ進んだ。
- 修正: 中断 commit `8102e2e` の終了コード連携を保持。正常な Test Runner の exit 2 は、検証済み XML に失敗がある場合だけ既知失敗の照合へ進む。CDPATH 対策も保持。
- 回帰: `test_unity_editmode_results.EditModeResultsTests.test_shell_editmode_propagates_validator_status_with_stubs` / `test_failed_build_never_runs_smoke_even_with_success_log`。`8102e2e^` の旧スクリプトで FAIL、現行 PASS。旧版比較は Python/shell stub を直接実行しただけで、GUI app は起動していない。open 経由では Player/Editor 自体の exit code が取得できない制約は残る。

### 7. P2 — 古い成果物を削除できなくても検証を続行

- 場所: `PhoneSaber/tools/unity_copy.sh:50`、`:124`、`:135`。
- 条件: read-only な出力先等で古い smoke.log / EditMode XML / build成果物の削除が失敗し、今回の起動も成果物を作れない。open は0を返し得るので、旧成功が今回の成功として使われる。
- 修正: smoke / EditMode / build すべて、初期化失敗なら app 起動前に非0で終了。shots の既存対策は維持。
- 回帰: `test_unity_copy_failures` の cleanup failure 3件。旧版 FAIL、修正版 PASS。stale PASS log/XMLを置き、削除失敗を注入し、起動 stub が呼ばれないことまで確認。

### 8. P2 — PDF 更新の途中書込みで旧配布物を破壊

- 場所: `PhoneSaber/tools/build_staff_manual_pdf.py:153`。
- 条件: Chrome が2ページの PDF を作れても、配布先の write_bytes が先に旧 PDF を truncate し、その後 ENOSPC。旧マニュアルが不完全な PDF に置き換わる。
- 修正: 出力先と同じ filesystem の一時ファイルへ書込み、完了したファイルだけを replace。書込み失敗時は旧 PDF を保持し、一時ファイルも片付ける。ページ数・HTML・配布 PDF 本体は変更しない。
- 回帰: `test_staff_manual_write.StaffManualWriteTests.test_write_failure_preserves_the_previous_pdf`。Chrome を stub にして途中書込みの ENOSPC を注入。旧版 FAIL（8 byteに破損）、修正版 PASS。

### 9. P3 — events.log 世代交代で自動遅延検証が誤って NG

- 場所: `PhoneSaber/tools/auto_latency_player_check.py:74`、`:117`。
- 条件: 検証開始時のファイル offset を、途中で回転した新 events.log に適用して末尾を越える。または今回の estimate が `.1` へ移る。Player は正しく記録したのに「1行もありません」となる。
- 修正: 現行＋4世代の開始位置を device/inode に結び付け、終了後に各ファイルの新しい部分だけを読む。以前の実行結果を数えず、wall clock の変更にも依存しない。perf/event log の生成・回転 policy は変更しない。
- 回帰: `test_auto_latency_player_check.AutoLatencyPlayerLogTests.test_estimate_is_found_when_events_rotate_during_the_check`。main 全体を app/socket stub で実行。estimate の前/後の回転の両方で旧版 FAIL、修正版 PASS。

### 10. P3 — bridge テストの起動失敗で child process を残す

- 場所: `PhoneSaber/ios/PhoneSaberSender/Tools/test_phone_saber_p2p_bridge.py:87`。
- 条件: start_bridge が process を作った後、listen待ち assertion が失敗。stop cleanup を登録する前に setUp が抜け、テスト用 bridge を残す。
- 修正: 起動前に cleanup を登録。起動失敗の assertion に bridge log も添付する。実 bridge の再起動 policy は変更していない。
- 回帰: `test_phone_saber_p2p_bridge.BridgeTestCleanupTests.test_failed_setup_stops_the_started_bridge`。起動失敗を注入して doCleanups を実行。旧版は terminate 0回、修正版は1回・waitありで PASS。

## 追加欠陥を確定しなかった項目

| 対象 | 確認・限界 |
| --- | --- |
| outbox・アプリ更新 | `DebugBundleOutbox` の Codable 部分は最初の `f796bdb` と現行で一致。相対 bundlePath、uploading→pending の復帰、欠損ファイル除外、backoff、manual retry、受信済み20件と prunedBefore を host XCTest で確認。対象の既存 build 間に新規 schema 非互換は見つからない。将来の新フィールド・新 enum 値の移行を保証するものではない。 |
| 同一 bundle 再送 | receiver は検証後に publication を lock し、保存済みの size/hash を比較。異なる bundle と同じ sessionID、bare 409 は成功にしない。実 Swift uploader→loopback receiver、分割200 receipt・旧201、Python 同時POST等を実行。実 AWDL/LAN flapping は未試験。 |
| thermal / 電池 / fps比較 | 30/60/120の熱 policy、連続冷却、復旧中・背面での再設定禁止、電池境界、比較期限の純粋テストは通過。録画開始の相互作用は所見4。録画途中の thermal 切替が録画を終了する既存 policy を、継続録画へ変更していない。 |
| P2P auto-restart・sender gate・LAN probe | retryの2/5/15/60秒と安定時reset、loopback↔LAN、detect-only既定、異常payloadのownership除外、protocol/version/port互換fixtureを確認。XCTest view model の probe は5007へ向けない。実機の復帰時間・2台のP2P識別は別問題。 |
| guard G・診断preflight | G既定ONの C++/Swift とビルド入口を確認。325 PNG / 27合成 / 541遷移の bit parity は差分0、formal 40/40。実 serializer/triage の guard context は17,096 byte、32 KiB preflight通過。認識変更なし。速い振りの実機正解率はこの結果から保証しない。 |
| perf log / EventLog | シーン・描画条件の窓境界、stats・sender gate・event rotation・失敗retryを standalone NUnitで確認。通常はキューとログ世代が有界。rotation失敗時に追記を継続する既存方針では上限超過し得る。所見9は読み手側の欠陥。 |
| shader / PlayerSmoke | Graphics差分、SaberInputBridgeの欠落shader分岐、smokeの引数・完了条件、shotsの旧結果除外を確認。C#をUnity実DLLでcompile。実 Player の描画・scene load・スクリーンショットは今回未実行。 |
| staff PDF / status / unity_copy | HTML変換と2ページ判定の既存テスト、PDF --check、Player所有port/Player.log判定のPythonテストを実行。--checkはmtimeの確認であり内容同一性の証明ではない。Chrome印刷・実 Player所有portのstatus実測はしていない。 |

## 実行結果

ログは `/tmp/phonesaber-review-*.log`（GUIや私的録画をrepoへ追加していない）。
通常の `python3` は3.13で cv2 がなく root suite が失敗したため、規定の `python3.12` で再実行した。
Android は最初 SDK 未指定で失敗し、環境変数の指定だけで再試行した。

| 実際に実行したもの | 結果 |
| --- | --- |
| `python3.12 -B -m unittest discover -s PhoneSaber -p 'test_*.py' -v` | 85 tests、FAIL 0。`phonesaber-review-python312-root.log`。 |
| `python3.12 -B -m unittest discover -s PhoneSaber/tools -v` | **217 tests、FAIL 0、Windows 6 skip**。guard context / Swift/C# protocol / Mac watchdog stubも含む。`phonesaber-review-tools-last.log`。その後の配布直前再確認は、最終targeted suiteで下記のとおり検証。 |
| iOS Tools unittest discover（初回はpython3、以後3.12） | 初回413件: uploader 1 FAIL、tracking setUp 1 ERROR、1 skip。trackingだけ再試行して同じcapacity=0を確認。最終はtracking 17件を明示除外し **415 tests、FAIL 0、1 skip**。除外以外の全suiteを実行、`phonesaber-review-ios-tools-last.log`。全件PASSとは表記しない。 |
| `python3.12 -B -m unittest test_phone_saber_p2p_bridge -v`（Tools内） | **30 tests、FAIL 0**。途中の広いsuiteで起動待ち1件の一時FAILがあった。単独再試行と最終415件ではPASS。cleanup修正はその失敗自体の原因が判明したという意味ではない。 |
| 実 Swift uploader→loopback receiver | 初回の失われた応答→再送ケースで1回FAIL（receiverは200を記録）。単独2件・その後の全suiteではPASS。原因未確定の一時失敗として保持。 |
| `python3 PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py` | host XCTest 26件（camera/health）＋6件（実UDP経路）、FAIL 0。camera設定はstub。 |
| `python3 /tmp/phonesaber-review-host.py` | 実Outbox/EventRecovery + 抽出camera型の host XCTest **Debug 17 / Release 17 PASS**。HeartbeatWriter/BridgeRetryPolicyの既存NUnit **5 PASS**。最初のhost stubの型不足を補って再実行した。 |
| `python3.12 /tmp/phonesaber-review-nunit.py` | 既存の Stats / SenderGate / EventLog NUnit **79 PASS**。SetUp/TearDown、TestCaseも実行。Unity/ソケットは不要。 |
| `python3 PhoneSaber/tools/check_pc_latency.py` | InputPoint / PhoneSaber runtime / SwingEventStreamをUnity 6000.3.9f1実DLLで Editor macOS/Windows、Standalone macOS/Windowsの **4構成compile PASS**、Windows native宣言compile PASS、NUnit **20 PASS**。GameSession等のゲーム依存とSaberInputBridge型はstub。Unityプロジェクト全体のcompileではなく、Editor/Playerは起動していない。 |
| `make -C PhoneSaber/android/core test parity` | C++ unitと比較tool unit PASS。**formal 40/40、325 PNG差分0、27合成差分0、541遷移差分0**。`phonesaber-review-core.log`。 |
| iOS Debug / Release xcodebuild | 最終とも **BUILD SUCCEEDED**、署名・インストールなし。通常ReleaseはSwiftUI macroの入れ子sandboxが拒否されexit65。再試行はCLIだけ `OTHER_SWIFT_FLAGS='$(inherited) -disable-sandbox'` を追加。外側の制限を変えず、project設定も変更していない。公式verifyと同一条件のPASSではない。 |
| Android Gradle | SDK/JBRを明示し `./gradlew --offline --no-daemon testDebugUnitTest assembleDebug lintDebug`。**76 tests、FAIL/ERROR/skip 0、APK build/lint成功**。端末へインストールしていない。 |
| `test_unity*.py` の最終 targeted suite | **41 tests、FAIL 0**。実アプリ起動をstubにしたもの。最後の配布直前再確認も含む。 |
| `test_ios_crash_liveness.py` | **2 tests×Debug/Release PASS**。旧版4 subtest FAILを記録。 |
| PDF / shell / diff | PDF `--check`: 最新（mtimeのみ）。PDF途中書込み回帰PASS、既存HTML/2ページテストPASS。`/bin/bash -n`でMac watchdog/unity_copy PASS。`git diff --check` PASS。Windows PS1のBOM/CRLFを維持。 |

iOS Release buildの実コマンド（最終はSwift修正後に再実行）:

```sh
xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj \
  -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' \
  -derivedDataPath /tmp/phonesaber-review-ios-release CODE_SIGNING_ALLOWED=NO \
  'OTHER_SWIFT_FLAGS=$(inherited) -disable-sandbox' build
```

Debugも同じコマンドで、`-configuration Debug`、`-derivedDataPath /tmp/phonesaber-review-ios-debug` を指定した。
Androidの環境指定は `ANDROID_HOME=/Users/satoshi/Library/Android/sdk`、
`JAVA_HOME=/Applications/Android Studio.app/Contents/jbr/Contents/Home`。

FAIL→PASSのログ: `recording-before`、`unity-wip-before`、`unity-copy-before`、
`heartbeat-before`、`ios-liveness-before/after`、`rotation-before`、`pdf-write-before`、
`bridge-cleanup-before`、`player-recheck-before`（すべて `phonesaber-review-` prefix、`.log` suffix）。
容量ゲートや正式期待値を弱めてPASSにしたテストはない。

## 未実行・残るリスクと提案（実装していない）

1. **watchdog policy（高）**: `PhoneSaberHeartbeat.cs:74`、Mac watchdog `:119`、Windows watchdog `:162`。一度書けた後の read-only/full disk は stale heartbeat と同じ判定になる。またheartbeatを一度確認した後に正当な同期scene loadが45秒を超えるとkillされる。初回scene load前はまだ監視が有効でない。実機で次の曲への最長ロード、sleep/wake、負荷中の復帰、書込みエラーを確認する。ディスク障害通知やロード猶予の追加は停止判定が変わるため提案のみ。WindowsのSetFileTime失敗時のmtimeも実機で確認する。
2. **カメラ構成・encoder（高〜中）**: `CameraViewModel.swift:1678`、`DebugVideoRecorder.swift:3175`。同期stop/構成自体が長時間戻らない場合、またはwriterがwritingのまま永久停止 / finishWritingのcallbackが来ない場合は今回の終端guardでは解決しない。session queueへの構成移動や新しいタイムアウトは未実装。実機でメディアサービス再起動、通話、熱切替とguided録画/転送の重なりを確認する。
3. **outbox / 証拠の総容量（中）**: `DebugBundleTransfer.swift:132`。受信済み履歴だけは20件に制限されるが、未受信bundle/raw動画/PNG/sessionの自動削除はしない。8時間分の総容量制限を意味しない。iOS更新でcontainerが移った後のrelative path、Wi-Fi/AWDL断のmanual retry、pending/received状態を実機で確認する。自動整理・総量上限は証拠保持と操作が変わるため提案のみ。
4. **Mac stale-lock（疑い）**: `saber-watchdog.sh:69`。stale判定とrenameは一体のCASではない。reclaimerが判定後に長く停止し、別watchdogが新lockを確保してPID再確認を終えた後に古いreclaimerがrenameする順序には注意が必要。実二重起動は今回未再現。長いスケジューリング遅延を入れた同時起動と、別kitコピーからの起動を確認する。OSの共有lockへの置換は運用依存を増やすので提案のみ。
5. **一時失敗の追跡（中）**: Swift uploaderの再送とbridge起動待ちが各1回失敗した。最終suiteは通過したが、物理ルートflappingや高負荷時のNWConnection state/receive/send callback順を採取する。再現しないことだけを理由に「環境原因」と断定していない。
6. **Windows / 実端末 / 描画**: Windows watchdog integration 6件はskip。Unity Test Runner、Unity Editor/Player build・smoke・screenshots、実AWDL、iPhone/AQUOSの8時間運転、Chrome印刷・PDF目視QA、公式 `verify_phone_saber.sh` は未実行。shaderの実描画、Windows mutex・mtime・Ctrl+C・二重起動はoperatorの本番機材で確認する。
7. **ホスト録画 E2E（環境制約）**: `test_phone_saber_tracking_e2e` は実capacity APIが available=0を返し、`insufficientDiskSpace(required:1040187392, available:0)` でsetUpが終了。disk実空きは約429 GiB。2回同じ失敗、17件未実行。録画上限/容量ゲートは維持した。actual recorder/PNG/overlay全体の成功とは数えない。

## 履歴の範囲

指定の `git log --since=2026-10-11T18:00:00+09:00 --no-merges` と指定パスで31件を抽出。
個別diffを保存し、runtime・運用・diagnostics・testsの差分と現在の呼出し先を追跡した。
`bbe818a` / `8102e2e` / `c339d2f` はmainの新規変更ではなく、このjobの中断作業として区別した。
既存レビュー済み項目は重複して認識変更せず、提案のまま残った障害経路は上記の再現範囲を明示した。

```text
78c04bf e3485ee f796bdb ac3ede2 d22df09 a10ed9b 7879820 c87a80e
cde8764 eeb7b74 e424eb2 081ee7a d49ff3f 172cc3a cac2c08 fd37543
0fcb3e7 b918ed3 bbe818a 1212021 04ab3f4 8f914bf 8958967 5a3021a
75abf2f aacffa9 ead244d 8102e2e 53a5dfa 9cbdbad c339d2f
```
