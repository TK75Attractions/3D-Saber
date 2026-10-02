# 写真調のプレイ背景

2026-10-03追加。既存11種類に3種類を追加し、プレイ開始時に全14種類から抽選する。直前の背景を除外する従来の抽選を維持し、曲中には切り替えない。既存enumの番号は変更せず、AuroraLake=11、RainyCity=12、SunlitOcean=13を追加する。

| 背景 | 内容 | 動き |
| --- | --- | --- |
| Aurora Lake | オーロラ、山影、静かな湖 | 空の緑の光だけが揺れ、水面が細かく波打つ |
| Rainy City | 雨に濡れた夜の街、街灯と路面の反射 | 雨筋と路面の小さな揺れ |
| Sunlit Ocean | 水中から見た水面、光の筋、両端の岩 | 水の屈折とゆっくり変化する光 |

背景画像は内蔵image_genで今回新規生成した写真風の画像。実際に撮影した写真・動画ではない。元PNGはAssets/Resources/Stage/Photographicに収録し、外部サービスへの通信は不要。生成プロンプトは作業フォルダTools/PhotographicStages/prompts.jsonに保存。

FloorRendererがPhotographicStageを構築し、そのTickを既存GamePlayManagerの曲時計から呼ぶ。独自Updateや実時間の_Timeは使用しない。曲停止中は自然現象も止まり、巻き戻し時は同じ状態へ戻る。サビの強調は穏やかな局所変化のみ。LOW設定では追加の雨筋・光・サビ強調を30%に抑える。

一枚の画面用メッシュと専用材質で描画。URPの2D RendererとForward Rendererに対応する。写真は縦横比を維持して画面を覆い、ノーツの中央域を柔らかく減光する。背景は深度を書き込まず、背景キューと低いsortingOrderでノーツより先に描く。カメラ、ノーツ、判定ゲート、譜面、入力、音量には変更を加えない。

PhotographicTextureImportが対象フォルダだけのインポートを設定する。最大2048、sRGB、縦横比維持、端はClamp、ミップマップなし、CPU側読込なし、高品質圧縮。選ばれた背景の画像のみResourcesから取得する。ステージが所有するメッシュと材質は破棄時に解放し、共有のResourcesテクスチャは破棄しない。

PhotographicStageTestsは素材・描画設定・資源解放・時計制御を検証。PhotographicStagePlayTestsは実Gameシーンを3種類で起動し、通常の曲時計による駆動と停止を検証する。PhotographicStagesPreview.Renderは実Gameのカメラ、HUD、固定した検証ノーツを使い3背景を撮影する。シーン・曲・譜面の保存は行わない。
