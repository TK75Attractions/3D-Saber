# スマホ用 譜面ライブラリ

https://saber-chart-library.metuji48.chatgpt.site

曲と難易度を選ぶだけで、現在のゲームの譜面を音付きで確認する閲覧用サイト。所有者専用の Sites として配信するため、スマホでも同じ ChatGPT アカウントで開く。

2026-10-14 時点で 14 曲・43 譜面。Manic Sonic / escapade / logic!!! / 勉強（Remaster）/ 青葉騒の作り直した 15 譜面を先頭に配置し、Rush E の E も収録。譜面のない NewSong は一覧に含めない。既定難易度のコピーである chart.json は重複掲載しない。

## 機能

- 0.25 / 0.5 / 0.75 / 1 / 1.25 倍再生、シーク、5 秒送り戻し、区間ジャンプ。
- 縦・横表示と拡大、曲名検索、更新した 5 曲への絞り込み。
- 青・赤・金、8 方向のフリック、ロングの残り回数を表示。
- 曲・難易度・時刻のリンク共有。例: `?song=RushE&difficulty=e&t=65`。
- 音源は曲ごとに読み込み、全曲を起動時にダウンロードしない。

## 再生成

Python 標準ライブラリと FFmpeg / FFprobe を使用。出力先は Sites 専用 checkout を指定する。

```powershell
python Tools/MobileChartLibrary/build.py --output <site-checkout> --ffmpeg <ffmpeg.exe>
node --test Tools/MobileChartLibrary/tests/*.test.js
```

`build.py` は StreamingAssets/Songs を列挙し、難易度 JSON をバイト単位でそのままコピーする。SHA-256、件数、表示難易度、BPM を catalog.json に記録。音声は loudness.json の playbackFile / gainDb に従い、時間を切らずに AAC 128 kbps へ変換する。元音源・元譜面は編集しない。音源ハッシュと変換設定が一致する場合は変換済みファイルを再利用する。

## 表示と時刻

ノーツ時刻は `(time + offsetMs) / 1000`。BPM / beat からノーツを再計算しない。ロングは lengthMs、未指定時は `(count - 1) * 0.7` 秒を使用し、途中への移動でも時刻から残り回数を求める。

カメラは Unity の SaberChartPlaybackPreview と同じ位置 `(0, 2.35, -7)`、俯角 12 度、垂直画角 60 度。判定面 z=0、生成面 z=20、標準先読み 1 秒、ロングの滞留移動 1 を使う。縦画面は判定面全体を収める投影倍率にする。背景・描画エフェクトは軽量な Canvas 2D による簡易表示であり、Unity 本体の動画ではない。音声の再生時刻に合わせて描画する。

## 検証（2026-10-14）

- 時刻オフセット、可変テンポ、ロングの巻き戻し、8 方向・金・同時押し、画面内への投影を Node の 4 テストで検証。
- Edge のモバイル表示で全 43 譜面の読み込み、件数、コピー元ハッシュ一致を検証。
- 14 音源すべての再生、0.5 倍再生、シーク、前後移動、検索、絞り込み、直接リンクを検証。
- 360 / 390 / 812 / 1280 px の画面幅で表示確認。ブラウザ例外・HTTP エラーなし。
- iPhone / Android 実機での再生確認は未実施。

ブラウザ検証記録はタスク workspace の `Tools/MobileChartLibrary20261014/qa/`。公開物は `Outputs/saber-chart-library-20261014/`。Sites の source helper を通して保存・配信する。
