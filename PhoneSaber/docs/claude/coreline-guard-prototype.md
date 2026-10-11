# core-line guard G (.6) 採用準備（2026-10-12）

**独立した4 session（保守的に4 event）で、過連結から実剣上への改善を目視確認した。正しい出力が誤りへ変わった例は今回の変更19件では0。ただし ON 後も誤り4件、判定不能1件があり、全変更の正解・全長保持・速振りの無害性は保証できない。**

`opt/coreline-guard-prototype` の採用確認用ビルドは **既定 ON** に揃えた。main への採用・配布は operator の判断待ち。中断コミット `360e0eb` をレビューして継続した。今回は新しい commit / push なし。
`CLAUDE.md §4`、正式 corpus・期待値を変更していない。以下は過連結 guard の証拠であり、僅差 winner の temporal CASE A を別 swing で2回確認したという主張ではない。公式 verify / Android APK build / 実機再試験は operator が残りを実施する。

## 全 ORIGINAL の証拠パック

派生物は **`$TMPDIR/phonesaber-guard-evidence/` のみ**。人が写る原画像・切り出し・overlay を repo に追加していない。元の inbox / Downloads / fixtures は read-only。

- repo 全 `Fixtures`、既存 labelled benchmark の hash 検証済み参照元と forensic sources、diagnostics-inbox **全36 bundle**を走査。`images/` 外も再帰走査する。annotated / annotation / overlay は認識入力から除外。
- **685ファイル / SHA-256重複除外646 PNG**（forensic 354、inbox ORIGINAL 279、fixture 52）。annotated 11を除外。既存ラベルの欠損0。両色を ON/OFF 再認識し、選択候補の全出力（source・端点・score・geometry・Double bit等）の差を抽出。端点が偶然同じでも別候補なら記録する。
- **18 PNG・19色別変更・5 session**。新規 null 0、既存 no_saber ラベルの選択変更0。旧研究の662枚に、新しい10月10日の他 bundle の23 ORIGINALを加えた全走査である。
- 全19変更を ORIGINAL と端点付近3倍表示で目視：`correct-after` **14**、`wrong-after` **0**、`both-wrong` **4**、`cannot-tell` **1**。OFF はすべて実剣から無関係な支持へ延びている。`wrong-after` は「OFF が正しかったのに ON が誤った」場合を指す。
- `correct-after` は可視の実剣上に位置・軸が戻った目視判定。厳密な両端 ground truth / 数pxの許容差 / 剣全長の保持率を測ったラベルではない。短線が横向きの青剣に対して別方向なら `both-wrong` とした。
- 既存 benchmark ラベルは変更せず、派生 JSON / CSV に `benchmark_label` を保持。今回 ORIGINAL で確認できた剣あり画像は `label=saber` / `label_source=pixel review`、既存ラベルがある画像は hash 検証済みラベルを残した。認識結果を剣ありラベルの根拠にしていない。

`inventory.json` は全入力のパス・SHA-256・session/frame・ON/OFF recognition signature、`changes.json` / `changes.csv` は各変更の色、ラベル根拠、source、両端、判定理由を収録。JSON は全候補と guard snapshot も残す。
各 session の `*_contact.png`（全変更）、`*_page_01.png` 等（2変更/ページ）、`*_frame_<ID>_<colour>.png` を生成。左 ORIGINAL、右 OFF 黄 / ON 緑、下段は各端点中心の**原画素3倍**。原画像の向きは変えていない。大きい contact は page 版で確認できる。

### 独立 event の数え方と代表証拠

同じ構えの近接 frame は1 event。同時の RED / BLUE も1 event。別 session を独立とし、同 session の別 swing は今回は増し数えない。4 session に明瞭な修正があるため **4 event**。完全な swing-only 時系列や同一hold内の成功率はこの静止画だけでは分からない。

| 独立 session / event | 目視した変更 frame・色 | 代表 OFF → ON endpoints（元画像px） | 判定と制限 |
|---|---|---|---|
| `20260922_101530_202` / 青を横持ち | BLUE 355,372,378,382,391,392,397,398,399,407,409,411 | 391: `[54,96]–[154,226]` → `[130,212]–[166,224]` | 窓までの長線を剣の軸へ戻す9変更。372/382/398は短い別方向の線で両方誤り。1 eventだけ数える |
| `20260923_113421_868` / 赤青を身体前で交差 | RED 124760,124880 | 124880: `[184,330]–[366,118]` → `[168,328]–[208,310]` | 124880は背景までの長線から赤剣へ戻る。124760は下記判定不能。別 swing 2件には数えない |
| `20261010_162838_460` / 赤を横持ち | RED 5027 | `[148,236]–[208,334]` → `[180,330]–[272,310]` | 消灯した別の棒付近へ延びる線から、点灯した赤剣の軸へ戻る |
| `20261010_163949_658` / 遠い両色 | BLUE 21952,21953 / RED 21953 | BLUE 21952: `[16,66]–[310,114]` → `[14,66]–[52,76]`; RED 21953: `[14,30]–[90,118]` → `[80,84]–[88,136]` | 柱/背景や別方向へ延びる線を実剣へ戻す3変更。同時の両色で1 event |

session名はすべて `phonesaber_` prefix付きでパックに収録。最初の旧8ラベル付き画像を8 eventとして扱わない。

### 成功に数えなかった変更

| session / frame / 色 | ON endpoints | 判定・根拠 |
|---|---|---|
| `20260922_101530_202` 372 BLUE | `[148,212]–[154,228]` | both-wrong。横向きの発光剣に対して短い横断方向 |
| 同 382 BLUE | `[148,208]–[156,226]` | both-wrong。同上 |
| 同 398 BLUE | `[132,220]–[142,204]` | both-wrong。旧ノートの「近傍へ戻る」を端点正解と解釈しない |
| `20260922_130726_609` 183910 BLUE | `[130,214]–[148,224]` | both-wrong。実剣の先端寄りの一部分にしか乗らず大幅に短い。独立修正eventに加算しない |
| `20260923_113421_868` 124760 RED | `[432,570]–[474,606]` | cannot-tell。赤い発光剣は見えるがぶれと右端の切れで軸・両端を確証できない。OFFの長線は服へ延びている |

guard は「支持する別候補を必ず選ぶ」処理ではない。過連結候補を除外した後、元のscore順で次の surviving eligible を選ぶ。372/382/398では別の短い core-line が残り、剣の軸へ戻らない。

## プレイヤーに何が変わるか

特定の暗い空隙をまたぐ core-line が窓・柱・別の棒などへ伸びて勝っているとき、同 frame の実支持を持つ近い別候補を根拠にその長線を落とす。剣が背景まで急に長くなったり別方向を向く現象を一部減らす。生成・score・端点推定・UDP・tracking/hold の方式は変えない。長さを一律に切る処理ではない。

壁画の BLUE FP9/11は変わらず、この guard だけで背景誤検出は解決しない。ONでも短い別方向候補や不当な短縮が残り得る。

## guard の実装

C++ `android/core/src/pipeline.cpp` と Swift `DetectionCore.swift` / `BGRADetection.swift`。既存 ranking と全 eligibility gate（元画素の赤・青支持判定を含む）の後、winner選択直前に適用。

除外対象は eligible、source厳密に `core-line`、`used_point_led_fallback=true`、全raw区間の実支持被覆 **.60未満**の候補。同色・現frameの別 eligible **non-core-line** 候補が以下をすべて満たすときのみ除外する：

- core support ≥ .40、high-value ratio ≥ .35、全raw区間の実支持被覆 ≥ .90。
- final endpoints のいずれか同士の距離 ≤ 30 元画像px。

被覆測定は支持点配列の既存順で PCA を求め、全min/max投影区間を1 sample格子bin（`ceil(hi-lo)+1`）に分け、丸めた投影binに実core/color maskの画素がある場合だけ占有する。暗い補間bridgeは軸計算には残るが占有には数えない。step=2では1 binは約2px。mask支持は物理的な発光の証明ではない。
候補ごとに測定をcacheし、対象lineがなければ測定しない。Set/Dictionary順を浮動小数点演算へ持ち込まない。body端点採用済みline・支持別候補なしlineは保持する。仮置き閾値 `.60/.40/.35/.90/30px` 自体は今回変更していない。

## ビルドの既定値と唯一の比較スイッチ

外向けの設定名を **`PHONESABER_CORELINE_GUARD`（既定1、比較時0）** に統一した。旧 `CORELINE_GUARD=1` のopt-in手順は廃止。

- Swift/C++ coreそのものが、設定なしでON。単独コンパイルや検出コードを含む新しいharnessもONになる。
- Xcode projectのDebug/Release両方に既定1とSwift compilation conditionを定義。target Debugは `$(inherited) DEBUG` でproject条件を保持。XCTest/Releaseも同じ設定。
- Android Gradleは環境変数を全build type共通のCMake引数へ渡す。CMake coreは既定ON、OFF定義をPUBLIC伝播してJNI/toolsを揃える。
- core Makefileは環境変数/同名make変数を受け、ON/OFF別build directory。明示した共通BUILDでもmode stampで再ビルドする。
- `guard_build.py` を parity、frame reference、JNI host check、fixture expectation generator、recognition benchmark、正式lossless runner、Swift benchmark、診断parity、video diagnostic、repair/tracking E2E、sender_soakで使用。
- static detector runnerも同じ値。公式verifyは値をhost toolsとXcode Debug/Releaseへ渡す。UI/Unity用harnessで検出を省いて固定結果を使うものには認識処理自体がない。
- `coreline_shadow.py` はguard比較研究なのでbaselineを明示OFF。evidence/evaluation/timing toolsは意図的に両modeを別コンパイルする。

OFFも既存の正しい候補・score・端点を使う。OFF/ON切替後に比較用buildを混ぜない。

```bash
# 通常（設定なしで ON）
make -C PhoneSaber/android/core test parity
python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json "$TMPDIR/phonesaber-guard-evidence/formal-on.json"

# 比較だけ OFF：全 script / Make / Gradle / 公式 verify はこの環境変数
PHONESABER_CORELINE_GUARD=0 make -C PhoneSaber/android/core test parity
PHONESABER_CORELINE_GUARD=0 python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json "$TMPDIR/phonesaber-guard-evidence/formal-off.json"
PHONESABER_CORELINE_GUARD=0 bash PhoneSaber/tools/verify_phone_saber.sh

# 直接呼ぶ build system には同じ設定名をbuild引数として渡す
xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release PHONESABER_CORELINE_GUARD=0 build
cmake -S PhoneSaber/android/core -B "$TMPDIR/phonesaber-guard-evidence/cmake-off" -DPHONESABER_CORELINE_GUARD=0
```

Xcode GUIはBuild Settingsの同名値を0にする。採用確認では1へ戻す。内部のdisable compilation conditionを個別編集しない。

## 今回の検証

| 検証 | ON（既定） | OFF（比較） |
|---|---:|---:|
| `make … test parity` | PASS | PASS |
| C++/Swift 全候補bit parity | 314 PNG、不一致0 | 314 PNG、不一致0 |
| 合成parity / frame transitions | 27 / 541、不一致0 | 27 / 541、不一致0 |
| 正式lossless（source・端点許容差・必須棄却を含む） | **40/40** | **40/40** |
| 正式positive / negative FP | 23/23、0/17 | 23/23、0/17 |
| Xcode展開設定 | Debug/Release ON確認 | Debug OFF確認 |
| SDK付属CMakeのhost build / 全19変更のCLI比較 | PASS / bit JSON一致 | PASS / bit JSON一致 |

314 PNGは正式35 unique PNGとinbox ORIGINAL279。候補score、production Double bit pattern、端点、source、eligibility、候補順、guard snapshotの被覆bitまで一致。正式manifest/PNG/期待値の編集なし。今回の正式期待値変更は不要だった。
Swift guard/診断＋shadow Python7テスト、recognition benchmark Python11テストPASS。診断contextは17,096 bytes、実32KiB preflight PASS、top3/24KiB圧縮予算を維持した。
証拠抽出Python3テストPASS。OFFの省略/ONのnull診断欄は同じ未適用snapshotとして正規化する。端点が同じでも候補のDouble bitが変わる場合は変更を検出し、inboxのeventファイル名から実frame IDを抽出する。
static BGRA detector / DeviceHealth XCTest5件とsender_soak Release host compileもPASS。CMakeはSDKの3.22.1を使い、ON設定を省略した既定buildとOFF=0を両方確認。OFF定義のcore→tools伝播と参照CLIへのbit JSON一致を確認した。Android NDK/APK実ビルドの代わりにはならない。

中断前の試作で、旧662ファイルに対しOFFと出発点の全候補bit不一致0、ONとG(.6) shadow選択不一致0、正式選択変更0を確認済み。今回の全入力685ファイルは別途 `inventory.json` / `changes.json` に保存した。

公式 `verify_phone_saber.sh` は今回未実行。Android APK buildと端末検証も未実行。追加のAndroid JNI host checkerは、既存CameraPolicyTestのAndroidX/CameraFormatPolicy参照をhost用classpath/source一覧が解決できず失敗した。guardの認識期待値失敗ではないが、Android検証PASSとは報告しない。

## host時間（ON / OFF）

macOS arm64、Apple clang 21.0.0 / Swift 6.4、`-O2 -ffp-contract=off` / `swiftc -O`。今回の paired host run：

| 言語 / 区間 | OFF ms/frame | ON ms/frame | 差 |
|---|---:|---:|---:|
| C++ / FrameProcessor | 3.205221 | 3.236947 | +0.031726 ms (+0.990%) |
| Swift / analyzeSabers | 5.830455 | 5.848411 | +0.017956 ms (+0.308%) |

1回のpaired runであり統計的有意性は評価していない。小さい差を実機の改善/悪化と断定しない。結果は `timing/cost.json` / `timing/swift-cost.json` と `timing.log` に保存。BGRA変換・PNG decode・JSON/通信/画像表示は区間外。正式35 unique PNG＋20261010の研究対象22 ORIGINAL＝57枚、step=2、10 warmup＋100測定、画像ごとにOFF/ON/ON/OFF、各pass中央値をmode別平均し、画像を等重みに平均する。
C++は `FrameProcessor.process`（trackingを含む）、Swiftは `analyzeSabers`（profile/diagnosticsなし、trackingなし）なので言語間の絶対時間を同一処理と解釈しない。ビルド終了後、他の検証と同時に測らない。host時間はiPhone/Android実機のfps・遅延・フレーム予算ではない。

## 残るリスクと operator の確認

`CLAUDE.md §4` の別swing CASE A gateは変更していない。このguardの大差scoreによる過連結修正を、4独立sessionの証拠で採用するかはoperatorが位置付けを判断する。8連続frameや両色を独立eventに増し数えず、速振り改善の証明としない。

- 同色2本の交差：1端点の近さは同一物体の証明ではない。長い本物のlineを別の短い剣の支持で除外し得る。
- 反射・投影・背景：core/HV/mask被覆を満たす近い背景も裏付けになり得る。壁画FPは残る。
- 長い分離点LED、露出の低い/淡い剣、速振り、近距離、画面端：真の空隙を支持不足と判断し得る。別候補がなければ保持するが、近い反射などがある場合は未確認。
- 支持候補ではなく残存core-lineを選ぶこと、不当な短縮・方向誤りが実データで残る。

operatorの作業：

1. このbranchの既定ON状態で `bash PhoneSaber/tools/verify_phone_saber.sh` をGit rootから実行。XCTest、Detection、正式40/40、Python、iOS Releaseを確認。期待値変更が必要なら採用を停止する。
2. `cd PhoneSaber/android && ./gradlew :app:assembleDebug :app:assembleRelease :app:testDebugUnitTest` を設定なし（ON）で実行。AQUOS sense9へONビルドを入れ、実機JNI/送信fps・開始停止・復帰を確認。必要なら同じ `PHONESABER_CORELINE_GUARD=0` で比較し、ONへ戻す。
3. iPhoneへONビルドを入れ、本番の壁画を含む画角、露出1/100秒、2.7–3.1mで **「振りだけ録画」2本**（各約50秒）。別々の振りを静止で区切り、横/斜め/頭上、画面端への出入り、淡い青/赤、分離点LEDを含める。開始/停止しに来る場面だけを証拠にしない。
4. swing中のORIGINALを保存し、before/event/afterと色別実剣の軸・両端を確認。guardの `core-line-overlinked` 理由と supporting snapshot、ONの新規null/別領域/不当短縮を照合。独立swingの修正と保持を確認後にproduction採用を決める。

## 再生成

```bash
python3.12 -B PhoneSaber/tools/coreline_guard_evidence.py --output "$TMPDIR/phonesaber-guard-evidence"
# 既存changes.jsonの目視判定は同じhash/色/両端の場合に保持する
python3.12 -B PhoneSaber/tools/coreline_guard_evidence.py --output "$TMPDIR/phonesaber-guard-evidence" --render-only
python3.12 -B PhoneSaber/tools/coreline_guard_benchmark.py --output "$TMPDIR/phonesaber-guard-evidence/timing" --iterations 100
```

証拠画像生成とSwift timingのBGRA変換はPillowを使う。正式checker/CLI自体はPillowを必要としない。パック内には私的な画像パス・人物写真を含むので、一時ディレクトリからrepoへ移さない。
