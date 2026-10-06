# Android sender（Phase 1）

2026-10-06 の決定: iPhone/Mac の本番・診断は維持し、AQUOS sense9
（Android 14 / Snapdragon 7s Gen 2）用の本番 sender を追加する。
Windows または Mac の Unity と同じ Wi-Fi に接続し、既存の UDP 契約を使う。
Android に Debug Recording、診断受信、計測 UI を移植しない。

## Phase 1 の実装

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

## Phase 2 のアプリ構成（未実装）

```text
Kotlin / CameraX ImageAnalysis（RGBA_8888, KEEP_ONLY_LATEST）
  → portrait の画素バッファと row stride
  → JNI（DirectByteBuffer、C++ FrameProcessor を直列所有）
  → fresh の payload
  → Kotlin UDP sender → 同一 Wi-Fi の Unity（5005 / 5006）
```

- CameraX の `OUTPUT_IMAGE_FORMAT_RGBA_8888` を使い、ImageProxy の実際の
  channel 配置・pixel stride・row stride を端末で確認する。`rotationDegrees` を
  解決してからサンプリングする。座標だけ回転すると sample step の格子が変わるため、
  iPhone 相当の入力画像の向きを先に統一する。前面/背面カメラのミラーも明示設定する。
- Kotlin の `NsdManager` で、Unity が公開する既存 Bonjour サービス
  `_phonesaber._udp`（Android API では通常 `_phonesaber._udp.`）を検出・resolve。
  resolve したホストへ、色別の既存固定ポート5005/5006で送る。手動 IP 入力も用意する。
  Android アプリ自身を診断受信サービスとして公開しない。
- カメラ権限、ローカルネットワークの利用、ネットワーク変更時の再探索、
  session lifecycle、executor、JNI バッファ寿命、必ず `ImageProxy.close()` する所有関係を実装する。
- 開始/停止・カメラ変更では `reset()`、解像度変更ではコアの履歴リセットを使う。
  世代の古い callback の破棄と `running` チェックは Kotlin 側で行う。
  iPhone と同じ180msの期限で `next_expiry()` / `expire()` を呼び、休止中も履歴を消す。
  期限処理は UDP を送らない。カメラ停止を「欠落フレーム」として繰り返し入力しない。
- NDK の arm64-v8a ライブラリ、Gradle/manifest、最小 UI、実機 APK は Phase 2。
  sense9 で画素配置・向き・候補・端点、処理時間、発熱、Windows/Mac Unity 受信を確認する。
  画質/ISP/露出差があるため、同じアルゴリズムでも実カメラの入力が同一になる保証はない。

依頼時は Android Studio/SDK 未導入の前提だった。今回の環境確認では
`/Applications/Android Studio.app` は存在し、`~/Library/Android/sdk` には
emulator/system-images/licenses がある。一方 platforms、build-tools、cmdline-tools、NDK はない。
Phase 2 には Android SDK/NDK と CMake の開発環境を完成させる必要がある。
この Phase 1 ではインストールや Android ビルドを行っていない。

## Mac でのビルドと parity

リポジトリルートから:

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
# 将来の NDK/JNI からも同じ静的ライブラリをリンクする
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
