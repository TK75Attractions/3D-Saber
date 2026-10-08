# unity-predict（2026-10-09）

短時間の端点予測を SaberInputBridge に追加した。初期値 H=0（OFF）。F8 で 0 / 20 / 40 / 60 ms を選択し、台名で正規化した PlayerPrefs キーへ保存する。RED / BLUE 共通。まず20 msで実機確認する。

直近3点の2区間について、同符号の成分の小さい速度を使う。端点の移動上限0.35 world単位、100 ms超の受信ギャップで履歴リセット、受信後33.33〜100 msで予測量をゼロへ減衰。受信の古さはHに足さない。Unityが実際に適用したサンプルのUDP受信monotonic時刻を使い、間引かれたpacketをキューで追いかけない。入力源・棒番号・設定・位置補正の変更、無効化／無効入力／単点／マウスへの遷移で履歴を捨てる。

OFFでは予測計算を呼ばず、既存の座標演算順を維持する。単点とマウスは予測対象外。Camera/IMU用履歴にはOFFと同じ写像・クランプを通した予測前の端点を渡す。F9の受信測定も変更していない。

受信経路では、有効端点フラグを座標・時刻と同じmain-threadスナップショットで公開するよう修正した（次packetのフラグだけが先行する競合を解消）。この修正はHに関係なく有効。IPEndPoint初期値のpacketごとの生成と、timestamp prefix判定の配列割当も削減した。parserの受理条件、数値演算、UDP本文・RED 5005 / BLUE 5006 / discovery 5007は変更していない。

## Mac上の再現計測

環境: Darwin arm64、Python 3.13.11、.NET SDK 9.0.109。比較元は `b7c896f84c5dadad08df4a7529bc3b5d3d661d60`。

```sh
python3 -B PhoneSaber/tools/endpoint_prediction_eval.py --json /tmp/prediction-30.json
python3 -B PhoneSaber/tools/endpoint_prediction_eval.py --render-hz 60 --json /tmp/prediction-60.json
python3 -B PhoneSaber/tools/benchmark_unity_prediction.py --before-ref b7c896f84c5dadad08df4a7529bc3b5d3d661d60
python3 -B -m unittest discover -s PhoneSaber/tools -p 'test_*.py' -v
PHONESABER_VERIFY_LOG_DIR=/tmp/unity-predict-verify-after bash PhoneSaber/tools/verify_phone_saber.sh
```

合成条件: 正弦／急な折り返し、振幅2.2 / 3.0 world単位、周期1.2 s、12 s、送信30 / 60 Hz、描画30 Hz（変更可能）、固定遅延140 ms、受信ジッター±6 ms、seed 20261009。以下は送信・描画30 Hzの正弦運動。数値はworld単位。

| H (ms) | 未予測→予測: 撮影時刻+Hの端点RMSE | 描画時刻の真値に対するRMSE | 折り返し最大行き過ぎ |
|---:|---:|---:|---:|
| 0 | 0.000000→0.000000 | 1.573630 | 0.000000 |
| 20 | 0.194969→0.052460 | 1.435892 | 0.059897 |
| 40 | 0.389395→0.155693 | 1.358974 | 0.165371 |
| 60 | 0.582736→0.329784 | 1.346456 | 0.270845 |

H=60の描画時刻RMSEは、正弦60 Hzで1.425642→1.192013、swing30 Hzで1.325578→1.120017、swing60 Hzで1.202971→0.987054。対応する最大行き過ぎは0.247777 / 0.350000 / 0.227778。全H・全条件のRMSE、p95、折り返しの観測数は評価器のJSONに出力する。

`future_rmse` は最後の適用サンプルの撮影時刻+Hに対する誤差。`off_future_rmse` は同じ未来時刻に対する未予測端点の誤差。`live_rmse` は全H共通の描画時刻の真値に対する誤差。行き過ぎはサンプルから検出した軸別折り返しの前後100 msで極値を外へ超えた距離。Pythonはfloat64モデルで、Unity float32のbit parity検証器ではない。実機のglass-to-receive 130〜150 msを再計測した値ではなく、F9の数値を下げる機能でもない。

CSVは `--csv /tmp/truth.csv` でリプレイできる。列は `capture_s,receive_s,ax,ay,bx,by`、時刻は同じ時計の秒で単調増加、座標はworld XYの真値。真値は線形補間し、範囲外の評価点は除外する。認識出力だけの記録を真値とみなさない。

timestamp prefix処理単体（実際の新旧C#を抽出、7回×100万件、Release、tiered compilation無効）の中央値は **13.106→5.439 ns/件**、割当は **51.556→11.556 bytes/件**（−40 bytes/件）。49ケースで旧版と文字列が完全一致。全UDP経路、ソケット、描画、物理遅延の測定とは区別する。

## 検証と制限

- Python `PhoneSaber/tools` 全52件PASS（新規8件を含む）。
- 実際の予測C#をRoslynでコンパイルし、最小Vector2 stubでOFFの符号付きゼロbit、等速、上限、ギャップ、折り返し、減衰のsmokeがPASS。Unityのコンパイル／NUnit実行ではない。
- 新規EditModeテスト17ケースを用意（予測、台別保存、色別スナップショット、prefix、予測前Camera履歴）。Unity Editorの実行は依頼どおり未実施。共通検証のprocess検出も対象プロジェクトをEditorが所有していると報告した。
- 新規script用.meta 3件のGUID重複なし。既存.meta／scene／prefab／game・chartコードは無変更。`git diff --check` PASS。
- 共通検証を前後2回実行。両方でDetection PASS、formal lossless **40/40 PASS**。iOS認識コード・定数・順位・削減順・診断は無変更で、Set/Dictionary順序を演算へ持ち込む変更もない。
- 両回のiOS XCTestは実行不可: sandboxでCoreSimulatorService接続が無効、ログ書込も `Operation not permitted`、Simulator UDIDを取得できない。
- 両回のiOS ReleaseはFAIL: `sandbox-exec: sandbox_apply: Operation not permitted` に続きSwiftUIMacros.StateMacroのexternal macro応答が不正となる。対象Swiftファイルは無変更。
- 共通スクリプト内の既存iOS Tools Pythonは両回 **384件、failures=1 / errors=1 / skipped=1**。tracking E2Eは `insufficientDiskSpace(required: 1040187392, available: 0)` で停止。P2P launcherの `compiler child started` 検出が失敗（原因未特定）。スコープ外のため修正せず、全項目PASSとは報告しない。

詳細ログは `/tmp/unity-predict-verify/20261009-003105` と `/tmp/unity-predict-verify-after/20261009-004343`。実機での行き過ぎ・切断判定と、Unityのコンパイル／EditModeテストは運営側確認が残る。

コミットは未完了。`git add` が `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-unity-predict/index.lock` を作成できず `Operation not permitted` で拒否された。提供された書込可設定に反して、現在のsandboxではGit indexを更新できない。全変更は `opt/unity-predict` の作業ツリーに残る。pushは未実施。コミットメッセージは `/tmp/unity-predict-commit-message.txt` に用意した。
