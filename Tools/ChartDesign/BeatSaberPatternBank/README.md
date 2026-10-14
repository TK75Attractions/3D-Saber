# Beat Saber 配置素材ノート

スマホ用の閲覧・ダウンロード先: https://beat-saber-pattern-bank.metuji48.chatgpt.site

変換済み譜面: https://beat-saber-pattern-bank.metuji48.chatgpt.site/patterns.html

## 3D-Saberへの反映

40素材すべてを4拍の本作形式JSONへ変換し、元の5曲のEasy / Normal / Hard、計15譜面へ反映した。
各素材は1小節の配置と左右を入れ替えた応答の計2小節で、合計80小節。
採用候補18・調整して採用16を適合させ、避けたい例6は指摘された問題を修正した配置にした。
Manic Sonic / escapade / logic!!! / 勉強（Remaster）/ 青葉騒へ、それぞれ7 / 7 / 10 / 7 / 9素材を組み込んだ。

公式動画26素材は映像で観察した構造に基づく簡略化した翻案で、原作ノーツの完全な採譜ではない。
カスタム8素材は原譜面の拍・左右の手を使い、本作の座標とフリック数へ調整した。
解説6素材は修正案から作った新規配置。元の悪い配置をそのまま移植していない。

`patterns/` と `converted-patterns.json` が再利用用の譜面。
`applications.json` に各素材の適用先、開始小節、拍、ノーツ列、元動画URL、音源付きの通し譜面へのリンクを保存する。
スマホページでは4拍素材と実際に組み込んだ2小節を切り替えられ、元動画と比較できる。
各JSONと全80譜面のZIPも保存可能。プレビューの音はメトロノームで、曲音源は通し譜面のリンクで再生する。

音源の発音候補、既存ロング、前後の手の移動、変速、難易度を検査して配置先を選定した。
現行15譜面のフリック連続数は全体・片手とも最大3。Easyは0。
独立検証は `integration-validation.json`、ブラウザとUnityの検証概要は `conversion-verification.json` を参照。
素材適用時はUnityの関連EditMode 43件とPlayMode 1件が成功。物理セイバーでの通しプレイは未実施。

## 色と出現位置を分離した配置

ユーザーの「Eのように縦並び・左右逆も使う」という方針を反映し、5曲15譜面の配置と40素材の座標を更新した。
青は左手、赤は右手を表すが、画面の左右を固定する意味ではない。青が右、赤が左、上下の同時打ち、
中央と斜めを通る受け渡しを使う。音型を保持する区間と、次の軸へ移る区間を分けて展開する。
色の判定をx座標から分離し、金ノーツの設計上の担当はカット計画と発音根拠に保存した。
スマホの配置一覧も色から担当手を表示する。

今回の更新はノーツの時刻・拍・色・個数・ロング長を保持する。移動の変化に合わないフリック1個のみタップへ変更した。
フリックの連続数は全体・片手とも最大3。新しい方向は前後の経路から選び直し、同時打ちの距離は1.5超を保つ。
詳細は `spatial-layout-verification.json` と `integration-validation.json`。
配置変更後はEditMode 58件とPlayMode 1件が成功。スマホ幅390pxとPC幅1440pxで、
素材・適用先の80表示と全15曲譜面を確認した。色の担当表示573ノーツ中、249ノーツは従来の左右と逆の位置。

基本譜面の生成は `FiveSongs20261014/spatial_layout.py`、小節素材の配置は `convert_patterns.py` の `LAYOUT` で定義する。
既存15譜面の今回限りの変換には `reflow_positions.py` を用いた。二重に回転させないよう反映済み版では停止する。
再試行する場合は `--baseline` に変換前のバックアップを指定する。通常の素材追加ではこの処理を再実行しない。
高難度や交差だけを悪い例と分類せず、次の動作との接続・視認性・同時動作の間隔で評価する。

## 追加収集時の更新

Python 3、NumPy、SciPy、FFmpegを使う。Unityルートから実行する。

```text
python Tools/ChartDesign/BeatSaberPatternBank/convert_patterns.py
python Tools/ChartDesign/BeatSaberPatternBank/integrate_patterns.py --repo . --output <候補フォルダー>
python Tools/ChartDesign/BeatSaberPatternBank/verify_conversion.py
python Tools/ChartDesign/FiveSongs20261014/verify_pack.py --repo . --output <検証結果.json>
python Tools/ChartDesign/BeatSaberPatternBank/export_web.py --pattern-site <素材サイトのチェックアウト> --chart-site <全譜面サイトのチェックアウト>
```

新素材は映像を確認し、`convert_patterns.py` の定義または原譜面データと、`LAYOUT` の画面配置を追加してから変換する。
`integrate_patterns.py` は `applications.json` で反映済みIDを除き、未反映素材だけの候補を生成する。
候補の対象JSONを検証して本体へ反映し、以降の検証とUnityの関連3テスト群を実行する。
適合する場所がない場合は無理に挿入せず、未適用理由を記録して配置を再検討する。
`export_web.py` は素材サイトと対象5曲のスマホ用譜面を更新する。両サイトを既存の公開範囲で更新する。
毎時の自動処理も収集・変換・適用・検証・サイト更新を対象とする。

2026-10-14 に作成。初回34素材から、19時台の定期処理で6素材を追加した。
現在は公式6曲とカスタム2曲、計12譜面から34区間と、配置の比較解説6区間。
各素材に観察理由、3D-Saber向けの調整案、出典と元動画の時刻を記録している。

19時台はElixia / Hard（128 BPM）とBe There For You / Normal（126 BPM）から各3区間を追加。
休符から単発へ戻る配置、2拍おきの両手打ち、片手2連から交互へ移る配置を収録した。
既存34素材の適用ノーツを保持し、追加6素材をlogic!!!と青葉騒へ組み込んだ。
追加・変換・適用・検証の記録は `batches/20261014-1915.json`。

## 収録範囲

| 曲 | BPM | 難易度 | 区間数 |
| --- | --- | --- | --- |
| Into the Dream | 183 | Normal / Hard | 8 |
| Burning Sands | 142 | Hard | 4 |
| Unlimited Power | 198 | Hard | 4 |
| Lvl Insane | 160 | Expert | 4 |
| Bring It On | 160 | Easy / Normal / Hard / Expert | 4 |
| Shut Up and Dance | 128 | Expert+ | 4 |
| Elixia | 128 | Hard | 3 |
| Be There For You | 126 | Normal | 3 |
| Cyrixの配置解説 | 未指定 | 解説 | 6 |

各楽曲区間は `240 / BPM` 秒の4拍。公式26区間は音声から拍を推定したもので、楽譜上の小節頭は未確定。カスタム8区間は原譜面の4拍グリッドと原曲音声を動画へ照合した（音声の照合刻みは20ms）。いずれも30fpsで書き出しているため、映像の境界にはフレーム単位の丸めがある。解説6区間は4秒の抜粋で、小節には数えない。

Bring It On は複数難易度を切り替える動画のため、素材ごとに画面の表示を確認した。前後付きの選択区間内も同じ難易度。Shut Up and Dance は終了画面の Expert+ / 894 good cuts と収録譜面のノーツ数を照合した。

## 素材の使い方

`collection.json` の `barFile`、`contextFile`、`poster` は閲覧サイトを基準にした相対パス。サイトから動画を個別保存でき、分類別ZIPには動画と索引を収録している。動画のGit保存先はSitesのソースリポジトリ。このUnityリポジトリには索引、変換譜面、適用記録、検証結果を置く。

`sourceNotes` はカスタム譜面の小節内時刻・左右の手・列・段・原作の矢印を記録した参考データで、ゲーム用の生成済み譜面ではない。`beat` は区間先頭からの相対拍、`column` は0〜3、`row` は0〜2、`cutDirection` はBeat Saber v2形式（0上、1下、2左、3右、4左上、5右上、6左下、7右下、8任意）。

分類は選択区間の移植適性の暫定評価。原作の高難度・交差・大きい動きだけを理由に悪配置とはしない。譜面全体の評価や実機プレイの快適さの保証ではない。小節を接続する際は前の振り終わりと次の開始位置も確認する。

移植はタップを基本にし、各素材のフリックは0〜1個を目安とする。連結後の譜面全体、および左右それぞれのノーツ列で、フリックが10ノーツ以上連続しないことを再検証する。動画中の原作の矢印数へこの制限を適用するものではない。動画の色はカスタムの場合があるため、左右の手を基準に3D-Saberの色へ変換する。

## 検証

- 80本（短い動画40＋前後付き40）すべてH.264/AACで書き出し、全区間のデコードと長さを確認。初回68本と追加12本をそれぞれ検証。
- フレーム列と前後付きの画像で配置・難易度表示を確認。実機での打感は未検証。
- モバイル幅390pxとPC幅1440pxで表示確認。初回34素材と追加6素材のブラウザ再生を確認。検索、分類/BPM絞り込み、候補保存の再読込、速度変更、前後付き切替、ダウンロードも検証。
- ZIPのCRCと動画のSHA-256を確認。分類別ZIPはすべて25MiB未満。

参考基準: [BSMG Basic Mapping](https://bsmg.wiki/mapping/basic-mapping.html)、[Intermediate Mapping](https://bsmg.wiki/mapping/intermediate-mapping.html)、[BeastSaber Curation](https://bsaber.com/curation)。素材ごとの判断は収録映像の観察メモに記載。

動画・楽曲・譜面の権利は各権利者に帰属する。出典付きの短い研究用抜粋であり、自由利用素材のライセンスを付与するものではない。
