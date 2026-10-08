# ios-detect2：認識結果を変えない第2回高速化

2026-10-09、arm64 Mac、Xcode 27.0 (27A266a)、Apple Swift 6.4。変更前は `b7c896f` の対象ファイル（第1回 `289b992` の実装）。`FrameProcessor.swift`・UDP・Unity は変更していない。

- HSV/mask：整数明度・chroma による厳密な早期除外、行アドレスの計算移動、配列の有効範囲内でのポインタ書き込み。必要な HSV 演算は元の式のまま。
- morphology：連続する非ゼロ画素の Manhattan 膨張を行区間単位でまとめ、半径1/2の収縮を同じ近傍の明示的な読み出しに展開。
- BFS：訪問順を保った FIFO を採点用画素列として再利用し、二重 append と画素ごとの除算を削減。
- 候補採点：clipped-white bitmap を bbox + 半径2、proposal 所属 bitmap を bbox 内だけに確保。evidence 読み出しをポインタ化。
- 自己レビュー：閾値・定数・ranking・eligibility・score/PCA の式と浮動小数点の加算順は維持。新たな FMA/fast-math 指定なし。ハッシュの反復順は認識計算に入れない。

## 実測

`blue-led-bright-large-05.png` (480×640)、CoreGraphics による XCTest と同じ BGRA 描画、sampleStep=2、native `swiftc -O`。各条件5回 warm-up、500回計測を3組、実行順 before/after、after/before、before/after。profile/diagnostics を無効にした実時間の平均。

| 条件 | 変更前 avg (ms) | 変更後 avg (ms) | 短縮 |
|---|---:|---:|---:|
| bright（1500回） | 3.476 | 2.220 | 36.1% |
| empty（1500回） | 0.762 | 0.224 | 70.6% |

bright 各組 avg：3.470→2.186、3.455→2.198、3.504→2.276 ms。別途 profile を有効にしたステージ平均：HSV/mask 0.979→0.330、morphology 0.743→0.320、BFS 0.290→0.227、brightness/contrast 0.391→0.347 ms。最適化前に計測してこのホットスポットを確認した。`sample PID 5 1` は「sample cannot examine process … try running with sudo」で取得不能だったため、組み込みのステージ計測を使用。

**シミュレータの `testOfflineBrightFrameTimingBaseline` bright avg < 8 ms は未確認。native 数値をシミュレータ数値として扱わない。**

再現（リポジトリ root、生成物は /tmp）：

```bash
build=$(mktemp -d /tmp/ios-detect2.XXXXXX)
src=PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender
tests=PhoneSaber/ios/PhoneSaberSenderTests/StaticBGRADetectionTests.swift
git show "b7c896f:$src/DetectionCore.swift" > "$build/DetectionCore-before.swift"
git show "b7c896f:$src/BGRADetection.swift" > "$build/BGRADetection-before.swift"
xcrun swiftc -O "$build/DetectionCore-before.swift" "$build/BGRADetection-before.swift" "$tests" -module-cache-path "$build/modules" -o "$build/before"
xcrun swiftc -O "$src/DetectionCore.swift" "$src/BGRADetection.swift" "$tests" -module-cache-path "$build/modules" -o "$build/after"
for phase in before after after before before after; do
    "$build/$phase" --benchmark PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/blue-led-bright-large-05.png 500
done
```

## 検証

- `make -C PhoneSaber/android/core parity`：PNG 269枚（fixture 35枚 + inbox original 234枚）、合成27ケース、FrameProcessor 541遷移、全て不一致0。formal期待値40件の失敗0。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`：PASS。HSV参照走査との比較、閾値の両極端、padding、sampleStep、全3×3 mask・半径0…4・非二値入力、bbox端・clipped-white・重複/無効座標の回帰を確認。DeviceHealth XCTest 5件もPASS。
- 同じ Static テストソースを変更前後の core とリンクし、`--lossless-signatures` で合成48ケース + fixture PNG 52枚を比較：全 stored property、supportSamplePoints、winner、eligibility、endpoint診断、pipeline診断・profileの整数カウンタ5種を含め完全一致。Double は `bitPattern`、診断 Dictionary はキーを含めて正規化。profile実時間のみ対象外。
- `bash PhoneSaber/tools/verify_phone_saber.sh`：実行したが **全PASSではない**。Detection PASS、formal lossless **40/40 PASS**、Diff Check PASS。ログ：`PhoneSaber/.verify-logs/phone-saber/20261009-004438/`。
- XCTest：CoreSimulatorService が Connection refused/invalid、Simulator inventory を取得できず NOT_RUN（スクリプト分類はFAIL）。2回の公式実行で同じ失敗。
- iOS Release：`sandbox-exec: sandbox_apply: Operation not permitted` により SwiftUI `@State` の macro plugin が malformed response、ビルドFAIL。
- Tools：384件実行、失敗1・エラー1・opt-in skip 1。P2P launcher は `pgrep` で compiler child を検出できない。独立確認でも `pgrep` が exit 3、`sysmond service not found / Cannot get process list`。録画E2Eは `insufficientDiskSpace(required: 1040187392, available: 0)`。検査・安全gateは緩めていない。
- Unity：公式スクリプトでは Editor がプロジェクトを所有中として NOT_RUN/BLOCKED。依頼範囲外なので操作していない。

コミットも試みたが、`git add` / `git commit` は共有 Git dir の `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-ios-detect2/index.lock` を作成できず `Operation not permitted`。通常の所有権は satoshi/staff だが、`os.access(..., W_OK)` はこの worktree Git dir で false（共有 `.git` 自体は true）。この実行環境では書き込めず、4ファイルを未コミットのまま保持。push は行っていない。コミットメッセージ案は `/tmp/ios-detect2/commit-message.txt` に保存した。残る作業はコミット、sandbox 外での公式検証、シミュレータ8 ms目標の実測。
