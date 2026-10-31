# 曲別音量補正（2026-10-14）

> 2026-10-30 から、本編・試聴・譜面エディターが鳴らすのは下の「前もって大きくした再生用の音源」(`master.ogg`)。
> この節の −18 LUFS の補正(当時の `loudness.json` の `gainDb`・`logic/normalized.wav`)は、v2.5 の大きさ
> (`master_report.json` の `v25Lufs`)を測った元の記録として残す。

収録音源14本（選曲対象13曲＋NewSongテンプレート）を、曲全体の知覚音量 **−18 LUFS** にそろえる。
FFmpegの `loudnorm` で全編を測定した結果、補正前は −25.27〜−6.13 LUFS（19.14 LU差）、
適用後は **−18.01〜−17.99 LUFS**。トゥルーピークは全曲 **−1.00 dBTP以下**。
曲内の静かな部分・盛り上がりや、既存の試聴フェード／サビ演出は維持する。

## データと再生経路

- 各曲フォルダーの `loudness.json` に原音のSHA-256、測定値、再生ゲインを保存。
- 13音源は固定の減衰だけを適用し、元の `audio.*` をそのまま読む。
- `logic!!!` は単純な増幅ではピークが上限を超えるため、2パスのラウドネス補正と
  ピーク制限を施した `logic/normalized.wav` を再生する。原本 `logic/audio.mp3` は保持。
  出力は原音と同じ44.1kHz・ステレオ、PCM16。先頭・末尾とも削らず、無音も追加しない。
- 本編は `SongPlayer.SetLoudnessGain`、試聴は既存フェードに掛ける係数として適用。
- 譜面エディタは所有するデコード済みPCMに1回だけ適用。サンプル数・周波数は変更しない。
  クリック音・効果音・ユーザーの音量設定は、この曲別補正の対象にしない。
- `SongLoudness` が3経路の情報を共有する。SHA-256はファイル情報が変わった時だけ再検証。
  音源取り込み時はサイズ／更新時刻が同じでもキャッシュを無効化する。
- 原音差し替え、未設定、壊れたメタデータ、欠損した加工済み音源は原音・ゲイン1へ戻す。
  音源を差し替えた際は補正も再生成する。古い加工済み音源を別の曲に適用しない。

## 検証

- Unity 6000.3.9f1: 関連EditMode 55件、PlayMode 13件が全て成功。
- `report.json`: 全編の補正前・補正後LUFS、トゥルーピーク、元音源と派生音源のSHA-256。
- `timing-check.json`: logicの原音・補正後とも **9,118,080フレーム**。
  10 / 60 / 100 / 150 / 200秒の相互相関は全て **時間差0サンプル**。
- `SongLoudnessTests`: 元音源の照合、派生音源欠損／復元、差し替え時の無効化、
  エディタのステレオ・半端な末尾・キャッシュ、全曲メタデータ、Unity内のlogic波形同期。
- `SongAudioLoadingPlayTests`: 本編の再読み込み、補正のない曲への切り替え、試聴フェードと再試聴。
- 既存の音源取り込み・サビ演出・5曲の本編ロード・選曲同期・曲時計も回帰検証する。
- 元の音源ファイルと全譜面JSONは変更していない。

## 再生成

Python 3とFFmpegを使用。候補フォルダーへ生成し、検証後に各曲のファイルを反映する。

```text
python Tools/Audio/SongLoudness/build_loudness.py --songs Assets/StreamingAssets/Songs --output <候補フォルダー> --ffmpeg <ffmpegのパス>
```

`audio.ogg` → `audio.wav` → `audio.mp3` の、本編と同じ優先順で測定する。
補正後の音量／ピークが許容値を外れた場合は検証エラーとなり、自動で本編を上書きしない。
`report.json` は資料用、曲フォルダーへ置くのは各曲の `loudness.json` と必要な `normalized.wav` のみ。

## 前もって大きくした再生用の音源(2026-10-30、`build_master.py`)

v2.5 で曲を +12 dB(MusicBoost)にしたところ、出口の MasterLimiter(先読みなし・その場で下げて 150 ms で戻す)が
山の立ち上がりと低音の1周期ごとに頭を平らに削り、音割れした。MusicBoost はやめ、曲ごとに前もって大きくした
`master.ogg`(難易度ごとの音源は `master_<難易度>.ogg`)を `loudness.json` の `playbackFile` で鳴らす(`gainDb` は 0)。
原音 `audio.*` と譜面は変えない。`logic/normalized.wav` は使わなくなったので消した。

- 処理(オフライン・遅れ0、出力[n] = 入力[n] × 倍率[n]): FFmpeg で float にデコード(周波数・チャンネルはそのまま)→
  30 Hz のゼロ位相ハイパス → 遅いレベラー(1.5 秒の窓の大きさが曲全体から 2 dB を超えて離れた分を 3:1 に縮める。
  下げ・上げとも 6 dB まで、曲全体より 15〜25 dB 下は上げない)→ 先読みリミッター(左右連動、4倍オーバーサンプルの真のピーク、
  先読み 5 ms・保持 5 ms、戻りは速い方 25 ms と、下げ幅の 7 割を 30 ms で追って 300 ms で戻る遅い方の2段)→
  libvorbis q8。山は削らない(先読みの平均で、天井を超えない倍率にする)。
- 大きさの決まり: v2.5 で実際に鳴っていた大きさ(loudness.json の倍率 × +12 dB → MasterLimiter を C# と同じ式で通して測った値)を
  下回らない。-8 LUFS より小さい曲は、リミッターの下げ幅が 95% の時間で 7.5 dB に収まる範囲で -8 LUFS へ上げる。
  天井の内側で v2.5 の大きさに届かない曲(青葉騒。v2.5 は削れていなかったが真のピークが +0.2 dBTP)は下げ幅 2 dB まで。
  割れずに -7 LUFS に届く曲はほとんど無かった(v2.5 は出口で削って大きく聞こえていた)。
- 天井: 処理は真のピーク -1.5 dBTP。ogg にしたあと真のピーク -1.0 dBTP 以下・サンプルの最大 0.89 以下
  (出口の MasterLimiter の閾値 0.891 に触れない)を確かめ、超えたら天井を下げて作り直す。
- 時間: FFmpeg でデコードし直したフレーム数が原音と同じこと、左右の和の差分の相互相関の山が 5 か所とも時間差 0 であることを確かめる。
  Unity の中では `SongLoudnessTests.MasteredSongsHaveTheSameAudioTimelineInUnity` が全曲で確かめる。
- 練習音源(`Assets/Resources/Audio/Tutorial_Loop120.wav`)も同じ道具で上げた(`--tutorial`)。ループを3つつなげて処理し真ん中を取る。
  拍がはっきり聞こえる決まり(`TutorialMusicTests` の、各拍の立ち上がり > 余韻の2倍)を余裕つきで満たす一番大きいところ
  (遅い戻りは 9 割・0.6 秒)。.meta(インポート設定)は変えない。
- `master_report.json`: 曲ごとの v2.5 の大きさ・削れたサンプル数、新しい大きさ・LRA・真のピーク・サンプルの最大・下げ幅(50/95/99%・最大)・
  時間差・ファイルの大きさ。

```text
python Tools/Audio/SongLoudness/build_master.py --songs Assets/StreamingAssets/Songs --output <候補フォルダー> --ffmpeg <ffmpeg> --ffprobe <ffprobe> [--apply]
python Tools/Audio/SongLoudness/build_master.py --songs Assets/StreamingAssets/Songs --output <候補フォルダー> --tutorial Assets/Resources/Audio/Tutorial_Loop120.wav --apply
```

Python 3 に numpy・scipy・numba・soundfile、FFmpeg は libvorbis 入り(gyan.dev の full build など)。`--apply` は確かめたあとで
`master*.ogg` と `loudness*.json` を曲フォルダーへ置く。作り直すときは、v2.5 の測定を前の `master_report.json` から読む。
音源を差し替えたときは、`build_loudness.py` で `loudness.json` を作ってから `build_master.py`。練習音源の `--tutorial` は
渡したファイルを上書きするので、原本(git の版)に対して1回だけ。

### 大きい版(2026-10-30 夜に用意、2026-10-31 夜 v2.10 からゲームに入れた)

ユーザー「それよりもっと大きくしたバージョンの音源だけ用意しといて(今後実装するとなったらすぐできるように)」。
10/31 夜、ユーザー「曲の音量もう少しだけあげれない？」で、下のコマンド(`--workers 1`)で全曲を作り直して入れた
(2:23 AM は v2.8 の短い音源と Y の長い音源、ElDorado は G も)。今の音源と比べた台B のスピーカーでの大きさ(150 Hz のハイパス後)は
+0.8〜+2.9 dB(2:23 AM +2.8・Y +2.9、escapade・Morning +2.0、Manic Sonic +1.8、アンダルシア +1.5、ElDorado +1.1・G +1.3、
校歌 +1.3、揺籠 +1.1、RushE +0.9、logic・勉強・製作中・Phobia +0.8〜0.9)。全帯域の LUFS も全部上がった(+0.3〜+1.7)。
**青葉騒だけは入れていない**(大きい版は全帯域で -0.3 LU 下がり、スピーカーでも +0.4 dB だけ。もともと一番大きい曲なので v2.6 のまま)。
`master_report.json` は入れた版(青葉騒の行は v2.6 のまま)。真のピークは全部 -1.0 dBTP 以下、サンプルの最大は 0.889 以下、時間差は 5 か所とも 0。
このリミッターは割れない代わりに、上げても大きさが頭打ちになる(校歌は -7.1 LUFS、2:23 AM は -7.3 LUFS あたりで、下げ幅だけが増える)。
それより大きくするために、2つを足した。

- ハイパスを 130 Hz に上げる。台B のスピーカーは 150 Hz 未満が出ないので、出ない低音で天井を使わない。
  低音の多い曲(Manic Sonic・2:23 AM・Morning・escapade)ほど、スピーカーで聞こえる大きさが上がる。
- 山の角を丸める(`--softclip-margin-db`)。4倍オーバーサンプルで、曲全体 + N dB を超える所をなめらかに曲げる。
  割れ(平らな頭)にはならないが、N が小さいほど音の角が取れて、ざらつきが出うる。

```text
python Tools/Audio/SongLoudness/build_master.py --songs Assets/StreamingAssets/Songs --output <候補フォルダー> --ffmpeg <ffmpeg> --ffprobe <ffprobe> --uniform-target -6.0 --gr95-cap 10 --highpass-hz 130 --softclip-margin-db 8
```

- `--uniform-target`: 全曲をこの大きさへ上げる。ただしリミッターの下げ幅(95% の時間)が `--gr95-cap` に達したらそこで止める。
- 大きい版は候補フォルダーにだけ作る(`--apply` とは一緒に使えない)。報告の `speakerLufs` は、150 Hz のハイパスを掛けてから測った、台B のスピーカーでの大きさの目安。
- 候補フォルダーに作ってから、各曲の `master*.ogg` と `loudness*.json` を曲フォルダーへ上書きし、`SongLoudnessTests` を流してからビルドする
  (コードの変更は要らない。10/30 の4曲だけの試しは、この PC の `C:\Users\shike\dev\3D-Saber-louder\` に残っている)。
- 長い曲は1本で数 GB のメモリを使う。ほかの重い作業と重なるときは `--workers 1`。
