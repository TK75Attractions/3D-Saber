# RUSH E（文化祭用）

Sheet Music Boss の [公式SoundCloud音源](https://soundcloud.com/sheetmusicboss/rush-e-impossible3) を使用。作曲は Andrew Wrangell、[元動画](https://www.youtube.com/watch?v=Qskm9MTz2V4) の案内は選曲画面と曲フォルダーの `CREDITS.txt` に表示・保存する。

利用者から、3日間の有料文化祭（1プレイ300円）での利用許可を得たとの連絡を受けて導入した。許諾メールは利用者が保管する。公開リポジトリには音声を含めず、音源を単独で再配布する許諾としては扱わない。

## 他の文化祭用PCへ移す

1. この変更を取得する。
2. 導入済みPCの `Assets/StreamingAssets/Songs/RushE/audio.wav` を、使用するPCの同じ場所へコピーする。
3. 選曲画面で **RUSH E** を選ぶ。プレイヤーをビルドする場合は、先に音源を配置してからビルドする。

音源がない端末では、この曲を選曲一覧に表示しない。`.gitignore` はこの曲の `audio.*` を除外する。ビルド済みゲームには音源が同梱されるため、文化祭の実施用として管理する。

WAVは44,100Hz、ステレオ、16bit PCM、144.219138秒。先頭の無音除去・速度変更はしていない。SHA-256:

```text
dd8fb71abaa1760ad0d989b3668974cb1c0ffff426a54139a3340c94329a0d65
```

別録音・別編集版に差し替える場合は、ノーツ時刻とプレビュー開始位置を確認する。`loudness.json` で再生ゲインを -8.27dB にし、他の曲と同じ -18 LUFS を目安に調整している。音声ファイル自体の波形は変更しない。

## 譜面

| 難易度 | レベル | ノーツ数 | カット数 |
| --- | ---: | ---: | ---: |
| Easy | 3 | 150 | 157 |
| Normal | 6 | 263 | 285 |
| Hard | 8 | 330 | 352 |

発音のピークを解析して配置し、速い連打は間引いて手を戻す時間を確保した。ロングは持続音上の反復カット、金ノーツは左右に離した同時カット。`chart.json` は Normal と同一。

ノーツの `time` は音源先頭からのミリ秒。`editorTempoMap` は編集を補助する区間別の推定値で、原譜の指定ではない。加速部の推定グリッドを小節線として見せないよう `hideBarLines` を有効にしている。

`audit.json` に密度・移動量の集計、`*-evidence.json` に発音の根拠、`*-cut-plan.json` に全カットの時刻と方向を記録。実際のノーツ生成・判定処理による自動検証に使用する。身体を使った実機プレイの評価とは別なので、会場のセンサー・投影環境に合わせて試遊し微調整する。

## 検証と再生成

Unity Test Runnerで `RushESongTests`、`SongLocalAudioTests`（EditMode）と `RushELoadingPlayTests`（PlayMode）を実行する。音源のないPCではデコード・実シーン再生のテストだけをスキップする。

表示確認用のバッチは `-batchmode -executeMethod RushEPreview.Render -rushEPreviewOutput <出力フォルダー>`。描画するため `-nographics` は付けない。3難易度を1080p/720pで保存し、作者・URL・開始ボタンを検証後、自動終了する。

解析・再生成には Python、NumPy、Pillow、ffmpeg が必要。リポジトリ外の作業フォルダーに `Source/rush-e.m4a` と `Source/provenance.json`（このディレクトリの `source.json` のコピー）を配置し、次を実行する。

```text
python Tools/ChartDesign/RushE/analyze.py <作業フォルダー>
python Tools/ChartDesign/RushE/build_rush.py <作業フォルダー>
```

ffmpegがPATHにない場合は `RUSH_E_FFMPEG`、ジャケットの太字フォントを指定する場合は `RUSH_E_COVER_FONT` 環境変数を使用する。出力は作業フォルダーの `Staging/` に作成し、ゲームへは自動反映しない。反映時には既存の `.meta` のGUIDを保持する。音量測定値は `loudness.json` を別途維持する。
