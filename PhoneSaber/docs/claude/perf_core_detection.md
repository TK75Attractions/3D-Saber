# Android recognition core の高速化（2026-10-08）

MacBook Air M5 / arm64、Apple clang 21、`-O2 -ffp-contract=off` のまま、明るい480×640の4枚で **46.8〜59.9%短縮**。変更前は `86d53d14c61a6e8d2a7ffe1bee75991f899e6845`。

| fixture（`ios/PhoneSaberSenderTests/Fixtures/` 基準） | 変更前 ms/frame | 変更後 ms/frame | 短縮 |
| --- | ---: | ---: | ---: |
| `blue-led-bright-large-05.png` | 12.004 | 4.953 | 58.7% |
| `blue-led-with-left-curtain-reflection-04.png` | 12.962 | 6.113 | 52.8% |
| `forensic-20260921/frame_1048.png` | 17.301 | 9.211 | 46.8% |
| `lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_89.png` | 25.087 | 10.057 | 59.9% |

## 測定と変更

`tools/benchmark.cpp` はPNGデコード・BGRA変換を計測外に置き、既定threshold・sample step=2で `FrameProcessor::process(PixelBuffer)` を測る。10回warm-up後に100フレームの中央値を取り、before/afterの実行順を交互にして5組測定。表は各5回の中央値。JNI・カメラ・無線は含まない。

```sh
make -C PhoneSaber/android/core benchmark
/tmp/phonesaber-cpp-core/core-benchmark 100 PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/blue-led-bright-large-05.png
```

変更前の一時コピーにだけ関数単位の時計を入れてprofileしたところ、bright-large-05では膨張・収縮が約44%、採点が約25%。時計はproductionには追加していない。

- 菱形の膨張・収縮を等価な半径1の十字の反復で計算。連続したbyte配列の処理にし、境界のゼロ消去も維持。
- clipped-whiteの重複判定をbbox+半径2のbitmapへ変更。line点は整数vectorのsort/uniqueで同じ行優先順を保ち、近傍の所属判定をbitmapへ変更。
- `Evidence` の5枚のmaskをコピーせずconst参照で保持。
- 浮動小数点式・加算順・PCA・threshold・ranking・診断値・FrameProcessorの状態遷移は変更なし。Swift/iOS、Unity、Kotlin、UDP、JNI interface、compiler flagsにも変更なし。

## 検証と未完了事項

- `make -C PhoneSaber/android/core test`: PASS。小画像の全列挙と疎密・非binaryマスクを含む参照菱形比較（半径0〜4）もPASS。
- `make -C PhoneSaber/android/core parity`: 269 PNG（formal 35 + inbox original 234）、全候補・double bits、541状態遷移、27合成ケースで不一致 **0**。formal **40/40**。
- lossless単独再実行: **40/40**（Swift module cacheを `/tmp` に指定）。ASan/UBSanのcore testとfixture 52 PNGもPASS。`git diff --check`: PASS。
- NDK 30 / arm64-v8a / API 26 / Debug のCMake build: coreとJNI `.so` のcompile/linkがPASS（`-O2 -ffp-contract=off`）。
- 指定のGradle `:app:testDebugUnitTest :app:assembleDebug` は **未検証**。sandboxが `~/.gradle` のlock書き込みを拒否。cacheを `/tmp` へコピーした再試行も、Gradle内部のソケット作成が `Operation not permitted` となり起動不可。
- `tools/verify_phone_saber.sh` は全段PASSには至らず。DetectionはPASS、Simulator/XcodeとPythonのsocket依存テストはsandbox制限、losslessは既定cacheへの書き込み制限（上記の単独再実行ではPASS）。
- commitは **未完了**。`git add` が `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-coreperf/index.lock` の書き込みを拒否された。pushはしていない。

40%目標はMacの4枚で達成したが、AQUOS sense9の実測・60fps可否とAndroid Gradle検証は残る。commit前に権限のある環境でAndroidと公式検証を完了する。
