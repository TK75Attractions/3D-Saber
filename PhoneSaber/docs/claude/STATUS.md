# PhoneSaber STATUS
- 2026-10-21: tools/install_iphones.sh を追加（利用可能・ペアリング済み実機iPhoneを列挙、全UDIDで1回ビルドし各端末へ再インストール、Team/Bundle環境変数・dry-run・実プロファイル署名期限・日本語対処案内）。偽PATHコマンドのbashテスト10ケース・bash構文・変更範囲diff --check PASS、verifyのToolsへ接続。実コマンドのdry-runはCoreDeviceService初期化timeoutでblocked、実機署名/build/installは未検証、総合verifyは未実行。認識/判定/UDP・Scene/Prefab/ProjectSettings/既存meta・pbxproj変更なし、commit/pushなし。
- 2026-10-21: unity_copy.sh の editmode/build-mac に子Unityのsoft fd上限65536（hard不足時は既存値）とライセンス障害ログ検出を追加。対話Editor不在を確認できた場合だけ旧Unity.Licensing.ClientへTERMし最大1回再試行、初回ログ保全・成果物初期化。Unity関連Python76件（偽ログ/プロセス・親上限不変・隠し起動ラッパー含む）、bash -n・対象diff --check PASS。実openは偽アプリ/比較用binaryとも-10827、psはEPERMのため実Unity EditMode/build・実ライセンス復旧未検証。認識/判定/UDP・Scene/Prefab/ProjectSettings/既存meta変更・commit/pushなし。
- 2026-10-21: 非公開field_regression add/run＋合成unittest10件＋verifyのField(PASS/FAIL/未導入SKIP)を追加。10/20指定6＋10/19全9clipの原寸45画像を目視ラベルし90色判定=63PASS/27明示XFAIL（誤検出12・未検出3・端点不一致12）、終了0。指定Application Supportへの作成はsandbox EPERMのため /private/tmp/phonesaber-field-regression-20261021 に画像・ラベル・全結果を一時保存（配置待ち、既定FieldはSKIP）。formal40/40、static BGRA、DeviceHealth20、AutoClip12PASS/保存1SKIP、bash -n・diff --check PASS。総合verify/iOS Release/Unityは未実行。認識coreはgood-20261020と差分なし、認識/判定/UDP・Scene/Prefab/ProjectSettings/既存meta変更・commit/pushなし。

- ユーザー待ち: operator の差分レビュー、Unity EditMode（PhoneSaberControlPanelTests を含む）、`bash PhoneSaber/tools/verify_phone_saber.sh`、Mac Player build。内蔵画面＋プロジェクターで F8 / Shift+F8 / F7・全Web操作・スマホACKを確認。既存スマホ関連の実機確認も継続。
- 現在の仮説と確度: F8 を loopback Web パネルにするとゲームの投影先と操作先を分離できる。共通表示・操作表と HTTP 検証を実装し、純粋テスト91件・Unity DLL参照4構成compile・JS DOM検証はPASS。実ソケットは sandbox の bind EPERM、実ブラウザ・2画面表示は未確認。
- 次にやること: operator が上記検証後にレビュー・コミットする。この作業では commit / push / merge しない。

- 2026-10-21: play証拠保全: events本体2MiB×5＋archive180MiB（合計約199MB目標）、座標未受信60秒でidleを1回記録しperf/latency-estimateだけ抑制・赤青どちらかの入力で復帰。session-startからUTC日付archiveへ逐次複写（quit/次startで確定、異常終了・世代交代にも保持）、14日超を削除、容量超過時は7日より古い終了archiveから削除し直近7日/書込中は容量より保全優先（容量の未回答選択肢1を仮定）。play_report --eventsでarchive単体/ディレクトリ再帰読込・時刻順・本体との複写重複除外。Unity同梱C#9/Mono/NUnitで単体24件PASS（InputPoint/Station読取のみ代役）、Python unittest26件・diff --check PASS、Unity6000.3.9f1 EditModeはUPM IPC socket listen EPERMでテスト開始前blocked、実機/Playerは未実施。good-20261020の認識・判定・UDP・既存ログ形式を変更せず、Scene/Prefab/ProjectSettings/meta変更なし、commit/pushなし。
- 2026-10-20: iOS AutoClipを実analyzeSabers出力でwinner照合し、色別trackLockSuppressedを保存、winner→分離競合→その他eligible→ineligible順（上限12、ineligible最大2）に記録。既計算のemitter入力8列を追加（emitterScore・nearSaturationPresent・aspectはobserver接続時だけ保持、画素走査の追加なし）、内部autoClip.formatVersion=2／転送envelope v1互換、480KiB超過時もトリガー付近の候補を保持。Mac reportはv1のselでwinnerを照合しv2の抑制・証拠列も表示、旧17:40:32の2235/2247/2248は実rank6/5/4を確認。run-static-tests.sh（BGRA＋DeviceHealth20＋AutoClipホストXCTest12PASS/1SKIP）、run_latency_host_tests.py（38＋6）、formal lossless40/40、旧HEAD/現行/observerの全候補・選択・pipeline署名40/40完全一致（SHA256 bebc0c6d6a56a16d3d82f18bab66d5e346b09eb4c47f6cc10e8bf09e1907e8e9）、Python非socket9件、変更対象のdiff --check PASS（全体は既存フォント差分の空白でNG）。保存E2Eはsandboxで空き容量API=0（保存ガード維持）、既存Python HTTP受信1件はbind EPERMでblocked、正式Simulator XCTest・実機は未実施。認識のselection/閾値/score/eligibility/候補・UDP変更なし、iOS・Mac report・本行のみ、commit/pushなし。

- 2026-10-20: PhoneSaberLaunchGuardを追加し、Mac standaloneの直接起動だけBeforeSceneLoadで/Applications/Beat Trace Slash.appをshellなしで開き即時Quit（Editor/Windows/batchmode・-phonesaber接頭辞引数・PHONESABER_LAUNCH_GUARD=0は対象外、launcher不在は警告のみ、PlayerPrefs書込みなし）。既存startupは変更せず速やかな終了に依存するため、終了反映前のポート確保は完全抑止しない。新規EditMode判定13ケースの補助ホスト実行とUnity実DLL参照Mac Player/Mac Editor/Windows PlayerコンパイルPASS、正式Unity EditModeは一時projectでUPM socket listen EPERMにより開始前停止、実機復元は未検証。新規2スクリプト＋新規ランダムGUID meta2件と本行のみ変更、commit/pushなし。
- 2026-10-20: iOSのみ、追跡ON・既存プレイ範囲ありで色別winnerの連続選択0.5秒後から両端＋既存margin/minimumSizeで拡張のみ（0..1、新規取得は拡張済み範囲内限定、OFF/未設定は拡張なし）。FrameProcessor作業用範囲を即時更新、mainへの保存・要約更新・PLAY_AREA growログは5秒に1回以下（既存PhoneSaber.playArea.v1、古い通知は設定revisionで破棄）。無視範囲に正規化両端を保存し、両端とも矩形内かつ各端がmax(0.03,登録長×0.25)以内（端の順序不問）の物体だけ無視、旧保存形式は矩形判定を維持、AutoClip selectableも同じ判定。static＋DeviceHealth20件、latency host38+6件（main停滞時の通知集約も確認）、AutoClip追加XCTest1件、formal lossless40/40、Android core test/parity（325 PNG・541遷移・27合成、不一致0）、iOS16/Swift5型検査（ContentView/App除外、既存警告あり）、diff --checkがPASS。既存未コミット差分を保持、Android/Unity編集・閾値/採点/順位/eligibility/候補一覧/UDP変更・commit/pushなし。実機のコーン横切り・自動拡張、正式Simulator XCTest/アプリbuildは未実施。

- 2026-10-20: 10/19 17:40実機（iPhone 13、追跡ON・露出−2、学習範囲y=0.11..0.805）の autoclip_20261019_174032_190 で、eligibleあり未送信192フレーム（赤71・青121）は中点y=0.81..0.99の範囲外だけが原因（無視範囲・追跡ゲート拒否なし）。iOS/Androidとも追跡ONではプレイ範囲を取得・再取得時だけに適用し、生きた追跡は範囲外でもゲート内最高得点を選択（0.25秒・3ミス、無視範囲、閾値・採点・順位・eligibility・候補一覧・学習・UDP・追跡ON/露出−2設定は維持）。両色の合成テスト、static（DeviceHealth20件）、latency host（35+6件）、core test/parity（325 PNG・541遷移・27合成、不一致0）、formal lossless40/40がPASS。OFF変更前後の325 PNG＋120合成/範囲設定ケースの全候補・選択・診断署名もビット一致（SHA256 f3fa76dffdcc03e9089c87b7ceb221b2111d63113da424819af50d37ad4313c5）。実機再試験は未実施。変更はPhoneSaber内のみ、commit/pushなし。

## 2026-10-20 F8 の別画面Web操作パネル（未コミット）

- `PhoneSaberControlPanelServer` を追加。`TcpListener` を 127.0.0.1:5010 に bind、使用中なら5011〜5019。起動ごとの暗号学的乱数128-bit hex tokenが全要求に必須。Host は選択ポートの `127.0.0.1:<port>` / `localhost:<port>` のみ。URL・tokenはログに記録しない。
- endpoint: `GET /?t=...`（日本語・外部リソースなしのHTML）、`GET /state?t=...`（JSON、ページが500msごとpoll）、`POST /action?t=...&name=...&value=...`（許可した操作だけenqueue、202）。未知操作・不正値400、token/Host不一致403、未知path404、不正method405、サイズ超過413、32件queue満杯503。HTTP/1.1・8KiB request / 2KiB target・body/chunked/重複header/query拒否・短いread/write期限・Connection: close・no-store・no-referrer・CSP。
- main thread の `BuildPanel` が両色受信・スマホ計測/遅延・判定遅延・予測・filter/stabilizer・表示同期/上限・位置補正・PC無視・送信中/最後のACK（`LastResultText`）・サービス/ログを構築し、同じモデルをIMGUIとJSONで使用する。両UIのボタンは同じ24種類のhandlerへ送る。Web操作はOverlay.Updateで最大8件/frame実行。socket threadはUnity API / PlayerPrefs / event logを呼ばない。
- 操作名（値）: `calibration-start/advance/cancel/reset`（空）、`calibration-blue/enabled`（0/1）、`prediction`（auto/0/20/40/60）、`display-sync`（0/1）、`frame-cap`（0=ゲーム/1=4倍/2=2倍）、`judge-mode`（0=ゲーム/1=自動）、`judge-adjust`（-10/10）、`judge-centre`（空）、`filter`（0/1/2）、`stabilizer`（0/1）、`phone-ignore`（red/blue/both）、`phone-clear-ignore/learn-area/clear-area/track-lock/clip`（空）、`phone-exposure`（0/-1/-2）、`pc-ignore`（red/blue）、`pc-clear-ignore`（空）、`clocks-synchronized`（0/1）。各 `calibration-*` / `phone-*` は接頭辞を含む完全な名前で送る。
- F8: default browserにtoken付きURLを開き、旧パネルは非表示、ゲーム上に「操作画面をMacの画面に開きました」を3秒。Shift+F8はIMGUIの開閉、server起動失敗時のF8もIMGUI。F7は従来の採取を進め、Web利用時は位置補正目印のみ投影する。Web利用不可なら従来どおり予備画面も開く。測定中の再Openでは採取を維持、キャンセルは専用操作または予備画面の閉操作。start/open/actionをmain threadから `event=control-panel` に記録。
- `Application.isPlaying && !Application.isBatchMode && PhoneSaberP2PBridgeProcess.AutoStartEnabled` のときだけ起動。disable/destroy/quit/セッション再初期化でlistener・接続をcloseしてthreadをjoin。Keysは変更許可外のため、Tの反転状態だけ既存private static fieldをcached reflectionで共用し、キーボードとdefaultsの同期を維持する（EditModeでfield存在も確認）。
- PASS（補助ホスト検証）: 新規EditModeテストの純粋部分91件（request解析/認証/Host/method/size/action全選択肢/JSON/F8）、実Unity DLLを参照して全runtime scriptsをMac/Windows×Editor/Player計4構成でcompile、新規テストもcompile。既存2件のwarningのみ。Nodeでinline JS構文、500ms poll、token付きPOST、ACK文字列のtextContent表示、disabled/selected、DOM維持、接続失敗表示を確認。
- 制限: port 0のソケットテストは `SocketException` で失敗し、Pythonでも127.0.0.1 bindが `PermissionError errno=1 (EPERM)` と確認。純粋部分の再実行ではこの1件とUnity GameObjectが必要なhandler対応テスト1件を除外。利用可能なブラウザsurfaceが無く実画面プレビュー不可。正式Unity Test Runner・verify_phone_saber.sh・Player build・2画面/スマホ実機はoperator実行待ち。ホストcompileはPlayer build成功の代用ではない。
- 範囲: 指定Overlay・新規ControlPanel 3ファイルとmeta・新規Editorテストとmeta・本STATUSだけ。新規metaは既存形式＋ランダム32hex GUID。認識/判定実装/UDP形式、Scene/Prefab/ProjectSettings/既存metaは不変。作業前からのフォント8ファイルの差分は触れていない。変更対象の `git diff --check` と新規source/metaの空白検査、全Assets内の新規GUID一意性、24 handlerと許可表の一致を確認してPASS。commit / push / mergeなし。

## 2026-10-19 起動・新規端末の既定値再同期（未コミット）

- iOS / Android に `defaults=1` を追加。version 1・id必須・単独操作・未知フィールド無視・重複は後勝ちという既存規則を維持し、既存setterで追跡ON・露出−2を保存する。ACKは `result=defaults detail=tracklock-on-ev-2`。
- UnityはAutoStartEnabled時のresponder起動ごとに1回と、直近60秒に探索が無いIPからのprobeで自動投入。新規IP時はpendingが0の場合だけ受信threadがフラグを立て、main threadでもpendingを再確認するので運営指示を上書きしない。投入時にT/Eの静的状態をON / index 2へ戻し、値だけで `phone-defaults` を記録する。
- 自動指示は送信中・ACK・無応答のいずれもトースト非表示。F8には状態を表示できる。defaults ACKの表示は「iPhone: 既定に戻しました（剣を追いかける ON・露出 −2）」。ACK storeのVolatileなRevisionと3秒の無応答期限だけでRefreshToastを再計算し、変更のないフレームでは文字列・LINQ配列を生成しない。
- PASS: `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`（Static BGRA + DeviceHealth20件）、`python3 PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py`（35+6件）、`make -C PhoneSaber/android/core test parity BUILD=$TMPDIR/core-build`（Python6件、formal40件、325 PNG / 541遷移 / 27合成で不一致0）。sandbox-exec等の警告はあったが、上記3コマンドはいずれもexit 0。
- 追加PASS: 実UDPSender等をmacOSでcompileしたMetricsDatagramTests9件、キャッシュ済みKotlin compiler / JUnitによるIgnoreZonesTest + PlayAreaTest12件、Unity実DLL参照のC# compileと純粋部分28件（defaults文字列・新規IP規則・静的状態reset・自動toast抑制・revision/期限判定を含む）。ホスト側の時計・自動起動フラグの代役を使用し、Unity Editorは実行していない。
- 今回のverify・Gradle・Unityはユーザー指定でoperator担当、未実行。新規変更は認識・閾値・座標UDPに触れていない。既存未コミット差分を保持し、commit / pushはしない。

## 2026-10-19 defaults / PC command ACK（レビュー待ち、未コミット）

- 既定: iOS `PhoneSaber.trackLock.v2` / Android `trackLock.v2` は ON、iOS `PhoneSaber.exposureBias.v2` / Android `exposureBias.v2` は −2 EV。v1 の値は移さず、以後のトグル・PC 指示は v2 に保存。自動送信の既存キーは未保存だけ ON（保存済み OFF・XCTest/underTest は従来どおり）。Ctrl+Shift+T は最初 OFF、E は最初 0、その後 −1→−2→0。認識閾値・scoring・ranking・eligibility・座標 UDP は変更なし。
- ACK: 指示を受けた PC の UDP 5007 に `PHONESABER_ACK 1 id=N station=<atom> platform=ios|android result=<atom> detail=<atom>` を専用探索キュー/threadで約0.3秒間隔3回送信。200 bytes未満、atom は `[A-Za-z0-9_.-]`、台未指定は `none`。同じ指示IDの再受信では再実行しない。
- result/detail: `ignored/red1-blue1`（色別追加数、片色0も可）、`nothing/red0-blue0`、`cleared/ignore|area`、`learning/10s`、`learned/samplesN`、`failed/samplesN`、`exposure/ev0|ev-1|ev-2`、`tracklock/on|off`、`clip-saved/saved`、`clip-off/off`、`autoclip/on|off`。学習は開始と完了を同じIDで返す。iOSのclip成功はwriter完了時だけ。Androidは既存clip機能未対応のためclip-off/autoclip offを返す。
- Unity: 最大32指示×8送信元のlock付きstore。未発行ID・不正ACKは無視、未知フィールドは無視。学習完了後に遅いlearning再送が来ても表示を戻さない。全ACKを `phone-ack` に記録し、トーストとF8で同じ日本語結果を表示（複数台は複数行、3秒無応答の表示は自動消去しない）。
- PASS: `make -C PhoneSaber/android/core test parity BUILD=$TMPDIR/core-build`（Python6件、formal40件、325 PNG / 541遷移 / 27合成で不一致0）、`run-static-tests.sh`（Static BGRA / DeviceHealth20件）、`run_latency_host_tests.py`（35+6件）。書込み可能なCLANG/SWIFTキャッシュを指定した `run_lossless_regression.py` は40/40（positive23/23、negative誤検出0/17）。
- 追加検証: 実ソースから抽出したLANLivenessProbeと既存MetricsDatagramTestsのhost実行8件PASS、Android全Kotlin型検査＋IgnoreZonesTest/PlayAreaTestの直接JUnit実行11件PASS。C#は実ソース＋Unity API代役による純粋テスト27件PASS（正式EditModeの代わりにはしない）。iOSはContentView/Appを除く全ソースのiOS16/Swift5型検査PASS。実Unity DLL参照でMac/WindowsのEditor/Player計4構成のcompile PASS（Editor/Playerは実行しない）。
- 指定の総合 `verify_phone_saber.sh` は実行したが全PASSではない。Simulatorサービス/UDP bind/既定Swiftキャッシュ/Git LFS共有領域がEPERM、ReleaseはSwiftUIマクロのnested sandbox起動失敗。Toolsは448件中failure15/error36/skip1（socket・キャッシュ等の制限）。既定cacheのlossless失敗は上記tmp指定で解消。変更範囲の `git diff --check` はLFS filterを無効化してPASS。
- 指定Gradle `:app:testDebugUnitTest :app:assembleDebug` はホームのwrapper lock書込み禁止。GRADLE_USER_HOMEをtmpにした再試行もFileLockContentionHandlerのsocket作成EPERMで停止。APK未生成。Unityは指示に従い起動・テスト実行なし。新規C#テストのmetaは既存形式＋新規ランダムGUID。

## 2026-10-19 track lock（iOS / Android、既定 OFF）

- 実装: eligibility・無視範囲・プレイ範囲を通る現フレームの候補だけを対象とする。色別に実測 winner の中点・長さ・更新時刻と、直前2回の実測中点の差分速度を保存する。更新から 0.25 秒以内なら `中点 + 速度 × dt` を予測し、`R = max(1.5 × 前回長さ, 0.12 × フレーム対角長)` 以下で最高得点の候補を選ぶ（同点は元の順序）。
- ゲート内候補が無ければ3フレームは選択・補間 UDP なし。3回目に追跡を捨て、4フレーム目は通常の最高得点から再取得する。完全に色が無いフレームも数える。0.25 秒超の消失・サイズ変更・カメラ再起動でも履歴を消す。追跡用の履歴は FrameProcessor が所有し、0.18 秒のプレビュー保持用履歴とは分けた。
- 操作: 両スマホの無視範囲付近に「剣を追いかける（通行人対策・試験）」を追加。UserDefaults / SharedPreferences に保存し、未設定なら OFF。`PHONESABER_COMMAND 1 id=N track-lock=0|1`、未知項目無視・同一項目の後勝ち・新操作の混在拒否に対応し、再送 id は別指示の後でも実行しない。Unity の Ctrl+Shift+T は静的な最後の送信値（開始 OFF）を反転し、ON/OFF のトーストを出す。
- OFF 不変性: threshold / scoring / ranking / eligibility / 候補一覧 / PCA / UDP 形式を変更していない。HEAD `b693f67` の検出 core と変更後 OFF を同じ静的 signature runner・module 名で比較し、325 PNG と runner 内の合成入力で全候補属性・選択座標・診断の `Double.bitPattern` が一致（SHA256 `16293d8cac0915bb1f023328f91624df5e48c7f553b900366730405cb4a28f22`）。
- `make -C PhoneSaber/android/core test parity BUILD=$TMPDIR/core-build`: C++ 合成テスト・Python 6件 PASS。parity は 325 PNG / 541 状態遷移 / 27 合成ケースで不一致0、formal fixture 40件の失敗0。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: 合成検出（赤青の近傍維持・3回消失・4回目の再取得・空フレーム・期限・寸法・範囲・速度）と DeviceHealth 20件 PASS。
- `CLANG_MODULE_CACHE_PATH=$TMPDIR/track-lock-clang SWIFT_MODULECACHE_PATH=$TMPDIR/track-lock-swift python3 PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py --json $TMPDIR/track-lock-lossless.json`: formal 40/40 PASS、positive 23/23、negative 誤検出0/17。最初のキャッシュ既定パスへの書込み拒否は tmp の cache 指定で解消。
- `python3 PhoneSaber/ios/PhoneSaberSenderTests/run_latency_host_tests.py`: 35件 + 経路6件 PASS（本番 FrameProcessor の実 BGRA フレームを使う追跡・補間 UDP 抑止・カメラ再開・OFF テストを含む）。Python のソースは変更していない。
- 補助ホスト検証: 実 UDPSender + MetricsDatagramTests 7件、実 Protocol.kt + IgnoreZonesTest / PlayAreaTest 10件、実 Unity/InputSystem DLL + PhoneSaberOperatorKeysTests 3件 PASS。Android 全 Kotlin ソースも既存 AndroidX と SDK 37 で単独 compile PASS（BuildConfig のみ stub、APK の代用ではない）。NDK arm64/API29 で core + JNI の共有ライブラリ compile/link PASS。補助 runner・ログ・生成物は TMPDIR のみ。
- 2026-10-19 取り込み時に Claude が sandbox 外で再検証: verify_phone_saber.sh 全 PASS、core test+parity 不一致0、Gradle testDebugUnitTest+assembleDebug PASS、Unity EditMode（PhoneSaber）318/318 PASS。
- （Codex sandbox 内では未実行だった）指定 Gradle `--stop` / `--no-daemon :app:testDebugUnitTest :app:assembleDebug` は指定 JAVA_HOME でも FileLockContentionHandler の socket 作成が EPERM で起動不可。`xcodebuild ... -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' ... CODE_SIGNING_ALLOWED=NO build` は sandbox の SwiftUI マクロ用プロセス起動失敗（malformed response、CoreSimulator も接続不可）で停止。Unity Editor 経由の Test Runner は未実行。iOS の CameraViewModel を含む id 再送 XCTest は全 XCTest 実行時に要確認。
- 自己レビュー: 既存の候補生成・点数・順位・範囲条件・送信ペイロードは維持。Unity の変更は指定3ファイルだけ。正式 corpus・期待値・scene/prefab/meta・ProjectSettings は変更していない。`git diff --check` PASS。

資料の索引と、実行ファイル・道具の場所は [README.md](README.md)。

## 2026-10-18 実機実験のまとめ（台A Mac+iPhone 13、台B Windows+AQUOS sense9、プロジェクター）

- 分かったこと: (1) iPhone がテザリングの親機だと発熱で 30 fps に落ち、同じ内容の経路通知で 1 秒ほど座標が止まる → 通知は無視（7c5a1c1）、親機は別の機器にする。(2) 判定補正の固定 120 ms は大きすぎ → 測定値で自動（PhoneSaberJudgeLatency）。予測 ON では負まで下げられる（63bb773）。judge-ms はゲームの誤差（LastCutSongTime − HitTime）で記録（a45a721）。(3) 会場の赤いコーンが剣より選ばれる（台A 約6 %、台B ほぼ毎コマ）→ スマホ側で候補を選ぶ前に外す「無視する範囲」（iOS 7c5a1c1、Android fd12421、PC F8「スマホ: 今の赤を無視」で遠隔操作）。PC 側の無視（f22f163）は予備。(4) 剣の長さの揺れ・誤認識の跳び → 揺れ止め（57287b8、30 fps で固まった分を 198894b で修正）。メニューは 0.25 s の猶予と照準の跳び止め（81524dc, f22f163）。(5) AQUOS は 60 fps 不可（AE 最大 30）。
- 運用: コーンなど赤い物に暗幕、プレイヤーの後ろも暗幕で暗くする。赤い服の人への対策（プレイ範囲の外の候補を外す・露出を下げる）は作業中。
- 事故: Codex の自動取り込みが 2 回チームの譜面を古い内容へ戻した（14b8cc0 → 5da8d50 で復元、f4fbe96 は本人が復元済み）。auto_merge.sh は範囲外のファイルで止まる。runner は停止中。unity_copy.sh editmode は一致したテストのクラスだけを流す（0f41188、譜面エディタの窓が出ていた）。
- 解析: `python3 PhoneSaber/tools/play_report.py --last-session` / `--by-trial`（F10 の印ごと）。events.log は 1 MB で回るが、ツールは .1〜.9 も読む。

## 2026-10-18 未明: 遅延対策（10-18 に実機で比較する）

- 10-17 のプロジェクター実プレイ（`play_report.py --since 07:46Z --until 07:50Z`）: iPhone がテザリング親機（送信元 172.20.10.1）で thermal=serious のまま 30 fps、c2s 49 ms・座標 30/s。通信 3〜5 ms、PC 5 ms、Mac 負荷 2.6。判定は固定補正 120 ms で当たりの中央 −31 ms（早め）、最適値の推定 89 ms。
- 入れたもの: 判定補正の自動化 `PhoneSaberJudgeLatency`（推定の合計＋台ごとの調整、予測 ON 時は先読み分を差し引く、F8 で「ゲームの設定」に戻せる、「当たりの中央に合わせる」ボタン、`event=judge-latency`）／判定ごとの記録 `event=judge-timing`／メニューと F8 にスマホの発熱・30fps・テザリング親機の注意／iPhone の低発熱表示（送信中 20 秒無操作で preview 停止・明るさ 0.05）／`PhoneSaber/tools/play_report.py`（1回のプレイの要約・`--compare`）／build-mac の smoke を流れだけの失敗で1回再試行。メニューは画面と同じ fps、本編は 2 倍。
- 未検証（実機）: 低発熱表示で 60 fps が保てるか、自動補正で当たりが中央に来るか、プロジェクターの処理遅延（F9 は投影の赤を認識できず測れない → 当たりの中央で合わせる）。比較候補: ネット（ルーター／別端末の親機／USB テザリング）、P2P 予備 ON/OFF（LAN 中も 0.25 秒ごとに ping する）、予測 OFF/自動。

## 2026-10-16 flaky_p2p-1016 最終監査・未検証変更の撤去

- 対象: `git log --oneline -5`、`git diff main...HEAD`、8a510ae の前回メモ。開始時 clean。commit/push・GUI起動なし。
- P2: 前回変更は対象assert未到達で、変更前FAIL→変更後PASS・CPU負荷下の改善率・新しいpong 1/2/3個のassertが未検証。初回pongの3秒超過とticker/仮想時計の競合はいずれも原因未確定。
- 再確認: 対象XCTest/FakeBridge/LockedBoxを抽出し、実P2PSender/P2PProtocol/UDPSender/MetricsDatagram/HostMonotonicClockと `xcrun swiftc -O -D DEBUG` でビルド。前回変更版・main版ともコンパイル成功、実行はNWListenerのEPERMでassert前に停止。修正効果の証拠にはならない。
- 結論: 「完了または除去」の指示に従い、前回のテスト変更と `tickForTesting()` を全撤去。両Swiftファイルはmainと同一。既存のflaky自体は未解決で、修正済みとは扱わない。新しいバグ修正・回帰テストの追加なし。
- 未完監査: 残る新規option/TODO/dead code/stubなし。前回メモはBLOCKEDを明記しており成功の偽記載はなかったが、撤去後の実装と矛盾しないよう本節で置換。/tmpのharnessを受入条件にしない。
- 既定動作: `git diff main` のコード差分0。認識/ranking/eligibility/scoring/PCA/threshold/UDP形式/formal corpus/Unity scenes・prefabs・meta/ProjectSettings不変。新規C#/Swift/Kotlinなし、meta/project追加不要。
- 実行環境: 初回Pythonの依存不足・Swiftキャッシュ書込拒否後、Python 3.12.1と/tmp内のSWIFT_MODULECACHE_PATH・CLANG_MODULE_CACHE_PATHで再実行。以下は再実行結果。
- `python3 -B -m unittest discover -v`（PhoneSaber/tools）: 324件、failure 1/error 1/skip 12/既存expected failure 1。auto-latencyとWindows socket policyのlocalhost拒否。全PASSではない。
- 同コマンド（ios/PhoneSaberSender/Tools）: 445件、failure 9/error 51/skip 1。50 errorはbindのEPERM、1 errorは録画E2Eの空き容量照会0。9 failureはreceiver bind拒否と、同じbindを使うcompiler-child準備待ち。後者は単独再実行も2 subtest失敗。
- 同コマンド（PhoneSaber/）: 85件、failure/error 0、skip 14（ソケット禁止等）。
- `python3 PhoneSaber/tools/check_pc_latency.py`: PASS（stats 50/sender gate 14/frame 10/NUnit 35、Unity API compile各構成成功）。Editor/Player実行ではない。
- `python3 PhoneSaber/tools/check_auto_latency.py`: C# 4 PASS/1 FAIL（loopback Access denied）で停止。後続Swift→C#実通信は未到達。
- `make -C PhoneSaber/android/core test parity`: C++/Python 6件PASS、formal 40件成功、325 PNG/541遷移/27 synthetic不一致0。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: Static BGRA・DeviceHealth 19件PASS。
- `python3 PhoneSaber/ios/PhoneSaberSenderTests/run_{latency,high_speed,camera_lifecycle,debug_transfer}_host_tests.py` を各実行: latency 39/high_speed 2/camera_lifecycle 18/debug_transfer 7件PASS。
- `run_send_gap_host_tests.py`: 24件中18 PASS、6件NWListener EPERM。抽出した既存P2P純粋ロジックXCTest 13件PASS。
- `python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/run_lossless_regression.py`: 40/40 PASS（positive 23/23、negative false positive 0/17）。
- `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /tmp/phonesaber-finish-1016/ios-release CODE_SIGNING_ALLOWED=NO build`: exit 65、sandbox_apply拒否→SwiftUIマクロ起動失敗。
- `xcrun simctl list devices booted`: CoreSimulatorService接続拒否。iOS XCTestは未実行。Unity Editor・実機/AWDL・公式verifyも未実行。GradleはAndroid差分がないため対象外。
- operator残検証: repo rootで `bash PhoneSaber/tools/verify_phone_saber.sh`（既存iOS XCTest/Release・Unity EditMode/PlayMode/compile）。今回追加のUnity/実機試験や前回変更の前後50回比較は不要（変更を撤去）。既存flakyの原因確定・修正は別途必要。
- `git diff --check` PASS。全ログ: `/tmp/phonesaber-finish-1016/`。空き容量照会0はdfの409GiBと不一致、録画E2Eは制約外で要再確認。いずれも今回差分による失敗ではなく、gate緩和・無関係な修正なし。

## 2026-10-15 Android 無人起動・準備確認 最終監査

- 対象: `git log --oneline -5`、`git diff main...HEAD`、ac98d54/aa841d1 の前回メモ。開始時 clean。commit/push・GUI起動なし。
- P2 `tools/latency_matrix.py:87`: 選択Androidのカメラ停止中でも別iPhoneの測定だけでexit 0。条件ごとにadbでwlan0 IPv4を取得し、Androidの新鮮な赤metrics・各blockのdisplay行（同じ送信元、conditions-changed=0）・終了行を照合。不明な測定元は失敗、他端末の数値は集計から除外。
- P2 `tools/android_ready.py:185`: 時計源UNKNOWNの正常端末もfloat変換で必ずNG。撮影起点の2項目だけ明示的UNKNOWNを許容。欠落・NaN・負数・送信停止・処理計測不足は引き続きNG。
- 回帰3本を旧HEADへ適用: assertion 12失敗、harness error 0。修正後: `test_android_ready.py` 19件／`test_tracking_report.py` 21件成功（macOS/Windows分岐・別iOS/Android・送信元変更・metrics欠落/陳腐化・混在時の数値も確認）。
- 未完成監査: 上記2欠陥と以下の未検証を確認。追加TODO・dead code・stub・未到達optionなし。adbの自動開始/30/60/lowlatは型付きextrasの既存回帰とGradle/hostで検証。
- 既定動作: 引数なし保存設定・iOS起動コマンド維持を回帰確認。前回メモの今回限定extras以外のゲーム動作変更なし。認識/UDP形式/formal corpus/Unity資産/ProjectSettings差分なし。
- 新規C#/Swiftなし。新規KotlinのLaunchSettings/LaunchSettingsTestはGradleに自動収録され、host compile一覧にも収録済み。
- `PATH=/Users/satoshi/.pyenv/versions/3.12.1/bin:$PATH python3 -B -m unittest discover -s PhoneSaber/tools`: 315件、失敗0、skip 11（Windows/PowerShell）、既存expected failure 1。
- `python3 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools`: 439件、skip 1、error 1。E2E setUpClassが容量API=0で拒否（required=1040187392、df空き414GiB）。単独再試行も同じ。容量ゲート未変更。
- `python3 -B PhoneSaber/tools/check_pc_latency.py`: Unity API 6構成compile、NUnit host 35/35成功。
- `python3 -B PhoneSaber/tools/check_auto_latency.py`: C# 5/5、Swift 3/3、実Swift→C# UDP往復成功。
- `make -C PhoneSaber/android/core test parity`: C++・Python4件、formal40件/325PNG/541遷移/合成27件、不一致0。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: static BGRA・DeviceHealth 13/13成功。
- `python3 -B PhoneSaber/ios/PhoneSaberSenderTests/run_{high_speed,camera_lifecycle,send_gap,debug_transfer}_host_tests.py` を各実行: 2/18/21/7件成功。
- 差分外の既存不良: 同ディレクトリの `run_latency_host_tests.py` は27件成功後、P2PTransportTests.swift:1892/1916/1932のNWError未解決でcompile失敗。Network importがSTANDALONE時に除外される。`git diff main -- PhoneSaber/ios` は空。今回修正対象外。
- Android環境: `JAVA_HOME='/Applications/Android Studio.app/Contents/jbr/Contents/Home' ANDROID_HOME=/Users/satoshi/Library/Android/sdk`（以下2検証で指定）。
- `PhoneSaber/android/gradlew -p PhoneSaber/android --no-daemon --rerun-tasks -Pkotlin.compiler.execution.strategy=in-process :app:testDebugUnitTest :app:assembleDebug :app:assembleRelease :app:assembleDebugAndroidTest :app:lintDebug`: 138 tasks実行、95 tests失敗0、APK/lint成功。
- `python3 -B -m unittest discover -s PhoneSaber/android`: 3件成功（JNI/JVM87件・fixture APK/lint・decoder benchmark）。端末なしの `android_ready.py` は期待どおりexit 1。
- iOS Release: `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /tmp/android-rig-review-ios-release CODE_SIGNING_ALLOWED=NO build`: exit 65。sandbox_apply拒否でSwiftUI macro起動失敗。iOS差分なし。
- 未実行: 公式verify、Unity EditMode/PlayMode・Player画面、iOS target XCTest、Android connectedDebugAndroidTest、実機カメラ/Wi-Fi/保存設定/発熱/光学遅延、実Windows。host成功を実機性能の証拠にしない。
- operator最小: 制約外で `PATH=/Users/satoshi/.pyenv/versions/3.12.1/bin:$PATH bash PhoneSaber/tools/verify_phone_saber.sh`（Unity EditMode/PlayMode、iOS XCTest/Release、上記E2Eを含む）。追加Unityテストクラスなし。
- Android実機: USB承認・カメラ許可・同一Wi-Fi・赤青の剣で `ANDROID_HOME=/Users/satoshi/Library/Android/sdk python3 -B PhoneSaber/tools/android_ready.py` をPlayer停止中/起動中に各1回。
- 実機比較: 同じ環境で `python3 -B PhoneSaber/tools/latency_matrix.py --phone android --only baseline fps30 --blocks 1`。両条件成功、60/30要求、通常再起動で保存設定復帰を確認。選択Androidのカメラ拒否＋別iPhone送信では必ず非0終了すること。
- 実機JNI: 上記JBR/SDKで `PhoneSaber/android/gradlew -p PhoneSaber/android --no-daemon -Pkotlin.compiler.execution.strategy=in-process :app:connectedDebugAndroidTest`。
- `git diff --check` と `git diff main...HEAD --check` 成功。
- ログ: `/tmp/android-rig-review-*.log`。変更はtoolsの2欠陥・回帰テスト・この短いメモのみ。

## ユーザー待ち

**10-13 両色同時停止:** オペレーターのレビュー・公式verify・iOS target XCTest待ち。次の実機停止後にDebug Recordingを終了し、Macへ届いた`connectivity.log`とUnity UTCを照合する。[原因一覧・検証](ios-send-gaps-20261013.md)。commit / pushは今回行わない。

**10-13 夕方 追加（遅延。本番の表示はプロジェクターとユーザーが確認）:**
- **プロジェクターにつないだ状態で F9 を1回**（スマホをスクリーンに向けて約1分）。Mac の内蔵画面では往復 126〜128 ms で、うち表示が約60 ms だった。本番の値はプロジェクターで決まるので、内蔵画面を詰める作業はしない。プロジェクターの映像モード（ゲーム / 低遅延）・台形補正 OFF・有線 HDMI を先に確認する（[runbook](EVENT_DAY_RUNBOOK.md) の「プロジェクターの遅延」）。
- iPhone を冷まして 60 fps に戻った状態での F9（30 fps では表示とカメラの誤差を切り分けられない）。テザリング ON のまま充電しながら送信すると発熱 serious で 30 fps に落ちる（10-13 16:20〜）。
- 本編で F8「剣の遅延補正」を 40〜60 ms にしたときの感触（行き過ぎが気になるか）。

**10-13 追加（ビルド済みアプリを作り直したあと）:**
- **「もっさり・不快」の確認**: 低電力モード OFF・電源接続で起動し、本編で F8 →「画面同期」を ON にして、ゲームの設定のまま（本編は vsync なしで約900 fps）と見え方を比べる。数字の入力は不要（`event=perf` に `vsync=`・`fps=`・座標の古さが残る）。ON のほうが良ければ、ゲーム側の `UnlockFrameRate` を担当者に直してもらうのが本筋。
- iPhone / Android の「低遅延カメラ設定（試験）」と 120 fps は既定 OFF。実機で比べるまで本番では使わない。

iPhone遅延: 10-13 に main へ取り込み済み（公式 verify PASS）。既定の動作は 60fps のまま。120fps と「低遅延カメラ設定（試験）」は既定 OFF で、実機での比較待ち（iPhone 13 で 60/120 fps と試験 OFF/ON を各60秒。数値入力不要、`Library/Logs/connectivity.log` の `CAPTURE_METRICS` を読む）。[手順と検証](ios-latency-20261013.md)。

プロトコル監査（cx/protocol-20261011）はオペレーターの差分レビュー・公式verify待ち。commit / push はしていない。経路受理・探索validation・警告表示の変更案は[契約報告](protocol-contracts-20261011.md)に記録し、実装していない。

2026-10-11時点。最新版を実機に入れて確認する。手順は[当日runbook](EVENT_DAY_RUNBOOK.md)。

**次に実機を触れるとき（2026-10-12 時点。iPhone は Xcode で入れ直す。Mac のゲームは Desktop の「ゲーム起動（台A）.command」）:**
- **振りだけ録画を2本（最優先）**: 本番の場所（壁画が映る画角）で露出 1/100 秒、Debug Recording の「振りだけ録画を開始」。約51秒で自動終了し、下に「Mac 受信済み」と出れば到着。10-12 に採用した **core-line 過連結 guard の実機再確認**（剣の端が遠くへ飛ばないこと、速く振っても剣を見失わないこと）と、壁画の青 FP 対策の判断に使う。
- **Wi-Fi 切替の復帰**: 送信中に Mac の Wi-Fi を別のネットワークへ切り替え、何秒で Unity に座標が戻るか（10-10 は約100秒）。iPhone に「PC（Unity）から応答がありません」が出ること、「通信ログ」に経路の切替が残ることも見る。
- **起動キット**: F8 で P2P bridge ON、強制終了で5秒後に再起動、Quit では再起動しない。固まり検知（45秒）は実 Player で未確認。
- **F9**: 画面に「比較用の記録 n/8 ブロック」が出る。8 ブロック（約2分）まで、iPhone の「fps自動比較」ON でカメラを画面に向けたまま待つ（5分で自動 OFF）。
- Windows・Android（AQUOS）は実機が手元に来たとき: Windows の bat（二重起動防止・固まり検知は Windows で未実行）、Android の「PC 応答なし」注意と撮影→送信の表示。

0. **最適化の実機確認（最優先・数字の入力不要）**: Mac `git pull`→Unity Play、iPhone を Xcode で入れ直し（60fps 既定・LAN 優先・送信経路と認識の高速化・UI 12Hz）。iPhone と Mac を同じ Wi-Fi にして Debug Performance の「fps自動比較」ON → F9 を数分→F9。AQUOS は Mac に USB 接続すれば Claude が入れて 60fps 維持・JNI 中央値を確認。剣の遅延補正は F8 で 20ms を試し、振り心地を一言もらう。

0b. **Mac 起動キットの確認（本番はビルド済みアプリ。10-10 決定）**: Unity の Play を止めて `PhoneSaber/mac/Start-Saber-A.command` をダブルクリック → F8 で P2P bridge が ON、強制終了（⌘⌥Esc）で5秒後に再起動、ゲーム内 Quit では再起動しない。

1. **剣あり・iPhone＋Macのガイド付き録画v3**: 露出1/100秒で1本。最初の「saberなし」の画像、赤・青の静止／速振り／先端向けを残す。遠い・淡い青、画面端も確認。会場に近い距離（画面から2.7〜3.1m、範囲直径1.5m）で、背後の人・投影中・頭上の振りを含める。
2. **AndroidをAQUOS sense9で残りを確認**: 2026-10-07にWindowsホットスポット経由で両色の送信・F8受信・30fpsを確認済み。残りは台A/B探索、向き・四隅、反転保存、開始／停止／前面復帰／再起動／Wi-Fi切替。
3. **Windows実行の残り**: 10-07にEditor Playで受信を確認（ホットスポットはPublic扱いのため全プロファイルでUDP 5005-5007を許可、UnityのBlock規則を無効化）。残りはビルド版＋bat監視、A/B探索、F7、異常終了後再起動／正常Quit。
4. ~~Unity EditMode Test Runner~~: 2026-10-09 に Claude がプロジェクト複製の batchmode で実行し 1678/1678 PASS（e512a67）。Editor 内での再実行は不要。
5. **30分〜1時間の連続運転**: 本番の箱・給電・配置で送信し、発熱・電池・処理／送信／受信fps・処理中央値・到着間隔・メモリを記録。途中で前面復帰・Wi-Fi切替・アプリ再起動を試し、停止後は前面復帰だけでは再開しないことも確認。遅延計測は確認後OFFへ。
6. **剣なしの Debug Recording を2本**(剣がなくてもできる): 露出1/100秒、区間ラベル「saberなし」で開始、各1〜2分、Capture Lossless を2〜3回。① 窓や空が映る明るい場所(青のルールの確認)、② 人の肌・服が前を動く場所(赤の残りの誤検出)。

## 現在の仮説と確度

- **両色停止（10-13）:** 経路通知でLANを捨てる処理とP2P送信失敗時の退避漏れは回帰で確定。実セッションの主因はphone側ログなしで未確定。カメラ・処理・経路の0.5秒超停止をログに残し、任意1ファイルで録画bundleへ運ぶ。

- **確認済み（プロトコル監査）**: P2Pの別端末がbridgeの同じloopback送信元へ集約され、UnityのIP gateで競合を検出できない。LAN/P2P往復の誤ロックとiOS探索応答の互換性未検証も残る。ゲームの受理ルール変更は提案のみ。計測prefix `timestamp=` の受理・時刻統計の不一致だけ修正（[契約報告](protocol-contracts-20261011.md)）。
- **採用（10-12）**: 実剣の上での端点の飛び = core-line の過連結（剣と遠くの明点を1本につなぐ）。guard G(.6) を既定 ON で採用した。根拠は独立4セッション（09-22・09-23・10-10×2）の19変更で 14 件が剣の上に戻り、悪化 0（4 件は位置は剣の上だが向きが違う、1 件は判定不能）、formal 40/40・選択変更 0、C++/Swift bit 一致。速振りでの無害性は実機未確認（ユーザー待ち）。`PHONESABER_CORELINE_GUARD=0` で比較用に切れる。本番の背景には青緑の壁画が一部映り、消灯時の BLUE FP になる（対策は未採用）。
- **高**: 肌・照明・背景の誤検出と、速振りのブレによる候補消失が主因。旧CASE A代表例は背景同士で、実剣の選択修正gateは未達。
- **高**: AWDLの詰まりは認識とは別の遅延原因。interactiveVoiceの実機効果は未確認。F8で間隔・同期時の片道時計差を比較できるが、画面全体の遅延ではない。
- **確認済み**: score決定性は `bbc1f32`、赤warmNoDeepRedは `8363024`。10-06の実機v2では最初の剣なし赤FPが598/598→0/598。ただし別の背景FP・小さい／淡い剣の保持は追加確認が必要。
- **採用済み**: 青blueNoDeepSupportは `fa5db16`。formal 40/40、既存正解187/187保持、剣なし青FP 29→18（offline）。遠い・淡い・ぶれた青の実機確認は未了。Wは淡いピンク剣を失うため不採用、fixture 89は窓期待値のまま。
- **実装済み・実機待ち**: 台指定、F8、両OSキット・監視・イベントログ、端末状態、自動復帰、反転、遅延確認、座標再生、F7位置補正。Mac＋iPhoneの台指定は確認済み。Android JNIはAPI 37 ARM64エミュレータで正式35 PNG／40期待値を確認（35/35 PASS）。内部浮動小数点のbit一致や実カメラの一致は保証しない。

根拠・残存FP・却下案は[FINDINGS](FINDINGS.md)。同文書とAndroid READMEの過去の「未commit／未実行」は、下の採用・検証commitを参照する。

## 次にやること

両色停止の修正をレビューし、公式verifyとiOS実行テスト後、新しいphoneログを付けた同条件のsessionを1本取得する。FPS/露出/route/watchdogの方針変更は提案のみ。[詳細](ios-send-gaps-20261013.md)。

プロトコル監査の差分をレビューし、公式verifyを実行する。iOS Releaseはsandbox/macroエラーで未成功、既存Mono統計benchmarkは変更前から失敗。無線・Player・台切替の未検証項目は[契約報告](protocol-contracts-20261011.md)を参照。

0. **誤検出ルールの採用判断（実機 capture 待ち）**: benchmark（recognition_benchmark.py）の分析で、赤は high_value_ratio ≥ 0.65 で剣なし FP 56→29（剣 29/29 保持）、青は color_purity ≥ 0.25 で FP 9→3（剣 31/31 保持、unknown 変化 1/346）。ラベル付きの剣は静止・近距離中心なので、ガイド付き録画 v3 の速振り・遠距離・淡い青に手ラベルを付け、剣を落とさないことと formal 40/40 を確認してから threshold を決める（docs/claude/red-fp-features.md）。

優先順。実装済み機能は実機で確認し、不具合が出た箇所を直す。

1. **本番の端末構成を通す**: 上のAQUOS／Windows／Test Runner結果を確認。F8・反転・F7・A/B保存、1920×1080の端点・最新入力・未検出時、開始／停止／復帰を実機とPlayModeで確認する。
2. **ガイドv3を解析**: ORIGINALを目視し、赤・青ルールの背景FPと小さい／淡い剣の保持を比較。自動露出／1/100秒、距離0.5〜3m・端からの向き・昼夜の速振りを比較する。既定露出変更はユーザー判断。
3. **本番の遅延・切断復帰を比較**: F8のp95／最大間隔、同期時の片道差、P2P RTT、画面全体の遅延を別々に測る。会場Wi-Fi・ネット使用中・2台Mac・LAN退避を同条件で比較し、許容値を決める。
4. **連続運転と復旧を確認**: 発熱・fps・電池・保存量・memoryHeadroom、低電池・カメラ中断・再接続・監視再起動を評価。録画256MiB上限の余裕を別端末でも確認する。
5. **残存背景FPとfixture 89**: 別人・別場所の点灯／OFF対、halo・点LED列を比較。窓と淡い剣の共存証拠、画像外1pxの端点を再審査し、認識と回帰を直す。期待値だけ変えると39/40。Wは再採用しない。静的マスクは固定背景だけの案で、静止剣の保持証拠とユーザー判断が必要。
6. **CASE A/B/Cを分けて調査**: bundleコピーでtriage dry-run／selection replayを行い、ORIGINALと全eligibleを照合。Aは別swingで実剣が僅差で負ける2event以上のgate後に修正。Bが多ければ生成前の棄却componentを記録。Cの端点修正はAと別にし、長い剣・分離LEDを保護。200ms以下の補間／外挿も誤出力の延長を比べ、ユーザー判断後に変更する。
7. **Mac計測画面を実操作**: 開始／停止／再開、CSV保存・結果フォルダ・失敗統計・実UDP・同一時計の表示→受信計測・Unityとのポート競合を確認する。
8. **診断受信と旧bundleを確認**: `/health`、安全な再起動、転送失敗後の再送、report／overviewを通す。234740／155919は手動再解析候補。旧010049の32KB超過は上限を維持する。
9. **会場でrunbookを通す**: 署名・権限、固定配置、背後の幕、投影・照明、箱の熱・電源、予備経路、終了、[2台目Mac](SECOND_MAC_SETUP.md)を確認。床の範囲表示、回転率・タイトル復帰・判定タイミングも確認。本番構成はMac＋iPhone／Windows＋Androidが候補で未確定。Mac はビルド済み `3D-Saber.app` を起動キット（Start-Saber-A/B.command）で回す（10-10のユーザー判断。Editor Play ではない）。B台の一時停止キーは追加しない（10-06のユーザー判断）。
10. **解析費用とログ保持を整理**: high既定／必要時maxの費用・精度を比較し、needs_captureへの長時間解析を減らす。inbox／実ログの保持期間・削除対象はユーザー判断。テストログ隔離と失敗記録を保つ。
11. **将来の構成を比較**: AWDLが不安定ならUSB/usbmuxd/TCPを試すか判断し、座標互換・滞留・切断復帰・画面全体の遅延を測る。Mac内蔵／USBカメラ直接認識は精度・端点・配置・遅延を比較して採否を決める。Continuity Cameraは別の無線経路で、縮小／FPSだけを通信遅延改善と扱わない。

## 作業ログ

### 2026-10-13 — 中断WIPから両色同時停止の調査を完了（未commit）

`755ba38`から継続。既存の接続保持・P2P退避・connectivity.log同梱を維持し、P2P接続通知だけでのLAN pending破棄と停止診断の取りこぼしを修正。修正前失敗／修正後PASSをhost XCTestとPythonで確認。ReleaseとXCTest build成功。実機・公式verifyは未実行。実行コマンド・結果・リスクは[調査報告](ios-send-gaps-20261013.md)。認識・座標形式・corpus・Unityは無変更。

### 2026-10-14 午前 直列の Codex ジョブを1件ずつ取り込み（Claude）
- 先読み: `PredictAdaptive`（F8「自動」のときだけ）。実データ（30 fps、遅れ 112〜118 ms）で表示のずれ RMS は 0.5 / 1 Hz で OFF 15.6 / 26.2 → 現行60 ms 12.2 / 20.5 → adaptive 60 ms 11.1 / 19.6（% of travel）。効果は控えめで、予測器の上限 60 ms が頭打ち。60 fps の録りはまだ無い。
- Player: 起動確認が Title → SongSelect → Game → Result → 判定調整 → Title を一周して実カットまで見る。`unity_copy.sh soak [分]`（連続プレイ）: 6 分・8 周は例外 0・Error 0、漏れ判定は 11 周以上で出る（未実施）。
- 小さな修正 7 件（iPhone のカメラ通知の世代・P2P 復帰時の古い座標・録画 outbox、Unity の受信再起動時の古い座標、Android の UDP channel の close、host check、receiver launcher）。各々回帰テストつき。
- 検証: 公式 verify PASS、Tools 全件、gradle、Unity EditMode PhoneSaber 系 1271 pass。`FiveSongPlayabilityTests` 15 件は main 単体でも失敗（10-13 に追加された5曲、担当者のもの）。
- Mac のアプリと Android の APK は 4938069 でビルド済み。**iPhone は端末に届かず未更新**（ロック中か圏外。入っているのは 10-14 00:10 頃の版）。
- runner9 は直列でバグ探しを継続中（bughunt7〜）。

### 2026-10-14 未明 無人の遅延計測（iPhone 13 を Mac の画面に向けて固定、家の LAN、Claude）
- 道具: `PhoneSaber/tools/latency_matrix.py`（iPhone を起動引数つきで再起動 → Player を `-phonesaberLatencyProbe` で起動、を条件ごとに繰り返して表にする）、Player の `-phonesaberTrackingProbe <秒>`（赤い棒を既知の軌道 0.5〜2.5 Hz と静止で動かし、戻ってきた座標と一緒に CSV 保存）、`PhoneSaber/tools/tracking_report.py`（見た目の遅れ＝相互相関、先読み方式ごとの表示のずれ）。F9 の判定は棒の位置を2次元の距離で見るようにした（スマホを横向き・斜めに置くと左右の動きがカメラの上下に出て1回も測れなかった）。
- **F9（30 fps、発熱 serious）**: 往復 117〜137 ms（run 間の揺れ ±6 ms 程度）。「低遅延カメラ設定（試験）」ON はスマホ自身の計測値が 48→41 ms に下がるが往復は変わらない（117〜122 対 119〜122）＝実際の遅れは減っていない。ウィンドウ表示・解像度変更は、スマホから見える棒の位置が変わって測れない（設定も次の起動に残るので条件から外した）。画面同期・maxQueuedFrames も差なし（10-13）。**設定で削れるものは見つからなかった。**
- **追従（30 fps、発熱 serious、70 秒）**: 見た目の遅れ 112 ms（相関 0.997、0.5〜2.5 Hz で 108〜114 ms とほぼ一定）。静止時の揺れは移動幅の 2.1%。受信は 26 件/秒で、100 ms 超の途切れが 70 秒に 52 回（速い動きで検出が抜ける）。
- **先読みの実力（同じデータ、表示のずれ＝移動幅に対する RMS %）**: 先読みなし 0.5 Hz 15.6 / 1 Hz 26.2 / 1.5 Hz 42.0 / 2 Hz 52.5。直近3点の直線で 80 ms 先読みすると 0.5 Hz 10.6 / 1 Hz 15.6（約3〜4割減）だが、1.5 Hz 以上は良くならず 2 Hz 以上は悪化、静止時の揺れは 2.6→2.8%。本番の予測器（保守的・上限 60 ms）は 1 Hz で 18.0。30 fps では標本の間隔が広く雑音が増幅されるので、先読みの伸びしろは 60 fps より小さい。
- **iPhone の発熱**: 電池 45% から急速充電しながら 60 fps で送ると、約2分で nominal → serious になり 30 fps に落ちる（00:18→00:20）。30 fps ではスマホの処理が約 33→48 ms、露光待ちが 8→17 ms、先読みも効きにくい。本番は「満充電にしてから使う・急速充電器を使わない・箱の通風」が効く。
- 60 fps（冷えた状態）の追従データはまだ無い。予測器の改善（このデータで設計・評価）と 60 fps の計測は Codex に渡す。

### 2026-10-14 Codex 2巡目（10-13 22:15〜23:00）の取り込みと、無人の遅延計測の準備（Claude）
- 取り込み: **lat2**（[latency-compensation-20261013.md](latency-compensation-20261013.md)。F8「剣の遅延補正: 自動」が実測の合計から色ごとに先読み量を決める。5 ms 超の変化だけ・10 秒に1回まで・欠測は15 秒以内に OFF、上限は予測器の最大値。F9 は maxQueuedFrames を切り替えなくなり、ブロックごとに「画面の遅延」を分解して `event=display-measured` に残し、同じ画面条件なら推定に使う）、**iosfix + review2**（[ios-crash-fixes-20261013.md](ios-crash-fixes-20261013.md)、[review-merged-20261013.md](review-merged-20261013.md)。カメラ通知を main 待ちにしない＝停止との相互待ちの解消、overlay writer が失敗状態のとき録画終了が進まない件、発熱による再起動と録画開始の状態不整合、heartbeat が 0 byte のまま固まり判定に入る件（Mac / Windows の watchdog）、アプリ・PDF の差し替えが途中で失敗しても旧版を残す、古い検証結果を再利用しない）、**robust**（[robustness-20261013.md](robustness-20261013.md)。P2P bridge の診断 relay が完了した接続を保持し続ける件、受信側の manifest・深い JSON・壊れたログでの異常終了、再実行用スクリプト `PhoneSaber/tools/robustness/`）。
- **青の背景ルール**は文書だけ取り込み（[blue-background-rule-prototype.md](blue-background-rule-prototype.md)）。ON にすると画面端の実青剣を1枚失い static suite も落ちるので、実装は `cx/mural-20261013` に残し、認識コードには入れていない。
- 無人計測の準備: Player に `-phonesaberLatencyProbe [ブロック数]`（キー操作なしで F9 を始めて終わったら終了、スマホが画面を向いていなければ 40 秒であきらめる）。iPhone アプリは起動引数 `-PhoneSaber.experiment.lowLatencyCamera YES` のときだけ低遅延カメラ設定（試験）を ON で始める（保存しない）。`-cameraFPS 30|60`・`-PhoneSaber.autoStartSending YES` は既存の UserDefaults の鍵なので引数で上書きできる。これで Mac から iPhone を再起動しながら条件を変えて F9 を回せる。
- 検証: 公式 verify（iOS XCTest・Detection・Lossless 40/40・iOS Release）PASS、Tools 全件 PASS、Unity EditMode `^(PhoneSaber|InputPoint|SaberInput)` 1241/1241。全体の EditMode では `ZeroHardRemixTests` の3件が落ちるが、main 単体でも落ちる（譜面の作業中、担当者のもの）。

### 2026-10-13 AQUOS sense9 の F9（Mac のビルド済みアプリ、iPhone のテザリング経由、16:44〜16:46）と、内訳の訂正
- F9 の往復: 中央値 114 ms（ブロック 111 / 109 / 114、55 サンプル、読み違い 0）、p95 217 ms、最大 308 ms。AQUOS は 30 fps（60fps のスイッチ OFF）。
- AQUOS 自身の計測: 撮影→OS 送信 87 ms（p95 95）、認識 6〜9 ms、通信（RTT÷2）4〜6 ms で時々 18〜23 ms。PC 受信→描画直前 1.5〜1.8 ms。
- **訂正**: iPhone の結果から「Mac の表示が約60 ms」と書いたが、確定ではない。同じ Mac の画面で、iPhone は「往復 128 − スマホ 50 = 78 ms が表示＋露光待ち」、AQUOS は「往復 114 − スマホ 87 − 通信 5 = 22 ms が表示＋露光待ち」となり、約55 ms 食い違う。両機種の「撮影時刻」の定義（露光のどの時点か、どの時計か）が違うためで、スマホの項は機種間で比べられず、表示との切り分けもできていない。確かなのは往復の合計（iPhone 30 fps: 126〜128 ms、AQUOS 30 fps: 114 ms）だけ。
- 切り分けには、時刻の定義に頼らないカメラ遅延の実測が要る（例: スマホ自身のライトを点けた時刻と、明るさの変化を検出した時刻の差）。Codex の再開後に検討する。
- AQUOS は往復の裾が長い（p95 が中央値の約2〜3倍）。テザリングの Wi-Fi 経由の揺れか、カメラ側かは未切り分け。本番は Windows のホットスポット経由。
- 次の計測: AQUOS の 60fps を ON にして F9。

### 2026-10-13 実機の遅延の内訳（iPhone 13 + Mac のビルド済みアプリ、ユーザーが F9 を実施 16:19〜16:21）
- F9 の往復（棒を切り替えた Update → その位置の座標を受信）: 中央値 126 ms（99〜130、60 サンプル、取りこぼし 0）。曲選択の画面（vsync=1、60 fps、FullScreenWindow、Metal）で実施。
- 同じ時間帯の自動計測: スマホ 撮影→認識完了 46 ms（**発熱 serious で 30 fps に落ちていた**。60 fps・発熱 fair のときは 33〜34 ms）、通信 0.6 ms（USB 直結）、PC 受信→描画直前 5 ms（p95 8〜16 ms）。
- 引き算: 126 −（スマホ 46 ＋ 通信 0.6 ＋ カメラの露光待ち 平均 約17 ms（30 fps の半周期、推定））≒ **約63 ms が「Update → 画面が光るまで」**。自動計測の「画面 約8 ms（半周期）」は大幅な過小評価だった。誤差はカメラの待ちの仮定で ±17 ms 程度。maxQueuedFrames 1/2 の交互ブロックに差は無い（115/126/127/125 ms）。
- つまりこのときの体感遅延は約126 ms で、内訳は 画面 約63 ＞ スマホ 46 ＞ 露光待ち 17 ＞ PC 5 ＞ 通信 1。60 fps に戻れば スマホ 33・露光待ち 8 で約105 ms の見込み。本編は vsync=0 なので画面の項が下がる可能性があるが未計測（本編中の F9 を依頼中）。
- iPhone の通信ログには `[PATH]` の通知が数秒〜30秒おきに出ている（テザリング ON の間、cellular）。修正前のアプリはこの通知のたびに LAN の宛先を捨てていたので、「両色が同時に1秒途切れる」原因だった可能性が高い（修正版は 16:10 に実機へ入れ直し済み）。
- 曲選択での約1.2秒の固まり（frame-ms-max 1180）は F9 中にも1回記録（16:21:07）。

### 2026-10-13 iPhone: 送信が途切れる経路の修正と、途切れの原因記録（Codex 実装、Claude がレビュー・検証）
- 10-13 のプレイで両色が同時に1秒以上途切れた件の調査（[ios-send-gaps-20261013.md](ios-send-gaps-20261013.md)）。当時の iPhone 側の記録が無いので原因は確定していないが、途切れを作る経路を4つ直した: (1) ネットワークの状態変化の通知（NWPathMonitor）のたびに LAN の宛先と接続を捨てて探し直していた → 既知の宛先を保ち、接続の回復だけ行う。(2) 同じ宛先の再設定で接続と待機中の座標を捨てていた。(3) P2P の送信直前の失効・送信エラーで LAN へ退避しなかった。(4) P2P がつながった通知だけで LAN の待機座標を捨てていた。
- `SEND_GAP`: 送信が 0.5 秒以上止まると、原因（カメラからフレームが来ない / 認識が止まった / 両色とも検出なし / 送信エラー / 経路なし）と各段の経過秒を通信ログに1行残す。Debug Recording のバンドルに `connectivity.log`（直近 64 KiB）を同梱する。
- 検証: 公式 verify（iOS XCTest・Detection・Lossless 40/40・iOS Release）PASS、Tools 全件 PASS（host 用の代役に ConnectivityLog を足した）。実機は 10-13 16 時台に入れ直して確認中。

### 2026-10-13 遅延の自動計測（ユーザー依頼「実機の遅延計測をもっと簡単に」「最優先」。Codex 実装、Claude がレビュー・検証）
- 手順なしの常時計測にした。スマホが送信中5秒ごとに診断 `PHONESABER_METRICS 1 …`（撮影→送信の中央値/p95、探索の往復時間 RTT、fps、発熱、電池）を PC の 5007 番へ送り、PC が自分の計測（受信→描画直前）と画面の半周期を足して、F8 に色別の「遅延の目安」を1行、events.log に `event=latency-estimate` を10秒ごとに残す。座標の payload と既存の探索メッセージは変えていない。古い PC は診断を無視し、古いスマホは送らない（PC は「届いていません」と表示）。P2P 経由は対象外。
- 内訳: スマホ（実測。iOS は撮影→認識完了、Android は撮影→OS 送信）＋ 通信（RTT÷2、推定）＋ PC（実測）＋ 画面（設定 Hz の半周期、推定。プロジェクターの処理・GPU の待ちは含まない）。詳細と限界は [auto-latency-20261013.md](auto-latency-20261013.md)。1日分の集計は `latency_report.py`。
- 検証: 公式 verify PASS、Unity EditMode 2018/2018、Android 76 tests・APK・lint、`check_auto_latency.py`（Swift の実 socket → C# の受信 Loop）。ビルド済みアプリでは `PhoneSaber/tools/auto_latency_player_check.py` で、この Mac の LAN アドレスからスマホ役のデータを流して確認する。結果（ビルド 53a5dfa、窓なし起動）: 8行すべて `status=estimate`、赤・青とも total 56.9 ms = スマホ 36.9（流した値）＋ 通信 1.6 ＋ PC 10.1（実測、7048 フレーム）＋ 画面 8.3（60 Hz）。**実機（iPhone / AQUOS）は未実施**。iPhone は Xcode で入れ直し、Android は APK の入れ直しが必要。

### 2026-10-13 「アプリにすると重い」（ユーザー報告 2回目、ログで確認）
- 12:46〜12:52 JST のプレイ（台A、新しいビルド）の `event=perf`: `low-power-at-launch=1`、`refresh-hz=30`。SongSelect は 29.5 fps（全フレームが 25 ms 超）、Game は vsync=0 で 900→640 fps（5分でじわじわ低下）だが画面は 30 Hz。原因は前回と同じ **Mac の低電力モード（バッテリー駆動）**。座標は届いていない（スマホなしのプレイ）。
- 起動スクリプトの注意は端末に出るだけで、ゲームが全画面になると見えなかった。対策: (1) 画面の更新が毎秒50回未満のとき、メニュー画面（本編以外）の上端に注意を出す（`PhoneSaberDisplayNotice`、F8 を開かなくても見える。本編では出さない）。(2) 起動スクリプトは最初の起動で低電力モードなら10秒待ってから起動する（Enter ですぐ起動。無人の再起動では待たない。`PHONESABER_LOW_POWER_WAIT=0` で無効）。
- 見た目は `PhoneSaber/tools/unity_copy.sh shots` で確認済み（ビルド済みアプリを隠して起動し、各 scene の画面を `_out/shots/*.png` に保存。`PHONESABER_SHOTS_REFRESH_HZ=30` で注意の表示を再現）: タイトル上端に黄色の注意が出て、本編には出ない。Player は scene ごとに自分で前面へ出ようとするので、出たら0.5秒以内に隠し直している（一瞬見える）。
- 低電力モードの解除はユーザーの操作（システム設定 > バッテリー > 低電力モード →「しない」。Claude はシステム設定を変えない）。解除・電源接続のうえで、F8「画面同期」ON との比較が残っている。

### 2026-10-13 Codex 6並列の1巡目を取り込み（Claude がレビュー・検証）
- ユーザー指示「Codex のリセットが頻繁に来るのでガンガン使ってよい。ゲームの完成度を高く」。runner を3本にして6並列＋待ち行列にした（`~/Library/Logs/PhoneSaber/codex-runner/`、`runner.log`）。5時間枠は 10:58 に使い切り、リセット後に自動再開（iosgaps・mural・docs2・review2 は途中）。
- **PC の予測**（[predict-20261013.md](predict-20261013.md)）: 録画の座標列では予測の効果がはっきりしない（40 ms で誤差の中央値 0.203→0.157、RMS は 0.6% 減、静止時の揺れは 9% 増）ので既定は OFF のまま。起動・欠測復帰の直後に1区間だけで外挿して剣が跳ぶ不具合を修正（3点そろうまで予測しない）。F8 に「自動」（NTP 同期確認済みのときだけ片道遅延を使う）を追加。
- **PC の表示遅延**（[pc-latency-20261013.md](pc-latency-20261013.md)）: `event=perf` がシーン・描画条件の切替で窓を切り直すようになり（以前はメニューの fps を Game 名で報告することがあった）、座標の古さ（描画直前、中央値/p95）・受信→初回使用・何が fps を制限しているか（vsync / target / 低電力の疑い / GPU の疑い / 不明）を記録。F8 に同じ判定を1行表示。`InputPoint.ResampleLatestInput()` を追加（まだ誰も呼ばない）。Mac / Windows の起動スクリプトが電源・画面の Hz を毎回ログに残す。
- **F8「画面同期」**（Claude 追加、`PhoneSaberDisplaySync.cs`）: 本編は `UnlockFrameRate` が vsync を切り 60 Hz の画面へ約900 fps で描いている。破れ・コマ揺れと比べられるよう、台ごとに vsync=1 を明示選択できる。既定は「ゲームの設定のまま」で何も変えない。
- **クラッシュ監査（報告のみ）**: [iPhone](ios-crash-audit-20261013.md) は P1 が1件（カメラの通知を main で同期配信しているため、停止の同期待ちと相互待ちになりうる。実機では未再現）、P2 が1件（overlay writer が失敗状態のとき録画終了が進まない）。[Android](android-crash-audit-20261013.md) は P0/P1 なし、P2 が1件（保存値の型が違うと起動時に ClassCastException。現行の書き込みでは起きない）。修正の適用は runner の待ち行列（iosfix / androidfix）。
- `unity_copy.sh` は Unity とビルド済みアプリを `open -g -j` で隠して起動するようにした（ユーザー指摘: 検証の窓が作業中の画面に出る）。終了コードが取れないので成否はログで判定。
- 検証: 公式 verify PASS（iOS XCTest・Detection・Lossless 40/40・Tools・iOS Release）、Unity EditMode 2011/2011、gradle unit/assemble/lint、core parity 325/325。実機は未実施。

### 2026-10-13 Android capture→send の仕上げ（Codex 実装、Claude がレビュー・検証して取り込み）
- 送信中10秒ごとに `PhoneSaberLatency` タグで1行（撮影fps・解析fps・赤青の送信fps・撮影→送信 median/p95・未解析推定・送信破棄・発熱・PC 応答からの秒数・Camera2 の実設定）。画面の「ログをコピー」で直近5分を取れる。数値の手入力は不要。
- 撮影→送信は「センサー時刻（REALTIME の端末だけ）→ OS への送信完了」で測るように変更。解析スレッドの優先度を1段上げ、送信中は Wi-Fi の low-latency と high-perf の両 lock を持つ。
- 「低遅延カメラ設定（試験）」（ノイズ低減・輪郭強調 OFF）は既定 OFF・保存しない。画素が変わるので AQUOS での比較が先。
- 検証: gradle unit / assembleDebug / lintDebug 成功、`make -C PhoneSaber/android/core test parity` で 325 PNG 差分 0・formal 40/40。AQUOS と Windows の実機は未実施。[結果と実機手順](android-latency-20261011.md)。

### 2026-10-13 iPhone capture→send の仕上げ（Codex 実装、Claude がレビュー・verify して取り込み）
- 60/30の通常AVCapture setter履歴をmainと一致させ、HDR/GDC/広色域変更は既定OFFの試験トグルへ分離。120 opt-inと最新1枚mailboxを維持。
- Releaseで撮影時刻を取得し、10秒ごとの経路・fps・遅延median/p95・発熱ログを追加。健康統計を現在の送信より後へ移し、集計ロックを短縮。120再発熱時の30fps退避、LAN fallback後の経路ログも修正。
- 修正前FAIL／修正後PASSのhost回帰、formal 40/40、静的検出、latency_report 15/15、iOS Release build・Debug XCTest build PASS。取り込み時に公式 verify（Simulator の全 XCTest を含む）PASS。実機は未実施。10秒ごとの計測行は画面の通信ログに出さずファイルだけに書くよう Claude が変更（経路の出来事を押し出さないため。ファイル上限は 1 MiB）。詳しいコマンド・人工負荷bench・未実装案は[ノート](ios-latency-20261013.md)。認識・UDP本文・Unity設定は未変更。

### 2026-10-13 ビルド済みアプリが「もっさり」する件（ユーザー報告、原因を実測）
- Player に `event=perf`（10秒ごとに fps・最大フレーム時間・25ms/50ms 超のコマ数・赤青 pkt/s・画面の更新回数・vsync）を追加して、実画面で測った。**Title / SongSelect は fps=30.0、refresh-hz=30、vsync=1**。本編は vsync=0 で約900fps（描画は上限なし）。
- この Mac は**低電力モード ON・バッテリー駆動**だった（`pmset -g` の lowpowermode 1）。ディスプレイ自体は 60Hz のままだが、Unity の Player が受け取る更新は 30Hz になり、メニューは 30fps、本編も画面に出るのは毎秒30コマ。**低電力モードを切って電源につないだ状態での再計測は未実施**（設定はユーザーが変える）。
- 対応: 起動キットが低電力モード・バッテリー駆動を見つけると注意を表示、F8 に「画面の更新が毎秒 30 回しかありません」、runbook の準備チェックに追記。
- 同じセッションの events.log では、iPhone（192.168.1.8、LAN）からの座標が1秒以上途切れる記録が頻繁にある（剣が画面外・未検出でも出るので、これだけでは通信の問題とは言えない）。perf の pkt/s で次回から区別できる。
- 未対応の観察: 本編は fps 上限なし（約900fps）で GPU を使い切る。ファンのない Mac では長時間で発熱する可能性がある（ゲーム本体側の設定 `UnlockFrameRate`。変更していない）。

### 2026-10-12 夜 ビルド済み Player で剣が出ない・本編が動かない不具合（ユーザー報告、修正済み）
- **原因**: `Shader.Find("Universal Render Pipeline/Unlit")`（予備の `Unlit/Color` も）が、ビルド済み Player では null を返す。どの Material も参照していない shader は build に入らないため。Editor では必ず見つかるので気づけなかった。
  - `SaberInputBridge.EnsureBladeOutline`（投影モード既定 ON の輪郭線）で `new Material(null)` が例外になり、Awake が止まって**剣が出ない**（Player.log に `ArgumentNullException: shader`）。
  - 本編では `GamePlayManager.Update` が**毎フレーム例外**（16秒で 33,131 回。小節線など）。曲選択のプレビュー・`SaberRig` も同じ形。Player では本編が正常に動いていなかった。
- **修正**: Graphics の Always Included Shaders に `Universal Render Pipeline/Unlit` を追加（`ProjectSettings/GraphicsSettings.asset` に1行。Editor の動作は変わらない）。`SaberInputBridge` は shader が無くても落ちない（輪郭線だけ省く）。
- **再発防止**: `PhoneSaber/tools/unity_copy.sh build-mac` が、**差し替える前に Player を窓なしで起動**して Title → SongSelect → Game を開き、例外の数と「自分へ送った座標が届いて剣の部品があること」を確かめる（`PhoneSaberPlayerSmoke.cs`、`-phonesaberSmoke`）。修正前の build では例外 33,131 件で不合格、修正後は 0 件・入力 OK。EditMode に `PhoneSaberBuildShaderTests`（script が名前で探す shader が build に入る見込みか）を追加。
- EditMode（PhoneSaber）1178/1178。Windows の build にも同じ設定が効くが、Windows 実機では未確認。

### 2026-10-12 認識の失敗集計（Codex、offline のみ。認識コードは未変更）
- [recognition-census-20261011.md](recognition-census-20261011.md): 696 ファイル（重複除外 657 画像）を本番 C++ で再認識し、剣ありの出力 RED 86/99・BLUE 82/82、剣なし FP RED 77/153・BLUE 44/178、位置ラベルとの合致 RED 60/77・BLUE 52/59。選抜された異常場面が中心なので当日の発生率ではない。
- 次に実機で確かめる第一候補は**青の背景条件**（限定した mask 条件＋purity。既知 FP 44→33、位置が合っていた 52 出力を保持）。赤の HV≥.65 の一律追加は見送り。赤の見落とし 13 件は候補が全て ineligible（compactRed purity<.50 が 11 件）。
- 露出: 10ms の赤速振り 4/4、20ms 8/9 で位置出力あり。別撮影・別姿勢で、1/100 より短い既定を選ぶ根拠は無い。**短い露出の既定化には比較撮影が1本必要。**
- Codex の無人 runner は 10-12 04:39 に全ジョブを終えて終了した。新しいジョブは入れていない。

### 2026-10-11 夜〜10-12 未明 Codex の点検結果の取り込みと仕上げ（verify 全項目 PASS）
- **認識: core-line 過連結 guard G を採用**（Codex 実装、Claude が変更前後の画像を目視）。C++ / Swift 共通、既定 ON、切替は `PHONESABER_CORELINE_GUARD=0`。parity 325 PNG 不一致 0、formal 40/40、host の追加コスト C++ +0.03ms / Swift +0.02ms。[採用ノート](coreline-guard-prototype.md)。
- **10-10〜11 に入れた変更の再レビュー**（Codex、[review-merged-20261011.md](review-merged-20261011.md)）で直したもの: ConnectivityLog の世代交代が directory を再帰削除する、CDPATH で Mac 起動パスが壊れる、Unity が crash しても PASS XML だけで成功扱いになる、最後の経路切替ログの欠落、など。
- **再レビューで「未修正の P1」とされた点への対応（Claude）**:
  - 送信元ゲートは**既定で「検出して警告するだけ」**にした（`PhoneSaberSenderGate.Enforce = false`）。弾く動作には、1台の LAN⇄P2P 往復を2台と誤判定して約1秒止める、P2P 経由の2台は区別できない、という穴がある。loopback と LAN の入れ替わりは競合に数えない。不正 payload は送信元の判定に使わない。
  - Mac watchdog: 古い lock を2つの監視が同時に回収して二重起動する競合を修正（rename で回収、取得後に自分の PID か再確認、cleanup は自分の lock だけ）。
  - iPhone の LAN 生存確認は版 1・port 5005/5006 の応答だけを受ける（`PHONESABER_UNITY 10` や別 port を生存扱いしていた）。
- **部品間の取り決めの照合**（Codex、[protocol-contracts-20261011.md](protocol-contracts-20261011.md)）: `timestamp=` 接頭辞が統計と P2P で扱われない不一致を修正。座標・探索応答の共通テストベクトル（`tools/fixtures/protocol_*.tsv`）を Swift / Kotlin / C# / Python のテストが読む。
- **手順書と実物の照合**（Codex＋Claude、[ops-doc-audit-20261011.md](ops-doc-audit-20261011.md)）: スタッフマニュアルの復旧表を「症状→一つの対処」に作り直し、runbook の先頭に開場直前チェック表。新しい画面表示（転送状態、手動 IP の解除、競合警告、発熱 30fps、充電警告）を反映。**STAFF_MANUAL.pdf を作り直した**（`tools/build_staff_manual_pdf.py`、2ページ）。
- **検証の落とし穴を2つ修正**: view model のテストが、この Mac で動いている本物の Unity の 5007 応答を拾って落ちる（テスト中は応答しない port を使う）。Codex の sandbox が残した Gradle daemon が別 worktree に書けず build が落ちる（gradle の前に `./gradlew --stop`）。
- **未取り込み（branch に保存。実機で確かめてから）**: `cx/ios2-20261011` の iPhone 遅延短縮（120fps の選択肢、HDR・歪み補正の無効化。カメラ設定が変わるので実機確認が先）、`cx/android-latency-20261011`（撮影→送信の表示修正、解析スレッドの優先度）、認識の失敗集計は 10-12 に完了して取り込み済み（上の項）。
- 検証: verify_phone_saber.sh 全項目 PASS、PhoneSaber/tools 178 tests、Android JVM 58、Unity EditMode（PhoneSaber）1139 中 1137（2件は譜面エディタ）。実機・Windows 実機は未確認。

### 2026-10-11 プロトコル監査のWIP継続（未commit・未push）

- `8114236 wip` とcleanなworktreeから再開。認識・UDP本文生成・formal corpus・scene/prefab/meta/ProjectSettingsは変更なし。Unity統計とphone/bridge共有P2P validatorの既存 `timestamp=` alias対応を維持し、操作者に見えるstatus警告変更はWIP以前へ戻した。
- [契約表と重大度別の報告](protocol-contracts-20261011.md)を完成。port、座標/計測、discovery/station、Bonjour、framing/session/sequence、liveness、起動/終了、ログ、triage envelope/schemaを照合。P2P複数端末のIP集約を実bridgeで再現し、見える受理ルール変更は提案に留めた。
- 再開後PASS: 共通契約＋replay30件、latencyを含む初回14件、実bridge28件＋追加監査1件、Python status/triage/schema/report/encoding94件、UDP probe38件、Android JVM58件・Debug APK build、native core-test・parity tool4件、Mac経路XCTest6件、Mono/NUnit61件。iOS Release再試行はexit65（sandbox_applyとSwiftUI macro plugin）、Mono benchmarkは変更前sourceでもStatistics mismatch。公式verify・formal全件・Unity Editor/Player・無線実機は未実行。詳細コマンドとログは契約報告に記録。

### 2026-10-11 午後 Codex による点検と修正（Claude が確認・検証して取り込み）
- **Codex 無人 runner**: Codex の5時間枠を6並列で約30分で使い切り、6ジョブが途中で止まった（13:31）。Claude なしで続きを回す runner を起動（`~/Library/Logs/PhoneSaber/codex-runner/`、`runner.log`、止めるには `STOP` を作る）。枠が戻ると3レーンで実行し、結果は各作業 branch に自動 commit する。**main には入らない。Claude が差分確認と verify をしてから取り込む。** 18:05 に再開。runner が正常終了を「上限で中断」と誤判定してやり直させる不具合があり 18:42 に修正（出力に写った指示文の語で判定していた）。
  - 優先順（ユーザー指示 10-11: 撮り逃しは減った。それ以外、特にバグを残さない）: A = 録画の再送 → 10-10〜11 に入れた変更のバグ探し（`cx/review-today-20261011`）→ guard G → 認識の集計。B = iPhone 当日対策 → 部品間の取り決めの食い違い探し（`cx/protocol-20261011`）→ iPhone 遅延。C = 運用4点 → 手順書と実物の照合（`cx/ops-20261011` 上）→ Android 遅延。
- **取り込み済み・夜（verify: iOS XCTest / Detection / Lossless 40 / iOS Release / Diff Check PASS、Tools 428＋126 PASS、Unity EditMode 1903 中 1901 PASS・失敗2件は譜面エディタ）**
  - 録画の再送と受信確認（`cx/outbox`、Codex）: 永続 outbox（アプリ再起動後も残る、前面復帰・経路復旧・送信開始で再試行、録画中は送らない）、画面に 転送待ち／転送中／Mac 受信済み／転送失敗 と未転送件数と「今すぐ送る」、同じ内容の再送だけ 200-duplicate で受領扱い（別内容と裸の 409 は失敗のまま）、bundle 確定直後に転送開始（overlay 生成を待たない）。「録画完了」は「端末に録画を保存」に変更。
  - iPhone 当日対策（`cx/ios2`、Codex）: 発熱 serious/critical で実効 30fps・nominal/fair が120秒続いたら復帰（保存設定は不変）、カメラ復旧は15分後も60秒ごとに無期限、非同期停止完了時に前面を再確認、未充電・低電池（20% 非充電／10%）・低電力モードの警告、手動 IP 指定中の表示と解除ボタン、選択中の PC を正しく表示、fps 自動比較は5分で自動 OFF＋警告、DEBUG の callback 履歴を16世代に制限。
  - 運用（`cx/ops`、Codex）: 固まり検知（Player が毎秒 heartbeat、初回確認後45秒止まったら kill して再起動。`PHONESABER_HANG_SECONDS=0` で無効）、Windows 二重起動防止、P2P bridge の自動再起動（2/5/15/60秒）、Mac Status ツールが built Player を認識。
  - Claude が追加: **監視自身が10秒以上止まっていた（PC のスリープ復帰）直後は固まり判定を1周期休む**（復帰直後に正常なゲームを kill する穴）、新規 C# 3ファイルの .meta、host 用 stub（tracking e2e・sender soak）を転送 API の変更に追随。
  - いずれも実機・Windows 実機では未確認。
- **取り込み済み（verify 全項目 PASS）**
  - Mac P2P bridge（903e4ba 相当）: 診断 relay の背圧、応答末尾の欠落、build cache、Bonjour 登録解除のログ。bridge テスト 27/27。10-10 の100秒の空白は Mac 側からも原因を断定できず。
  - Android（ca7c868 相当）: 選択中の PC が 5007 に3秒応答しないと注意表示、lint と fixture APK の同時 build。JVM 56/56。実機未確認。
  - Unity 受信（116049d 相当）: NaN・無限大・桁外れの座標を無効扱い、sender-change は1秒1行、ログ回転失敗で世代を消さない、5007 bind 失敗時の socket 解放。
  - Unity 追加（46254ea 相当）: **送信元ゲート**（同じ port へ2台が交互に送ると先にいた1台に固定、F8 に警告、LAN⇄P2P の単発切替は取りこぼさない）、F7 採取のキャンセル、F9 が maxQueuedFrames を元に戻す。EditMode 1897 中 1895 PASS（失敗2件は譜面エディタのテスト）。
  - 録画→Mac 転送（e0052e8 相当、[詳細](review-debug-bundle-20261011.md)）: **Mac 側の解析契約が振りだけ録画の6枚を拒否していた**のを修正、予約した lossless が bridge と重なる・容量で押し出されるのを修正、manifest と summary の session 不一致・NUL 入りファイル名を拒否。
  - iPhone 送信（[詳細](review-ios-event-day.md)）: ready 通知が送信中に来ると最新の待機座標を捨てる欠陥を修正。
  - `run-static-tests.sh` の DeviceHealth テスト件数の決め打ち（5件ちょうど）を「6件以上」に変更（テスト追加で verify の Detection 段が落ちた。認識の失敗ではない）。
- **当日の故障予測**（[premortem](premortem-20261011.md)）: 上位は背景の誤認識、箱内の発熱、B 台の通信。ゲーム本体に関わる2点（通信断で赤だけマウス操作になる、タイトルの×が確認なしで終了）は**ユーザー判断で対応しない**（10-11）。

### 2026-10-11 10-10 の実機テストで出た不具合の修正（Claude＋Codex、verify 全項目 PASS）
- **Wi-Fi 切替後に約100秒届かない**（0058315）: P2PSender は失敗した接続の次から、browser が報告する interface（awdl 優先）へ順に固定して試す。最初の接続と pong 後は従来どおり OS 任せ。原因は仮説（UDP が切れたネットワークの cached record へつなぎ直し続け、失敗が通知されない）で、実機未確認。iPhone のログが無く特定できなかったため、**通信ログ**（経路切替・P2P 探索/再接続・LAN 宛先変更。画面に直近12行、端末の Library/Logs/connectivity.log）を追加。
- **届いていないのに NETWORK READY**（同）: 送信中に Unity の応答も P2P も3秒無いと「PC（Unity）から応答がありません」を表示（見出しの文言は不変）。runbook に「PC の Wi-Fi 自動接続を切る」を追記。
- **手動録画に振りの画像が残らない**（9212734、原因調査と保持修正は Codex、[詳細](debug-retention-20261011.md)）: Stop 待ち候補の間引きが中間の最良窓を捨てる欠陥を修正。`memory` と出ていた ledger は実際には予算超過ではなく tracking 窓への置換だった（`tracking_mode` / `not_retained` に分離。**10-10 に「メモリ上限で4件捨てられた」と報告したのは誤り**）。55.40秒の event が末尾5秒扱いにならなかった件は現ソースで再現せず、実際の終了時刻（frame 数/60 より長い可能性）を `recordingEndTimeSeconds` に記録するようにした。止めに来る場面のほうが instability が高い、という設計上の限界は残るため、**振りだけ録画**（swing_only_2026_10_11 v1、51秒、消灯5秒→大きく振る→止める→速く振る、lossless 6枚、自動終了）を追加。
- **F9 の比較が成立しない**（f32e771）: 記録ブロック数と「8 ブロックで十分」を画面に表示。
- **解析ツール**（43c8e52、review-python-tools.md）: latency_report の `--since` を時刻として解釈（JST 指定で全件落ちていた）、書きかけの latency-block 行を無視、triage receiver に 150 秒の idle timeout、selection replay が壊れた JSON で落ちない、PlayMode batch が 0 件実行を成功にしない。
- **Unity の受信→描画直前**（b46352b、unity-frame-cost.md）: 中央値 7.0ms / p95 7.6ms（フレーム間隔 8.33ms）、Update 先頭→描画直前 0.18ms。余分な1フレーム遅れは無い。
- **core-line 過連結と壁画 FP の offline 解析**（327c642、Codex、[詳細](coreline-overlink-and-mural.md)）: 暗い隙間への無条件補間（pipeline.cpp:179–184 / BGRADetection.swift:390–398）と raw 長に依存する加点が原因。限定 guard G(.6) で 21952 BLUE / 21953 RED が正しい候補に戻り、formal 40/40、旧ラベルの剣 赤29・青31 の検出を保持（端点の正解までは未確認）。壁画 BLUE FP は 9→4 まで、0 にできる rule は無し。**本番採用は保留**（別 swing の実剣 2 event が未達）。
- **iPhone 送信処理の連続運転**（[soak.md](soak.md)）: Mac 上で 15 分×2 条件。footprint +0.7〜0.8MB、スレッド・fd・送信レート・送信時間は劣化なし。
- 検証: verify_phone_saber.sh（iOS XCTest / Detection / Lossless 40 / Tools / iOS Release / Diff Check）全 PASS。Unity EditMode は PhoneSaber 系 PASS、全体 1870 件中の失敗 2 件は譜面エディタのテスト（ChartRecordingWorkflowTests、今回の変更と無関係）。
- **Codex レビュー後の修正**（8a86b0a、P1 なし・P2/P3 の4点）: 経路の順番は「リンクが使える状態に戻ったとき」だけリセット（1回だけ pong が返る経路へ戻り続ける穴）、試行中の interface の広告の出入りではやり直さない・やり直しは 4 秒以上あける、警告は送信要求に連動（P2P だけで開始した場合も出る）・手動 IP では P2P 接続を証拠にしない、手動 IP は別の文言。
- **レビュー指摘のテスト追加**（Codex）: 警告の4パターン（P2P のみで bridge 停止、LAN 応答の途切れと回復、手動 IP、停止）と、保持処理の最悪条件（60fps で上昇し続けるスコア）。停止直後に警告が残る点を1行で修正。pending は 5 秒窓ぶん（約300件）で頭打ちになり、捨てた payload は全件返ることを確認。この最悪条件では挿入ごとに全 pending を見直すため Debug の simulator で 1 フレーム約 5ms かかる（通常の録画では pending は少数。影響は Debug Recording 中だけ）。
- **core-line guard G の実装準備**（ローカル branch `opt/coreline-guard-prototype` fa2aaad、main には未投入・push していない）: C++ / Swift 共通の compile-time フラグ `PHONESABER_CORELINE_GUARD`、既定 OFF。OFF は main と 662/662 bit 一致、ON は formal 40/40・選択変更 0、shadow の数値を再現、C++ core 1.648→1.674 ms/frame。採用は別 swing の実剣 2 event の証拠後。
- Codex 週間枠は 98% 使用（10-14 頃リセット）。

### 2026-10-10 学校での実機 capture（iPhone＋Mac、Claude 解析）
- F9（Editor、iPhone テザリング経由 LAN、60fps、n=22）: 中央値 135ms、p95 179ms、最小 103ms。10-07 の LAN（30〜40fps、中央値 132〜150ms、p95 204〜347ms）と中央値は同等。1ブロックだけなので fps 自動比較は未成立。
- 16:30:15 頃 Mac がテザリングから別 Wi-Fi（192.168.31.x、DHCP 16:30:24）へ切替。P2P も en0（テザリング回線）経由だったため LAN と同時に切れ、awdl0 で座標が戻ったのは 16:31:57（約100秒 無受信）。録画が原因ではない。つなぎ直しに100秒かかった理由は未特定。
- phonesaber_20261010_163034_875（手動 43秒）: 手持ちで床の Mac 画面を撮影。剣なし。画面の色を RED 49.7% / BLUE 92.4% で検出（画面の誤検出の証拠としてのみ有効）。
- phonesaber_20261010_163949_658（手動 60秒、1/100秒、屋外に面した明るい場所、剣は約5m 以上・逆光、ORIGINAL で赤・青の実剣を確認）: 検出率 RED 66.6% / BLUE 55.9%（移動中を含む）。**実剣がほぼ静止している frame で、剣と遠くの明るい点を1本につないだ core-line 候補が大差で winner になる**。
  - frame 21952 BLUE: winner core-line 端点 [16,66]-[310,114]（rawPCASpan 300px、score 108.7）。正しい候補（color-mask [14,66]-[52,76]、61.6）は eligible のまま rank 3。前後 frame（21954/21955）は正しい core-line [16,68]-[56,76]。
  - frame 21953 RED: winner core-line [14,30]-[90,118]（score 118.1）。正しい候補（core-halo [80,84]-[88,136]、63.4）は eligible rank 2。
  - 判定: 正しい候補は eligible に残るが、**僅差ではなく大差**（gap 47〜55点）で長い core-line が勝つ。CASE A（僅差のすり替え）の定義には合わない新しい型（core-line の過連結）。1 recording・同じ構えの中の連続 frame なので、修正 gate（別 swing で2 event 以上）は未達。会場に近い屋内条件で再現するかを次の capture で確認する。認識コードは未変更。
- phonesaber_20261010_164236_960（手動 57秒、1/100秒、屋内・青緑の壁画が背景の別画角）: 検出率 RED 84.6% / BLUE 89.7%。ユーザーの体感も「判定はいい感じ」。ただし bundle の12枚はすべて録画末尾（停止しに来る場面、剣は消灯・手持ち）で、振っている最中の ORIGINAL が無い。末尾では壁画を BLUE（color-mask、端点間 約420px）、手を RED と誤検出。過連結がこの画角で出るかは画像が無く判定不能。次は振りの最中に lossless を残す（Capture Lossless か ガイド付き録画）。


### 2026-10-10 Claude 並列レビュー（iPhone / Unity / Android）と修正
- iPhone（5680fa9, review-ios-sender.md）: 開始直後の停止→開始で古い完了通知が新しい送信のカメラを止める（F1）、Bonjour の IP 変更後も LAN 生存確認が旧 IP を見続け P2P に回り続ける（F2）、生存確認の受信 error 後の再接続、LAN 送信中の「P2P」表示、など7件。verify 全段 PASS。
- Unity（32821c4, review-unity-phonesaber.md）: イベントログの書込失敗で行が消える・世代交代失敗で記録が止まる、受信 lock 中に毎フレームの台名読み出しが待つ。EditMode 1698/1698、PhoneSaber PlayMode 17/17。
- Android（86a350c, review-android.md）: IPv6 RA/DHCP の LinkProperties 通知のたびに PC 選択を消していた（会場 Wi-Fi で数分ごとに途切れうる）、前回 PC を名前だけで照合、カメラ起動の例外、エラー文、画面再生成で閾値が戻る。unit 47/47。
- Mac launcher（b41ae3f）: built .app でも P2P 予備経路が起動するよう open --env で bridge の場所を渡す（実 Player では未確認）。
- PlayMode 全件を小分け実行するツール（45e8ecf, unity_playmode_batches.py）: 6 run・211 pass・crash 0、既知の Calibration 2件のみ失敗。
- Windows kit（d3d4b58, review-windows-kit.md）: Player の受信 Block 規則（表示名 ServTechSlash）を firewall スクリプトが拾えず無効化できなかった、標準入力リダイレクト時に再起動待ちの timeout が失敗して1回目のクラッシュで監視終了、STOP 残存時に無言で閉じる。Windows 実機では未実行（確認5項目はレビュー文書末尾）。
- docs（41b79e9）: スタッフ手順・runbook・P2P/iOS/Android README・CLAUDE.md/AGENTS.md を現行コードに合わせた（LAN 優先、P2P は launcher が自動、Thread.Abort の古い記述を削除）。
- 実機で確認すること: iPhone 開始直後の停止→開始でカメラが止まらない／Mac の IP 変更後に LAN 表示へ戻る、Android を IPv6 のある Wi-Fi で10分以上送信して途切れない／Unity 再起動後に同じ PC へ戻る、built .app の F8 で P2P bridge ON。

### 2026-10-07〜10 遅延・負荷の最適化（まとめ。詳細は各 docs/claude/*.md と commit message）
- 計測: F9 遅延テスト（画面→受信、20回ごとに events.log、latency_report.py）、両スマホの「撮影→送信」、Unity frame cost（PhoneSaberFrameCost.cs: ゲーム約39B/フレーム、Editor 計測の500KB/フレームは Editor 拡張分）。
- 経路: iPhone は LAN 優先（Unity の 5007 応答で確認）・P2P 予備。10-07 実測で LAN は P2P より中央値約45ms速く p95 約半分。iPhone 60fps 既定（撮影→送信 50→37ms）。
- iPhone: 認識を bit 一致のまま 3 段階で高速化（XCTest 明るいフレーム 20.7→3.3ms、Mac native 2.3〜2.9ms）、座標を検出キューから直接送信（MainActor 待ち約10ms→ほぼ0）、UI 12Hz 集約、送信の割り当て・dispatch 削減、Release で落ちる queue precondition を DEBUG 限定に。
- Android: Debug でも -O2（-O0 で全送信が鮮度切れだった）、C++ core を4段階で高速化（Mac 12.0→約1.2ms）、回転を JNI 内へ、60fps（対応時）、Wi-Fi 低遅延ロック。JNI parity 38/38（エミュレータ）。
- Unity: 受信の割り当て削減（parser/統計 0）、HUD/UI の毎フレーム文字列・TMP 再設定削減、運営表示/F9 の OnGUI は表示中だけ、実行順固定、maxQueuedFrames=1、Player 通常 Log の stack trace 省略、任意の揺れ補正（OFF/弱/中）と遅延補正（0〜60ms、実データで効果小のため既定 OFF）、1クリック/バッチの Player ビルド、Windows firewall スクリプトを Public（ホットスポット）対応。
- 検証基盤: Unity EditMode を batchmode（プロジェクト複製）で 1694/1694、PhoneSaber PlayMode 17/17（入力の focus を無視する設定が必要）。全 PlayMode は Enlighten の native クラッシュで途中終了することがある（main でも）。
- 分析: recognition_benchmark.py（ORIGINAL 601枚、剣なし FP 赤56/83・青9/32）、修正B offline（静止青ぶれ 139→13px だが formal 5件失敗）、赤/青 FP 特徴（red-fp-features.md）、ぶれ原因（jitter-analysis.md）、予測の実データ評価（predict-real.md）。
- 不採用/保留ブランチ: opt/android-energy・opt/android-startup（探索・起動順、実機確認待ち）、opt/android-pipeline・opt/ios-debug-load（効果小）。

以下は履歴に記録された検証結果。今回の文書整理で再実行したものではない。

- 2026-10-07 / 台別位置補正 `0631d2c`: F7で4隅採取・保存・ON/OFF・台別リセット。Windows／MacのruntimeとEditModeテストはRoslynコンパイルPASS、Editor実行・実機は未了。
- 2026-10-07 / Android JNI正式回帰 `44dafd8`: API 37 ARM64エミュレータで35/35 PASS（40色別期待値・両色の端点／payload・RGBA hash・padding）。内部doubleのbit一致とCameraX実画像は対象外。
- 2026-10-07 / F8ネットワーク確認 `d1ca0dc`: 直近5秒の間隔／片道時計差・判定、両アプリの計測スイッチ。commit記録ではmerge後にUnity compile・iOS verify・Android build検証、実機比較・Editorテストは未了。
- 2026-10-07 / 剣なし座標再生 `d3b8832`: synthetic／保存track・診断bundle読取抽出、遅延・欠落・誤検出・台探索。ツール単体テストPASS（localhostのみ）、ゲーム内動作は要確認。
- 2026-10-07 / スマホ自動復帰 `18d8a78`: 送信意思保存、前面復帰／再起動、カメラ再試行、同じ台への再探索、自動ロック防止。公式verify・Android Debug build／unit tests PASS、実機復帰・長時間は未了。
- 2026-10-07 / PC監視・Macキット・ログ `7506dd6`: 異常終了5秒後の再起動、正常Quit／STOP、5世代イベントログ、背景受信・スリープ防止。両OSのRoslyn compile・Mac監視mock PASS、Windows実行は未了。
- 2026-10-07 / 反転設定 `18541a7`: Androidに左右／上下反転、両アプリで保存（既定OFF）。公式verify・Android Debug build・JVM 27件PASS、実機の向きは要確認。
- 2026-10-07 / 端末状態・Android開発モード `80a07b9`: 発熱・電池・実測fps・処理中央値、エミュレータ用非Wi-Fi手動送信。公式verify・Android Debug build・JVM 24件PASS、実機の熱は未確認。
- 2026-10-07 / 赤青支持画素走査の高速化 `eaa07cb`: 判定不変、明るいframe平均52→20.7ms。269 PNG・541遷移・27合成でSwift/C++差分0、formal 40/40・公式verify PASS。
- 2026-10-07 / F8運営表示・Windowsキット `2f3c0fb`: 台・両色の受信／解析・経路・探索・無受信警告、台別bat・Private UDP許可。Roslyn compile PASS、Windows／Editor実操作は未了。
- 2026-10-06 / 青ルール採用 `fa5db16`: deep-blue支持なしのeligible候補を却下・再選択。BLUE変更はformal 5/35＋inbox 38/234、RED変更0/269（[一覧](data/2026-10-06_blue_no_deep_support_output_changes.csv)）。formal 40/40、269 PNG×3経路・541遷移・27合成で差分0、公式verify・Android build／unit tests PASS、実機青の再試験待ち。
- 2026-10-06 / 台指定 `1132754`（merge `ad0b5a4`）、確認 `fe4cee7`: PC／スマホのA/B、P2P／診断も台を限定。Mac＋iPhoneで同じ台だけ接続することを実機確認済み、Android／Windowsは未確認。
- 2026-10-06 / 剣なし録画・手動capture受理 `689f3ee`、`2ab0813`: 163345_325／163444_198（1/100秒、P2P）は赤FP 9.7%／11.3%、青3.2%／27.3%。赤は白に近い暖色、青は深青支持0の空色。手動録画のlossless 3枚を受理する修正を実施。
- 2026-10-06 / Android core／sender／PC探索 `b0efcc2`、`a61dd1a`、`170fd79`、`33feead`: Mac Swift/C++ 248画像×3経路・541遷移・18合成でbit差分0、formal 40/40、Android build・JVM 16件PASS。bionicは237/237 selected出力一致、内部値は1ulp差。AQUOS／Windows受信は未確認。
- 2026-10-06 / 実機赤・露出・ガイドv3 `8f9a347`、`461ac03`、`4a841f4`: v2最初の剣なし赤598/598→0/598、棚の橙色FPは残存。1/100秒で横切り72%→99.6%・先端向け96.6%→100%、別の剣なし区間は3.5%／100%で画像不足。露出上限・診断schemaを修正し、v3に最初の剣なし画像と5枠を追加。公式verify PASS、v3実機待ち。
- 2026-10-06 / repo統合・運用案内 `344b4b7`、`93452ce`、`9cd6697`: Git／Unityルートを3D-Saber、ツールをPhoneSaberへ統一し、4組み合わせの手順を整理。公式verify・Android build／unit tests・変更C# compile PASS。
- 2026-10-05 / 整理: `af20a7b` shadow R7e/PF22 記録と一度きりの調査ツールを削除（旧 bundle は引き続き読める）、`376ddcc` FINDINGS に調査を統合・残作業を一本化（公式 verify 全 PASS）。
- 2026-10-05 / 残存 FP・W: `cc47927`、`5888bb5`、`5fee717`。全件分類、青 D は保留、淡い剣を失う W は不採用（研究テスト 27 PASS、D formal 40/40）。
- 2026-10-05 / 赤の認識: `43f6b61`、`06a18c5`、`8363024`。hue/深赤単独は見送り、warmNoDeepRed 採用（公式 verify 全 PASS、formal 40/40、198 枚 mismatch 0、要実機再試験）。
- 2026-10-05 / 診断転送・起動: `9661ec2`、`fb6dc17`、3D-Saber `9613d60`。ガイドの swing 4 枚を受理、Unity Play で受信側起動（会場確認未了）。
- 2026-10-04 / 撮影・解析: `904faba`、`7823070`、`ded9bba`、`2a82db8`、`60f1933`。2 本は振りの証拠不足、ガイド v2、肌・照明 FP を確認（CASE A gate 未達、実機再試験待ち）。
- 2026-10-04 / 旧 shadow 比較: `f70dcde`、`431555c`。R7e/PF22 を当時集計したが昇格せず（formal 40/40、露出感度・実機証拠不足）。
- 2026-10-04 / Mac 運用: `6ac28ca`、`a2faab5`、`d5e547c`、`96a144f`。古い受信コード・ログ隔離、一覧、2 台目 setup、負荷依存テスト対策（6ac28ca: Tools 366 件 OK・1 skip、実機運用未確認）。
- 2026-10-03 / P2P 改善: `0aeeb8f`、`1c751df`、`36d9d93`、`63eca9d`、`5723323`、`4def143`。通信優先度・復帰・Mac 固定、露出実験、runbook、検証用 Simulator 分離（verify PASS、効果は実機待ち）。
- 2026-10-03 / 解析・診断: `59383b3`、`f88fa50`、`a18816a`、`ad25839`。不採用候補 12 件、test/短録画の転送停止、解析 25 分＋high 再試行（144936 は CASE B、155919 は背景）。
- 2026-10-03 / P2P 導入: `09ffdcd`、`152397c`、`06362f7`。LAN fallback・診断 relay・RTT（導入時 XCTest 177、Python 277、formal 40/40、Release PASS）。
- 2026-10-03 / 決定性・解析基盤: `bbc1f32`、`1ad12f9`、`31ad94c`、`7449bc7`、`67597e4`、`312f63c`、`d55294c`、`4a38f53`。score 固定・bit parity・emitter/露出・区間・解析耐性・hotspot（formal 40/40、当時 verify PASS）。
- 2026-10-02 / 診断・背景再解析: `6bf2cf3`、`a93658e`、`40986a2`、`ecbebc8`、`bdd684e`、`cf3a873`、`ebb398a`、`23b667f`、`b2d74dc`。窓・CASE・メモリ・report/benchmark（XCTest 150→155、Python 182→220、formal 40/40、背景 FP 7/8）。
