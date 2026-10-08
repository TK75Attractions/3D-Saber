# core-r2（2026-10-09）

Swift `d1d9726` の整数max/minによるvalue/chroma生成と、実述語と同じ先頭ガードを通った画素だけのHSV計算をC++へ移植。HSV・emitterの浮動小数点式、加算順、threshold、候補順、採点、診断、UDPは変更なし。収縮の半径1/2展開と膨張の行run統合も個別に実測したが遅かったため、`detection.cpp` の十字反復を維持した。

MacBook Air M5 / arm64、Apple clang 21、`-O2 -ffp-contract=off`。変更前は `1a5e29d1b3375dea1fa0bc920a03dcafee615091`。`make -C PhoneSaber/android/core benchmark` で各版を別の `BUILD=/tmp/phonesaber-core-r2-{before,scan,erode,dilate,both}` に作成し、同じ `tools/benchmark.cpp` を使用。PNG変換は計測外、既定threshold・step=2、10回warm-up後100フレームの中央値。5巡を before→scan→erode→dilate→both と逆順で交互に実行し、各5回の中央値を集計した。他の検証は測定後に実行。

表の単位はms/frame。erode/dilate/both列にはscanの変更も含む。fixture名は `PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/` 基準。

| fixture | 変更前 | 採用scan | 短縮 | 展開erode | run dilate | 両方 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `blue-led-bright-large-05.png` | 1.488959 | 1.322250 | 11.2% | 1.444333 | 1.524583 | 1.644291 |
| `blue-led-with-left-curtain-reflection-04.png` | 1.820291 | 1.672125 | 8.1% | 1.820666 | 1.926958 | 2.114417 |
| `forensic-20260921/frame_1048.png` | 2.946000 | 2.794875 | 5.1% | 3.014834 | 3.096333 | 3.321959 |
| `lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_89.png` | 3.099250 | 2.949750 | 4.8% | 3.254625 | 3.347333 | 3.639041 |

変更前の再構築例（Git rootから）：

```sh
mkdir -p /tmp/phonesaber-core-r2-baseline
git archive 1a5e29d1b3375dea1fa0bc920a03dcafee615091 PhoneSaber/android/core | tar -x -C /tmp/phonesaber-core-r2-baseline
make -C /tmp/phonesaber-core-r2-baseline/PhoneSaber/android/core benchmark BUILD=/tmp/phonesaber-core-r2-before
make -C PhoneSaber/android/core benchmark BUILD=/tmp/phonesaber-core-r2-final
/tmp/phonesaber-core-r2-before/core-benchmark 100 PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/blue-led-bright-large-05.png
/tmp/phonesaber-core-r2-final/core-benchmark 100 PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/blue-led-bright-large-05.png
```

残り3枚も表のパスを指定し、5組を交互に測定する。今回の変更前の値は `perf_core_detection.md` の過去の値を流用していない。Android実機・JNI・カメラ・無線の速度は未測定。

- `make -C PhoneSaber/android/core test`: PASS（菱形参照比較、core test、Python 4件）。不採用の展開erode版・両方の移植版にも同じcore testを実行しPASS。
- `make -C PhoneSaber/android/core parity`: PASS、269 PNG、541状態遷移、27合成ケースで不一致0、formal 40/40。候補・double bits・endpoint・eligibility・winner・診断を比較。
- 一時的にscan直後の9枚のマップを書き出す変更前/後ハーネスを `/tmp` に作成。全16,777,216 RGB値、threshold 5組（red/blueとも145/25/30、0/0/0、255/255/255、およびred=235/38/255・blue=110/8/10、red=1/255/0・blue=215/17/20）でbyte列が完全一致。
- 公式 `verify_phone_saber.sh` も実行（log/cacheは `/tmp`）。Detection・lossless 40/40・diff checkはPASS。XCTestはCoreSimulatorService接続不可とログ書き込みの `Operation not permitted` によりSimulatorを選択できず未実行。iOS Releaseは `sandbox-exec: sandbox_apply: Operation not permitted` によりSwiftUI macro pluginが起動できずFAIL。
- 公式Toolsは384件、8 failures・33 errors・1 skipでFAIL。HTTP/UDPのsocket bindが `Operation not permitted`、tracking E2Eは空き容量取得値0による `insufficientDiskSpace`、ほかにreceiver起動の終了値2とP2P compiler子プロセス未検出のassertion failureがある。全段PASSとは扱わない。Unityは既存Editorがprojectを使用中で未実行（原因調査・変更なし）。Gradleは今回の対象外で未実行。
- `git diff --check`: PASS。変更範囲は `pipeline.cpp` と本メモのみ。`opt/core-r2` へのcommitを試みたが、`git add` が `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-core-r2/index.lock` の作成を `Operation not permitted` で拒否されたため未commit。変更をそのまま残し、pushしていない。目的・変更・測定・検証を含むcommit messageは `/tmp/phonesaber-core-r2-commit.txt` に保存。
