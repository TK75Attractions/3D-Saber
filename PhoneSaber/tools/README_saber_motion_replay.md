# Saber Motion Replay（実機なしで座標入力を再生）

Unity の既存 `InputPoint` に RED=UDP 5005 / BLUE=UDP 5006 の座標を送る、
Python 3.10 以降・標準ライブラリだけの CLI です。ゲーム、認識処理、シーンの変更は不要です。
既定は `127.0.0.1`、30 fps、両色。Unity で通常のゲームシーンを Play にして使います。
同じ宛先に実機の送信がある場合は停止して、再生座標だけを受信できる状態にしてください。

`Tools/README_mock_bridge.md` の既存モックは UDP 9002 の `IMU:` / `SWING:` 用です。
今回は UDP 5005/5006 のカメラ座標経路を検証するため、PhoneSaber のツールとして
`PhoneSaber/tools/saber_motion_replay.py` に置きました。IMU モックの機能は複製していません。
以下のコマンドは Git / Unity プロジェクトのルートから実行します。

## 合成した動き

```bash
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic slash-left
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic figure-eight --duration 10 --speed 2 --fps 60
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic thrust --color red --repeat 3
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic idle --host 192.168.1.20
```

パターンは `slash-left` / `slash-right` / `slash-up` / `slash-down` / `thrust` /
`figure-eight` / `idle`。`--speed` は合成時は毎秒の周期数、保存トラックでは再生速度倍率です。
slash はその方向へ振った後、次の周期の開始点へ戻ります（戻りも送信します）。
thrust は画面中央付近で投影された棒の長さを伸縮します。深度は既存 payload にないので送りません。
合成座標は左上原点、X は右向き、Y は下向きの 1920×1080 ピクセル（0..1919 / 0..1079）。
RED/BLUE は同じ動きを左右に100 pxずらして生成します。

送信形式は電話と同じ ASCII `x1,y1,x2,y2`、改行や色フィールドなしです。
`--timestamp` のときだけ `ts=<秒、小数6桁>;x1,y1,x2,y2` を送信します。
時刻は再生開始時の Unix 時刻＋再生フレームの生成予定時刻で、人工遅延を加える前の時刻です。
元の recording の時刻を Unix 時刻として送ることはありません。

## Debug Recording triage bundle の抽出（入力は読み取り専用）

受信 inbox は `~/Library/Application Support/PhoneSaber/diagnostics-inbox`。
`extract` の引数は bundle のフルパス、または inbox 内の bundle 名です。
`frames/*.json` の per-frame `red` / `blue`、`detected`、`endpoint`、`timestamp` だけを
再生に必要な形へ整理します。context の重複は frameID ごとに統合し、矛盾があればエラーにします。

```bash
# source-size は撮影時の実際のカメラ画像サイズに合わせる（以下は例）。
python3 -B PhoneSaber/tools/saber_motion_replay.py extract \
  phone_saber_triage_phonesaber_20261006_163444_198 \
  --source-size 480x640 --output /tmp/saber-replay/run.json

# 一つの swing 周辺だけ、CSV 形式で保存する場合。
python3 -B PhoneSaber/tools/saber_motion_replay.py extract /path/to/triage-bundle \
  --source-size 1920x1080 --mirror-x --first-frame 100 --last-frame 130 \
  --output /tmp/saber-replay/swing.csv

python3 -B PhoneSaber/tools/saber_motion_replay.py play --track /tmp/saber-replay/run.json
python3 -B PhoneSaber/tools/saber_motion_replay.py play --track /tmp/saber-replay/swing.csv --speed 0.5 --repeat 2
```

context の `endpoint` は **カメラ画像のピクセル** であり、必ずしも1920×1080ではありません。
`--source-size` を指定すると電話の `scaledPoint` と同じ `(output-1)/(source-1)` の倍率、
整数丸め、`--mirror-x` / `--mirror-y` で1920×1080へ変換します。
電話の Mirror 設定を合わせてください。指定した変換は全フレームに適用します。

`--source-size` なしの場合、その frameID / 色の `udpTransmissions` にある
`sendStarted` / `configuredUDPOutputPixels` の実送信端点を優先します。
この場合は撮影時の UDP Output が1920×1080だったことを確認してください。
送信端点の記録がなく、source endpoint しかない場合はサイズを推測せずエラーにします。
旧 bundle にも明示的な `--source-size` で対応できます。

出力は `--output` 必須、`.json` / `.csv` 対応です。inbox 内、入力 bundle 内、
それらを指す symlink への出力を拒否し、既存ファイルも上書きしません。
元 recording・画像・report は変更しません。track は git 管理外の scratch/output に保存してください。

triage は **全セッションを連続収録したものではなく、選ばれた短い証拠窓** です。
最初の frame の時刻を0秒とし、途中の長い空白をそのまま保存します。
`sample_period` は frameID 差と timestamp 差から推定し、単一 frame では1/30秒です。
再生は指定 fps で直前のサンプルをその1サンプル周期内だけ使い、空白は無送信にします。
未検出 (`detected=false`) や未記録の色にも送信しません。端点補間や認識・予測処理は行いません。
待ち時間を減らすには `--first-frame` / `--last-frame` で一つの窓を選びます。

JSON track の最小例（端点はすでに1920×1080）：

```json
{"version":1,"size":[1920,1080],"sample_period":0.03333333333333333,"duration":0.06666666666666667,"frames":[{"t":0,"red":{"detected":true,"endpoint":[800,600,1000,400]}},{"t":0.03333333333333333,"red":{"detected":false,"endpoint":null}}]}
```

`frame_id` は任意。未記録の色はキーを省略できます。CSV は各行に `t,frame_id,sample_period,duration` と
各色の `detected,x1,y1,x2,y2` を持ち、detected の `0` は未検出、空欄は未記録です。

## 人工遅延・欠落・背景誤検出

```bash
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic figure-eight \
  --duration 10 --latency-ms 50 --jitter-ms 15 --loss 0.05 \
  --dropout 2:200 --dropout 5:200 \
  --false-positive-at 7 --false-positive-frames 4 --seed 42

# ネットワークを一切開かず、予定パケット数と最終送信時刻を確認。
python3 -B PhoneSaber/tools/saber_motion_replay.py play --track /tmp/saber-replay/run.json \
  --latency-ms 100 --loss 0.1 --seed 42 --dry-run
```

- `--latency-ms`: 固定の追加遅延。`--jitter-ms`: 一様分布の±変動。合計は0 ms以上に丸めます。
- `--loss`: 各色の各パケットを独立に捨てる確率（0..1）。
- `--dropout START_SECONDS:DURATION_MS`: 再生開始からの送信予定時刻で区間を判定し、
  その区間の全パケットを捨てます。例 `2:200` は2.0秒以上2.2秒未満の200 ms無送信。
  複数指定可。遅延や jitter を加えたパケットも区間内へ送信しません。
- `--false-positive-at` / `--false-positive-frames`: 指定秒以降の最初の再生 tick からNフレーム、
  中点から離れた画面反対側の隅のランダムな棒端点に置き換えます。
  未検出・空白中にも生成し、`--color` で選んだ色だけに適用します。
  その後に loss / latency / jitter / dropout を適用するので、実際の受信枚数は減る場合があります。
- `--seed`: 同じ入力・fps・設定なら同じ予定パケットが得られます。

`--repeat` はトラックを指定回数繰り返し、impairment の時刻は全繰り返しを通じた再生開始基準です。
遅延パケットは送信予定時刻順に並べ、jitter による逆順到着も再現します。
送信は monotonic clock の絶対時刻で待機します。OS負荷・ネットワーク遅延による実受信時刻の変動は別にあります。
Ctrl+C で終了すると待機中の送信も止まり、socket を閉じます。

## 台別 discovery

```bash
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic slash-right --station A
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic idle --discover
python3 -B PhoneSaber/tools/saber_motion_replay.py play --synthetic idle \
  --station B --discovery-address 192.168.1.255 --discovery-timeout 2
```

`--station` または `--discover` で UDP 5007 へ `PHONESABER_DISCOVER 1` を送ります。
応答 `PHONESABER_UNITY 1 red=... blue=... name=... station=A` の送信元IPとポートを使い、
station 指定時は台名を完全一致で絞ります。候補が0台または複数台ならエラーにして送信しません。
`--host IP` は直接指定（station と併用不可）。`--red-port` / `--blue-port` は必要なときだけ上書きできます。
Unity が Play 中で、discovery が有効なことと同一LANであることが必要です。
`--dry-run` は station 指定があっても discovery を実行しません。

## テスト

```bash
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_*.py'
git diff --check
```

抽出・重複統合・変換・JSON/CSV往復・inbox保護、全合成パターン、seed付き impairment と gap、
payload / routing / monotonic送信待機、台選択を検証します。
実 UDP 試験は localhost の ephemeral port だけです。sandbox が bind を拒否するとその1試験だけ skip し、
mock socket の送信・discovery 試験は実行します。ゲーム内の見た目や判定は Unity Play で確認してください。
