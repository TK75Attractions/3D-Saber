# predict3 技術メモ — 2026-10-14 拒否レビュー対応
基準: merge-base ea7848d / HEAD 3785360。git log -5、main...HEADをレビュー。commit/pushなし。
P1: check_endpoint_predictor_parity.py:124とEditor共有fixtureテストの許容誤差を撤去。1 bit不一致も拒否。
P2: PhoneSaberOperatorOverlay.cs:260–262の旧60ms/0.35表示を、手動60ms/0.35・自動120ms/2 worldと露光項へ訂正。
回帰: CLI/Unity共有テストの1 bit改変、実表示Labelのテストは各旧コードでFAIL→修正後PASS。期待fixtureは不変。
MonoがCSVの-0を+0に読む差はテスト入力で符号を保持して解消。予測演算は今回変更していない。
check_hit_feelにUnity実Vector2 DLLでのMonoホスト実行を追加。native lifecycle/Editor実行の代用とはしない。
初期OFF・手動演算/保存は不変。手動240予測960成分はea7848dの実C#ともbit一致。
前回メモの意図した自動LS/120ms/2 world・総遅延への露光追加は維持。F8→InputPoint→PredictAdaptiveに到達。
評価ツールの既定写像は前回差分のprojective（affineで従来を再現）。比較/7種ablation/候補上限もCLI回帰で実行。
新規.cs/Swift/Kotlinなし、変更したAssetsの.csには既存.metaあり。認識/UDP/formal corpus/scene/prefab/meta/ProjectSettings差分なし。
初回python3のtoolsは239件/5 ERROR（numpy不足）、iOS Toolsは434件/1 FAIL（旧Python整数制限）/1 ERROR。
指定Python3.12.1で再実行: 両ディレクトリで python3.12 -B -m unittest discover -v。
tools: 271件 OK（6 skip、1 expected failure）。iOS Tools: 433件/1 ERROR/1 skip、FAILなし。
iOS ERRORはtest_phone_saber_tracking_e2eの容量照会available=0/required=1040187392。該当iOSコードはmerge-baseから不変。
PASS: tools内 python3.12 -B -m unittest -v test_tracking_report test_hit_feel_replay、29件。
PASS: python3.12 -B PhoneSaber/tools/check_endpoint_predictor_parity.py --before-ref 977d5c6、Python手動240・実C#手動240/自動2240予測8960成分bit一致。
ea7848dを--before-refにすると凍結baseline列の対象版ではないためFAIL。正しい基準977d5c6ではPASS（baseline列は不変）。
PASS: python3.12 -B PhoneSaber/tools/check_hit_feel.py --before-ref ea7848d、旧5 FAIL再現→.NET/Mono stub各12・実Vector2 Mono11 PASS、30万frame allocation=0 B。
PASS: python3.12 -B PhoneSaber/tools/check_pc_latency.py --compensation-before-ref ea7848d、旧4 FAIL再現→35 PASS、Unity API4構成/Editor test assembly2構成compile。
PASS: python3 -B PhoneSaber/tools/check_auto_latency.py、C#5・Swift3 PASS、Swift→実C# Loop UDP metrics PASS。
PASS: make -C PhoneSaber/android/core test parity PYTHON=python3、4 tests/325 PNG/541遷移/27合成、不一致0。Android差分なし、Gradle対象外。
PASS: bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh、BGRA検査・DeviceHealth13 tests。
PASS: python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_{camera_lifecycle,debug_transfer,latency,send_gap}_host_tests.pyを個別実行、15/7/(27+6)/18 tests。
PASS: python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py、formal40/40・positive23/23・negative0/17。
PASS: tracking_report.py 実録20261013-{154251,154804}/20261014-{040012,040239}を --actual-csharp --lead-ms 106 --compare-ref ea7848d --json /tmp/... で再生。
040239だけ --check-targets、PASS。30fpsのturn-max49.85/49.12%、154804の2.5Hz RMS OFF63.18→64.06%悪化も再現。
上記自動の高速往復リスクは残る。調整は実機比較後の提案のみ、今回実装なし。
FAIL（環境）: xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /tmp/predict3-review-ios-build CODE_SIGNING_ALLOWED=NO build。
SwiftUI macroのsandbox_apply EPERM。通常環境でReleaseを再確認。実Unity/Player、Simulator XCTest、実機/GPU/投影機、公式verifyは未実施。
operator残検証1: PhoneSaber/tools/verify_phone_saber.sh（既存Editor終了後、通常環境）。iOS Release/XCTestと容量ERRORも再確認。
残検証2: repo rootから open -g -j -n -W /Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app --args -batchmode -nographics -projectPath "$PWD" -runTests -testPlatform EditMode -testFilter 'PhoneSaberEndpointPredictorTests;PhoneSaberAdaptivePredictionTests;PhoneSaberLatencyCompensationTests;PhoneSaberLatencyEstimateTests' -testResults /tmp/predict3-unity.xml -logFile /tmp/predict3-unity.log。
公式verifyの既存filterは上記4クラスを含まない。実Editorでの厳密bit一致はこの実行で確認する。
残検証3: F9後F8のOFF/20/40/60/自動を赤青で確認、表示120ms/2 world/露光・再起動保存・60/30fps折返し・100ms欠測をGPU/投影機込みで比較。
ログ/再生JSON/生成物は /tmp/predict3-review-*。git diff --check PASS。GUI/Dock/フォーカスを取るアプリ起動なし。
