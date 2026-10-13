# PhoneSaber 異常入力・負荷試験

Git root から実行する。seed はすべて `20261013` が既定。ランダム部分は既定120秒、最大180秒。
結果は stdout、relay の資源サンプルとログは `--output` の指定先へ出る。
正式な lossless corpus は読書きしない。通信は `127.0.0.1` の動的ポートだけで、
Bonjour・Codex 解析・GUI・Simulator・Unity Editor/Player を起動しない。
生成 bundle、実行ファイル、試験用 source と host harness の module cache は temporary directory に保存する。

必要なもの: Python 3.12、Mac Command Line Tools (`xcrun swiftc`)。
C# は Unity **6000.3.9f1** の同梱 Mono/compiler を直接呼ぶ（Unity アプリは起動しない）。
外部 Python package は不要。

```bash
python3.12 -B PhoneSaber/tools/robustness/relay_soak.py --seed 20261013 --seconds 120 --output /private/tmp/phonesaber-relay.json
python3.12 -B PhoneSaber/tools/robustness/triage_fuzz.py --seed 20261013 --seconds 120
python3.12 -B PhoneSaber/tools/robustness/receiver_disconnects.py --seed 20261013 > /private/tmp/phonesaber-disconnect.log
python3.12 -B PhoneSaber/tools/robustness/run_host.py csharp --seed 20261013 --seconds 120
python3.12 -B PhoneSaber/tools/robustness/run_host.py swift --seed 20261013 --seconds 120
python3.12 -B PhoneSaber/tools/robustness/log_fuzz.py --seed 20261013 --seconds 120
```

- `relay_soak.py`: 48個の UDP source、ランダム・切断・最大65,000 byte datagram、session/sequence の端値、
  8個の「accept するが読まない」TCP 接続、遅い送信、繰り返す half-close を混ぜる。
  正常 BLUE の転送を毎回照合する。RED の転送先も bind したまま読まない。
  消えた peer を idle timeout 後に同じ session で再接続して確認する。
  `phone_saber_p2p_bridge.py` の build を使用。sandbox で `ps` が使えないため、
  relay source の tmp コピーに自己 RSS (`task_info`)・累積 CPU 秒 (`getrusage`) の出力だけ追加する。
  production source は書き換えない。FD は `lsof` の数値 FD を数える。
  `rss_kib` は常駐メモリで、CPU 平均%は CPU 秒の差 / 経過秒の差 ×100。
  8接続への大容量送信での backpressure は既存 `DiagnosticsRelayTests` が別途確認する。
- `triage_fuzz.py`: PSBT の byte mutation、サイズ・型・filename の mutation、全 EOF 境界、
  同時 retry をメモリ stream で確認する。予期しない例外時は seed、iteration、body hex を出す。
- `receiver_disconnects.py`: 1 PNG/context 付き upload の全 body byte 境界で TCP を half-close。
  毎回400応答と空 inbox を確認し、/health、模擬 ENOSPC、実 HTTP の同時 retry も確認する。
  この列挙試験は180秒で打ち切る。body をすべて受信したケースは retry で確認する。
- `ReceiverCheck.cs`: 実 parser と既存 legacy oracle の bit 比較、detect-only の全受理、
  1秒の ownership 解放、受信カウンタ、timing 上限/期限、silent/back の対応を確認する。
  物理 UDP 上限内の入力を生成し、時計は単調増加とする（実コードの契約）。
- `CodecCheck.swift`: 実 codec と実座標整形関数を host build。SplitMix64 で mutation / round-trip、
  UInt32/UInt64 境界、sequence filter の重複/逆順/30秒期限を照合。
  座標整形は実機の入力範囲（source 1..4096、範囲内 PixelPoint、1920×1080出力）を使う。
  DetectionCore.swift は整形関数のためにリンクするだけで、認識処理は呼ばない。
- `log_fuzz.py`: 64 MiB の非 UTF-8・一行ログを実 latency CLI と status tail 読取りへ渡す。
  その後は途中行・数値破損・巨大な整数をランダムに試す。集計値の `nan` 混入は未修正の既知事項。

最小再現は通常の unittest に残した。認識テストや GUI を含む公式 verify は operator が実行する。

```bash
python3.12 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_robustness_regressions.py -v
python3.12 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_phone_saber_p2p_bridge.py -v
python3.12 -B -m unittest discover -s PhoneSaber/tools -p test_latency_report.py -v
```

`test_nonfinite_block_measurement_does_not_poison_report` は operator 判断待ちの **expected failure**。
現状の誤集計を明示するためで、修正済みの回帰テストを skip するものではない。
負荷が違えば時間内の iteration 数や RSS は変わる。同じ seed で生成順序は再現し、
不具合の回帰は時間に依存しない最小入力で確認する。資源の絶対値に脆い pass/fail 閾値は置いていない。
