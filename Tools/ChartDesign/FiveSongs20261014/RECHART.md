# 5曲の譜面再設計 / 2026-10-14

現行譜面には、この再設計後に[40素材の変換配置](../BeatSaberPatternBank/README.md)を追加した。
以下の集計とプレビュー撮影結果は追加前の記録。現在のノーツ数・フリック数は
`independent-audit.json`、各素材の挿入位置は `../BeatSaberPatternBank/applications.json` を参照。
追加後も関連Unityテスト44件が成功し、連続フリックは全体・片手とも最大3。
その後のユーザー指示で色と画面位置を分離。現行版は上下の同時打ちと左右反転も使う。
新しい配置では関連Unityテスト59件が成功。`../BeatSaberPatternBank/spatial-layout-verification.json` を参照。

Rush Eより前に追加した5曲のEasy / Normal / Hardを作り直した。
小節ごとにレシピを入れ替え、同型反復を避けるためだけに打点を削る旧処理を廃止。
曲ごとのリズム骨格を繰り返し、句末の応答・句頭の両手打ち・変速時の休符で展開を付ける。

## 調べたBeat Saber譜面

本家収録曲のプレイ録画を25–31秒、60–66秒の連続フレームで確認した。
BPMと密度は動画の譜面情報、および[Beat Saber Map Analysisの集計](https://github.com/officialMECH/bs-analysis)と照合。
公式収録と作者は[Beat Gamesのマッパー一覧](https://beatsaber.com/documentation/level-mappers/index.html)を参照した。
録画はKival Evan本人のプレイ。カスタム譜面は補助資料として区別する。

|参照譜面|BPM・難易度・NPS|確認した配置と今回の使い方|
|---|---|---|
|[Into the Dream / Hard](https://www.youtube.com/watch?v=VfRyM0-jCSs&t=60s)|183 / Hard / 2.78|60秒台の左右交互と上下の折返し、途中の両手アクセント。Manic Sonicの180帯を縦軸の交互から斜め・弧へ展開。|
|[Into the Dream / Normal](https://www.youtube.com/watch?v=JmEDepUBXgU&t=60s)|183 / Normal / 1.87|同じ区間でも打点を減らし、次の手が読みやすい間隔を取る。各曲NormalとHardの密度差の参考。|
|[Unlimited Power / Hard](https://www.youtube.com/watch?v=pykGEBUtJeE&t=25s)|198 / Hard / 2.52|25秒台の左右の受け渡しと両手の着地、次の単打への休符。escapade 190帯の句頭を整理。|
|[Lvl Insane / Expert](https://www.youtube.com/watch?v=am22mbvgyD8&t=60s)|160 / Expert / 約3.0|60秒付近の同時打ちから間隔を空けた単打への移行。logic / 勉強の160帯の密度と句切りの参考。|
|[Burning Sands / Hard](https://www.youtube.com/watch?v=QcaBh6KS7_E&t=25s)|142 / Hard / 3.25|25秒台の単打→両手の上下反転→単打というまとまり。青葉騒140帯の歌の裏拍と句頭の両手打ちへ。|
|[Bring It On / Kolezan](https://beatsaver.com/maps/2475)（カスタム）|160 / Easy–Expert|公開DATの64–80拍を確認。Hardは左右交互から71拍の両手へ移り、その後に間を置く。シンコペーションを一貫した手順にする補助例。|

参考資料の難易度名とこのゲームのLVは直接換算しない。NPSはノーツ数/曲長で比較し、
同時打ちの割合・片手間隔・移動量も別々に検査した。壁・爆弾・クロス配置は移植していない。
160/180/190/140の基本テンポに近い例を選び、減速区間は実際の秒間隔に合わせて余白を増やした。
logicの8/8、各曲固有の変速表と一致する本家譜面を見つけたという意味ではない。
8/8は四分音符4個分として時刻計算し、八分音符単位の裏拍を保持した。

[BSMGの配置原則](https://bsmg.wiki/mapping/basic-mapping.html)も参照。
振った後の次の入口を確保し、高速部分は読みやすい反転を基本とする。
上記は参考にした構造であり、参照曲の座標列や音源をそのまま収録していない。

## 曲ごとの再設計

- **Manic Sonic:** 180帯は主拍の交互打ちと短い8分フィル。160/60への減速で密度を落とし、再加速で同じ縦軸へ戻る。
- **escapade:** 190の裏拍主題を反復。中盤の表拍、208–240拍のシンコペーション、主題回帰を分ける。
- **logic!!!:** 80–141拍の裏拍、120帯の主拍、236拍以降の裏拍主題を区別。小節途中の変速点を丸めない。
- **勉強（Remaster）:** 100/70帯には広い間隔、168–208拍は小節頭と余韻。360帯はEasyで4拍、Normal/Hardで2拍ごとの実打を基本にする。
- **青葉騒:** 140の安定した主拍から、176拍以降の歌の裏拍へ展開。256拍で再び現れる主題に配置上の共通性を残す。

セクション名は作譜上の役割であり、歌詞のAメロ/サビを確定したラベルではない。
各打点は元音源の発音強度0.20以上、グリッド時刻から±45ms以内の実音候補を持つ。
細分を消すのは、句頭の両手アクセントの前後0.30秒（Easy 0.45秒）とロングの手の占有を確保するとき。
この検出条件は聴感の完全な一致を保証するものではなく、打点の根拠として保存している。

## フリックと難易度

Easyはフリックなし。Normal/Hardは4小節フレーズの末尾だけに置く。
方向指定を減らしても、通常ノーツの上下・斜めの移動で振りの流れを保つ。
上限は全体12%/22%、直近16ノーツで1本/3本、全体連続4本以下・各手連続3本以下。
休符や逆手の通常ノーツを、片手の連続数をリセットする理由にはしない。
同時打ちの並び順にも依存せず、矢印を含む拍が続く場合の合計を検査する。
実測値は [independent-audit.json](independent-audit.json) に保存。

|曲|ノーツ数 Easy / Normal / Hard|フリック率 Normal / Hard|
|---|---|---|
|Manic Sonic|188 / 341 / 523|3.23% / 11.09%|
|escapade|154 / 282 / 405|4.96% / 8.15%|
|logic!!!|206 / 389 / 605|2.06% / 9.09%|
|勉強（Remaster）|147 / 274 / 403|3.65% / 8.44%|
|青葉騒|170 / 312 / 442|5.77% / 12.90%|

実測の連続フリックは全譜面で全体最大2、片手最大3。10ノーツ以上の連続はない。
ロングは2回カットで、終端まで同じ手の別ノーツを置かない。
左右の使用割合、同時2ノーツまで、移動速度、方向への入口、音源末尾も検査する。
テンポ・拍子・拍原点・offset・表示LV・音源・stage.jsonは従来値を保持した。

## 検証と再生成

- Python独立検証: 15譜面の経路・ロング占有・左右負荷・発音根拠・フリック制約を確認。
- Unity EditMode: `FiveSongPackTests` / `FiveSongPlayabilityTests` 43件成功。全15譜面の予定カットがPerfectで完了。
- Unity PlayMode: `FiveSongLoadingPlayTests` で5曲の本編読み込み、音源末尾で切り詰められないことを確認。
- `FiveSongRechartPreview.Render`: 本編視点のエディタープレビューを15譜面・18区間、計432フレーム出力。変速前後と密集部を目視確認。
- 物理セイバーを持つ人による通しプレイは未実施。

Python 3 + NumPy + SciPy + FFmpeg。Unityルートから次を実行する。

```text
python Tools/ChartDesign/FiveSongs20261014/rechart.py --repo . --output <候補フォルダー>
```

音源は既存のStreamingAssetsから読み、初回はLibrary以下へ解析キャッシュを作る。
生成対象は5曲の20譜面JSON（Normal別名含む）・15カット計画・15発音根拠・audit.jsonのみ。
候補を本体へ反映後、`verify_pack.py --repo . --output <監査JSON>` と上記Unityテストを実行する。
任意の既存解析は `--features <Analysisフォルダー>` で指定でき、音源ハッシュを照合する。

プレビュー再撮影:

```text
Unity -batchmode -projectPath <repo> -executeMethod FiveSongRechartPreview.Render -rechartPreviewOutput <output> -logFile <log>
```

作業時の元譜面・参照動画・検証ログ・検査画像は作業フォルダーの
`Tools/FiveSongsRechart20261014` に保存。参考動画や第三者の音源はリポジトリへ含めていない。
