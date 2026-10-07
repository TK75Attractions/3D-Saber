# Android 本番 sender（Phase 2）

単一repoの Git root は Unity project です。Android Studio では `PhoneSaber/android/` を開きます。以下のコマンドは Git root から `cd PhoneSaber` して実行します。

2026-10-06 の決定: iPhone/Mac の本番・診断は維持し、AQUOS sense9
（Android 14 / Snapdragon 7s Gen 2）用の本番 sender を追加する。
Windows または Mac の Unity と同じ Wi-Fi に接続し、既存の UDP 契約を使う。
Android に Debug Recording、診断受信は移植しない。
当日のネットワーク確認用に既存C++ measurement modeをJNIから公開し、「遅延計測モード」を追加（既定OFF、保存しない）。
長時間運転用の端末状態（発熱・電池・実測解析fps・JNI処理中央値）は通常画面に表示する。


## AQUOS sense9 でのビルド・インストール

対象は AQUOS sense9（Android 14 / API 34、Snapdragon 7s Gen 2）。
`PhoneSaber/android/` 自体を Android Studio の **Open** で開く（リポジトリルートではない）。
core のアルゴリズムとUDP仕様は維持する。

1. SDK Manager → SDK Platforms で Android API 37 を用意する。この環境には
   `platforms/android-37.0` と `build-tools/36.0.0` が既にある。
2. SDK Tools → **Show Package Details** を有効にし、**NDK (Side by side)
   30.0.16248370（r30 LTS）** と **CMake 3.22.1** をインストールする。
   Gradle JDK は Studio の Embedded JDK（17以上）を選ぶ。
3. オンラインの開発環境で Gradle Sync を実行する。Gradle 9.8.0、AGP 9.2.1、
   AGP 内蔵 Kotlin、AndroidX / CameraX 1.6.2 を使う。別の Kotlin Android plugin は不要。
   `compileSdk=37 / targetSdk=35 / minSdk=29`、ABI は `arm64-v8a` のみ。
   API 37 を扱える Android Studio を使う（Panda 3 2025.3.3 Patch 1 以降）。
4. sense9 の設定 → デバイス情報 → ビルド番号を7回タップし、開発者向けオプションの
   **USBデバッグ** を有効にする。USB接続し、PCのRSAキーを端末で許可する。
   Android Studio の実行先に sense9 を選び、`app` を **Run** する。
5. 端末と Windows/Mac を同じ Wi-Fi に接続し、Unity の Play/受信を開始する。
   PC欄の名前・IPを確認して **開始**。カメラ権限の許可は初回だけ。
   見つからなければPCのIPv4を入力して保存。空欄を保存すると自動探索に戻る。

端末で使う画面は「開始/停止」「PC名・IP・探索状態/手入力」「赤/青の検出・予測・保持状態」
「色別送信fps」「認識の閾値（明るさ145、色の優位差25）」がある。
端末状態行には「発熱 正常 / やや高い / 高い / 危険」、電池%・充電状態、
直近5秒の実測解析fpsとJNI処理msの中央値、撮影→送信（センサー露光時刻からJNI処理完了まで）の中央値も表示する。送信中は Wi-Fi の低遅延ロック（`WIFI_MODE_FULL_LOW_LATENCY`）で省電力を止める。C++ core は Debug でも -O2（-O0 では sense9 で約300ms/フレームとなり、180msの鮮度制限で全送信が破棄された）。高い・危険、または開始3秒後から
解析fpsが要求30fpsの70%未満（21fps未満）になると注意行を出す。
カメラfpsや認識を自動調整しない。長時間運転の対処は
[当日runbook](../docs/claude/EVENT_DAY_RUNBOOK.md#4-正常な状態) を参照。
両色共通の既定彩度30、sample step=2は固定。閾値変更は停止中に行う。
反転はAndroidの閾値の下、iPhoneの「詳細設定」→「検出」にある「左右反転」「上下反転」で設定・保存する（既定OFF、Androidは停止中のみ変更可）。同じ台ではiPhoneとAndroidを同じ反転設定にする。
送信中は画面を点灯状態に保つ。画面を離れる・停止・Wi-Fiや送信先が変わると停止し、
確認後に開始し直す。Debug Recording、診断、P2Pはない。
「遅延計測モード」は停止中だけ変更でき、iPhoneと同じラベル。
ONで開始すると既存の `ts=<epoch>;x1,y1,x2,y2` を送り、PCのF8で直近5秒の間隔・片道差・判定を確認する。
片道差にはスマホとPCのNTP同期が必要。間隔は同期不要。手順と判定閾値は[当日runbook](../docs/claude/EVENT_DAY_RUNBOOK.md#会場でのネットワーク確認f8)。

Gradle wrapper は公式の Gradle 9.8.0(SHA-256 確認済み)で `gradle wrapper` により生成した。
2026-10-06 に Mac で `./gradlew assembleDebug testDebugUnitTest` が通った(JVM テスト 16 件)。

```bash
cd android
./gradlew :app:testDebugUnitTest :app:assembleDebug
./gradlew :app:installDebug
# Windows は gradlew.bat を使う
```

APKは `app/build/outputs/apk/debug/app-debug.apk`。Run/debug APKにも診断機能は含めない。
公開配布用の署名鍵・keystoreはこのプロジェクトに入れていない。

## MacのAndroidエミュレータで送信経路を確認（開発用）

Debugビルドだけに「**開発用: 非Wi-Fiで送信を許可（手入力IP必須）**」スイッチがある。
初期値はOFFで保存しない。ONのときはactive networkを使い、仮想Ethernet/携帯回線でも
手入力IPへ送信できる。非Wi-Fiでの自動探索は行わない。Releaseにはスイッチを表示せず、
手入力IPがあってもWi-Fiを必須にする。切替・送信先変更・ネットワーク変更・画面終了では
停止するので、確認して開始し直す。

1. MacのAndroid Studio → Device Managerで **ARM64 (`arm64-v8a`)** のAPI 29以上のAVDを用意する。
   APKのABIはarm64だけなのでx86_64 AVDではJNIを読み込めない。背面カメラは
   webcam、またはVirtual Sceneに設定する。固定AE [30,30]を広告するカメラが必要で、
   非対応のエラーが出るAVDではカメラ設定/イメージを変える（アプリは要求fpsを緩めない）。
2. `cd PhoneSaber/android` → `./gradlew :app:testDebugUnitTest :app:assembleDebug`。
   `adb devices`でAVDのserialを確認し、
   `adb -s <serial> install -r app/build/outputs/apk/debug/app-debug.apk`。
   アプリを起動してカメラ権限を許可する。
3. MacでUnityをPlayし、UDP 5005/5006の受信を開始する。別の受信確認用にはUnityを停止して
   次のスクリプトをMacのTerminalで動かす（同じポートを同時に2つの受信側で使わない）。

   ```bash
   python3 -u - <<'PY'
   import select
   import socket
   sockets = []
   for port in (5005, 5006):
       sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
       sock.bind(("0.0.0.0", port))
       sockets.append(sock)
   print("UDP 5005/5006を待機中（Ctrl+Cで終了）")
   while True:
       for sock in select.select(sockets, [], [])[0]:
           payload, peer = sock.recvfrom(1024)
           print(sock.getsockname()[1], peer, payload.decode("ascii"))
   PY
   ```

4. AVDでWi-FiをOFFにし、仮想携帯回線等のactive networkを残す。アプリの開発用スイッチをON、
   PCのIPv4に **`10.0.2.2`** を入力して保存し「開始」。これはAndroid Emulatorから
   ホストMacへ届く特別なアドレスで、MacのLAN IPではない。
5. webcamに赤/青の点灯したsaberを映す。Virtual Sceneの場合はExtended Controls →
   Cameraで赤/青の棒の画像を配置し、プレビューに映す。解析fpsとJNI中央値が更新され、
   「検出」または「予測」のときに色別送信fpsが増え、Macに
   `x1,y1,x2,y2`が届くことを確認する。CameraX → JNI → C++ core → UDPが確認対象。
   「保持（送信なし）」/未検出だけなら送信されないので入力画像・画角を確認する。
6. スイッチOFFでは非Wi-Fiで開始できないこと、停止後に送信が止まり、再開始できることを確認する。
   Releaseでも非Wi-Fiを拒否することを別途確認する。エミュレータの成功では
   sense9の熱・カメラISP・実Wi-Fi・Android上のbit parityまでは確認できない。

ツール版の参照: [AGP 9.2 の互換表](https://developer.android.com/build/releases/agp-9-2-0-release-notes)、
[Gradle 9.8.0](https://docs.gradle.org/9.8.0/release-notes.html)、
[NDK r30](https://developer.android.com/ndk/downloads)、
[CameraX](https://developer.android.com/jetpack/androidx/releases/camera)。

## Windows / Mac とPC探索

Windows Defender Firewall の「アプリを許可」で Unity Editor（または実行したゲーム）を
**プライベートネットワーク**で許可する。受信の詳細規則を使う場合は、同じUnity実行ファイルへ
**UDP 5005–5007** を許可する。5005=赤、5006=青、5007=探索応答。
Macもファイアウォールが有効ならUnityの受信を許可する。
ゲストWi-Fi・APのクライアント分離・VPNがあると探索や座標受信ができない場合がある。

- UDP探索: Wi-Fiへbindした一時ポートのソケットから2秒ごとにASCII
  `PHONESABER_DISCOVER 1` を `255.255.255.255:5007` と各IPv4サブネットの
  directed broadcastへ送る（prefix 1–30）。Unityの
  `PhoneSaberDiscoveryResponder.cs` が同じソケットへunicastで返す
  `PHONESABER_UNITY 1 red=5005 blue=5006 name=<PC>` を解析する。
  パケットの送信元IPを使用する。広告ポートの値も検証し、固定契約5005/5006に
  合わない応答は採用しない。8秒応答がないUDP候補は失効する。
- Bonjour: Android NSDで既存の `_phonesaber._udp.` を探索・直列resolveする。
  API 33以上ではWi-Fi networkを指定し、API 34以上ではIPv4を優先する。
  座標の送信ポートはBonjourサービスの広告ポートによらず5005/5006。
  サービス消失で候補を解除する。Android自身はサービスを公開しない。
- 自動探索は最初に見つかったPCを維持する。複数PCがある会場では意図したPCの
  IPv4を手入力して保存する。SharedPreferencesの手入力は自動探索より優先する。
  UDPには受信確認がないため、表示するPCは「探索で発見した送信先」であり接続保証ではない。
- 探索中（画面表示中）は `WifiManager.MulticastLock` を保持し、停止・画面終了・
  ネットワーク切替でソケット/NSD/lockを解放する。Wi-Fi networkへのbindは、モバイル通信が
  default networkでも座標がWi-Fiを使うため。Wi-Fiスキャン/接続変更APIは使わないため、
  `NEARBY_WIFI_DEVICES` や位置情報の実行時権限は要求しない。

## カメラ・座標・送信の一致条件

本番iPhoneは `CameraViewModel` の `preferredCameraFormatIndex` で30fps対応の
640×480以下の最大サイズを選び、それがなければ30fps対応の最小サイズを使う。
AVFoundationの非ミラー `.portrait`、32BGRA、stabilization off、自動露出が既定。
通常VGAなら**検出入力は480×640のportrait**で、出力座標は1920×1080に正規化する。
Androidも背面カメラ・同じサイズ選択方針、固定AE range [30,30]、自動露出・手ぶれ補正offを要求する。
固定30fpsを広告しないカメラではエラーにし、別fpsへ黙って切り替えない。
実際のサイズは画面に表示する。AE/ISPによって実際のフレーム間隔が変わる可能性は残る。

CameraXは `OUTPUT_IMAGE_FORMAT_RGBA_8888` / `STRATEGY_KEEP_ONLY_LATEST`。
`ImageProxy.planes[0]` のbyte順R,G,B,A（[API仕様](https://developer.android.com/reference/androidx/camera/core/ImageAnalysis)）を使い、
pixel stride=4、row strideとposition/limitを検証する。target rotationは常に
`Surface.ROTATION_0`（portrait）。CameraXの自動画素回転は無効で、
`imageInfo.rotationDegrees` の時計回り回転を **検出前** に適用する。
画面回転・Unity座標変換を追加せず、背面の非ミラー画像をx右/y下に揃える。

| 元画像の点 (x,y)、サイズW×H | コアへ渡す点 | コアのサイズ |
| --- | --- | --- |
| 0° | (x,y) | W×H |
| 90° | (H−1−y,x) | H×W |
| 180° | (W−1−x,H−1−y) | W×H |
| 270° | (y,W−1−x) | H×W |

回転はRGBの値・チャンネル・alphaを変えない。0°かつstride×heightの全バイトがある場合は
DirectByteBufferのsliceをそのままJNIへ渡す。90/180/270°は再利用するdirect scratchへ
画素を1回コピーする。最終行のpaddingを省略する0°のplaneも、既存コアのstorage条件を
満たすためtight strideへ詰め直す。検出後に端点だけ回転するとsample step=2の格子が
変わるので、その方式は使わない。viewport crop・resize・gamma変換は追加しない。
CameraXのYUV→RGBA変換とiPhoneのBGRA生成はISP/色変換が異なり、入力の完全一致は保証しない。

JNIは `core.hpp` の `FrameProcessor` public APIだけを呼ぶ。カメラ/expiryは同じ直列executor、
ImageProxyはJNIが戻るまで保持し必ずfinallyでcloseする。セッションの開始/停止では
コアを作り直してreset相当とし、180ms期限は `next_expiry` / `expire` で予約する。
期限処理は送信しない。破棄したCameraXフレームを欠落フレームとして数えない。

`FrameResult.text` のfresh結果だけを背景UDP workerへ渡す。文字列はコアが生成した
通常のASCII `x1,y1,x2,y2` をそのまま送る。RED=5005 / BLUE=5006、
output=1920×1080、mirrorX/Yは保存した設定（既定false）を使う。各辺の「寸法−1」で正規化し、Swiftと同じ四捨五入は
既存コアが行う。最大3処理フレームの予測はfreshなので送るが、held/expired/absentは送らない。
ゼロ座標、heartbeat、送信停止パケットを追加しない。
計測スイッチONのセッションだけ、JNIから既存C++の `measurement_mode` を有効にする。
JNIは検出後・文字列生成直前に壁時計のUnix epoch秒を採り、両色へ渡す。
既存 `ts=%.6f;...` formatterをそのまま使い、Kotlin/UDP workerは無加工で転送する。
撮影・認識時間は片道差に含まない。OFFの通常経路・認識・fresh/held/予測は変更しない。

UDPは待機フレーム1個の置換mailboxでnewest-wins、ノンブロッキングソケット。
新しいフレームは空結果も含めて古い待機フレームを置き換え、送信失敗・socket満杯は破棄する。
再送FIFOはない。処理開始から180ms以上古い結果は送信直前に破棄する。
停止後のin-flight検出結果は世代チェックで破棄し、停止/送信先変更でmailbox/socketも消す。
送信fpsは色別の成功したOS送信回数/秒で、Unity受信fpsや無線遅延の測定ではない。

## 検証状況と残る確認

上記の2026-10-06ビルド結果は端末状態・開発用ネットワーク設定を追加する前のもの。
今回追加したJVMテストとDebug/Releaseビルド、上記エミュレータの実送信は改めて確認する。

JVMテスト（`app/src/test`）には探索応答parser・不正応答・ポート契約・手入力IP、
コアpayloadの無加工routing・fresh/held/predicted、0/90/180/270°回転・row padding・
非zero plane position・チャンネル保持・scratch再利用・不正入力を追加した。
端末状態の日本語分類/表示、警告境界・ウォームアップ、直近5秒の中央値・失速時の失効、
およびReleaseのWi-Fi必須・Debugの手入力IP/明示ON条件もJVMテストに含む。
JNIの正式lossless fixture testは下記を参照。CameraX/NSD/ライフサイクルのinstrumented testは未実装。

実機で必要な確認: sense9のRGBA配置/stride、VGA→480×640の向きと画面四隅のUnity mapping、
permission拒否/再許可、開始/停止/再開・画面終了・Wi-Fi切替時の資源解放と古い送信の破棄、
Windows UDP探索/ファイアウォール、Mac UDP/Bonjour探索、手入力IPの再起動後保持、
赤/青の候補・端点、実処理/送信fps、発熱・長時間運転、両OSのUnity受信とend-to-end遅延。
**Android NDK/bionic上のbit parityは未確立**。MacのPhase 1 parity成功だけでは証明できない。
既存corpusをNDK上で検証する作業が残る（[core/EXACTNESS.md](core/EXACTNESS.md)）。

## JNIの正式lossless fixture test

`app/src/androidTest` の `NativeCoreLosslessTest` は正式manifestの40件（35 unique PNG）を
Androidでdecodeし、本番カメラと同じ `NativeCore.process` JNIへ渡す。
GradleのandroidTest assetsは `../ios/PhoneSaberSenderTests/Fixtures` を直接参照し、
`../ios/PhoneSaberSender/Tools/lossless_regression_manifest.json` だけをbuild内へコピーする。
PNGをAndroidソースへ複製しない。manifestの実パスに空白はない。

`BitmapFactory` はARGB_8888、sRGB、スケーリング・premultiply無効でdecodeし、
ARGB整数からRGBA bytesへ明示的に変換する。全画素のalpha=255と、Mac PNG CLIの
無変換RGBA SHA-256を検査するため、色空間変換などが画素を変えた場合は認識前に失敗する。
0度の `RotationHelper` を通し、tight strideと13 bytesの行末paddingの両方を検証する。
各入力でNativeCoreを作り直し、履歴・予測・端点順序の持ち越しを防ぐ。

RED/BLUEの有無、fresh/非予測、ポート、既定1920×1080・反転OFFのpayloadをMac期待値と
完全一致で比較する。Debug限定JNI probeは既存 `analyze` を読み取り専用で呼び、元画像の
selected端点を両色とも順序込みで完全一致、candidateTypeも一致で検査する。
さらに全40件のmanifestについてdetected、candidateType、順序反転を許す平均端点距離と
`endpointTolerancePx`、`expectedRejectedCandidateTypes` を既存
`run_lossless_regression.py` と同じ条件で検査する。probeはReleaseにリンクしない。
候補の内部浮動小数点値のbit一致やCameraXのISP出力は、このtestの検証範囲に含まれない。

Mac生成の `app/src/androidTest/assets/native_fixture_expectations.json` をソース管理対象として
置く。再生成はarm64 MacでGit rootから実行する（clang++/make/zlib、Python 3が必要）:

```bash
python3 -B PhoneSaber/android/core/tools/generate_android_fixture_expectations.py
```

generatorは既存C++ `tools/png_cli.cpp` を一時ディレクトリでbuildして全正式PNGを処理し、
既存manifest loader/evaluatorでSHA-256と40件の期待値を検証してからJSONを書き出す。
payloadは既存 `tools/frame_cli.cpp` と既定のcore座標変換で生成する。
正式manifest・PNG・認識アルゴリズムは変更しない。生成コマンドはJSONにも記録する。

ARM64、API 29以上のemulatorまたはUSB deviceで実行する。このMacのAVDは `psparity`:

```bash
# Android StudioのDevice Managerでpsparityを起動するか、Terminalで:
"$HOME/Library/Android/sdk/emulator/emulator" -avd psparity
# 別Terminal:
cd PhoneSaber/android
export JAVA_HOME="/Applications/Android Studio.app/Contents/jbr/Contents/Home"
export ANDROID_HOME="$HOME/Library/Android/sdk"
"$ANDROID_HOME/platform-tools/adb" devices
./gradlew connectedDebugAndroidTest
```

deviceの場合はUSBデバッグの認証を済ませる。複数のdeviceがある場合は
`ANDROID_SERIAL=<serial> ./gradlew connectedDebugAndroidTest` で選択する。
カメラ権限・Unity・Wi-Fi送信は不要。レポートは
`app/build/reports/androidTests/connected/debug/index.html`。
初回はAndroidX test runner等の依存取得にネットワークが必要で、未cacheのoffline buildは通らない。
サンドボックスでは `~/.gradle` のlock書き込みやGradle daemonのsocketも制限される場合がある。
今回のサンドボックス検証ではMac oracle生成と正式40/40、oracleの再生成時のbyte一致、
NDK 30のarm64 Debug/Release CMake buildが成功した（probeのDebug存在・Release不在も確認）。
KotlinのAndroid API型検査も一時的なInstrumentationRegistry stubで通ったが、
Gradleはcache lock書き込み拒否、別の一時cacheでもlock管理socketの拒否で起動できず、
JVMテスト・APK build・instrumented実行は未確認。実行結果は上記コマンドで確認する。

## Phase 1 の実装（維持）

`core/include/phonesaber/core.hpp` がプラットフォーム非依存の C++17 API。
標準ライブラリ以外の依存はなく、JNI、CameraX、ソケット、OS 時計を含まない。

- `src/detection.cpp`: `DetectionCore.swift` の成分評価、PCA、軸方向ヒストグラム、ロバスト端点、スコア。
- `src/pipeline.cpp`: `BGRADetection.swift` の HSV・diffuser マスク、形態処理、8近傍 BFS、候補生成・重複排除、RED/BLUE の eligibility とランキング、元画素を読む RED `warmNoDeepRed` ゲート。
- `src/frame_processor.cpp`: 本番 `FrameProcessor.swift` の色別履歴、端点順序維持、最大3欠落フレームの外挿、180ms のプレビュー保持、期限切れ、解像度変更時のリセット。
- 同ファイルの座標変換・文字列生成は `DetectionCore.swift` と `CameraViewModel.handle` の送信契約に対応。

`PixelBuffer` は RGBA/BGRA、幅・高さ、バイト単位の row stride、利用可能な
バッファ長を明示する。alpha は無視し、行末パディングは読まない。
既定 threshold は brightness=145 / dominance=25 / saturation=30、sample step=2。
画像は既存 iPhone の非ミラー portrait キャプチャと同じ向き（x右、y下）で渡す。
コアは追加の回転・リサイズ・自動threshold調整をしない。

`FrameProcessor` はカメラセッションごとに1個を直列実行する。処理開始時の
monotonic time と、各色の送信時点の Unix epoch を呼び出し側から渡す。
`FrameResult.text` がある場合だけ、その `port` へ UDP を送る。
新規検出と予測は fresh、held はプレビューだけ。過去1回の検出だけでは予測しない。
予測は実検出履歴を書き換えず、送信停止にゼロ座標・空文字・heartbeat を使わない。

通常 payload は厳密に `x1,y1,x2,y2`。
既存 measurement mode の文字列関数は `ts=%.6f;x1,y1,x2,y2` と互換で、
当日用の「遅延計測モード」から利用する（既定OFF）。Debug Recordingなどの診断モードは追加しない。
RED=5005、BLUE=5006、既定出力1920×1080、mirrorX/mirrorY=false。
source/output 各辺の「寸法−1」で正規化して四捨五入する既存仕様を維持する。
端点の入れ替え以外の平滑化や候補の時間方向選好は、現在の本番経路に存在しない。

## Mac でのビルドと parity

`PhoneSaber/` から:

```bash
make -C android/core
make -C android/core test
python3.12 -B android/core/tools/compare_parity.py --json /tmp/phonesaber-cpp-parity-report.json
git diff --check
```

Make は `clang++ -std=c++17 -O2 -Wall -Wextra -Werror -ffp-contract=off` を使い、
既定の成果物を `/tmp/phonesaber-cpp-core` に置く。C++ core に外部依存はない。
PNG CLI だけ macOS の zlib をリンクする。Python 比較には xcrun/swiftc、ffmpeg、ffprobe が必要。
Swift module cache も一時ディレクトリに置く。比較は入力を読み取るだけで、fixtureやinboxを変更しない。

```bash
/tmp/phonesaber-cpp-core/phonesaber-png path/to/original.png
# Android app の JNI も同じ静的ライブラリをリンクする
cmake -S android/core -B /tmp/phonesaber-cmake -DCMAKE_BUILD_TYPE=Release
cmake --build /tmp/phonesaber-cmake
# Mac PNG CLI も作る場合は configure に -DPHONESABER_BUILD_TOOLS=ON
```

CMake は Android toolchain を指定しても core のみなら zlib 不要。
この環境には `cmake` コマンドがないため、今回は Make/clang++ 経路を実行した。

比較内容:

1. 既存 `lossless_regression_manifest.json` の SHA-256 と40件の期待値を、
   `run_lossless_regression.py` の既存 loader/evaluator で確認する。
   detected、candidateType、expectedRejectedCandidateTypes、endpointTolerancePx をそのまま使う。
   Swiftとの端点比較自体には tolerance を使わない。
2. `~/Library/Application Support/PhoneSaber/diagnostics-inbox/*/images/*.png` の全 original を比較。
   名前に `annotated` を含む画像は描画済みの診断画像なので除外し、同内容でもファイルを間引かない。
3. 既存 `VideoDetectionDiagnostic.swift` と本番 Swift 2ファイルを `swiftc -O` でコンパイルする。
   一時コピーの runner に production 値の `Double.bitPattern` signature だけを追加する。
   選択結果・全候補の順序、eligibility、source、端点、raw/robust geometry、全保存済み
   production double と score breakdown の各項をビット一致で比較する。
   時間計測と診断専用 trace/counter は比較しない。診断のアルゴリズムはC++へ移植しない。
4. C++ の最小 PNG decoder（8-bit RGB/RGBA、非interlace、PNG filter 0–4）の
   出力全バイトを、既存 Swift runner の ffmpeg BGRA 入力と比較する。
   PNG の色管理・gamma変換・premultiply は適用しない。未対応形式はエラーにする。
   各画像で PNG/RGBA、連続 BGRA、13バイト行パディング付き RGBA の3経路を比較する。
5. `frame_reference.py` が本番 `FrameProcessor.swift` から Track/state transition/
   stable/prediction/expiry を抽出し、テスト専用 Swift adapter を作る。
   `DetectionCore.swift` の本物の payload 関数で、541操作の履歴・予測・保持・期限・リセット・
   mirror・timestamp・送信/非送信を比較する。別実装の Swift 正解値は保持しない。
6. 空画像・赤/青の棒・白飛び・暖色・点LEDの合成入力を、奇数寸法・sample step 1/2/3・
   BGRA/padded RGBA で比較する（18ケース、36経路）。
7. `tools/test_compare_parity.py` は比較器が1 ULP、符号付きゼロ、eligibility、
   候補数、選択端点の差を見逃さないことと、original探索・stride変換を unittest で確認する。

2026-10-06 の結果: 正式40/40（35 unique PNG）、inbox original 213 PNG
（annotated 5枚除外）、画像248枚 × 3入力経路、差分0。状態遷移541操作、合成入力18ケースも差分0。
Apple clang 21.0.0 / Swift 6.4 / arm64 macOS 上で検証。
ビット一致の条件・Androidで未検証の点は [core/EXACTNESS.md](core/EXACTNESS.md) を参照。
