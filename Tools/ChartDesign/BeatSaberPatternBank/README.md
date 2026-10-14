# Beat Saber 配置素材と5曲の設計

素材集: https://beat-saber-pattern-bank.metuji48.chatgpt.site
譜面: https://beat-saber-pattern-bank.metuji48.chatgpt.site/patterns.html
今回の変更: https://beat-saber-pattern-bank.metuji48.chatgpt.site/design.html
全曲ビューア: https://saber-chart-library.metuji48.chatgpt.site

## 更新範囲の固定

ユーザー指定により、更新できるのは ManicSonic / escapade / logic / BenkyouRemaster / Aobasou の Easy・Normal・Hard だけ。
ZeroとPhobiaは固定。RushEを含むすべての対象外曲も変更・再生成・書き出ししない。
`scope-policy.json` と `scope_guard.py` を生成・素材統合・サイト書き出しで使用する。
作業前後で対象外のローカル37譜面とWeb28譜面のハッシュ、およびWeb目録を照合する。
`frozen-baseline-20261014.json` は固定指示時点の記録。後日の手動編集をこの過去版へ勝手に戻さない。

## 現行の素材と作り分け

現行76素材すべてを本作の4拍JSONへ翻案。72素材を15譜面へ144小節として参照・適用し、4素材は理由付きで保留。以下の先行改修の説明は履歴。
採用候補42・調整して採用28・避けたい例6（問題点を解消した修正版）。
譜面データからの変換8、映像に基づく翻案56、解説の修正版6。映像は正確な採譜ではない。
前回の6素材は Crystallized / Normal（174 BPM）、Firestarter / Normal（135 BPM）、MISA MISA!（190 BPM、Expert+相当の推定）の各2区間。
MISA MISA!の作者公開映像の0:00〜1:27が対象曲。難易度画面はなく、現行譜面ZIPも取得不可のため版・難易度の一致は未確定。比較用として密度を下げた。
元動画・投稿者・作者・時刻・分類理由は `collection.json/csv`。自由利用の許諾を得た素材ではなく、出典付きの短い研究用抜粋。

同じリズム表・常時左右交互・同じ上下運動を5曲全体に当てる作り方を見直した。
650小節の通常フレーズを曲別に再設計し、別に新素材6本を12小節へ適用した。既存58素材の適用ノーツは保持。
Manic Sonicは主拍と両手の着地、escapadeは短い連打と休符、logic!!!は非対称の応答、勉強は変速の対比、青葉騒は歌の発音と間を主軸にする。
`FiveSongs20261014/identity-design.json` に変更小節・時刻・参照素材・BPM差・未参照理由・変更前後の比較を記録する。
通常の横配置を主軸にする。縦・交差・反転は短い技であり、全体を周期回転させない。
色で担当手を判定し、金はcut-plan/evidenceの記録を使う。xの正負で手を推定しない。
フリック連続は現行で全体最大2・片手最大3。Easyは0。境界と同時打ちの並び順も検証する。

## 継続更新の手順

1. scope-policy、現在のcollection・converted-patterns・applicationsと最新ユーザー指示を読む。
2. 映像と前後を実見し、新規素材ごとにEXPLICIT/LAYOUTの固有定義を追加する。登録済み58本・64本等の過去件数へリセットしない。
3. convert_patterns.pyで素材化。integrate_patterns.py --repo <入力> --output <候補> --features <解析> --spatial-preserve-rhythmで未適用分だけ候補へ挿入する。
4. 曲別再設計済みの譜面を旧rechart.pyで再生成しない（保護あり）。音や展開を再設計するときだけidentity_rework.pyの曲別定義をレビューして使う。定時更新は現在の譜面を入力にする。
5. 入力との差分・元音源・verify_pack.py・verify_conversion.pyを確認。変更箇所と適用済み素材の記録を照合する。
6. FiveSongPackTests / FiveSongPlayabilityTests / FiveSongLoadingPlayTestsを検証。開いているユーザーのUnityを閉じない。独立コピーはWindowsのパス長を避けて短い場所へ作る。
7. export_web.py --pattern-site <既存サイト> --chart-site <既存ビューア>で対象5曲だけ反映する。対象外のハッシュ不変を必ず確認。
8. JSON/CSV・分類ZIP・件数・スマホ表示・動画デコード/再生を確認し、同じSites IDと非公開範囲で公開する。

新素材のpreferredSongsとmaximumBpmDifferencePercentで、適用先とBPM差を明示できる。無理に違う曲へ埋め込まない。
`applications.json` は素材と実際の曲・難易度・小節・時刻・ノーツを相互に結ぶ。
既存の検証履歴は `batches/`。最新のテスト結果は `conversion-verification.json` を参照。
物理セイバーでの通しプレイは未実施。

## Hardの音はめ・可動域改修（2026-10-15）

今回の変更は5曲のHardのみ。Easy・Normalと対象外の全曲は保持。
`FiveSongs20261014/hard-rhythm-design.json` に打点の追加・削除・発音の位置と強さ・候補を不採用にした理由・短い技の区間を保存した。
556打点を追加し72打点を整理。強い半拍候補はBPM90〜205、発音強度0.60以上、拍との差28ms以内という解析基準であり、音楽全体の正確な採譜率を示すものではない。
横幅は約±2.1、縦は約±1.1まで使用。高速部分は経路を詰め、広い高低差を通常の運動として許可する。縦・交差・同側は短い区間へ組み込む。
23件のHard素材適用を音源に合わせて再構成し、`applications.json` の `appliedNotes` と `noteIndices` を更新。元の適用は `referenceAppliedNotes` として保持。`identity-design.json` のHard統計はこの改修前の履歴であり、現在値は `hard-rhythm-design.json` を参照。
実機で人がプレイした検証ではない。更新時は現在のHardを共通定型リズムから再生成しない。

## 定時追加 2026-10-15 00:20

Full Charge / Normal（125 BPM）、Origins / Hard（175 BPM）、Give a Little Love / Hard（128 BPM）から各2区間を追加。4拍相当で小節頭・譜面版は未確定。6素材の固有定義を映像と前後の流れから作成した。
今回の曲への適用は `integrate_patterns.py --spatial-preserve-rhythm` を使用。Manic Sonic、logic!!!、青葉騒のHardへ各2素材、合計12小節を適用した。座標のみを変更し、打点・手・種類・フリック・ロング長は完全保持。Easy・Normalと残り2曲のHardは変更なし。
素材自体の4拍JSONと、曲の既存リズムに合わせた位置の翻案は別に表示する。参照BPMとの差、位相、合う担当手と発音、変更前のノーツは applications.json に記録。前回の短い技、既存64素材の適用内容、音はめ改修の根拠も保持。
今後、音はめ改修済みHardを従来の疎な2小節テンプレートで置換する実行は停止する。未適用素材がない場合は書き換えない。適合しない素材は無理に入れず理由を記録して保留する。

座標差分の診断値 `positionChangedNotes` は移動距離0.15超を数える。今回の83ノーツは境界での小さな補間も含む全座標変更数。打点の追加・削除は0。

## 2026-10-15 難易度包含・4置き場への改修

今回の最新指示は5曲15譜面。以前の「Hardのみ」を更新した。hierarchy-design.json / verify_hierarchy.pyが現行設計。HardからNormal、NormalからEasyを選び、時刻・手・位置を100%保持。ユーザー指定の共通アクセントを保持。中心間距離1.2超は判定領域の非重複を保証しない。実機の到達・追跡は未確認。

定時の新素材は候補コピーでhierarchy_materials.py --repo <候補> --features <解析>を使う。微小な補間や単独Hard書換えへ戻さない。全15譜面のverify_pack.py / verify_hierarchy.pyとverify_conversion.pyを通す。適用できない素材はapplications.jsonのpendingへ理由付きで残す。現行76素材、72素材適用、4素材保留。旧70素材の適用記録は今回の曲別再設計に合わせて更新し、元のノーツはpreHierarchyAppliedNotesへ保存した。

追加はAngel Voices Hard166、Light it Up Normal174、Unlimited Power Normal No Arrows198 BPM各2区間。Angel Voicesの作者クレジットは公式Hardと動画説明が一致せず明記。映像の4拍相当であり小節頭・譜面版は未確定。2素材をescapadeへ反映、4素材は保留。


