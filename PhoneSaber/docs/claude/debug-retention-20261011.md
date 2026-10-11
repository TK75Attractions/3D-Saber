# 手動 Debug Recording の保持調査（2026-10-11）

診断のみの変更。commit / push はしていない。指定の2 bundle は read-only で JSON と既存 report を確認し、画像・bundle・個人データをリポジトリへコピーしていない。

## 原因と修正

### `memory` は実際の予算超過を意味していなかった

`DebugVideoRecorder.swift:1927` は候補の geometry があれば tracking 窓の選択へ入り、motion の hit は画像保持へ渡していなかった。`motionSummary()`（同ファイル:2167、旧:2160）は、保持も明示的棄却理由もない motion event に既定値 `memory` を付けていた。tracking が開始すると既存の motion capture も捨てていた（同:2090）。

従って15.188 / 13.010等の行と、eventIndex=1000000000 の11.922は同じランキングの候補ではない。前者は時間的にまとめた motion event の信号集計、後者は `detectedToggle` を除いた1 frame の tracking score。今回の `memory` 行から、予算超過で高スコアの tracking window を失ったとは言えない。

tracking に置き換えた motion capture は `tracking_mode` と記録し、不明な保持なしは `not_retained` にした。実際の motion BGRA 予算拒否は従来どおり `memory`。Python の入力検査はこの2つの既知コードのみを追加で受理し、未知コード・不正な値・context 不整合の拒否を維持する。

### 終了待ち候補の間引きで、正しい中間の最良候補を失う

`DebugRecordingTriage.swift` の `DebugHandlingAwareRetention.insert`（現:2561、旧の間引き:2576）は provisional 候補を `capacity + 1` に制限し、最古を保護して残りの弱い候補を削除していた。最古→中間→最新とスコアが上がる場合、中間は「最新より弱い」というだけでは削除できない。Stop が来れば最新だけが末尾5秒に入ることがあり、中間が最良の有効候補になる。

60 fps、56.9秒の合成例で、50.30秒(score 5)、51.50秒(10)、52.10秒(20)、55.40秒(30)を入力すると、Stop時に正しい中心は51.50秒。旧コードは中間を失い50.30秒を選ぶ。追加テストを旧コードに当てて失敗を確認し、修正後は51.50秒を保持する。

勝ち得ない dominated 候補だけを削除し、その他の終了待ち候補は既存の共通256 MiB BGRA予算内で保持する。tracking / bridge 両方に適用する。確定後の枠数（tracking 1、bridge 2）、12画像、64 MiB転送、32 KB preflight、コピー前のメモリ検査、disk安全ゲートは維持した。追加の provisional 窓は画像輸出前には最終枠数まで選別される。

### 55.40秒の末尾誤分類は現ソースでは再現していない

いただいた終了時刻56.9秒なら55.40秒は末尾扱いである。`DebugVideoRecorder.swift:2714` は最後に受理した frame の相対時刻を `finish(end:)` に渡す。`DebugRecordingTriage.swift:2612` はそれまでの provisional 候補を Stop時に `stop` に分類し直してから最終選択する。`phone_saber_session_report.py:762` は保存済みの flag と notes を表示するだけで、handling 期間を再計算しない。

55.40 / 56.9の直接回帰テストは旧コードでも通る。上記の候補間引きは「弱い outside 候補を選ぶ」原因にはなるが、「末尾1.5秒を outside と表示する」説明にはならない。この2つを同じバグとは断定していない。

旧bundleは判定に使った終了時刻や実機buildの識別を持たないため、実機で動いたコード・終了時刻との照合は未完了。frame数 / 60だけでは、欠落frameがある録画の最終presentation timestampは決まらない。別記録の49.25秒は、終了が60秒ならoutsideとして正しい。今後の照合用に `motionSummary.trackingCapture.recordingEndTimeSeconds`（同:2252）を追加した。実機の再記録でこの値と中心時刻・selection notesを比較することを提案する。

## テスト

- `DebugHandlingPeriodTests`: macOS上で対象XCTestクラスを抽出し、実際の診断Swiftモジュールに対して実行。11件 PASS。中間候補、60 fpsの真のStop境界、末尾1.5秒の分類を含む。
- 旧保持コードに同じ11件を適用: 2テストで3 assertionが失敗。中間候補が失われる欠陥を確認した。末尾1.5秒分類は旧コードでもPASS。
- `DebugRecordingRetentionTests`: 実際の録画→Stop→bundleで中間swingの画像、`tracking_mode`、最終時刻、12枚と256 MiBの上限を検査するテストを追加。制限されたmacOSホストでは空き容量APIが0を返し、録画のdisk安全ゲートが開始を拒否するため未実行。macOSだけその環境を明示的にskipする。iOSではskipしない。
- Python: ledger追加・未知コード拒否・context不整合と、受信サーバーを起動しないsession-reportクラス、計26件 PASS。受信サーバー関連2件はソケットbindがサンドボックスで拒否され、実行不可。
- targeted `xcodebuild test`: `PhoneSaberSenderTests/DebugHandlingPeriodTests` と `PhoneSaberSenderTests/DebugRecordingRetentionTests` を指定して試行。CoreSimulatorServiceへ接続できず対象deviceを取得できないため、テストは未実行。Simulatorは作成していない。
- `swiftc -O -D DEBUG -enable-testing` で recorder / triage / FrameProcessor / detector / guided recording のmacOS用モジュールをビルド。UI依存の型宣言と転送を行わないstubのみホスト用supportで補った。
- `git diff --check -- PhoneSaber` PASS。リポジトリ全体のdiff検査は、既存フォントのLFS filterが共有`.git`へ書こうとして拒否されたため不可。フォントは触れていない。全体の `verify_phone_saber.sh` は指定どおり実行していない。

Xcodeプロジェクトが実際に参照するテストディレクトリは `PhoneSaber/ios/PhoneSaberSenderTests/`。既存の `DebugRecordingTriageTests.swift:1976` 以降に追加したためプロジェクト配線の変更は不要。

## 設計上の限界と残る確認

- 有限のBGRA予算では、終了時刻未定の全候補を必ず保持できるとは保証できない。今回の修正は予算に空きがあるのに中間leaderを削除する欠陥を除いたもの。予算に達すればコピーを拒否し安全を優先する。必要なら別タスクで、実際のtrackingコピー拒否数・候補保持数を診断に追加して実機負荷を測る。
- manual / unlabeledではスコアだけで本物の点灯saberと背景を区別できない。handling期間外の最良tracking窓を選ぶが、必ず実剣とは限らない。提案は現行のガイド付き録画または `sabersVisible` ラベルを使うこと。ラベルは既存どおり同点時の優先だけであり、認識ルールは変えていない。
- 12枚のため、従来どおり最終tracking窓は1 event（bridgeと共存すると8枚等）。複数の独立swingを必ず収録する仕様ではない。複数swingの網羅はガイド付き録画で補うことを提案する。
- productionのranking / eligibility / scoring / PCA / UDP、formal lossless corpusと期待値、repair / safety gateには変更なし。今回の実機画像保持・負荷と、55.40秒誤分類の実機起因の確認は残る。
