# core-line 過連結と青緑の壁画 — offline shadow 分析（2026-10-11）

本番認識・閾値・正式コーパス・期待値は変更していない。コミットなし。
**無条件の長さ／gap／被覆率制限は正式回帰を壊す。まず実支持を測り、現在の別候補で裏付ける限定 guard を追加 capture で検証することを推奨する。** 壁画は BLUE color-mask の面積または highValueRatio 条件で一部を除外できるが、core-line/core-halo の FP は残る。

## データと再現

- 読んだ運用資料: `PhoneSaber/CLAUDE.md`、`PhoneSaber/AGENTS.md`、`STATUS.md` 先頭と2026-10-10学校capture、`red-fp-features.md`。
- baseline commit: `0c97b1c1a662b19e5b8221d9f516feb95fa621d6`。
- `recognition_benchmark.py` の production C++ CLI、既定設定・step=2。比較元は録画の選択候補の `endpointPipeline.finalSelected`、source、finalScore。最終送信用の端点順や top-level 診断の coreSupport を候補支持率と混同しない。
- inbox の `phone_saber_triage_phonesaber_20261010_163949_658` と `..._164236_960`: 各12画像中 annotated 1枚を除いた各11枚、計22 ORIGINAL（480×640）。**44色別winnerの有無・source・認識端点・scoreが保存JSONと完全一致、最大score差0。**
- 163949 の frame 21948/21949/21950/21952/21953/21954/21955/21956 は ORIGINAL で両色の点灯剣を確認した8枚。19861/19862/19876には点灯剣が見えない。164236の11枚はすべて停止しに来た消灯場面。8+3、0+11を分けて評価し、60秒／57秒全体の検出率へ外挿しない。この場の目視区分は分析スクリプトだけに記載し、既存ラベルファイルは変更していない。
- 旧 benchmark は10月10日の新しいinboxを除いて再構成: 640ファイル、重複除外601枚（forensic 354 / inbox 234 / fixture 52）、ラベル証拠欠損0。コーパスinventory SHA-256 `72cd6839781474ce772891a118943b3529967b752dae72af4f9f50010c094976` は既存 `recognition-benchmark.md` と一致する。
- baseline: RED 剣あり出力29/30、剣なしFP56/83、unknown出力347/488。BLUE 31/31、9/32、346/538。赤の既存1欠落は今回のguardによる欠落ではない。
- 候補のsource・端点が変わった場合を「変更」、出力がnullになった場合を「欠落」として区別。剣ありの出力数は位置／両端の正解率ではない。
- 観測器は `pipeline.cpp` の**一時コピーだけ**に挿入し、全640+22ファイルで候補全体（double bit signatureを含む）を無改造CLIと照合: 662/662一致。本番ファイルは読取だけ。

recognizer source SHA-256: `0bf4e975f19fc3468b9f0d41006f25f476b395fc3f08e42e7ba6284839ad916c`。


## 過連結を許す経路（file:line）

1. 色の近傍の明点をcoreとする。`PhoneSaber/android/core/src/pipeline.cpp:280` は maximum≥235かつsecond channel≥100、`:299–302` は色maskを半径2（原画像4px）膨張して明点を関連付ける。背景の白い点も色の近くなら入る。
2. `pipeline.cpp:104–160` のHoughは全coreを対象に10度刻みで投票。480×640では処理格子240×320、最低6票。軸の±6格子（12px）をPCA再fitし、±2格子（4px）の全inlierを採る。軸方向の連結componentを要求せず、離れた点のmin/max投影を端にする。spanの**下限**24格子（48px）、近傍coreに対するinlier率≥0.28はあるが、**軸方向の最大距離／空隙の上限はない**。
3. `pipeline.cpp:170–178` は軸±3格子の色/core支持画素を集める。その直後の **`:179–184` はminTからmaxTまで0.75格子刻みで、明るさも色も調べず点を追加**する。Swiftでも `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift:390–398` が同じ「Bridge missing/white LED packages」処理。点LEDの隙間を埋める目的の点列が、背景までの暗い線も埋める。
4. `PhoneSaber/android/core/src/detection.cpp:169–206` のarea/aspect/extent/PCAはこの合成点込み。細い補間線はaspectとextentを得られる。`:102–167` のdominant_bodyも合成点込みの1格子binで計算する。dense group間の許容gap≤2は**局所bodyの抽出**であり、proposal全体の暗い空隙を制限しない。continuityとlargest_gapは選んだbody内の点数から計算する。
5. `detection.cpp:260–298` のhighValueRatioは色mask上の画素だけ（V≥220）を分母・分子に使う。meanValue/purityも色画素だけ。補間した暗い点を大量に足しても、これらの平均へ直接入らない。core coverageは最大12個の粗いbinで、binにcoreが1点あれば占有になる。clipped-whiteは候補の半径2近傍も数え、25%でボーナスが飽和する。
6. `pipeline.cpp:54–56,357–361` のline条件はpurity≥.25、retained≥.35、局所continuity≥.70、HV≥.35。sparse棄却は粗いcore coverage<.30 **かつ**core support<.18。今回の2例は通る。gap≤2が全spanに対する強制eligibility条件になっているわけではない。

### 通過した既存の防御と端点採用

- 21952 BLUE: retained=.35065（下限.35のすぐ上）、continuity=.95238、HV=.67089、purity=.28919、core support=.30736、粗いcore coverage=.33333。実支持gapは160pxなのに、記録largest_gap=0（局所body）。`pipeline.cpp:399–411` のweak-raw-tailはcore support<.30が必要で、.30736なので対象外。
- 21953 RED: retained=.54808、continuity=.86667、HV=.57895、purity=.32020、core support=.41346、粗いcore coverage=.33333。`pipeline.cpp:420–424` のweak-bridgeはbody側continuity≥line側を要求。正しいcore-haloは.75、color-closeは.83333で.86667に届かない。color-maskはcontinuity .88だがHV .24265<line HV−.20=.37895。どれも全条件を満たさない。
- BLUE trusted-emitter比較（`pipeline.cpp:74–100`）も、正しい候補がcolor-maskなのでtrusted sourceにならず救済しない。
- `detection.cpp:306–315`（Swift `DetectionCore.swift:990–1024`）のbody端点採用はdense_line≥4点/格子binなどが必要。21952は81点/21bin=3.857、21953 REDは57点/15bin=3.8で、どちらもraw端点fallback。retained≤.30でもなく、BLUE専用diffuserのpurity≥.40等にも届かない。
- **21954/21955 BLUEも同じ過連結proposalを持つ**（raw 292.69px）。局所densityが87/21=4.143、95/22=4.318へ上がるとbody端点が採用され、最終出力は `[16,68]–[56,76]` / `[16,68]–[58,76]`。21955の保存JSONと現行CLIは58で一致する。短い最終端点だけで「過連結proposalは消えた」と判断しない。

## 約50点差の原因

`detection.cpp:321–333` / Swift `DetectionCore.swift:1034–1056` のlengthSupportは
`clamp((rawSpan / minFrameDimension − .08) / .22, 0, 1)`。
BLUEの長い線は1、正しい40.80px候補は.022756。REDは長い線.756267、正しい54.33px候補.150861。
rawSpanの**直接length項は数点**だが、radiance（最大65）、clippedWhite（最大73.9）、core項の倍率にもなる。
補間点の近くの白／照明支持でclippedWhiteが飽和し、短い実剣より大きな倍率を得る。

| score項 | BLUE長線 | BLUE正しいmask | RED長線 | RED正しいhalo |
|---|---:|---:|---:|---:|
| proposal_penalty | -59.260 | 0.000 | -30.000 | 0.000 |
| radiance | 28.842 | 0.909 | 33.505 | 5.750 |
| length | 3.005 | 0.408 | 1.183 | 0.543 |
| aspect | 8.000 | 3.480 | 6.205 | 2.025 |
| extent | 0.451 | 1.268 | 0.511 | 0.826 |
| width_consistency | -1.886 | -0.577 | -2.367 | -1.874 |
| area | -0.024 | -0.008 | -0.011 | -0.016 |
| peak_brightness | 12.160 | 12.160 | 12.160 | 12.160 |
| mean_brightness | 6.113 | 6.723 | 5.317 | 4.543 |
| high_brightness_ratio | 16.531 | 18.322 | 14.265 | 10.713 |
| color_purity | 3.054 | 3.121 | 3.381 | 4.238 |
| local_contrast | 10.196 | 9.505 | 2.668 | 3.316 |
| emitter_texture | 0.000 | 2.109 | 7.625 | 5.369 |
| clipped_white | 73.900 | 1.318 | 55.888 | 11.149 |
| longitudinal_high_coverage | 1.250 | 2.500 | 2.000 | 2.750 |
| core_support | 3.688 | 0.187 | 3.752 | 0.791 |
| longitudinal_core_coverage | 2.667 | 0.152 | 2.017 | 1.106 |
| **total** | **108.6854** | **61.5758** | **118.0986** | **63.3895** |

差はBLUE **47.1097点**、RED **54.7091点**。
BLUEのcore coverage penaltyは−30、robust penaltyは−29.260で合計−59.260でも勝つ。
REDはnormalizedSpan=.24638<.32でrobust penaltyが0、core coverage penalty−30だけ。
正しい候補がeligibleのままでも、長いrawSpanに依存する複数のボーナスで逆転する。僅差CASE Aではない。

## 実支持の定義と分布

補間点を入れた既存のcontinuity/gapを再利用せず、proposalの元の支持点列と実maskを観測する。
PCA軸に沿う**全raw interval**を1格子（原画像2px）binに分け、core maskまたはcolor maskに実際に属する点があるbinだけを数える。
`lit_coverage` はその占有bin率、`lit_gap_px` は占有しないbinの最長連続数×2。
`core_coverage/core_gap_px` はassociated coreだけ。`span_per_core` はrawSpan/(2×core画素数): サンプル格子の軸長／core点数。
**litは閾値maskの支持を指す解析用略称で、物理的な発光の証明ではない。** 壁画の青画素もlitへ入り、真の光点がproposalの幅外にあると支持を過小評価する。
pointCount/core画素数はstep=2のサンプル点数、component area比は`pointCount×4/(width×height)`。既存largest_gapは格子単位の局所body値。

| frame /色 | rawSpan px | core点 | span/core | 全lit被覆 | 全lit gap px | 全core被覆 | 全core gap px | 記録gap |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 21952 blue | 300.52 | 71 | 2.116 | 0.212 | 160 | 0.159 | 216 | 0 |
| 21953 red | 118.26 | 43 | 1.375 | 0.267 | 70 | 0.217 | 90 | 1 |
| 21954 blue | 292.69 | 67 | 2.184 | 0.163 | 170 | 0.143 | 218 | 0 |
| 21955 blue | 292.69 | 69 | 2.121 | 0.170 | 170 | 0.150 | 218 | 0 |
| 32359 blue | 306.14 | 135 | 1.134 | 0.377 | 182 | 0.240 | 218 | 1 |
| 32363 blue | 329.53 | 163 | 1.011 | 0.418 | 192 | 0.261 | 194 | 0 |

以下は旧benchmarkの**baseline winner**。剣ありラベルは画像のラベルであり、候補の全spanが実剣というラベルではない。min / p10 / p50 / p90 / max（最近傍順位）。

| 色 /ラベル / core-line n | span/core | 全lit gap px | 全lit被覆 | 全core被覆 |
|---|---|---|---|---|
| red / saber / 3 | 0.671 / 0.671 / 0.886 / 0.945 / 0.945 | 54.000 / 54.000 / 58.000 / 82.000 / 82.000 | 0.281 / 0.281 / 0.434 / 0.438 / 0.438 | 0.281 / 0.281 / 0.340 / 0.396 / 0.396 |
| red / no_saber / 1 | 3.364 / 3.364 / 3.364 / 3.364 / 3.364 | 56.000 / 56.000 / 56.000 / 56.000 / 56.000 | 0.293 / 0.293 / 0.293 / 0.293 / 0.293 | 0.195 / 0.195 / 0.195 / 0.195 / 0.195 |
| red / unknown / 16 | 0.479 / 0.521 / 0.866 / 3.204 / 3.326 | 2.000 / 14.000 / 74.000 / 242.000 / 342.000 | 0.141 / 0.163 / 0.342 / 0.816 / 0.968 | 0.140 / 0.141 / 0.278 / 0.613 / 0.673 |
| blue / saber / 14 | 0.747 / 0.825 / 1.660 / 2.286 / 2.630 | 18.000 / 28.000 / 122.000 / 134.000 / 214.000 | 0.188 / 0.213 / 0.250 / 0.604 / 0.730 | 0.161 / 0.172 / 0.226 / 0.458 / 0.541 |
| blue / no_saber / 1 | 1.315 / 1.315 / 1.315 / 1.315 / 1.315 | 58.000 / 58.000 / 58.000 / 58.000 / 58.000 | 0.587 / 0.587 / 0.587 / 0.587 / 0.587 | 0.373 / 0.373 / 0.373 / 0.373 / 0.373 |
| blue / unknown / 153 | 0.174 / 0.276 / 1.170 / 2.447 / 5.267 | 0.000 / 2.000 / 104.000 / 208.000 / 254.000 | 0.128 / 0.199 / 0.409 / 0.986 / 1.000 | 0.109 / 0.147 / 0.294 / 0.939 / 0.994 |

| BLUE color-mask winner群 | n | pointCount min/p10/p50/p90/max | HV min/p10/p50/p90/max | area比 min/p10/p50/p90/max |
|---|---:|---|---|---|
| 旧・剣あり | 3 | 102.000 / 102.000 / 223.000 / 323.000 / 323.000 | 0.488 / 0.488 / 0.495 / 0.578 / 0.578 | 0.001 / 0.001 / 0.002 / 0.004 / 0.004 |
| 旧・剣なし | 6 | 112.000 / 112.000 / 121.000 / 145.000 / 145.000 | 0.365 / 0.365 / 0.417 / 0.460 / 0.460 | 0.001 / 0.001 / 0.002 / 0.002 / 0.002 |
| 旧・unknown | 12 | 70.000 / 88.000 / 108.000 / 210.000 / 213.000 | 0.250 / 0.374 / 0.548 / 0.616 / 0.629 | 0.001 / 0.001 / 0.001 / 0.003 / 0.003 |
| 壁画 | 6 | 2086.000 / 2086.000 / 4431.000 / 6226.000 / 6226.000 | 0.201 / 0.201 / 0.261 / 0.311 / 0.311 | 0.027 / 0.027 / 0.058 / 0.081 / 0.081 |

壁画mask winnerには183px・2086点の小さい例（32358）もある。その他はraw 385–423px、4254–6226点、面積5.54–8.11%、HV .201–.311、purity .262–.267。32359の次点maskは4050点、raw 425.54px。
壁画core-lineはpointCount405/454、HV .741/.539、core support .333/.359、purity .266/.303。単なるBLUE purity≥.25では全9 FPが残る。
今回の遠い青の8 winnerはpointCount72–232、HV .671–.824だが、過連結winnerを含む。
**旧BLUE maskの正解winnerはHV最小.488、他sourceの剣ありwinnerには.353の例もある。** 全sourceへ.65を要求すると剣を失う。
全eligible BLUE maskは剣あり画像に34候補（最大面積15.34%、HV最小.179）、剣なし8候補、unknown319候補もある。これら全候補を「本物の剣」として扱わない。近い実剣の面積上限の証拠にも使わない。

## shadow sweep

現在のeligible候補だけを除外し、**元のscore順の次のeligible**を選ぶ。score/生成/既存棄却/過去端点を変更しない。
条件を満たす値は通す（capは≤、floorは≥）。sourceが厳密にcore-lineの候補にline guard、BLUE color-maskだけにmask guardを掛ける。
旧benchmark欄は「剣あり出力数 / 剣なしFP数 / unknown出力が変わる数」。unknownの比較母数はRED347既存出力（全unknown488）、BLUE346（全538）。
昼光変更は8枚の剣あり画像内のRED/BLUE選択変更数。すべてのsweepでこの8枚には両色の出力が残るが、BLUE全source HV≥.75だけは6/8になる。
壁画欄はBLUE FP /11。日光末尾のRED FP2/3、BLUE0/3と、壁画RED FP2/11は今回の全ruleで変わらない。


| rule | 旧 RED: 出力/FP/U変更 | 旧 BLUE: 出力/FP/U変更 | 昼光変更 R/B | 壁画青FP | formal |
|---|---:|---:|---:|---:|---:|
| baseline | 29/56/0 | 31/9/0 | 0/0 | 9 | 40/40 |
| span_per_core=1 | 29/56/8 | 31/8/82 | 1/3 | 9 | 39/40 |
| span_per_core=2 | 29/56/4 | 31/9/35 | 0/3 | 9 | 39/40 |
| span_per_core=3 | 29/56/2 | 31/9/2 | 0/0 | 9 | 40/40 |
| span_per_core=4 | 29/56/0 | 31/9/2 | 0/0 | 9 | 40/40 |
| span_per_core=6 | 29/56/0 | 31/9/0 | 0/0 | 9 | 40/40 |
| span_per_core=8 | 29/56/0 | 31/9/0 | 0/0 | 9 | 40/40 |
| lit_gap_px=8 | 28/56/15 | 31/8/118 | 1/5 | 9 | 34/40 |
| lit_gap_px=16 | 28/56/14 | 31/8/112 | 1/5 | 9 | 34/40 |
| lit_gap_px=32 | 28/56/13 | 31/8/98 | 1/4 | 9 | 36/40 |
| lit_gap_px=64 | 29/56/9 | 31/9/89 | 1/3 | 9 | 38/40 |
| core_gap_px=16 | 28/56/15 | 31/8/119 | 1/5 | 9 | 34/40 |
| core_gap_px=32 | 28/56/13 | 31/8/112 | 1/4 | 9 | 36/40 |
| core_gap_px=64 | 29/56/11 | 31/8/92 | 1/3 | 9 | 37/40 |
| reported_gap_px=4 | 29/56/0 | 31/9/0 | 0/0 | 9 | 40/40 |

| rule | 旧 RED: 出力/FP/U変更 | 旧 BLUE: 出力/FP/U変更 | 昼光変更 R/B | 壁画青FP | formal |
|---|---:|---:|---:|---:|---:|
| lit_coverage=0.25 | 29/56/6 | 31/9/31 | 0/3 | 9 | 40/40 |
| lit_coverage=0.4 | 29/56/8 | 31/9/74 | 1/3 | 9 | 39/40 |
| lit_coverage=0.5 | 28/56/13 | 31/9/88 | 1/3 | 9 | 36/40 |
| lit_coverage=0.6 | 28/56/14 | 31/8/91 | 1/5 | 9 | 36/40 |
| lit_coverage=0.7 | 28/56/14 | 31/8/97 | 1/5 | 9 | 35/40 |
| lit_coverage=0.8 | 28/56/14 | 31/8/105 | 1/5 | 9 | 34/40 |
| core_coverage=0.2 | 29/56/7 | 31/9/40 | 0/3 | 9 | 40/40 |
| core_coverage=0.3 | 29/56/9 | 31/9/79 | 1/3 | 9 | 38/40 |
| core_coverage=0.4 | 28/56/11 | 31/8/92 | 1/3 | 9 | 36/40 |
| core_coverage=0.5 | 28/56/14 | 31/8/98 | 1/3 | 9 | 35/40 |

| rule | 旧 RED: 出力/FP/U変更 | 旧 BLUE: 出力/FP/U変更 | 昼光変更 R/B | 壁画青FP | formal |
|---|---:|---:|---:|---:|---:|
| mask_area=0.005 | 29/56/0 | 31/9/0 | 0/0 | 4 | 40/40 |
| mask_area=0.01 | 29/56/0 | 31/9/0 | 0/0 | 4 | 40/40 |
| mask_area=0.02 | 29/56/0 | 31/9/0 | 0/0 | 4 | 40/40 |
| mask_area=0.03 | 29/56/0 | 31/9/0 | 0/0 | 4 | 40/40 |
| mask_area=0.05 | 29/56/0 | 31/9/0 | 0/0 | 4 | 40/40 |
| mask_hv=0.35 | 29/56/0 | 31/9/1 | 0/0 | 4 | 40/40 |
| mask_hv=0.5 | 29/56/0 | 31/3/5 | 0/0 | 4 | 38/40 |
| mask_hv=0.65 | 29/56/0 | 30/3/12 | 0/0 | 4 | 37/40 |
| blue_hv=0.35 | 29/56/0 | 31/9/1 | 0/0 | 4 | 40/40 |
| blue_hv=0.5 | 29/56/0 | 30/3/20 | 0/0 | 4 | 36/40 |
| blue_hv=0.65 | 29/56/0 | 28/2/73 | 0/0 | 3 | 35/40 |
| blue_hv=0.75 | 29/56/0 | 27/2/108 | 0/4 | 2 | 34/40 |
| blue_purity=0.25 | 29/56/0 | 31/3/1 | 0/0 | 9 | 40/40 |
| lit=0.4+mask | 29/56/8 | 31/9/75 | 1/3 | 4 | 39/40 |
| lit=0.5+mask | 28/56/13 | 31/9/89 | 1/3 | 3 | 36/40 |
| lit=0.6+mask | 28/56/14 | 31/8/92 | 1/5 | 3 | 36/40 |

- `span_per_core≤3`は40/40だが21952/21953を除外しない。≤1なら両方落ちるがformal0600を変え、21952はまだ別の不正確なlineに移る。
- 記録gap≤4px（≤2格子）のみは全結果不変。補間された点と局所bodyを見ているから。
- 無条件全lit≥.6なら21952のrank2も落ちてcompact maskになるが、旧REDの出力1件を失いformal36/40。
- BLUE maskの面積cap0.5–5%はいずれも壁画9→4、旧winnerは不変。HV≥.35も9→4、旧unknown1件がnullになる。面積capとHV条件は今回ほぼ同じ画像を落とすため、効果を足し算できない。
- mask HV≥.5は旧FP9→3になるがformal38/40。全source HV≥.65は旧実剣31→28、formal35/40。背景への効果だけで採用しない。

### 限定した候補間guard（追加検証の第一候補）

G: **未短縮raw端点を出すcore-line**（used_point_led_fallback=true）で全lit被覆率<L、かつ現フレームに以下の別候補があるときだけ除外する。

- 既存eligibleかつ同時に掛ける他のshadow条件も通るnon-core-line候補。
- core support≥.40、HV≥.35、全lit被覆≥.90。
- 両候補の端点のいずれかの距離≤30px。

単一の明点密度を証拠とせず、現在の近い別候補を要求する。L=.5/.6/.7を比較した。
上記の.40/.35/.90/30は**offline仮置き**であり、30pxは既存weak-raw-tailの近接幅と同じ。1端点の近さだけでは別の剣／反射も一致し得る。最適閾値やphysical identityが確立したという意味ではない。
rawOnlyはbody採用済み候補を除外しない単純な被覆floor。mask併用はHV≥.35かつ面積≤.02、purity併用は全BLUE purity≥.25。


| rule | 旧 RED: 出力/FP/U変更 | 旧 BLUE: 出力/FP/U変更 | 昼光変更 R/B | 壁画青FP | formal |
|---|---:|---:|---:|---:|---:|
| raw_lit=0.35 | 29/56/5 | 31/9/5 | 1/1 | 9 | 40/40 |
| raw_lit=0.4 | 29/56/5 | 31/9/5 | 1/1 | 9 | 40/40 |
| raw_lit=0.5 | 29/56/6 | 31/9/6 | 1/1 | 9 | 38/40 |
| guarded_lit=0.5 | 29/56/2 | 31/9/5 | 1/1 | 9 | 40/40 |
| guarded_lit=0.6 | 29/56/2 | 31/9/5 | 1/2 | 9 | 40/40 |
| guarded_lit=0.7 | 29/56/2 | 31/9/7 | 1/2 | 9 | 40/40 |
| guarded+mask | 29/56/2 | 31/9/6 | 1/2 | 4 | 40/40 |
| guarded+mask+purity | 29/56/2 | 31/3/7 | 1/2 | 4 | 40/40 |

rawOnly .4はformal40/40でも21952 BLUEをrank2 core-line `[32,64]–[82,118]` にするだけで、実剣の横向きmaskへ戻らない。
G .5もそのrank2を残す（全lit=.526）。**G .6**は両方の未短縮lineを除外する:

| frame / 色 | baseline | G .6 の出力 |
|---|---|---|
| 21952 BLUE | core-line `[16,66]–[310,114]`, 108.6854 | color-mask `[14,66]–[52,76]`, 61.5758 |
| 21953 RED | core-line `[14,30]–[90,118]`, 118.0986 | core-halo `[80,84]–[88,136]`, 63.3895 |
| 21953 BLUE | core-line `[30,64]–[78,120]`, 82.6022 | color-mask `[16,68]–[52,76]`, 63.8855 |

21954/21955はbody採用済みなのでGでは変更しない。旧BLUE剣あり画像では8/31の選択geometryが変わるが出力31/31を保つ。
8枚は既存ラベルの同じ静止窓、20260922_101530_202の391/392/397/398/399/407/409/411。
ORIGINALを再目視すると、変更後の端点は点灯した横向きBLUEの近傍（x約120–172、y約204–224）に戻り、変更前は遠方の窓側までつながる。
398の変更後は短いcore-line subsegmentであり、**実剣の全長・両端の正解保持を8枚すべてで保証する手ラベルはまだない**。単なる31/31を「端点も無害」と読まない。
G .6で旧unknown変更はRED2/347、BLUE5/346（null化0）。mask併用でBLUE6/346（null化1）、purity併用で7/346。

壁画の残り4 FPは32357 core-halo、32358 core-halo（maskを除外した後）、32359 core-line、32363 core-line。
壁画lineは既に局所bodyへ短縮され、GのrawOnly条件に該当しない。全lit≥.5とmask条件を併用すると9→3までだがformal36/40。
**壁画消灯時9→0かつ正式40/40を実証したruleは、このsweepにはない。**

## formal losslessの扱い

無改造C++ baselineは既存正式checkerの検出有無・source・端点許容差・必須棄却sourceを使って**40/40**。
G .6、mask面積≤.02、mask HV≥.35、G+mask、G+mask+purityはすべて40/40で、正式40色別例の選択変更0。
「別sourceでも何か検出したから合格」とする簡略照合ではない。shadowでsource変更や端点変化が出るruleは下表の失敗として残した。期待値はそのまま。

| 代表rule | 期待値と異なる正式例 |
|---|---|
| span_per_core=2 | `production_blue_frame_0600` |
| lit_gap_px=32 | `production_blue_frame_0600`, `red_long_core_line_1048`, `red_long_core_line_919`, `red_long_core_line_1091` |
| lit_coverage=0.4 | `red_long_core_line_919` |
| lit_coverage=0.5 | `production_blue_frame_0600`, `red_long_core_line_1048`, `red_long_core_line_919`, `red_long_core_line_1091` |
| mask_hv=0.5 | `production_blue_frame_0220`, `blue_raw_tail_256` |
| mask_hv=0.65 | `device_normal_blue_1249`, `production_blue_frame_0220`, `blue_raw_tail_256` |
| blue_hv=0.65 | `device_normal_blue_1249`, `production_blue_frame_0220`, `production_blue_frame_0600`, `blue_candidate_local_cancellation_251`, `blue_raw_tail_256` |
| raw_lit=0.5 | `production_blue_frame_0600`, `red_long_core_line_1048` |

例: span/core≤2でformal0600はcore-line→color-mask、端点誤差63.02px（許容3px）。無条件lit≥.5ではred_long_core_line_1048も45.72pxずれる。
「長い線を全部落とす」は正式点LED／raw endpoint保護と衝突する。

## 推奨と不足証拠

**本番採用は保留。** 調査・次captureでの優先順位はG .6、その後BLUE mask HV≥.35（面積capは別案として比較）。
面積capは近距離の大きな剣を落とし得る。HVは速振り・淡い／露出の低い青を落とし得る。どちらもthreshold固定前に保持分布が必要。
purity .25は旧ネガティブに効くが壁画には効かないので、壁画解決の条件として推奨しない。

Gは本日2例を直してformalを守るが、同じ構えの連続frameを複数eventとして数えない。
`CLAUDE.md §4` の別swingでの実剣2event以上というgateは未達であり、score差も僅差CASE Aに該当しない。
過連結専用guardの採用判断に必要な独立実剣証拠と、そのgateへの位置付けの確認が残る。gateを今回のshadow結果で緩めない。
特に長い点LED、2本同色／交差、投影光、近距離、速振りによる真の空隙を過連結と誤判定しない証拠が必要。

### 次のcaptureで決める条件

1. **屋内・壁画ありの固定画角、1/100秒、2.7–3.1m、1–2分**。同じ露出・背景で剣OFF→赤ON→青ON→両ON→OFFを行い、各状態でCapture Losslessを残す。背景の人・投影は実運用通り。停止しに来る最後だけでなく、振っている最中のORIGINALを必ず保存する。
2. **別々のswingを最低2回、各回の前後に静止して区別**。天井灯／窓／壁画の明点と剣が同一直線上になる向きから外れる向きへ、横・斜め・頭上に振る。各eventでbefore / event / afterのlossless窓を残し、実剣candidateがeligibleのままか、無関係な支持まで長くつながったかを確認する。2eventが再現しなければgateの証拠とは数えない。
3. **保持用の別recording**: 同条件の淡い青・赤を、静止／速振り／先端向け／画面端で各数回。距離0.5m（大きい面積）、約3m、本日同様5mを区別する。分離点LEDの剣も端から端まで見えるlosslessを残す。Gの別候補なしケースと、反射・2本の剣が近いケースも必要。
4. 各保存窓をORIGINALで色別に剣有無・実剣領域・両端まで手ラベルし、同じshadowを評価する。採用検討の条件は、別swingの過連結を2event以上確認、正しい別候補が現frameで残ること、実剣の新規null化／別領域選択／不当短縮がないこと、正式40/40。壁画4残存候補はOFF/ON対で特徴を比較し、屋内の「体感が良い」を保存画像の正解率に置き換えない。

## スクリプト・検証・派生物

追加した解析専用ファイル:

- `PhoneSaber/tools/coreline_shadow.py`: 無改造CLIとのparity、実支持観測、shadow sweep、正式checker照合。
- `PhoneSaber/tools/coreline_shadow_support.hpp`: 一時pipelineコピー専用の観測器。認識score・eligibility・端点に参加しない。
- `PhoneSaber/tools/test_coreline_shadow.py`: dark bridgeを支持に数えないnative測定、境界、再選択、mask面積単位、null/変更区別、現候補guardの6テスト。

再実行（標準Python、make/clang++/zlib。詳細JSONやbuildの出力先はTMPDIR配下に限定）:

```bash
python3 -B PhoneSaber/tools/coreline_shadow.py --output "$TMPDIR/phonesaber-coreline-replay"
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_coreline_shadow.py' -v
CLANG_MODULE_CACHE_PATH="$TMPDIR/phonesaber-module-cache" SWIFT_MODULE_CACHE_PATH="$TMPDIR/phonesaber-module-cache" python3 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json "$TMPDIR/phonesaber-formal.json"
```

今回はPython unit 6/6（既存recognition_benchmark unitも11/11）、C++ baseline正式40/40、候補全体parity662/662、保存winner44/44一致。Swift公式lossless harnessも再実行し40/40（positive23/23、negative FP0/17）。baseline込み49設定をshadow評価した。
本番認識とmanifest/fixtureのdiffは0。対象作業以外の既存ファイルを変更していない。画像をrepoへコピーせず、PNG合成表示・詳細JSON・ログ・バイナリは以下の一時ディレクトリだけに置いた。


`/var/folders/sf/rx78c9vs3_37dby19j8b5xv00000gn/T/phonesaber-coreline-jlfg7qsh`

`shadow.json` に全sweepの集計と正式失敗詳細、`observed.json` に全観測、`benchmark.json` に無改造の旧benchmark、`session_support.json` に今回22枚の詳細、`formal-swift.json` / `formal-swift.log` にSwift正式結果。外部inboxは読取のみ。
