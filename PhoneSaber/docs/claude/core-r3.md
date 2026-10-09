# core-r3（2026-10-09）

Android C++ core の bit 一致を保つ第三弾。BFS の点列を FIFO と兼用して画素ごとの除算と二重キューを除去。採点では同じ binary64 投影・axial bin を再利用し、最大12要素の作業配列を stack に移した。白画素の菱形13近傍は従来の y→x 順で列挙し、内部画素の境界判定をまとめた。robust body は全点保持時のコピーを省略。core-line は非支持画素を投影前に除外し、votes・neighborhood・inliers・proposal bitmap を候補間で再利用。抑制用 snapshot は Candidate だけを複製し、不要な支持点コピーも除去。

浮動小数点の式・加算順、BFS/候補/同点順、threshold、ranking、eligibility、診断、UDP は維持。`-O2 -ffp-contract=off` は変更なし。フレームをまたぐ static scratch・unordered container は導入していない。

Mac M5 / arm64、Apple clang 21。変更前は `13a38170225ecd70cd353bd35d308f6402bd671f`（r1/r2 を含む）。同じ `tools/benchmark.cpp`、BGRA・step=2・既定threshold、PNG decode/変換は計測外。10回 warm-up 後200フレームの中央値を、fixture ごとに before→after / after→before 交互で7組取得し、その7値の中央値を集計。検証・profiler の終了後に速度を測定した。過去ラウンドの測定値は流用していない。

fixture は `PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/` 基準。全て480×640。MBは10^6 bytes、allocationは C++ `new/new[]` の回数と要求byte数（libc内部の malloc は計数外、30フレーム平均）。

| fixture | 前 ms | 後 ms | 短縮 | allocation 前→後 / frame | 要求MB 前→後 / frame |
| --- | ---: | ---: | ---: | ---: | ---: |
| `blue-led-bright-large-05.png` | 3.491333 | 3.217083 | 7.9% | 2344→757 | 7.13→4.71 |
| `blue-led-with-left-curtain-reflection-04.png` | 4.456042 | 3.462750 | 22.3% | 2631→905 | 8.44→6.15 |
| `forensic-20260921/frame_1048.png` | 7.300250 | 6.140584 | 15.9% | 7220→2049 | 12.97→7.77 |
| `lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_89.png` | 7.272542 | 6.250250 | 14.1% | 6334→1590 | 13.65→8.61 |

試行中央値には幅があった（上から順に、前2.925–3.557 / 後2.866–3.289、前3.716–4.471 / 後3.024–4.109、前6.243–7.463 / 後4.830–6.815、前5.934–7.999 / 後5.537–6.482 ms）。Android実機・rotation/JNI・60fps達成は未測定。

`sample PID 10 1` はプロセス検査を拒否。`xctrace record --template 'Time Profiler'` は `~/Library/Caches/com.apple.dt.InstrumentsCLI/path_manager` への書き込みが `Operation not permitted` で失敗。そのため計測専用 `sampling-benchmark` を追加し、1ms の SIGALRM で中断PCを採取、`atos` で復号した（macOS/arm64限定、productionへの組み込みなし）。各fixture 2000フレーム、変更前45,558 samples中 BFS 16.1%、採点/body 26.9%、core-line 呼び出し行11.4%。変更後47,762 samplesでも BFS 16.3%、採点/body 27.2% が主処理として残る。inliningされた関数は呼び出し元への帰属を含み、sample比率は速度計測値ではない。

Git root での再測定：

```sh
mkdir -p /tmp/phonesaber-core-r3-source
git archive 13a38170225ecd70cd353bd35d308f6402bd671f PhoneSaber/android/core | tar -x -C /tmp/phonesaber-core-r3-source
make -C /tmp/phonesaber-core-r3-source/PhoneSaber/android/core benchmark BUILD=/tmp/phonesaber-core-r3-before
make -C PhoneSaber/android/core benchmark allocation-benchmark sampling-benchmark BUILD=/tmp/phonesaber-core-r3-final
r3_root=PhoneSaber/ios/PhoneSaberSenderTests/Fixtures
r3_fixtures=(
  "$r3_root/blue-led-bright-large-05.png"
  "$r3_root/blue-led-with-left-curtain-reflection-04.png"
  "$r3_root/forensic-20260921/frame_1048.png"
  "$r3_root/lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_89.png"
)
python3 -B PhoneSaber/android/core/tools/benchmark_compare.py /tmp/phonesaber-core-r3-before/core-benchmark /tmp/phonesaber-core-r3-final/core-benchmark --iterations 200 --rounds 7 "${r3_fixtures[@]}"
/tmp/phonesaber-core-r3-final/core-allocation-benchmark 30 "${r3_fixtures[@]}"
python3 -B PhoneSaber/android/core/tools/sampling_profile.py /tmp/phonesaber-core-r3-final/core-sampling-benchmark --iterations 2000 --output /tmp/phonesaber-core-r3-profile "${r3_fixtures[@]}"
```

変更前のallocation/profilerは、現行Makefileの同targetに `SOURCES` として archive した `src/detection.cpp src/pipeline.cpp src/frame_processor.cpp` の絶対パスを渡して構築する。CSVには各試行値も出力される。今回の生ログ・生成物は `/tmp/phonesaber-core-r3-*` に保存し、commitには含めない。

- `make -C PhoneSaber/android/core test`: PASS（独立FIFO参照との3,840ケース比較を追加、既存morphology/rotation/支持判定/core test、Python4件）。
- `make -C PhoneSaber/android/core parity`: 最終版PASS、269 PNG・541状態遷移・27合成ケースすべて不一致0、formal 40/40。全候補・保存double bits・端点・eligibility・winner・診断を比較。
- 同じtestを `-O1 -g -ffp-contract=off -fsanitize=address,undefined -fno-omit-frame-pointer` で実行しPASS。
- 変更前/最終版CLIの追加比較：seed `0x51ab3` の240 rawフレーム、BGRA/RGBA、step 1/2/3/9、stride padding 0/1/13、画像端・白芯・赤青・ランダム画素で不一致0。
- 公式 `verify_phone_saber.sh`（ログ/cacheは `/tmp`）：Detection、lossless 40/40、diff check はPASS。XCTestはCoreSimulatorService接続不可・ログ書き込み拒否で未実行。iOS Releaseは `sandbox-exec: sandbox_apply: Operation not permitted` によりSwiftUI macro pluginが起動できずFAIL。Toolsは384件、8 failures・33 errors・1 skipでFAIL：HTTP/UDP bind拒否、空き容量取得値0による `insufficientDiskSpace`、receiver終了値2、P2P compiler子プロセス未検出等。全段PASSではない。Unityは既存Editorがprojectを使用中で未実行（調査・変更なし）。Gradle/Android実機は対象外で未実行。
- 自己レビュー・`git diff --check`: PASS。変更は `PhoneSaber/android/core/**` と本メモのみ。`opt/core-r3` へのcommit準備後、`git add` が `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-core-r3/index.lock` の作成を `Operation not permitted` で拒否。指定上は共有Gitディレクトリが書き込み可能だが、実際のsandboxでは拒否されたため未commit。変更を保持し、pushしていない。目的・変更・数値・検証を含むcommit本文は `/tmp/phonesaber-core-r3-commit.txt`、変更全体のpatchは `/tmp/phonesaber-core-r3.patch` に保存。
