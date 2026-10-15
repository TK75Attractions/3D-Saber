# unity_alloc-1015 完了確認（2026-10-15）

- レビュー対象: 1e44946、分岐点876ac15。git log -5 / git diff main...HEADを確認。対応する前回短報は存在しなかった。
- P2: hostが失敗件数だけを比較し、別の色の失敗・準備失敗・任意例外を旧版の回帰再現として受理していた。
- 修正: Host.cs:19でAssertionExceptionの理由と赤/青/統計の個別maskを照合。注入回帰3件は修正前FAIL→修正後PASS。
- P2（既存）: P2PTransportTests.swift:1のNetwork importがstandalone条件で除外され、NWError参照3箇所がコンパイル不能。importを共通化し実host再実行PASS。
- Frames.csを既存Host.csの条件分岐へ統合。新規C#/Swift/Kotlinなし。変更対象Assets内C#4件の既存.metaを確認。
- 本番コードへの追加修正なし。前回のidle lock回避を維持。座標変換・最新値取込み・競合の失効/Reset・既定設定は維持。
- 認識、UDP形式、formal corpus、scene/prefab/.meta、ProjectSettingsの差分なし。新規runtimeオプションなし。

実行結果（ログ: /tmp/phonesaber-unity-alloc-*.log）:
- toolsでpython3 -m unittest discover -v: 248件、NumPy未導入によるimport ERROR6件。Python 3.12で再実行。
- toolsでpython3.12 -m unittest discover -v: 290件、OK（skip11、既知expected failure1）。追加gate回帰3件を含む。
- iOS Toolsでpython3 -m unittest discover -v: 439件、FAIL1/ERROR1/skip1（旧Pythonの巨大整数と下記容量判定）。
- iOS Toolsでpython3.12 -m unittest discover -v: 438件、FAIL1/ERROR1/skip1（ポート競合と下記容量判定）。
- test_phone_saber_p2p_bridge.P2PBridgeTests.test_two_peers_share_the_unity_sender_identity: ポート競合後の単独再実行PASS。
- Python 3.12でCorruptStatusTests 4件、TrackingDiagnosticTests 11件の単独再実行PASS。
- python3 PhoneSaber/tools/check_pc_latency.py: frame8件・統計50件・sender14件・既存host35件PASS、6構成API compile PASS。
- 同checkの--frame-before-ref main / --sender-before-ref main（併用・各単独、単独時--frames-only）: 旧版2/1/3件FAIL→現行全PASS。
- 同hostのidle/32frame毎の両色入力/pollは計測区間の割当0 B。非実時間loopであり、実60Hz・画面遅延・Player全体GCの測定ではない。
- python3 PhoneSaber/tools/check_auto_latency.py: NUnit5件、Swift3件、Swift→実C# UDP loopback PASS。
- make -C PhoneSaber/android/core test parity: C++ build/unit・Python4件PASS。325 PNG/40正式fixture/27合成例/541遷移で不一致0。
- bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh: Static BGRA・DeviceHealth13件PASS。
- PhoneSaber/ios/PhoneSaberSenderTests/run_{camera_lifecycle,debug_transfer,high_speed,send_gap}_host_tests.py: 各18/7/2/21件PASS。
- 同run_latency_host_tests.py: 修正前NWError compile FAIL→修正後カメラ27件・経路6件PASS（build/runとも実行）。
- git diff --check PASS。commit/push・macOS app起動なし。Gradle未実行（Android差分なし）。

未検証・operator実行:
- 録画E2E: DebugVideoRecorder.swift:308が空き容量0を返しsetUpClassで停止。dfは416GiB空き。両Pythonで再現、本差分外、安全gateは変更しない。
- 通常terminalでiOS Toolsからpython3.12 -m unittest -v test_phone_saber_tracking_e2eを再実行。0値のfallback変更は別途レビュー対象の提案のみ。
- Unity実行: PHONESABER_UNITY_COPY=/tmp/phonesaber-unity_alloc-1015 bash PhoneSaber/tools/unity_copy.sh editmode 'PhoneSaberReceiverStateReadTests;PhoneSaberInputStatsTests;PhoneSaberSenderGateTests'
- 上記はopen -g -j -n -W経由、XML/logで判定。公式verifyの既定filterにはこの3fixtureが含まれないため追加が必要。
- Unity Player実build/PlayMode、xcodebuild（iOS Release/XCTest）、公式verifyは未実行。operatorの通常検証に加える専用iOS build条件なし。
- 実機追加確認はLAN赤青10秒→青のみ停止2秒→再開10秒。赤が継続し、青の端点が再開後に更新されること。host測定では描画・実UDP thread競合を保証しない。
