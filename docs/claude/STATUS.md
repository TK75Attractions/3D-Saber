# PhoneSaber STATUS

## ユーザー待ち
- なし(2026-10-04 夜の2本は受信・解析済み)。次の撮影は「ガイド付き録画」(区間ラベルと剣を振る間の lossless 保存を自動化、実装中)ができてから依頼する。

## 現在の仮説と確度
- **2026-10-02 の再解析で見直した。** 既存の代表例(005850_489 f2537、013205_087 f255、010049_190 f660–664)は、
  original PNG に**点灯した赤 saber が映っていない**。赤い出力は、背景の赤ラベル(左)と赤カラビナ(右下)の間を往復している。
  - したがって「僅差の candidate すり替え(CASE A)」は起きているが、**本物の saber の上では未確認**。
    いまの例に修正 A を入れても、誤った背景出力が安定するだけ。修正 A の根拠にはならない。
  - より有力な見方: saber 不在時に背景の赤い物が eligible になる(背景誤検出 / eligibility の問題)。**確度は「高」**(2026-10-02 解析)。
    production の detector を offline で再現(端末の score と完全一致)した結果、赤ラベル・カラビナは emitterScore 0.68–0.73(閾値 0.42)で eligible を通る。
    赤の「value」は max channel = R なので、つや消しの赤い物でも R が飽和すれば LED と区別できない。白い芯(core)の証拠は必須ではない。
    10-01 の実 saber 映像でも、赤ラベルは 14 frame 中 13 frame で eligible のまま控えており、端から見た saber(score 40.2)にラベル(55.1)が勝つ frame もあった。
    単独できれいに分ける特徴はない(mean value の AUC 0.92 が最良だが露出に依存し、保護対象の正例 device_normal_red_390 と重なる)。
    詳細: docs/claude/analysis/2026-10-02_background_false_positives.md
  - 本物の saber が関わる例は 1 件だけ(20260930_234740_348 f4023 blue。CASE B 寄り。画面外に出た可能性あり、event frame の PNG なし)。
  - CASE C(raw PCA tail): 010049_190 f665(raw 218 / robust 52)の 1 件。対象は背景物。
- score gap の分布(winner − runner-up、agent の再解析): ジャンプ開始 5 件はすべて ≤3.2。
  ただし安定 frame でも 26% が <3 なので、margin だけではジャンプを切り分けられない(n=5、すべて背景物)。
- 修正 A の gate(本物の saber で CASE A を、別々の swing で 2 event 以上)は**未達**。production は変更しない。
- **認識の非決定性を修正済み(bbc1f32)**: core-line 候補の score が Set の反復順(起動ごと・frame ごとに変わる)に依存していた。
  最大 4.3 点揺れ、eligibility の閾値(0.30)をまたぐ例もあった。点を並べ替えるだけの1行修正で、144 画像で winner/eligibility/端点は不変、formal 40/40。
  「ぐわんぐわん」への寄与は未確定(既存の記録では winner は変わっていない)だが、次の capture は決定的な認識で撮れる。

## 次にやること
- **2026-10-04 実機2本(005325_638 自動露出 / 005855_691 1/100 秒)**(docs/claude/analysis/2026-10-04_real_saber_sessions.md):
  bundle に残った frame は録画の最初と最後(準備・停止中)だけで、剣を振る場面が1枚もなかった。tracking event が準備中の背景どうしの約 490px ジャンプを選んだため。
  本物の saber 上の CASE A/B/C は 0(保留 1)。修正 A の gate は未達。Codex も2本とも needs_capture(1,058 秒 / 642 秒で完了、時間切れなし)。
  shadow 昇格テストは未達:点灯した赤 saber は 2 frame だけ(R7e/PF22 とも不採用 0)、背景の赤 winner は R7e で 9/17(目標 80%)。
  露出:1/100 で静止した saber は十分明るい(emitterScore 0.80–0.86)が、ISO が上がるので背景の誤検出は減らない。既定は自動のままを推奨。
  → 対策(実装中、診断のみ):録画全体での shadow 集計、最初と最後の数秒を event に選びにくくする、ガイド付き録画。
1. 新しい実機 session が届いたら、bundle のコピーで次の2つを実行する。
   - `python3 ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py --dry-run <copy>`(CASE hint)
   - `python3 ios/PhoneSaberSender/Tools/phone_saber_selection_replay.py <copy>`(score gap の分布と replay)
   そのうえで original PNG を見て、**本物の saber が映っているか**を最初に確認し、CASE A/B/C を判定する。
2. summary.json の motionEventSummary.runtime.memoryHeadroom.minimumAvailableBytes で、256MiB 上限の余裕を判断する。
3. 背景誤検出の測定基盤: `run_background_negative_benchmark.py`(formal 40 件とは分離。private 画像は commit せず、inbox の path と sha256 で参照)。baseline は false positive 7/8。
   新しい capture が届くと、受信時に `<inbox>/<bundle>.report.md`(無料の1ページ要約)が自動で作られる。
4. 修正方針の判断(**要ユーザー判断**): 修正 A(時間的一貫性)より先に「背景の赤い物の eligibility」を直すべきかどうか。
   eligibility の変更は production の recognition 変更になるので、gate(証拠・regression・実機再試験)を満たしてから行う。
   - offline 探索の結果(docs/claude/analysis/2026-10-03_eligibility_rule_exploration.md):最良の候補 R7e
     (赤のみ:clippedWhite ≥ 0.35、または太さ d240 ≥ 4.2、または d240 ≥ 3.5 かつ purity ≥ 0.60)で、
     formal 40/40、背景 FP 8→0、実 saber の取りこぼし 0、blue は不変。
   - ただし**採用は見送り**。決め手の差が太さ 0.3px 程度しかなく、明るさ +8% で背景 FP が 7/8 に戻る。
     部屋 3 つ程度・5 session 程度からの当てはめで、証拠が足りない。差分案は 2026-10-03_r7e_candidate_rule.diff.txt(未適用)。
   - R7e と PF22(赤 purity ≥ 0.22、ただし clippedWhite ≥ 0.35 は除外)を shadow(計算するが適用しない)として Debug Recording に記録済み。
     既存 23 bundle では PF22 が未検出に変えるジャンプは 16 件中 1 件(144936_295 f2552)だけ。10-02 のラベル・カラビナは purity 0.53–0.79 で防げない。
   - 採用に必要な capture: 点灯した赤 saber を 0.5–3m、3部屋以上、昼/夜、固定露出/自動露出。同じ部屋で消灯時の赤い物。±1EV の露出振り。

- **2026-10-04 背景誤検出ルールの比較**(docs/claude/analysis/2026-10-03_background_fp_rule_study.md、129 frame × 2 色を目視でラベル付け):
  R7e+PF22 で formal 40/40、背景ベンチ FP 7/8→0/8、saber 不在時の赤の背景 winner 54→26、実 saber の取りこぼし 0、選ばれたジャンプ 17 件中 10 件が未検出に変わる。
  ただし差は小さく(R7e の clipped 判定 +0.10、PF22 の purity ±0.04)、明るさ +8% で効果がほぼ消える。手持ちカメラの session(155919)と青の背景誤検出は、どのルールでも直らない。
  → 推奨:R7e を先に(単独 commit)、PF22 を次に。ただし実 saber の shadow 記録が足りないので、まだ本番に入れない(上の録画が必要)。

---

## 作業ログ(新しい順)

### 2026-10-04
- 実機2本を解析(上記)。ラベル表に 22 frame を追加(904faba)。Codex 解析は時間内に完了。
- 受信側の点検と修正:/health で古いコードのまま動いているかを表示、Start PhoneSaber が自分で起動した受信側だけ安全に再起動、ログに時刻と理由、test が本物のログ置き場に書かない。
  (調査結果:docs/claude/analysis/2026-10-03_receiver_log_audit.md)テスト由来の codex ログ 3,958 件はゴミ箱へ移した(本物 12 件は残した)。
- 全 session の一覧ページ `phone_saber_sessions_overview.html`(受信のたびに自動更新、`PhoneSaber Overview.command`)。デスクトップに PhoneSaber Status を追加。
- verify は実行ごとに専用の Simulator を作る(並列実行で test runner が kill される問題を解消)。2台目の Mac 用の setup(docs/claude/SECOND_MAC_SETUP.md)。
- 背景誤検出ルールの比較(上の「次にやること」参照、phone_saber_rule_study.py)。production の認識は変更なし。

### 2026-10-03(夜)
- 当日用 runbook(docs/claude/EVENT_DAY_RUNBOOK.md)と読み取り専用の点検 `PhoneSaber Status.command`(phone_saber_status.py)を追加(5723323)。
- Mac が2台あると診断 bundle が座標と別の Mac に届く問題を修正:座標で固定した Mac に送る(36d9d93)。
- 露出実験スイッチ(Debug Recording 欄、自動(既定)/ 1/100 / 1/120 / 1/240 秒、activeMaxExposureDuration で上限、ISO は自動)。
  自動ではカメラ設定に触れない。metadata・session report に記録(63eca9d)。
- shadow PF22 を記録(diagnostic only、parity bit 一致)。既存 23 bundle の集計は上の「次にやること」4 を参照(4316ce3)。
- 検証:並列 verify が同じ Simulator を共有して test runner が kill される問題があり、`PHONESABER_IOS_SIMULATOR_ID` で専用 Simulator を指定して PASS。

### 2026-10-03(午後)
- 実機なしの改善(並列):iOS P2P の review 指摘(正しい Mac への固定、送信 watchdog 3 秒、ヒステリシス、backoff、前面復帰、
  ローカルネットワーク許可の表示、接続中の検索停止)、Unity 側の bridge 自動起動の改善(受信できる間だけ起動、テストで起動しない、
  Mac 名入りの service 名)、1 秒未満の録画は自動転送しない、Codex 解析に要約を渡し timeout 時は high で1回だけ再試行。
- 調査(docs/claude/analysis/2026-10-03_motion_blur_and_blue_jumps.md):frame 2552 の勝者はズボンではなく壁コンセントのラベル。
  露出 1/50s のブレが主因。155919_297 の青のジャンプは saber が写っていない背景どうしの往復(誤検出)。
- 実機テスト2回目:体感は良好(遅延・判定のブレとも改善)。受信側に 413 が 17 回出ていたのは、こちらの test 実行時に
  Simulator が test 用 bundle を本物の receiver へ自動転送していたため(P2P relay / LAN の両方が Simulator から見える)。
  → test 実行中は自動転送しないよう修正。155608_448 は 1 frame だけの録画で、precheck 失敗は正しい挙動。
- Codex 再解析(1500 秒の制限内で 962 秒で完了、decision: needs_capture):本物の saber で初めて CASE B を確認。
  f2552 で、速く振った赤 saber の動きぶれ領域が hasEmitterCore=0・emitterScore 0.305(<0.42)で不採用になり、
  ズボンの小さな赤い領域(purity 0.18)が唯一の eligible として勝って飛んだ。ただし不採用候補 4 件が保存されず、原因を断定できなかった。
  → 不採用候補の保存数を 6→12 に増やし、削るときは直前の winner に近いものを優先して残すようにした。
- 実機テスト(phonesaber_20261003_144936_295、P2P 経由):診断 bundle は P2P relay 経由で受信できた。
  Codex 解析は 600 秒で timeout(入力が増えたため)→ 1500 秒に延長。
- 剣のラグの原因:AWDL の転送の詰まり。Unity の Editor.log の bridge 集計で `maxGapMs` が 170〜300 ms(ときに約 1 秒)。
  iPhone の認識処理時間は中央値 28.5 ms で以前と同じ。memoryHeadroom の最小は 2.0 GB(256 MiB 上限は問題なし)。
  対策:P2P の通信に `serviceClass = .interactiveVoice` を設定(効果は実機で要確認)。

### 2026-10-03(午前)
- 09ffdcd: Mac ↔ iPhone の P2P(peer-to-peer Wi-Fi)通信を追加。LAN(Bonjour / 手動 IP)へ自動で戻る。認識処理と Unity は変更なし。
  XCTest 177/177(任意実行の 1 件は skip)、Python 277/277、formal 40/40、Release PASS。実機での AWDL 確認は未実施(手順は ios/PhoneSaberSender/P2P_BRIDGE.md)。

### 2026-10-03(朝)
- 7449bc7: Debug Recording の区間ラベル(未設定 / saberあり / saberなし / 赤い物隠し)。区間ごとの検出率と誤検出率を `phone_saber_segments.py` で集計。
  認識結果は不変(録画なし / ラベルなし / ラベル切替の3通りで同一出力を確認)。XCTest 168/168、Python 267/267、formal 40/40、Release PASS。
- 02aafa4: session report に「背景誤検出の証拠(emitter / shadow R7e / 露出)」の節を追加。
- worktree と一時 branch はすべて削除し、main だけの状態。

### 2026-10-03(深夜、並列作業の続き)
- 1ad12f9: 診断 on/off の parity test を、全 candidate で bit 一致に厳格化(通常の hash seed でも一致)。XCTest 162/162、Python 245/245、formal 40/40。
- 31ad94c: Debug Recording に emitter の証拠(emitterScore と各項、閾値までの余裕、channel 統計)、shadow R7e 判定(適用しない)、frame ごとの露出(ISO・露光時間・bias・WB)を記録。
  診断 on/off で認識結果が bit 一致することを確認(固定 seed で 52 画像)。
- bbc1f32: core-line の scoring を Set 反復順から独立させた(認識の非決定性の修正)。
- d55294c: 新 tool の review 指摘を修正。session report の CASE 二重計上(B=2→1)、replay で同じ sessionID の bundle が上書きし合う問題、
  hotspot の2乗時間(4000 件で2–3秒、upload 応答の前に走る)、深さ 1000–8000 の JSON で receiver がログ保存前に落ちる残りの経路。XCTest 155/155、Python 239/239。
- 116130f: R7e(赤の eligibility 候補ルール)の offline 探索結果を記録。採用は見送り(理由は「次にやること 4」)。
- 312f63c: Codex 出力の深い入れ子で receiver が RecursionError で落ちる問題を修正(ログ保存前に落ちていた)。
- 67597e4: credential redaction の正規表現が長い単語列で3乗時間になっていた(20KB で約12秒、`"token"*40000` は数時間)。
  出力を変えない線形時間の形に置き換え(差分 fuzz 約670万件で不一致なし)。
- 4a38f53: 静的ホットスポット解析(背景誤検出の候補を自動で示す)。既存 2 session で赤ラベルを背景として検出。session report にも表示。
- R7e の offline 探索(上の「次にやること 4」)。

### 2026-10-02(夜、並列作業)
- cf3a873: bridge event が tracking 窓と重なると peak が bundle から落ち、precheck が失敗する不具合を修正(高)。
  失効した bridge copy を Stop 前に解放(中)、event 入れ替えは copy 成功後に evict(低)。XCTest 155/155、Python 220/220。
- b2d74dc: docs と code の不一致を修正(256MiB 上限、tracking 窓 11/8/5、toggle の扱い、bridge_priority、Desktop link 3つ、UI の上限表示 768→864MiB)。
- ebb398a: 1ページの session report(`phone_saber_session_report.py`)と、receiver での自動生成。
- 23b667f: 背景ネガティブ benchmark(baseline: false positive 7/8)。
- 014c4d2: 負荷で揺れる timing test を堅牢化(budget は据え置き、median 判定)。
- bdd684e: verify で `-collect-test-diagnostics never`(テスト後の simctl diagnose で最大10分止まる問題の対策)。selection replay tool を追加。
- 並列 agent の運用: 編集する agent は別の git worktree で作業し、lead が review → main に取り込み → 全 verify → push。worktree と一時 branch は取り込み後に削除。

### 2026-10-02
- 背景誤検出の解析(read-only agent): production detector を offline で再現し、赤ラベル・カラビナが eligible を通る理由を特定。
  formal corpus に「つや消しの赤い物」の hard negative がないことも確認。報告は docs/claude/analysis/2026-10-02_background_false_positives.md。
- ecbebc8: CASE audit と candidate geometry validator を強化(recovery hint、countForTally、validator を Swift 出力に厳密化、クラッシュ修正)。
- 既存 bundle 20 件を横断で再解析(read-only agent、表は docs/claude/analysis/2026-10-02_jump_events.csv)。
  ジャンプ/切替 17 件。CASE A 寄り 5 件はすべて背景物どうしの往復で、点灯 saber は映っていない(2537 / 255 は original PNG を目視で確認済み)。
  例B は phonesaber_20261002_010049_190(f660: eligible 1→1 で 437px、f665: raw 218 / robust 52)。
- 「push しない」指示を受けた(2026-10-02 04:2x)。同日夜に「push はどんどんしてよい」と再指示があり、以降は検証 PASS ごとに push している。
- 40986a2(ローカル): Debug Recording 中に os_proc_available_memory() の最小値を記録
  (motionSummary.runtime.memoryHeadroom と Stop 時のログ)。macOS の host harness では API が使えないため #if os(iOS)。
  検証: XCTest 150/150、formal 40/40、Python 182/182、Release PASS、diff-check PASS。
  初回は Python E2E(macOS 向けコンパイル)で 'unavailable in macOS' になり、上記の分岐で修正。
- a93658e(ローカル): triage の --dry-run に BRIDGE_SUMMARY と CANDIDATE_AUDIT を追加(有料の model 呼び出しなしで CASE hint を得られる)。
  既存 bundle 2件のコピーで実行した結果、geometry 記録より前の bundle なので audit は空(想定どおり)。
- 6bf2cf3(push 済み): 未commitだった diagnostics 改善一式、CLAUDE.md、回帰テストを commit。
  検証: XCTest 150/150、formal 40/40、Python 182/182、Release PASS、diff-check PASS。
- CLAUDE.md(運用ルール)を repo root に追加。
- 未commitの diagnostics 改善(15変更+新規2)を §3 と照合し、一致を確認。
  production の recognition コードパス(BGRADetection.swift / DetectionCore.swift / UDPSender.swift)に差分なし。
  FrameProcessor.swift の差分は、Debug Recorder へ diagnosticColors を渡すだけ。
- 既存 session の回帰確認(新しい選択ロジック: toggle 成分をランキングから除外、bridge と共存する場合は 8 frame 窓):
  - phonesaber_20261002_005850_489: peak 2538(red、score 74.52、detectedToggle=0、旧ランキングの記録最大値)。
    toggle を除外しても、ほかの frame の score は下がるだけなので、peak 2538 は最上位のまま。
    窓は peak を中心に取るため、onset 2537(color-close → color-sparse-raw、約422px)は、11枚の窓にも 8枚の窓にも入る。
  - phonesaber_20261002_013205_087: peak 256(red、score 90.30、detectedToggle=0)。同じ理由で onset 255 は窓に入る。
  - 回帰テスト `testRecordedCandidateSwitchOnsetStaysInTheWindowBesideABridgeEvent` を追加。
