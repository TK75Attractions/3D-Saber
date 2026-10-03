# PhoneSaber STATUS

## ユーザー待ち
- **iPhone 実機 Debug Recording を1回**(2026-10-03 更新。main 7449bc7 以降のビルドで撮る)
  1. Xcode で main の最新の PhoneSaberSender を iPhone に入れる(Debug build)。
  2. Mac で Start PhoneSaber を起動する。
  3. iPhone で Debug Recording を ON にする。診断対象の色は BOTH のまま。カメラは正立・固定(三脚など)。背景の赤い物(ラベル・カラビナ)は片付けない。
  4. 録画を開始し、**区間ラベル**を切り替えながら撮る(合計 5 分以内):
     - 「saberあり」: 赤・青の saber を点灯させて画面内に映し、大きく速く振る swing を数回。画面端への出入りも含める。
     - 「saberなし」: saber を画面外に出すか消灯して約 10 秒。
     - 「赤い物隠し」: 赤いラベル・カラビナを布などで隠して約 10 秒(saber はなし)。
  5. Stop すると triage bundle が Mac に自動転送され、`<inbox>/<bundle>.report.md` に1ページの要約ができる。
  - 解析で見ること: saberなし / 赤い物隠し区間の赤の誤検出率(`phone_saber_segments.py`)、本物の saber での CASE A の有無、shadow R7e の判定、露出、memoryHeadroom。

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
   - 次の capture で判断できるように、R7e の判定を「shadow(計算するが適用しない)」として Debug Recording に記録する作業を進行中。
   - 採用に必要な capture: 点灯した赤 saber を 0.5–3m、3部屋以上、昼/夜、固定露出/自動露出。同じ部屋で消灯時の赤い物。±1EV の露出振り。

---

## 作業ログ(新しい順)

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
