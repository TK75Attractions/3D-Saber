# PhoneSaber STATUS

## ユーザー待ち

2026-10-09時点。最新版を実機に入れて確認する。手順は[当日runbook](EVENT_DAY_RUNBOOK.md)。

0. **最適化の実機確認（最優先・数字の入力不要）**: Mac `git pull`→Unity Play、iPhone を Xcode で入れ直し（60fps 既定・LAN 優先・送信経路と認識の高速化・UI 12Hz）。iPhone と Mac を同じ Wi-Fi にして Debug Performance の「fps自動比較」ON → F9 を数分→F9。AQUOS は Mac に USB 接続すれば Claude が入れて 60fps 維持・JNI 中央値を確認。剣の遅延補正は F8 で 20ms を試し、振り心地を一言もらう。

1. **剣あり・iPhone＋Macのガイド付き録画v3**: 露出1/100秒で1本。最初の「saberなし」の画像、赤・青の静止／速振り／先端向けを残す。遠い・淡い青、画面端も確認。会場に近い距離（画面から2.7〜3.1m、範囲直径1.5m）で、背後の人・投影中・頭上の振りを含める。
2. **AndroidをAQUOS sense9で残りを確認**: 2026-10-07にWindowsホットスポット経由で両色の送信・F8受信・30fpsを確認済み。残りは台A/B探索、向き・四隅、反転保存、開始／停止／前面復帰／再起動／Wi-Fi切替。
3. **Windows実行の残り**: 10-07にEditor Playで受信を確認（ホットスポットはPublic扱いのため全プロファイルでUDP 5005-5007を許可、UnityのBlock規則を無効化）。残りはビルド版＋bat監視、A/B探索、F7、異常終了後再起動／正常Quit。
4. **Unity EditMode Test Runner**: `Window > General > Test Runner` でEditModeを実行。F8統計・位置補正・台別保存を含むテストの結果を共有する。Roslynでのコンパイル確認は済んでいるがEditor内では未実行。
5. **30分〜1時間の連続運転**: 本番の箱・給電・配置で送信し、発熱・電池・処理／送信／受信fps・処理中央値・到着間隔・メモリを記録。途中で前面復帰・Wi-Fi切替・アプリ再起動を試し、停止後は前面復帰だけでは再開しないことも確認。遅延計測は確認後OFFへ。
6. **剣なしの Debug Recording を2本**(剣がなくてもできる): 露出1/100秒、区間ラベル「saberなし」で開始、各1〜2分、Capture Lossless を2〜3回。① 窓や空が映る明るい場所(青のルールの確認)、② 人の肌・服が前を動く場所(赤の残りの誤検出)。

## 現在の仮説と確度

- **高**: 肌・照明・背景の誤検出と、速振りのブレによる候補消失が主因。旧CASE A代表例は背景同士で、実剣の選択修正gateは未達。
- **高**: AWDLの詰まりは認識とは別の遅延原因。interactiveVoiceの実機効果は未確認。F8で間隔・同期時の片道時計差を比較できるが、画面全体の遅延ではない。
- **確認済み**: score決定性は `bbc1f32`、赤warmNoDeepRedは `8363024`。10-06の実機v2では最初の剣なし赤FPが598/598→0/598。ただし別の背景FP・小さい／淡い剣の保持は追加確認が必要。
- **採用済み**: 青blueNoDeepSupportは `fa5db16`。formal 40/40、既存正解187/187保持、剣なし青FP 29→18（offline）。遠い・淡い・ぶれた青の実機確認は未了。Wは淡いピンク剣を失うため不採用、fixture 89は窓期待値のまま。
- **実装済み・実機待ち**: 台指定、F8、両OSキット・監視・イベントログ、端末状態、自動復帰、反転、遅延確認、座標再生、F7位置補正。Mac＋iPhoneの台指定は確認済み。Android JNIはAPI 37 ARM64エミュレータで正式35 PNG／40期待値を確認（35/35 PASS）。内部浮動小数点のbit一致や実カメラの一致は保証しない。

根拠・残存FP・却下案は[FINDINGS](FINDINGS.md)。同文書とAndroid READMEの過去の「未commit／未実行」は、下の採用・検証commitを参照する。

## 次にやること

優先順。実装済み機能は実機で確認し、不具合が出た箇所を直す。

1. **本番の端末構成を通す**: 上のAQUOS／Windows／Test Runner結果を確認。F8・反転・F7・A/B保存、1920×1080の端点・最新入力・未検出時、開始／停止／復帰を実機とPlayModeで確認する。
2. **ガイドv3を解析**: ORIGINALを目視し、赤・青ルールの背景FPと小さい／淡い剣の保持を比較。自動露出／1/100秒、距離0.5〜3m・端からの向き・昼夜の速振りを比較する。既定露出変更はユーザー判断。
3. **本番の遅延・切断復帰を比較**: F8のp95／最大間隔、同期時の片道差、P2P RTT、画面全体の遅延を別々に測る。会場Wi-Fi・ネット使用中・2台Mac・LAN退避を同条件で比較し、許容値を決める。
4. **連続運転と復旧を確認**: 発熱・fps・電池・保存量・memoryHeadroom、低電池・カメラ中断・再接続・監視再起動を評価。録画256MiB上限の余裕を別端末でも確認する。
5. **残存背景FPとfixture 89**: 別人・別場所の点灯／OFF対、halo・点LED列を比較。窓と淡い剣の共存証拠、画像外1pxの端点を再審査し、認識と回帰を直す。期待値だけ変えると39/40。Wは再採用しない。静的マスクは固定背景だけの案で、静止剣の保持証拠とユーザー判断が必要。
6. **CASE A/B/Cを分けて調査**: bundleコピーでtriage dry-run／selection replayを行い、ORIGINALと全eligibleを照合。Aは別swingで実剣が僅差で負ける2event以上のgate後に修正。Bが多ければ生成前の棄却componentを記録。Cの端点修正はAと別にし、長い剣・分離LEDを保護。200ms以下の補間／外挿も誤出力の延長を比べ、ユーザー判断後に変更する。
7. **Mac計測画面を実操作**: 開始／停止／再開、CSV保存・結果フォルダ・失敗統計・実UDP・同一時計の表示→受信計測・Unityとのポート競合を確認する。
8. **診断受信と旧bundleを確認**: `/health`、安全な再起動、転送失敗後の再送、report／overviewを通す。234740／155919は手動再解析候補。旧010049の32KB超過は上限を維持する。
9. **会場でrunbookを通す**: 署名・権限、固定配置、背後の幕、投影・照明、箱の熱・電源、予備経路、終了、[2台目Mac](SECOND_MAC_SETUP.md)を確認。床の範囲表示、回転率・タイトル復帰・判定タイミングも確認。本番構成はMac＋iPhone／Windows＋Androidが候補で未確定。B台の一時停止キーは追加しない（10-06のユーザー判断）。
10. **解析費用とログ保持を整理**: high既定／必要時maxの費用・精度を比較し、needs_captureへの長時間解析を減らす。inbox／実ログの保持期間・削除対象はユーザー判断。テストログ隔離と失敗記録を保つ。
11. **将来の構成を比較**: AWDLが不安定ならUSB/usbmuxd/TCPを試すか判断し、座標互換・滞留・切断復帰・画面全体の遅延を測る。Mac内蔵／USBカメラ直接認識は精度・端点・配置・遅延を比較して採否を決める。Continuity Cameraは別の無線経路で、縮小／FPSだけを通信遅延改善と扱わない。

## 作業ログ

### 2026-10-09 最適化 第1弾（Codex 4本、Claude レビュー・検証）
- iOS 送信経路（ba2d876）: 座標を検出キューから直接送信（MainActor 待ち 約10ms→ほぼ0、Mac ハーネス）。
- iOS 認識 2周目（d1d9726）: シミュレータ明るいフレーム 12.4→5.4ms（初期 20.7ms）、空フレーム 2.2→0.6ms。bit 一致。
- Android（44476a4）: 回転を JNI 内へ（回転 約45%短縮）、エミュレータ JNI 38/38。
- Unity（977d5c6）: 剣の遅延補正（F8 で 0/20/40/60ms、既定 OFF。まず 20ms を試す）、受信フラグの競合修正。
- 統合後 main で verify 全段 PASS、parity 0 mismatch。実機確認待ち: iPhone/AQUOS の F9・60fps 維持、Unity Test Runner。

### 2026-10-08 認識の高速化（Swift / C++、結果は bit 一致）
- C++ core（a264ad7）: 明るい480×640で 12.0→5.0、25.1→10.1 ms/frame（Mac M5）。膨張・収縮を十字の反復に、std::set を bitmap に、Evidence を参照に。parity 269/269・JNI 35/35（エミュレータ）。
- iOS（289b992）: XCTest の明るいフレーム 20.7→12.4 ms（シミュレータ）。verify 全段 PASS、parity 0 mismatch。統合後の main でも parity 269/269 一致。
- いずれも Codex gpt-6.1-sol が実装、Claude がレビュー・検証。詳細は perf_core_detection.md / perf_ios_detection.md。
- 次: AQUOS で解析 fps と JNI 中央値を確認し、Android も 60fps を試す。iPhone は F9（fps自動比較 ON）で効果を確認。

### 2026-10-08 iPhone を LAN 優先・P2P 予備に
- F9 自動 A/B（Mac+iPhone, Editor）: LAN 中央値 133〜157ms / p95 約210〜220ms、P2P 166〜255ms / 350〜415ms。60fps 区間（赤 約37件/秒）は30fps区間より 30〜40ms 速い。maxQueuedFrames 1/2 は差なし。
- iPhone: LANLivenessProbe が Unity の探索応答（UDP 5007）へ0.5秒ごとに問い合わせ、1.5秒以内に応答があれば LAN、なければ P2P。トグル名を「P2P予備」に。
- iPhone は60fps要求でも約37件/秒（Debug ビルドも -O。認識が20ms台）。Swift と C++ core の bit 一致高速化を Codex に委任（perf/ios-detection, perf/core-detection）。
- 検証: verify は Tools の P2P bridge 起動待ちテスト1件のみ失敗（Codex 2本並行の負荷）、単独再実行2回 PASS。他段 PASS、新テスト testLANLivenessProbeRequiresARecentUnityReply PASS。

### 2026-10-08 F9 の誤判定除去と自動 A/B
- 10-07 夜の F9（60fps、P2P 162ms / LAN 157ms）は最小 11/19ms を含み信頼できない。原因は旧版がワールド座標で閾値0.3を判定していたこと（71595c4 で正規化座標に修正済み）と条件の違い。
- F9: 25ms未満・逆方向の応答を棄却（rejected）、左右の棒位置を学習して切替先に近い応答だけ採用。15回ごとに maxQueuedFrames 1/2 を交互にし latency-block を記録。iPhone Debug に「fps自動比較」（30秒ごとに30⇄60、保存しない）。集計は `PhoneSaber/tools/latency_report.py`。
- 検証: verify 全段 PASS、Unity は Roslyn コンパイル＋判定ロジックを .NET で単体実行（EditMode 未実行）、latency_report の unittest 2/2。

### 2026-10-07 iPhone 60fps を本番既定に、F9 結果の自動記録
- 実測（Mac+iPhone、P2P）: F9 画面→受信 中央値120ms/p95 140ms（30fps）。iPhone 撮影→送信 30fps 50ms → 60fps 37ms。
- iPhone: 「詳細設定→カメラ」に 30/60fps（既定60、保存、60非対応端末は30）。認識・UDP形式は不変。Unity: maxQueuedFrames=1（990aa4a）。F9 は20回ごとに events.log へ median/p95・赤pkt/s・経路・描画fpsを自動記録、Play停止時も記録。
- 検証: verify 全段 PASS、Unity は Roslyn コンパイルのみ。次: P2P と LAN の F9 比較（ログから自動判定）。

### 2026-10-07 遅延の計測手段と即効の改善（全機種）
- Unity: F9 遅延テスト（画面に赤い棒を左右交互→スマホで撮影→赤の受信位置が切り替わるまで。表示・カメラ・認識・Wi-Fi・受信を含む）。InputPoint を DefaultExecutionOrder(-2000)、SaberInputBridge を -1500 にし、受信の取り込み→剣の移動の順を固定（最大1フレームの遅れを除去）。
- Android: 送信中は WIFI_MODE_FULL_LOW_LATENCY ロック。端末状態行に「撮影→送信」（センサー露光時刻→JNI完了）の中央値。
- iPhone: 端末状態行に「撮影→送信」（AVCapture ホスト時刻→認識完了）の中央値。
- Windows: firewall スクリプトを Private+Public・LocalSubnet 限定に変更し、Unity の受信 Block 規則を無効化（ホットスポットが Public 扱いで受信できなかったため）。
- 認識・UDP 形式・閾値は変更なし。検証: verify 全段 PASS（Detection は件数定数 4→5 の修正後に単独再実行）、Android unit 35/35、Unity は Roslyn でコンパイルのみ（EditMode は未実行）。

### 2026-10-07 Android実機（AQUOS sense9）で送信できない問題を修正
- 症状: 剣が映っても「未検出・送信0」、解析2〜8fps。原因1: Debug APKでC++ coreが-O0になり1フレーム約300ms→送信側の180ms鮮度制限で全破棄。原因2: RotationHelperの1バイトずつのget/putで約80ms。
- 修正: core/JNIを-O2固定、回転をInt単位の一括コピーに。sense9で解析30.4fps・JNI中央値17〜33ms、Windows Unityで両色受信を確認。
- 検証: Android unit 34/34、API 37 arm64エミュレータでJNI parity 35/35（-O2でもbit一致）。

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
