# PhoneSaber 調査結果のまとめ

2026-10-06 のrepo統合: Git/Unity root は `3D-Saber/`、ツールは `PhoneSaber/`。Git root から `bash PhoneSaber/tools/verify_phone_saber.sh` を実行。旧 school-festival は履歴を保って統合・アーカイブ済み。

2026-10-06 時点。残作業は [STATUS.md](STATUS.md)、当日の手順は [EVENT_DAY_RUNBOOK.md](EVENT_DAY_RUNBOOK.md)。
旧 `analysis/` の個別メモと未適用 diff は統合して削除した。原文・試作差分は Git 履歴に残る。
R7e/PF22 の shadow 記録と一回限りの調査スクリプトは、当時の比較に使った。以下はその結果を保存したもの。
ユーザーの現在の部屋には赤い物がない。過去画像のラベル・カラビナの記述は、現在の撮影条件ではない。

## 証拠と保存データ

- [ジャンプ一覧](data/2026-10-02_jump_events.csv): 20 bundle の横断解析で抽出した 17 event。分類は当時のもの。
- [物体ラベルとルール比較](data/2026-10-03_background_fp_labels.csv): 25 session、151 frame × 2 色 = 302 行。10-04 の 22 frame 追記を含む。
- [残存誤検出の一覧](data/2026-10-05_residual_fp_inventory.csv): 96 誤出力＋ラベル差異 1 件。物体、端点、特徴、元 PNG の SHA-256、仮想再選択を保持。
- 10-05 の調査は上記 151 枚＋232850 の 12 枚＋formal の 35 枚 = 198 ORIGINAL。明るさ 0.92/1.00/1.08 の 594 replay を比較した。
- formal lossless は 35 画像・40 色別 fixture。背景 negative benchmark の 8 枚はラベル CSV と重複するため、独立標本として足さない。
- 正解は ORIGINAL PNG の目視。annotated や CASE hint は正解ではない。unknown、消灯、画面外を取りこぼしと断定しない。
- 隣接 frame、複数候補、gain 複製は独立 session ではない。gain は 8bit の乗算・丸め・clipping で、実機の AE/AWB を保証しない。
- private PNG と詳細 JSON は repo に置かない。旧メモの全 198 枚の path/SHA 一覧は Git 履歴、現在の誤出力の SHA は inventory CSV に残る。

## 不安定さの原因

### 背景・肌・照明の誤検出: 確度 高

- 初期の代表例 005850 f2537（約 422px）、013205 f255（約 430px）、010049 f660–664 には点灯した赤剣が映っていなかった。
  背景同士のすり替えを、本物の剣の CASE A と数えていた。時間的に固定しても、誤った出力が安定するだけ。
- 赤の value は `max(R,G,B)`、つまり多くの場合 R。反射物も R が飽和すれば LED の証拠を満たす。
  過去のラベル・カラビナは emitterScore 0.68–0.73 で閾値 0.42 を通った。白い芯は必須ではなく、短い候補では芯の score 寄与も小さい。
- ラベルは実剣がある 14 frame 中 13 frame でも eligible。端から見た剣 40.2 点に背景 55.1 点が勝つ例があった。
- 初期の赤 LED 対背景では mean value の AUC 0.92 が最良だったが、遠い昼光下の剣 `device_normal_red_390` と重なる。
  mean≥241 または density≥1.9 は背景 30/35 を落としたが、余裕は 1 value / 0.03 density しかなく採用しなかった。
- 肌・光との比較でも V p90 の AUC .993、色相 .935、G/R .934 と高い一方、全特徴の分離余白は負。G/R≤.50 の一律条件は formal 31/40。
  暖色の実剣と白飛びした肌、青光下の肌と淡い剣が重なるので、AUC の高さだけで閾値を採用しない。
- 232850（ガイド付き、1/100 秒）では saberなしでも赤 598/598、青 6/598。赤 winner の正体は顔・足の肌と天井照明だった。
  青だけの区間でも赤 115/115、158/240。肌は本番でも画角に入るので、赤い物を片付けるだけでは解決しない。
- 手持ちの 155919 f5216–5223 は剣なし。青はシート・包装の白飛び・PC 画面、赤は指や肌を選んだ。
  固定カメラとレンズを覆わない運用は有効だが、動く肌を静的な背景マスクで消すことはできない。

### モーションブラーと候補消失: 確度 高（代表例 1 件）

- 144936 f2552 は実剣がカメラ方向を向く速い振り。1/50 秒で半透明の円盤になり、本体＋前腕の emitterScore は 0.305 < 0.42。
  小さい芯も purity 0.39 < compactRed の 0.50 で落ち、壁コンセントのピンクのラベルが勝った。ズボンという初期判定は訂正した。
- これは正しい候補が eligible に残る CASE A ではなく CASE B。前腕とつながった大きい mask を受理すると、誤った軸を出す。
- 1/50 秒で先端のブレは約 85px。1/100–1/120 なら約 35–42px と推定したが、同じ振りの実機比較は未了。
- 005325（自動）と 005855（1/100）は、保存画像が準備・停止に偏り、速い振りを比較できなかった。
  点灯赤剣は 2 frame のみ。1/100 でも emitterScore 0.80–0.86 と十分明るかったが、ISO が上がり背景誤検出は減らなかった。
- 232850 の swing lossless 4 枚は正しい剣を選び CASE A/B/C なし。ただし 1/100 でも先端の残像・二重輪郭があった。
  露出の既定は自動を維持。短い露出はブレ、暗い剣の取りこぼし、照明の縞を実機で比較して判断する。

### AWDL の通信詰まり: 確度 高（改善効果は未確認）

- 学校 Wi-Fi と AWDL を併用すると、約 32 packet/秒でも `maxGapMs` は 170–300ms、ときに約 1 秒。改善候補メモには最大約 1.8 秒の記録もある。
- 当時の iPhone 認識時間中央値は 28.5ms と変わらず、認識の揺れとは別の遅延問題だった。
- `serviceClass = .interactiveVoice` は OS への優先度ヒント。追加済みだが、RTT・到着間隔・画面から受信までの実測で効果を確認する必要がある。
- 同じ Wi-Fi の LAN は比較・退避経路。USB/usbmuxd は未実装の選択肢で、安定性を実測するまで解決を保証しない。
  FPS や Mac 取得後の縮小だけを、無線遅延の改善とは扱わない。

### score の非決定性・端点の崩れ

- core-line の score が Swift Set の反復順で最大 4.3 点揺れ、eligibility の 0.30 をまたいだ。`bbc1f32` で修正済み。
  既存記録で winner が変わった証拠はなく、体感の揺れへの寄与は未確定。
- 010049 f660 は eligible 1→1 でも約 437px 跳んだ。f665 は raw PCA span 218px / robust 52px。背景物の CASE C であり、実剣の修正 B を正当化する証拠ではない。
- 005325 f205 の画面端の青は約 110px はみ出す出力があり C 寄りだが保留。
  234740 f2439 は剣と壁が混ざった raw tail。物体 filter だけで直った成功例に数えない。

## 採用した変更

| 変更・commit | 内容・検証 | 残る確認 |
|---|---|---|
| 決定性 `bbc1f32`、parity `1ad12f9` | 点の順序を固定。144 画像で winner/eligibility/端点不変、formal 40/40。診断 on/off は bit 一致。 | Set/Dictionary の反復順を計算に持ち込まない。 |
| 赤 warmNoDeepRed `8363024` | ユーザー承認済み。既存の eligible・順位付けの後で暖色候補を外し、次の eligible を選ぶ。公式 verify 全 PASS、formal 40/40。 | 実機再試験。 |
| P2P `09ffdcd`、診断 relay `152397c` | LAN fallback を維持した追加経路。`06362f7` で RTT と到着間隔を表示。 | 会場での遅延・復帰。 |
| P2P 改善 `0aeeb8f`、`1c751df`、`36d9d93` | interactiveVoice、送信 watchdog 3 秒、Mac 固定、ヒステリシス、backoff、前面復帰、診断送信先の統一。 | 2 台 Mac、切断、LAN 退避の実機確認。 |
| 録画診断 `6bf2cf3`、`ecbebc8`、`cf3a873`、`59383b3` | geometry/CASE、tracking 窓、bridge 共存、copy 解放。不採用候補 6→12、前 winner に近い候補を優先。 | B が多ければ候補生成前に落ちた component の記録を検討。 |
| 撮影支援 `63eca9d`、`ded9bba`、`2a82db8`、`60f1933`、`9661ec2` | 露出上限実験、ガイド付き録画、開始 3 秒・終了 5 秒を選びにくくする、赤い物を仮定しない v2、swing 4 枚の precheck 対応。 | 撮影中の lossless と物体真値を確保。 |
| Mac 要約・一覧 `ebb398a`、`a2faab5`、`6ac28ca` | 無料 report、overview、古い受信コードの `/health`、安全な再起動、時刻と失敗理由、テストログ隔離。 | 実運用・保存量・失敗後の手順。 |
| Unity 診断受信の自動起動（3D-Saber `9613d60`） | Play で受信側も起動。Unity を閉じても解析のため受信側は残る。 | 当日 Mac での起動・終了確認。 |

warmNoDeepRed の定義:

- 集合 D は、scoring に使った sample 点を原 BGRA に戻し、周囲 1px の正方形を合併した画素。端で clip し重複を除く。bbox 全体ではない。
- deep red: `R≥180, G<.40R, B<.65R`。warm: `R≥120, .45R≤G≤.92R, B≤.95G`。
- eligible RED で `deepCount=0 AND warmFrac≥.30` のとき却下。BLUE・score・PCA/端点・UDP は変えていない。境界は整数比較。
- 198 枚・±8% で既存の正しい実剣出力 147/147 を保持。元画像で肌・光候補 134/144 を除外、saberなし FP 80→55、232850 の保存 9 枚は 9→0。
- production と調査の仮想再選択は元画像 198 枚で mismatch 0。処理時間中央値 13.9→14.2ms。全検証 PASS は採用 commit の記録で確認した。
  調査メモ中の sandbox による XCTest 未実行・Tools/Release 失敗は、それ以前の試行の結果。
- 保持側余白は .212。ただし肌・光との全体分離余白はなく、+8% の 232850 では別背景へ移って FP 9/9 が残った。
  147/147 は「既存正解を保った」数字で、全可視剣の recall 100% ではない。未生成・ineligible・順位負けは残る。

### blueNoDeepSupport — 2026-10-06 採用（この worktree、未commit）

- ユーザーの本番変更指示により、保留していた青 D を Swift / Android C++ core に採用。全既存 eligibility とランキングの後、eligible BLUE のみに適用し、最初の生存候補へ再選択する。残らなければ未検出。
- 画素領域は赤と同じ D（production sample 点 × step、原 buffer、周囲 1px 正方形の合併、unique、画像端で clip）。deep blue は `B >= 180 AND R*100 < 40*B AND G*100 < 65*B`。`deepCount == 0` のみ却下。整数比較、Set は membership だけで反復せず、score・順位・端点算出・RED・UDP の規則は維持。
- 採用根拠（既存の offline 調査）: 198 ORIGINAL、gain .92/1.00/1.08 で現在正しい BLUE 187/187 を保持、formal 40/40、nominal saberなし青 FP 29→18。独立標本数や全可視剣 recall を意味しない。
- 追加の実機根拠: 2026-10-06 の saberなし `163444_198` は青 FP 27.3%。明るい空色 RGB 約 202,234,245 に deep-blue pixel 0。別候補に深青支持があれば再選択されるため、この規則だけで session 全 FP が 0 になるとは限らない。
- この worktree の before/after（default step=2）: formal 35 PNG 中 BLUE 出力変更 5（全て RED fixture 用 PNG）、BLUE の formal 期待値に変更 0、40 色別期待値すべて PASS。inbox ORIGINAL 234 枚中 38 変更（25 未検出、13 別候補）。annotated 8 枚は除外。全 269 枚で RED 出力変更 0。
- formal で BLUE が変わる PNG は `forensic-20260921/frame_1000.png`, `frame_1048.png` と `lossless-regression/phonesaber_20260923_143446_247/` の `red_dropout_false_83.png`, `red_dropout_last_true_82.png`, `red_dropout_recovered_84.png`。BLUE 出力は全て別候補へ移る。期待値・tolerance・manifest は無変更。
- **変更全43件の一覧（5 formal＋38 inbox）**: [CSV](data/2026-10-06_blue_no_deep_support_output_changes.csv)。corpus-relative path、SHA-256、before/after BLUE 端点を保存。inbox の同じ画像の別ファイルも1件ずつ数え、private PNG / 詳細 JSON は repo に追加しない。
- `155919 f5321/f5331` の淡い端片は採用後に未検出となる。点灯真値は依然未確定で、成功例に数えない。遠い・淡い・ぶれた青、画面端の実機保持は引き続き確認する。確定正解の最小 deep count 14 は全実剣の下限ではない。
- diagnostics は `deepCount`, `pixelCount`, `rejected` と `blueNoDeepSupport` 理由を emitter / decision trace に記録し、streamed metadata / triage geometry へ伝播。Mac の emitter validator と metadata schema を同時更新し、両 encoding と triage trace/geometry の入力契約をテスト。32KB preflight 上限は無変更。
- 検証: `run_lossless_regression` 40/40、static Swift・C++ core test PASS。追加の XCTest 6 件＋赤青 context 2 件を元のテスト関数と本番 source snapshot から Mac host で実行し 8/8 PASS（両 metadata encoding と 32KB を含む。Simulator の全 verify とは別）。Mac C++ / Swift parity は 269 PNG × 3 入力経路、541 状態遷移、27 合成ケースで mismatch 0。diagnostics の hash seed 切替・収集 on/off の bit parity も PASS。Python tools の広範囲 suite は 381 test で 8 failure / 33 error / 1 skip（socket の sandbox 拒否、録画容量取得が 0、bridge compiler 監視）。今回の emitter/schema/triage 契約テスト 10 件は PASS。Simulator verify / Android build は Claude の後続検証。

## 試したが採用しなかったもの

| 案 | 結果 | 見送りの理由 |
|---|---|---|
| R7e: clip≥.35 または d240≥4.2 または d240≥3.5 かつ purity≥.60 | 初期 formal 40/40、背景 bench 7/8→0/8。129 frame の赤背景 winner 54→26。 | 決め手は約 0.3px の太さ差。+8% で bench 7/8 に戻り、遠い剣にも弱い。 |
| PF22: purity≥.22、clip≥.35 は免除 | f2552 を未検出にした。R7e 併用で 17 jump 中 10 件が未検出。 | purity 余裕 ±.04、肌には効かない。+8% で clip 免除が背景を通す。 |
| R7e/PF22 の実機 shadow（当時） | 10-04 の点灯赤 2 枚で却下 0、背景 winner は R7e 9/17。232850 の saberなしは R7e 67%、PF22 約 2%。 | 対象数と背景 80% 除外の目安を満たさず、本番へ昇格しなかった。 |
| hue>11° AND coreSupportRatio<.28 | formal 40/40、既存正解 147/147 保持、232850 FP 9→4。 | +8% で FP 9/9、青光下の肌・照明が残る。全体分離余白なし。 |
| deep red count≥1 のみ | 肌・光候補 144/144 を除外、FP 80→51。 | 白・magenta の実剣も失い formal 37/40。warm 条件を足した最終案だけ採用した。 |
| W: RED neutralFrac≥.80 AND ring4HaloFrac≤.05 | fixture 89 は実剣へ移る。他 39 fixture とラベル済み正解は保持。 | 合成の淡いピンク剣 RGB 250/215/218（min/max .86）を失う。効果が窓 1 例だけなので不採用（`5888bb5`）。 |
| 青 bpf22 / bmean225 | bpf22 は背景 1/19 のみ改善、bmean225 は formal 35/40。 | 効果不足・実剣の損失。 |
| 静止候補の一律除外、時間的 G1 | 背景を抑える場面はあった。 | 静止した剣も 2.2px/frame、背景との差 1–2px。G1 は誤った背景も追い続けた。 |
| emitter の緩和・compact purity .35 | ブレた候補を拾った。 | 前腕の誤軸、または formal 39/40。 |
| compact/面積/太さ・単特徴の追加 gate | 全正解保持の範囲では赤 other/布 0/52、青 2/13 の却下が最大。 | 小さい本物と背景が重なる。全 class を分ける安全な閾値なし。 |

## 残存誤検出と fixture 89

- 残存調査の nominal 正しい出力は赤 50/67、青 65/74（ラベル＋formal の独立目視 box）。既存 winner の保持率と全可視剣の recall は分けて扱う。
- warmNoDeepRed 後の元画像誤出力は赤 59（不在 55＋可視時 4）、青 37（不在 29＋可視時 8）。inventory の残り 1 行はラベル差異。
- 赤は other 41、布 11、肌 4、画面 2、窓 1。青は白い光・窓 12、布 9、画面 4、青光の物体 5、other 4、金属反射 3。
- 青 D は光・窓 winner 12/12 と反射 3/3 を落とすが、別候補へ再選択されるので最終 FP 減少は 11 件。
  衣服・肌・画面には深青支持があり、全背景を消す規則ではない。未知の端片を成功数から除いている点にも注意する。
- fixture `red_short_component_last_true_89` の期待端点 (422,146)–(444,146) は窓。本物は右下。
  ユーザーは 10-05 に「剣の位置に直す」と決定したが、detector はまだ窓を選ぶため期待値だけ直すと 39/40。
- 窓 score 58.62 に対し実剣は 52.48。再選択の実剣端点は (442,620)–(450,640) だが y=640 は画像外 1px。
  物体真値だけでなく端点 geometry も再審査が必要。期待値・tolerance は現状維持し、W で gate を通すことはしない。

## 受信側監査で分かったこと

- HTTP 413 計 80 件はすべて loopback 発。Simulator のテスト bundle が本物の receiver へ自動転送されていた。
  `f88fa50` でテスト中の転送を止めた。1 frame の録画失敗は証拠不足として正しく、`a18816a` で約 1 秒未満の自動転送を止めた。
- 実 Codex timeout 5 件。受信側が古いコードのまま動き、25 分へ延長後も 600 秒で止まる例があった。
  `0aeeb8f` / `ad25839` で 25 分、digest、timeout 後 high で 1 回だけ再試行。`6ac28ca` で古いコードを検出する。
- 監査時の Codex 記録 3,291 件の実実行は 12 件だけ。後にテスト由来 3,958 件をゴミ箱へ移し、実記録 12 件を残した。テストは一時ログ先へ隔離済み。
- 旧 010049 bundle の context は 41–42KB で今も precheck 不可。上限 32KB は維持する。schema 拒否は `03628df`、起動時の自動再解析は `c62378e` で対処済み。234740/155919 の timeout bundle は手動再解析の候補。
- 深い JSON・秘密情報除去の極端な処理時間・CASE 二重計上・replay の session 衝突は `312f63c`、`67597e4`、`d55294c` で修正済み。
  queue full、report 失敗、受信残骸は監査ではなかった。保存量の無制限増加と終了行なし 1 件の原因は未解決。

## 未解決の問いと判断条件

- 実機で warmNoDeepRed は肌・照明 FP を減らし、小さい剣・淡い剣・速い振りを維持できるか。別人・別照明・距離 0.5–3m の未使用 capture が必要。
- 青の淡い端片は点灯剣か。blueNoDeepSupport は 10-06 に採用したが、その真値と遠い・淡い青の実機保持は未確認。確定正解の最小 deep count 14 は全実剣の下限ではない。
- CASE A の gate は未達。別々の swing で 2 event 以上、正しい剣 candidate が eligible のまま遠方候補に僅差で負ける証拠が必要。
  初期ジャンプ 5 件の score gap は全て ≤3.2 だが、安定 frame も 26% が <3。margin だけでは判別できない。
- 修正 B は A と別変更。raw/robust 乖離、単独 body、弱い tail、分離 LED でないこと、妥当な body PCA が条件。長い剣・分離 LED の短縮を防ぐ。
- 静的マスクは静止背景に限る案。静止剣との重なりや照明変化を検証し、動く肌には別の特徴を探す。
- 実機 memoryHeadroom の最小は約 2.0GB（10-04 は 2028/1981MiB）で、録画の 256MiB 上限には余裕があった。長時間・別端末の保証ではない。
- interactiveVoice、LAN、USB を同じ条件で比べ、許容遅延・途切れを決める。30 分以上の発熱・fps・電池・メモリ、切断復帰、会場運用は未検証。
- Mac カメラで直接認識する構成は、ユーザーが残す将来案。iPhone 完結方式との精度・遅延・設置条件の比較後に判断する。
