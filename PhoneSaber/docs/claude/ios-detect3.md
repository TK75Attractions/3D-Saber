# ios-detect3（2026-10-09）

`opt/ios-detect3` の既存未コミット差分をレビュー・検証した。基準は `origin/main` = `35afe44c2d5f90cdde3ea6c62bf8c8b9dc369cd4`。BFSの点列をFIFOと兼用、major投影とaxial binの再利用、最大12 binのSIMD16作業域、body点数の明示、白飛び13近傍表、proposal workspaceと小さな抑制用snapshotによって、一時配列・点コピー・同一式の再計算を減らす。threshold、浮動小数点の式・逐次加算順、点・候補順、eligibility、winner、診断、UDPは変更しない。フレーム間の可変共有workspaceは導入しない。

## XCTestの変更前後

`DetectionCoreTests.testOfflineBrightFrameTimingBaseline`、Debug/iOS Simulator、各12回。変更前はHUD作業ツリーの総合検証で取得し、そのSwift本体・テストはorigin/mainと同一。変更後はios-detect3作業ツリーの総合検証。両方ともUnityの実時間PlayModeテストと並行しており、負荷・温度の影響を含む参考値である。

| 入力 | 前 avg / median / max ms | 後 avg / median / max ms |
| --- | --- | --- |
| empty | 0.436 / 0.472 / 0.519 | 0.266 / 0.263 / 0.289 |
| bright | 3.257 / 2.962 / 4.377 | 2.715 / 2.685 / 2.954 |
| bright profiled | 2.971 / 2.961 / — | 2.834 / 2.845 / — |

bright中央値は約9.4%短縮。ただし測定環境を固定した因果推定ではない。追加のnative `swiftc -O` 測定は同一モジュール名・同一Staticテストソースでbefore→after→after→before→before→after、各200回。brightの各run中央値の中央値は前5.139 ms、後5.903 ms（約14.9%増加）、emptyは0.491→0.470 ms。Unity・検証・ビルドと同時実行のため速度改善は確定できない。実機や60fps達成を主張しない。

## 検証

- 同一Staticテストソース、`swiftc -O -module-name PhoneSaberLossless`で変更前後をビルドし、`--lossless-signatures`：合成72ケース＋PNG fixture 52枚が完全一致。候補全stored property、支持点、winner、eligibility、endpoint/pipeline診断、profile整数カウンタを含む。DoubleはbitPattern、診断Dictionaryは正規化。profile実時間は比較外。
- `make -C PhoneSaber/android/core parity`：PNG 269枚（fixture 35＋inbox original 234）、合成27、FrameProcessor 541遷移、全て不一致0。formal期待値40件の失敗0。
- `make -C PhoneSaber/android/core test`：core-testとPython 4件PASS。
- `bash PhoneSaber/tools/verify_phone_saber.sh`：Detection PASS、formal lossless 40/40 PASS、Tools 401件PASS（skip 1）、iOS Release PASS、Diff Check PASS、Unity各stage NOT_RUN。
- **XCTest FAIL**：全276件中274 PASS、1 FAIL、1 skip。`P2PTransportTests.testFallbackWithoutLANCountsTheDroppedCoordinate`の633/634行で`XCTAssertTrue`失敗。変更前origin/mainコードの総合検証も同名テストで失敗（全275件中273 PASS、1 FAIL、1 skip）。検査・期待値・productionコードは変更していない。
- Toolsのtimeoutはなく、モジュール単独再実行は不要。

総合検証が全PASSでないため**未コミット**。変更を保持し、pushしていない。今回の失敗は変更前にも存在するが、gateを緩めずoperatorへ引き継ぐ。

ログ（各作業ツリー内）:
- 前：`3D-Saber-hud-gc/PhoneSaber/.verify-logs/phone-saber/20261009-162344/`
- 後：`3D-Saber-ios-detect3/PhoneSaber/.verify-logs/phone-saber/20261009-162818/`
- native：`PhoneSaber/.verify-logs/ios-detect3-native/`（同一モジュール名の比較結果とbenchmark.json）
