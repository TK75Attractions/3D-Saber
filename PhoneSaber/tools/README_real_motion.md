# 実録画による描画端点予測の評価

認識結果の端点を参照軌跡として使うオフライン評価。手でラベル付けした
物理的な正解でも、実測の無線遅延でもない。画像・認識コード・UDP は変更しない。

## 抽出

リポジトリのルートで実行する。元録画は読み取りだけで、出力は `/tmp` へ置く。

```bash
python3 -B PhoneSaber/tools/real_motion_extract.py \
  "$HOME/Downloads" --output /tmp/predict-real-all.csv \
  --manifest /tmp/predict-real-all-manifest.json
python3 -B PhoneSaber/tools/real_motion_extract.py \
  "$HOME/Library/Application Support/PhoneSaber/diagnostics-inbox" \
  --image-width 480 --image-height 640 \
  --output /tmp/predict-real-triage.csv --manifest /tmp/predict-real-triage-manifest.json
```

対応形式:

- Debug Recording `*_metadata.json`: `frames`, `presentationTimeSeconds`,
  `red/blue.{x1,y1,x2,y2,detected,predicted}`。
- triage `frames/*.json`: `frames`, `timestamp`, `red/blue.endpoint`,
  `detected/predictionUsed`。重複する近傍フレームは時刻・色ごとに統合し、
  座標や既知フラグの矛盾があれば中断する。
- 明示した JSON のフレーム配列 / 単一フレーム、および JSONL。
  `captureTimeSeconds`、`endpoints.first/second.{x,y}` も対応する。

`summary.json` / `state` の集計値や forensic / analysis の画像だけからは
時系列を作らない。Downloads の forensic に対応する metadata が本体。
triage の寸法指定は元画像で確認した寸法を渡す。この調査では480×640。
他の録画に同じ寸法を無条件に指定してはいけない。

CSV/JSON は匿名 session ID、相対秒、frame、元画像の pixel 端点、色、
detected/predicted、寸法、時刻フィールド名、label を含む。
欠測端点・未記録フラグは空欄/null。manifest のみ元 session / path を含むため
コミットしない。CSVのframe IDは元値を保つ。コミット対象の sample はframeも相対値。
画像は読まない。入力ファイルを書き換えない。

## 再測定

```bash
# 全録画、現行と977d5c6の比較。全Hで同じ有効フレーム集合を使う。
python3 -B PhoneSaber/tools/endpoint_prediction_eval.py \
  --real /tmp/predict-real-all.csv --horizons 0,20,40,60 \
  --estimators baseline,current,ls3,adaptive --json /tmp/predict-real-after.json

# 最も長い時系列の全整数H掃引 (30/60 Hz)。manifestのsessionで選ぶ。
python3 -B PhoneSaber/tools/real_motion_extract.py \
  "$HOME/Downloads/phonesaber_20260922_130726_609_metadata.json" \
  --output /tmp/predict-real-longest.csv
python3 -B PhoneSaber/tools/endpoint_prediction_eval.py \
  --real /tmp/predict-real-longest.csv --estimators baseline,current \
  --json /tmp/predict-real-full-sweep.json

# 古い録画の未記録 predicted フラグを除外した感度分析。
python3 -B PhoneSaber/tools/endpoint_prediction_eval.py \
  --real /tmp/predict-real-all.csv --require-known-prediction \
  --horizons 0,20,40,60 --estimators baseline,current \
  --json /tmp/predict-real-known-after.json

# 因果的な近傍端点対応の診断のみ。実装にはこの入替を追加していない。
python3 -B PhoneSaber/tools/endpoint_prediction_eval.py \
  --real /tmp/predict-real-longest.csv --align-endpoints \
  --horizons 0,20,40,60 --estimators baseline,current \
  --json /tmp/predict-real-aligned-after.json

python3 -B PhoneSaber/tools/check_endpoint_predictor_parity.py --before-ref 977d5c6
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_*.py' -v
```

30 Hzの録画から新しい60 Hzの正解を捏造せず、30/60 Hzの**描画**を再現する。
受信時刻はcapture+140 msと仮定し、未記録のジッタを追加しない。
位相0/0.25/0.5/0.75フレームを平均する。各描画では最新packetのみ適用。
捕捉失敗・iPhone予測済み・50 ms超の穴をまたがず、各区間で履歴を初期化する。
参照の間は線形補間なので20 msの正解にも補間誤差がある。

`--world-width 11 --world-height 6` は正規化座標から既存のworldScale=(5.5,3)へ
移す標準写像の仮定。台固有校正、感度、Unity親transformを復元した値ではない。
移動上限0.35 world unitsはC#と共通。寸法/写像を変えると上限の効き方も変わる。

未来RMSEはcapture+Hを目標にし、OFFも**同じ目標**で比較する。
live RMSEは仮定受信遅延を含む描画時刻を目標にし、全Hで同じ集合を使う。
折り返しは各端点のX/Yの符号反転、前後100 ms窓。ノイズも含む。
絶対はみ出しと、OFFのはみ出しを引いた非負の追加はみ出しを別々に報告する。
OFFの絶対はみ出しは近接した複数の極値のため0とは限らない。
全体/録画/色別の観測数、RMSE、p95、最大値を出す。

`baseline` は元の2区間最小速度、`current` は減速比を加えたC#と同じ推定。
`ls3` は不等間隔3点最小二乗、`adaptive` はspeed/2の上限1で予測を縮める
研究用候補であり、C#に採用していない。

共有ベクトルは固定seed977の不等間隔/ギャップ/反転に、OFFの符号付きゼロ、
無効値、上限、停止、resetを含む。PythonはVector2の各操作でfloat32へ丸める。
新旧の実際のC#を.NET SDKのC#9と最小Vector2 stubでコンパイルし、期待ビットと比較。
同じCSVをUnity EditModeテストも読む。.NETの一致はUnity/Mono/IL2CPPでの実行結果ではない。

`fixtures/real_motion_sample.csv` は最長録画の約4秒・赤青240行だけを匿名化した
約26 KBの再現テスト用標本。全録画の代わりにこの小標本を根拠にチューニングしない。
