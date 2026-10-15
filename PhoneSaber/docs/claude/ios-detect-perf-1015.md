# ios-detect-perf-1015 完了レビュー（2026-10-15）

基準 main=b998225、前回 eed3d70。専用メモは存在せず、perf_ios_detection.md / ios-detect3.md は別変更として確認した。
- 中: benchmark_detection.py:47。陽性だけ／陰性だけの有効な manifest で空群の median が例外終了。空群を samples=0 とし、両ケースの回帰を追加（修正前2 error→修正後PASS）。
- 低: DetectionCore.swift:922 の近傍展開は撤去。実比較で高速化の確証なし（他テスト並行、blade median 11.634→11.712 ms）。認識本体は main とバイト一致。
- benchmark の loader 契約・p95・分類の前回修正は維持。定数1だけのテストを補い、異なる20値の p95、通常実行、単一群、source-root を検証。
- P2PTransportTests.swift:1 の import 修正を維持。同じ standalone typecheck で main は NWError 未解決、最終版は成功。
- runtime / recognition / UDP / formal corpus / Unity / ProjectSettings の最終差分なし。既定動作は同一。
- 新規 C# / Swift / Kotlin なし。.meta / project 登録の追加対象なし。新規オプション・TODO・stub の追加なし。

実行結果（ログ: /tmp/phonesaber-finish-ios-1015/）:
- tools 内: PATH=/Users/satoshi/.pyenv/versions/3.12.1/bin:$PATH python3 -B -m unittest discover -v → 287件、OK（Windows/PowerShell 11 skip、既存 expected failure 1）。
- iOS Tools 内: PHONESABER_BACKGROUND_BENCHMARK_INTEGRATION=1 python3.12 -B -m unittest discover -v → 442件、setUpClass 1 error（録画E2E、下記）。追加3件・実 inbox 検証はPASS。
- 初回 python3=3.9 の両 discover は依存不足・整数桁制限差で失敗。soak の初回ビルドも撤去編集と衝突。上記はソース固定後の全体再実行結果。
- benchmark_detection.py --iterations 5 --baseline-source-root /tmp/phonesaber-finish-ios-1015/baseline --json /tmp/phonesaber-finish-ios-1015/benchmark.json → 実ビルド400 samples、200組の選択・整数カウンタ一致。
- benchmark_detection.py --manifest /tmp/phonesaber-finish-ios-1015/manifest-True.json --source-root PhoneSaber --json /tmp/phonesaber-finish-ios-1015/single-positive.json → 5 samples、空群 samples=0。
- 撤去前の main/HEAD を同じ Static テスト・swiftc -O で比較: 合成72＋PNG 52の候補・診断・整数カウンタ署名がビット一致。
- make -C PhoneSaber/android/core test parity → PASS。PNG 325、合成27、状態遷移541で不一致0、formal 40/40。
- python3 PhoneSaber/tools/check_pc_latency.py → PASS（6構成compile、NUnit 35）。check_auto_latency.py → PASS（NUnit 5、Swift 3、実C# loopback）。
- bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh → 撤去後もPASS（Static BGRA＋DeviceHealth 13）。
- PhoneSaber/ios/PhoneSaberSenderTests/run_{latency,send_gap,debug_transfer,camera_lifecycle,high_speed}_host_tests.py → 全PASS。latency 27＋配送6、send_gap 21、転送7、lifecycle 18、high_speed Release/DEBUG 各2。カメラ/UIの実機検証ではない。
- git diff --check → PASS。保護対象・新規ソースの検査もPASS。

未確認・既存環境障害:
- 録画E2Eは2回とも insufficientDiskSpace(required:1040187392, available:0)。df は416 GiB空き。容量APIが0を返す環境で、安全ゲートは変更していない。
- xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /tmp/phonesaber-finish-ios-1015/ios-release CODE_SIGNING_ALLOWED=NO PHONESABER_CORELINE_GUARD=1 build → FAIL。sandbox-exec 拒否で SwiftUI State macro が malformed response。
- iOS XCTest・公式verifyは未実行。operator の通常の公式検証1回で、録画E2E・XCTest・Release を確認する（このsandbox外、Python 3.12）。
- operator コマンド（repo root）: PATH=/Users/satoshi/.pyenv/versions/3.12.1/bin:$PATH bash PhoneSaber/tools/verify_phone_saber.sh
- Unity EditMode / 実機は未実行。runtime差分を撤去したため、本件固有の追加試験は不要。通常の公式verifyの範囲は維持。
- GradleはAndroid差分なしのため未実行。Windows/PowerShell実行はMacでは未検証。
- commit / push は行っていない。
