# core-r4（2026-10-09）

基準はorigin/main `35afe44c2d5f90cdde3ea6c62bf8c8b9dc369cd4`。core-line proposal採点のindices/uniqueを候補間で再利用し、既存proposal bitmapでsort前に重複を除く。valid座標の集合・row-major点順・逐次FP演算・候補順・threshold/ranking/eligibility/UDPは維持。score_componentのScoredは支持点を所有コピーするため、scratchのclear/reuseが過去候補に影響しない。使用したbitmap位置は採点直後、全continueより前に消す。scratchは色・frame内に限る。

## プロファイルと計測

`make -C PhoneSaber/android/core benchmark sampling-benchmark BUILD=/tmp/phonesaber-core-r4-before`。既存SIGALRM/atos sampling_profile.py、各1000frame、blue brightとforensic1048。4157/7506 samples中、score_component 26.3/30.4%、components 16.6/10.5%、dilate+erode 9.1/10.4%。core_linesがinlineされたpipeline.cpp:333の呼出行は12.3/16.1%。samplingは速度測定ではなく、inline帰属を含む。morphologyはclangのRpassで既にwidth16/interleave4のvector化を確認したため変更しない。

整数の白飛び判定全画面cache、放射輝度256値表、Hough投影cacheも試したが、一貫した時間改善を確認できず全て外した。最終差分はpipeline.cppと本メモのみ。

Mac arm64、clang -O2 -ffp-contract=off、BGRA/step2。allocation_benchmarkは30frame、decode/warm-up・libc内部mallocは計数外。byteはnew/new[]要求byteの総量であり常駐memoryではない。

| fixture（全480×640） | new回数/frame 前→後 | 要求byte/frame 前→後 | median ms 前→後 |
| --- | --- | --- | --- |
| blue-led-bright-large-05.png | 757→730 | 4,707,192→4,652,140 | 2.362833→2.319333 |
| blue-led-with-left-curtain-reflection-04.png | 905→881 | 6,150,863→6,093,331 | 4.094500→3.944792 |
| forensic-20260921/frame_1048.png | 2049→1986 | 7,768,195→7,678,831 | 6.288375→6.344750 |
| lossless-regression/phonesaber_20260923_143446_247/red_dropout_last_true_89.png | 1590→1545 | 8,610,098→8,518,322 | 5.626541→6.304250 |

上表の速度はfixtureごとに前後/後前を交互、各300frame、7組のrun中央値の中央値。Unity長時間PlayMode・Android再build・parityと並行しており、fixtureにより測定負荷が異なる。最後のfixtureは約12%遅い値であり、この並行負荷下の測定だけではレイテンシ改善を確定できない。

Unity・Xcode・Gradle・emulator・parityの終了後、同一binary・同一手順・7組×300frameで再測定した。並行検証なしのrun中央値の中央値は以下（fixture順は上表と同じ）。

| fixture | 前 ms | 後 ms | 短縮 |
| --- | ---: | ---: | ---: |
| bright | 1.221333 | 1.179250 | 3.45% |
| curtain | 1.554541 | 1.490416 | 4.13% |
| forensic1048 | 2.539084 | 2.466958 | 2.84% |
| red dropout | 2.818583 | 2.726125 | 3.28% |

最終差分のallocation回数は2.7〜3.6%削減し、並行検証なしの時間計測も4fixtureで2.8〜4.1%短縮した。rawは`benchmark-idle.json`に保存。Android実機の性能・60fpsは未測定。

## 最終差分の検証

- core test：core-test＋Python4件PASS。
- parity：PNG269（fixture35＋inbox original234）、合成27、FrameProcessor541遷移の不一致0。formal40件失敗0。
- Android `:app:testDebugUnitTest :app:assembleDebug`：PASS、unit35件失敗0。
- psparity emulator `:app:connectedDebugAndroidTest`：PASS、JNI/Android38件失敗0。emulatorは終了した。
- git diff --checkと自己レビュー：PASS。FP式・演算順・候補順・threshold/ランキングは変更しない。unordered container、共有可変scratch、UDP、ゲームコード、既存metaの変更なし。

raw計測はこのworktreeの `PhoneSaber/.verify-logs/core-r4/`、検証ログはoperator scratchpadの `core-r4-test.log`、`core-r4-parity-final.log`、`core-r4-android-final.log`、`core-r4-jni-final.log`。fixture pathは `PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/`基準。
