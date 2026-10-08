# ios-ui-load（2026-10-09）

CameraViewModel のカメラ・検出結果・UDP 完了通知を、ロック付き最新値ボックスで集約し、MainActor の timer で **12 Hz** 公開する。各フレームの Task を廃止し、Published は値が変わった場合だけ代入する。検出・試行・完了・エラー数は通知をすべて累積する。停止時には未公開カウンタを確定し、停止後の遅延通知で overlay が復活しないようにした。

Debug Performance は既存の120サンプル窓・最大値・入力間隔を off-main で記録し、表示のみ最大5 Hz。Debug Recording のフレーム記録・候補診断・送信開始コールバックと端末 health の1 Hz/発熱通知は維持。preview/overlay は session・端点・画像サイズが同一なら SwiftUI 更新を省く。認識、浮動小数点演算、payload、RED 5005 / BLUE 5006 / discovery 5007、CoordinateDelivery の経路選択は変更していない。

## この Mac での前後計測

arm64、macOS 26.6.2、Apple Swift 6.4、`swiftc -O`。60 fps × 3秒 = 180フレーム、赤青各1本、毎フレーム端点を変更。送信完了時刻は処理開始と同じ模擬値。変更前（開始時 HEAD `dca0c41`）と変更後から**本番の UI メソッドを抽出してコンパイル**し、Combine `objectWillChange` で Published の実代入数を数えた。カメラ通知・結果・赤青完了の4処理を変更前では各 Task、変更後では12 Hz の同一スナップショットにまとめる。

| 3秒間 / 毎秒 | 変更前 | 変更後 |
|---|---:|---:|
| MainActor 更新処理 | 720 / 240 | 36 / 12（95%減） |
| Published 代入通知 | 4,142 / 1,380.67 | 331 / 110.33（92.0%減） |

両方とも処理180、赤青それぞれ試行180・完了180を assertion で確認。同じ計測を複数回行い上記数値を再確認。Release 相当の UI 更新負荷を測るハーネスで、認識・実 UDP・SwiftUI 描画・消費電力・実機の発熱や jitter は測っていない。表示には最大約83ms＋MainActor の混雑による遅れがある。送信には UI の timer/描画完了を待つ処理を追加していない。

再現（repo root）：

```bash
git show dca0c41:PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift > /tmp/ios-ui-load-before.swift
python3 PhoneSaber/ios/PhoneSaberSenderTests/measure_ui_load.py --source /tmp/ios-ui-load-before.swift
python3 PhoneSaber/ios/PhoneSaberSenderTests/measure_ui_load.py
python3 PhoneSaber/ios/PhoneSaberSenderTests/measure_ui_load.py --checks
```

## 検証

- Mac XCTest（本番集約器＋iOS テストから抽出）：**3/3 PASS**。fresh/predicted/held/missing、旧世代、完了/エラー/解除、120サンプル保持、スナップショット保持中の producer 継続を確認。
- iOS の60 fps timer上限、変化しない Published の通知抑制、停止後の overlay、既存の非同期テストを追加・調整。全 iOS テストの **build-for-testing PASS**（実行ではない）。
- Debug / Release build PASS：`xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Debug`（または `Release`）`-destination 'generic/platform=iOS' -derivedDataPath /tmp/ios-ui-load-debug`（または `-release`）`CODE_SIGNING_ALLOWED=NO 'OTHER_SWIFT_FLAGS=-Xfrontend -disable-sandbox' build`。テストのコンパイルは同じ Debug コマンドの末尾を `build-for-testing` にした。
- `bash PhoneSaber/tools/verify_phone_saber.sh` は実行済み。**Detection PASS、formal lossless 40/40 PASS、diff --check PASS**。全 PASS ではない（ログ：`PhoneSaber/.verify-logs/phone-saber/20261009-010316/`）。
- iOS XCTest：CoreSimulatorService 接続拒否で simulator を選べず未実行。通常の iOS Release：sandbox 内の SwiftUI macro plugin が malformed response で失敗。上記フラグによる Debug/Release/テストコンパイルは成功したが、Simulator 実行の代替にはならない。
- Python Tools：384 tests、failure 1 / error 1 / skip 1。tracking E2E は `insufficientDiskSpace(required:1040187392, available:0)` で中断（`df` の実空きは442 GiB）。P2P launcher の子 compiler 検出は単独再実行でも失敗し、独立した sleep プロセスによる確認でも `pgrep` が `sysmond service not found / Cannot get process list` を返した。制約を避けるための gate 変更はしていない。
- Unity は Editor がプロジェクトを所有しており、公式スクリプトでは NOT_RUN / capability BLOCKED。今回の scope 外。

コミットは未完了。通常の `git add` と、共有 `.git` を明示した再試行の両方が、`/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-ios-ui-load/index.lock` の作成時に `Operation not permitted` で拒否された。指定された書き込み root と実際の sandbox の挙動が一致していない。ブランチは `opt/ios-ui-load`、HEAD は `dca0c41` のまま。コミットメッセージは `/tmp/ios-ui-load-commit.txt` に用意済み。push はしていない。
