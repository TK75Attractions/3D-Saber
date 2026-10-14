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
| E（隠し・最難関） | E | 1,158 | 1,169 |

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

Eは専用の `chart_e.json` を使い、通常1,151ノーツ＋短いロング7ノーツ。フリックは0。スコア・実績はEasy/Normal/Hardとは別に保存する。表示はE、内部レート上限は10。

2026-10-14の追加依頼で、[指定動画（MASO Channel）](https://www.youtube.com/watch?v=HkKRAd7JYkU)と同じ譜面へ変更した。動画153秒のリザルトにある **RUSH E (v2) / Charted by JonathanRune / Expert+** を照合し、[BeatSaver 1eb9](https://beatsaver.com/maps/1eb9)のStandard ExpertPlus（投稿者ryhme、版ハッシュ`b6a0b4ea2319a5c8ea8175d3a1d86b5d1b617db1`）を移植。以前の音声ピーク解析によるE譜面と`e-attacks.json`は置き換えた。

全1,158ノーツの時刻・4列3段の相対配置を保持し、フリック指定を外した。金ノーツ以外は元の担当手も保持する。横座標は-1.5/-0.5/0.5/1.5、縦座標は-0.8/0/0.8。クロス配置も左右反転せず残している。Beat Saberの赤=左手・青=右手を本ゲームの青=左手・赤=右手に変換する。2ノーツ同時192組、最後は上下2段の4ノーツ同時を維持する。

元譜面の音源には既存音源より1.656508秒長い先頭の余白がある。音声を11025HzのPCMへそろえ、曲中4/20/45/70/95/120/134秒の各3秒を相互相関で照合し、全区間で18,263サンプルの同じ差（相関0.982〜0.997）を確認。ノーツ時刻からこの差だけを引き、既存の許諾音源は変更しない。元譜面の音源・ジャケットはリポジトリへ追加していない。

ロングは曲時刻18.218/24.531/54.031/60.437/78.312（両手）/109.187秒の7ノーツ。元の開始時刻と位置を保ったまま2〜3回カット、420〜700msに変更し、終了から次の同じ手のノーツまで250ms以上空ける。連打をまとめたり元ノーツを減らしたりしない。

追加の金ノーツ指定では、曲時刻125.718〜136.281秒の終盤ラッシュ159ノーツを金に変更した。それとは別に47.437/57.093/65.437/95.562/112.218/118.406秒の左右同時押し6組（12ノーツ）も金に変更し、合計171ノーツ。色だけを変え、ノーツ数・時刻・位置・7個のロングは維持する。金は既存ルールどおり左右どちらの手でも切れる。`e-audit.json`に山場と途中アクセントを別集計し、`e-evidence.json`の`goldReason`で区別する。元の担当手は`sourceHand`に残す。

`BeatSaberSource/`に元の`info.dat`と`ExpertPlus.dat`、`e-beatsaber-provenance.json`に出典・ハッシュ・音声照合結果を保存。`e-evidence.json`が全ノーツの元インデックス・拍・位置・担当手、`e-cut-plan.json`が全カットの検証時刻、`e-audit.json`が移植件数と変更点を記録する。再生成は`python Tools/ChartDesign/RushE/build_secret_e.py --output <Stagingフォルダー>`。標準ライブラリのみで動き、参照版のハッシュが違えば停止する。反映時は`chart_e.json`の既存`.meta`を維持する。

ジャケットは元動画の公式サムネイルを `cover.jpg` に保存。横長画像は盤に合わせて中央を円形に表示し、画像自体は加工しない。`build_rush.py` で再生成する場合も、以前の自作画像へ戻さない。

`RushESecretTests`は解除条件に加え、参照ファイルのハッシュ、全1,158ノーツの時刻・配置・金以外の担当手、金ノーツの範囲と数、ロング後の余裕、最後の4ノーツを検証する。`RushESongTests`では位置によらず色から担当手を決めて全カットを実際に判定し、エディター往復でも時刻を保つことを確認。`RushELoadingPlayTests`はEを含む各難易度の読み込み・切り詰めなし・通常/LOWの両手ロング/密集部/終止和音の表示を検証する。画像を保存する場合は`SABER_UI_CATALOG_CAPTURE_DIR`に出力先を指定する。`RushESecretPlayTests`は選曲画面の秘密の入口と復元を確認する。今回の移植後は関連EditMode 23件・PlayMode 3件が全件成功し、6枚の実Game画像も確認した。身体を使った実機試遊は別途必要。

`-batchmode -executeMethod RushESecretPreview.Render -rushEPreviewOutput <出力フォルダー>` で通常画面・タイル・E表示・曲変更後・Eゲーム開始を撮影し、描画メッシュと読み込みを検証できる。2026-10-14に関連EditMode 43件、PlayMode 18件が成功。タイル描画の修正後、解除の実シーンテストと上記の撮影も成功した。
