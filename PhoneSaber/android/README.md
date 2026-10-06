# Android 本番 sender（Phase 2）

単一repoの Git root は Unity project です。Android Studio では `PhoneSaber/android/` を開きます。以下のコマンドは Git root から `cd PhoneSaber` して実行します。

2026-10-06 の決定: iPhone/Mac の本番・診断は維持し、AQUOS sense9
（Android 14 / Snapdragon 7s Gen 2）用の本番 sender を追加する。
Windows または Mac の Unity と同じ Wi-Fi に接続し、既存の UDP 契約を使う。
Android に Debug Recording、診断受信、計測 UI を移植しない。


## AQUOS sense9 でのビルド・インストール

対象は AQUOS sense9（Android 14 / API 34、Snapdragon 7s Gen 2）。
`PhoneSaber/android/` 自体を Android Studio の **Open** で開く（リポジトリルートではない）。
この worktree の変更は未commit。Swift、core のアルゴリズム、UDP仕様は変更していない。

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
「色別送信fps」「認識の閾値（明るさ145、色の優位差25）」だけ。
両色共通の既定彩度30、sample step=2は固定。閾値変更は停止中に行う。
送信中は画面を点灯状態に保つ。画面を離れる・停止・Wi-Fiや送信先が変わると停止し、
確認後に開始し直す。Debug Recording、診断、計測モード、P2Pはない。

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
output=1920×1080、mirrorX/Y=false。各辺の「寸法−1」で正規化し、Swiftと同じ四捨五入は
既存コアが行う。最大3処理フレームの予測はfreshなので送るが、held/expired/absentは送らない。
ゼロ座標、heartbeat、送信停止パケットを追加しない。
コアにある `ts=%.6f;...` は既存measurement modeとの互換APIだが、Androidでは
`measurement_mode=false` を固定し、計測UI/タイムスタンプを有効にしない。

UDPは待機フレーム1個の置換mailboxでnewest-wins、ノンブロッキングソケット。
新しいフレームは空結果も含めて古い待機フレームを置き換え、送信失敗・socket満杯は破棄する。
再送FIFOはない。処理開始から180ms以上古い結果は送信直前に破棄する。
停止後のin-flight検出結果は世代チェックで破棄し、停止/送信先変更でmailbox/socketも消す。
送信fpsは色別の成功したOS送信回数/秒で、Unity受信fpsや無線遅延の測定ではない。

## 検証状況と残る確認

このPhase 2では、ユーザー指定により **Gradle/AGP/NDKのダウンロード、Gradle Sync、
Androidビルド、JVMテスト実行、APKインストールを行っていない**。

SDKにはNDK/CMakeがまだない。Android Studioで上記パッケージを入れてから
JVMテストとビルドを実行する必要がある。

JVMテスト（`app/src/test`）には探索応答parser・不正応答・ポート契約・手入力IP、
コアpayloadの無加工routing・fresh/held/predicted、0/90/180/270°回転・row padding・
非zero plane position・チャンネル保持・scratch再利用・不正入力を追加した。
JNI/CameraX/NSD/ライフサイクルのinstrumented testは未実装。

実機で必要な確認: sense9のRGBA配置/stride、VGA→480×640の向きと画面四隅のUnity mapping、
permission拒否/再許可、開始/停止/再開・画面終了・Wi-Fi切替時の資源解放と古い送信の破棄、
Windows UDP探索/ファイアウォール、Mac UDP/Bonjour探索、手入力IPの再起動後保持、
赤/青の候補・端点、実処理/送信fps、発熱・長時間運転、両OSのUnity受信とend-to-end遅延。
**Android NDK/bionic上のbit parityは未確立**。MacのPhase 1 parity成功だけでは証明できない。
既存corpusをNDK上で検証する作業が残る（[core/EXACTNESS.md](core/EXACTNESS.md)）。

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
既存 measurement mode の文字列関数も `ts=%.6f;x1,y1,x2,y2` と互換で実装しているが、
Android 本番 UI に診断モードを追加する予定はない。
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
