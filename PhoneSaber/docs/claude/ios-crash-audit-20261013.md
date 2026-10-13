# iPhone Sender crash / hang 監査 — 2026-10-13

## 結論と監査条件

ハングにつながる不具合を2件報告する。P1は通知配信と同期停止の相互待ち、P2は録画 overlay の終端 writer を待ち続ける経路。両方とも現行コードを抽出したホスト実行で修正前の失敗と提案後の通過を確認した。iPhone の AVFoundation 障害そのものを起こした実機再現ではない。再現範囲を各項目に明記する。新たな確定クラッシュは見つからなかったが、8時間の実機安定性を保証する結果ではない。

- 対象: `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/` の全16 Swiftファイル。以下のファイル位置はこのディレクトリ相対。行番号は監査開始時のスナップショット、Git HEAD `0fcb3e7` に対応する。
- `PhoneSaber/CLAUDE.md` と `PhoneSaber/AGENTS.md` を先に読んだ。今回の明示指示を優先し、既存ファイルの変更・commit・push・STATUS追記・公式verifyは行っていない。
- リポジトリへの追加はこのノートだけ。以下のdiffは提案であり、適用していない。認識の ranking / eligibility / scoring / PCA / thresholds、UDP本文、formal lossless corpus、Unity scene / prefab / .meta / ProjectSettingsには差分を作っていない。
- Swift全16ファイルの SHA-256 を保存し、実験後に元ファイルが開始時と一致することを確認した。同時作業があるため、採用時にdiffの適用先を再確認すること。
- P1 / P2 は優先順位。P1はアプリ全体が停止してiOS watchdog終了に至り得るもの、P2は録画終了が進まないもの。

## 1. P1 — カメラ通知の main 同期配信と session 同期待ちが相互待ちを作る

**場所:** `CameraViewModel.swift:1025`, `:1033`, `:1040`（通知の `queue: .main`）、`:353`（`CaptureSessionRunner.stopSynchronously`）、`:1556`（MainActor の `stop` から呼び出す）。関連: `:360` の `session.startRunning()`。

**発生順序:**

1. 開始または復旧の `startRunning()` が session キューで実行中。
2. メインスレッドが停止操作（FPS変更に伴う停止を含む）に入り、`stopSynchronously()` の `queue.sync` で session キューを待つ。
3. session の開始・停止が完了する前に、その処理自身、またはその処理が完了待ちしている経路から runtime error / interruption 通知が post される。
4. 現行 observer の `queue: .main` は、そのメインキューで observer block が実行されるまで post 元を待たせる。メインは session 待ち、session 側は通知完了待ちになり、停止ボタンも回復処理も進まない。

NotificationCenter の同期配信はホストで実測した。`startRunning` が同期メソッドで失敗時に runtime error を通知する点は [Apple startRunning documentation](https://developer.apple.com/documentation/avfoundation/avcapturesession/startrunning()) に記載されている。通知の具体的な発信スレッドは端末・障害条件に依存し、**この順序の実機発生は未確認**。ただし main が利用できない間に通知 post が戻らないことは、3通知すべてで再現した。

**提案:** 3 observer の配信を `queue: nil` にする。block は通知内容を値として取り出して既存の `Task { @MainActor ... }` を作るだけなので、UI / recovery の処理先は MainActor のまま。セッション停止の同期性や認識を変えず、post 元を main 待ちから解放する。復旧時の体感が改善する変更を含むため、今回は提案のみ。

```diff
--- a/PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift
+++ b/PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift
@@ -1021,7 +1021,7 @@
         sessionObserverTokens.append(center.addObserver(
             forName: AVCaptureSession.wasInterruptedNotification,
             object: session,
-            queue: .main
+            queue: nil
         ) { [weak self] notification in
             let reason = notification.userInfo?[AVCaptureSessionInterruptionReasonKey]
                 .map { String(describing: $0) } ?? "理由不明"
@@ -1030,14 +1030,14 @@
         sessionObserverTokens.append(center.addObserver(
             forName: AVCaptureSession.interruptionEndedNotification,
             object: session,
-            queue: .main
+            queue: nil
         ) { [weak self] _ in
             Task { @MainActor in self?.handleCameraInterruptionEnded() }
         })
         sessionObserverTokens.append(center.addObserver(
             forName: AVCaptureSession.runtimeErrorNotification,
             object: session,
-            queue: .main
+            queue: nil
         ) { [weak self] notification in
             let error = notification.userInfo?[AVCaptureSessionErrorKey] as? NSError
             let message = error?.localizedDescription ?? "AVCaptureSession runtime error"
```

**回帰テスト:** 下記共通テストdiffの `test_camera_notifications_do_not_wait_for_main_queue`。実際の登録メソッドを抜き出し、実 `AVCaptureSession` を通知 object にする。main をセマフォ待ちにして、別キューの3通知 post が各250ms以内に戻ることを要求する。iOS専用の interruption reason 定数だけホスト用文字列へ置換し、UI handler は空実装にする。修正前: 3通知ともFAIL。提案後: 3通知ともPASS。`-O` と `-O -D DEBUG` の双方で同じ結果。これは配信の相互待ちを防ぐ回帰であり、実カメラの復旧成功を検証するものではない。

## 2. P2 — overlay writer が終端状態になっても readiness 待機を抜けない

**場所:** `DebugVideoRecorder.swift:3135`（`while !input.isReadyForMoreMediaData`）。呼出しは `:2498` の overlay 生成、録画終了待ちの状態は `FrameProcessor.swift:433` の `debugRecordingIsFinalizing`。

**発生順序:**

1. Debug Recording を停止し、raw / metadata / PNG の後処理を終え、overlay の生成に入る。
2. H.264 input にバックプレッシャーがかかり、`isReadyForMoreMediaData` が false になる。
3. その待機中に書き込みエラー（例: 容量枯渇、media service障害）で writer が `.failed` になり、readiness が戻らない。
4. 待機ループは readiness だけを見て1ms sleepを繰り返す。writer の失敗検査と容量検査はループの後なので到達しない。
5. `finish` の completion が呼ばれず、録画の「保存中」/ finalizing が解除されない。通常送信は別キューで継続できるが、次の録画を開始できない。バックグラウンド猶予が終了しても detached export Task のキャンセル処理はないため、前面復帰後もこの待ちが続き得る。

**証拠と限界:** 実 AVFoundation クラスを継承し、input=false と writer=.failed / .cancelled の終端状態を注入した。現行の待機部分をそのままコンパイルすると、どちらも500ms以内にエラー終了しない。修正後はどちらも即座にエラーになる。**実際にiPhoneの容量を枯渇させて、その readiness / status の組合せを再現したわけではない。** 別途行ったホストの非モック writer 実験は `startWriting=false / status=failed / ready=true` であり、こちらは本件の待機条件に入らない。すべての writer 失敗がハングするという主張ではない。

**提案:** 待機中にも `.writing` を確認し、終端なら reader / writer をキャンセルして既存の `appendFailed` に落とす。呼出し元は overlay 失敗を任意の補助画像の失敗として扱うため、完成済みのraw / metadata / losslessを保持して終了できる。通常時の待機や録画上限は変えない。`.writing` のまま永遠に進まない encoder にタイムアウトを導入することは、新しい終了基準になるのでこのdiffには含めない。

```diff
--- a/PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugVideoRecorder.swift
+++ b/PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DebugVideoRecorder.swift
@@ -3133,6 +3133,13 @@
             OverlayRasterizer.draw(frame.red, color: .red, into: pixelBuffer)
             OverlayRasterizer.draw(frame.blue, color: .blue, into: pixelBuffer)
             while !input.isReadyForMoreMediaData {
+                guard writer.status == .writing else {
+                    reader.cancelReading()
+                    writer.cancelWriting()
+                    throw DebugVideoRecorderError.appendFailed(
+                        writer.error?.localizedDescription ?? "overlay writer stopped while waiting"
+                    )
+                }
                 try await Task.sleep(nanoseconds: 1_000_000)
             }
             let presentationTime = CMSampleBufferGetPresentationTimeStamp(sample)
```

**回帰テスト:** 下記共通テストdiffの `test_terminal_overlay_writer_does_not_wait_forever`。元の `while` 部分を抽出して実行する。`.failed` と `.cancelled` を注入し、500ms以内に `appendFailed` が返ることを要求。修正前: 両方FAIL。提案後: 両方PASS。各 `-O` / `-O -D DEBUG` で確認。テストは未完了Taskをキャンセルし、永久待ちを残さない。

## 両不具合の ready-to-apply 回帰テスト

既存のTools unittestに追加する新規テスト案。カメラ・UDP・Bonjour探索を開始せず、ホストで配信と待機の該当コードを実行する。抽出するのは実ソースのメソッド / ループであり、単なるソース文字列の有無の検査ではない。元ソースの実行を保つため、抽出の境界が変わった場合は明示的にテストが失敗する。iOS全アプリの結合テストの代替ではない。

```diff
--- /dev/null
+++ b/PhoneSaber/ios/PhoneSaberSender/Tools/test_ios_crash_liveness.py
@@ -0,0 +1,130 @@
+"""実際の通知登録と overlay 待機を抜き出して実行する、macOS 用回帰テスト。"""
+import os
+from pathlib import Path
+import shutil
+import subprocess
+import sys
+import tempfile
+import unittest
+
+SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"
+
+
+@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Xcode/macOS required")
+class IOSCrashLivenessTests(unittest.TestCase):
+    def run_swift(self, source):
+        for flags in ([], ["-D", "DEBUG"]):
+            with self.subTest(flags=flags), tempfile.TemporaryDirectory(
+                prefix="phonesaber-liveness-", dir=os.environ.get("TMPDIR")
+            ) as temporary:
+                directory = Path(temporary)
+                fixture = directory / "Fixture.swift"
+                fixture.write_text(source)
+                binary = directory / "fixture"
+                compile_result = subprocess.run(
+                    ["xcrun", "swiftc", "-O", "-parse-as-library", *flags,
+                     "-module-cache-path", str(directory / "module-cache"),
+                     str(fixture), "-o", str(binary)],
+                    capture_output=True, text=True, timeout=90,
+                )
+                self.assertEqual(compile_result.returncode, 0, compile_result.stderr)
+                run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
+                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
+
+    def test_camera_notifications_do_not_wait_for_main_queue(self):
+        camera = (SOURCE / "CameraViewModel.swift").read_text()
+        start = camera.index("    private func registerCameraSessionObservers()")
+        end = camera.index("    func sceneDidChange", start)
+        method = camera[start:end].replace("private func register", "func register")
+        # iOS 専用定数を文字列へ置き換える。配信キューと Task の本体は元コードのまま。
+        method = method.replace("AVCaptureSessionInterruptionReasonKey",
+                                '"AVCaptureSessionInterruptionReasonKey"')
+        self.run_swift(NOTIFICATION_HEAD + method + NOTIFICATION_TAIL)
+
+    def test_terminal_overlay_writer_does_not_wait_forever(self):
+        recorder = (SOURCE / "DebugVideoRecorder.swift").read_text()
+        start = recorder.index("            while !input.isReadyForMoreMediaData {")
+        end = recorder.index("            let presentationTime", start)
+        self.run_swift(OVERLAY_HEAD + recorder[start:end] + OVERLAY_TAIL)
+
+
+NOTIFICATION_HEAD = r'''import Foundation
+import Darwin
+import AVFoundation
+@MainActor final class CameraViewModel {
+ let session = AVCaptureSession()
+ var sessionObserverTokens: [NSObjectProtocol] = []
+ func handleCameraInterruption(reason: String) {}
+ func handleCameraInterruptionEnded() {}
+ func handleCameraRuntimeError(_ message: String) {}
+'''
+
+NOTIFICATION_TAIL = r'''
+}
+@main struct Audit {
+ @MainActor static func main() {
+  let model = CameraViewModel()
+  model.registerCameraSessionObservers()
+  var failures = 0
+  for name in [AVCaptureSession.wasInterruptedNotification, AVCaptureSession.interruptionEndedNotification, AVCaptureSession.runtimeErrorNotification] {
+   let done = DispatchSemaphore(value: 0)
+   let session = model.session
+   DispatchQueue.global().async {
+    NotificationCenter.default.post(name: name, object: session)
+    done.signal()
+   }
+   let passed = done.wait(timeout: .now() + 0.25) == .success
+   if !passed { failures += 1 }
+   print(name.rawValue, passed ? "PASS" : "FAIL: poster waits for main")
+  }
+  model.sessionObserverTokens.forEach(NotificationCenter.default.removeObserver)
+  exit(failures == 0 ? 0 : 1)
+ }
+}
+'''
+
+OVERLAY_HEAD = r'''import AVFoundation
+import Foundation
+import Darwin
+enum DebugVideoRecorderError: Error { case appendFailed(String) }
+final class TerminalWriter: AVAssetWriter {
+ var terminalStatus: AVAssetWriter.Status = .failed
+ override var status: AVAssetWriter.Status { terminalStatus }
+ override var error: Error? { NSError(domain: "overlay-regression", code: 28) }
+}
+final class BlockedInput: AVAssetWriterInput {
+ override var isReadyForMoreMediaData: Bool { false }
+}
+func wait(input: AVAssetWriterInput, writer: AVAssetWriter, reader: AVAssetReader) async throws {
+'''
+
+OVERLAY_TAIL = r'''
+}
+@main struct Audit {
+ static func main() throws {
+  var failures = 0
+  for status in [AVAssetWriter.Status.failed, .cancelled] {
+   let writer = try TerminalWriter(outputURL: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4"), fileType: .mp4)
+   writer.terminalStatus = status
+   let input = BlockedInput(mediaType: .video, outputSettings: nil)
+   let reader = try AVAssetReader(asset: AVMutableComposition())
+   let done = DispatchSemaphore(value: 0)
+   let task = Task.detached {
+    do {
+     try await wait(input: input, writer: writer, reader: reader)
+    } catch DebugVideoRecorderError.appendFailed {
+     done.signal()
+    } catch {}
+   }
+   let passed = done.wait(timeout: .now() + 0.5) == .success
+   print("terminal writer",status.rawValue,passed ? "PASS" : "FAIL: wait never reports failure")
+   if !passed { failures += 1 }
+   task.cancel()
+  }
+  exit(failures == 0 ? 0 : 1)
+ }
+}
+'''
+
+if __name__ == "__main__":
+    unittest.main()
```

3つのdiffを連結したpatchに対して、未修正の一時コピーで `git apply --check` と `git apply` を実行し、双方 exit 0。適用結果のSwift2ファイルはビルド済み提案コピーとバイト一致した。

## その他の監査結果（確定不具合としては採用しなかった事項）

| 対象 | 確認した内容・判断 |
| --- | --- |
| CameraViewModel.swift | `@MainActor` の lifecycle / permission / recovery 世代確認、frame callbackの `nonisolated` 経路、UI mailboxの短いlockを追跡。watchdog / UI timer / session observers / path monitorはdeinitで取消・解除される。停止と録画開始の間には開始状態を先に確保する対策がある。通知による相互待ちは項目1。カメラ構成のメイン実行や同期stopの長時間ブロックは残る実機リスク。 |
| FrameProcessor.swift | pending frameは1スロット、世代snapshotはlock読み取り。resetはpendingLockを解放してからframe queueへsyncし、通常のonResultはMainActorを同期呼出ししない。pixel buffer lockは失敗時return・成功時defer unlock。寸法変更でtrackをreset。録画finishの合流callbackは1回のfinishから排出する。checked/unsafe continuation APIの使用なし。 |
| DetectionCore.swift / BGRADetection.swift | 全ファイルでforce unwrap / 範囲・乗算・インデックスを検索し、入力サイズ検査と呼出し側を照合。`analyzeSabers` は正の寸法と `bytesPerRow >= width*4` を要求。morphologyも正の寸法とmask要素数を確認。カメラから受け取る妥当なCVPixelBufferの範囲では、padding / odd sizeで越境する例は見つからず、既存の最適化静的テストも通過。内部maskのbaseAddress unwrapは非空配列の条件下。APIへInt.maxや不整合bufferを直接与える人工例だけで、本番不具合とはしなかった。認識変更は提案していない。 |
| DebugVideoRecorder.swift | 5分 / disk / metadata / BGRA保持量の上限、raw append failure、finishのエラー分岐、independentCopy / copyBGRA / OverlayRasterizerを追跡。dimension変更は録画appendをskipする。ファイルI/Oはthrow / catchに流れる。固定のCodingUserInfoKey、dictionaryの既知key、候補bestのnil検査後のunwrap等に新たな成立可能なnil経路を見つけなかった。overlay待機は項目2。 |
| DebugRecordingTriage.swift | metadataの強制cast前にdecodeMetadataがframes[]のobject型を確認。directory / eventIndex / selected candidateのunwrapは分岐で保証される。候補bbox / centroid / endpointの長さは生成時の内部契約に依存する。壊れた外部JSONから任意の内部候補を復元する本番経路は見つけなかった。retention窓・events・metadata境界を確認。 |
| DebugBundleTransfer.swift | main-thread preconditionはReleaseにも残るが、今回追った呼出しはMainActorかDispatchQueue.mainに戻す。enqueueは任意キューからmainへ渡す。singleton初回参照はCameraViewModelのinitでmain上。activePackageURL unwrapは同じmain処理内のguard後。Documents.first!は通常のiOS app containerの前提で、取得不能を起こす実例なし。outbox永続化失敗はログ、次の起動でbundle再走査。転送期限・finish時のFileHandle.close / package削除・LAN session invalidationがある。容量検査を外す提案なし。 |
| UDPSender.swift | configure / stopのqueue.syncはmain側から。completionはUI mailboxへ、onUpdateはTaskへ渡るため、現行app callbackから同じsenderのsyncを再入する経路なし。wait / reconnect / send watchdogのwork itemはstopでcancel。LAN probe timerはstopでcancel。P2P同様のcompletionキュー検査は `#if DEBUG` 内。 |
| P2PSender.swift / P2PProtocol.swift | ticker / browser restart / send watchdogはstopで取消。callback内stopはsync再入できる設計だが、現行appのonState / onStatsはMainActor Taskへ渡すため、到達例なし。packet headerは20byte以上を確保し、decodeは最短長を検査。session / sequenceはUInt32 / UInt64、送信sequenceはwrapping increment。 |
| CoordinateDelivery / ConnectivityLog（UDPSender.swift内） | delivery lock内でlanProbe / p2p usable / log / noRouteへ進むが、追った実callbackでは逆順のdelivery取得なし。logのentriesは200件、通常のファイル回転は256KiB・1世代。ioは専用キュー、handleはdefer close。回転先がdirectoryの場合は意図的に追記を続けるため上限を超え得るが、通常のapp生成経路ではdirectoryにならない。サンドボックス内でその衝突を作る外部条件は未確認。 |
| DeviceHealth.swift / EventRecovery.swift | rolling windowは件数上限あり。時刻差に基づくFPS / thermal / retryで、単純な総フレーム数による経過判定ではない。RecoveryBackoffのfailedSince / healthySinceは同メソッド内でnilを埋めてからunwrap。既存の8時間時刻テストも通過。 |
| GuidedRecording.swift | 固定scriptのvalidation、scheduler / ledgerのstep範囲とdictionary keyの存在を確認。speech delegateはMainActor Taskへ戻す。cancel / endはaudio sessionを解放。guided Task / FPS alternation Task / maximum duration Taskは停止経路でcancel。受理するscriptを外部から任意に改変する経路なし。 |
| ContentView.swift / CameraPreview.swift / PhoneSaberSenderApp.swift | scenePhaseをlifecycleへ渡す。previewのlayer force castはoverride layerClassによって保証される。アプリエントリにfatalErrorなし。任意queueでUI状態を変更する新たな経路は見つからなかった。 |

### カウンタ・Release差・資源についての補足

- 60fps×8時間 = 1,728,000 frame、2色の送信は最大3,456,000件程度。対象iPhoneのIntは64bit、フレームsequenceはUInt64で、この範囲は桁あふれに遠い。100年単位の加算上限やInt.maxの人工注入を8時間の不具合とはしない。
- `fatalError` / 明示 `assert` / 明示continuationの使用は対象Swift内に見つからなかった。main-thread `precondition` と `MainActor.assumeIsolated` はReleaseでも有効。実呼出しを追い、項目1以外の違反経路は確認できなかった。UDP / P2P finishの `dispatchPrecondition` はDEBUGだけ。
- DEBUGはtrace / freeze診断 / exposure sampleを追加する。Releaseは録画中のみ詳細analysisを作る。DEBUGの送信handler履歴も直近16世代に制限されている。今回の2不具合はDEBUG分岐の外にある。
- CameraViewModelのtimer / observerは解除されるが、deinitだけで進行中のcapture / UDP / P2P / recordingすべてを停止する保証まではない。通常のアプリは単一StateObjectを保持するため、画面を捨てる実シナリオが確認できず確定不具合にしなかった。所有者破棄を試す結合テストが必要。
- カメラ出力は明示的に32BGRAを要求している。一方FrameProcessor入口でpixel format自体を再検査していない。想定外formatの拒否方針はoperatorにも影響するため提案対象。strideの検査を通る異形式・format変更の実フレームは未取得。新たな認識結果の変更やbuffer変換を実装していない。
- メモリ警告observerは見つからなかった。録画保持は固定の256MiB上限で、警告時に縮小する設計ではない。端末全体の余裕、writer / camera pool、PNG・overlay生成中の一時メモリは別。jetsamを再現していないので「必ず落ちる」とはしない。自動録画停止やlossless証拠の間引きは挙動変更として提案のみ。
- raw frame PNGと録画sessionはoperatorの整理まで残る。未受信outboxを自動削除しない設計。録画1sessionのdisk上限が8時間分の総容量を制限するわけではない。保持方針を勝手に変更しない。

## 実行した検証と結果

作業先は `$TMPDIR/phonesaber-ios-crash-audit-zigofetl`（実パスは `/var/folders/sf/rx78c9vs3_37dby19j8b5xv00000gn/T/phonesaber-ios-crash-audit-zigofetl`）。snapshot / 提案版 / baseline版 / apply-check版を分けた。主なログもこのディレクトリにある。以下の `$audit_root` はこの実パスを指す。

| 実行 | 結果 |
| --- | --- |
| `xcodebuild -version`, `xcrun swift --version` | Xcode 27.0 (27A266a), Apple Swift 6.4 (swiftlang-6.4.0.34.1)。 |
| `bash "$audit_root/PhoneSaberSender/run-static-tests.sh"`（変更前） | exit 0。静的BGRA検出suite PASS。pixel HSV・閾値端・odd size / padding・sample step・morphology・scoring scratch・frame edges等。DeviceHealth XCTest 10件、失敗0（8時間時刻のwindow testを含む）。`static-tests.log`。 |
| 下記Release build（通常設定、変更前） | exit 65。`sandbox-exec: sandbox_apply: Operation not permitted` によりSwiftUI State macro pluginが応答不能。アプリソースの不具合と分類していない。`release-build.log`。 |
| 同Release buildに `OTHER_SWIFT_FLAGS=-disable-sandbox` を追加（変更前） | exit 0 / BUILD SUCCEEDED。コンパイラがmacro pluginへ追加する入れ子sandboxを省く設定。外側の実行環境制限はそのまま。署名なし・インストールなし。`release-build-no-nested-sandbox.log`。 |
| 上記設定で提案2diff適用後のRelease build | exit 0 / BUILD SUCCEEDED。`proposed-release-build.log`。公式buildと同一条件のPASSとは扱わない。 |
| `python3 -B "$audit_root/baseline/Tools/test_ios_crash_liveness.py" -v` | exit 1。2テスト×DEBUG定義あり/なしの4 subtestがFAIL。通知3種類とwriter終端2種類、それぞれ期待した未完了を確認。`baseline-regression-tests.log`。 |
| `python3 -B "$audit_root/PhoneSaberSender/Tools/test_ios_crash_liveness.py" -v`（提案適用後） | exit 0。2テスト×DEBUG定義あり/なしがすべてPASS。`proposed-regression-tests.log`。 |
| `xcrun swiftc -O ... connection-cycle.swift` の参照循環実験 | 実NWConnectionでstate handlerにconnection自身をcapture。cancel→外側参照破棄→500ms後にweak参照nil。cancel後の蓄積を支持しなかったため不具合から除外。iOSや受信待ち中の全パターンの解放保証には拡張しない。 |
| 実AVAssetWriterの初期化・start / cancel実験 | startWriting=false、status=.failed、input ready=true。sandbox下のencoder実験は本件2のfailure状態注入と区別。成功する実encoderのdisk-full実験は未実行。 |
| actual recording / triage host harness の `swiftc -O -D DEBUG` build | exit 0。DetectionCore / BGRADetection / FrameProcessor / DebugVideoRecorder / DebugRecordingTriage / GuidedRecording + CameraViewModelからinterval型を抽出 + DebugBundleTransferのupload無効stub + TrackingDiagnosticsCaptureHarness。`capture-build.log`。 |
| `"$audit_root/capture-harness" "$audit_root/captures"` | exit 133。開始時の空き容量APIが0を返し `insufficientDiskSpace(required:1040187392, available:0)` をtop-level harnessが未捕捉。`capture-run.log`。アプリの録画開始はcatchされるため、これはアプリの新規fatalErrorとはしない。録画 / PNG / overlayの実行成功とは数えない。 |
| `xcrun simctl list devices available -j` | exit 1。CoreSimulatorService / simdiskimagedへ接続できず。Simulator XCTest未実行。`simulator-discovery.log`。 |
| `git apply --check` / `git apply`（一時コピーのみ） | exit 0 / 0。提案の連結patchを適用可能。 |

Releaseコマンド（通常・変更前、ログへredirect）:

```sh
xcodebuild -project "$audit_root/PhoneSaberSender/PhoneSaberSender.xcodeproj"   -scheme PhoneSaberSender -configuration Release   -destination 'generic/platform=iOS' -derivedDataPath "$audit_root/DerivedData"   CODE_SIGNING_ALLOWED=NO build
```

録画ホストharnessのコンパイルコマンド（提案コピー）:

```sh
xcrun swiftc -O -D DEBUG -module-cache-path "$audit_root/module-cache" \
  "$audit_root/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift" \
  "$audit_root/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift" \
  "$audit_root/PhoneSaberSender/PhoneSaberSender/FrameProcessor.swift" \
  "$audit_root/PhoneSaberSender/PhoneSaberSender/DebugVideoRecorder.swift" \
  "$audit_root/PhoneSaberSender/PhoneSaberSender/DebugRecordingTriage.swift" \
  "$audit_root/PhoneSaberSender/PhoneSaberSender/GuidedRecording.swift" \
  "$audit_root/CameraIntervals.swift" \
  "$audit_root/PhoneSaberSenderTests/TrackingDiagnosticsCaptureHarness.swift" \
  -o "$audit_root/capture-harness"
```

ノートから3つのdiffを再抽出して `git apply --check` を再実行し、exit 0。ノートの `git diff --no-index --check /dev/null ...` は空白エラー出力なし（exit 1は新規ファイルが/dev/nullと異なるため）。最終確認でも既存Swift全16ファイルのhashは開始時と一致し、PhoneSaberのstatusはこのノートの新規追加だけだった。

通過した変更前・提案版のbuildでは、上に `OTHER_SWIFT_FLAGS=-disable-sandbox` を追加した。static tests / 回帰fixtureは `-O`、回帰fixtureは別途 `-D DEBUG` の有無を両方実行。DEBUG定義付き最適化コンパイルは通常のXcode Debug `-Onone` 実行を意味しない。

**実行できていない / 実行していないもの:** iOS Simulatorの全XCTest、iPhoneへの署名 / インストール / 実カメラ、8時間の実時間soak、実機のメモリ警告 / jetsam / media service reset / 容量枯渇・ファイル保護、AWDL・Wi-Fi切断と復帰、background export終了猶予の強制失効、Thread Sanitizer / Address Sanitizer。formal lossless corpusと公式 `PhoneSaber/tools/verify_phone_saber.sh` はoperator担当という指示により実行していない。既存ソース・corpusを変更していない。

## 追加価値が高い未充足テスト10件（優先順）

既存のFPS・通知状態機械・世代・録画limit・outbox単体テストは存在する。以下は、それらにない障害の同時発生 / 実資源寿命の組合せを狙う。1・2のホスト回帰は上に提案済みで、現リポジトリには未追加。

1. **通知postと同期停止の競合:** 項目1の3通知をsession queueから発し、MainActorがstop待ちするfixture。postとstopが期限内に返り、後でMainActorのhandlerも一度だけ実行する。ホスト回帰に加え、実CameraViewModelで確認。
2. **overlay backpressure中のwriter終端:** 項目2の回帰に加え、実encoderで書込み不可 / media service resetを注入し、raw / metadata / losslessが保存済みのままfinalizing解除・次の録画開始まで進むことを確認。
3. **録画中の寸法・stride切替:** odd寸法・row padding・小/大フレームを交互にCMSampleBufferで流し、30→60fps復旧と重ねる。越境なし、古いtrack送信なし、録画skip / finalizeが期限内で収束する。ASanも併用。
4. **空 / 異format入力の入口:** imageBufferなし、作成APIが拒否するzero寸法、BGRA以外の合法CVPixelBufferを分ける。想定外formatの製品方針を決めた後に、例外・越境がなくカメラ復旧を妨げないことを検証。人工Int.maxだけのテストで代用しない。
5. **全経路のlock / sync競合:** detection、LAN probe、P2P pong・fallback、sender completion、MainActor stop / station変更をバリアで同時発火。Thread Sanitizerで逆順lock / 同一queue syncがなく、旧世代送信が終了することを検証。
6. **録画終了要求の競合:** duration limit、runtime error、user Stop、background移行を同じwriter completion前に重ねる。manual / auto completion各一度、finalizing解除一度、stop中に新しいrecorderが孤立しないことを検証。
7. **background / foreground全状態の遷移表:** permission pending / camera starting / live / recovery sleep / recorder creation / raw finalize / overlay / packaging / LAN upload / P2P uploadで往復し、background task猶予失効も挟む。新旧callbackの世代管理と復帰後の期限内進行を確認。
8. **容量・権限エラーと再起動回復:** metadata write / synchronize / PNG / package copy / outbox atomic saveの各地点でENOSPC・EACCESを注入。完成済み証拠が消えず、FileHandleとpartial packageが蓄積せず、再起動時のoutbox scanで回復する。失敗回数分のfd増加がないことを測る。
9. **低メモリ警告と停止時ピーク:** 小容量iPhoneで録画保持上限の近くに達した直後に警告、PNG / overlay生成、前面復帰を重ねる。phys_footprintと利用可能メモリを追跡してjetsam / カメラpoolの枯渇がないことを確認。証拠削減や自動停止の変更が必要なら別途operatorが判断。
10. **実アプリ8時間の資源soak:** 60fps通常送信に短い録画→転送、Wi-Fi / AWDL断復帰、FPS変更、停止再開を反復。ReleaseとDebugでメモリ・thread・fd・task・timer・observer・temp packageの傾向を比較し、最終stop後にweak参照 / セッション / socketが解放されることを確認。既存SenderSoakHarnessはカメラ・無線・UIと録画を対象外にしているため、そのPASSで代用しない。

## 未解決リスクと採用前の確認

- P1の実機での通知スレッド / session完了待ち、P2の実encoderの終端＋readiness=falseを実測する必要がある。ホスト回帰のPASSを実機障害の再現・解消と混同しない。
- `.writing` のまま止まるwriter、`finishWriting` のcallbackが返らないFramework障害には提案2のguardだけでは対処できない。期限・キャンセルAPIの追加は挙動変更なので未実装。
- 同期stopとメイン上のsession構成は長時間ブロックし得る。構成も含めてsession queueへ移す案は変更範囲・Start/Stop挙動が大きいため、この監査では実装していない。
- 固定BGRA保持上限と警告無処理、未受信outbox / raw PNG / sessionの総容量増加は、端末容量とoperatorの録画頻度次第。自動整理・保持上限の変更は別判断。
- ソース抽出回帰はUIKit / SwiftUI本体、MainActorのすべてのreentrancy、実AWDLを検証しない。operatorによる公式verifyと実機試験が残る。
