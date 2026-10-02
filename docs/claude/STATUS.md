# PhoneSaber STATUS

## ユーザー待ち
- **iPhone 実機 Debug Recording を1回**(2026-10-02 依頼、同日に条件を追加)
  - 事前に main の最新版の PhoneSaberSender を Xcode で iPhone にインストールする(memoryHeadroom・bridge・candidate geometry 診断を含むビルド)。
  - Mac で Start PhoneSaber を起動し、iPhone で Debug Recording ON、診断対象の色は BOTH のまま。
  - **赤・青の saber を点灯させ、画面内に映した状態で**撮る(既存の 10-02 session は saber が映っていない、または消灯していた)。
  - カメラは正立・固定(三脚など)。背景の赤い物(ラベル・カラビナ)はあえて片付けない(背景誤検出の比較のため)。
  - 棒を大きく速く振る swing を数回。画面端への出入り(フレームアウト → フレームイン)も含める。
  - 最後に 5 秒ほど、saber を画面外に出す/消灯した区間を入れる(背景だけの区間の比較用)。
  - Stop 後に triage bundle を Mac へ自動転送(diagnostics-inbox に届けば、こちらで解析を開始する)。

## 現在の仮説と確度
- **2026-10-02 の再解析で見直した。** 既存の代表例(005850_489 f2537、013205_087 f255、010049_190 f660–664)は、
  original PNG に**点灯した赤 saber が映っていない**。赤い出力は、背景の赤ラベル(左)と赤カラビナ(右下)の間を往復している。
  - したがって「僅差の candidate すり替え(CASE A)」は起きているが、**本物の saber の上では未確認**。
    いまの例に修正 A を入れても、誤った背景出力が安定するだけ。修正 A の根拠にはならない。
  - より有力な見方: saber 不在時に背景の赤い物が eligible になる(背景誤検出 / eligibility の問題)。確度は「中」。
  - 本物の saber が関わる例は 1 件だけ(20260930_234740_348 f4023 blue。CASE B 寄り。画面外に出た可能性あり、event frame の PNG なし)。
  - CASE C(raw PCA tail): 010049_190 f665(raw 218 / robust 52)の 1 件。対象は背景物。
- score gap の分布(winner − runner-up、agent の再解析): ジャンプ開始 5 件はすべて ≤3.2。
  ただし安定 frame でも 26% が <3 なので、margin だけではジャンプを切り分けられない(n=5、すべて背景物)。
- 修正 A の gate(本物の saber で CASE A を、別々の swing で 2 event 以上)は**未達**。production は変更しない。

## 次にやること
1. 新しい実機 session が届いたら、bundle のコピーで次の2つを実行する。
   - `python3 ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py --dry-run <copy>`(CASE hint)
   - `python3 ios/PhoneSaberSender/Tools/phone_saber_selection_replay.py <copy>`(score gap の分布と replay)
   そのうえで original PNG を見て、**本物の saber が映っているか**を最初に確認し、CASE A/B/C を判定する。
2. summary.json の motionEventSummary.runtime.memoryHeadroom.minimumAvailableBytes で、256MiB 上限の余裕を判断する。
3. saber 不在時の背景誤検出が主因なら、修正 A ではなく「背景 candidate の eligibility」側の診断を優先する(要ユーザー判断:目標の見直し)。

---

## 作業ログ(新しい順)

### 2026-10-02
- 既存 bundle 20 件を横断で再解析(read-only agent、表は docs/claude/analysis/2026-10-02_jump_events.csv)。
  ジャンプ/切替 17 件。CASE A 寄り 5 件はすべて背景物どうしの往復で、点灯 saber は映っていない(2537 / 255 は original PNG を目視で確認済み)。
  例B は phonesaber_20261002_010049_190(f660: eligible 1→1 で 437px、f665: raw 218 / robust 52)。
- 「push しない」指示を受けた(2026-10-02 04:2x)。以降の commit はローカルのみ。origin/main は 6bf2cf3 まで。
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
