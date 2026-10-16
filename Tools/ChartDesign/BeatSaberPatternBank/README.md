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

現行112素材すべてを本作の4拍JSONへ翻案。94素材を15譜面へ188小節として参照・適用し、18素材は理由付きで保留。以下の先行改修の説明は履歴。
採用候補61・調整して採用45・避けたい例6（問題点を解消した修正版）。
譜面データからの変換8、映像に基づく翻案98、解説の修正版6。映像は正確な採譜ではない。
最新6素材は I Wanna Be A Machine / Normal（128 BPM）、World Wide Web / Normal（128 BPM）、Untamed / Hard（145 BPM）の各2区間。
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
3. convert_patterns.pyで素材化。候補コピー上でhierarchy_materials.py --repo <候補> --features <解析>を実行し、Hardの位置を変えたら下位も包含関係を保つ。旧spatial-preserve-rhythmは現行譜面には使わない。
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



## 定時追加 2026-10-15 03:41

公式3曲から6素材を追加し、固有の4拍譜面に翻案。今回の3素材と、前回保留のAngel Voicesの2素材をlogic!!!・勉強の合計10小節へ反映。Hardの発音・手・ロングは保持し、下位包含を再検証。残る5素材は候補件数と不採用理由をapplications.jsonへ保存。累計82変換・77適用。

移動時間の検査をロング表示の終了ではなくcut-planの最終カット基準へ修正。表示余白を待ち時間に二重計上していたため、以前は有効な候補も除外されていた。test_material_gate.pyで境界と同時ノーツ間隔を回帰検証する。

## 定時追加 2026-10-15 07:05

One Hope Normal145、Immortal Normal150、Firestarter Easy135 BPMから6素材を追加。4素材を青葉騒140 BPMの8小節へ反映し、Hardの18打点の座標を変更。打点・担当手・ロングは保持し、NormalとEasyは包含関係と待ち時間を維持して更新。残る新規2素材と既存5素材は、実音・担当手・両手の並びや移動に合う候補がないため保留。元のEasyと本作の難易度は同一ではなく、広い移動のある翻案4素材はHard向け。累計88変換・81適用・7保留。

## 定時追加 2026-10-15 09:37

10月15日 09:37追加: Origins / Normal（175 BPM）、Crystallized / Hard（174 BPM）、Burning Sands / Normal（142 BPM）各2区間。6素材を4拍譜面へ翻案し、今回5素材を10小節へ反映。累計94素材、86素材適用、8素材保留。単発から両手で着地して休む形、左右で異なる内外・上下の応答を追加。Hardの発音・担当手・ロングは保持し、下位も包含関係を維持しました。映像の難易度とは別に、今回の広い移動を含む翻案はHard向けです。 更新曲はAobasou, ManicSonic。Hardの座標変更は20打点。現在の既存88素材の翻案定義は変更せず保持しました。

## 2026-10-15 フレーズのメリハリ改修

4分・8分の長い列を、曲別の発音の強弱と区切りに沿って短いまとまりと休符へ整理した。5曲15譜面が対象。新規の発音は追加せず、残した打点の時刻・手・位置、既存ロング、共通アクセントは保持。Hardの強い発音上位15%も保持。`rhythm-contrast-design.json` に曲別の変更理由、元の発音強度、変更前後の列長と休み、下位で残す打点を保存。

`hierarchy_rework.reduced` はこの設計が存在する場合、`rhythm_selection.select_retained` で確定打点を保持する。定時の位置素材追加で旧来の平均間隔へ再生成しない。新しい配置が保持打点の移動時間を満たさなければ候補を不採用にする。リズムを再改修するときは、この設計も明示的に更新する。

## 定時追加 2026-10-15 15:47

10月15日 15:47追加: $1.78 / Hard（200 BPM）、Be There For You / Hard（126 BPM）各3区間。6素材を4拍譜面へ翻案し、今回5素材を10小節へ反映。累計100素材、91素材適用、9素材保留。左右の役割が異なる短い連打と、両手一組の後に休む形を追加。全難易度の打点・担当手・ロングと、直前に設計した休符を保持。下位の包含関係も維持しました。映像の難易度とは別に、今回の広い移動を含む翻案はHard向けです。 更新曲はManicSonic, escapade, logic。Hardの座標変更は29打点。現在の既存94素材の翻案定義は変更せず保持しました。

## 定時の方向保持 2026-10-15 15:47

位置素材の適用では既存の打点・担当手・ロング・フリック方向を保持し、`hierarchy_materials.finalize_preserved` で全難易度のカット経路を採用前に検査する。方向練習用のフォールバックも、確定済みの下位譜面へ新しい打点を追加しない。`test_direction_training.py` の3件を追加し、既存6件と合わせて検証する。

## 曲別スタイルとHard 7〜9への改修

`song-style-design.json` が最新の人間の指示による音型・手順・動線の設計。Manic Sonic 9、escapade 9、logic!!! 7、勉強 8、青葉騒 8。表示だけでなく実データのゲーム計算も同じ整数になるよう検証。Normal・Easyも新しいHardからの部分列で、`rhythm-contrast-design.json` を第2版に更新。従来の手順・位置を定時更新で戻さない。旧リズム設計は `rhythm-contrast-design.pre-style.json` に保存。既存100素材の出典・個別JSONは保持。旧91適用のうち90区間の再編集前を `preSongStyleAppliedNotes` に記録し、休符になった1素材は保留へ戻した。計90適用・10保留。

定時更新では代表フレーズの `protectedCoreHits` を `style_core_valid` で保持する。その他の区間は、現在の実音・手順・方向と移動条件が合う場合だけ素材を適用する。`test_style_core.py` の3件を含め、回帰検査は12件。

## 定時追加 2026-10-16 09:34

10月16日 09:34追加: POP/STARS / Hard（170 BPM）、Crab Rave / Hard（125 BPM）、Rum n’ Bass / Hard（132 BPM）各2区間。6素材を4拍譜面へ翻案し、今回3素材を6小節へ反映。累計106素材、93素材適用、13素材保留。休止から単発へ戻る応答と、片手から両手への密度差を素材化。全難易度の打点・担当手・フリック・ロング、直前に設計した休符と曲別の代表フレーズを保持。下位の包含関係も維持しました。映像の難易度とは別に、今回の広い移動を含む翻案はHard向けです。 更新曲はAobasou, BenkyouRemaster。Hardの座標変更は14打点。現在の既存100素材の翻案定義は変更せず保持しました。

## 2026-10-16 拍の土台と音への応答

`beat-pickup-design.json` と `rhythm-contrast-design.json` 第3版が最新の人間のリズム編集。曲別の短い拍刻み、二拍の土台、歌・楽器への応答とフィルを組み合わせる。強い発音の順位だけで土台の拍を消さず、発音のない拍を追加しない。今回追加した打点は発音強度0.45以上・時刻誤差30ms以内を確認。Hardの既存ロング・強い発音上位15%・共通アクセント・フリックを保持。編集区間外のリズムと、残した全ノーツの担当手・座標を保持。Hardは9/9/7/8/8。Normal/Easyは完全な部分列で、定時更新では第3版のretainedHitsを守る。古い短列化や主音だけの選択へ再生成しない。素材106・適用93・保留13を維持。適用区間の旧ノーツはpreGrooveAppliedNotes。

## 分類別ZIPの配信容量（2026-10-16）

分類ZIPはdownload.htmlで必要時に端末側で生成し、25 MiB未満で分割する。動画・画像の本体はclips/で一度だけ配信する。Tools/BeatSaberPatternBank20261014/package.pyがpackages/の保存一覧と各メンバーのSHA-256を生成する。export_web.pyの後にpackage.pyを実行し、全ZIPのブラウザ保存とメンバー照合を確認する。packages配下のJSONはSHA-256の対象なのでexportの空白除去から除外。ZIPをdistへ再配置すると動画が重複し容量上限に近づくため、元の静的ZIP生成へ戻さない。素材・画質・出典は保持する。保存は「ZIPを準備」→「ZIPを保存」の2段階。失敗・中止時は再試行可能。

## 定時追加 2026-10-16 12:27

10月16日 12:27追加: I Wanna Be A Machine / Normal（128 BPM）、World Wide Web / Normal（128 BPM）、Untamed / Hard（145 BPM）各2区間。6素材を4拍譜面へ翻案し、今回1素材を2小節へ反映。累計112素材、94素材適用、18素材保留。拍の土台に続く短い応答と、左右で高さや間隔を変える形を素材化。全難易度の打点・担当手・フリック・ロング、直前に設計した休符と曲別の代表フレーズを保持。下位の包含関係も維持しました。映像の難易度とは別に、今回の広い移動を含む翻案はHard向けです。 更新曲はlogic。Hardの座標変更は4打点。現在の既存106素材の翻案定義は変更せず保持しました。
