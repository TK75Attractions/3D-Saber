# HARD開始演出

選曲画面で校歌（曲ID `Epilogue`）のHARDを開始するときだけ、写真3枚・和文モールス・タイトル演出を挟む。
アンダルシアなど他曲のHARD、EASY／NORMAL、判定調整、タイトル移動、リザルトからの再挑戦は従来の遷移を使う。

| 時間 | 表示 |
| --- | --- |
| 0–4.4秒 | 実際の選曲画面にノイズが強まる。演出用の文字は出さない |
| 4.4–8.8秒 | 写真2・中庭 |
| 8.8–13.2秒 | 写真3・校庭 |
| 13.2–17.8秒 | 写真1・校門／正面。最後は必ずこの写真 |
| 18.25–19.15秒 | 上段の曲名が現れる |
| 19.2–20.1秒 | 下段の難易度が現れる |
| 19.5秒 | ノイズが完全に晴れる |
| 19.65–22.2秒 | Dotの8本の線が左右の画面外から収まる |
| 22.8–24秒 | 実ゲーム画面へフェードし、既存のカウントインへ渡す |

写真の寄り・移動・クロスフェードと、30→20%の粒状ノイズを維持。
MOVは使用しない。JPEGと7文字の透過PNGは受領素材から再圧縮せずコピーし、写真の向きだけUVで補正する。
校歌のLv.10ではDotの7文字を使用。他曲では校舎写真・モールス・特殊な難易度表示を含む演出全体を生成しない。
LOW設定では粒状ノイズ・写真移動・線の移動を止め、写真とタイトルのフェードを残す。

`ScreenTransition.LoadGame` が遷移中の入力ロックと破棄を所有し、`HardSongIntro` が描画と音を持つ。
写真で画面を覆ってからゲームシーンを非同期で読み込む。
既存の `GamePlayManager` は `ScreenTransition.IsBusy` を待つため、演出の裏で曲やノーツの時計は開始しない。
読み込みが遅いときは完成したタイトルを保持する。破棄・無効化時には音を止め、元から有効だった入力だけを戻す。

## モールス

指定文「これを解読できたら私たちに教えてくれたら景品贈呈！」を和文の読みへ変換。
0.55–17.1秒、720Hz、129回の信号。短点約35.9ms、点線比1:3、間隔1/3/7単位。
濁点は独立符号。感嘆符には和文表の符号がないため音として送信しない。

元表：[JARL](https://www.jarl.org/Japanese/A_Shiryo/A-C_Morse/morse.htm)。
`node Tools/HardIntro/generate-signal.mjs` で同じWAVを再生成できる。

## 検証・プレビュー

- EditMode: `HardIntroTimelineTests`
- PlayMode: `HardIntroPlayTests;ScreenTransitionPlayTests;SongLeadInPlayTests`
- 実シーン撮影: Unityの別検証コピーで `-batchmode -executeMethod HardIntroPreview.Render -hardIntroOutput <絶対出力先>`

撮影は通常の `SongSelectController.StartGame()` を実行し、画面の実時刻を `frames.ffconcat` に記録。
動画音声は実際に使うモールス・カウントイン・曲のクリップを、その回のDSP開始時刻で合成する。
シーンやユーザー設定、スコアは保存しない。
