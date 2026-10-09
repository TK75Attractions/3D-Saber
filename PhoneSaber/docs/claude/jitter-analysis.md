# iPhone 認識の端点 jitter 分類

2026-10-09、Mac 上で実測。基準 commit `73cf24c`、branch `opt/jitter-analysis`。
認識・定数・score・候補順・UDP・Unity は無変更。画像と詳細 JSON はコミットしない。

**結論:** 大ジャンプの主な確認済み原因は背景・肌・反射の誤検出と実剣候補の eligibility 消失。
静止時にも、剣の本体と離れた背景を含む raw PCA の長さ変動が見える。
PCA 内部の A/B 反転はあるが、記録された最終出力では既存の順序安定化により吸収されている。
本物の剣で §4 修正 A を認める「別 swing 2 event」の証拠は今回も得られなかった。
数値要約: 70,622色別行、最終A/B反転0件、診断窓の大ジャンプ56件中34件が剣なし、
静止青9遷移中5件が53–58px変動。formal40/40、認識bit parity mismatch0。作成物は解析ツール・テスト・資料のみ。

## 対象・測定方法

- Downloads の `phonesaber_*_metadata.json` 18 session、34,874 frame、赤青 **69,748 行**。
  forensic は16 directory / 354 PNG。`*_analysis` は5枚の annotated PNGだけで、別の時系列ではない。
- diagnostics-inbox は32 session / 242 context JSON。重複窓を統合して437 frame / **874 行**。
  242 PNGには annotated が含まれる。全 forensic / inbox PNGの寸法を確認し、すべて480×640。
- `real_motion_extract.py` の抽出を利用。追加の `diagnostic_frames()` は保存された診断だけを読む。
  同じ session/frame/color の compact context は最も情報量の多いものを一度だけ採用する。
  既存 `phone_saber_selection_replay.py` と `candidate_selection_audit()` を再利用した。
- 隣接条件は frame ID +1、capture時刻差 `0 < dt ≤ 50 ms`。色を混ぜず、欠測や時刻穴をまたがない。
  両端で `detected=true`、端点あり、既知の `predicted=true` でない組だけを変位分布に入れる。
  古い未記録 predicted は未知のまま保持し、既知 false だけの感度分析も出す。
- 端点変位 D は `max(|Aₜ−Aₜ₋₁|, |Bₜ−Bₜ₋₁|)`、単位は元画像 px。
  direct/cross の距離和が小さい方で対応した D と midpoint 変位も計算する。quantile は最近傍順位。
  内部反転疑いは cross が有利、direct D ≥20px、対応後 D ≤ direct D/4。実回転や別候補も混ざり得る。
  これは**解析用の定義**であり、production threshold ではない。
- 静止/移動は端点速度で分類せず、[元画像ラベルとSHA-256](data/jitter-image-labels.json)を人手で付けた。
  再測定時に110 ORIGINALファイルのhashを検証する。手持ちの「ほぼ静止」は微小な実移動を含み、
  三脚固定の物理的ground truthではない。隣接 frame・色・重複画像を独立 swing と数えない。
- 9/21〜10/06 の**当時の認識結果**を分析する。capture の認識 build hash は揃っていない。
  後日の決定性・赤/青背景filterを既存metadataへ適用した結果ではなく、現行アプリの不具合率でもない。

## 静止と移動の変位分布

| ORIGINAL確認区間 | 色・遷移数 | D p50 | p90 | p95 / 最大 | ≥50px | midpoint p50 / 最大 |
|---|---:|---:|---:|---:|---:|---:|
| `20260922_101530_202` f390–392 / 397–400 / 407–411、ほぼ静止 | 赤 9 | 4.0 | 6.0 | 6.0 / 6.0 | 0 | 下記合算参照 |
| 同区間、ほぼ静止 | 青 9 | 53.1 | 58.2 | 58.2 / 58.2 | **5** | 下記合算参照 |
| 同区間、赤青合算 | 18 | 4.5 | 55.7 | 58.2 / 58.2 | 5 | 1.4 / 28.6 |
| `20261003_144936_295` f2550–2557、青を持つ手の姿勢ほぼ一定 | 青 7 | 14.6 | 16.5 | 17.0 / 17.0 | 0 | 10.0 / 13.3 |
| 同区間、赤を横振り→先端向け→横振り | 赤 7 | 55.8 | 186.4 | 260.2 / 260.2 | 4 | 38.3 / 234.4 |

移動赤の2遷移（f2552 / 2553）は認識失敗と復帰を含む。260pxを実際の剣の移動量と読まない。
静止の25遷移・移動の7遷移は2 sessionの小標本で、撮影条件も異なる。
18録画すべてを静止/移動と判定できる密な原画像やガイドラベルはなく、残りは unknown とした。

| 全体・感度分析 | 有効遷移数 | D p50 / p95 / 最大 | D ≥100px | midpoint p50 / p95 |
|---|---:|---:|---:|---:|
| Downloads、未知 predicted を許す | 63,419 | 14.1 / 241.1 / 705.0 | 8,429 | 9.2 / 174.6 |
| Downloads、両端 predicted=false が既知 | 11,425 | 14.4 / 94.3 / 562.8 | 519 | 9.5 / 56.4 |
| triage イベント窓 | 237 | 18.0 / 474.5 / 502.6 | 56 | 12.6 / 445.6 |

未知 predicted を含む遷移は51,994件。全体の分布は物理移動・背景FP・endpoint破綻の混合である。
triage は異常選抜の窓なので、全録画との割合比較や全体への外挿はできない。

## 4種類の件数と具体例

### 1. A/B 順序反転

送信に使われる記録端点では、Downloads **0 / 63,419**、triage **0 / 237**。
緩い「cross の距離和が小さい」条件でも0件なので、20pxの解析境界だけで0になった結果ではない。
`FrameProcessor.stableEndpoints()` は前回端点への direct/cross 距離和で順序を選ぶ。
名前付き A/B に、物理的な柄/先端という意味が保証されるわけではない。

順序安定化**前**の保存候補では反転疑いがある。

| 保存段階 | Downloads（60,303比較） | triage（107比較） |
|---|---:|---:|
| rawPCAEndpoints、cross有利 / 厳しい反転疑い | 5,076 / **699** | 19 / **1** |
| 候補のfinalOutputEndpoints、cross有利 / 厳しい反転疑い | 5,575 / **689** | 19 / **1** |

Downloadsの厳しい反転疑いのうち、後述の同じbody代理条件を満たすのは raw 455 / final 461件。
残りを同一物体の反転と断定しない。例は `20261003_144936_295` **f1908 青**。
同じ core-line のraw順序では146.9px、対応後/送信端点では19.7px、midpoint11.4px、span140.32→140.01px。
ORIGINAL f1907/1908では画面右端の青剣が見え、候補の領域も連続している。
この反転の約127px分は既に吸収されており、今回の大ジャンプ対策の優先原因ではない。

### 2. winner の候補切替

triage の tracking 記録は322色別frame、`candidateSwitch=true` は**18 frame**。
有効な隣接測定ができる切替は15件、そのうち D ≥100px は13件。
Downloadsにはこのidentity診断がなく、候補indexやtype変更だけで切替件数を捏造しない。
以下は記録された18件の全一覧（戻りも含む遷移数で、独立 event 数ではない）。

| session（`phonesaber_`省略） | frame / 色 | 原画像と解釈 |
|---|---|---|
| 20261002_005850_489 | 2537, 2538, 2543 赤 | 点灯赤剣なし。2537の背景切替→2538戻り。D441.0、midpoint421.8px |
| 20261002_010049_190 | 665 赤 | 点灯赤剣なし。core-halo→core-line、raw span46.0→218.3px |
| 20261002_013205_087 | 255, 256, 260 赤 | 点灯赤剣なし。255の背景切替→256戻り。D442.8 / 441.1px |
| 20261003_155919_297 | 5219, 5220 青; 5331 赤 | 机/包装/画面、肌・反射。5219/5220はD316.1 / 322.1px |
| 20261004_005325_638 | 198 赤; 200 赤青; 201 赤 | 赤剣なし。200赤D490.2、201赤489.6px。青は左端の実剣/青照明と候補を要照合 |
| 20261004_220440_308 | 622 青 | ORIGINALは膝・床・ケーブル。青剣なし。D356.9px |
| 20261006_004003_239 | 2525 赤 | 赤剣の先端向け。前frameで剣候補ineligible、勝者は背景 |
| 20261006_010346_502 | 8135, 11605 赤 | 8135は赤剣なし。11605は別の手動swing画像で、隣接測定なし |

有効遷移のない198赤・5331赤・11605赤も、記録されたtracking切替として18件に含めた。
returnは2538/256/201など。同じ変化を「switch-outの根拠」と「復帰の根拠」の2 eventには数えない。

既存CASE auditは `countForTally=true` **115行のみ**を集計し、A hint13 / B hint39 / C hint19 / none44。
これは保存されたgeometryによるhintで、実剣のCASE A/B/C件数ではない。
例えば **f2553赤のA hint**は実剣への復帰で、gap43.27点。僅差の誤選択を示さない。
**f2552赤のB hintはunknownTruncated**だが、保存された実剣位置のineligible候補を原画像と照合できる。
他の omitted candidate の存在まで断定しない。

### 3. 同じ領域の PCA / endpoint 長さ変化

- 記録trackingで `geometryMatch & !candidateSwitch` の66 frame中、絶対lengthChange ≥10 / 25 / 50px は **7 / 4 / 2**。
  同じtypeかつ前後の端点を測定できる37遷移に絞ると **2 / 1 / 1**。
  geometryMatchも物理真値ではなく、同じ画像領域の候補対応である。
- 古いmetadataにはbbox/identityがない。補助的に「同じsourceType、前robust本体長≥20px、
  robust端点の対応後変位≤前本体長/4」を同じbodyの代理条件とした。
  Downloads **26,769遷移**、出力長変化≥10 / 25 / 50px は **2,667 / 1,466 / 1,205**。
  この集計は候補同一性の確定件数ではなく、実移動・候補分裂/結合も含む。
- **静止実剣の例:** `20260922_101530_202` 青 f390→391。
  原画像の青剣は左寄りの短い棒でほぼ同じ位置。robust44→52pxに対し raw397.16→345.10px。
  出力は `(444,182)–(52,228)` → `(394,200)–(52,224)`、D53.1px。
  同じ core-line の本体は保たれ、離れた背景を含む長い軸の遠端が動く。
  f392 / 408 / 410 / 411にも53.9 / 55.7 / 58.2 / 58.2pxの同種変化があり、
  静止青の9遷移中5件を占める。候補index0の維持だけを根拠にはしていない。
  `*_analysis/debug_blue_390.png`の線と原画像も照合したが、annotatedを真値には用いていない。
  同じ9遷移で保存済みrobust端点だけを仮に使うと、D p50は53.1→8.2px、最大58.2→12.2px、
  ≥50pxは5→0。短縮による見かけの安定化もあり、正しい全剣長の保持や修正Bのgateを証明する数字ではない。
- **endpoint経路自体の例:** `20261003_155919_297` 赤 f5321→5322。
  connected-coreでgeometryMatch、raw span415.39→413.86px、raw端点D2px。
  最終出力長412.99→170.42px、D236.5px、midpoint112.7px。
  PCA自体の移動より、robust/body採否の切替が原因。原画像は肌・腕・反射で点灯赤剣なし。
  f5321の+161.6pxも記録値としてあるが、その前frameを測れる隣接集合には入れない。
- `010049 f665` のraw/robust乖離は候補typeも切り替わるので、同一候補だけの例には数えない。

### 4. dropout / predicted

| 色別frame・遷移 | Downloads | triage窓 |
|---|---:|---:|
| output detected=false | 5,175 / 69,748 | 333 / 874 |
| predicted=true / false / 未記録 | 306 / 13,058 / 56,384 | 64 / 810 / 0 |
| 隣接output true→false / false→true | 680 / 673 | 19 / 25 |
| 時刻/ID穴で除外した比較 | 0 | 236 |
| 欠測・予測等で変位から除外した隣接比較 | 6,293 | 337 |

predicted出力はdetected=trueを取り得るので、true→falseだけでは生の認識失敗を数えきれない。
例は `20260923_113614_837` **f348赤**：`redDetectionSucceeded=false`、`detected=true,predicted=true`、
出力 `(312,254)–(324,242)`。保存されたlast_true/false/recovered ORIGINALがある。
`20261003_144936_295` **f1908/1909赤**はeligibility段階失敗、出力なし・predictionUsed=false。
その後f1911は成功。保存されていないf1910の状態は補わない。
予測/欠測は独立の分類であり、候補消失や背景切替と排他的ではない。

## 何が見える揺れを支配するか

triageのD≥100px **56遷移**のうち、両側のORIGINALを見てこの色の剣なしと分類できるのは
**34件（60.7%）**。内訳は記録switch11、同typeのgeometry連続endpoint破綻1、identity不明22。
ほかに実剣の速振り失敗/復帰2、未確定20。34件は誤出力の下限であり、残り20件を正常とも異常とも断定しない。
この選抜標本では、A/B入替より**誤った物体へ出力すること**が大ジャンプを支配する。
静止青の限定標本では、raw PCAの遠端/長さ変化が5/9遷移の50px級揺れを説明する。

実剣での別原因も確認できた。
`144936 f2552` は赤剣がカメラ方向へ向き、1/50秒露出で円盤状にぶれる。
実剣/腕領域のcolor-maskはhasEmitterCore/emitterScore失敗、小さい実剣coreもcompactRedColorPurity失敗。
eligibleは背景のラベル1件のみで、D186.4px。CASE Aの期限付き選好では救えない。
`20261006_004003_239 f2524` は点灯赤先端が画像中央に見えるが、剣位置のcolor-mask/core-haloは
compactRedHighValueRatio / compactRedColorPurityでineligible。唯一のeligibleは背景 `(124,188)–(130,196)`。
一方、`20261006_005910_095 f2140` のD185.2pxは、背景から先端の本物 `(258,342)–(274,326)`へ戻る
**正しい復帰**。ジャンプ抑制でこの移動を止めるべきではない。

## 修正候補の期待効果順と CLAUDE.md §4 gate

以下は提案だけで、本タスクではすべて未適用。効果量は異常窓の範囲以外へ外挿しない。

| 優先 | 具体案・期待する効果 | ある証拠 | §4との対応・不足する証拠 |
|---|---|---|---|
| 1 | 残存FPの発光支持・候補生成/eligibilityを物体別に見直す。背景への400px級移動を減らす期待が最大。まずno-saber/点灯対を同条件で撮り、肌・窓・包装・画面別に分布を測る | 56ジャンプ中34は剣なし。背景だけでも大変位。新しい部屋/学校にもFPがある | 単なる修正Aではない。§4の「証拠なしのproduction修正禁止」に従う。既存赤/青filter適用後の実機、未使用の人・場所・淡い剣の保持、formal40/40とnegative/positive recallが必要。旧metadataの率を現行filter効果として使わない |
| 2 | 修正B候補：本体と遠いtailを分け、raw/robust/妥当なbody PCAとtail支持を比較する。採否の境界で急に全長が変わる条件もoffline比較 | 静止青9遷移のrobust仮置換でD中央値53.1→8.2px、最大58.2→12.2px。f390 raw397 / robust44。f5322 rawD2なのにfinalD236.5 | §4 BはAと別commit、Aの後。raw/robust乖離はあるが、単独bodyの棒形状・弱tail支持・分離LEDでないこと・body PCA妥当性を全部示す診断が不足。点LEDが見える剣を画像だけで「非分離」と断定しない。長剣/分離LED/端欠けを短縮しないregression必須。現在は未許可 |
| 3 | 先端向け/速振りのCASE B対策。1/50対1/100–1/120の同じ動作を比較し、生成前に落ちたcomponentと露出・purity・emitterを記録する | f2552ブラー、f2524実剣のcompact条件失敗。正しい候補がeligibleに残らない | §4 A条件を満たさず、過去endpoint固定では救えない。Bのtail gateとも別。短露出で暗い/遠い青を失わない比較、照明縞・ISO・複数swingの未使用captureが不足。eligibilityの一律緩和は根拠なしに実装しない |
| 4 | 修正Aの期限付きgeometry選好をofflineで継続評価。現frameのeligibleだけから選び、消失時即解除 | 完全eligible保存275色別frame、隣接比較64でmidpoint≥100pxは27。margin1/3で27→27、margin5で27→25。変更は005325 f200赤1frame、gap3.7724 | その2遷移改善は**背景FPを固定した結果**。実剣の別swing2 eventという§4 A gateは0 confirmed。f2553の復帰やf2140の正常復帰を妨げない評価も必要。0.5span/IoU.2/3frame/score5は既存replayの解析入力でありproduction根拠ではない |
| 5 | dropoutからの復帰・予測の境界を診断し、現在の順序安定化を維持する。新たなA/B補正は優先度低 | 出力A/B反転0、内部反転は吸収。306/64予測frameを分布から除外 | A/Bが目に見える主原因という証拠なし。§4のA/B変更gateの代替にはならない。予測改善なら可視剣の手付け端点と欠測直前/復帰の誤差、異物を追い続けない評価が不足 |

候補indexをidentityとしない。Aのoverrideは期限を延長せず、正しい候補が消えたら解除する。
diagnostics由来のA/B/C hintや、背景だけのofflineジャンプ減少でgateを通したことにしない。

## 再現・変更前後・検証

Git rootで実行。出力はprivateな詳細データなので `/tmp` に置く。

```bash
python3 -B PhoneSaber/tools/jitter_analysis.py \
  --downloads "$HOME/Downloads" \
  --inbox "$HOME/Library/Application Support/PhoneSaber/diagnostics-inbox" \
  --labels PhoneSaber/docs/claude/data/jitter-image-labels.json \
  --output /tmp/jitter-census.json
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_*.py' -v
PHONESABER_VERIFY_LOG_DIR=/tmp/jitter-verify \
  bash PhoneSaber/tools/verify_phone_saber.sh
# 初回に書込拒否されたSwift module cacheだけをwritableな場所へ移して再検証
CLANG_MODULE_CACHE_PATH=/tmp/jitter-swift-cache \
SWIFT_MODULECACHE_PATH=/tmp/jitter-swift-cache \
  python3 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py \
  --json /tmp/jitter-lossless.json --csv /tmp/jitter-lossless.csv
CLANG_MODULE_CACHE_PATH=/tmp/jitter-swift-cache \
SWIFT_MODULECACHE_PATH=/tmp/jitter-swift-cache \
  python3 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools \
  -p test_diagnostic_parity.py -v
git diff --check
```

変更前後の抽出は同じMac・同じ入力で比較した。行数、端点、フラグ、時刻、寸法、匿名ID、JSON bytesが一致。

| 比較 | before → after | 差 |
|---|---:|---:|
| Downloads抽出行数 / JSON bytes | 69,748 / 16,062,865 → 同じ | **0** |
| triage抽出行数 / JSON bytes | 874 / 190,149 → 同じ | **0** |
| production recognition / UDP / Unityファイル差分 | 0 → 0 | **0** |

抽出SHA-256はDownloads `6eda205b951fe5bea9b32a059087bd95b483d89abf43db49760f7adebeb828bc`、
triage `ff00506cec61bd2deeae391fcf7e83c46d908e9fdf88d56dcf2e1f6b93ec9f74`で前後一致。
最終解析harnessは10.484秒、110 ORIGINAL hash検証。これは精度改善や実機処理速度のbefore/afterではない。

検証結果:

- 解析・tools suite **65 test、PASS、1 skip**（sandboxでlocalhost UDP bind拒否）。追加5 testは反転の吸収、
  重複窓、時間穴、予測除外、未知flag、原画像hash不一致を検証する。
- Static BGRA Detection **PASS**。cacheを移したformal lossless **40/40**、期待値変更なし。
- 52 fixture PNGで診断off/on/on+profiling、通常hash seed / deterministic hashのbit parity **PASS、mismatch0**。
- 公式verify全体は**PASSではない**。初回lossless/parityの失敗は `~/.cache/clang/ModuleCache` 書込拒否で、上記再実行で解消。
  既存iOS Tools広範囲suiteは374 test、9 failure / 26 error / 1 skip。
  localhost socket bindの `PermissionError: Operation not permitted`、Swift cache書込拒否、
  P2P launcherのcompiler child待ち失敗を含む。未解決の広範囲suiteを成功扱いしない。
- XCTest: `simctl list devices available -j` を2回試したが、CoreSimulatorService接続拒否（POSIX61）で
  device setを取得できず**未実行**。sandbox内でSimulatorサービスに接続できない。
- iOS Release:2回とも `sandbox-exec: sandbox_apply: Operation not permitted`、
  SwiftUIMacros.StateMacroのswift-plugin-server malformed responseでbuild失敗。認識sourceのコンパイル失敗とは区別する。
- Unity checksは対象外で未実行。公式verifyにもEditor所有によるBLOCKED表示がある。
  Android/gradle・通信遅延・実機再試験はこのread-only解析の対象外で未実行。
- `git diff --check` PASS。自己レビューで候補・score・eligibility・PCA/fallback・winner・diagnostics・
  浮動小数点計算順・RED5005/BLUE5006/discovery5007を含むproduction差分なし。
  pushなし。**commitは未完了**：通常の `git add` を2回実行したが、
  `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-jitter-analysis/index.lock` 作成を
  sandboxが `Operation not permitted` で拒否した。`git commit --only` も試したが、新規4ファイルは
  index未登録のためpathspecエラーになった。許可設定の変更や別のindexによる迂回は行っていない。
  5ファイルの変更はworktreeに残り、目的・before/after・検証を含むcommit本文は
  `/tmp/jitter-commit-message.txt` に準備済み。
