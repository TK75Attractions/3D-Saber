# iPhone sender 当日8時間運用レビュー — 2026-10-11

対象はカメラ復旧、端末状態、設定保存、Bonjour、UDP、LAN 生存確認、UI 公開。
先に `PhoneSaber/CLAUDE.md`・`AGENTS.md`・`review-ios-sender.md` を読んだ。
前回の F1〜F7 は反映済みで、再修正していない。認識、payload、formal corpus、Unity、
Debug Recording / guided recording / bundle transfer / P2PSender 内部は変更していない。
ユーザー指定に従い commit / push / 公式 verify は行わない。

## 新しい所見（重大度順）

| 重大度 | 場所 | 再現条件・影響 | 対応 |
|---|---|---|---|
| 中 | `UDPSender.swift:218` | 赤の送信完了待ち中に最新座標を待機させ、Wi-Fi 復帰などで ready 通知が来る。旧コードは待機座標を辞書から除去した後に送信中か確認していたため、最新座標を捨てる。以後 fresh 座標が来なければ最後の位置が PC に届かない。 | 条件の順序を1行だけ変更。送信中なら待機座標を保持し、完了後に最新だけを送る。回帰テストで修正前の失敗を確認。 |
| 中 | `CameraViewModel.swift:1092` | 復旧タスクが前面状態を確認して session の非同期停止を投入した後、停止完了前に Home / 画面ロックへ移る。完了側には前面状態の確認がなく、背面で configureAndStart を呼ぶ。復帰待ちという既存の制約を迂回する。端末上の具体的な中断・遅延は未計測。 | 提案のみ。完了側でも前面状態を再確認し、復帰まで再設定を延期する。復旧中の表示と再試行時期が変わるため未実装。 |
| 中（表示） | `CameraViewModel.swift:1317`、`ContentView.swift:71` | 台が未指定、または同じ台名の広告が2つあり、選択済み Mac A の後に Mac B を解決する。hostSelection は A のままだが、上部の Mac 名・IP・Resolved 表示は B に上書きされる。係員は B に送っていると判断しうる。 | 提案のみ。選択中の宛先と他の発見サービスの表示を区別する。選択・保存済み Mac 固定の方針は変更しない。 |

## 長時間運用の残存リスク（仕様変更は未実装）

- **高・運用上**: `CameraViewModel.swift:1490` の fps は要求値と60fps対応の有無で決まる。
  `.serious/.critical` に応じた自動低下・冷却後の自動復帰はない。`DeviceHealth.swift:42` は
  高温と要求fpsの70%未満を警告するが、撮影設定は変えない。OSによる実fps低下・停止は実機確認が必要。
  `CameraViewModel.swift:1068` は連続15分の復旧失敗で自動試行を終了し、冷えても手動の再開が必要。
  自動30fps化・冷却後復帰は別途方針を決める提案。既報 N2 の中断中の再試行問題も未解消。
- **中・運用上**: `DeviceHealth.swift:43` の警告は低電池・低電力モードを入力に持たない。
  電池5%は数値として表示されるが専用警告はなく、低電力モードと画面輝度も管理しない。
  給電不足の警告、低電力モード表示、画面輝度の方針追加は提案に留める。
- `CameraViewModel.swift:1225` の idle timer 抑止は「送信中または送信意思あり」かつ前面。
  stop() と非アクティブ化で解除する。権限待ち・権限拒否でも送信意思が残れば前面では抑止する。
  実際の座標送信だけに限定する変更はしていない。

## 確認した既存方針

- 再起動時は送信意思・auto-start、台、mirror、fps、露出実験、P2P、手動IPを保存値から復元する。
  自動宛先は IP を保存せずサービス名を保存し、同じ Mac の新 IP を受け入れる。
  保存済み Mac 名が消えた場合に別 Mac へ勝手に移らないのは既存の方針。
- 60fps形式がない場合は30fpsへフォールバックし、要求設定の60自体は保存したまま。
  送信中のfps変更は停止・再設定・再開、mirror変更は送信設定を更新する。
  台と手動IPのUIは送信中に編集できない。露出実験は録画中・保存中に変更できない。
- 再接続はポートごと、待機座標は最新1件。Bonjourで同じサービスのIPが変わると
  UDP両ポートとLAN probeを更新する。30秒の再探索、Wi-Fi断、Mac睡眠、Unity再起動の
  実際の復帰時間は本ホストで再現していない。前回レビュー N1/N3/N8 の注意も残る。
- カメラ復旧・生存確認・health集計・UI fps は単調時計を使う。日時変更は表示ログや計測用epochに影響するが、
  復旧backoffの時計を変えない。healthとUI診断は窓・件数上限がある。メモリ警告の実機試験は未実施。

## 変更と検証結果

既存の明示登録済み XCTest ファイルに3件追加（新規テストファイル・project変更なし）。

- `DetectionCoreTests.testReadyNotificationPreservesLatestPayloadWhileSendIsInFlight`:
  readyの再通知とwaiting→readyを両方検証。古い待機座標の置換、最新の保持・完了後送信、青の接続世代維持。
  **変更前FAIL（4 assertion）、変更後PASS**。送信完了は注入ハンドラで保留して決定的に順序を再現する。
- `DetectionCoreTests.testRestoredBonjourServiceAcceptsNewIPOnlyForTheSavedMac`:
  保存Mac固定、新IP受理、別Mac拒否、手動から自動への解除という既存方針を検証。PASS。
- `DeviceHealthTests.testRatesAfterEightHoursUseOnlyTheRecentWindow`:
  uptimeを8時間進め、直近5秒だけの60fps・中央値・途絶時ゼロを検証。PASS。8時間の実運転試験ではない。

ホストは arm64 macOS、Apple Swift 6.4。実行したコマンドと結果:

1. `python3 /private/tmp/phonesaber-event-audit/run_host_checks.py before`: `swiftc -O -D DEBUG` で
   変更前UDP、実EventRecovery/DeviceHealth、抽出した実カメラ純粋型・既存XCTestをビルド・実行。
   25 tests、修正対象1 testの4 assertionだけFAIL。
2. 同スクリプト `after`: 修正後をビルド・実行、最終 **28/28 PASS**。
3. 同スクリプト `release`: `DEBUG`を外してビルド成功、31 tests中30 PASS。
   実NWListenerを使う既存LAN probe受信エラー復旧テストは `POSIX EPERM / Operation not permitted` でFAIL。
   sandboxがloopback UDP待受を拒否したため、実ソケット復旧は確認できなかった。
   Network.frameworkは実物、P2PSenderのみ未使用のstub。iOSアプリ全体のReleaseビルドではない。
4. `python3 PhoneSaber/ios/PhoneSaberSenderTests/measure_ui_load.py --checks`:
   実UI集約器と既存テストを抽出したMac XCTest、**3/3 PASS**。
5. `git -c filter.lfs.process= -c filter.lfs.required=false diff --check -- PhoneSaber`: PASS。
   通常のgit statusは共有Git LFS一時領域への書き込み拒否で失敗したため、filterを無効化して確認した。
   filter無効のstatusではUnityフォント8件が変更表示されたが、clean filterを使わない表示のため実変更かは未確認。
   それらのファイルは編集していない。

## オペレーターに残す検証

- 公式 `bash PhoneSaber/tools/verify_phone_saber.sh`、iOS Debug/Releaseビルド、iOS XCTest全体。
  この作業ではユーザー指定のとおりxcodebuild・Simulator・公式verifyを実行していない。
  追加3テスト、EventRecoveryTests/DeviceHealthTests、既存UDP coalescing/waiting/watchdog/stop/host変更テスト、
  `P2PTransportTests.testLANLivenessProbeRebuildsTheConnectionAfterAReceiveError` を含める。
- 充電した箱内で8時間運転し、serious/critical、冷却復帰、5%・低電力モード、実fps・メモリ・UI警告を確認。
- 通話・Siri・Control Center・通知バナー・他アプリのカメラ・ロック/解除・権限取消/再許可。
  特に復旧の非同期stop完了前に背景へ移す。停止後のauto-lock再有効化も確認。
- OS終了後の再起動で保存設定と宛先、自動送信を確認。Wi-Fi30秒断の同IP/新IP、
  Mac睡眠/復帰、Unity再起動、2台広告、送信中のfps/mirror/P2P変更、日時変更、メモリ警告を確認。
