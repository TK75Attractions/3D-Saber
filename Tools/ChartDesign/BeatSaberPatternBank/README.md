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

64素材すべてを本作の4拍JSONへ翻案し、15譜面へ128小節として適用済み。
採用候補34・調整して採用24・避けたい例6（問題点を解消した修正版）。
譜面データからの変換8、映像に基づく翻案50、解説の修正版6。映像は正確な採譜ではない。
今回の6素材は Crystallized / Normal（174 BPM）、Firestarter / Normal（135 BPM）、MISA MISA!（190 BPM、Expert+相当の推定）の各2区間。
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
3. convert_patterns.pyで素材化。integrate_patterns.py --repo <入力> --output <候補> --features <解析>で未適用分だけ候補へ挿入する。
4. 曲別再設計済みの譜面を旧rechart.pyで再生成しない（保護あり）。音や展開を再設計するときだけidentity_rework.pyの曲別定義をレビューして使う。定時更新は現在の譜面を入力にする。
5. 入力との差分・元音源・verify_pack.py・verify_conversion.pyを確認。変更箇所と適用済み素材の記録を照合する。
6. FiveSongPackTests / FiveSongPlayabilityTests / FiveSongLoadingPlayTestsを検証。開いているユーザーのUnityを閉じない。独立コピーはWindowsのパス長を避けて短い場所へ作る。
7. export_web.py --pattern-site <既存サイト> --chart-site <既存ビューア>で対象5曲だけ反映する。対象外のハッシュ不変を必ず確認。
8. JSON/CSV・分類ZIP・件数・スマホ表示・動画デコード/再生を確認し、同じSites IDと非公開範囲で公開する。

新素材のpreferredSongsとmaximumBpmDifferencePercentで、適用先とBPM差を明示できる。無理に違う曲へ埋め込まない。
`applications.json` は素材と実際の曲・難易度・小節・時刻・ノーツを相互に結ぶ。
既存の検証履歴は `batches/`。最新のテスト結果は `conversion-verification.json` を参照。
物理セイバーでの通しプレイは未実施。
