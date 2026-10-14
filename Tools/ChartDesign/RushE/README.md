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
| E（隠し・最難関） | E | 1,062 | 1,062 |

Easy/Normal/Hardは発音のピークを解析して配置し、速い連打は間引いて手を戻す時間を確保した。ロングは持続音上の反復カット、金ノーツは左右に離した同時カット。`chart.json` は Normal と同一。Eの採譜方針は後述。

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

ffmpegがPATHにない場合は `RUSH_E_FFMPEG` 環境変数を使用する。公式サムネイルを再配置する場合は `Source/rush-e-youtube.jpg` に置く。出力は作業フォルダーの `Staging/` に作成し、ゲームへは自動反映しない。反映時には既存の `.meta` のGUIDを保持する。音量測定値は `loudness.json` を別途維持する。


## RUSH Eだけの隠し難易度 E

選曲中にジャケットを10回切ると、E音の打音から3段の膨らむ効果音へ続き、正方形のタイルが画面を覆って紫の背景と「E」だけの難易度表示へ切り替わる。切替は約1.85秒。曲を変えると回数・解除・背景が戻る。解除は保存しない。

カメラ両手の横切る動作、マウスドラッグ、ジャケットのクリックに対応する。かざしたままの開始操作は従来どおり2秒、Enter/Spaceでも開始できる。切替中は開始・難易度変更・自動開始を止める。連続カメラ値の途切れや手ぶれでは回数を増やさない。

Eは専用の `chart_e.json` を使い、通常1,062ノーツ、同時切り40組。ロングとフリックは0。スコア・実績はEasy/Normal/Hardとは別に保存する。表示はE、内部レート上限は10。

2026-10-14の音への忠実さの見直しでは、単純な音量しきい値・75ms間引き・同じ手の140ms回復制限を廃止した。連続旋律では音声から周期と位相を求め、中高域の弱い発音も1打ずつ通常ノーツへ戻す。周期は発音の裏付けに使い、実音ピークのない時刻へノーツは追加しない。明確な連続音を休憩のために抜いたり、ロングへまとめたりしない。

同じ打音の帯域別ピークは35ms以内でまとめる。大量の音が重なる和音部分は主要拍の実音ピークを拾い、和音のうなり・減衰の細かな変動で不規則な連打を作らない。終止和音は136.280秒の左右同時1回とし、余韻上の追加カットを廃止。左右交互で全発音を保持し、最短の同じ手の間隔は約79.8ms。最難関向けの密度であり、身体での実機評価は別途必要。

`e-attacks.json` に音声SHA-256・採用した1,022発音・周期解析・密集和音区間、`e-evidence.json` に各ノーツとの対応を保存。`e-audit.json` で全採用発音の収録、架空のカット0、手の回復都合による欠落0を確認できる。検出結果は音源の自動解析によるもので、ピアノの全声部を楽譜から転記したものではない。

ジャケットは元動画の公式サムネイルを `cover.jpg` に保存。横長画像は盤に合わせて中央を円形に表示し、画像自体は加工しない。`build_rush.py` で再生成する場合も、以前の自作画像へ戻さない。

`RushESecretTests` は解除条件・斬撃の重複防止・弱い連続音の回帰・終止和音後の架空カットがないことを確認し、`RushESecretPlayTests` は実選曲画面のクリック10回・紫テーマ・解除後のE専用表示・曲変更時の復元を確認する。`RushESongTests` と `RushELoadingPlayTests` はEにも適用する。`build_secret_e.py <解析作業フォルダー> [--output <別のStagingフォルダー>]` で、標準譜面と同じ `analysis.json` と `features.npz` からEを再生成できる。

`-batchmode -executeMethod RushESecretPreview.Render -rushEPreviewOutput <出力フォルダー>` で通常画面・タイル・E表示・曲変更後・Eゲーム開始を撮影し、描画メッシュと読み込みを検証できる。2026-10-14に関連EditMode 43件、PlayMode 18件が成功。タイル描画の修正後、解除の実シーンテストと上記の撮影も成功した。
