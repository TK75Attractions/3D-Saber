# 当日資料の照合（2026-10-11・未commit）

最初の文書作業は `356ff71` から継続。中断再開時に最初に実行した `git log --oneline -3` は `21a2efe` / `356ff71` / `7c045fc`（すべてWIP）、`git status` はclean。`21a2efe` の文書・テストをレビューして保持し、不足分だけ補完した。
前回の監視・二重起動防止・再試行・Statusの変更を引き継いで確認した。今回の変更は資料と資料用回帰テストのみ。
**commit / push・公式verifyは行っていない。オペレーターがレビュー・公式検証・commitを行う。**

## 未解消（高）

`docs/STAFF_MANUAL.pdf` の2ページは旧版のまま。台だけ直す手順と一律5秒待機が残り、これを配布すると新版Markdownの修正が届かない。`docs/claude/EVENT_DAY_RUNBOOK.md:4` に配布禁止と差替手順を明記したが、PDF自体はユーザーの例外指示に従い保持。新版スタッフ2ページ・復旧1ページ以内・開場チェック1ページの印刷確認も未完了。詳細は下記。

## 修正した資料の不一致（重大度順）

行番号は修正後。パスは `PhoneSaber/` 基準。

| 重大度 | 場所 | 当日の失敗シナリオと修正 |
| --- | --- | --- |
| 高 | `docs/STAFF_MANUAL.md:52`、`docs/claude/EVENT_DAY_RUNBOOK.md:29` | 正常Quit後やSTOP残存時にも5秒待ち続け、営業が再開しない。異常終了の表示がある場合だけ5秒待機、正常終了・理由不明は技術担当、STOP／二重起動は表示を見せる手順に分けた。 |
| 高 | `docs/STAFF_MANUAL.md:54`、`docs/claude/EVENT_DAY_RUNBOOK.md:212` | 固まったゲームへ送信やF8だけを試しても復旧しない。更新済みPlayer＋監視の45秒検知＋5秒待機、スタッフは60秒後に技術担当へ。旧build／監視無効／STOPの場合の技術担当の確認を追記。 |
| 高 | `docs/STAFF_MANUAL.md:56`、`docs/claude/EVENT_DAY_RUNBOOK.md:67` | B台のIPが保存済みだと台をAに直してもB台が動く。学生は技術担当に台と手動IPの確認を依頼。担当資料にiPhoneの空欄入力、Androidの空欄保存とWindows＋iPhoneの手動IP必須を明記。 |
| 高 | `docs/claude/EVENT_DAY_RUNBOOK.md:32`、`windows/README.md:39`、`android/README.md:123` | WindowsホットスポットをPrivateへ直そうとして時間を失い、残ったUnityのBlock規則を見逃す。管理者スクリプトはPrivate/Public双方・LocalSubnet対象で、初回許可ダイアログの後に実行することを統一。 |
| 高 | `ios/README.md:1` | 指定された当日用入口ファイルが存在せずiPhone手順を開けない。入口READMEを追加し、本番設定と既存の詳細READMEへのリンクを用意。 |
| 中 | `android/README.md:44` | 前面復帰後、古い指示どおり「開始」を押そうとすると現在のボタンは「停止」で、自動復帰した送信を止める。前面／Wi-Fi復帰は自動、手動停止・台変更・開発用スイッチ切替は別として記載。 |
| 中 | `docs/claude/EVENT_DAY_RUNBOOK.md:56` | 露出実験を録画専用と思い、録画OFF後も暗い通常映像の原因を見逃す。通常送信にも適用・保存、解除は「自動(既定)」への変更と明記。 |
| 中 | `ios/PhoneSaberSender/README.md:11` | 存在しない `run_debug.command` を探す、他人の `/Users/satoshi/…` で起動に失敗する、通常送信のまま時計差を測ろうとする。実在launcher・相対コマンド・手動IP・「遅延計測モード」と終了後の戻し方を修正。8765の診断受信側との競合も説明。 |
| 中 | `docs/claude/EVENT_DAY_RUNBOOK.md:103` | F8の送信元IPを送信先PCと照合し、正しく届いているのに違う台と判断する。LANはスマホのIP、Macのローカル転送は127.0.0.1と記載。専用競合警告は現ツリーに無いことと他端末を止める確認方法を追加。 |
| 中 | `docs/claude/EVENT_DAY_RUNBOOK.md:173`、`:192`、`:194` | NETWORK READYや録画の「完了」をPCへの到達・転送完了と誤認する。応答なし警告・通信ログ・約51秒の振りだけ録画、Mac Status／同じsessionIDの受信記録での転送確認を説明。転送専用UIは現ツリーに無い。 |
| 中 | `windows/README.md:79`、`docs/claude/EVENT_DAY_RUNBOOK.md:136` | F9の測定後にカメラを画面へ向けたまま営業する。8ブロックの進捗・終了・プレイヤー側へ戻す・固定／F7確認を追加。F4表示切替の既定ONと保存も説明。 |
| 中 | `docs/STAFF_MANUAL.md:10`、`:47` | スタッフ用の開場手順に同じWi-Fiの確認がなく、手入力先からの応答なし表示も復旧表に当てはまらない。開場確認を追加し、両方の「応答がありません」表示を技術担当へ見せる1操作に統一。手入力番号の古さはWi-Fi確認だけでは直らない。 |
| 中 | `docs/claude/EVENT_DAY_RUNBOOK.md:18`、`:127`、`:138` | F9を終了してもiPhoneのfps自動比較は動き続ける。同期チェックも残り、スタッフが期待する「間隔のみ」と一致しない場合がある。診断録画OFF・自動比較OFF・事前fps・同期チェックOFFへの戻しを開場表と計測後の手順へ追加。自動比較はDebugビルドだけにある。 |
| 低 | `docs/claude/EVENT_DAY_RUNBOOK.md:149`、`:183` | F7の表示名が手順と違う、Statusの古いログやGit WARNを現在のゲーム障害と誤認する。ボタン名を現物と一致させ、Statusの最後のbridge開始による切出しと古い記録／正常でもWARNの条件を説明。 |

スタッフの復旧表は2列の「症状→一つの対処」に変更し、UDP／P2P／bridgeの用語を入れていない。補完時に「手動IP」「IP」の説明も「PC番号」に統一。F8の引用表示は実際の「payload解析: OK」に揃えた（runbook:102、Windows README:66）。
runbook先頭に、A/Bそれぞれ上からチェックする11行の開場チェックを追加。スタッフ用は営業手順と復旧手順の間に改頁を明示した。
**ユーザー向けコード文字列の変更は0件。** 認識・UDP形式・formal corpus・Unityシーン／prefab／.meta／ProjectSettingsに今回の差分はない。

## 手順照合の範囲

| 資料 | 照合した手順と現行実装 |
| --- | --- |
| STAFF_MANUAL（MD＋既存PDF全文） | 開場・営業・全復旧行・片付け。ContentView / MainActivity、TitleSceneSkin / TitleMenuController、F8、Mac/Windows launcherとSTOP・heartbeat。 |
| EVENT_DAY_RUNBOOK | 前日準備・組合せ・起動順・F8計測・F7四隅・F4/F9・自動復帰・ログ／Status・診断・トラブル表・終了。PhoneSaberPlayerBuild / Station / PositionCapture / StatsDisplay / LatencyProbe、iPhoneのLAN応答監視・録画・転送、Androidのresume/discovery、firewall／watchdog。 |
| mac/README | build出力、A/B起動引数、ログ・正常Quit・STOP・二重起動拒否の文言、45秒監視、bridge再試行と環境変数、StatusのPlayer.log選択。 |
| windows/README | build一式、bat/helper、終了コード・ロック・監視、許可スクリプト全文、ホットスポット／IP、F8/F9と終了。Windows実動作は下の未実施欄を参照。 |
| android/README | buildファイル／wrapper、画面ラベル・既定値、開始／停止・台・手入力・前面／Wi-Fi復帰、開発用スイッチ、カメラ／送信と既存検証コマンド。過去の日付付き結果は今回の成功根拠に使っていない。 |
| ios/READMEとPhoneSaberSender/README | 入口の欠落、計測launcher／probe、接続UI・台／手動IP、時刻モード・復旧・露出・録画／転送、静止画検証のコマンド。 |

## 中断再開後に実際に実行した検証

今回の再開後の結果。前回のログを今回の結果として扱っていない。

- `python3.12 -B -m unittest -v PhoneSaber.tools.test_ops_docs`: **19件PASS**。今回追加・強化した3件は修正前にFAIL、旧PDF配布防止の1件も追記前にFAILを確認。文書作業前 `356ff71` の文書を読み込ませると19件中18のテストメソッドで失敗（subtest込み31 failure・1 error、相対リンク存在のみPASS）。比較ログ: `/private/tmp/phonesaber-doc-audit/docs-before-resumed.log`。
- `python3.12 -B -m unittest -v PhoneSaber.tools.test_ops_watchdog`: **Mac実行5件＋Windowsソース契約2件PASS、Windows実行5件skip**。今回のSTOP検査は初回でPASS。偽open／psと実子プロセスによる検査で、本物のUnity起動ではない。前回の競合の疑いは下記に保持。
- `python3.12 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_phone_saber_status.py -v`: **46件PASS**。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: **PASS**。swiftcで現行の静止画検査をビルド・実行、lossless pixel scan／proposal membership／scoring scratch／morphology／blueNoDeepSupport／速振り10フレームを検査。端末状態のホストXCTest **5件PASS**。正式corpus 40件の検証やiOSアプリビルドではない。
- `python3.12 -B PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_status.py`: 実CLIを実行し、数秒で日本語の状態表を表示、**exit 2（NG 3／WARN 2／OK 3）**。この環境ではゲーム受信なし・bridge停止・診断HTTP応答なし。稼働中ブースの成功とは扱わず、起動／復旧／機材変更はしていない。
- `xcrun swift -module-cache-path /private/tmp/phonesaber-doc-audit/swift-cache /private/tmp/phonesaber-doc-audit/read_pdf.swift PhoneSaber/docs/STAFF_MANUAL.pdf`: **PASS、既存2ページの全文を抽出**。古い台変更・5秒待機の文言が残ることを確認。最初の呼出しではPDF引数を付け忘れ、一時readerがexit133で停止したが、引数付きで再実行して成功。PDFは文書作業前356ff71とbyte一致。
- `bash -n PhoneSaber/mac/saber-watchdog.sh PhoneSaber/mac/Start-Saber-A.command PhoneSaber/mac/Start-Saber-B.command PhoneSaber/start_phone_saber_latency.command PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`、`git diff --check`: **PASS**。

## 前回WIPで実行した検証（過去の記録）

今回のログ・一時HTMLは `/private/tmp/phonesaber-doc-audit/`。以前のログを今回の結果として再利用していない。

- `python3.12 -B -m unittest -v PhoneSaber.tools.test_ops_docs`: **16件PASS**。HEADの文書を差し戻して同じテストを実行すると、15の親テストで失敗（subtestを含むfailure29、リンク存在確認のみPASS）。変更項目の回帰を確認。内容／ソース契約の検査であり実機操作の証明ではない。
- `python3.12 -B -m unittest -v PhoneSaber.tools.test_ops_watchdog`: 初回はSTOPケース1件FAIL。単独再試行PASS、全体再試行は**Mac5件＋Windowsソース契約2件PASS、Windows実行5件skip**。偽open／psと実子プロセスによる検査で、本物のUnity起動ではない。
- `python3.12 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_phone_saber_status.py -v`: **46件PASS**。
- `bash PhoneSaber/ios/PhoneSaberSender/run-static-tests.sh`: **PASS**。現行の拡張された静止画検査をswiftcでビルド・実行、端末状態のhost XCTest **5件PASS**。以前の「14検査」だけの構成ではない。
- `python3.12 /private/tmp/phonesaber-ops/compile_api.py`: **PASS**。前回のharnessを読み直し、現ソースからUnity6000.3.9f1 APIでEditor Mac/Windows・Player Mac/Windowsの4構成と関連Editor test7ファイルを再コンパイル。無関係な `UdpImuBridge.TryGetLatest` のみstub、Unity APIは実DLL。
- そのDLLに対して `/private/tmp/phonesaber-ops/Runner.cs` を同梱Roslynで再コンパイルし、同梱Monoで実行: **policy5件PASS、定常heartbeat1000書込みのmanaged allocation 0 byte**。Unity Test Runnerの実行ではない。
- `xcodebuild -project PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj -scheme PhoneSaberSender -configuration Release -destination 'generic/platform=iOS' -derivedDataPath /private/tmp/phonesaber-doc-audit/DerivedData CODE_SIGNING_ALLOWED=NO build`: **FAIL（exit65）**。`sandbox_apply: Operation not permitted` によりSwiftUI macro pluginが応答できない。iOS Release成功とは扱わない。
- `/bin/bash -n`（Mac watchdog・A/B launcher・計測launcher・Swift静止画検証スクリプト）、`git diff --check`: **PASS**。

## PDF・印刷の未完了

再開後、pdfスキルを読み、開始マーカーを正常実行して最終Markdownから一時HTMLを更新。Chromeで `file:///private/tmp/phonesaber-doc-audit/staff.html` を開く操作は **Browser Useのセキュリティレビューで拒否**。理由は許可URLがhttp／httpsのみで、fileプロトコルは対象外。回避操作はしなかった。ユーザーの例外指示に従って旧PDFを保持し、runbook冒頭に「配布しない」を追加した。以下は前回の生成失敗の記録。

履歴 `41b79e9` に「headless Chrome（従来と同じ）」で生成した記録がある。repo内に生成スクリプトは無かった。
現行Markdownから一時HTMLを作り、Chromeを `--headless --no-sandbox --disable-gpu --disable-crash-reporter --user-data-dir=/private/tmp/phonesaber-doc-audit/chrome --no-pdf-header-footer --print-to-pdf=…` で実行したが、**SIGABRT（exit134）、PDF出力なし**。
ユーザーの例外指示に従い、**STAFF_MANUAL.pdfは元の2ページ版を保持・未更新**。PDFKitで2ページと全文を読み取って確認済み。旧PDFには台／手動IPと5秒待機の誤手順が残るため、新版として配布できない。
スタッフ用の2ページ構成・復旧1ページ以内、runbook先頭のチェック1ページ構成は改頁付きMarkdownと一時HTMLに用意したが、新版の印刷／描画による収まりは未検証。
サンドボックス外で最終Markdownをブラウザ印刷し、ページ数と各表の切れ・重なりを確認してPDFを差し替える必要がある。一時変換スクリプトは `/private/tmp/phonesaber-doc-audit/render_html.py`（最終MDで実行済み）。

## 未実施・残るリスクと提案（未実装）

- 実機iPhone／AQUOS、Wi-Fi切替、P2P接続・転送、Windows cmd/PowerShell／firewall、本物のlauncher＋Player起動・45秒ハング復旧・長時間運転、Unity Editor Test Runner／Playerビルドは未実施。公式 `verify_phone_saber.sh` はオペレーター担当のため未実行。
- STOPテストの初回失敗では、STOP作成と監視検知が競合し `hang detected` が記録された。再試行はPASSだが競合を否定できない。`mac/saber-watchdog.sh:96` のSTOP確認から `:111` のkillまでには間隔がある。実機で境界を確認し、必要ならkill直前にもSTOPを再確認する修正を提案する（今回はコードを変更しない）。
- 依頼に挙げられた専用「送信元競合」警告と「転送状況」は現ツリーに存在しない。F8は最後の送信元だけ、iPhoneは録画完了表示とConsoleログだけ。専用表示の追加は操作画面を変えるため別作業として提案する。LAN/P2Pが届いた証拠も認識品質の保証ではない。
- PDF再生成・印刷確認が残るため、全作業完了／不具合ゼロとは報告しない。既存WIPのWindows終了コード0の致命的終了、長い読込み、初回heartbeat前の固まり等の実機リスクは引き続き[運用強化記録](ops-hardening-20261011.md)を参照。
