# PhoneSaber STATUS

## ユーザー待ち
- **iPhone 実機 Debug Recording を1回**(2026-10-02 依頼)
  - 事前に main(40986a2 以降)の PhoneSaberSender を Xcode で iPhone にインストールする(memoryHeadroom と bridge 診断を含むビルド)。
  - Mac で Start PhoneSaber を起動し、iPhone で Debug Recording ON、診断対象の色は BOTH のまま。
  - 棒を大きく速く振る swing を数回。画面端への出入り(フレームアウト → フレームイン)も含める。
  - Stop 後に triage bundle を Mac へ自動転送(diagnostics-inbox に届けば、こちらで解析を開始する)。

## 現在の仮説と確度
- 主仮説: candidate selection の瞬間的なすり替え(CASE A)。確度は「中」(部分支持)。
  - 既存 session: candidateSwitch 7件、100px以上のジャンプ 9件、そのうち endpointPathChanged=false が 6/9件。
  - eligible が1件だけでのジャンプ(例B frame 660)や raw PCA tail の伸び(例B frame 665)は、CASE B/C の可能性あり。
- 修正 A の gate(新 capture で、別々の swing の CASE A が 2 event 以上)は**未達**。production は変更しない。

## 次にやること
1. 新しい実機 session が届いたら、まずコピー上で
   `python3 ios/PhoneSaberSender/Tools/phone_saber_triage_codex.py --dry-run <bundle copy>` を実行し、
   BRIDGE_SUMMARY / CANDIDATE_AUDIT の hint を得る。そのうえで CLAUDE.md §5 の手順(original PNG で確認)で CASE A/B/C を判定し、ここに記録する。
2. summary.json の motionEventSummary.runtime.memoryHeadroom.minimumAvailableBytes を確認し、256MiB 保持上限に実機の余裕があるかを判断する。
3. 待っている間は、既存 session の再解析と tools / tests の改善を進める。

---

## 作業ログ(新しい順)

### 2026-10-02
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
