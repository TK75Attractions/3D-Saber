# iPhone capture→send 遅延・120 fps opt-in（2026-10-13）

`be48784`（`wip: iPhone latency work ported onto current main...`）と clean な作業ツリーから再開。
main は `0fcb3e7`。WIP を保持して修正し、旧 `ios-latency-20261011.md` をこの記録へ置き換えた。
**未commit・未push。オペレーターが差分レビュー、公式 verify、commit を行う。**

## 指摘（重要度順、行番号は修正後）

| 重要度 | 場所 | 具体的な失敗シナリオ／対応 |
| --- | --- | --- |
| P1 | `CameraViewModel.swift:140,1690,1735` | WIP は既定60/30 fpsでも auto HDR・HDR・GDC・広色域自動設定をOFFにしていた。対応形式では明るさ・色・画角が main と異なり、同じ認識へ異なる画素を渡し得る。既定OFFの「低遅延カメラ設定（試験）」に分離。OFFへ戻すと、保存した設定を復元して通常の構成を再適用する。 |
| P2 | `FrameProcessor.swift:256,586` | Release の sample callback は captureHostTime=nil を渡しており、通常ビルドで撮影→送信の値を比較できない。Releaseでも同期時計からPTSをホスト時計へ変換する。変換できない値は従来どおり未計測。 |
| P2 | `FrameProcessor.swift:586`、`DeviceHealth.swift:154` | UI が統計のソートロックを持つ時、検出後の onHealthSample がロックを待ってから onResult（UDP要求）を呼んでいた。送信要求の後へ統計を移し、snapshot の集計・ソートもロック外にする。認識・payload・送信順の赤青は変えない。 |
| P2 | `DeviceHealth.swift:201` | 120 fpsへ冷却復帰した直後に serious/critical へ再上昇すると、120秒の切替間隔が優先され120 fpsを維持する。120だけは熱退避を即時にする。60/30の既存方針は維持。 |
| P3 | `UDPSender.swift:919` | 経路選択時はP2P usableでも、送信キューで切れてLANへfallbackすると、lastRouteがP2Pのまま。LAN退避／経路なしを記録し直す。送信経路の判断やP2PSender内部は変更しない。 |

## 通常の撮影経路と opt-in

60 fps（既定）／30 fps、試験OFFの新規起動では、main と同じ形式選択関数を呼ぶ。
`CameraFormatOption` の画角情報は120の適合確認にだけ使い、30/60の順位に入れない。
main と実ソースの構成ブロックを同じAVCapture stub上で実行し、次の全setter履歴が一致することをホストXCTestで確認した。

- activeFormat、iOS 18以降のauto frame rate OFF、対応端末のlow-light boost OFF、min/max frame duration。
- BGRA出力、late frame破棄、delegate queue、portrait、mirror=false、対応端末のstabilization OFF。
- 試験OFFでは wide-color auto、activeColorSpace、auto HDR、HDR、GDCへ新たな書込みをしない。
- session構成・元のエラー処理・形式変更後の既存露出実験適用はmainと同じ。露出Autoはdeviceに触れず、既存の保存済み露出実験も維持。

HDR/GDCの無効化がiPhone 13の60 fps画素に影響しないとは立証できないため、通常経路では変更しない。
Appleは[GDCの対応端末では既定true](https://developer.apple.com/documentation/avfoundation/avcapturedevice/isgeometricdistortioncorrectionenabled)とし、[広色域自動設定](https://developer.apple.com/documentation/avfoundation/avcapturesession/automaticallyconfigurescapturedeviceforwidecolor)はactiveFormat/colorSpaceを変え得ると説明している。
[WWDC21](https://developer.apple.com/videos/play/wwdc2021/10047/)でもauto HDRは既定true、HDR系の処理が画像のダイナミックレンジに関係すると説明される。遅延短縮の実機証拠はまだない。

「低遅延カメラ設定（試験）」はアプリ起動ごとにOFF。ONではwide-color auto・auto HDR・HDR・対応GDCをOFF。
HLGならsRGBへ戻してからHDRをOFFにする。SDRへ戻せない形式では試験の開始を失敗として示す。
OFFへの切替／試験中の形式変更では、保存した形式・colorSpace・HDR・GDCを復元してから次の通常構成を行う。
露出の試験設定は別スイッチ。録画・録画終了処理中はこの新トグルを変更できない。

120 fpsは既存fps pickerで明示選択した場合のみ。
60 fpsの選択形式と寸法・公称FOVが完全一致し、120 fps対応範囲を持つ形式がある時だけ表示する。
実機で適合形式が無ければ表示しない。画角・寸法・120という数値だけで画質や遅延の同一性は保証しない。
AVFoundationのlate frame破棄と既存の最新1枚mailboxを維持し、フレームをFIFOで溜めない。
serious/criticalで30 fpsへ退避、連続冷却120秒後に保存済み120へ戻る。

## 自動ログと読み方

送信開始から10秒おきに `Library/Logs/connectivity.log` に1行出す（Debug/Release共通、直近5秒の観測）。
停止・fps/試験設定の変更による再起動で10秒区間をリセットする。UI更新自体を止めるアプリ背景中にはログを保証しない。

```text
2026-10-13T...Z [CAPTURE_MODE] station=A requested-fps=120 capture-fps=120 low-latency=false format=... BGRA portrait
2026-10-13T...Z [CAPTURE_METRICS] route=LAN（Unity 応答あり） capture-mode-fps=120 capture-fps=118.8 processed-fps=118.8 send-fps-red=118.8 send-fps-blue=0.0 capture-to-send-median-ms=... capture-to-send-p95-ms=... thermal=nominal window-s=5
```

- `CAPTURE_MODE`: 再設定時の形式／試験トグル。`capture-fps`は設定されたactiveVideoMaxFrameDurationの逆数。
- `CAPTURE_METRICS`: `capture-mode-fps`は設定値。`capture-fps`／`processed-fps`は実測カメラcallback／処理数。
- send-fps-red/blue はローカルの成功completion数。剣が見えない時の0は自然。PCが受け取った証拠ではない。
- capture-to-sendは**既存表示と同じPTS→認識完了（送信直前）**。UDPキュー待ち、無線、PC受信、描画は含めない。
- p95は直近5秒の有効標本を昇順にし、nearest-rankの95%点。中央値は中央2点平均。変換不可・負値・2秒超は除外し、標本なしは`unavailable`。
- routeは直近の座標経路（赤青ごとの経路内訳ではない）。剣が無い間は前の経路が残り得る。送信fpsと合わせて読む。

通信ログUIは直近12行で、10秒ごとの `CAPTURE_METRICS` は画面には出さずファイルにだけ書く（経路の出来事を押し出さないため。取り込み時に Claude が変更）。ファイルは1MiBで `.1` へローテーションする（計測行は8時間で約0.7MiB）。
オペレーターはXcode Devices and Simulatorsのアプリcontainerを取得し、`Library/Logs/connectivity.log` と `.1` を読む。
Debug Performanceにも使用中のformat index・寸法・公称FOV・binned、出力pixel format、frame durationを追加した。
`latency_report.py --sender-metrics connectivity.log` はReleaseのCAPTURE_MODEと旧Debug stdout両方を読む。
設定切替を跨ぐF9ブロック／開始時刻不明の先頭ブロックは除外。スマホとPCの壁時計同期が必要。
設定ログなしは受信pkt/sから30/60/120を推定し、中間値を除外する。過負荷120を受信数だけで60と誤認し得るため設定ログを優先する。

## 安全に直した送信前の遅延

capture・検出・UDPのQoSは既にuserInteractive。BGRA全体のコピーは通常撮影には無く、サンプルを最新1枚だけ保持する。
12Hz UIは短いsnapshotロックだけで、検出queue.syncを経由していない。今回、健康統計のソートロックを外し、その統計待ちを現在の送信より後へ移した。

ホストbenchは実FrameProcessor・本番認識で16×16 BGRAを120枚処理し、onHealthSampleに3ms待ちを与えた。
送信callbackまでのprocessing-start起点のmedian/p95（認識も含む）:

| ソース | median | p95 |
| --- | ---: | ---: |
| WIP be48784 | 3.773 ms | 3.798 ms |
| 修正後（最初の比較） | 0.014 ms | 0.025 ms |

これは統計待ちを送信前から除いた証明用の人工負荷で、iPhoneの通常時に3.7ms短縮するという測定ではない。
PTS取得をReleaseでも有効化するためcallbackの少量の観測コストは追加される。実機でログのmedian/p95を比較する。
新しいframeが処理中に到着しても、処理済みの現在frameの座標を捨てる変更はしていない。

## オーナーのiPhone 13で行うこと（数値入力なし）

Macは電源接続・低電力モードOFFで、ビルド済みPlayerとiPhoneを同じLANへ。剣と照明・カメラ位置を固定する。
このビルドをXcodeでiPhoneへインストールし、「送信開始」後は下記を順に各60秒以上試す。Debug RecordingはOFF。

1. fpsは**60**、**低遅延カメラ設定（試験）OFF**。露出設定は現状のまま全区間で揃える。新しい初期状態は露出Auto。
2. **60のまま試験ON**。画像／剣の追従や誤検出も確認する。
3. **試験OFFへ戻して60**。通常状態へ戻ることを確認する。
4. **120 fpsが表示された場合だけ120、試験OFF**。表示されない端末では行わない。
5. **120のまま試験ON**。最後に**60・試験OFF**へ戻す（120のfps選択だけは保存される）。

オペレーターはCAPTURE_MODEで区間を切り、同じ経路・thermal=nominal/fairでmedian/p95と実測fps、赤青send fpsを比較する。
120が本当に約120で回っているか、熱退避で30になったか、試験OFFへ戻ったかも読む。
剣が見えている区間で送信fpsが低ければ、未検出／処理落ち／経路不調を分けて調べる。
撮影側の改善だけで画面全体が改善したとは判断しない。PCのevents.logのperf/refresh-hzとF9も比較する。
DebugビルドのF9追加比較は「F9 比較条件=60⇄120」「fps自動比較ON」（120適合端末のみ）。30秒ごと、5分でOFF。
試験トグルは比較中に固定し、F9設定比較と同時にON/OFFしない。

## 実施した検証

- `python3 PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py --benchmark`:
  macOS `swiftc -O`、実FrameProcessor／本番認識／録画ソース（未変更）をコンパイル。
  AVCapture設定はsetter履歴stub。既存DetectionCoreTests/DeviceHealthTestsの該当テストと、実UDPSender/P2PSender/CoordinateDeliveryの既存XCTestを別実行。
  **最終実行32/32 PASS**（撮影/健康26、経路6）。最終benchはmedian 0.011ms／p95 0.024ms。
  修正前比較は `--baseline /private/tmp/phonesaber-ioslat-before --benchmark`（HEADの5ソースを`*-before.swift`として抽出）。
  デフォルト設定・設定復元・Release時刻・送信/統計順序・120再発熱退避・P2P fallback経路表示が修正前FAIL、修正後PASS。
  baselineの新getterは既存lastRouteを読むだけ、存在しなかった設定helperは旧無条件呼出を再現するadapter。旧送信・熱・カメラ構成処理本体はそのまま。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: 静的BGRA検出／pixel scan／proposal／scoring／morphology PASS、DeviceHealth XCTest 13/13。
- `CLANG_MODULE_CACHE_PATH=/private/tmp/phonesaber-ioslat-formal-cache SWIFT_MODULECACHE_PATH=/private/tmp/phonesaber-ioslat-formal-cache python3 PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json /private/tmp/phonesaber-ioslat-lossless.json --csv /private/tmp/phonesaber-ioslat-lossless.csv`: **formal 40/40**（positive 23/23、negative FP 0/17）。初回は既定module cacheへの書込み制限でコンパイル不可、cache先を変えて成功。
- `python3 -B -m unittest discover -s PhoneSaber/tools -p test_latency_report.py -v`: **15/15 PASS**。
- iOS Release `xcodebuild ... -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /private/tmp/phonesaber-ioslat-release CODE_SIGNING_ALLOWED=NO 'OTHER_SWIFT_FLAGS=$(inherited) -Xfrontend -disable-sandbox' build`: PASS。
- Debug XCTest build `xcodebuild ... -configuration Debug -destination 'generic/platform=iOS Simulator' -derivedDataPath /private/tmp/phonesaber-ioslat-tests CODE_SIGNING_ALLOWED=NO 'OTHER_SWIFT_FLAGS=$(inherited) -Xfrontend -disable-sandbox' build-for-testing`: PASS。両コマンドのprojectは`PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj`、schemeは`PhoneSaberSender`。
  初回の通常Release buildはSwiftUI StateMacroのsubprocess sandbox起動が拒否され失敗。repoのビルド設定は変更せず、検証コマンドだけ上記compiler flagを追加して再ビルドした。
- `git diff --check`: PASS。認識core／UDP本文／正式corpus／Unity scenes・prefabs・.meta・ProjectSettings／禁止された録画3ファイル／P2PSender内部にmainとの差分なし。

**未実施:** Simulator上の全XCTest実行（CoreSimulatorService接続がsandboxで拒否）、公式verify（オペレーター担当）、署名・実機インストール、iPhone 13の120適合形式・画質・熱・実遅延、Mac+iPhone LAN/P2P実無線、Windows+Android、Player描画。
ホストのstubはAVFoundationの内部挙動・画素一致を証明しない。通常構成のコード／setter同一性と本番認識の回帰を確認した。

## 未実装の提案／残るリスク

HDR/GDC OFFや120 fpsの採用、短い露出の既定化は実機比較後に判断する。実機の画質・誤検出が変わる可能性がある。
120に適合形式が無い場合、寸法/FOV条件を緩めたり640×480から別解像度へ変える案はオペレーターの判断が必要で、実装していない。
処理中に新frameが来た場合の現在frame送信破棄、capture callback内で直接検出、UDPキューを省く直接送信も、出力・復旧順序が変わり得るため未実装。
F9 median約135msは表示・露光/readout/ISP・検出frame等込み。既存の撮影→送信60fps約37ms／30fps約50ms、検出約3ms、Unity受信→render直前約7ms@120fpsという実測と混同しない。
macOS低電力モードでPlayerが毎秒30回しか表示更新を受けない点、本編vsync OFF約900fps／menu vsync ONは今回変更していない。
