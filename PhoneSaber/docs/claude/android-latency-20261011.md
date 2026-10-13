# Android 撮影→送信の遅延予算（2026-10-13 仕上げ）

対象: AQUOS sense9 + Windows PC。マージ後の開始点は `8a0af37`。
**未commit・未push**。差分レビュー・公式verify・commitはoperatorが行う。
以下が今回の結果。後半の「2026-10-11 の記録」は当時の履歴で、未実装・検証数・タグは当時のもの。

## Findings（重大度順）

1. **P2・診断欠落を修正**: `android/app/src/main/java/jp/phonesaber/sender/MainActivity.kt:141`、
   `DeviceHealth.kt:125`、`LatestUdpSender.kt:40`。
   以前のlogcatは毎秒の解析fps/JNI/成功送信のageだけで、撮影fps、破棄、PC応答経過時間がなく、
   端末だけでコピーできなかった。例えば撮影60fps→解析30fpsで半分を落としていても、
   生き残った送信のmedian/p95だけは良く見える。撮影・解析・赤青の送信・破棄を一緒に記録する。
   `DeviceHealthTest.diagnosticsDoNotHideCaptureAndDroppedFrames` は変更前に **1/1 FAIL**、変更後PASS。
   他の追加テストでカウント、期限、reset、UNKNOWN、表示項目、コピー保持期間を検証した。
2. **P3・要実機確認**: `CameraSession.kt:125`。
   NR/EDGEの既定モードによる遅延は未確定。HIGH_QUALITYはcapture rateを落とし得るが、
   AQUOSの既定がそのモードとは断定しない。許可された試験設定だけを追加した。
   直接send・Preview変更・露光制限は提案に留め、認識/閾値は変更しない。

## 実装と維持した契約

- **10秒ごとに1行、固定タグ `PhoneSaberLatency`**。直近5秒の撮影fps、解析fps、
  赤/青OS送信成功fps、capture→最後の成功したOS sendのmedian/p95/件数、破棄、
  thermalのAndroid値と日本語、現在のPCからの探索応答ageを含む。
  寸法、要求fps、機種/API、sensor clock、camera generation、実NR/EDGE/EIS/OIS、
  露光時間、frame duration、AE range、Wi-Fi lockの保持状態も付ける。
  メインthreadの既存1秒refreshで10秒を判定するため、UIが詰まれば遅れる。catch-upの連続出力はしない。
- **「ログをコピー」**: timestamp付き直近5分を保持（最大60行）。停止/再開で消さず、
  コピー時も5分で失効させる。アプリのプロセス再起動・Activity再作成では消えるので、その前にコピーする。
  数字の手入力は不要。Androidのクリップボードへコピーし、operatorがチャット/メモへ貼り付ける。
- `capture_fps` は成功したCamera2 `onCaptureCompleted` callbackの到着数/直近5秒。
  `analysis_fps` は正常にJNI解析した数/直近5秒。sensor clockがUNKNOWNでもfpsは取れる。
  `dropped_camera_frames_est` はカメラ開始/復旧からの成功capture数−解析数を0以上に丸めた推定。
  配送中のフレームやcallback順序/Previewの影響も含み、HAL内部のdropやCameraXの厳密な破棄数ではない。
  `dropped_udp_frames` はfresh packetのあるpendingの置換、または全packetの送信が失敗/期限切れしたフレーム数。
  空/held、意図した停止/宛先切替のflush、片色だけ失敗したフレームはこの数に含めない。
  部分失敗は赤/青fpsの差でも見る。カウンタはcamera generation更新でresetする。
  成功ageは従来どおりREALTIMEだけ。UNKNOWN/空/未検出はage=UNKNOWN、0msで補完しない。
  PC reply ageはUDP 5007の探索応答からの経過であり、座標のACK/RTTではない。
- **既存の `[60,60]` / `[30,30]` は維持**。AE target rangeはAEがfpsを選べる範囲で、
  `[30,60]`なら暗所で30fpsを選べる。固定60はその自由を制限し、露光時間の制約で暗くなり得る。
  既存の「カメラ60fps」スイッチで30に切替可能。暗所について説明を追加した。
  Previewを含む最も遅いstreamのminFrameDurationにも制約されるので、固定要求でも実測60を保証しない。
  [Camera2 fps仕様](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#CONTROL_AE_TARGET_FPS_RANGE)。
- **ImageAnalysisは `KEEP_ONLY_LATEST` を維持**。この方式では `setImageQueueDepth` は無効で、
  depth=1を加えても改善しない。BLOCK_PRODUCER/FIFOへ変えない。RGBA、解像度、JNI回転と認識入力は維持。
  [公式queue depth仕様](https://developer.android.com/reference/androidx/camera/core/ImageAnalysis.Builder#setImageQueueDepth(int))。
- **「低遅延カメラ設定（試験）」は既定OFF、保存しない、送信中は変更不可**。
  ONのときだけ、available modesにOFFがあればNR/EDGEをCamera2InteropでOFF指定する。
  OFF非対応/nullなら未指定でHAL既定を維持し、FAST等へ勝手に代替しない。
  通常OFFでは新しいpixel処理指定は一切ない。EIS/OISは以前からOFF指定済みなので、その既定は維持した。
  HALが実際に報告する結果値を記録する。OFF要求でも実結果が違う/非対応なら短縮を保証しない。
  ノイズ・輪郭・検出結果は変わり得るのでONの本番採用はoperatorの実機比較後。
  [NR](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#NOISE_REDUCTION_MODE)、
  [EDGE](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#EDGE_MODE)。
- **送信中はlow-latencyとhigh-performanceの両Wi-Fi lockを保持、停止/背景化で両方解放**。
  既存のlow-latency lockは維持。前面・画面ONではlow-latencyが優先する。
  Android 14/API34以降のHIGH_PERFはLOW_LATENCYへ置換されるため、AQUOSで二重取得の追加短縮は約束できない。
  `isHeld=true`は要求保持を示し、HALの有効化/無線時間の保証ではない。
  [Wi-Fi lockの制約・API34の扱い](https://developer.android.com/reference/android/net/wifi/WifiManager#WIFI_MODE_FULL_HIGH_PERF)。
- **送信workerは維持**。mainへの座標hop、全画素copy、再送、payload変更は追加していない。
  統計のlockは赤と青のsendの間に挟まず、両色send後に集計する。
  recognition/ranking/eligibility/scoring/PCA/threshold、JNI/core、formal corpus、Unity scenes/prefabs/meta、
  ProjectSettingsは今回の作業で変更なし。マージで採用済みのcore-line guardも変更していない。

## ホストでのsend比較（Androidの測定ではない）

実行: `python3 PhoneSaber/android/core/tools/udp_dispatch_benchmark.py`。
実際のLatestUdpSenderをコンパイルし、Network.bindSocketだけをstubにし、
SystemClockはhostのnanoTimeへ置換。比較する直接send版は一時ディレクトリ内だけでworkerのhopを除去する。
Mac/JBRのloopback、約60fps、30frame warm-up後180frame、順序を反転して3pair。
送信先5005/5006が他アプリに占有されていたらbind失敗で止まる。CameraX/JNI/無線/Android niceは含まない。

最終実行（各mode 540frame、warm-up込み2520datagram、全payload/受信数一致、破棄0）:

| pair | worker median / p95 ms | direct試作 median / p95 ms |
| --- | --- | --- |
| 1 | 0.182416 / 1.047750 | 0.126937 / 0.208375 |
| 2 | 0.194708 / 5.848792 | 0.077771 / 0.170250 |
| 3 | 0.057292 / 3.021000 | 0.035896 / 0.068875 |

中央値差は約0.021〜0.117ms。hostのworker tailは約1〜6msで、Androidの改善予測には使えない。
先行比較ではproducerの遅れをfixed deadlineへ追い付かせるburstでpending置換1frameが起き、
「全180frame成功」というbenchmarkの仮定が179件/破棄1で失敗した（アプリの意図したlatest動作）。
最終toolはcatch-up burstを作らず、成功+破棄の会計も検証する。成功p95だけを見る危険の実例として残す。
直接sendは解析threadをsocket初期化/bind/syscallで止め、CameraXの選ぶframe列やexpiry時刻を変え得る。
AQUOSでの実測なしに採用しない。送信threadの優先度変更、入力解像度/Preview変更、manual exposureも提案のみ。

## 再検証（2026-10-13）

```sh
cd PhoneSaber/android
export JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home"
export ANDROID_HOME="$HOME/Library/Android/sdk"
./gradlew --stop
./gradlew :app:testDebugUnitTest :app:assembleDebug :app:lintDebug
# 追加後はsandboxのKotlin daemon書込み制限を避けて実行
./gradlew -Pkotlin.compiler.execution.strategy=in-process :app:testDebugUnitTest :app:assembleDebug :app:lintDebug
cd ../..
make -C PhoneSaber/android/core test parity
python3 PhoneSaber/android/core/tools/udp_dispatch_benchmark.py
git diff --check
```

- マージ直後の指定Gradle3task: **BUILD SUCCESSFUL**。Kotlin daemonのホームへのmarker書込みは拒否され、
  fallbackコンパイルで成功。先行する環境変数なしの試行はJava未検出で停止し、その後上記env付きstopからやり直した。
- 追加後のin-process実行: **JVM 72件、failure/error/skip 0、Debug APK build成功、lint error 0 / warning 27**。
  既存のdeprecated API、UI文字列、SDK/配布設定等のwarningは残す。scope外の整形/SDK変更はしない。
  新規10件: 診断欠落の回帰、capture/send window・reset・重複callback・queue上限と累積数保持、破棄/空/heldと負の推定防止、
  NR/EDGE試験OFF・ON・非対応・null、PC reply ageの未応答/別PC/reset、10秒cadence/停止/catch-up抑制、
  5分保持/reset継続/失効/上限、固定status項目/UNKNOWN。UNKNOWN時計とOS send ageの既存回帰もPASS。
- C++ core tests PASS、Python comparator **4/4 PASS**。Swift/C++ parity **325 PNG × 3経路、差分0**、
  **formal 40/40**（35 unique PNG）、状態遷移541・合成27の差分0。annotated 12枚は従来どおり除外。
- send比較: 上表の最終6run PASS。receiverでpayloadの完全一致、成功したOS send数との受信数一致を確認。
- `git diff --check` PASS。commit/pushは行っていない。
- 未実行: **AQUOS/Android camera実機、emulator、connectedDebugAndroidTest、Release build、Windows/無線/光学遅延、
  発熱連続運転、公式verify**（operator実行）。`adb devices` は接続端末0件。
  hostのparityはbionic/実カメラの証明ではない。

APK: `android/app/build/outputs/apk/debug/app-debug.apk`。
テスト: `android/app/build/reports/tests/testDebugUnitTest/index.html`。
Lint: `android/app/build/reports/lint-results-debug.html`。

## AQUOS + Windowsでの採用確認（数字を入力しない）

1. 同じ箱/画角/光量/充電条件でWindows hotspotとUnityを起動し、APKを入れる。
   既定閾値145/25/30、同じmirror、遅延計測モードOFF、低遅延カメラ設定（試験）OFF。
   10秒warm-up、赤青の静止→速振り→画面端を各10秒以上。**ログをコピー**して貼り付ける。
   PCに保存する場合は `adb logcat -v time -s PhoneSaberLatency:I`。通常運用にはNTP/数字入力不要。
2. 60fps維持ならcapture/analysisは約60、両色freshならsend red/blueは各約60、
   REALTIMEならage samplesは5秒で約300（両色で600にはならない）。JNIは16.67ms以内が処理予算。
   `ae_fps=[60, 60]`、`frame_duration_ns`は約16,666,667を期待するが、HALとstream次第。
   未解析推定は配送中の数枚を含めても増え続けないこと、UDP破棄0を期待する。
   PC reply ageは通常3秒未満。thermal 3以上/analysis 42未満が続けば発熱/処理を切り分ける。
3. 停止→60fps OFF→開始し、同じ操作をする。約30/30/各30fps、約150age samples、
   frame duration約33,333,333ns、JNI予算33.33ms、warning境界21fps。
   明→会場の暗さも比較し、60fpsで暗く/未検出になるなら30と画像を比べる。thresholdで埋め合わせない。
4. 60fpsで試験OFF/ONを各3回、開始前に切り替えて同条件で比較。ON対応時は `nr=0 edge=0`、
   `video_stabilization=0 ois=0`（後者2つは元からOFF）を期待する。非対応なら既定値のまま。
   背景FP・淡い剣・速振り・四隅とcapture→sendの両方を見る。改善なし/認識悪化ならOFFへ戻す。
   再起動後OFF、送信中操作不可も確認。
5. 開始/停止、背景→前面、Wi-Fi切替、PC変更、カメラ復旧でcamera generationと統計resetを確認。
   両Wi-Fi lockが送信中held、停止/背景時は `adb shell dumpsys wifi` 等で解除を確認。
   停止後もコピーでき、5分を過ぎた古い行は消えること、旧座標再送がないことも確認。
6. 本番の箱・給電で30分以上運転し、**5分以内ごとにコピー**（またはlogcat連続保存）して熱/fps/破棄/p95の推移を残す。
   60fpsでcapture→send **median <50ms / p95 <80ms**を暫定比較目標にできるが、AQUOSの予測/合格保証ではない。
   iPhoneの実測37ms/60fps・50ms/30fpsは参考で、Androidの値と同一視しない。
   F9のscreen→receive約135msも範囲が違う。capture→OS sendはPC到着/Unity描画まで含まない。

未解決: Previewの実PRIVATE stream制約、AQUOSのREALTIME対応/NR・EDGE実モード/ISPの時間、
Wi-Fi lockのHAL効果、発熱時の60fps維持、PC到着と光学遅延。CameraXの厳密なdrop数は公開されないので推定と明記した。
UNKNOWNではcapture→sendが取れず、成功のみのp95は過負荷で良く見え得る。fps・件数・破棄も必ず合わせて評価する。

---

## 2026-10-11 の記録

対象: AQUOS sense9 / Android 14・15、Windows PC のホットスポット。
比較元は `67a67fc`。中断した `b03244a`（wip）の修正を引き継いでレビュー・検証した。
今回の追加コミット・push はしていない。公式 verify と採用は operator が行う。

## 結果と欠陥（重大度順）

- **P2・修正済み: 送信までの時間を過小表示**。
  `android/app/src/main/java/jp/phonesaber/sender/CameraSession.kt:133–138`、
  `LatestUdpSender.kt:77–89`。以前はセンサー時刻→JNI終了を「撮影→送信」と表示し、
  空結果・held・送信失敗も集計していた。例: 露光開始100.000秒、JNI終了100.020秒、
  worker待機後に100.055秒でOS送信成功なら、旧表示20msに対し正しくは55ms。
  成功した送信の直後で計測し、同一フレームの最後の成功を1件として集計する。
- **P2・修正済み: UNKNOWN時計からもっともらしい誤測定**。
  `CameraSession.kt:79–82`、`DeviceHealth.kt:54–56`。
  旧コードはUNKNOWNを `nanoTime()` と比較していた。別時計のoffsetが20msなら、
  実際の遅延にかかわらず20msを表示し得る。REALTIMEだけを
  `elapsedRealtimeNanos()` と比較する。UNKNOWNは未計測にする。
- **P3・改善: 解析executorに優先度指定がない**。
  `CameraSession.kt:40–42`、`AnalysisExecutor.kt:6–8`。
  CPU競合時に通常優先度の解析が待たされ、結果が古くなる可能性がある。
  既存の1本のScheduledThreadPoolExecutorに名前とAndroid nice -1
  (`THREAD_PRIORITY_MORE_FAVORABLE`) を付け、最初のJNI/expiry処理より先に設定する。
  worker本数・処理順・expiry・認識・送信条件は維持。実機での改善msは未測定で、
  優先度だけで一定の短縮や60fpsを保証しない。
- **P3・wipのテストを修正**。
  `android/app/src/test/java/jp/phonesaber/sender/AnalysisExecutorTest.kt:16`。
  型指定なしの `schedule` がRunnable版を選び、結果がnullになって同一Threadのassertが失敗した。
  `schedule<Thread>` でCallable版を明示した。製品側の欠陥ではない。

## 遅延予算

以下の F は要求フレーム周期（60fps: 16.67ms、30fps: 33.33ms）。
「未測定」は静的コードから確定できない項目で、0msという意味ではない。
センサー/ISPの遅延は機種・露光・発熱に依存し、各段階を単純に1Fずつ足して推定しない。

| 段階 | 現在の処理・場所 | 追加時間 / フレームの扱い |
| --- | --- | --- |
| 現実の動き→次の露光 | AE ON、AE lock false、[60,60]対応時は固定60、非対応は固定30（CameraSession:85–114） | 露光開始までの位相待ち0〜F、ランダム位相なら平均F/2=8.3/16.7ms。新しい撮影→送信統計にはこの待ちを含まない |
| 先頭行の露光開始→readout→ISP出力 | 露光/ISOはAE任せ、上限変更なし。EIS/OISは既にOFF（:115–118） | 露光時間・rolling readout・ISP滞留は未測定。要求周期Fは露光の1/100秒上限ではなく、実際の露光やpipelineフレーム数をこのコードは読んでいない |
| 解像度・ストリーム交渉 | ImageAnalysisとPreviewに同じResolutionSelector。要求fpsに対応するVGA以下の最大候補、なければ最小候補（CameraPolicy:18–29） | 通常640×480→90度回転で480×640。固定の解析リサイズはない。CameraXが選んだ実寸をcoreへそのまま渡す。sensor/ISPの縮小コストは未測定 |
| ImageReader / CameraX backpressure | `STRATEGY_KEEP_ONLY_LATEST`、CameraX既定background executor、ImageProxyはfinallyでclose（CameraSession:110,147） | アプリの解析は同時1枚。CameraX 1.6.2内部reader深度は4、待機cached imageは最新1枚への置換。深度4は4FのFIFO待ちを意味しない。通常負荷で最新フレームの到着位相は0〜F程度だが、OS停滞には上限保証なし |
| CameraX YUV→RGBA | `OUTPUT_IMAGE_FORMAT_RGBA_8888`、CameraX内 `convertYUVToRGB`（:108） | 全画素の変換・RGBA出力バッファへの書き込みがある。機種上のmsは未測定、独立した固定1F待ちはコード上にない。JNI時間表示には含まれない |
| 解析executorへ配送 | 専用1本、nice -1。JNI・expiry・セッション操作を直列化（:40,121） | 1回のexecutor dispatch。msは未測定、固定フレーム待ちなし。priority変更はここでのCPU競合待ち削減を狙う。CameraXの受取/drop executorとは別 |
| JNI / 回転 | direct RGBA planeを渡し、nativeの再利用scratchへbyte回転。0度は条件により共有（NativeCore.processCamera / rgba_rotation.hpp） | コピーは90/180/270度で1回、Kotlinの全画素コピーなし。Macの既存90度回転測定0.103〜0.125ms。AQUOSの値ではない |
| detection / 結果生成 | 既存C++ coreをDebugでも-O2、JNI内で認識→payload→独立したNativeResult生成 | Macの既存90度回転込みJNI全体1.742〜3.176ms（android-perf.md）。今回のAQUOS実測なし。目標は全JNIがF未満、認識の式・閾値は変更なし |
| 解析→UDP worker | analyzerが `sender.offer` を直接呼ぶ。メインスレッドhopなし。pending最新1フレーム、空結果も置換（LatestUdpSender:37–42） | 1回のnotify/wakeup、FIFOなし。workerのスケジューリング待ちは未測定。既存gateを保持したOS送信中はofferも待つ。停止後の古い結果は既存世代チェックで破棄 |
| UDP OS送信 | Wi-Fiへbindしたnonblocking DatagramChannel。ASCIIを無加工送信、赤5005/青5006、再送なし。処理開始から180msで破棄（:65–81） | socket初期化は初回のみ。ASCII byte配列等の小さい確保は既存のまま。send syscallのmsは未測定。固定F待ちなし。今回この成功直後を測る |
| OS送信→無線→PC→Unity表示 | 既存Wi-Fi low-latency lock（MainActivity:43）、本番Windowsホットスポット | 未測定で今回の統計の範囲外。OSに渡った時刻はPC受信・描画時刻ではない。UDP payloadに時刻は追加しない |

CameraX 1.6.2のGoogle Maven配布sources.jarも読んだ。
`ImageAnalysis.java:225,365–367` にreader深度4、
`ImageAnalysisNonBlockingAnalyzer.java:62–63,88–92` にacquireLatest/最新cache置換、
`ImageAnalysisAbstractAnalyzer.java:220–225,445–449` にRGBA変換・再利用bufferがある。
`setImageQueueDepth(1)` はKEEP_ONLY_LATESTで無視されるので追加しない。
[公式ImageAnalysis仕様](https://developer.android.com/reference/androidx/camera/core/ImageAnalysis#STRATEGY_KEEP_ONLY_LATEST)、
[queue depth / RGBA変換の仕様](https://developer.android.com/reference/androidx/camera/core/ImageAnalysis.Builder#setImageQueueDepth(int))。

固定[60,60]は以前から実装されていた。[30,60]のようなauto rangeではAEが範囲内で30fpsを選べるが、
固定rangeにはその自由がない。ただし全出力streamの最も長いminFrameDurationにもfpsが制約されるので、
表示の「60fps要求」は実測60fpsの保証ではない。
[Camera2のfps制約](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#CONTROL_AE_TARGET_FPS_RANGE)。

## 入力画素を変える案は未実装

ユーザーの「operatorが気付く挙動変更は提案だけ」を優先した。
今回のdiffはカメラ設定・JNI・core・UDP生成を変更しない。同じ入力列と処理時刻なら認識結果は同じ。
スケジューリング変更で処理される実カメラフレーム列は変わり得るため、物理撮影全体の結果一致までは主張しない。

- **より小さいstream / Previewサイズ変更**: 320×240を480×640へ拡大しても元のVGA画素に一致しない。
  ISPの縮小・crop・binningが変わる可能性もある。480×640入力がbit一致する証拠がないためresolution selectorを維持した。
- **NR/edgeをFASTに強制**: 現在アプリはNR/EDGEを指定していない。CameraX/Camera2 templateやHALの実際の値はCaptureResult確認が必要。
  FASTはcapture rateを落とさない設定、HIGH_QUALITYは落とし得る。ZERO_SHUTTER_LAGは高解像度再処理向けのstream別処理で、名前だけでは追加遅延を断定できない。
  FASTを強制するとRGBA・候補が変わり得るので未実装。採用するならavailable modesと実結果を確認し、背景FP・淡い剣・速振りを別評価する。
  [NR仕様](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#NOISE_REDUCTION_MODE)、
  [edge仕様](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#EDGE_MODE)。
- **露光10ms上限（1/100秒）のopt-in**: UIに明示した実験設定としてのみ提案。既定AEは維持。
  Android 14/15でAE ONのままSENSOR_EXPOSURE_TIMEを書くだけでは通常AEに上書きされ、上限にはならない。
  実装にはMANUAL_SENSOR対応、CaptureResultの実露光/ISO確認、自動ISOの設計が必要。
  暗さ・ノイズ・LEDの見え方が変わるので本番採用前に比較が必要。
  [AEによる露光値の上書き](https://developer.android.com/reference/android/hardware/camera2/CaptureRequest#CONTROL_AE_MODE)。

## 計測仕様と既存UIへの追加

`ImageInfo.timestamp`→そのフレームで最後に成功した `DatagramChannel.send` 直後。
先頭行の露光開始時刻を起点にする。REALTIMEはelapsedRealtimeNanosと同じtimebase、
UNKNOWNは他時計との比較を保証しない。
[センサー時計仕様](https://developer.android.com/reference/android/hardware/camera2/CameraCharacteristics#SENSOR_INFO_TIMESTAMP_SOURCE)、
[露光起点仕様](https://developer.android.com/reference/android/hardware/camera2/CaptureResult#SENSOR_TIMESTAMP)。

DeviceHealthMeterをcamera/senderで共有し、解析fps/JNI統計と送信統計を分けた。
送信統計は直近5秒、上限1200件、medianは偶数件の中央2値の平均、p95はnearest-rank。
両色が成功しても1フレーム1件。片色だけ成功ならその成功時刻を使う。
pending置換・期限切れ・socket満杯・非fresh・空結果で成功がなければ集計しない。
負の差・無効timestamp・2秒超の差は旧来と同様に除外し、未計測を表示する。
セッション開始でresetし、止まった値は5秒で失効する。
予測freshは現在の撮影フレームのageであり、最終実検出フレームのageではない。

既存の端末状態行と毎秒の `Log.i("DeviceHealth", ...)` に
`撮影→送信 中央値 XX ms / p95 YY ms（N件）` を表示する。
解析JNI時間は回転・認識・結果生成を含み、センサー/CameraX/worker待ちは含まない。
新しい撮影→送信値は旧JNI終了値より大きくなり得るので、旧表示と直接比較しない。
比較元にも同じ計測だけを載せて、executor優先度の有無を比較する。

## 実行した検証

repo rootから:

```sh
make -C PhoneSaber/android/core test parity
cd PhoneSaber/android
JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home" \
ANDROID_HOME="$HOME/Library/Android/sdk" \
./gradlew :app:testDebugUnitTest :app:assembleDebug
```

- 指定Gradle初回: Debug APK buildは成功、JVM 60件中1件失敗（wipのRunnable/Callableテスト不備）。
  Kotlin daemonのホームへのmarker書き込みはsandbox拒否、fallbackコンパイルは動いた。
- テスト修正後: 同じJAVA_HOME/ANDROID_HOMEで
  `./gradlew -Pkotlin.compiler.execution.strategy=in-process :app:testDebugUnitTest :app:assembleDebug`
  を実行し **BUILD SUCCESSFUL、60件 / failure 0 / error 0**。
  nice設定前の初回task、同じthreadでexpiry、キャンセルqueue掃除、REALTIME/UNKNOWN・不正値、
  成功送信までの55ms、p95、失効/resetを検証。既存CameraPolicy 4件で固定fps条件と解像度選択、
  PayloadRouting 4件でpayload/fresh条件を確認。新しいカメラoption選択は追加していない。
- 変更前の失敗確認: `/tmp`で旧 `DeviceHealth.kt` をそのままcompileし、旧CameraSessionの計測式とexecutor初期化を
  JVM用に取り出して旧APIを使う同一シナリオのJUnitを実行。送信55msのはずが20ms、UNKNOWNがnullのはずが20ms、
  executorの名前/事前priority設定が欠落、の **3/3失敗**。現ソースで同じシナリオは **3/3 PASS**。
  ソケットやCameraXの実動作を再現するintegration testではない。
- C++ core test PASS、Python comparator unit test **4/4 PASS**。
  Swift/C++ parity: **314 PNG × 3入力経路、差分0**（formal 35 unique PNG + inbox original 279、annotated 11除外）。
  **formal 40/40、状態遷移541操作・合成27ケース、差分0**。
- `git diff --check` PASS。認識/core/JNI/corpus/Unity/ProjectSettingsに差分なし。

APK: `android/app/build/outputs/apk/debug/app-debug.apk`。
Gradleレポート: `android/app/build/reports/tests/testDebugUnitTest/index.html`。
この実行でNDK arm64 Debugライブラリもbuildした。
未実行: 実機/emulator、connectedDebugAndroidTest、Release build、Windows送受信・光学遅延・発熱運転、
公式 `verify_phone_saber.sh`（operator実行）。Mac parityはAndroid/bionicでのbit一致の証明ではない。

## AQUOS + Windowsのチェックリスト

1. 同じ箱・画角・距離・明るさ・充電条件で、WindowsのホットスポットとUnity受信を開始。
   Debug APKを入れ、既定threshold（145/25/30）、同じmirror、通常payload（遅延計測モードOFF）で赤/青を確認。
   端末表示は通常 **480×640 / 60fps要求**を期待するが、実際のstreamは機種交渉に依存する。
   寸法が違えば拡大して合わせず記録し、入力同一条件の比較をやり直す。
2. 10秒warm-up後、静止した両剣を10秒→速振り10秒→画面端への出入り10秒。
   既存statusの解析fps・色別送信fps・JNI中央値・撮影→送信median/p95/件数を画面で読み、
   数字を入力せず `adb logcat -v time -s DeviceHealth:I` を保存する。
   比較元は同じ計測を載せ、通常priorityだけに戻したbuildで同条件のA/Bを各3回。
3. 60fpsが維持できれば **F=16.67ms、解析約60fps、両色freshで各約60送信fps、直近5秒約300件**。
   両色を600件とは数えない。jniは **16.67ms未満**が持続処理の予算。
   既存warning境界は開始3秒後 **42fps未満**。未検出なら送信件数0/未計測は正常。
   REALTIMEでも剣検出中ずっと未計測なら時計・送信成否を調べる。UNKNOWNなら今回のage測定は使えない。
4. 「カメラ60fps」OFFでも同じ操作を実施。
   **30fps要求、F=33.33ms、解析約30、両色各約30、約150件、warning境界21fps**を期待する。
   60fpsで低下し30fpsで安定するならJNI時間・発熱・stream制約を切り分ける。
5. 明→会場相当の暗さで両設定を比較。
   [60,60]要求でも実測30fpsならPreviewを含むstream min duration、HAL、発熱を調査。
   光量不足で剣が消える/ブレる現象は認識閾値を変えず記録する。
   priority変更の短縮量は未確定。撮影→送信の暫定評価目標を **median <50ms / p95 <80ms（60fps）**と置いて比較できるが、
   これは実測予測・合格保証ではない。1F=16.67msを超すp95差が反復するならsensor/CameraX/CPU待ちを調査する。
6. 開始/停止を繰り返し、前面復帰・Wi-Fi切替・送信先変更を試す。
   停止後の旧座標の再送がなく、再開時に統計がresetし、未送信5秒でageが未計測になることを確認。
   UDPは5005/5006の既存 `x1,y1,x2,y2` のまま、再送/heartbeatの追加がないことも確認。
7. 本番の箱で30分以上、充電・発熱・fpsとp95の推移を保存。PCの受信/到着間隔は別に確認し、
   光学capture→Unity画面は高fps外部撮影で測る。端末のcapture→OS send値を無線/描画遅延と混同しない。
   `connectedDebugAndroidTest` で正式JNI fixtureも再確認する（今回未実行）。

未解決リスク: PreviewのPRIVATEサイズをYUV表から評価できない場合は従来通り不明として受理するので、
実際のstream組合せが60fpsを制限し得る（CameraSession:94–103,149–153）。
NR/EDGEの実設定、露光/ISPの時間、UNKNOWN時計の機種挙動、nice -1の持続効果は実機確認が必要。
統計は成功した送信だけで、破棄や2秒超のclock差を含まない。過負荷時にp95だけが良く見える可能性があるので、
解析/送信fps・件数と一緒に評価する。
CaptureResult診断やカメラpolicy変更は別提案とし、今回の差分には入れない。
