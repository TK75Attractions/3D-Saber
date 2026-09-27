# ディスク式の曲選択画面

2026-09-27の設計資料と、その後の回答に基づく実装。`SongSelect` シーンを再生すると生成する。シーンへの手動配置や設定変更は不要。

## 操作

| 操作先 | 照準を重ねる時間 | キーボード |
| --- | --- | --- |
| 左右の盤：曲を送る | 1秒 | 左右矢印・A/D |
| 難易度：EASY / NORMAL / MASTER | 1秒 | 上下矢印・W/S、1/2/3 |
| 中央の盤：プレイ開始 | 2秒 | Enter・テンキーEnter・Space |
| 判定調整 | 2秒 | ボタンをクリック |
| タイトルへ | 2秒 | Esc |

各ボタンはクリックでも操作できる。難易度は丸い見た目で、下の難易度名も含む190×196の受付範囲を持つ。盤の受付範囲は円。

発射後は発射した位置から0.12秒以上連続して外すまで再発射しない。入力が途絶えた場合と、0.2秒を超える長いフレームでは溜め直す。幕の最中も再発射防止の状態を保持する。

選曲時間は100秒。幕の間は時計を止め、0秒で選択中の曲と難易度を一度だけ開始する。選曲シーンへ入り直すと100秒に戻る。

## 試聴と背景

選択を1秒維持すると従来の試聴区間を10秒だけ再生し、停止する。繰り返さない。背景は同じ音源のDSP時計を参照する。

背景は専用カメラ・専用レイヤー30・1920×1080のRenderTextureを使い、UIの後ろに表示する。本編のカメラ設定やブルームは変更しない。床の映り込みは光の鏡像メッシュで描く。光と粒子は選曲専用シェーダーで描画する。

拍の時刻は各難易度のノーツの `(beat, time + offsetMs)` を統合して補間する。校歌のテンポ変化とアンダルシアの拍子変更に対応し、強拍は `ChartMeterMap` と `timeSignatures` から決める。判定調整の個人オフセットは背景には加えない。ノーツから2点を得られない場合のみBPMと譜面の開始時刻に戻す。

資料付属のジャケットを2:23 AM、El Dorado、校歌、Morning、揺籠へ設定。アンダルシアは既存画像を使う。画像がない曲は曲名入りの仮ジャケットを表示するため、NeonParadeや追加曲も表示できる。

## 実績

曲と難易度ごとに、通常プレイを完了した時だけ保存する。SはS+を含み、FCはAPを含む。同じ人の再プレイも加算し、表示単位は指定どおり「人」。したがって個人の重複を除いた人数ではなく、延べ達成人数である。

ランクは本編と同じ `PlayRankHelper` を使う。FCはBAD・MISSが0、APは全判定がPERFECTの結果。判定調整・空の結果・途中離脱は記録しない。プレイごとに発行するIDで、同一結果の重複保存を防ぐ。結果画面の再表示では加算しない。

保存キーは `songAchievements_v1_<曲ID>::<難易度>`。既存ハイスコアからの人数の推測・移行はせず、導入後の完了分から集計する。

## ファイルの役割

- `SongSelectSkin`：配置、ディスク移動、ふりがな、難易度、実績と制限時間の表示。
- `SongSelectDiscTarget` / `SongSelectAimTracker` / `SongSelectAimPointer`：的ごとの時間と照準受付。
- `SongSelectCountdown`：100秒の時計と一度だけの期限通知。
- `SongSelectCorridor` / `SongSelectBeatTimeline`：通路と拍への反応。
- `SongSelectRubyText`：既存のTextMeshProフォントで親文字位置にふりがなを重ねる。外部のルビライブラリは追加していない。
- `SongAchievementStore` / `GameSession`：実績の保存。`GamePlayManager` の通常終了で、最終判定数を確定した直後に一度呼ぶ。
- `SongSelectDesignPreview`：選曲画面の画像出力とUIレイキャスト検証。保存設定を退避・復元する。

本編のゲームプレイ、譜面、判定幅、判定調整、本編の演出・音の同期は変更していない。

## 検証

Unity 6000.3.9f1の検証用コピーで、既存のユーザー設定とは異なる保存先を使ってテストする。編集時テストは `SongSelect;SongAchievement;SongWheel;SongPreviewWindow;UISkinTests`、プレイ時テストは `SongSelect;SongPreviewSynchronization;SongChartPreview;SongChartAvailability;SongJacketOwnership;AndalusiaSongFlow;ScreenTransition;PlayFeedbackPlayTests.SongSelect` が対象。

画像出力は1920×1080と1280×720で確認する。プロジェクターモードの画像は確認対象だが、実際の投影と実機の外部照準での操作感は別途調整が必要。
