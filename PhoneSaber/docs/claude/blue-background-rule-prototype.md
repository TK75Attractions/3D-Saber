# 青背景ルール試作（2026-10-13、未採用・既定 OFF）

> **main には文書だけを入れている（2026-10-14、Claude）。** 実装は作業ブランチ `cx/mural-20261013`（この Mac のローカルのみ）に残してあり、認識コード・ビルド設定には入っていない。理由: ON にすると画面端の実青剣を1枚失い、既存の static suite も FAIL するため採用の見込みが低く、既定 OFF でも本番の認識ファイルに分岐を増やす利点が無い。以下の「スイッチ」「配線」の記述はそのブランチ上の話。

census の B を C++ / Swift に同じ条件で実装した。**本番採用は勧めない。既存ラベル付き剣82枚は保持するが、追加目視で画面端の実青剣を1枚失うことを確認した。** 正式コーパス・期待値・UDP・Unity asset は変更していない。中断時の `54b3466` の境界テストを継続し、commit / push はしていない。

## 実装とスイッチ

- 設定名は **`PHONESABER_BLUE_BACKGROUND_RULE`** 一つ。既定0、比較用1。既存 `PHONESABER_CORELINE_GUARD` は既定1のまま独立。
- BLUE の厳密な `color-mask` のみ、`pointCount × sampleStep² / (元画像width × height) ≤ .02` **かつ** `highValueRatio ≥ .35`。全BLUE eligibleに `colorPurity ≥ .25`。境界一致は通す。step=2ではcensusの×4と同じ。mask格子の寸法で割らない。
- 暫定閾値の置き場は言語ごとに `BlueBackgroundRule` 一つ：`android/core/include/phonesaber/core.hpp:57`、`ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift:428`。暫定であることをコメントに明記。
- 既存支持gate・core-line guard の後、winner選択の前（C++ `pipeline.cpp:520`、Swift `BGRADetection.swift:1191`）。除外後は現frameの既存score順の次候補。score・候補生成・PCA・端点・source・順位は変えない。除外理由の優先順はarea → HV → purity。
- C++ CLIはON時だけ `blueBackgroundRejection` を出す。Swiftは候補に理由を保持し、Debug Recordingの既存rejectionReasons/rulesへ記録。診断採取時は値・比較・閾値も残す。採取なしでも理由名を残す。既存top3・24KiB圧縮予算・32KiB preflightを維持。
- Make（mode stamp・出力先も両switchで区別）、CMake PUBLIC定義、Gradle全build type、Xcode Debug/Release、公式verifyのXCTest/Release引数、static host scriptへ配線。
- 共通 `guard_build.py` 経由で parity、frame reference、JNI host check、fixture expectation generator、recognition benchmark、正式lossless runner、Swift benchmark、診断parity、video diagnostic、repair/tracking E2E、sender_soakへ伝播する。共通設定の全4組合せと不正値はunitで確認。
- 解析専用core-line shadow/evaluationは従来の比較条件を固定する。今回の証拠・時間計測は `--rule blue-background` によりG=1を固定してBだけを切り替える。通常のproduction benchmarkは同じ環境変数を読む。正式期待値生成toolを実行して期待値を更新してはいけない。

```bash
# OFF（通常）
make -C PhoneSaber/android/core test parity
CLANG_MODULE_CACHE_PATH="$TMPDIR/phonesaber-census-module-cache" SWIFT_MODULE_CACHE_PATH="$TMPDIR/phonesaber-census-module-cache" \
  python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json "$TMPDIR/phonesaber-blue-background-evidence/formal-off.json"

# ON（比較専用）
PHONESABER_BLUE_BACKGROUND_RULE=1 make -C PhoneSaber/android/core test parity
PHONESABER_BLUE_BACKGROUND_RULE=1 CLANG_MODULE_CACHE_PATH="$TMPDIR/phonesaber-census-module-cache" SWIFT_MODULE_CACHE_PATH="$TMPDIR/phonesaber-census-module-cache" \
  python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json "$TMPDIR/phonesaber-blue-background-evidence/formal-on.json"

# 直接呼ぶbuild systemには同名build設定を渡す。Xcode GUIでの変更は不要。
xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -sdk iphoneos -derivedDataPath "$TMPDIR/phonesaber-blue-ios" CODE_SIGNING_ALLOWED=NO PHONESABER_BLUE_BACKGROUND_RULE=1 build
cmake -S PhoneSaber/android/core -B "$TMPDIR/phonesaber-blue-cmake" -DPHONESABER_BLUE_BACKGROUND_RULE=1
# Gradle / 公式verifyは環境変数。公式verifyはoperatorが実行する。
PHONESABER_BLUE_BACKGROUND_RULE=1 bash PhoneSaber/tools/verify_phone_saber.sh
```

## 検証結果

| 実行した検証 | OFF | ON |
|---|---:|---:|
| `make -C PhoneSaber/android/core test parity` | PASS | PASS |
| C++ / Swift 全候補・Double bit parity | 325 PNG、不一致0 | 325 PNG、不一致0 |
| 合成 / frame transitions parity | 27 / 541、不一致0 | 27 / 541、不一致0 |
| Swift 正式lossless（source・端点許容差・必須棄却） | **40/40** | **40/40** |
| C++ 正式checker / 選択変更 | 40/40 | 40/40、変更0 |
| Swift 境界unit＋実serializer/preflight | PASS | PASS |
| CMake host＋CLI、24変更画像のMakeとの全bit JSON比較 | PASS | PASS |
| Android NDK arm64-v8a / API29 core＋JNI shared library | PASS | PASS |
| 既存static BGRA suite / DeviceHealth XCTest | PASS / 13件PASS | **FAIL**（広い青合成棒の除外）、後続XCTest未実行 |
| iOS Release `xcodebuild … -sdk iphoneos … build` | 環境エラー | 同じ環境エラー |

OFFはmain `83fc03d` の認識コードからビルドしたbaselineと全696ファイル / 657 unique PNGで候補全体・Double bitsが一致（不一致0）。ONとの差も657画像で独立照合し、指定除外と理由以外の候補field/score/端点/順序の変更0、RED変更0。正式manifest/PNG/期待値のdiffは0。

両言語のunitはarea capの一致/1点超、HV/purityの一致/nextDown/nextUp、source限定、RED無影響、step=1/2を確認。中断時の実装前失敗ログはTMPDIRの `phonesaber-blue-before-cpp.log` / `phonesaber-blue-before-swift.log`。合成の7136/307200面積mask（1784 sample点）はOFFで出力、ONでarea理由によりnullになることをC++ integrationでも確認。

診断圧縮の回帰：2位の青除外候補の理由が、旧triageではtop1へ圧縮されて消える。同じ新版test＋旧 `DebugRecordingTriage.swift` の一時コピーで失敗を再現し、修正後OFF/ONともPASS。context最大 **17,154 bytes**、実transportの32KiB preflight PASS。上限を拡張していない。

追加の画面端剣回帰はC++ / SwiftそれぞれOFF/ONで既存frame-0344を読み、BGRA・padded RGBAと候補bitsのparity、OFF出力/ON null、HV理由、score/端点の不変を確認してPASS。正式期待値には追加していない。Pythonの共通設定2件、証拠抽出3件、recognition benchmark11件、census unit10件もPASS。`git diff --check`、変更shellの `bash -n` PASS。

iOS Releaseの両buildは `sandbox-exec: sandbox_apply: Operation not permitted` によりSwiftUI macro pluginが起動できず失敗。認識・recorderのhost compileは成功しているが、iOS全体buildのPASSとは扱わない。Xcode Debug ON展開設定とRelease compile commandには青条件を確認。Gradleの `:app:testDebugUnitTest :app:assembleDebug :app:assembleRelease` はTMPDIRへcacheを移して実行したが、内部socketが `Operation not permitted` となり設定評価前に失敗。APK・Gradle伝播の実ビルド確認は未了。

公式verify、Simulator/XCTest iOS suite、実機カメラ/UDP送信/遅延、Android APK/端末での試験は未実行。GUI・Unity・Playerを起動していない。

## host時間（OFF / ON）

macOS arm64、Apple clang 21.0.0 / Swift 6.4、C++ `-O2 -ffp-contract=off` / Swift `-O`。正式35 unique PNG＋会場2 bundleの22 ORIGINAL＝57画像、step=2、各pass10 warmup＋100測定。画像ごとにOFF/ON/ON/OFFを隣接実行し、各pass中央値をmode別に平均、画像を等重みに平均した。

| 区間 | OFF ms/frame | ON ms/frame | 差 |
|---|---:|---:|---:|
| C++ `FrameProcessor.process` | 3.402319 | 3.434762 | +0.032444（+0.954%） |
| Swift `analyzeSabers` | 6.171761 | 6.157757 | −0.014004（−0.227%） |

PNG decode・BGRA変換・JSON/通信/表示は区間外。Swiftはtrackingなし、C++はtrackingを含むので言語間の絶対時間を比較しない。他の検証build完了後に実行した1回のpaired host計測で、ownerのMac作業による負荷は制御していない。小差を改善/悪化と断定せず、iPhone/Android実機の遅延・fpsへ外挿しない。全画像ごとのpass値は `timing/cost.json` / `timing/swift-cost.json`、進捗と集計は `timing.log`。

## ラベル付きbenchmarkと目視を分ける

全696ファイル、SHA重複除外657画像、37 inbox bundle。既存hash検証済み手ラベル・formal truth・物体CSV・census追加目視77 frameを同じ規則でjoin（追加ラベルは既存TMPDIRのannotations.jsonを読取）。欠損/衝突0。選抜された窓の混合集団で、会場での発生率ではない。位置はbox内の軸中点（padding8px）proxyで、両端正解率ではない。

| 指標 | OFF → ON |
|---|---:|
| BLUE 剣あり出力 | **82 → 82 /82**、失う0 |
| BLUE 位置合致 | **54 → 54 /59**、失う0 |
| BLUE 剣なしFP | **44 → 33 /178**、除去11・追加0 |
| BLUE unknown出力 | 299 → 288 /397、変更12（null化11、別候補1） |
| RED 剣あり / 位置 / FP | 86/99 / 61/77 / 77/153、すべて不変 |

census本文の位置52/59と違うのは、今回のbaselineには採用済みG(.6)がONで、既に2位置を救済しているため。Bの追加効果は位置保持のみ。壁画窓 `164236_960` の青FPは9→4。残る32357/32358はcore-halo、32359/32363はcore-line。PC/衣服への青FPはこの限定条件では解決しない。

**追加目視で benchmark のunknownから実剣1枚の悪化を確認した。82/82の保持を全画像無害と言い換えない。**

## 重大度順の所見と採用リスク

1. **高・ONで実剣を失う**：Swift `DetectionCore.swift:445` / C++ `core.hpp:70` のHV条件。既存 `production-video-IMG_5933/frame-0344.png` は手から伸びる青発光剣が画面左端に一部だけ見える。OFFは `color-mask [6,632]–[6,592]`、70点、HV=.25、purity=.32830、ONはHV理由でnull。手のブレ/遮蔽/画面端を伴う。既存annotationはambiguousでbenchmarkの剣分母には含まれない。これは私のORIGINAL目視による **wrong-after**。剣の全長・速度・露出時の物理姿勢は確定できない。会場でも同じ条件の淡い/速い青を失い得る。
2. **高・近接大面積の保持未達**：面積cap（Swift `DetectionCore.swift:441` / C++ `core.hpp:69`）。未変更static suiteはONで大面積青の検出assert（`StaticBGRADetectionTests.swift:180`）に失敗する。支持gateの検証を一時的に面積除外と区別して進めた比較実行でも、後続の `blue horizontal` を失った。正式expectationではなく合成の大面積棒で、suiteを緩めてPASSにはしていない。0.5mの実剣での安全性は未確認。
3. **中・壁画FPが残る**：選択位置 Swift `BGRADetection.swift:1191` / C++ `pipeline.cpp:520`。`164236_960 f32358` はmaskを外しても同じ壁画のcore-haloへ移る（**both-wrong**）。他の残存line/haloもこの条件外。長い線が短くなっただけで実剣への修正と数えない。
4. **低・診断の理由欠落を修正**：`DebugRecordingTriage.swift:1603`。ONで2位のeligibleを除外して1位が残る場合、旧top1圧縮では2位の理由が消えた。青除外が既存top3にある場合は3件を残す。修正前FAIL/修正後PASSとサイズgateを確認。

ruleによって変更した24 unique画像（25ファイルoccurrence、5 session）の全枚を目視。**correct-after21 / wrong-after1 / both-wrong2 / cannot-tell0**。correct-after21はすべて点灯青剣なしの背景出力除去。実剣の不当短縮を新たに確認した枚数0、実剣のnull化1。遠方・淡い・速振り・端向きのラベル付き出力は変更0だが、独立swingの保持保証ではない。

もう一つのboth-wrongは `20260923_143446_247 f82`：窓/明点を結ぶ別の背景lineへ移る。画面端の光と青照射があり剣全体のpresenceはunknownだが、両軸とも実剣を説明しない。変更後を正解へ数えない。

## 証拠パックと再生成

写真はrepoにコピーしていない。全入力inventory・候補・変更CSV/JSON・original/OFF/ON contact sheets・端点周辺の3倍zoom・各枚の判定と理由は **`$TMPDIR/phonesaber-blue-background-evidence/`**。`REVIEW.md` から24枚へ辿れる。frame-0344の手/剣cropも同ディレクトリ。画像・パスは私的なのでrepoへ移さない。

```bash
python3.12 -B PhoneSaber/tools/coreline_guard_evidence.py --rule blue-background --output "$TMPDIR/phonesaber-blue-background-evidence"
# 目視判定は同じhash/色/両端なら保持する。再描画だけなら --render-only
python3.12 -B PhoneSaber/tools/blue_background_rule_evaluation.py --output "$TMPDIR/phonesaber-blue-background-evidence" --annotations "$TMPDIR/recognition-census-20261011/annotations.json"
python3.12 -B PhoneSaber/tools/coreline_guard_benchmark.py --rule blue-background --output "$TMPDIR/phonesaber-blue-background-evidence/timing" --iterations 100
python3.12 -B -m unittest discover -s PhoneSaber/tools -p test_blue_background_rule.py -v
```

annotations.jsonが無い環境では既存ラベル/物体CSVだけで評価できるが、censusの分母82/178とは別として報告する。目視判定を新しい出力へ無条件で引き継がない。formal選択変更または期待値不一致ならevaluationはSTOPする。

## 会場で採用判断に必要な「振りだけ録画」

**現ruleをそのまま採用するには、frame-0344の実剣lossと大面積合成lossをまずoperatorが評価する必要がある。新captureだけで既知の悪化を無視しない。** 今回は例外・閾値・救済を実装せず、画面端/低HVの扱いの再設計を提案するに留める。

その再設計と採用判断を支える具体的な1本：本番固定画角で青緑壁画を部分的に入れ、投影ON、背後を人が歩く。露出1/100秒・camera試験設定OFF、約51秒の **「振りだけ録画」** を使い、開始/停止しに近寄る場面を検証対象にしない。

- 0–6秒：3m位置で青OFF。壁画のFP窓を保存。
- 6–14秒：淡いdiffuser青ON、横/斜め静止→先端をカメラへ向ける。姿勢ごとに2秒保持。
- 14–30秒：3mで別々の速い横/斜め/頭上swingを最低2回ずつ、各回を1秒静止で区切る。画面端への出入り、半分だけ見える剣、手に重なる端向きを含める。
- 30–36秒：青OFF、同じ背景/投影/人を維持。
- 36–45秒：0.5mで大きい青を画面内に見せ、静止→速振り→端向き。棒以外の操作を撮る時間にしない。
- 45–51秒：青OFF。

swing中のbefore/event/after ORIGINALが保存されていることを必ず確認。色別presence・実剣box・見える軸の両端・画面端で可視の部分を手ラベルし、OFF/ONを全保存窓に適用。独立swing/姿勢ごとのnull化・別領域移動・不当短縮が **0**、淡い/遠い/速い/端向き/部分剣/近接を保持し、OFF壁画の出力が減ること、残存4種類の候補を同定し、formal40/40・bit parityを守ることが必要。既知frame-0344も解決する案であることを確認してからoperatorが採用を決める。上限を緩める／HV免除／core-line除外の追加は別提案で、今回は未実装。
