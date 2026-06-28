# Note-Recorder（譜面エディタ）

本編リズムゲーム用の **譜面を作るツール** です。8×8グリッドにノーツを置き、本編が読める `chart.json` 形式で書き出せます。

---

## 開き方

1. Unity で `Assets/Note-Recorder/Scenes/SampleScene.unity` を開く
2. **Play（▶）を押す**
3. 画面が出たら **自動でヘルプが開きます**（以降は **H** または **F1**、右上の「**? ヘルプ**」ボタンで開閉）

> 操作はすべて Play 中に行います（ノーツの配置などはランタイム動作）。

---

## クイックスタート（最短の流れ）

1. **BPM / OFFSET / 拍子** を入力欄で設定（曲に合わせる）
2. **Start** で再生しながら、または **← →（1拍）/ ,（前小節）.（次小節）** で位置を合わせる
3. グリッドを **左クリックでノーツ配置 / 右クリックで削除**
4. 必要なら種類（Tap/Flick/Long）や向き（テンキー）を設定
5. `ChartManager` の **曲フォルダ名・難易度** を決める
6. **Export（本編形式）** で `StreamingAssets/Songs/<曲>/` に書き出し
7. 本編を Play → 曲選択に出てくるので選んでプレイ

---

## 操作一覧

| 操作 | 内容 |
|---|---|
| 盤面 左クリック | ノーツを置く / 選ぶ |
| 盤面 右クリック | ノーツを消す |
| Start / Stop ボタン | 再生 / 一時停止 |
| タイムライン クリック・ドラッグ | その位置へシーク |
| ← / → | 1拍 戻る / 進む |
| , / .（カンマ / ピリオド） | 1小節 戻る / 進む |
| 拍入力欄 | 数字でその拍へジャンプ |
| Ctrl + Z / Ctrl + Y | 元に戻す / やり直し |
| マウスホイール（Long選択中） | ロングの長さを調整 |
| H / F1 / 「?」ボタン | ヘルプ開閉 |

### ノーツの色

白 = **Tap**、黄 = **Flick**、赤 = **Long**。明るいマスは選択中のノーツ。

### 向き（選択中ノーツにテンキー）

```
7 8 9        ↖ ↑ ↗
4 5 6   =    ← ・ →
1 2 3        ↙ ↓ ↘
```
`5` = 向きなし。Flick で「指定方向に斬る」ノーツに使います。

---

## 保存と本編への出力

- **Save / Load** … エディタ用データの保存・読込（`persistentDataPath/Songs/...`。本編はここを読みません）
- **Export（本編形式）** … 本編が読む形へ変換して書き出し
  - 出力先: `Assets/StreamingAssets/Songs/<曲フォルダ>/`
  - 生成物: `chart.json`（曲一覧表示に必須）＋ `chart_<難易度>.json` ＋ `audio.<ext>`（音源は自動コピー）
  - 呼び出し方: `ChartManager` を右クリック →「本編形式でエクスポート」、または Export ボタン（`SaveLoadUI.OnExportButtonClicked` を割り当て）

### 変換の対応（エディタ → 本編）

| エディタ | 本編 chart.json |
|---|---|
| `startTick` + resolution + bpm | `time`(ms) ＝ 拍 × 60000/bpm |
| `offset`(秒) | `offsetMs` |
| グリッド x,y（0〜7） | ワールド座標（既定 x:±2.5 / y:−1.0〜1.5） |
| type 0/1/2 | `"tap"` / `"direction"` / `"long"` |
| direction 0〜8 | `"none"`〜`"downright"` |
| Long の duration | `count`（必要カット数） |

---

## ChartManager の調整項目（インスペクター）

- **currentSongFolder / currentDifficulty** … 出力先の曲ID・難易度（easy/normal/hard）
- **exportXRange / exportYRange** … グリッド→ワールド座標のレンジ
- **exportLongCutsPerBeat** … ロング1拍あたりのカット数
- **exportFlipY** … 縦の向き。本編で上下が反転していたら切り替える（既定 true＝行0が上端）
- **exportCopyAudio** … エディタ実行時に音源を曲フォルダへ自動コピー

---

## ファイル構成

| 場所 | 役割 |
|---|---|
| `ScriptsCore/ChartManager.cs` | 中核。ノーツ追加/削除、Undo/Redo、保存/読込、本編エクスポート |
| `ScriptsCore/TimeManager.cs` | 再生・拍/Tick・小節表示の時間管理 |
| `ScriptsCore/ChartConvertMath.cs` | エディタ値→本編値の純変換（テスト済み・Unity非依存） |
| `ScriptsCore/ChartExporter.cs` | 本編形式 JSON の組み立て |
| `ScriptsGrid/GridGenerator.cs` / `GridCell.cs` | 8×8グリッドの生成とマス単位の入力・表示 |
| `ScriptsUI/EditorControlUI.cs` | BPM/OFFSET等の入力、移動・Undo/Redo のショートカット |
| `ScriptsUI/TimelineManager.cs` | タイムライン（シークバー＋ノーツ点） |
| `ScriptsUI/SaveLoadUI.cs` | Save / Load / Export ボタンの受け口 |
| `ScriptsUI/EditorHelpOverlay.cs` | 本ヘルプ画面（自動生成・配線不要） |
| `AudioWaveformRenderer.cs` | 音源の波形表示 |
| `BeatNoteData.cs` | エディタ内部のデータ定義 |
| `BeatNoteRecorder.cs` | **旧・簡易版（現在のシーンでは未使用）** |

> 注: `BeatNoteRecorder.cs` は初期の簡易エディタで、いまの `SampleScene` では使われていません（中核は `ChartManager`）。

---

## 注意

- 音源を手動で置く場合は、曲フォルダに `audio.mp3` / `audio.ogg` / `audio.wav` のいずれかの名前で配置してください。
- `chart.json` が無い曲フォルダは本編の曲選択に出ません（Export は必ず1つ用意します）。
