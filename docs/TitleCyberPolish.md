# タイトル画面の整理

この記事は初回改修の記録です。現在の採用ロゴ・ランダム背景・演出は [タイトルの背景とアニメーション](TitleConcepts.md) を参照してください。

ノーツを切って開始する体験と暗い赤青の空間を残し、専用に作ったロゴと開始ノーツに視線を集める改修。

## 変更前

![変更前の実タイトル画面](images/title-before.png)

## 変更後

![変更後の実タイトル画面](images/title-after.png)

いずれも実際のTitleシーンをUnity 6000.3.9f1で撮影。720p画像を掲載し、1080pでも確認している。

- PLAY、SOLO、下部の操作説明、タグライン、版表記と丸いカード枠を外した。
- ロゴのB/E/A/T/R/C/S/L/Hを専用の輪郭から制作した。横に広い直立の字形で、線幅・面取り・字間・高さを揃える。`TitleWordmark`が文字の形をUIメッシュとして描くため、ロゴは既存フォントに依存しない。他画面の共通フォント設定は変更していない。
- 背景を赤青の段階的なレールと控えめな床の光へ整理した。
- 開始ノーツは四隅と斜めの印で示す。クリック、Enter/Space、セイバー切断による開始経路を維持する。
- 終了操作は右下の×ボタンにした。

## 参考

[Ghostrunner 2公式ページ](https://www.playstation.com/en-us/games/ghostrunner-2/)の鋭い輪郭と暗い空間の光、[WipEout Omega Collection公式ページ](https://www.playstation.com/en-us/games/wipeout-omega-collection/)の幾何学的な形の揃え方を参考にした。ロゴ・画像・フォントなどの素材は転載していない。

## 実画面の再確認

専用コピーをバッチ起動し、`-executeMethod TitleDesignCapture.Render -titleOutput <出力先>`を指定する。実シーンを1080p・720pで保存し、見えているノーツの位置をUIにレイキャストしてクリック、ノーツ切断とSongSelectへの遷移まで確認する。

実機センサーとプロジェクター投影の見え方は、このバッチ確認には含まない。

開始処理の既存EditMode `TitleStartNoteTests` は初回改修（388bfdc）で3件すべて成功した。その後の専用ロゴ化では開始処理を変更せず、実画面撮影とクリック・切断・遷移を再確認した。

初回の画面撮影では変更前後ともUnity Editor自身の検索インデックス起動時に `ArgumentOutOfRangeException` が出たが、シーン描画・クリック・切断・遷移の確認は完了した。初回テスト実行ではこの例外は発生していない。
