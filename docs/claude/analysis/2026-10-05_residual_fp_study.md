# warmNoDeepRed後の残存FP offline study（2026-10-05）

**推奨: production昇格できる規則はなし。次はBLUE深青支持のshadow観測と淡い端片の真値確認。REDの背景物／布を安全に分離する規則は今回の探索ではなし。** REDの白core＋色halo不足はfixture 89だけを実剣へ戻す補助案。production Swift・threshold・fixture期待値・既存label CSVは未変更、commitなし、private PNGのrepoへのコピーなし。

## 入力と全件目視分類

HEAD `8363024`。前studyの198 ORIGINAL（CSV151枚/25 session＋232850追加12枚＋formal35枚/40 fixture）。`phone_saber_skin_study.py`の一時Swift instrumentation、`phone_saber_rule_study.py`のlabels/gain/評価、`phone_saber_deep_red_study.py`のsample→原画素集合方式を再利用。gain 0.92/1.00/1.08の**594 replayで未改変production harnessとのRED/BLUE出力・候補全field一致**。新toolはeligible候補だけの追加特徴を計算する。gainは8bit共通乗算＋丸め/clippingであり、AWB/実露出の再現保証ではない。

原画像上の全残存winnerと可視frameの出力を目視確認した。**全件のsession/frame/bbox/endpoint/物体/特徴/原PNG SHA-256/仮想再選択は [inventory CSV](2026-10-05_residual_fp_inventory.csv)**（96誤出力＋下記ラベル差異1件、gain=1）。集計単位はframe×colourの出力1件、候補数ではない。unknown（RED5/BLUE7枚）は消灯FPと断定しない。未知のBLUE淡い端片を除外した保持率には下記の制約がある。

| ORIGINALのclass | RED absent | RED visible誤出力 | BLUE absent | BLUE visible誤出力 | 計 |
| --- | ---: | ---: | ---: | ---: | ---: |
| 白/neutral照明・窓 | 0 | 1 | 8 | 4 | 13 |
| warm条件に入らない肌 | 4 | 0 | 0 | 0 | 4 |
| screen/monitor | 1 | 1 | 4 | 0 | 6 |
| clothing/fabric | 11 | 0 | 5 | 4 | 20 |
| reflection（金属椅子） | 0 | 0 | 3 | 0 | 3 |
| blue-lit object（足指・ぼけたkeyboard） | 0 | 0 | 5 | 0 | 5 |
| other | 39 | 2 | 4 | 0 | 45 |
| **計** | **55** | **4** | **29** | **8** | **96** |

BLUE absent29=既存CSV28＋formal `frame_1000` 1。旧RED専用追加truthをBLUEへ流用すると232850/6288もFP扱いになり、labels+追加だけで29となるが、ORIGINALには青剣が明瞭（box 285,435–390,485）。独立BLUE truthで実剣として3 gain保持対象に加えた。既存CSV/RED truthを変更していない。
`other`は過去画像の赤label strip、赤carabiner、印刷包装、BLUEがRED剣へ出る2件。これらをユーザーの現在の部屋にあるとは仮定しない。布は物体種別で優先分類し、青いspillが当たっていてもclothへ計上。反射のない青照明の足指はblue-lit objectへ分類した。
RED visibleの20260930_234740_348/2439（CSV id12）は実剣と壁を混ぜた長いraw tail（344,482–420,182）で、純粋な背景winnerではなくgeometry誤出力。全件一覧と集計に残し、物体ゲートの成功例とは扱わない。残るRED visibleはbackground label/monitor/fixture89窓。BLUE visibleは吊り衣類3＋shirt spill1＋窓4。

## 画素・特徴定義（bbox塗りつぶしなし）

原sample座標×2の周囲1px正方形の和集合をDとする（原解像度、重複除去、端clip）。ringは`dilate(radius=2/4/8) \ D`。fractionの分母は各集合の全画素。色画素だけ、bbox全域、resize画像を分母にしない。
`deepRED: R≥180, 100G<40R, 100B<65R`、`deepBLUE: B≥180, 100R<40B, 100G<65B`。CSVのdeepCountは色別D内count。REDのwarmFracは**現production**の`R≥120, .45≤G/R≤.92, B/G≤.95`（integer比較）で、productionのdeepCount/warmFracと一致を確認。BLUE warmFracは未定義としてCSV空欄。
median G/R・B/GはD内の明るい画素（RED R≥120 / BLUE B≥120）；それぞれR=0/G=0を除外、未定義は空欄。clippedWhiteとemitterScoreはSwift診断の値。別列clippedFracはD内`min(R,G,B)≥245`。area=point_count×4、aspect=bbox長辺/短辺、thicknessProxy=4×axial_density（0ならpoint_count/max(raw_pca_span,2)）。厚みは既存skin tool由来のproxyで実測幅ではない。
neutralFracはD内`max≥180 AND min≥.85max`。ringの色haloはRED `R≥120,G/R<.65,B/R<.80` / BLUE `B≥120,R/B<.65,G/B<.80`。coolFracはD内`B≥120,.45≤R/B≤.92,.65≤G/B≤.98,R≤G`。clipped/neutralだけの無条件免除なし。

## 最大2classの分離探索

正例は全gainの**確定ラベル上で現在正しい同一winner** RED149/BLUE187出力。元画像のother/fabric負例と単特徴（RED11/BLUE10: deep count/fraction、warm、neutral、clipped、cool、area、aspect、thickness、emitter、production clippedWhite）を両方向で比較し、全classを分離する正のgapなし。零支持の本物や短い本物を除外しなかった。

| 特徴 | 現在正しいRED（全gain） | RED other（元gain） | RED fabric（元gain） |
| --- | --- | --- | --- |
| deepCount | 0–2297 | 2–204 | 14–251 |
| deepFrac | 0–.869 | .005–.479 | .057–.677 |
| area | 48–5336 | 48–17788 | 164–328 |
| thicknessProxy | 2.18–47.02 | 2.86–78.37 | 5.78–8.88 |
| emitterScore | .452–.937 | .456–.857 | .429–.607 |

compact gate `area≤A AND thickness≤T AND clippedWhite≤C`も、A={48,80,120,160,240,320,480,640,960}、T={2,3,4,6,8,10,15,20}、C={0,.01,.05,.1,.2}の360組/色を探索。全正しい出力保持はRED45組、BLUE262組；other/fabricの元winner却下は最大RED0/52、BLUE2/13。全class分離なし。最良compact案も同一winner全保持・formal40/40だが、最大class対策にはならない。BLUE fabricの深青支持は最大5731、全gain背景winner最大10778で本物の最小14と重なる。青照明の衣服・足指・画面や逆色剣には色支持だけで対処できない。

## 白core/haloとBLUE深支持のshadow比較

**最終順位済みeligible候補だけをfilterし、元順位で最初の残存候補を仮想出力する。** 内部eligibility変更や実機性能改善と同値ではない。
白core探索は色×{neutralFrac,clippedFrac}×core={.3,.4,.5,.6,.7,.8,.9}×halo={0,.01,.03,.05,.1,.2}×radius={2,4,8}の504組（同一winner保持418組）。閾値を下げたRED案は全gainで最大6背景winnerを落とすが、class分離ではなく露出・領域生成依存。保守的な比較案Wは**RED neutralFrac≥.80 AND ring4HaloFrac≤.05でreject**。
BLUE案Dは**deepBLUE count≥1でaccept**。count={1,5,10,14,15,20}、fraction={.001,.002,.004,.005,.01}、`zeroDeep AND coolFrac≥{.3,.5,.7,.8,.85,.9}`を比較した。

| 案（元gain） | 同一正しいwinner保持 全gain | RED absent | BLUE absent | RED/BLUE visible誤出力 | formal | 89実剣へ |
| --- | --- | --- | --- | --- | --- | --- |
| production | 149/149・187/187 | 55 | 29 | 4/8 | 40/40 | いいえ |
| D count≥1 | 149/149・187/187 | 55 | 18 | 4/7 | 40/40 | いいえ |
| D count≥10 | 149/149・187/187 | 55 | 17 | 4/7 | 40/40 | いいえ |
| D count≥15 | 149/149・185/187 | 55 | 17 | 4/7 | 40/40 | いいえ |
| D fraction≥.004 | 149/149・187/187 | 55 | 18 | 4/7 | 40/40 | いいえ |
| zeroDeep & cool≥.3 reject | 149/149・187/187 | 55 | 18 | 4/7 | 40/40 | いいえ |
| zeroDeep & cool≥.85 reject | 149/149・187/187 | 55 | 26 | 4/7 | 40/40 | いいえ |
| zeroDeep & cool≥.90 reject | 149/149・187/187 | 55 | 28 | 4/8 | 40/40 | いいえ |
| Wのみ | 149/149・187/187 | 55 | 29 | 3/8 | 39/40* | はい |
| **W＋D count≥1** | **149/149・187/187** | **55** | **18** | **3/7** | **39/40*** | **はい** |

*Wで不一致になるのは89の旧窓期待値だけ。**他39件は39/39**、期待値は一切変更せず。89の再選択は**(442,620)–(450,640)**の実剣、全3 gainで同座標。Dだけは89のRED窓を変えず40/40。W/Dによるnominal絶対正しい出力はRED50/67→51/67、BLUE65/74→66/74（labelled＋formal独立物体box）；現在winner保持と全frame recallを混同しない。

| gain | W＋D 同一winner RED/BLUE | absent RED base→after | absent BLUE base→after | visible誤出力 RED/BLUE base→after | formal base→after（89除外） |
| --- | --- | --- | --- | --- | --- |
| .92 | 50/50・62/62 | 27→27 | 31→19 | 2→2 / 9→9 | 17→17（17→17/39） |
| 1.00 | 50/50・65/65 | 55→55 | 29→18 | 4→3 / 8→7 | 40→39（39→39/39） |
| 1.08 | 49/49・60/60 | 78→78 | 34→20 | 11→11 / 14→11 | 13→15（13→15/39） |

formalのgain≠1は元のtype/endpoint期待値を固定した厳格比較。production自体が17/40、13/40になるため、shadowの不利益と混同しない。全正しいwinnerのendpointも同一保持。
Dは元gainのneutral光/窓winner12/12、金属reflection3/3をrejectするが、再選択後は窓7/12のみ無出力、残りは青spillを含む別candidateに戻る。monitor2件も別FPへ戻る。衣類誤出力1件（002509/70998）は実BLUEへ回復；他の衣類・足指・画面は深青支持あり。背景winnerだけの却下数を最終FP低下と取り違えない。

## 余白・限界・次の記録

Dの現在正しいBLUEは全gainでdeep count **14–6340**、fraction **.004401–.955862**。最小count14は005134/17940と144936/1907のgain .92（後者は最小fractionも該当）。N=1保持余白13画素、zero-deep負例への却下余白1画素；N=14は保持余白0。fraction=.004は保持余白.000401（0.0401ポイント）しかない。元gainのneutral光/窓12件は支持0で正例とのcount gap14だが、**全背景vs正例gapは−10764**。全class共通の分離marginではない。
Wの正しいRED neutralFrac最大.609756、core=.8への余白.190244。89窓はneutral=.910798、ring4 halo=0で余白.110798/.05。この窓1例の分離を一般的な照明保証に拡張しない。BLUE neutralFracは窓.025–.923、実剣0–.558で単独分離できず、clipped白coreだけでも解決しない。
**厳格な全実剣保持を未確認とする反例候補:** 既存unknownの155919/5321・5331はORIGINAL右上に淡いBLUE端片らしい物がある。D/cool案は以下の元出力を落とす。点灯状態と剣本体の支持を確定できないのでFP成功にも実剣成功にも数えない。**これらが実剣なら最小支持は0、正のN/fによる全実剣保持は成立しない。**

| session 20261003_155919_297 / frame | gain | 原出力bbox | deepCount / coolFrac | D後 |
| --- | --- | --- | --- | --- |
| 5321 | .92 | 452,4–472,16 | 0 / .634 | 無出力 |
| 5321 | 1.00 | 448,4–472,16 | 0 / .655 | 無出力 |
| 5331 | 1.00 | 446,4–472,20 | 0 / .742 | 無出力 |

BLUE unknown元出力11件中Dで同一保持7件。上の3件に加え、002509/69743のgain1.08は吊り衣類から別候補へ変わる（点灯真値は保留）。cool≥.85/.90案はunknown11/11も保持するが、元gainの消灯FP低下は29→26/28に留まる。.85へのzero-deep unknown余白は.004289（0.43ポイント；69743/+8%のcool=.845711）と小さいため、追加の安全な分離根拠とはしない。
全gainのeligible実剣proposalには深支持0がRED10/417、BLUE16/977残る。Dは現在正しいwinner187件を保つ条件であり、全実剣proposal保持や未観測の淡い剣への保証ではない。混合proposal・反射・床/布spill、AWB、低露出、遠距離、motion blur、frame端で余白が消える可能性がある。隣接frame・gain・候補を独立sessionの証拠と数えない。
**Dはshadow観測候補に留め、端片の点灯真値/保持が確認できるまでproduction適用しない。Wは89の物体真値修正を伴う別検討案。** 認識を変えず、候補ごとのdeepBLUE count/fraction、neutral/core/ring、coolFrac、仮想winnerを記録する。別session/別場所で両剣OFF→REDのみ→BLUEのみ→両剣ON、昼光窓・天井照明・screen・通常の衣服・肌に青spill、淡い青diffuser/遠距離/速振り/端を含む未使用losslessを追加する。赤label/carabinを配置する必要はない。最大RED classには発光haloの空間的分布、点LED列、時間的な剣点灯/OFF対を比較するデータが不足している。

## 再現と検証

追加tool: `ios/PhoneSaberSender/Tools/phone_saber_residual_fp_study.py`。raw export: `python3 -B ios/PhoneSaberSender/Tools/phone_saber_residual_fp_study.py --json /private/tmp/phonesaber-residual-current.json`。最終gate再評価は`--summarize-from`（既定W＋D）、grid再探索は`--summarize-from`＋`--explore`。任意gateは`--rule-json`（W＋D ruleは`{"blue_deep_count":1,"white":{"red":{"metric":"neutral_fraction","core":0.8,"halo":0.05,"radius":4}}}`）。JSONと目視用contact sheet/cropはrepo外だけに保存。CSVは本文用の数値一覧でPNGを含まない。
検証: `python3 -B -m unittest discover -s ios/PhoneSaberSender/Tools -p 'test_phone_saber_skin_study.py'` **27 tests PASS**、`git diff --check` PASS。新規testはBLUE境界・現warm B/G=.95境界・ring集合・色別再選択/不変性・6288独立truth・unknown出力の損失を別集計することを確認。元gainformal baseline40/40、D40/40、W＋D39/39＋89実剣。

## 2026-10-05 Claude の判断:W は production に入れない
W を実装して公式 verify を回したところ、合成の淡いピンクの剣(RGB 250,215,218、min/max 0.86)が「白い光」として外れた。
fixture 89 の窓の画素は min/max の中央値 0.94・p10 0.87、青い照明下の淡い剣(formal 82/84)は芯付近の画素の p90 が 0.87–0.90 で、差が小さい。
W の効果は fixture 89 の1枚だけで、淡い剣を失う危険に見合わないため採用しない。fixture 89 の期待値は窓のまま残す(直すと公式 gate が 39/40 になる)。
