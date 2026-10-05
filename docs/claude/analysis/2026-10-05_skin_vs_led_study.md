# 赤候補: 肌・光と点灯LEDのoffline study（2026-10-05）

**結論: 全ラベルを余裕付きで分離する頑健なルールは確認できなかった。productionへの適用は推奨しない。** 色相＋core支持率の弱いshadow候補はformal 40/40と既存の正しい剣出力を維持するが、消灯時の照明・青い光を受けた肌・その他の背景が残る。Swift、本番threshold、fixture期待値、CSVは未変更。commitなし。

## データと目視ラベル

元PNGの縮小一覧198枚を目視し、232850の候補座標と、formalの問題例は原寸でも照合した。annotatedは正解に使わない。inboxには今回34 bundleが存在するが、分析範囲はCSVの25 session＋指定の232850のみ。未ラベルの他bundleへ結果を外挿しない。

| 入力 | 元PNG / 評価単位 | RED正解 |
| --- | ---: | --- |
| 10-03 CSV（10-04追記を含む） | 151 / 25 session | saber 46、absent 100、unknown 5 |
| 232850追加 | 12 / 1 session | saber 3409・3888・5449、absent 1248–1255・6288 |
| formal lossless | 35 / 40 fixture・9 source group | 11 RED positive期待値、7 RED negative、22 BLUE評価 |
| background negative benchmark | 8 / 5 session | 全8枚が上のCSVに含まれる。二重計上しない |
| 合計 | **198元PNG、594 gain/frame評価** | eligible RED 1,606観測（同じ物体の重複あり） |

全RED候補は7,697観測（gain 0.92 / 1.00 / 1.08で1,443 / 2,500 / 3,754）、元画像のeligible候補469件の内訳は**LEDを含む133、肌134、光10、other174、unknown18**。色マスク上／候補全体の画素を分けた。CSVのsaber boxとの自己面積25%以上の重なり、または出力中点がbox＋15px内なら「saberを含む候補」。それ以外は元画像で描いた肌／光box内の中点で分類、残りはother。広いproposalは剣と肌の混合を含むため、「133個の独立した純LED」ではない。formalには独立した目視boxを定義した。

重要な不一致: `red_short_component_last_true_89`の正式期待端点 `(422,146)–(444,146)` は元PNGの窓の明るい領域。赤剣は右下（目視box `425 600 480 640`）。**この期待値は保持し、分類はlightとした。** formal 40/40と「光を全排除」はこの例では両立しない。formalの全positiveを無条件にLED学習ラベルにすると結論を歪める。

232850のoriginalでは1248が照明ハロー、1249・1251・1255が顔、1250・1252–1254が足指、6288が足のwinner。消灯8枚に光eligibleが各1、肌eligibleは計11。点灯3枚は9 LED候補と背景も共存。画像で確認できた背景物は各captureのotherとして扱い、現在の部屋に赤ラベル・カラビナが存在するとは仮定しない。step 0の598/598は既存session集計であり、この研究で598枚を目視／再生した数字ではない。

## 特徴量と分離余白

`phone_saber_rule_study.py`の一時コピーpatch、ラベル判定、BGRA gainを再利用。候補の実際のsample点とcolorMask所属だけを一時Swiftにexport追加し、元の生成・eligibility・rankingは維持。無変更harnessとの全RED診断項目・順序・eligibleの一致は198/198。推奨shadowは最後の順位済みRED候補列に対して計算し、内部の候補生成やBLUEに適用しない。

G/R・B/R、符号付きHSV色相、S・V・chromaのmean/p10/p50/p90、purity、clippedWhite、core支持、局所contrast、d240、bbox aspect、面積、輝度std・variation、2px横方向V勾配を計測した。局所contrastは既存の色画素対周辺の値であり、独立したcore/halo分割のcontrastではない。d240はsampleStep=2の逆変換を含む `scaled axialDensity × 4 × 240 / min(original width,height)`、axialDensityが0の場合は `pointCount / max(rawPCASpan,2)` をscaled densityの代用とする（R7eと同じfallback）。面積はsample数×4。色比等は候補内かつproduction colorMaskが1の点を主集計に使用。

AUCは元画像eligible候補133 LED対144肌＋光、LED側が高くなる方向。gapは符号を合わせた `min(LED)−max(肌/光)`。正のgapのみ全候補を単一閾値で分離できる。全て負、他の背景を含めるとさらに難しい。

| 特徴（方向） | LED範囲 | 肌＋光範囲 | AUC | gap |
| --- | --- | --- | ---: | ---: |
| G/R mean（低） | 0.170–0.861 | 0.586–0.896 | 0.934 | -0.275 |
| B/R mean（低） | 0.208–0.871 | 0.440–0.872 | 0.816 | -0.431 |
| signed hue mean °（低） | -14.249–15.283 | -13.243–18.539 | 0.935 | -28.527 |
| V p90（高） | 235.400–255.000 | 219.600–254.000 | 0.993 | -18.600 |
| purity / S mean（高） | 0.150–0.838 | 0.138–0.560 | 0.881 | -0.410 |
| clippedWhite（高） | 0.000–1.000 | 0.000–1.000 | 0.676 | -1.000 |
| core支持率（高） | 0.007–1.000 | 0.000–1.000 | 0.641 | -0.993 |
| d240（高） | 1.091–35.706 | 2.000–41.558 | 0.498 | -40.467 |
| bbox aspect（高） | 1.000–15.609 | 1.000–47.909 | 0.559 | -46.909 |
| 面積 px相当（高） | 48.000–13940.000 | 84.000–35056.000 | 0.466 | -35008.000 |
| 局所contrast（高） | 0.000–1.000 | 0.000–1.000 | 0.788 | -1.000 |
| 横V勾配（高） | 1.364–32.033 | 0.320–36.810 | 0.673 | -35.446 |
| 輝度variation（高） | 0.019–0.820 | 0.017–0.598 | 0.617 | -0.579 |

V p90のAUCは高いが、暗い赤剣・窓・白く飽和した肌の範囲が重なる。G/R≤0.50単独は元画像formal **31/40**に低下した。色相も暖色／白飛びの剣 `frame_1048` 15.283°を皮膚から分離できない。肌に青光が当たる155919の候補は負の色相やcore支持率1にもなる。厚さ・棒形状だけでも大きい腕や混合proposalが通る。

## 最終shadow候補の成績と失敗

| gain | ラベルabsent 109枚: 赤FP base→shadow | 可視赤剣49枚: 正しいwinner base→shadow | 正しい出力の保持 | formal base→shadow |
| --- | ---: | ---: | ---: | ---: |
| 0.92 | 31→19 | 33→33 | 33/33 | 17/40→17/40 |
| 1.00 | 80→57 | 33→33 | 33/33 | **40/40→40/40** |
| 1.08 | 98→96 | 35→35 | 35/35 | 12/40→12/40 |

formalのgain列は元画像用のsource/type/endpoint期待値をそのまま使った厳格結果。±8%でbase自体が変化しており、40/40を露出変動全体で満たしたとは言わない。gainはBGRを一様にround/clampするデジタル感度試験で、実際のAE・AWB・sensor clipping変化ではない。

元画像の可視赤剣に対する絶対recallは33/49=**67.3%**。元から11枚none、5枚背景winnerで、候補を除外するshadowでは100%にできない。「追加の見逃し0」と「全可視剣のrecall100%」を区別する。formalの目視上正しいRED winnerも17/17保持。gainを合わせた既存の正しいRED winnerは147/147保持するが、eligible LED候補全体は424中2件（+8%の代替proposal）をshadowが落とす。候補単位の100%recallも達成していない。

元画像の肌28/134、光4/10、other47/174候補をshadowで却下。232850の消灯9枚は**9→4 FP**（消灯stepの8枚は8→4、6288は1→0）、点灯は3/3保持。1248の照明、1250・1252・1253で肌を落とした後の照明が残る。+8%では別の背景候補へ移り、消灯9/9にFPが残る。155919の11枚は11/11 FPのまま。既存negative benchmarkも7/8→6/8に留まる（−8% 4→3、+8% 8→7）。

shadowの参考連続score `max(11−h,100×(core−0.28))` のAUCは元画像**0.674**、全gain候補**0.619**。元画像の全候補gapは−71.650で、分離余白なし。正しいbaseline winnerだけの最小pass余白は元画像1.707 score、全gain0.356 score（−8% `frame_1000`、h=10.644/core=0.264）。暖色剣は−8%でcoreが0.309まで低下する `frame_1048` もあり、大きい安全余白ではない。

同一データで閾値を探索した探索的結果。連続event8枚、同じ物体の複数proposal、gain複製を独立標本とはしない。session holdout・別人・別照明での外部検証なし。従ってAUCから汎化や統計的な100%recallを主張しない。

## session内訳（元画像、全26 session）

FPは剣があるframeの背景winnerも含む。候補欄は LED / skin / light / other / unknown。formalは別表の互換性評価であり、ここでは9 source groupをまとめた参考行。

| session（phonesaber_省略） | PNG | eligible候補内訳 | 背景winner base→shadow | 正しい剣winner base→shadow |
| --- | ---: | --- | ---: | ---: |
| 20260927_230332_382 | 4 | 9 / 0 / 0 / 6 / 0 | 0→0 | 2→2 |
| 20260928_013117_739 | 2 | 0 / 0 / 0 / 1 / 0 | 1→1 | 0→0 |
| 20260929_140729_518 | 3 | 0 / 0 / 0 / 0 / 0 | 0→0 | 0→0 |
| 20260929_152037_074 | 4 | 0 / 9 / 0 / 1 / 0 | 2→2 | 0→0 |
| 20260929_152331_131 | 2 | 0 / 0 / 0 / 1 / 0 | 1→0 | 0→0 |
| 20260929_185841_367 | 3 | 0 / 0 / 0 / 0 / 0 | 0→0 | 0→0 |
| 20260929_190505_735 | 2 | 0 / 0 / 0 / 1 / 0 | 1→0 | 0→0 |
| 20260929_191853_743 | 2 | 0 / 0 / 0 / 0 / 10 | 0→0 | 0→0 |
| 20260929_192926_896 | 3 | 0 / 0 / 0 / 0 / 0 | 0→0 | 0→0 |
| 20260929_203439_379 | 3 | 4 / 0 / 0 / 0 / 0 | 0→0 | 1→1 |
| 20260929_203710_701 | 3 | 0 / 0 / 0 / 1 / 0 | 1→1 | 0→0 |
| 20260929_220028_395 | 1 | 0 / 0 / 0 / 0 / 0 | 0→0 | 0→0 |
| 20260930_005824_838 | 1 | 0 / 0 / 0 / 0 / 0 | 0→0 | 0→0 |
| 20260930_234740_348 | 8 | 14 / 8 / 0 / 5 / 0 | 2→1 | 4→4 |
| 20261001_002509_264 | 10 | 14 / 3 / 0 / 11 / 8 | 2→1 | 5→5 |
| 20261001_003921_526 | 9 | 12 / 4 / 0 / 6 / 0 | 2→2 | 4→4 |
| 20261001_005134_111 | 10 | 19 / 0 / 0 / 12 / 0 | 1→0 | 5→5 |
| 20261002_005850_489 | 12 | 0 / 0 / 0 / 27 / 0 | 11→8 | 0→0 |
| 20261002_010049_190 | 12 | 0 / 0 / 0 / 29 / 0 | 9→7 | 0→0 |
| 20261002_013205_087 | 12 | 0 / 0 / 0 / 28 / 0 | 11→11 | 0→0 |
| 20261003_144936_295 | 11 | 19 / 2 / 0 / 5 / 0 | 3→0 | 7→7 |
| 20261003_155608_448 | 1 | 0 / 0 / 0 / 1 / 0 | 1→1 | 0→0 |
| 20261003_155919_297 | 11 | 0 / 87 / 0 / 13 / 0 | 11→11 | 0→0 |
| 20261004_005325_638 | 11 | 0 / 1 / 0 / 14 / 0 | 10→10 | 0→0 |
| 20261004_005855_691 | 11 | 4 / 1 / 0 / 9 / 0 | 7→1 | 2→2 |
| 20261004_232850_471 | 12 | 9 / 17 / 8 / 3 / 0 | 9→4 | 3→3 |
| formal | 35 | 29 / 2 / 2 / 0 / 0 | 1→1 | 17→17 |

## 再現・検証

```bash
python3 -B ios/PhoneSaberSender/Tools/phone_saber_skin_study.py --json /private/tmp/phonesaber-skin-study/report.json
python3 -B -m unittest discover -s ios/PhoneSaberSender/Tools -p 'test_*.py'
git diff --check
```

Python外部package不要、`xcrun swiftc`・ffmpeg・ffprobeが必要。scriptの手描きboxとformal目視boxがラベル定義。reportの各候補は元path、sha256、gain、rank、geometry、category、featuresを持つ。全候補にはineligibleも含むが、AUC／却下集計はeligibleのみ。JSONはrepo外にのみ保存可。原PNGをコピーしない。

研究用9 tests PASS。要求されたTools全体は410 tests、8 failures・33 errors・1 skipでPASSではない。主因はsandboxのsocket bind禁止、tracking E2Eのavailable disk=0、compiler child検出テストの失敗。Swiftキャッシュを/tmpへ移して再実行済み。これらの既存testやgateは変更しない。無変更harnessとの198/198 parity、formal元画像40/40、`git diff --check` PASS。

## 元画像の同一性（path＋SHA-256のみ）

`inbox/` = `~/Library/Application Support/PhoneSaber/diagnostics-inbox/`、`repo/` = このworktree。以下は解析した全198 ORIGINALのinventory。画素・private PNGはrepoへ追加していない。

<details>
<summary>198 ORIGINALのpathとSHA-256</summary>

| path | sha256 |
| --- | --- |
| `inbox/phone_saber_triage_phonesaber_20260927_230332_382/images/image_03_red_dropout_false_1339.png` | `973e866848db8a754db78846e2c652eeddf9f748eed8173a68fd4b01989649a9` |
| `inbox/phone_saber_triage_phonesaber_20260927_230332_382/images/image_04_red_dropout_false_1342.png` | `833292f88e9292c0c140a73f6539d1e58bfaf3c0b5b07deee5b54b9d2c3739ed` |
| `inbox/phone_saber_triage_phonesaber_20260927_230332_382/images/image_01_manual_frame_1420.png` | `7c1e1f3d891f0b9f10556efa55840e73089205452b0634008c1c45d10b5bf3e9` |
| `inbox/phone_saber_triage_phonesaber_20260927_230332_382/images/image_02_manual_frame_1580.png` | `88b4c1b9ea293c66753ec54eb6d1f92a26d3174b16580ae64152fa8ea13f0d1d` |
| `inbox/phone_saber_triage_phonesaber_20260928_013117_739/images/image_01_frame_758.png` | `1a01e3582bad0376ccd0bc43a2fe4185395632eb63eeedef4cc9977b464c8657` |
| `inbox/phone_saber_triage_phonesaber_20260928_013117_739/images/image_03_frame_759.png` | `fec95b649c8f64e3e6a41f3d957d1c317e332f67f64e1fa6edeb5182944f8e79` |
| `inbox/phone_saber_triage_phonesaber_20260929_140729_518/images/image_03_frame_798.png` | `5d6db7e45a81d5a936acae149551c96aca52c5b81c9f3595e4f5558c4e1bc685` |
| `inbox/phone_saber_triage_phonesaber_20260929_140729_518/images/image_01_blue_dropout_false_816.png` | `0694819a47c7668419ea5bf9d4fa843dd71e6a1f6bc40302b0552b95826b460d` |
| `inbox/phone_saber_triage_phonesaber_20260929_140729_518/images/image_02_blue_dropout_false_820.png` | `98f5c8f548d9d38d176bc768eb2a6a23046c1aba5e47767d1917c7d38b64dda2` |
| `inbox/phone_saber_triage_phonesaber_20260929_152037_074/images/image_03_frame_93.png` | `56fbb8bb8b33be3dcc9d0f9ca59d5eb83dd118aee8308978617885ff9f0dc35e` |
| `inbox/phone_saber_triage_phonesaber_20260929_152037_074/images/image_04_frame_96.png` | `2f2e9ab5dd84068afac3a6f3ca95ac24f547d860ce438aeef94430bd5be20a77` |
| `inbox/phone_saber_triage_phonesaber_20260929_152037_074/images/image_01_red_dropout_false_147.png` | `add7f7caf04b76951409803d55cd6aa35cb040aad2027ffd1db32411bbe27664` |
| `inbox/phone_saber_triage_phonesaber_20260929_152037_074/images/image_02_red_dropout_false_156.png` | `2f9478ce00943550a700f37ac3bcda2aa0eab1349e16e2c9ac0795dfea482099` |
| `inbox/phone_saber_triage_phonesaber_20260929_152331_131/images/image_01_red_dropout_false_91.png` | `b47330ffb56cf135c5c47484d4970280e62dc2e63899e167295475c698af2966` |
| `inbox/phone_saber_triage_phonesaber_20260929_152331_131/images/image_02_blue_dropout_false_96.png` | `a9c811bd5ccdad9008adfaeae1f9dd8411f964a5c0e474cbd84290ed92c89cec` |
| `inbox/phone_saber_triage_phonesaber_20260929_185841_367/images/image_03_frame_326.png` | `e4214035383b1d5da440203e4e20172194e1cb10796a1a9bf06fb7b699e22def` |
| `inbox/phone_saber_triage_phonesaber_20260929_185841_367/images/image_01_red_dropout_false_339.png` | `a1d2de7bfc9f1291ecc6ff41aee02f6358f2781fd265ab436a85d893f5f583c4` |
| `inbox/phone_saber_triage_phonesaber_20260929_185841_367/images/image_02_blue_dropout_false_344.png` | `bef63921259795fa011e7285310d5375da4b5dd87384e6dcc15ab8bb25ba5231` |
| `inbox/phone_saber_triage_phonesaber_20260929_190505_735/images/image_01_blue_dropout_false_1259.png` | `baa11102686a4942a7896e4df64e4a38509e4e5f76a22b06cfbdd5535d5ae430` |
| `inbox/phone_saber_triage_phonesaber_20260929_190505_735/images/image_02_blue_dropout_false_1267.png` | `2eaa8401f3ef6178c9b616f3b082f0d36a9bbf270e50a780661300cc797d4a43` |
| `inbox/phone_saber_triage_phonesaber_20260929_191853_743/images/image_01_blue_dropout_false_2997.png` | `a3f0d285072817a6d75a76446ba3a380534b880730170746964d8c22859dbb18` |
| `inbox/phone_saber_triage_phonesaber_20260929_191853_743/images/image_02_blue_dropout_false_3004.png` | `4558add6ec73059f23ce00d44d84197992e743978ae68fec70372f6cd4b617eb` |
| `inbox/phone_saber_triage_phonesaber_20260929_192926_896/images/image_03_frame_1380.png` | `e1c3bd2e52cc93d072bfd9514f1c34d304ffd939c91f7f8fb3a5cf128ceac2a6` |
| `inbox/phone_saber_triage_phonesaber_20260929_192926_896/images/image_01_red_dropout_false_1501.png` | `6fbab8dbeeff70ee3c7e78cdac9c7525667e506da37fab21ec8bdf1ee3e0e024` |
| `inbox/phone_saber_triage_phonesaber_20260929_192926_896/images/image_02_red_dropout_false_1568.png` | `6525b322de8558f7a22a995ee135081e62ac6666fea1ac1978015dc56caee5f7` |
| `inbox/phone_saber_triage_phonesaber_20260929_203439_379/images/image_03_frame_1124.png` | `724e07701477c6759e1c915fa10fba7d3c817afd7b8fe8ec5b8cf8eb3bc0784e` |
| `inbox/phone_saber_triage_phonesaber_20260929_203439_379/images/image_01_red_dropout_false_1178.png` | `8141c9e6083cd4fb6ba5ab989eba235a26513827ec8febfc86c4bc15930ab802` |
| `inbox/phone_saber_triage_phonesaber_20260929_203439_379/images/image_02_red_dropout_false_1358.png` | `5e45db514263cb318bf2c3a9d7c03ab989fe939f6712cae661abaf7418d5a76b` |
| `inbox/phone_saber_triage_phonesaber_20260929_203710_701/images/image_03_frame_5452.png` | `3fdd8b0d8596537932f0daf4f1dacebc68461239d8722bb1309b4220f34c1383` |
| `inbox/phone_saber_triage_phonesaber_20260929_203710_701/images/image_01_red_dropout_false_5455.png` | `8e545cd67fa6b82d6c3d0bf8c036577c7d4999e2884219afd57ff7147e2bc6e3` |
| `inbox/phone_saber_triage_phonesaber_20260929_203710_701/images/image_02_blue_dropout_false_5464.png` | `abaab834241e000e58878664c127f22aad9844f07a4355ca1eb92c541d364570` |
| `inbox/phone_saber_triage_phonesaber_20260929_220028_395/images/image_01_frame_1255.png` | `0eded94cce5f109106b9e0c82b4d69c4b1a048b58c2b326f51460732e79a8561` |
| `inbox/phone_saber_triage_phonesaber_20260930_005824_838/images/image_01_frame_709.png` | `c8a3627f115739aadfcb096269f8b1a17b84b43751efea7a84fb4a472386b98d` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_08_frame_2439.png` | `dcb384aa361e573f25087bfe4954f24f0524e29faaefcd66171cc9f08d53b540` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_04_motion_event_2_event_at_2441.png` | `cf90fdb035e4008d4eef4701e824a0774249d4bb1a768ace8576d63ab517b1a0` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_05_motion_event_2_event_post_2463.png` | `5c89ec3ad8193275eba0a372d660a9927889ceacfc9e00dd75696cc7c818bbe2` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_02_motion_event_1_event_at_2512.png` | `a8b6aa4e85914d87c526c74fa3ec7e6741bac6e6065211ef1a709680e0b746f3` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_01_motion_event_0_event_at_3495.png` | `78112e92867e340b536f9da68e739ea2726a4644d8f7d202cc25b2a0f8c3d062` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_06_red_dropout_false_4015.png` | `80dcac68aef146f5ed80584669c8ec62771e805648f3f8fdb7e8d8d021550300` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_07_red_dropout_false_4021.png` | `8729d789dc52301ff4b967529dfa4b9776880e95c7faff0613b11d96722f3eb3` |
| `inbox/phone_saber_triage_phonesaber_20260930_234740_348/images/image_03_motion_event_1_event_post_4095.png` | `cb1c71a00b0d9b901b6aca5c6388a09f6af20e4f7d71c747edde969d14c1a3db` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_03_motion_event_1_event_pre_69683.png` | `573269fa7dce27ec53ad825648ab46b349443356a31e920d1288e69ddcb037f9` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_10_frame_69684.png` | `9f575aca89b4919ff84a31c20366d8129af2e565ec0ebb0bcfab115a88205c9e` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_08_blue_dropout_false_69743.png` | `4692c48d59cd906f66deac57e875fbd2f5f7d1c80b645017c2bcdfd7ac4086b0` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_09_red_dropout_false_70998.png` | `e877adab87c4643474b667841452d9280be454f592a99688dcce012d3e6b7b24` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_02_motion_event_1_event_at_72881.png` | `483f09d84123b4ec1f0e03d4bee16a57b1db5bd66f05b929a12ae7698b517e03` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_01_motion_event_0_event_at_75152.png` | `eb7e9fa1fc397cbdef5dc0e1f490978923a573658523a486dff765d88843c2fa` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_06_motion_event_381_event_pre_75589.png` | `9abb23838f97c9ca0f375e2dcd7b554e5e3af14fcdeded33b841e5a4b55fbc58` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_05_motion_event_381_event_at_75607.png` | `7f53950131eb759757b5c43ef8c9bad7b8fecbf67d8887ec1c808a5a744b5d77` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_07_motion_event_381_event_post_75625.png` | `5a902251e6bb780c98c9778a4d3c2af3d0588f0d00c7d36b01382d4aff2b73dd` |
| `inbox/phone_saber_triage_phonesaber_20261001_002509_264/images/image_04_motion_event_1_event_post_76899.png` | `14a68ff9b4ef7de07ef13b985fc9579e11b7231d0b12bc85675d69ec8f81fd25` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_09_frame_18124.png` | `c7118aeda048a709cb61704c1f0a24b16b8ea9d60a236e948e32df45c0c2b811` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_04_motion_event_2_event_pre_18140.png` | `bfdf3a21cb826a084a079e23b057ddb09edf9ec86e7dbdb21f8ed52de4ea76fb` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_08_red_dropout_false_18155.png` | `650fdcc45c2da96969378b605e14b5e460d5aaa58cc7fbeec330624d9a35aa91` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_03_motion_event_2_event_at_18215.png` | `191507105ba968dfce7661f41c41dbe701df6e937bb82d3ab564e3a70b6bf991` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_01_motion_event_0_event_at_18246.png` | `1eaa4c87b934b383a4060123fd0b46ebd003d007f54ca25b767d6b5ef017a7a7` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_06_motion_event_166_event_pre_20533.png` | `b10ef661ec1a83a7304231f91f792d99163f38c0c3d88d180750ecdf82256604` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_05_motion_event_166_event_at_20551.png` | `f24ba327b7820145958dc5d4f31b5755f7034200dd43c7e2aadb6488db027d37` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_07_motion_event_166_event_post_20568.png` | `c7cac8ffa1ca4a37232903ce0f444258ae5c0b623ad52bf2447f73b9a5eeff0c` |
| `inbox/phone_saber_triage_phonesaber_20261001_003921_526/images/image_02_motion_event_0_event_post_24174.png` | `f3cbf25e0082810565b788837cbb9285dd1300e3536851a907a70184c2423539` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_10_frame_15608.png` | `2479a93f21b4feed0d653cbfd7742cc37e196f7e095e86809c8b1b4a20f93405` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_02_motion_event_1_event_pre_15623.png` | `8f6741d8aedbb017753a4ed5a3e0ba2fbec0fc7b7f077bd08b1beca9e2d8813a` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_01_motion_event_1_event_at_16171.png` | `1a98bf628c4488cb245119278178db01c8078ef6ef0088d038f8b56aa0040523` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_08_red_dropout_false_17572.png` | `8f1408c77e4352420f8ee4a5ea0c0ec37ee4ae90d0e0b8cff3fdf99cb4941969` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_06_motion_event_138_event_pre_17923.png` | `4a5ae514ea90902dcc81b2ccd79005165e72e4102ff1e9eda5ef6a267b91a2b6` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_05_motion_event_138_event_at_17940.png` | `2a1df5b27ff505dfe53c23e92718803f6da27b2b6edcf3ba53b0ad3966b65397` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_07_motion_event_138_event_post_17997.png` | `bd6f6ae4ec08f18e14f7c24e882efe035bbbba3cc6aeb2e3516745acceabe9d4` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_09_red_dropout_false_18326.png` | `dd7f89edaa786cddbc3b1f703a0ed28f765763552ec5772326d37224cdad7683` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_04_motion_event_0_event_at_21300.png` | `d238c548fa6050f2403b8d70936cf19264bbba14ff783a419503225e381a4f6b` |
| `inbox/phone_saber_triage_phonesaber_20261001_005134_111/images/image_03_motion_event_1_event_post_22875.png` | `4bd6f31498bb70b1db8621e42db17a2eea00f3a998e505fee40edd0b958a232a` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_12_blue_dropout_false_274.png` | `fcf41ca6cb73217b377d28b228d0897c92f606c9c9b06497f1b6e121528884fb` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_01_motion_event_1000000000_before_2533.png` | `1192f7bacd0dd96ec571abf7e0ca99c9cdc3d8e2a6c90f6bc677d57fa54c0781` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_02_motion_event_1000000000_before_2534.png` | `03bd77d4a142942f4d20dcfa51090a1cc80ac4593c72ab70ee523351277861d1` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_03_motion_event_1000000000_before_2535.png` | `5897cda1b6990051d946f1283a865954bd383726282ef38c116b579c44d4d856` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_04_motion_event_1000000000_before_2536.png` | `dd7d6e5bb76ac8372f8154cabdc14eb05b5922436c1f62705f7f0c5776a86889` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_05_motion_event_1000000000_onset_2537.png` | `a3835b4759aaf21f84c7ec74aa2a118b0af1eecb3eedb6353ed92ceacfe64d18` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_06_motion_event_1000000000_peak_2538.png` | `f475935e5286a14a3bf074473cd609b411573d2037eee6813d97e5c8ed3d78ad` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_07_motion_event_1000000000_after_2539.png` | `29ffdacae6a948fb179870da8b567d491e1624837c76e110fc47d46ad3350e58` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_08_motion_event_1000000000_after_2540.png` | `c07f8c690c24aa922d9bd87d05631159ea464a2ac42cd4c19340d44828dac0b8` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_09_motion_event_1000000000_after_2541.png` | `05531256c58ab5b67da03b10cc048cc4a3b124a5b7335ed5b45ff42e24293d57` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_10_motion_event_1000000000_after_2542.png` | `38f740332b2b87860a10001b88e704399ed2d49eefa0b3a18577f8cf69f8ea31` |
| `inbox/phone_saber_triage_phonesaber_20261002_005850_489/images/image_11_motion_event_1000000000_recovery_2543.png` | `533d61a856ed7864a7606ab9338ea2c05817e23f8b1b478bdb6a570648d81a40` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_12_blue_dropout_false_594.png` | `b85bbe469b28a554b6a05339d898c2d30fc1a7ab146ac3e86923c726b3fddbbb` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_01_motion_event_1000000000_before_656.png` | `5a4c0eadfbaff1c4429df7c7468881045db6fa92fe1c12b956ccd9d5d9f8d80d` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_02_motion_event_1000000000_before_657.png` | `f692a96f1d87e75cbba9776229324e6c5aeec1a3080f859905b1ada61e848c8e` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_03_motion_event_1000000000_before_658.png` | `c29fb529b16bf410617e5079e61d46e7f2316f18aa4768a4dca72eea90a5cf15` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_04_motion_event_1000000000_before_659.png` | `47ebb1adec0af748c3ef0a8e9f923ab7b39cba110d984eb7deec6199e2d43ac7` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_05_motion_event_1000000000_onset_660.png` | `67e1b263c92054e0141f01ad6f04f537fca29ff1ceca5356ab815e44531b68fd` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_06_motion_event_1000000000_peak_661.png` | `f1c5268ceed6bd0a8771b6bc2652b13e41c977ff51a622ca94526e58ace438b9` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_07_motion_event_1000000000_after_662.png` | `fea4f3571d48a92c6114c50b65f03c9d6b29e55bf2fc8137711a4a07a561e845` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_08_motion_event_1000000000_after_663.png` | `6cbad6931a106f32e30e09171ecd614a1bdb1153e8d05c8fbc3f3256559165a6` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_09_motion_event_1000000000_after_664.png` | `1d2c1cd232b6d0f03611ab65acf3a3cd983b60fc6ff811c686cb9dfec499593c` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_10_motion_event_1000000000_after_665.png` | `35807bacc11523a92e482c5c87940c6f5f03c65a96de5cb5872a7fdc750ee774` |
| `inbox/phone_saber_triage_phonesaber_20261002_010049_190/images/image_11_motion_event_1000000000_recovery_666.png` | `2d35833dae6324c6b111635af868fcc70d7e5d57a7464810c2e22b56d1838fbf` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_01_motion_event_1000000000_before_251.png` | `8132a15994ac2ab91ad425d81de3e64eccabedddb0ab3eb22bee0c0e3cbe9b12` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_02_motion_event_1000000000_before_252.png` | `02deb51614f69a1bf59d92b7a6005cbb3a113a2e106d54cdd4a43e1d7a271146` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_03_motion_event_1000000000_before_253.png` | `865aa59ebdb245a218e06a0aac0b8255465f06e4c9967eca5297a463f15cf91e` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_04_motion_event_1000000000_before_254.png` | `1e05a10f4c6403ed821f60e6a16e388408e71efd3632689fadf67a112b40c90d` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_05_motion_event_1000000000_onset_255.png` | `5c31a5a834613c9983ee7078440b204746a1e7b9a8a07b45fb10c651e55a2fe4` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_06_motion_event_1000000000_peak_256.png` | `9712bb0a81c96656eec511b7d4d587a5dcb3fbe4db99539e4315443e00711a5d` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_07_motion_event_1000000000_after_257.png` | `911a3316a3e1af30cd079ac7d4b3fad7e22376d213d47c789c1de38288198a92` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_08_motion_event_1000000000_after_258.png` | `21ef7ed5bf236b60769148635f555f408583fdb5aa3a47db6f5eb87ed3e196a8` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_09_motion_event_1000000000_after_259.png` | `baf387001bb7683cd4e09a0937c50ad8e0ccb5198d55d3ffb9078c5efee5f44c` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_10_motion_event_1000000000_after_260.png` | `48425aec422e7fd92d373ee9b4cac53902b8b3f47e418fd2f00956657f261d2c` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_11_motion_event_1000000000_recovery_261.png` | `76551637aba38848874850c063945d1737693189e3077237e81044efc3a4c30d` |
| `inbox/phone_saber_triage_phonesaber_20261002_013205_087/images/image_12_red_dropout_false_282.png` | `37bdd0010114463e902429e859219461731294301368abe7a4c3b87941093432` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_01_bridge_event_1_before_success_1907.png` | `94cb17ef5404cce3a6a4a76684f72456e89b33a6b48343fb522e9aac1be02089` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_02_bridge_event_1_dropout_1908.png` | `63456d288fb6f9ad8da9bcc0544c05c54731a0e6cdb8b4bedebd13cd7d63c438` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_04_bridge_event_1_after_success_1911.png` | `b5ded18945d68f775a26be4ca40f98e50a767df8c9503d05bd1e20521ddee378` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_05_motion_event_1000000000_before_2550.png` | `e5ec058c6db4b26c389879ed0be00dcc7c7054357e6976036f4d889c2cbd5270` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_06_motion_event_1000000000_before_2551.png` | `10c49ba2a6ee83fc15524bc2e2759558173767662da494c95423e918d72ba30f` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_07_motion_event_1000000000_onset_2552.png` | `6b3d474f80e8f0b360d49267aad2f89c28d2d415915adade54e855c758ee4646` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_08_motion_event_1000000000_peak_2553.png` | `cfdd5479e0b6b3da2c834a98101c82bb80b73f35ee4605982b6e246ec2935bd6` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_09_motion_event_1000000000_after_2554.png` | `b1d0630ce12354811e80806b5c99d63a7f3eb8d94d88d515a6d4233362a3a003` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_10_motion_event_1000000000_after_2555.png` | `dc1cf30cf074bc8c957aef7da4968747dc9243f40f481158e7e5e74daa00573f` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_11_motion_event_1000000000_after_2556.png` | `33faf02588952b11960000e1b11ea51cb2f56f7c695df4f40fe46c78855be7ef` |
| `inbox/phone_saber_triage_phonesaber_20261003_144936_295/images/image_12_motion_event_1000000000_after_2557.png` | `0c94daa8069ed1b7ab50caea68ee07dc6b61d13874b028735c4a60297efcc6a7` |
| `inbox/phone_saber_triage_phonesaber_20261003_155608_448/images/image_01_motion_event_1000000000_peak_8.png` | `3d8cca68f830d860e43441263f4b3d189a0265800b5f42f93e4e0926306d964b` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_05_motion_event_1000000000_before_5216.png` | `0a0fe912985b696e997242d3e3537080de4c83ab14dbe73a6ef385b2a64989c1` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_06_motion_event_1000000000_before_5217.png` | `7ad3ce2d6ddca722446369ed2f08ec5c85fc7d6b641c80415c64f2b55ea99d75` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_07_motion_event_1000000000_onset_5218.png` | `8c7e32f67914d9dcd253668de364808dd0988569d6e47bac8d94120ae801fb45` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_08_motion_event_1000000000_peak_5219.png` | `cc7e8221c70908817cb3b5ce94b53de06c6b3d34f90b5a7db8ae4d27deaa5aa9` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_09_motion_event_1000000000_after_5220.png` | `1df0137a2c9ddbe98247f2c1f3674ac06b8ecfa97272a037ed0d269a7b18d4ed` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_10_motion_event_1000000000_after_5221.png` | `9521de773f9b7db904daff90e591e967d035526fa7037a11762b9826a94a9c39` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_11_motion_event_1000000000_after_5222.png` | `29c8a2cef8fd8f0571eb8974021fce2147db7dfdd8e401afa235896ce1b274d9` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_12_motion_event_1000000000_after_5223.png` | `b97d6513eb5b7c4f7b531db3449a6ca914df975296a430c260ac860f7443ba02` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_01_bridge_event_2_before_success_5321.png` | `4409cda5fdd84255202f2c140628c256d708b2a4aa7f4cb2ed8dad677f8e1b7d` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_02_bridge_event_2_dropout_5322.png` | `a91675a4867dae172e187210c2289e2395c67e24fea7124c5f40dcd7193d9b37` |
| `inbox/phone_saber_triage_phonesaber_20261003_155919_297/images/image_04_bridge_event_2_after_success_5331.png` | `5cc5e997c52eb8f4e7c29fcac41c1925881f4dbb9acd63357e5a6ab4078c21de` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_05_motion_event_1000000000_before_198.png` | `a4938956f44364b2832c1f3fe5283140dae4a83763589ea152faccae7ad13752` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_06_motion_event_1000000000_before_199.png` | `1849a88563753f196bc3c37c801d5a58b4f356452ab3576efbfde399d893ae46` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_07_motion_event_1000000000_onset_200.png` | `44fdc4e72e531f9535de78c0886c833fc646e565c9c18338aae951f23633888d` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_08_motion_event_1000000000_peak_201.png` | `d468132f6b0a8a942a0f0657954f3f52892d3fb51571194bac5b35b5bbb8bfa2` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_09_motion_event_1000000000_after_202.png` | `90310109fcf894c768b5e34db66a75630ea011df8227c258a2ac10c990d20eec` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_10_motion_event_1000000000_after_203.png` | `6db61ada850525b7eae45862662740f94aa6d61b5e677d2e97959d2d94728b1d` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_11_motion_event_1000000000_after_204.png` | `8ec8885809b86217920a8195e42ce275cd3d453f3f2fb107828939bdfb062010` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_12_motion_event_1000000000_after_205.png` | `f1ebf0e1b9868abef09bf3f4fddc2aadb1405ab08c06100c8d09866341997b76` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_01_bridge_event_2_before_success_3302.png` | `61d2a049843445984ebd9b4dda197fcf4e3272051fd1418bdbb6da718b827709` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_02_bridge_event_2_dropout_3303.png` | `8ff0774bd7afe4ac34994dca804fde93ac0bd0fcf7bb0faa996a91b7706986fd` |
| `inbox/phone_saber_triage_phonesaber_20261004_005325_638/images/image_04_bridge_event_2_after_success_3306.png` | `9a87d9c0754a88ae23292a84c7d22fe48a9dd7b42dbeab07566ca6718c5df82d` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_01_bridge_event_5_before_success_12548.png` | `a6f93c893f7a42768164aeb490a56fb549c15980420a786c61be3f924850fa9a` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_02_bridge_event_5_dropout_12549.png` | `f370a07c46746c53134480f931cc60cb3540f2296b6949d5b3c53ac15f41c62b` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_04_bridge_event_5_after_success_12585.png` | `05154606a48b26ec9e103afb555fc15f11ddf95f198ce978e333d0e8694d30dd` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_05_motion_event_1000000000_before_12626.png` | `011ae7c469e54718785de0c5bd751b77d26cab6a817918a9e2f8a93712688d2e` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_06_motion_event_1000000000_before_12627.png` | `f384ce53d3412ae14e9fd7e3cfd675f1f239652cdf93f8931871c6383523fcd7` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_07_motion_event_1000000000_onset_12628.png` | `325171dbb01255c950422f2644f2a7166b2af7467304155599556bd59c28d5b1` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_08_motion_event_1000000000_peak_12629.png` | `90e3d23f3b44a137ce4a32f970653dbf65e1e197639b7f676d3931e1e9abc11c` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_09_motion_event_1000000000_after_12630.png` | `bfa408242960dc248477367459404d7c8212d4231de39175965b83e85713bd8f` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_10_motion_event_1000000000_after_12631.png` | `cfd3499782425bb38431dbd7aeba64448e66c6e7b79e29b2ba5f8a2e14c32b0c` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_11_motion_event_1000000000_after_12632.png` | `2469777235df5ba6f8c9630f1cefb8d97b3888eb0d438e3731c853b1fea4f3c0` |
| `inbox/phone_saber_triage_phonesaber_20261004_005855_691/images/image_12_motion_event_1000000000_after_12633.png` | `637cd43c53bb2975dd859ee0b165128a06d3e4e1bd6592e035809184661d3acd` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_05_motion_event_1000000000_before_1248.png` | `0d3449b35ff06a95b2c65a4b5d8e1de9cdb25cb092a7d485efeeece83d2046fa` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_06_motion_event_1000000000_before_1249.png` | `15c483c3d5c9db74a7d92322f75f5bc7e3dc41120b5efbc168aed2576103d8be` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_07_motion_event_1000000000_onset_1250.png` | `551b4c9e8b94454f043eeb2425989bd2f2881c7b08e03e97b6a1de6f3bfd318a` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_08_motion_event_1000000000_peak_1251.png` | `526029b375efa1243c2120569be33e662ebfd62138b7420284b57f7d17a56f85` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_09_motion_event_1000000000_after_1252.png` | `9ae3c16d90379ef066d1755b9d87f58bce22ad2a3f836cbb17b4b1b76630b9cd` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_10_motion_event_1000000000_after_1253.png` | `35acd4aeec2900063aceebaa48edd7f82fb04615687d0151b57085827835c3e2` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_11_motion_event_1000000000_after_1254.png` | `cf521e6d16093898526138109f4c9ccd7c480c99202ea6396333160df5386b7c` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_12_motion_event_1000000000_after_1255.png` | `6393a71c0c57c4d6fee56f8f5dcef855a575ef13d7e510ef9312876b864d2efb` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_01_manual_frame_3409.png` | `e439e8b360628ecc3e6a582e3a2f3de5aaa0b18aba0dea7496ffdfb4e6807c9b` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_02_manual_frame_3888.png` | `ea94d25608c91d11644d06e5a4f1814f49fe36ff062dc71b1ba201cb1c5b0478` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_03_manual_frame_5449.png` | `4d2d4f28539e83f088af0331dea873ab68bc1e1566e2c2cb30c30371aac9bc30` |
| `inbox/phone_saber_triage_phonesaber_20261004_232850_471/images/image_04_manual_frame_6288.png` | `6b87cc84f6b3cc7b20e5bb298c30b220dd31995dc809b6d200cc066eaf382e3c` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/blue-led-with-left-curtain-reflection-03.png` | `8fa4e16f33eed9fa85a59f62c590892a401e824d525be72c64f0bb76868accdc` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/blue-led-with-left-curtain-reflection-04.png` | `279c929f404c7fcb3f9ffabf32bf539269dc947ec6f932fdb41fc8720137f2a8` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/blue-led-bright-large-05.png` | `1ed82a25a5d7361cad0cb7e572779acfbf26e573fd527a9f5b302a36e07908e2` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260922_101530_202/frame_390.png` | `c4a76403f416981fb84df300b9dae428c497ad2a769de1178cc816e85a4f3729` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_113614_837/frame_397.png` | `6819af101fc615f6abd05a4f596f3fc46abecfe7fd8c70c5832c27c10e8dfcff` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_091430_020/blue_dropout_false_1249.png` | `21bb564541a5854a22f2776f4807a3be23cd00e1b59fcd8353a1af3baf3c3dee` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/production-video-IMG_5933/frame-0220.png` | `b35d9b6045c64dc02677ebd610d284d97556b91764138dfc2df5ef940d9879fb` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/production-video-IMG_5933/frame-0600.png` | `bb4583b4b909e9980e27c6d4ac6ad4bc179f0ad48fe101f1a737235346dcd7ad` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/blue-led-with-curtain-reflection-01.png` | `888f846de44be2ff456f6a15188335617f100da78bcd1616bf49eeb6545c1c4d` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/blue-led-with-curtain-reflection-02.png` | `ba52eae443fad3c65ec3182771c77cb1a154915278e256d142118515a403299d` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_251.png` | `1dac776e018d1b9f34a7e6f4224f51f593418985ae63726525f86351194eed92` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_254.png` | `cefdfbd2a1707d2b20916aa18984e919833b48a4655e9831ba8a2db77d51ab5b` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_256.png` | `2e138d6c8b8eca2b49c6134111f04e3745e057e7d0888d401cbd74a8d0f67281` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/blue_dropout_false_361.png` | `204b3f035543306e73d4b1f202bf3b6244f8cd83a60ff0d6c8fb7332b5e4ad18` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260922_134817_916/frame_103.png` | `2f61f9449fff688434c35b06f804876670f736549e618c18c56a1ca0becab06c` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260922_134817_916/frame_131.png` | `78ee533b5d477dfa29318da880d40ca4619d6e44c616d901b9178abf706ee35d` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260922_134817_916/frame_136.png` | `e1d1d279096dcb55ad0844991f54aad850a419d7931b36edef066a75b744c7cf` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260922_134817_916/frame_139.png` | `15d22b694c0128856c542ea15c5332c57df5791d703fd8e806d1dea5dc36cf3c` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_59.png` | `74a3531b2f427b3e07504aaaca86ff36ab4aac3e099ee0868ef02ac3ceeee275` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_62.png` | `5c66b7e688a5e8d3dbf5ccb370b8aadf4124e7d69d2460d311624bda230f2ff3` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_63.png` | `5b630dcacb7aee43eb24026fd342221725513e453c0ccf76844320023e45b4a1` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_64.png` | `cd0d4cc440684b9f4ee1454325aefd3e006e73afda1e8431e245e3eb93ffb384` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/frame_65.png` | `dd150a31b30dd336e961cc37b43abb0f3e261af0221b51e799bf381883173c7c` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_false_83.png` | `5ff9dbaddccaff8dfa9d1fdf62830e85c2bacbdbb5456a626aa6cee0a1953fe7` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_false_90.png` | `67f21cb35fd247125c7c3ad07203dca302f842c02e4e9dc121691457c1e9f013` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_false_348.png` | `c69a828af90b3b84bebb5183f00f1ee5492a3162b3c951b1d551c333c6c3403d` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_82.png` | `249260b95e1ed589669515e67484f9e1a2189502594d5f7ab375295d2a407bbf` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_89.png` | `be223939a7dbda7d88bd70a54cb3a50b1f75f175aef26c89bb896f3ef61930ac` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_recovered_84.png` | `1f15edb8e702d322229f164746bae182d8bdc45baf6e1ca57f21ce5ba7e1c299` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_recovered_112.png` | `7bc7f49a7c41f9ee81f1954024d02bdba08adb8161317e703080caa683d8709f` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260923_143446_247/red_dropout_recovered_351.png` | `a5384da5b0ebdb29948d4448369d042d50d226c7d4df53042d975606c6c08f4a` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/forensic-20260921/frame_1000.png` | `e35b5a084eef73ff58d2c66f89eb924b162475e68a2ba24c8a15c160f94a8ba5` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/forensic-20260921/frame_1048.png` | `26a4aa281e552c4ab2a7bd37d438622ad4c460d12c4fbe9ba06822863c0062ed` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/forensic-20260921-211845/frame_919.png` | `39821cf667a39217df942f2d68694f61b3548bb4d66c503121a9e3e3c77aef86` |
| `repo/ios/PhoneSaberSenderTests/Fixtures/forensic-20260921-211845/frame_1091.png` | `d0a2cd326939f58d9f8583e749deb0e405fb6c3f9c3d50252f6038c1176b972b` |

</details>

入力定義と検出器のSHA-256:

| path | sha256 |
| --- | --- |
| `docs/claude/analysis/2026-10-03_background_fp_labels.csv` | `d09bb2a62d04aa06fd2acaf4b160772270ff197aed98f8ad2d2de8623f0bbc48` |
| `ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift` | `bb60a498bfffb71f613be3078aa1eb3a74092465711fd33a23a58aaf7b139a45` |
| `ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift` | `21dbc5acee5051c3b47546d6799ee24053ba87939c4c659801832cab54698676` |
| `ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift` | `5119b1a104b355f2ad7eb501365bd549ef16bd462f56abaeae992a47d9776623` |
| `ios/PhoneSaberSender/Tools/lossless_regression_manifest.json` | `fb26d43e5d39f2f0966dcb0c6941fe7c5282e990f2924c16c47b2141c37ea6a7` |
| `ios/PhoneSaberSender/Tools/background_negative_benchmark.json` | `51a3899c97b302802623117bf388702ad74c705194a7065e1eb3d4c7e98fa5e9` |

## 推奨: 実機で先に記録するshadow rule（適用しない）

**定義:** 最終順位済みREDの各eligible候補について、production sample点のうちRED `colorMask!=0` の画素集合Cで `h=mean(signed HSV hue)` を求める（hue>180°なら360°を引く）。`core=既存 coreSupportRatio`（coreMask点数／候補全点数）。**`shadowReject = (h > 11.0°) AND (core < 0.28)`**。clippedWhiteによる免除なし。診断でのみ記録し、production eligibility・順位・座標を変更しない。仮想再選択を評価するなら元eligible順位の最初の非却下候補を使う。このshadowは検出器内部のeligibility箇所で適用する実験とは同値でない。

**根拠:** 元画像formal 40/40、±8%で既存の正しい剣winner147/147保持、232850の消灯FP9→4。ただし全体分離marginなし、absolute recallは100%でなく、+8%では消灯9/9 FPが残るため、本番昇格の根拠にはならない。色相だけの厳格化／clippedWhite無条件免除も支持しない。

**確認capture:** 認識を変えずにh、core、G/R・B/R、V p90、clippedWhite、仮想残存候補とgapを全候補で記録する。別sessionで「両剣OFF→REDのみ静止・低速・速振り→BLUEのみ→両剣ON」を各20–30秒、顔・掌・裸足／足指が単独・剣の隣に入る条件、天井照明が入る角度で行う。暖色照明・昼光・青剣の光が肌に当たる条件、近距離／遠距離、1/100と自動露出を比較し、複数の人と別の場所も含める。赤ラベル等を設置する必要はない。新captureを未使用の検証集合としてORIGINAL losslessの物体位置を目視ラベルし、明るさ・AWB・距離別に真の赤剣の却下0とFP低下、h=11/core=0.28境界の余白を確認できたときだけ再検討する。
