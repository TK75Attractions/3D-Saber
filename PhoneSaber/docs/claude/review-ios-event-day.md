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


## 当日 hardening 実装（2026-10-11、中断した WIP の続行）

開始時は `67437af`（wip）と clean な作業ツリー。WIP の8ファイルをレビューして保持し、
復旧中の熱対策による再停止と、背面での比較終了によるカメラ再設定を追加で防いだ。
ユーザー指定に従い、新しい commit / push・公式 verify は行っていない。
以下の行番号は続行後のソース。前半の「提案のみ」は、この節の実装で更新する。

### 所見・実装（重大度順）

| 項目・重大度 | 場所 | 具体的な失敗シナリオ | 実装・係員への表示 |
|---|---|---|---|
| 1・高 | `DeviceHealth.swift:133`、`CameraViewModel.swift:1229` | 箱内・給電・60fpsで発熱しても、要求60fpsのまま8時間運転する。WIP では復旧中のfps切替がbackoffをリセットしうる。 | serious/criticalで実効30fps、nominal/fairが連続120秒で要求fpsへ復帰。熱による切替間隔も120秒以上。保存fpsは変更せず、既存の手動fps用stop/reconfigure/startを共用。起動・復旧中は割り込まず、live後に反映。「発熱のため 30fps に下げています。箱を開けて通風を確認」。 |
| 2・高 | `EventRecovery.swift:37`、`CameraViewModel.swift:1098`、`:1128` | 連続15分の失敗後、冷えても自動復旧しない。非同期stopの完了前にHome/ロックへ移ると、背面で再設定する。 | 既存backoffの15分後は60秒間隔で無期限再試行。送信停止・背面では再設定せず、完了時にも送信意思と前面を確認。復旧中は通風・カメラ権限の確認を促す表示を維持。背面は「アプリを開いてください」。 |
| 3・高 | `DeviceHealth.swift:168`、`CameraViewModel.swift:1224` | 給電ケーブルが抜け、電池20%→10%へ低下しても専用警告がない。低電力モードでもカメラを動かす。 | 純粋な電源警告policyを既存deviceHealthWarningへ統合。送信中の未充電、非充電の20%以下、充電中を含む10%以下、低電力モードを警告。「ケーブルを確認」「設定でOFF」。既存UIタイマーから1秒間隔のhealth更新も行い、停止中の10%・低電力モードも更新する。 |
| 4・中 | `CameraViewModel.swift:1346`、`ContentView.swift:38` | 保存された手動IPが台指定を上書きし、P2Pも使えず、係員が台を変えても宛先が直らない。 | 台pickerの隣に「手動 IP 指定中: 台の選択は使われません」。送信意思がOFFの時だけ解除ボタンを表示・受理。保存手動IPと旧Bonjour固定を除去し、選択台の自動探索へ戻す。P2Pの保存ON/OFFは変更せず、ONならfallbackを再開。 |
| 5・中 | `CameraViewModel.swift:56`、`:1388`、`ContentView.swift:84` | Mac Aへ送信する一方、後から解決したMac Bが上部の名前・IPを上書きする。 | 名前・IPはhostSelectionの選択中LAN宛先から表示。BのstatusでAのstatusを上書きせず、「ほかに見つかった PC」に表示。別PCの削除でその行を解除。手動IPも実際の指定先を表示。再探索の全体通知でも選択宛先を消さない。選択・保存Mac固定・P2P内部は変更しない。 |
| 6・中 | `EventRecovery.swift:72`、`CameraViewModel.swift:1468`、`ContentView.swift:21` | F9終了後も30秒ごとの30/60切替が続く。WIP の期限切れによる保存fps復帰は、背面でもstop/startする。 | ONから単調時計で5分でOFF。フレームが来なくても既存UIタイマーと比較taskで期限を確認する。保存fpsへ戻すが、背面では設定だけ更新して撮影への反映を前面まで延期。画面上部に指定のオレンジ警告。 |
| 7・低 | `UDPSender.swift:5`、`:96` | Debugビルドでfps切替・再接続のconfigureを繰り返し、callbackとそのcaptureが一日中積み上がる。 | DEBUG専用の純粋な履歴型で直近16世代に制限。テストが使う絶対indexを維持し、破棄済みindexは無視。古いcallbackのcaptureも解放する。送信・payloadのコードは変更しない。 |

### 項目別の回帰テスト

既存の明示登録済み3つのXCTestファイルのみを編集した。新規Swiftファイル・project変更はない。

1. `DeviceHealthTests.testThermalFPSReductionCoolingAndNoFlapping`、
   `testThermalCoolingMustBeContinuousAndThirtyFPSIsUnchanged`：120秒境界、fair/nominalの連続、
   再発熱・unknownによる冷却リセット、設定30fpsを検証。
   `testThermalFPSReconfigurationWaitsForLiveForegroundCamera`：停止・背面・起動・復旧中は再停止しない。
2. `EventRecoveryTests.testBackoffKeepsRetryingEveryMinuteAfterLongFailureAndCanReset`：
   15分境界から8時間まで466回の仮想時計サンプルで60秒継続。
   `testOneFrameDoesNotResetFailureBudget`、`testStableFramesResetBackoff`：1frameと10秒安定復帰を区別。
   `testRecoveryStopCompletionDefersUntilForegroundAndSending`：stop完了時の背面・停止と前面復帰。
3. `DeviceHealthTests.testBatteryAndLowPowerWarningsAppearInTheExistingHealthWarning`：
   抜線、20%/20.1%、10%/10.1%、充電/full/unknown、停止中、不正値、低電力モード。
4. `DetectionCoreTests.testClearingManualHostRequiresStoppedIntentAndUnlocksAutomaticSelection`、
   `testManualHostClearPreservesStationAndP2PPreference`、`testManualHostClearIsBlockedDuringPermissionWait`：
   解除後の自動宛先、台A保持・台B拒否、P2P ON/OFF保持、権限待ち中の解除拒否。
5. `DetectionCoreTests.testDestinationDisplayKeepsSelectedMacWhenAnotherMacResolves`、
   `testBonjourDisplayUsesSelectedDestinationInViewModel`：A→B、B削除、Aの新IP、手動IP、全体探索通知。
6. `EventRecoveryTests.testFPSComparisonExpiresAfterFiveMinutesEvenWhenNotSending`：299.999秒/300秒、
   OFF・再ON、8時間経過。`testFPSRestoreInBackgroundDefersCaptureRestart`：背面で値を戻しても
   runningを落とさず権限要求を再発行しない。
7. `DetectionCoreTests.testUDPSenderCallbackHistoryIsBoundedAndKeepsAbsoluteIndices`：
   configure100回で16世代、古いindex0無視、84/99有効、captureしたオブジェクトの解放。

### 実行した検証・結果

ホストはarm64 macOS / Apple Swift 6.4。生成コード・実行ファイルは `/private/tmp` のみ。
ホストでは実 `DeviceHealth.swift`・`EventRecovery.swift`・`UDPSender.swift`、抽出した実カメラ純粋型・
宛先/開始/fpsメソッド、既存XCTestを `swiftc -O` でビルドして実行した。
Network.frameworkは実物、AVFoundation/Bonjour/P2Pの起動はstub。全iOSアプリの実行ではない。

- `python3 /private/tmp/phonesaber-event-audit/run_hardening_checks.py before`：
  `be01dc5`相当のWIP前ソース、41 tests / 14 failed tests / 61 failed assertions。
  新規APIが存在しないため、旧挙動（固定fps・解除操作なし・比較期限なし等）を表す互換アダプタを
  ホスト内だけに追加して比較した。元から存在するbackoff・UDP・宛先更新メソッドは旧実装そのもの。
  7項目それぞれのテストが失敗した。既存corpusや期待値は変更していない。
- 同スクリプト `wip`：`67437af`、41 tests / 2 failed tests / 4 failed assertions。
  WIPのhealth条件を抽出したpolicy比較と、実selectCameraFPSメソッドで、復旧中の熱切替・
  背面のfps復帰を再現。続行後は両方PASS。
- 同スクリプト `after`：`swiftc -O -D DEBUG -D EVENT_RECOVERY_STANDALONE -D DEVICE_HEALTH_STANDALONE`、
  最終 **47/47 PASS**。既存のUDP最新値保持、waiting timeout、片ポート再接続、send watchdogと
  古い完了の拒否、停止時の再接続キャンセル、健康な接続の前面検証も実行した。
- 同スクリプト `release`：DEBUGなしでビルド・実行、最終 **46/46 PASS**。
  DEBUG履歴テスト1件を含まない。iOS Releaseアプリのビルドではない。
- `python3 PhoneSaber/ios/PhoneSaberSenderTests/measure_ui_load.py --checks`：**3/3 PASS**。
- 以前の `run_host_checks.py after` も実行：**35/36 PASS**。実NWListenerによる既存の
  LAN probe受信エラー復旧1件は `POSIX EPERM / Operation not permitted`（sandboxのUDP待受拒否）。
  この失敗をPASS扱いにはしていない。上記47件には実ソケット待受テストを含めない。
- `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender
  -configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator'
  -derivedDataPath /private/tmp/phonesaber-hardening-build CODE_SIGNING_ALLOWED=NO build`：**失敗**。
  既存ContentViewの `@State` に対するSwiftUIMacros.StateMacroのplugin serverがmalformed response。
  Simulator serviceにも接続不能。アプリ全体のDebugビルド成功とは報告しない。
- iOS Simulator SDK、`-target arm64-apple-ios17.0-simulator -D DEBUG` の `swiftc -typecheck` と
  `swiftc -emit-module -enable-testing -module-name PhoneSaberSender`：**PASS**。
  対象はUIマクロに依存するContentView/PhoneSaberSenderApp以外の全本番Swift。
  ログは `/private/tmp/phonesaber-hardening-ios-typecheck.log`・`phonesaber-hardening-ios-module.log`。
  同対象のDEBUGなし `swiftc -typecheck` も **PASS**（`phonesaber-hardening-ios-release-typecheck.log`）。
- 同iOS SDK・`-D DEBUG`、生成した実アプリmoduleとXCTestのSwift overlayを使った、
  Xcodeに登録済みの6つのXCTestファイルの `swiftc -typecheck`：**PASS**。
  新しい3ファイルのテストも実モデルを参照して型検査した。実行したのは上記ホスト抽出テストのみ。
  最初の直接型検査はXCTest overlayの検索パス不足、続く全 `.swift` 指定は別CLI用の
  StaticBGRADetectionTestsを混ぜたため失敗した。登録済みXCTestだけの正しい設定で完走。
  ログは `/private/tmp/phonesaber-hardening-ios-tests-typecheck.log`。
- `git diff --check`、`git diff --check HEAD^`：**PASS**。
  WIP前からの差分を確認し、認識・payload・formal corpus・Unity・指定の録画/guided部分・
  P2PSender内部の変更がないことを確認した。

ホスト比較の初回は同じ生成ファイルを2モードが書く競合でコンパイルを中断した。
その後はモードごとの一時ディレクトリに分離して完走した。iOS module確認も編集と重なった
試行を破棄し、編集完了後に再実行してPASSを確認している。

### 選択理由・残る検証

- 再発熱が60fps復帰直後に来た場合も、要求された「2分以上の切替間隔」を優先して
  再低下まで最大2分待つ。間隔制限中も既存の高温警告は残る。
- 他PCは直近の1台を補助表示する。今回の2台シナリオを直し、宛先を勝手に変更する機能や
  新しいPC選択UIは追加していない。LANの宛先と既存のP2P経路表示を区別する。
- 本番のthermal/battery通知、実fps、冷却、箱内8時間、stop完了と背景遷移の競合、
  P2P fallbackの実再開、2台の実Bonjour広告、5分の実時間経過は実機未確認。
  fps切替時の短い座標途絶は既存stop/start経路によるもの。
- 公式 `bash PhoneSaber/tools/verify_phone_saber.sh`、iOS XCTest全体の実行、
  iOS Debug/Releaseアプリの成功ビルド、formal lossless検証はオペレーターが実施する。
  本作業ではcommit/pushしていない。WIPコミットは既存のまま残る。


### 2度目の中断からの再開・再検証（2026-10-11）

開始時の履歴は `0c876aa`、`67437af`（いずれも wip）、`be01dc5`、作業ツリーは clean。
2つのWIPを `be01dc5..HEAD` でレビューし、7項目の本番実装と回帰テストを保持した。
追加の本番変更は不要と判断。今回の未commit差分は、この再検証記録のみ。
新しいcommit/pushと公式verifyは実施していない。

再実行したもの（ホストテストは実ソース・既存XCTestの抽出とカメラ/P2P stubを使用）:

- `python3 /private/tmp/phonesaber-event-audit/run_hardening_checks.py before`:
  **47 tests、14 failed tests、61 failed assertions**。7項目すべての旧挙動を検出。
  新規APIのない旧実装に対する互換アダプタは、上の検証節と同じものを使用。
- 同スクリプト `wip`: **47 tests、2 failed tests、4 failed assertions**。
  `67437af`の復旧中の熱fps切替・背面でのfps復帰を検出。
- 同スクリプト `after`: `swiftc -O -D DEBUG`、**47/47 PASS**。
- 同スクリプト `release`: `swiftc -O`、**46/46 PASS**（DEBUG履歴テストを除く）。
  いずれも `EVENT_RECOVERY_STANDALONE`・`DEVICE_HEALTH_STANDALONE` を指定。
- `python3 PhoneSaber/ios/PhoneSaberSenderTests/measure_ui_load.py --checks`: **3/3 PASS**。
- `python3 /private/tmp/phonesaber-event-audit/run_host_checks.py after`: **36/37 PASS**。
  既存の実ソケット受信エラー復旧1件は、UDP待受の `POSIX EPERM / Operation not permitted` で失敗。
  ログ: `/private/tmp/phonesaber-event-audit/resume-probe.log`。実ネットワークの復旧をPASS扱いにはしない。
- iOS Simulator SDK、`swiftc -swift-version 5 -target arm64-apple-ios16.0-simulator`:
  ContentView/PhoneSaberSenderAppを除く本番コードのDEBUGあり・なしの型検査、
  DEBUGありのtestable module生成、登録済み6つのXCTestファイルの型検査はすべて **PASS**。
  プロジェクトの最小iOS 16を対象にした。ログ: `/private/tmp/phonesaber-hardening-resume/`。
- 前節と同じDebug `xcodebuild ... CODE_SIGNING_ALLOWED=NO build`を再実行: **exit 65**。
  `sandbox-exec: sandbox_apply: Operation not permitted`に続き、既存の `ContentView.swift:9` などの
  `SwiftUIMacros.StateMacro` plugin serverがmalformed response。Simulator serviceにも接続できない。
  ログ: `/private/tmp/phonesaber-hardening-resume-build.log`。UIを含む成功ビルド・画面検証は未達。
- `git diff --check be01dc5..HEAD`: **PASS**。認識・payload・corpus・Unity・除外指定ファイル、
  CameraViewModelの録画/guided部分・P2PSender内部に変更がないことを再確認。

実機のthermal・battery・カメラ・Bonjour/P2P、および8時間運転は未実施。
上の選択理由（熱切替は120秒間隔、他PCは直近1台、DEBUG callback履歴は16世代）を維持した。
