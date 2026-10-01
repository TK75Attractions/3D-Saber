# 実機再試験前のtracking verification（2026-10-01）

対象はschool-festivalのmain、診断・解析・verificationのみ。production認識、HSV/brightness/eligibility、candidate scoring/ranking、PCA/body/fallback、final endpoint selection、UDP、Unityは変更しない。

## Analysis前のpreflight

`analyze_bundle`は最初に既存の厳密なinput contractを検証し、次に`tracking_preflight`を実行する。どちらもCodex binary探索、scratch作成、Luna起動より前。repair gateでも同じpreflightを再実行し、古いanalysis reportで不足した証拠を迂回しない。

- selected event、event ID、対応するselection ledger、唯一のpeak/centerが必要。
- 選択画像の元の順序と各compact context内のframe/timestamp順序を検証する。並べ替えて不整合を隠さない。
- PNG/contextは1対1。session/frame/timestamp/color/event/role/image pathを照合し、PNG実在・signature、元frameの存在、重複・symlink・サイズ上限も既存input contractで検証。
- tracking解析には少なくとも3枚の連続frameがbefore/peak/afterを含み、timestampは有限数で単調増加すること。
- 各選択frameのtracking timeline、detected=trueの各frameのselected candidate、finalSelectedと実際のemitted endpoint、endpointSource/path履歴が必要。候補・端点履歴は最低3frame、stageDiscontinuitiesは最低2frame。
- raw PCA、final geometry、candidate geometry/metrics、採用pathなどは既存schema validatorも検証する。body/robust intervalはproductionで観測された場合だけ必要で、存在しない計算結果を補完しない。
- dropout peakがdetected=falseでcandidate switch/path change/final jumpを伴わない場合はeligibility-dropoutとして扱う。連続temporal画像数やtracking/candidate/endpoint/stage履歴を強制しない。既存legacy/static/eligibilityのみの解析にはtracking temporal条件を追加しない。
- UDP sendStartedは任意。送信待ちや録画終了で欠けた送信値は捏造せず、Unity受信確認とは区別する。

失敗時は`[AUTO_REPAIR][PRECHECK_FAILED] result=NEEDS MORE EVIDENCE`とreason code/detailを出し、Luna、再解析、Solは呼ばない。主なcodeは`temporalEvidenceMissing`、`eventCenterMissing`、`temporalOrderInvalid`、`frameMappingMissing`、`imageFileMissing`、`imageFileInvalid`、`timestampMissing`、`trackingTimelineMissing`、`candidateHistoryMissing`、`endpointHistoryMissing`、`endpointPathHistoryMissing`、`trackingAssessmentDataMissing`、`inputContractInvalid`。

input contract自体を満たさないbundleはtyped `PrecheckFailed`（BundleError互換）で停止し、入力ファイルを変更しない。形は正常で証拠が不足するbundleではversion 3のanalysis JSON/Markdownを保存し、`analysisExecuted=false`、`precheck`、`trackingSummary`を記録する。既存reportを上書きしない。receiverはこの結果からrepairを起動しない。既存reportを使う再起動経路もgateで停止する。

## Terminal summary

analysis終了時とrepair gateのterminal reportに各eventを1行で表示する。analysis側のgateは`pending`、repair側は実際の`actionable`/`needs_capture`。JSON reportにも同じ値を保存する。

```text
[AUTO_REPAIR][TRACKING_SUMMARY] sessionID="phonesaber_..." selectedEventID=1000000000 color="red" centerFrame=1010 instabilityScore=3.666666666666667 detected=true candidateSwitch=true endpointPathChanged=false midpointDiscontinuity=100 angleDiscontinuity=0 lengthDiscontinuity=0 firstUnstableStage="candidate-selection" visualEvidence="complete" temporalEvidence="complete" analysisResult="actionable" reanalysisExecuted=true solEscalationExecuted=true gateResult="actionable" reasonCodes=[]
```

stageはcandidate-selection、mask-component、raw-pca、body-endpoint、final-selection、downstream、unknown。画像で裏付けられたanalysis conclusionを表示し、数値scoreだけでproduction原因を断定しない。不明なdiscontinuityはnullで表示する。

## 決定論的E2E harness

```bash
python3 -B -m unittest discover -s ios/PhoneSaberSender/Tools -p 'test_phone_saber_tracking_e2e.py' -v
python3 -B -m unittest discover -s ios/PhoneSaberSender/Tools -p 'test_phone_saber_tracking*.py' -v
```

macOSのSwift host harnessが実際のDetectionCore/BGRADetection/FrameProcessor/DebugVideoRecorder/DebugRecordingTriageをcompileする。25frameのsynthetic BGRAと固定frame/timestampをreal recorderへ渡す。diagnostic path切替はfixture candidateだけで注入し、production関数を変更しない。CameraViewModelのinterval型宣言はUIKitから切り離してそのまま抽出し、外部uploadはno-opに置換する。録画は一時directory内。PNGはCoreImageでdecodeし、識別pixelとframe IDを照合する。

その後、実際のbundle transport、input_plan、temporal_events、Luna prompt、structured output parser、load_analysis、repair_gateを接続する。LLMは既存fake CLIのfixture response。real model呼び出し・外部upload/pushはない。macOS以外は実capture部分を明示skipする。

| Scenario | 確認する結果 |
| --- | --- |
| A Stable tracking | 全frame score=0、不要なrepairなし |
| B Candidate switch | 最大score frame中心の11枚、candidateSwitchをpromptへ、candidate-selectionを表現、gate actionable |
| C Raw geometry jump | rawPCAから不連続、raw-pcaを表現、downstream指定はgate拒否 |
| D Endpoint path switch | fallbackPCA/bodyPCAの履歴とendpointPathChangedがcontextに残る。画像/実出力が安定ならrepairしない |
| E Final emitted endpoint stable | diagnosticsも実送信sourceEndpointも安定、repairなし |
| F Missing temporal image | imageFileMissing、モデル呼び出し0、別frame補完なし |
| G Broken mapping | frameMappingMissing、モデル呼び出し0、入力保存 |

追加のintegrationはdetected=trueのcandidate-switch bundleをLuna → Luna再解析 → Sol second opinion → actionable gate → real repair state machineまで通す。repair LLMのfixtureをminiature repositoryで呼び、more-evidence応答で停止させる。production source、remote、commit/pushは触れない。model順・read-only sandbox・実行flag・gate値・repairAttemptedとcall数をassertする。既存repair成功/review/verification/normal pushのstate machineテストも維持する。gateの視覚確認・具体的proposal・stage整合・provenance・corpus条件を緩めていない。

## Recording OFF監査

FrameProcessorのDEBUG経路は`collectPipelineDiagnostics: debugVideoRecorder != nil`、Release経路はrecorderがnilなら通常detectSabers。recorder appendとdiagnosticSendStarted設定もrecorder存在時のみ。

履歴/候補copy/instability計算/geometry matching/PNG copyはDebugVideoRecorderのappend・observe・retain経路内。temporal選択・大量JSON生成は録画finalize/build内。endpointDiagnosticTrace生成はDetectionCoreの`collectEndpointDiagnostics`条件下で、BGRADetectionがrecording flagを渡す。通常の認識に必要な既存geometry計算は維持する。

XCTestはOFF状態で動く棒を25回認識し、fresh結果25件、motion history=0、diagnostic send callback=nilを確認する。通常解析とprofilingのみの両方でcandidateが実在し、pipelineDiagnostics=nil、rejection diagnostics空、endpointDiagnosticTrace=nilを確認する。既存録画ONのmetadata/PNG/windowテストも維持する。

## 次の人手実機benchmark

1. 同じ端末・30fps設定・照明・送信先で、Recording OFF、Debug Performanceを閉じ、Freeze Diagnostics OFFをbaselineにする。
2. Debug Performanceだけ開いてcamera FPS/frame interval、detection/processing、queue wait、frame replacement、send intervalの平均・p95/maxを保存する。profilingの観測負荷をbaselineと区別する。
3. 同じ動きをRecording ONで記録し、OFFへ戻して回復を確認する。ONのcopy/observation/JSON/Stop処理とOFFの認識時間を混ぜない。thermal状態と長時間動作も記録する。
4. 縦横/高速移動/交差/隠れ/背景反射でselected temporal PNG、peak、mapping、candidate/source履歴が揃うことをpreflightで確認する。
5. Wi-Fi/Personal HotspotとUnity入力の最終確認は実機で行う。sendStartedは受信/描画latencyの証明ではない。

既存host detector benchmark:

```bash
python3 ios/PhoneSaberSender/Tools/benchmark_detection.py --iterations 5 --json /tmp/phonesaber-detector-benchmark.json
```

これは40枚のhost detector時間で、iPhone実機FPSでもend-to-end latencyでもない。Mac画面変化からの既存latency試験は`start_phone_saber_latency.command`で起動する。UDP 5005/5006を共有するのでUnity受信と同時起動しない。

## LauncherとUnity

Desktopの3つのlauncher link、Startのdry-run、installerの反復、log lifecycle、最新timestamp session/複数PNG/空画像/欠けたreportのviewerテストを確認する。launcherとviewer本体は変更しない。root setup toolのlink検証が従来2つだけだった穴を3つへ修正し、そのinstaller test fixtureも3つに更新する。

Unity verificationは`PHONESABER_VERIFY_UNITY_RUN=1 bash Tools/verify_phone_saber.sh`で既存所有process確認を利用する。Editor所有時にはkillせずEditMode/PlayMode/CompileをBLOCKEDと報告する。今回Unity repositoryはクリーンで変更なし。

## 今回のverification結果

- iOS XCTest: **122/122 PASS、skip 0、assertion failure 0**。最終結果はiPhone 17 Pro / iOS 26.5、xcodebuild exit 0、xcresultのPassed、既存classifierのPASSを確認した。ログは`.verify-logs/phone-saber/20261001-145543/`。
- iOS 27での再実行は全test成功後にverbose simulator diagnostics収集が待機したため、その再実行のxcodebuildを終了して再試験した。26.5でも診断収集待機を確認し、sampleでsimctl診断収集待ちを特定した。今回のverification directoryを引数に持つ自分のsimctl collectorのみを終了すると、Xcodeは正式なxcresultを保存して正常終了した。テストhost/Unity/user processは終了していない。途中で中断した再実行をPASS扱いにはしていない。
- PhoneSaber Tools: **138/138 PASS**。tracking診断11件、preflight5件、real capture E2E4件（A–Gの7scenarioと追加gate integration）を含む。
- root Mac setup Tools: **6/6 PASS**。
- Detection / Static BGRA Detection: **PASS**。
- formal lossless corpus: **40/40 PASS**、positive **23/23**、negative false-positive **0/17**、optional fixture欠落0。
- iOS Release: **PASS**。
- `git diff --check`: **PASS**。
- Unity EditMode / PlayMode / Compile: **BLOCKED**（Editorが3D-Saberを使用中）。Unity無変更。verification全体のexit 2はこのBLOCKEDを表す。
- Desktop launcher: 3つのlinkが正しいrepoを指し、Start dry-run成功、latest log実在/読取り可。最新実session `phonesaber_20261001_005134_111` の10 PNGをviewerが解決できた（open呼び出しをinterceptし、windowは開かない）。既存launcher7件/viewer7件のtestもTools内で成功。

認識production directoryのdiffは空。実機FPS/latency、Unity testの追加実行は未確認として維持する。

## 今回変更したファイル

- `tools/setup_mac.py`
- `tools/test_setup_mac.py`
- `ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py`
- `ios/PhoneSaberSender/Tools/phone_saber_triage_receiver.py`
- `ios/PhoneSaberSender/Tools/phone_saber_auto_repair.py`
- `ios/PhoneSaberSender/Tools/phone_saber_tracking_diagnostics.py`
- `ios/PhoneSaberSender/Tools/phone_saber_tracking_e2e.py`
- `ios/PhoneSaberSender/Tools/test_phone_saber_tracking_diagnostics.py`
- `ios/PhoneSaberSender/Tools/test_phone_saber_tracking_preflight.py`
- `ios/PhoneSaberSender/Tools/test_phone_saber_tracking_e2e.py`
- `ios/PhoneSaberSender/Tools/TRACKING_VERIFICATION.md`
- `ios/PhoneSaberSender/Tools/README.md`
- `ios/PhoneSaberSenderTests/DetectionCoreTests.swift`
- `ios/PhoneSaberSenderTests/TrackingDiagnosticsCaptureHarness.swift`
