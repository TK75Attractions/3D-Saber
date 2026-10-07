# iOS 認識の無損失高速化（2026-10-08）

`perf/ios-detection`、変更前 `86d53d1`。production の変更は `DetectionCore.swift` のみ。
Mac の同条件測定で bright 平均 **11.231 → 6.751 ms（39.9%短縮）**。
Simulator の全 XCTest と iOS Release は下記の環境制限で未完了のため、未コミット。

## 変更と一致性

- 膨張の菱形近傍を行ごとの連続区間として `memset` で書き込み、unsafe buffer 内で処理する。対象画素・境界・出力値は同じ。
- 収縮も同じ菱形の行区間だけを読む。中心が0なら必ず落ちるため早期終了する。
- 白飛び画素の重複カウントと core-line の画素所属判定を、Set から bitmap に置き換える。bitmap は所属判定専用で、core-line の採点点列は従来と同じ row-major 順にソートする。
- 浮動小数点式・加算順序・近傍の訪問順・閾値・ranking・定数・コンパイラの浮動小数点オプションは変更していない。BGRADetection / FrameProcessor / UDP / UI / Unity / Android は未変更。

## 測定

Apple M5 / 32 GB、macOS 26.6.2、Xcode 27.0、Swift 6.4、`xcrun swiftc -O`。
`testOfflineBrightFrameTimingBaseline` の本文を変更せず Mac の XCTest ハーネスで実行し、PNG の読込のみ ImageIO + 同じ BGRA CGContext で代用した。**iOS Simulator の実行結果ではない**。
変更前と最終版のバイナリを交互に5組実行。各回は元テストどおり warm-up 2回、bright 12回。下記は5回の平均値。
初回調査では旧版19.318 msだったが、ホスト負荷の影響を避け、交互測定の値を比較に採用した。

| bright | 変更前 (ms) | 最終版 (ms) |
|---|---:|---:|
| 各回の平均の平均 | 11.231 | 6.751 |
| 各回の中央値の平均 | 11.216 | 6.751 |
| morphology（profile） | 3.115 | 1.454 |
| componentsScorePCA（profile） | 3.621 | 2.092 |
| lineScore（profile） | 1.786 | 0.849 |
| brightnessContrastColor（上記と重複する内訳） | 1.312 | 0.817 |

初期のホットスポットは morphology と、component 段に含まれる追加の close、採点中の画素ハッシュ操作だった。
詳細は一時ハーネス `/tmp/phonesaber-ios-perf/` に保存。`TimingTest.swift` と旧版・最終版をそれぞれ同じ `swiftc -O` / XCTest リンク条件でビルドし、`xctest-before` / `xctest-final` を交互実行した。各回のログは `xctest-final-pair-*.log`、集計は `timings-final.json`。測定ファイル・バイナリは Git に含めない。
実機の60fps持続と Simulator の速度差は未確認。

## 最終検証

- `make -C PhoneSaber/android/core parity`: **PASS**。269 PNG、27合成ケース、541 FrameProcessor 状態遷移、すべて mismatch 0。formal 40件の失敗0。
- `make -C PhoneSaber/android/core test`: **PASS**。
- 旧版と最終版の全候補・winner・pipeline/endpoint 診断の再帰スナップショット比較: リポジトリ内 **52 PNG / mismatch 0**。Double / Float は bitPattern、辞書は比較用にのみ整列（計算には不使用）。
- Static BGRA: **PASS**。3×3の全512パターン、半径0〜4、縦横1画素・非正方形・非二値入力も菱形の定義から求めた参照値と一致。
- native XCTest の timing テスト: 交互測定10回すべて **PASS**。既存の `testCoreLineCandidateScoringIsIndependentOfPointOrder` も本文を変えず native XCTest で **PASS**（重複・ランダム順序30回）。
- `git diff --check`: **PASS**。自己レビューで FP 式と演算順序、候補生成・採点・eligibility・端点・選択の意味が同じことを確認。

公式 `bash PhoneSaber/tools/verify_phone_saber.sh` は root から3回実行。キャッシュ権限エラーを `/tmp` の `CLANG_MODULE_CACHE_PATH` 指定で解消後、最終ログは `.verify-logs/phone-saber/20261008-014710/`。
**Detection / Lossless 40/40 / Diff Check は PASS、全段PASSには至っていない。**
iOS XCTest は CoreSimulatorService への接続が拒否され NOT_RUN（stage FAIL）。Tools は localhost bind 等が `Operation not permitted`（384 tests、8 failures / 33 errors / 1 skipped）。
iOS Release は既存 SwiftUI `@State` マクロの起動で `sandbox-exec: sandbox_apply: Operation not permitted` / malformed response（FAIL）。Unity は NOT_RUN、capability は Editor 所有により BLOCKED。
テスト・gate・期待値は緩めていない。

残作業は制限のない検証環境で公式全段PASSを確認し、計測・変更・検証結果を記した commit をこの branch に作ること。**push はしない**。
このセッションでは共有 Git 管理ディレクトリも writable roots の外にある。
