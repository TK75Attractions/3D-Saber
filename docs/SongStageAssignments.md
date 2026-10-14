# 曲ごとの固定背景

2026-10-14、プレイ時の背景抽選を終了し、14曲それぞれへ異なる立体背景を割り当てた。全難易度・再プレイで同じ背景を使用する。

| 曲 | 背景 | 情景 |
| --- | --- | --- |
| ElDorado（黄金狂） | Amber Foundry | 金色の工場 |
| 2:23 AM | Midnight Harbor（追加） | 深夜の港、帆船、灯る倉庫と吊橋 |
| 青葉騒 feat.初音ミク | Verdant Atrium（追加） | 緑の温室、葉の段床、揺れる吊り鉢 |
| 勉強（Remaster） | Clockwork Library（追加） | 高い書棚、大時計、歯車と振り子 |
| アンダルシア | Desert Sanctum | 砂漠の聖域 |
| 校歌 | Violet Vault | 紫の大広間 |
| escapade | Crystal Grotto | 結晶洞窟 |
| logic!!! | Azure Prism | 青の立体構造 |
| Manic Sonic | Obsidian Relay | 青緑の金属デッキ |
| Morning | Sky Sanctuary | 空の聖域 |
| Phobia PARTⅠ | Abyssal Ruins | 深海の遺跡 |
| RUSH E | Pulse Array | 音楽に連動する灯具の配列 |
| 揺籠 | Moonlit Garden | 月夜の庭園 |
| Zero -狂奏曲- | Astral Orbit | 惑星と軌道 |

`NewSong` は譜面がなく選曲に出ないため、今回の14曲には含めない。

曲フォルダの `stage.json` の `stageTheme` に列挙名を保存する。曲名表示や難易度の変更では割り当てが変わらない。譜面エディターは未編集フィールドを保持するため、曲情報の保存でも指定は残る。将来の曲追加時には未使用の背景を指定し、必要に応じて背景も追加する。未指定・無効な指定は Obsidian Relay に固定で戻る。Gameシーン直接起動では既定曲ElDoradoの背景、調整画面ではObsidian Relayを使う。

チュートリアルには専用の **Practice Courtyard（練習の中庭）** を使う。石畳の左右に木組みの休憩所、ベンチ、植木鉢、足元灯を配置し、四つの小さな風鈴が穏やかに揺れる。中央と説明文字の背後を空け、背景粒子も小さく薄くした。選曲中の曲に関係なく入場時に選択し、チュートリアル入場フラグの消費後も維持する。通常曲には割り当てない。

`FloorRenderer.useSongThemeOnPlay = false` でInspector/検証用の固定指定を使える。旧 `randomizeOnPlay` の保存値は `FormerlySerializedAs` で引き継ぐ。`Build(StageTheme)` は明示指定のまま。構築後は曲途中に背景を変更しない。

既存ID 0〜10と削除済み写真背景のID 11〜13は維持し、新作は14〜16、チュートリアル専用は17。通常曲14種と練習用1種の計15背景。背景数と列挙値は一致しないので、一覧走査は `StageThemeCatalog.All` を使う。写真素材は復活させず、全て実メッシュで構成した。

新作も既存のScenicStageWorldへ接続し、材質ごとの結合メッシュ、共有シェーダー、72個以内の背景粒子を使う。動作はGamePlayManagerの曲時計で進み、停止・シークに追従する。中央通路を空け、装飾ColliderやLightは生成しない。

検証: `SongStageAssignmentTests` が全プレイ可能曲の明示指定・重複・再プレイを検査し、`ScenicStageWorldTests` が新作を含む形状、通路、描画グループ数、停止・巻き戻し・解放を検査する。`SongStageAssignmentPlayTests` は実Gameシーンで14曲と難易度を変えた再プレイを確認する。見た目は `ScenicWorldsPreview.Render` で実Gameカメラから出力できる。`-scenicPreviewLow` はLOW表示での確認用。

2026-10-14の実施結果: 関連EditMode 205件とPlayMode 5件が成功。既存テストの固定曲一覧に抜けていたRUSH Eを追加し、その1件を再実行した。描画確認後の足元・葉の調整ではScenicStageWorldTests 56件を再実行し成功。通常/LOWそれぞれ全14背景を実Gameカメラで描画した。比較画像は背景差を確認するため同じ検証用ノーツとHUDを使っており、画像上の曲名は実際の割り当てを表さない。実機セーバーとプロジェクターでの確認は含まない。

チュートリアル背景追加時の検証: `ScenicStageWorldTests`、`SongStageAssignmentTests`、`TutorialProgramTests`、`TutorialMusicTests` のEditMode 86件と、`TutorialFlowPlayTests`、`SongStageAssignmentPlayTests` のPlayMode 4件が成功。専用背景の自動選択、入場フラグ消費後の保持、音源ループ中の連続動作と停止追従、練習への移行、スキップ、通常14曲と再プレイを確認した。実チュートリアルの持ち方説明と赤ノーツ練習を通常/LOW表示で撮影し、文字とノーツの視認性を確認した。
