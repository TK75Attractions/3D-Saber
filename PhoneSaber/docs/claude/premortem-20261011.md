# 当日の故障予測（pre-mortem、2026-10-11、Codex gpt-6.1-sol・静的解析のみ）

最優先は **背景の誤認識、箱内の発熱、B台の通信・復旧**です。通信が正常でもゲーム入力が正しいとは限りません。

指定の7資料と関連コードを静的に照合しました。ファイル変更・ビルド・実機試験は行っていません。以下の順位は「起こりやすさ×営業への被害」の推定です。**R＝[runbook](PhoneSaber/docs/claude/EVENT_DAY_RUNBOOK.md)、S＝[スタッフマニュアル](PhoneSaber/docs/STAFF_MANUAL.md)**。

**当日の失敗予測：上位15件**

| 順位 | スタッフへの見え方・システム上の原因 | 現在の検知・対処案内 | 復旧の記載 | 最小の改善 |
|---|---|---|---|---|
| **1** | **剣が勝手に動く・突然長くなる。** 壁画、肌、投影光、遠方の明点が候補になり、最高得点の候補を採用する。実剣と明点の過連結も確認済みで、対策は本番未採用。[選択処理:1260](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift:1260) | **品質異常は検知しない。** 誤検出座標でもF8は解析OK・良好になり得る。 | R§1・5に背景確認あり。Sは固定確認→担当へ。 | 当日画像で合格した背景幕・遮光配置を固定し、「誤動作→幕を閉める」をスタッフ表に追加。 |
| **2** | **午後から遅い、途切れる、端末が止まる。** 給電・画面点灯・60fpsを箱内で継続。熱による自動減速設定はない。[iOS警告:43](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/DeviceHealth.swift:43) | スマホに発熱・fps低下と通風／電源確認を表示。PCへの通知・自動交代はない。 | R§4・5、S§2・3あり。予備端末は記入欄のみ。 | 冷却済み・設定済み予備端末を実際に用意し、交代条件と交換手順を1枚にする。 |
| **3** | **B台だけ開始しても動かない。** 学校Wi-Fiの端末間隔離、WindowsのPublic扱い／Block規則。許可スクリプトもLocalSubnet限定。[Firewall:18](PhoneSaber/windows/Allow-PhoneSaber-Firewall.ps1:18) | F8は無受信を検知するが原因は判別不能。Androidの送信fpsはPC到着確認ではない。 | R§5・Windows READMEに具体策。Sは担当へ。 | 本番Playerで許可を済ませ、B用ホットスポットを保存。「B予備SSIDへ接続」の復旧カードを用意。 |
| **4** | **静止なら動くが、速く振る・頭上へ上げると消える。** 自動露出のブレ、距離・向き・遮蔽で候補が消失。予測は最大3処理フレーム。[FrameProcessor:654](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/FrameProcessor.swift:654) | 未検出・受信減少は見えるが、ブレ／遮蔽／通信を区別しない。 | R§1・5に事前比較・画角確認。Sに振り方の復旧説明なし。 | 実機で合格した距離・頭上範囲・照明を写真で固定し、スタッフ向けに「立ち位置へ戻す」を追加。 |
| **5** | **別台が動く／台を直しても戻らない。** 保存した手動IPは台指定より優先。iPhoneではP2Pも無効化される。受信側に台の拒否機能はない。[台変更:1270](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:1270) | IP・台表示はあるが、不一致を止めない。 | R§1・5は説明あり。S§3の「台を直す」だけでは解消しない。 | **「停止→手動IP解除→台指定→開始」**へSを修正。コードなら「この台を自動探索」ボタンを追加。 |
| **6** | **Wi-Fiが変わった後、長時間戻らない。** LANとP2Pが同じWi-Fiインターフェースを使う場合があり、独立した予備経路ではない。約100秒途絶の実績。[STATUS:71](PhoneSaber/docs/claude/STATUS.md:71) | iPhoneは応答なし警告・通信ログあり。自動再接続するが復帰時間の保証なし。 | R§1・4・5あり。SにSSID確認手順なし。 | 他SSIDへの自動接続を事前解除し、A/Bの正しいSSIDを箱に貼る。Sに「指定SSIDへ戻す」を追加。 |
| **7** | **休憩後・端末交換後に位置がずれる。** F7補正は台名だけで保存され、端末・画角・反転・箱の移動を識別しない。EditorとPlayerも別保存。[保存キー:121](Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberPositionCalibration.cs:121) | ON／保存済みは表示するが、古い補正かは検知しない。 | R§3に再測定条件あり。Sは担当へ。 | 箱・端末・床に固定位置の印を付け、**交換／移動後はF7必須**を交換カードに記載。 |
| **8** | **充電していたはずなのに電源が切れる。** 抜け・給電不足・熱による充電制限。電池表示はあるが、低電池や継続減少の警告条件はない。[Android警告:40](PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/DeviceHealth.kt:40) | 電池%・充電状態のみ。異常の積極通知なし。 | R§4・5に電池減少。Sは接続確認中心。 | 低電池／未充電の目立つ警告を追加し、同じ場所の予備ケーブルへ交換できる配置にする。 |
| **9** | **通知・ロック・画面移動後に入力が止まる。** 前面での送信が前提。Androidは画面終了で停止、意思を保持して前面復帰時に再開。強制ロック解除・OS終了後の起動は人が必要。[onStop:433](PhoneSaber/android/app/src/main/java/jp/phonesaber/sender/MainActivity.kt:433) | 前面では復旧状態を表示。箱内・ロック中のスタッフへの通知なし。 | R§1・4、S§3あり。 | 集中モード・画面固定を準備済みにし、「解除→アプリ前面」の実機写真を箱に貼る。 |
| **10** | **通信が切れても赤だけ残る／マウスへ移る。** Unityは約1秒まで入力を有効扱いし、その後赤だけマウスへフォールバック。青は消える。[本編設定:400](Assets/Scripts/Game/GamePlayManager.cs:400) | F8を開けば無受信警告。通常画面では入力源の変更を案内しない。 | Rは無受信対応のみ。赤だけマウスになる説明はR/Sともなし。 | 本番モードでマウスフォールバックを無効化し、入力途絶時に小さな常設表示を出す。 |
| **11** | **ゲームが消え、5秒待っても戻らない。** タイトルの×は確認なしの正常Quit。監視も正常終了する。STOP残存でも起動しない。[終了ボタン:185](Assets/Scripts/UI/TitleSceneSkin.cs:185) | launcherには終了理由が出るが、ゲーム画面は消える。 | Rは正常Quitの例外とSTOP削除を説明。S§3は「落ちたら5秒」のみ。 | Sに「戻らなければStart-Saberを再実行」を追加。本番の×は長押し／確認付きにする。 |
| **12** | **復旧で連打した後、受信停止・片方だけ動く。** Windows batにはMac版のような二重起動ロック／既存Player確認がない。[bat:30](PhoneSaber/windows/Start-Saber-B.bat:30) | Unityはポート競合を記録し再試行。Windows launcherは重複を止めない。 | R§1・5に重複停止あり。Sに具体操作なし。 | WindowsにもPC単位の起動ロックと「既に起動中」の表示を追加。 |
| **13** | **画面が固まったまま、監視が復旧しない。** 両launcherはプロセス終了待ちで、Unityのフレーム進行を監視しない。[Mac待機:114](PhoneSaber/mac/saber-watchdog.sh:114) | **ハング検知なし。** F8も固まれば使えない。 | R/Sはクラッシュ中心。強制終了→監視復旧のスタッフ手順なし。 | SにOS別の「ゲームだけ強制終了」手順を追加。自動化にはUnity更新時刻の監視が必要。 |
| **14** | **PC／投影が消える、音が出ない、画面が読めない。** スリープ設定・投影入力・音声出力は別管理。コードのスリープ対策は`Screen.sleepTimeout`設定で、Windows実効性は未確認。F4の投影モードも保存される。[起動設定:46](Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberOperatorOverlay.cs:46) | 投影・音声出力の正常確認なし。 | Windows READMEにスリープなし設定。R/Sに投影・音声の復旧表なし。 | 「AC時スリープなし／投影入力／音声出力／F4」のチェック写真と復旧カードを追加。 |
| **15** | **学校Wi-FiではA台だけ復旧不能になる。** P2P bridgeが非0終了すると`startFailed`を立て、同一プロセスでは再起動しない。LANも隔離されていると逃げ道がない。[bridge終了:280](Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberP2PBridgeProcess.cs:280) | F8はbridge停止、ログは警告。ただしPlayerにも「script compile／Editor再起動」と案内する。 | R§5にbridge確認・LAN退避。Playerでの再起動手順は不足。 | 当面「ゲームQuit→A launcher再実行」を明記。コードなら回数制限付き再起動／再試行ボタン。 |

**(a) 文書とコードの矛盾・誤誘導**

- **Androidの復帰説明が古い。** [README:44](PhoneSaber/android/README.md:44)は画面終了・Wi-Fi変更後に開始し直すと説明するが、現行コードは送信意思を保持し、前面復帰・ネットワーク更新で自動再開する。
- **露出実験は録画限定ではない。** R§1は診断設定として扱うが、保存値を読み、通常送信開始時にも適用する。[読み込み:828](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:828)、[適用:1537](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:1537)
- **Mac Statusは本番Playerを誤診する。** R§4の「通常は全項目OK」に対し、ツールは名前が`Unity`で始まるポート使用者だけを正常扱いし、PlayerをNGにして停止・Editor Playを案内する。さらに読むログはEditor.log固定。[判定:265](PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_status.py:265)
- **診断受信の自動起動はPlayerでは成立しない配置。** repoを`Application.dataPath`の親から探す。Mac launcherが渡す外部パスはP2P用だけなので、ビルド版では別途Start PhoneSaberが必要。[診断探索:21](Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberTriageReceiverLauncher.cs:21)
- **WindowsのPrivate必須という案内は過剰。** R§5／Android READMEはPrivateへ変更を案内するが、現行許可スクリプトはPublicのホットスポットにも対応する。Windows READMEの説明が正しい。
- **Sの台修正・5秒再起動は条件不足。** 手動IP残存、正常Quit、STOPファイルでは、その操作だけで復旧しない。
- Android READMEの「未実行」記載とSTATUSの後日のPASS記録が混在。**エミュレータの成功と、本番B台の未確認を分けた最新版チェック表が必要**。

**(b) 代替が成立していない単一障害点**

- **各台の1台のカメラ・同じ認識器**：両色を同時に失う。マウスは赤だけで、二刀プレイの代替にならない。
- **B台のWi-Fi経路**：AndroidにP2Pなし。ホットスポットも同じPC・無線・電源に依存する。
- **A台の隔離環境でのbridge**：LAN不能時にはbridgeが唯一の経路。片色の受信ソケット喪失でも探索・Bonjour・bridgeを全部停止する。[連動処理:924](Assets/Scripts/Managers/Inputsystem/Input/InputPoint.cs:924)
- **各PC／投影／給電**：ソフト監視で復旧できるのは主にゲーム終了。OSハング・停電・投影故障には自動代替なし。
- **技術担当者**：Sは多くの症状を担当者へ渡すが、連絡先・予備機材欄が空。担当者不在なら復旧が止まる。

**(c) Xcode Debug導入で見落としやすい設定差**

- **通常のRunはDebug**。[scheme:21](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender.xcodeproj/xcshareddata/xcschemes/PhoneSaberSender.xcscheme:21) ReleaseビルドPASSだけでは、当日導入したアプリの性能を確認したことにならない。最適化設定もrepoでは明示されていない。
- **Debugは詳細パネルを閉じてもフレームTrace・間隔統計を作る**。Release通常経路とは処理量が異なる。[FrameProcessor:540](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/FrameProcessor.swift:540)
- **「fps自動比較」をONにしたままF9だけ終了すると、iPhoneは30秒ごとの30⇄60fps切替を続ける**。切替は停止→開始を伴う。通常送信へ戻す手順に、このOFF確認がない。[比較処理:1378](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift:1378)
- **60fps・P2P ON・反転OFFは新規状態の既定値**。現行アプリは保存済み30fps、P2P OFF、反転、手動IP、露出上限を優先して読む。Xcodeで最新版を入れたことだけでは設定を初期化しない。
- **Debug限定のテスト履歴が実アプリでも蓄積する**。UDP再設定ごとのcallbackを無制限に保存し、停止でも消さない。影響量は未測定だが、8時間運転に不要。[UDPSender:96](PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/UDPSender.swift:96)

**(d) 実機時間を優先配分する5つの確認**

1. **本番背景・投影・人ありで認識確認**：両色の消灯、静止、速振り、頭上、先端向け。合格は「F8良好」だけでなく、消灯時に出ず、実剣へ追従すること。
2. **本番Player＋launcherでA/B同時接続**：学校Wi-Fiと予備ホットスポットを試し、交差接続なし、両色受信、WindowsのPublic受信を確認。B台を優先。
3. **箱・充電器を含めた45〜60分の連続運転**：両台を並行稼働し、熱、電池の増減、実測fps、F8の間隔を開始時／終了時に比較。短時間では代用しにくい。
4. **スタッフによる故障復旧リハーサル**：画面ロック、アプリ再起動、Wi-Fi切替、ゲーム強制終了、正常Quitを順に試し、復帰時間を測る。AはLANを切ってP2P予備も実際に通す。
5. **本番投影でF7→1曲→結果→次客を通す**：音声出力、画面の見やすさ、四隅、入力途絶時の赤マウス化、Fキー操作、予備端末交換後の再測定まで学生スタッフに操作してもらう。