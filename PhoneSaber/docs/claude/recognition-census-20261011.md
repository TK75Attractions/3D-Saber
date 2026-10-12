# 判定 failure census と次の改善候補（2026-10-11、10-12再開・検証）

**次に実機で検証する第一候補は青の背景条件。赤の HV≥.65 の一律追加は見送る。** 青の限定 mask 条件＋purity 条件は今回の既知 FP を44→33にし、位置が合っていた52出力を保持した。赤は source を限定した弱い HV 条件なら77→63、正しい位置60→61。ただし、改善した場面の多くは旧室内の布・手持ち撮影で、本番の肌・壁画・投影への効果は限定的。小さい赤剣の救済は必要だが、単純な純度緩和では正式negativeが壊れる。時間的選択は背景同士の飛びを減らすに留まり、実剣の CASE A の採用 gate は未達。

**短い露出の既定化には比較撮影が1本必要。** 実露出10msの赤速振りは4/4、20msは8/9で位置出力を確認したが、別撮影・別姿勢であり有意な改善の実証ではない。1/120秒以下のラベル付き速振りは0枚。現時点で1/100より短い既定を選ぶ根拠はない。

本番の C++/Swift、閾値、formal画像・manifest・期待値を変更していない。前回の `70c8346 wip…` の解析スクリプトを引き継いだ。今回は commit/push していない。

## 範囲・真値・分母

- `recognition_benchmark.py` の探索範囲すべて：Downloads forensic、read-only diagnostics-inbox **37 bundle**、repo内fixture。ORIGINAL **696ファイル / SHA重複除外657画像**（forensic354、inbox290、fixture52）。annotated12ファイルを除外。同じ画像のコピー39個も実行したが率には足さない。
- 642画像が480×640、15画像が512×910。ジャンプの100px、端点の手ラベル、露出比較は480×640のinbox画像。正式fixtureの寸法は元のまま。
- benchmarkのhash確認済み手ラベル138参照・formal40参照、既存物体CSV151 frame×2色、今回ORIGINALを目視した77 frameの解析専用ラベルをjoin。既存CSVの欠落0、既存truthとの衝突0。追加ラベルはTMPDIRのみ。正式コーパスへ追加していない。
- 追加目視は10-10の4 bundle、`144936_295` の2550–2557、`232850_471` のmanual4枚とOFF窓、10-06のガイド付きmanual12枚。消灯・画面外は色別に判定した。青の剣を持って赤だけ速振りした窓を「青の速振り」とは数えない。
- **presence** は剣の有無、**位置** は手ラベルboxに最終軸の中点が入るか（padding8px）。候補の実物identityや両端正解とは別。候補分類にも同じ位置proxyを使うので、過連結・体との混合で軸中点が外れる候補を「未生成」と誤解しない。
- 端点は赤37枚/7 session、青19枚/5 sessionに追加。見える剣部の中心軸・両端を画像座標で読み、露出中の残像は画像の見える包絡に対する参考値。別の時刻の物理姿勢を復元した真値ではない。読取誤差の目安±8px（遠い青は±4px）。隣接静止10枚や遠方8枚を独立eventとは扱わない。
- 選抜された異常窓・formal・旧室内の混合集団。以下の率は当日の発生率ではない。unknownは率の分母から除外。formal期待端点を新しい物体真値として流用していない。特に正式`red_short_component_last_true_89`が窓を選ぶ既知の問題はそのまま。

無改造production C++、step=2、既定設定で全696ファイルを再認識。一時コピーへ支持点・scoring前のdiscard・warm/deep-blueの観測だけを入れ、**候補全体・score・eligibility・端点・double bitが696/696で一致**。古い記録出力の再集計とは区別する。

core SHA-256 `0bf4e975f19fc3468b9f0d41006f25f476b395fc3f08e42e7ba6284839ad916c`、画像inventory `d69d27b7a6acceacdffa2faeac398bcc4f207332a5ee9c169f9b4f2f7f055225`。

## census：未検出・順位負け・誤検出

| 色 | 剣ありの出力 / n | 剣なしFP / n | unknown出力 / n | 位置ラベルの合致 / n |
|---|---:|---:|---:|---:|
| RED | 86/99（86.9%） | 77/153（50.3%） | 300/405 | 60/77（77.9%） |
| BLUE | 82/82（100%） | 44/178（24.7%） | 299/397 | 52/59（88.1%） |

presenceだけでは青の7件の位置外出力を成功に数えてしまう。boxなしの剣ありはRED22、BLUE23枚で出力は全てあるが、実物の位置正解は未評価。

| 位置ラベル付き剣の候補分類 | RED /77 | BLUE /59 |
|---|---:|---:|
| winner中点が実剣box内 | 60 | 52 |
| box内にeligible候補が残るが別winner（wrong winner） | 3 | 7 |
| box内候補はあるが全ineligible | 13 | 0 |
| box内に採点済み候補が返らない | 1 | 0 |

赤13件のineligibleのうち12件はnull、1件は背景出力。未生成1件もnullなのでpresenceのmiss13件と一致する。位置のmiss17件と分ける。青はこの位置ラベル集団でgeneration/eligibility missを確認しなかっただけで、全速振りや淡い青の保証ではない。

赤のineligible13 frameの棄却述語（同じframeに複数あり、合計は13にならない）。候補数は同じ画像のproposal違いを含む。

| production述語 | 該当frame | 該当候補 |
|---|---:|---:|
| compactRed purity<.50 | 11 | 17 |
| emitterScore<.42 | 4 | 8 |
| hasEmitterCore=false | 3 | 7 |
| peak<218 | 2 | 4 |
| compactRed HV<.50 | 3 | 3 |
| compactRed peak<230 | 1 | 1 |
| core-line purity<.25 | 4 | 7 |
| core-line retainedBodyRatio<.35 | 3 | 6 |
| core-line continuity<.70 | 1 | 1 |

定義はC++ `android/core/src/detection.cpp:299–303`、`pipeline.cpp:57–59,363–364`、Swift `DetectionCore.swift:946–960`、`BGRADetection.swift`のcore-line品質判定（794付近）。暖色/deep-blueなどの最終支持gateも観測したが、この13件のbox内候補には追加の該当がなかった。これは棄却順序全体のtraceではなく、数値から確認できるbase述語と最終sourceの監査。

代表例：

- `20261003_144936_295 f2552 RED`：20ms、ISO100。剣＋前腕maskはpeak233、mean187.29、HV.0632、emitterScore **.3052**、芯条件も不成立。小さい`color-emitter`はpeak233、HV.6739、purity **.3933**でcompact純度に落ちる。現行C++ではRED null。前腕を含む大きいmaskの軸は`(226,194)–(258,328)`で、これを受理しても実剣の端点が直るわけではない。
- `20261001_003921_526 f18140 RED`：強い近接ブレの実剣boxに採点済み候補なし、重なるscoring前discardもなし。**「そのbox内の候補が返らない」以上の原因は未確定**。mask段階、連結・重複抑制、軸中点の逸脱を切り分ける観測は未収録。
- RED wrong winnerは`002509 f72881`、`234740 f2439`、新しい`163949 f21953`。f2439は背景混合の長いwinnerを外すと同じframeの実剣candidateへ移る。
- BLUE wrong winnerは`005134 f16171/f18326`、`002509 f72881`、`192926 f1568`、`005325 f205`、`163949 f21952/f21953`。後者2枚は既報のcore-line過連結。青のrankを安定化するだけでこれら全部が直るという結果ではない。

scoring前のdiscardも全画像で観測した。例：青f205では色成分20587点が面積>.22、f18326では993–1967点の混合成分がspan/aspect/extentに落ちた。しかし同じframeに位置proxy上のeligible lineはあるため、「候補未生成の取りこぼし」に足さない。

### 剣なしのwinner source

| source | RED FP /77 | BLUE FP /44 |
|---|---:|---:|
| color-mask | 21 | 12 |
| color-close | 20 | 0 |
| color-sparse-raw | 4 | 0 |
| color-emitter | 1 | 6 |
| connected-core | 8 | 19 |
| core-halo | 16 | 2 |
| core-line | 7 | 5 |

青はもはやmaskだけの問題ではない。PCや画面外の青照射に深青の支持・connected-coreがある。赤も純赤の背景はwarm gateを通る。

### 場面別（色ごとに分母が違う）

sceneは色別の既存背景注記をまとめた分類であり、全winnerの物体名を再注釈したものではない。同じ画像でも色別の注記によりgroupが異なる場合がある（旧橙布の画像で青側は「画面外の青照射」など）。最後2列は後述R/B規則を各色へ掛けたFP数。

| 場面 | RED FP/n | BLUE FP/n | R後 | B後 |
|---|---:|---:|---:|---:|
| 本番に近い壁画・肌（10-10、1 bundle） | 2/11 | 9/11 | 2 | 4 |
| PC画面・服（10-10、1 bundle） | 8/11 | 11/11 | 8 | 11 |
| 屋内肌・天井照明（追加目視） | 1/11 | 0/21 | 0 | 0 |
| 旧室内ラベル・カラビナ | 29/37 | 0/37 | 29 | 0 |
| 旧室内橙布・clip | 15/17 | 0/0 | 7 | 0 |
| 手持ち机・包装・指 | 6/8 | 3/8 | 1 | 3 |
| 他PC画面・キーボード | 2/7 | 7/7 | 2 | 7 |
| 青の剣は画面外・青照射のみ | 0/0 | 6/6 | 0 | 6 |
| 赤の白飛び（青側は剣なし） | 0/0 | 2/2 | 0 | 2 |
| 昼光・窓・近接 | 2/6 | 0/16 | 2 | 0 |
| その他室内・肌等 | 4/30 | 0/35 | 4 | 0 |
| 位置未ラベルの旧benchmark/fixture | 8/15 | 6/24 | 8 | 0 |
| **合計** | **77/153** | **44/178** | **63** | **33** |

0/0は該当ラベルなし。最後のgroupのFPは正式negative失敗を意味しない。正式17 negativeはC++/SwiftともFP0。投影中の本番・背後の人・2.7–3.1mを同時に手ラベルした比較窓は不足している。

## 端点：core-line guard後にも何が残るか

正式の端点許容差試験と、実画像の手ラベル乖離を別にした。端点順序は反転可。短い=ラベル長の80%未満、長い=120%超、角度は180度対称。短い剣では±8pxが20%に近いので下表を確定失敗件数とは扱わない。

| 比較 | n /出力 | 短い | 長い | 角差>20° | 端点平均誤差 p50 /最大 px |
|---|---:|---:|---:|---:|---:|
| RED baseline | 37/37 | 1 | 13 | 2 | 13.32 /88.74 |
| RED G .6参考 | 37/37 | 1 | 13 | 0 | 12.02 /26.63 |
| BLUE baseline | 19/19 | 1 | 4 | 2 | 9.63 /132.31 |
| BLUE G .6参考 | 19/19 | 1 | 2 | 1 | 6.82 /16.48 |

Gは既報の`guarded_lit=.6`のみ。baseline最大長比はRED2.90、BLUE8.39。G後はRED最大1.66、BLUE1.21。青の残存短縮は同じ遠方静止窓のf21949（長さ約61%、角差31.5°）。f21952の8倍の遠方端点はGで解消するが、この短縮は解消しない。赤の残存長さ差にはブレた包絡・短い約40pxの遠方剣の読取誤差が混在する。これを根拠に全raw端点をbodyへ短縮する提案はしない。

R/Bのshadowはこの手ラベル付き37/19出力の端点を変更しない。body/PCAの修正Bを正当化する独立swing・分離LED非該当の証拠はまだ不足。

## 改善候補の順位とshadow

以下は**実装前の調査順位**。今回選んだ値は探索用で、production既定を決めたものではない。除外案は「現eligibleから除外→元のscore順の次候補」、score・PCA・生成は固定。出力保持と実剣位置保持を併記する。片色限定案は反対色不変。

| 順位 /案 | 剣あり出力 前→後 /n（失う/足す） | 位置合致 前→後（失う/戻す） | FP 前→後（除去/追加） | unknown変更（null化/新出力） | formal |
|---|---|---|---|---|---|
| 1 B：青mask面積/HV＋purity | 82→82 /82（0/0） | 52→52 /59（0/0） | 44→33 /178（11/0） | 12/397（11/0） | 40/40 |
| 2 R：赤のsource限定HV | 86→86 /99（0/0） | 60→61 /77（0/1） | 77→63 /153（14/0） | 12/405（9/0） | 40/40 |
| 3 C：compact赤 purity .35の救済試作 | 86→91 /99（0/5） | 60→64 /77（0/4） | 77→81 /153（0/4） | 10/405（0/5） | **39/40** |
| 4 T：期限付き僅差選択（strict、margin5、hold1） | 窓内 R44→44 /47、B37→37 /37（各0/0） | R41→41 /47、B32→32 /37 | R60→60 /94、B31→31 /109（各0/0） | R0/41、B1/36（null化なし） | 単フレーム40/40、履歴未検証 |

G＋R＋Bを同時に再選択したshadowも正式40/40。剣あり出力R86/B82を保持、位置はR60→62/B52→54、FPはR77→63/B44→33、unknown変更R14/B17（null化R9/B11、新出力0）。FP減少はG単独では0なのでR/Bの追加利益。これも本番での併用の安全性を確定する結果ではない。

### 1：青の広い・暗いmaskと低purityの候補を分けて抑える

Bの具体則：BLUE `color-mask`だけ `pointCount×4/(画像幅×高さ)≤.02 AND HV≥.35`、全BLUE eligibleは `purity≥.25`。深青支持が1画素あれば通る既存最終gateに加えて再選択する。

機構：C++ `pipeline.cpp:293–307`（relaxed/close/clean mask）、`detection.cpp:176,288–303`（面積、purity、明るさ）、`pipeline.cpp:453–465`（深青支持後の選択）。Swift `BGRADetection.swift:614–704,1169–1185,1260`、`DetectionCore.swift:946–957`。差し込むなら既存支持gate後のwinner選択直前で、scoreは変更しない。

分解：mask条件単独は44→39、unknown1変更、purity単独は44→38、unknown11変更。併用44→33。壁画はmaskだけで9→4、purityだけでは9のまま。残る4はcore-halo/lineで、PC11/11も残る。**purity .25で壁画が消えるとは言えない。**

期待利益は消灯時の壁画による青出力の減少。面積capは近接の大きい剣、HVは淡い青・速振り、purityは白いdiffuser青を失うリスク。formal40/40と今回の保持だけではこれらの下限を決められない。

**単一の新capture**：本番の固定画角、投影ON、人が背後を歩く、3mで青OFF→淡い青静止→速振り/端向き→OFFを行い、各状態のlossless窓を残す。最後に0.5mの大きい青を同じ記録に入れる。Bで実剣のnull化/別領域移動が0、壁画OFFの残存4候補を特定できるかが判定点。

### 2：赤の明るさ条件をsourceで限定する

Rの具体則：RED `color-mask/color-close/color-sparse-raw`のみHV≥.45。core-halo/connected-core/core-lineを同じ条件で落とさない。HVは**色mask支持点のV≥220の率**で、LEDの物理的証明ではない。

機構：C++ `detection.cpp:264–303,328` と `pipeline.cpp:446–465`、Swift `DetectionCore.swift:946–960`、`BGRADetection.swift:1149–1185,1260`。旧赤FP45/77のsourceがこの3種だったための限定案であり、source名が実物classを表すとは考えない。

利益の内訳は旧橙布/clip **15→7**、手持ち机・指 **6→1**、肌/天井照明 **1→0**。本番に近い壁画赤2、PC赤8、旧ラベル29は残る。全winnerの明るさをさらに上げるだけでは、この本番側の残りを分けられない。

一律HV≥.65はFP77→36だが、出力86→77、正しい位置を7件失う。遠方静止`163949`の赤6枚、1/100秒のslow f3409、既存白い小剣f20551等を失う。.45に下げても全source適用では遠方赤2枚を失う。旧メモの「29/29保持」は小さい旧ラベル集団に限った結果だった。

**単一の新capture**：本番の屋内3m、赤OFF→赤の静止/先端向き/速振り→OFF。背後の肌と投影を入れ、淡い剣も使う。R対象sourceの本物HV下限、Rで変わる全出力の物体/両端、残る肌FPを一緒に確認する。現データでの全保持をproduction閾値の確定とは扱わない。

### 3：小さい・白い赤のeligibilityを調べる（単純緩和は不採用）

位置miss17中13は候補がineligible、compact純度の該当は11 frame。候補の順位を変える前にここを調べる価値がある。C++ `detection.cpp:202,299–303`、Swift `DetectionCore.swift:761–762,946–960`。最終warm支持はC++ `pipeline.cpp:449–451`、Swift `BGRADetection.swift:1152–1167`。

Cはcompactのpurityだけ.50→.35として既存採点済み候補を復活し、peak/HV/base emitter条件と元画素のwarm gateを要求したshadow。候補間の後段関係を再構築していないため、将来の本番実装と完全等価の試験ではない。回復した4位置だけで無条件緩和を薦めない。`.40`でもFP4増、正式`red_hard_negative_348`がcolor-closeの新FPになり39/40。`.35`はf2552の小点を出すが、blur円盤の正しい軸・長さが復元できたという証拠ではない。

次の変更案は「compact判定を一律に緩める」ではなく、白い小剣と肌/光を分ける**現フレームの支持証拠を収集し、救済対象を限定すること**。その条件はまだ同定できていない。過去の出力や端向きの推測だけでineligibleを復活させる時間的救済は行わない。

**単一の新capture**：本番3m、赤を横向き→先端向きに静止→突く/速振り→OFF。白い剣部、青の照射、肌が同時に入るようにする。各失敗窓で小候補のraw支持・compact述語・実剣boxを確認し、同じ特徴のOFF候補をnegativeとして比較する。新しい分離特徴がなければ救済案は見送る。

### 4：現eligible＋前winnerのgeometryで期限付きの僅差選択

対応の試作を2種類比較。既存replayはbbox IoU≥.2 **OR** 中心距離/前rawSpan≤.5。strictは最終中点距離≤30px **AND** 最終長比.5–2 **AND** 軸角差≤20°。前winnerに対応する現在の候補Mがあり、現最高Rが対応せず、score差≤marginのときだけMを選ぶ。overrideは連続1/3 frameで期限切れ、overrideで延長しない。候補消失/null/唯一eligible/窓の切れ目で解除。index identity、過去座標の保持、予測端点は使わない。

挿入候補：C++ `pipeline.cpp:446–465` と `frame_processor.cpp:26–43`、Swift `BGRADetection.swift:1260` と `FrameProcessor.swift:61,497–515`。既存FrameProcessorの欠測予測（C++48–57）とは別。今回の集計はprediction/UDP結果を含めない。

#### retained tracking/motion窓での評価

すべてframeIDが連続するrunのみ。戻りの遷移も含むので「独立CASE A event数」ではない。ジャンプは端点順序を最良対応して2端点の最大距離≥100px。中点ジャンプも別に計算した。

| データ | 完全候補のframe数 R/B | 出力が両frameにある連続pair R/B | 100px端点ジャンプ R/B | そのうちstrictな安定別候補がある R/B |
|---|---:|---:|---:|---:|
| ORIGINALの現行C++再認識（47 run/色、単独frame含む） | 182/182 | 77/42 | 19/11 | 5/6 |
| 保存当時の完全eligible context（18 run/色） | 123/97 | 65/30 | 24/12 | 4/5 |

保存contextは古い認識版を混ぜないため別表。完全性が不足/geometryがないmotion contextの出現数RED523、BLUE552を除外（重複context込み、独立frame数ではない）。ORIGINALがないcontext frameの現在版C++結果は復元できない。

| strict、hold1、margin | 現行R/Bの端点ジャンプ後 | 現行変更frame R/B | 保存版R/Bのジャンプ後 |
|---|---:|---:|---:|
| 1 | 17/11 | 1/0 | 24/12 |
| 3 | 15/11 | 3/0 | 24/12 |
| 5 | 13/10 | 4/1 | 22/12 |
| 10 | 13/10 | 4/2 | 21/12 |

hold3もこの窓では同じ結果。緩い対応のmargin5では現行19/11→14/8（中点では13/8）、変更5/2。strictは長いspanやbbox重なりだけの誤対応を減らすが、新たな実剣改善は確認できない。5は最適値ではなく比較点。

現行strict margin5の赤変更4枚は`013205 f255`、`005850 f2537/f2543`、`005325 f200`の既知剣なし。青変更は`005325 f204`のunknown。背景を消さず、別の背景を安定して選ぶ。保存版のf2553 REDは本物が復帰する側で、背景を安定させるMとのscore差43.27。大marginでこれを抑えると本物への復帰を遅らせ得る。

Tのformal40/40は独立画像で履歴を空にした基準結果。正式corpusにはこのpolicyを検証する連続履歴期待値がなく、formal通過を時間的安全性の証明にしない。**現行の実剣CASE A（別swing2 event）を今回確定できていない**。

**単一の新capture**：3m、赤/青で別々のswingを2回、各回の前後に停止を入れ、背景明点のそばを横切る。tracking窓にORIGINALと全eligibleを残す。実剣Mが残ったまま遠方Rが僅差で勝つswitch-outを2回確認し、対応条件・score分布・正しい復帰の遅れを評価する。背景間のswitchはgateへ数えない。

## motion blurと露出：既定化の判断材料

camera情報があるORIGINALは181/657枚。**設定名ではなく実露出時間で分類**した。`20261006_004003_239`は設定maxShutter1_100でもcapActive=false/status=notNeeded、観測最大33.33ms、保存12 frameすべて実20ms。現在の`CameraViewModel.swift:1995–2013`にはこの旧状態を修正する処理が既にある。今回新しい本番バグ修正を提案する根拠にはしない。

| 剣/動き | 実露出 | 画像n /session数 | 正しい位置 /n | 出力 /n | ISO min–max |
|---|---|---:|---:|---:|---:|
| RED fast | 20ms | 9/2 | 8/9 | 8/9 | 100–320 |
| RED fast | 10ms | 4/3 | 4/4 | 4/4 | 640–800 |
| RED slow | 20ms | 1/1 | 1/1 | 1/1 | 320 |
| RED slow | 9.434–10ms | 3/3 | 3/3 | 3/3 | 640–800 |
| RED cross | 20ms | 1/1 | 1/1 | 1/1 | 320 |
| RED cross | 9.434–10ms | 3/3 | 3/3 | 3/3 | 640–800 |
| BLUE swing（速さ未確定） | 20ms | 1/1 | 1/1 | 1/1 | 250 |
| BLUE swing（速さ未確定） | 10ms | 2/2 | 2/2 | 2/2 | 640–800 |
| BLUE other moving（赤を速振りする窓） | 20ms | 8/1 | 8/8 | 8/8 | 100 |

fastの証拠：20msは`144936 f2550–2557`の1 swingと`004003 f3397`。10msは`232850 f3888`、`005910 f3014/f3058`、`010346 f11157`。ガイドのphaseとORIGINALを照合した。何千frameというguided detected集計を現在のC++/実剣recallへ代用していない。

blur長は物理的な剣長・端向き・速度と混ざるため、今回の画像から露光中の真の移動長を全枚で復元できない。目視で剣部のブレた包絡幅を区間で記録した**blur proxy**は、20ms fast9枚で12–95px、10ms fast4枚で12–45px。中央値の区間中心はいずれも約27.5px。20msの75–95pxの円盤f2552のみ未検出、その他12枚は位置出力あり。**「長い露出で大きいblurは失敗し得る」は支持するが、「10msへの変更で検出率が改善した」というpaired測定ではない。** 同じ移動なら20→10msでブレが半減するのは物理的な予測であって、このデータの実測改善量ではない。

1/120秒（8.333ms）の記録もあるが、ラベル付きfastは0。1/120、1/200、1/240の既定化効果、淡い青の損失、投影/照明の縞は未検証。露出を変える反実仮想は現在の候補再選択だけでは作れず、eligibility緩和shadowを露出短縮の効果とは呼ばない。

### cost：ISOと画像の揺らぎ

同じ屋内ガイドの20ms `004003`は赤swing ISO320、10ms `005910`は640、`010346`は800。**露出半減に伴う約2–2.5倍のISOを記録**している。ただし時刻・構え・画角は異なる。古い教室20ms/ISO100と家10ms/ISO800の8倍を露出変更だけのcostと解釈しない。

processed PNGの平らな壁ROI `(20,380)–(60,420)`で、8連続frame/7差分から各差分の平均を除き、輝度差SDの中央値/√2を測った：

| 窓 | 実露出/ISO | ROI平均輝度（8bit） | 時間差分proxy |
|---|---|---:|---:|
| `004003 f2522–2529` | 20ms /320 | 107.22 | 0.384 |
| `005910 f2138–2145` | 10ms /640 | 101.34 | 0.445 |

proxyは約16%増だが、画角・微振動・照明・ISPを含む。sensor noise/SNRの測定でも、ノイズによる検出悪化の証明でもない。8bit PNGのISPによる平滑化が大きく、これで高ISOを安全と断定しない。1/100秒でも肌・PC・壁画のFPは残るので、短い露出を背景除去策としても扱わない。

**operatorへの判断**：当面1/100を比較実験の第一設定として使い、既定のさらに短縮は保留。既定化を決める1本は、本番画角・3m・同じLED/照明/投影で auto→1/100→1/120（可能なら1/200）→auto、各ブロックに静止/同じ速振り/先端向き/OFFを入れる。各設定で実露出・ISOと複数swingのlossless窓を残す。剣消失、端点乖離、淡い青、OFF FP、縞の同条件比較が必要。実機captureは今回行っていない。

## やらないこと・不足証拠

- 赤HV≥.65/.70や複合brightness条件を全sourceへ即追加しない。複合条件はFP77→31でも剣あり出力を8件失う。
- 青purityだけで壁画が消えると言わない。Bでも壁画4、PC11が残る。BLUE全sourceに高いHVを要求する案は既報のformal/実剣損失もある。
- compact純度の一律緩和、emitter全体の緩和は採用しない。現shadowは新FPと正式39/40。前腕maskを通しても正しい軸とは限らない。
- temporal policyでnullを穴埋めしたり、過去の端点を出し続けたり、静止候補を一律背景にしない。現在のeligibleが消えた瞬間に選好は解除する。
- 安定した背景を選ぶ結果を「実剣の改善」へ数えない。score marginを大きくして本物への復帰を遅らせない。CASE Aの2独立swing条件は緩めない。
- 全raw PCAを短縮しない。手ラベル37/19とG参考結果だけで分離LED/長い剣の非破壊は保証できない。
- blue unsupported-lineの非重複候補だけを復活する試作も評価したが、今回657画像で出力変更0、FP改善0、formal40/40。次の最優先案にはしない。
- unknown変更やformal40/40、選抜窓の割合を運用precision/recallへ換算しない。投影・背後の人・本番3mの独立正解窓、新端末、露出<1/100の保持が不足。

## 再現・検証・成果物

解析用変更は`tools/recognition_census.py`（WIP修正）、`tools/recognition_census_noise.py`、`tools/test_recognition_census.py`とこのノートだけ。nativeの観測コピー・build・人の画像・解析ラベル・詳細JSONはすべてTMPDIR。inboxはread-only。

```sh
python3 -B PhoneSaber/tools/recognition_census.py \
  --output "$TMPDIR/recognition-census-20261011/completed" \
  --annotations "$TMPDIR/recognition-census-20261011/annotations.json"
python3 -B PhoneSaber/tools/recognition_census_noise.py \
  --output "$TMPDIR/recognition-census-20261011/verified/noise.json"
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_recognition*.py' -v
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_*shadow.py' -v
CLANG_MODULE_CACHE_PATH="$TMPDIR/phonesaber-census-module-cache" \
SWIFT_MODULE_CACHE_PATH="$TMPDIR/phonesaber-census-module-cache" \
python3 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py \
  --json "$TMPDIR/recognition-census-20261011/formal-swift.json"
```

追加annotationを省けば既存hashラベル＋CSVだけのcensusを再生成する（今回の拡張分母とは違う）。annotationは77 frameの目視box/両端/blur区間で、TMPDIR消失後は元画像から再注釈が必要。noise toolだけPillow/numpyが必要。blur区間は人の目視であり自動尺度ではない。

`completed/baseline.json`に無改造C++、`completed/rows.json`に観測・camera・追加真値、`completed/census.json`に全rule・正式失敗・端点・露出・tracking sweep。`formal-swift.json`は公式Swift結果。人の画像や詳細診断をrepoへコピーしていない。

検証：C++全696画像の観測parity、C++正式40/40、Swift公式lossless **40/40（positive23/23、negative FP0/17）**、Python27/27（既存benchmark11、既存shadow6、追加census10）。棄却限定R/B/Gの正式期待値は維持。Cの39/40は候補案の不採用根拠として明示し、期待値を書き換えていない。最終レビューで失敗例JSONのwinnerが棄却候補に上書きされるWIP由来の記録バグを修正し、nullの例を守るテストを追加。census数値が不変であることも確認した。production/manifest/fixtureの作業差分0、`git diff --check`確認。
