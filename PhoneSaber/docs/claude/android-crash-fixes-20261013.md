# Android host検証・完了メモ（2026-10-14、6176740再監査）

対象は `git diff main...HEAD` のhost tool・Python回帰・本メモの3ファイル。runtimeのcrash修正ではない。
- P2 `android/core/tools/android_host_check.py:72`：現JVMテストの依存不足でcompile停止。純粋Kotlin 7ソースとGradleと同じCameraX 1.6.2を追加済み。
- P3 同`:90,98`：makeの出力と異なる旧/tmp decoderを使用し、欠落／古いdecode結果になる。一時BUILDと実行decoderを一致済み。
- 両回帰は修正前FAIL→後PASSを再実行。F3はF2依存修正を残して旧benchmarkだけ復元し、旧パス欠落を注入。
- 広域検証を実行し、環境制約と検証範囲を明記。追加コード欠陥なし；今回は本メモのみ更新。
- `--benchmark` は8×8 PNG実decode・4方向を実行し、BUILD一致とcleanupも回帰で確認。
- host回帰はportable `make test` と別（JBR/NDK/Gradle cacheが必要）。下記の明示コマンドで実行。
- 差分にTODO・stub・未テストoptionなし。アプリ既定動作・認識・UDP本文・正式corpus・Unity/ProjectSettingsは無変更。
- 新規C#/Swift/Kotlinは0件；追加meta・project登録は不要。

実行結果（全ログ `/tmp/phonesaber-finish-*.log`、GUI起動・commit・pushなし）：
- `JAVA_TOOL_OPTIONS=-Xcheck:jni python3 -B -m unittest discover -s PhoneSaber/android -p test_host_check.py -v`：2/2、実JUnit/JNI 79件、CheckJNI違反0。
- `make -C PhoneSaber/android/core test parity`：C++・Python4件成功、正式40/40、325 PNG・541遷移・27合成条件の不一致0。
- tools内 `python3 -B -m unittest discover -v`：初回Xcode Python3.9でnumpy/Pillow欠落4 errors。依存がある `/opt/homebrew/Caskroom/miniconda/base/bin/python3` (3.13.11)で233件、失敗0、Windows限定6 skip、既存expectedFailure 1。
- iOS Tools内同unittest：初回Python3.9で巨大整数回帰FAIL＋tracking E2E ERROR。`/Users/satoshi/.pyenv/versions/3.12.1/bin/python3.12` で431件、FAIL 0、tracking E2E ERROR 1、opt-in 1 skip。
- opt-in背景画像integration：`PHONESABER_BACKGROUND_BENCHMARK_INTEGRATION=1 python3.12 -B -m unittest -v test_background_negative_benchmark.RealInboxIntegrationTests`：1/1成功。
- `python3 -B PhoneSaber/tools/check_pc_latency.py`：Unity実API 4構成compile、EditModeテスト2構成compile、Windows P/Invoke compile、NUnit34/34。
- `python3 -B PhoneSaber/tools/check_auto_latency.py`：C#5/5、Swift3/3、実Swift→C# UDP loopback成功。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`：静的BGRA・DeviceHealth13/13。
- `python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py`：33/33（カメラ/UIはstub）。`run_send_gap_host_tests.py`：17/17。
- AndroidでJBR JAVA_HOME・SDK ANDROID_HOMEを指定し `./gradlew --no-daemon -Pkotlin.compiler.execution.strategy=in-process :app:testDebugUnitTest :app:assembleDebug :app:lintDebug :app:assembleRelease :app:assembleDebugAndroidTest`：成功、76/76、lint error0/warning27。
- Debug/unsigned Release APKにarm64 JNI・c++_sharedあり。androidTest APK生成済み。`adb devices -l`：0台。
- iOS `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /tmp/phonesaber-finish-ios-build CODE_SIGNING_ALLOWED=NO build`：exit65。sandbox_apply拒否→SwiftUI macro plugin応答不正；ビルド成功は未確認。
- tracking E2Eは空き容量APIが0を返して開始拒否（dfでは430GiB空き）。TMPDIR=/private/tmpでも同じERROR。容量gateは変更していない。
- `git diff --check`成功。失敗対象のiOSソース・既存expectedFailureはmainと同一で、この差分が原因の失敗なし。

残件（operator）：公式 `bash PhoneSaber/tools/verify_phone_saber.sh` でiOS XCTest・Release・tracking E2Eを制約のない環境で再確認。
Unity Editor実行・ART/CameraX・実Wi-Fi・署名Release・長時間実機は未実施；本差分はruntime無変更なので追加必須のUnity/実機テストなし。
ARTの既存JNI3件を確認する場合のみAndroidで `./gradlew --no-daemon :app:connectedDebugAndroidTest -Pandroid.testInstrumentationRunnerArguments.class=jp.phonesaber.sender.NativeRotationTest`。
既存 `tools/test_latency_report.py:20` のNaN集計expectedFailureはoperator保留の挙動変更案。今回実装せず、PASSと扱わない。
