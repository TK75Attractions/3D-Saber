# Player / Editor 技術監査（2026-10-14）

- 対象: Assets/Scripts の runtime C# 全206件を静的走査。認識・UDP形式・formal corpus・scene/prefab/meta・ProjectSettings は変更なし。commit/push なし。
- 修正: PhoneSaberPlayerSmoke.cs:87 の Error/Assert/Exception をthread-safeに集計。許可リストは空。旧実装は Error を出しても配布gateを通過した。
- 拡張: Title → SongSelect → Game(14秒) → Result → Gameのライブ判定調整(8秒) → SongSelect → Title。両色UDP・端点・両刃の描画mesh・実HitCount・遷移を必須化。
- 既定の待機合計は約47秒＋ロード時間、shell上限75秒。判定・採点の条件を変更せず、Cut/RegisterHitの直接呼出しなし。
- Resultは途中集計のUI確認。保存は専用hiscore_PhoneSaberSmoke_normalへ隔離して復元。調整はDiscardAndExit。完走の精算・達成・日次順位保存は未確認。
- PhoneSaberPerfLog.cs:145 が今回のperf行をPlayerログへ複写。unity_copy.sh:83 が全sceneのfpsを出力し、欠落も失敗にする。
- smoke-window は窓を開く。open -g -n -W、1280×720、非batch。通常smokeはopen -g -j -n -W経由。今回はどちらも実アプリを起動していない。

重大度順の残存差・リスク（通常動作の変更は提案のみ）:

- 中: Assets/Scripts/Managers/Inputsystem/Input/UdpImuBridge.cs:413 → ImuBleBridgeLauncher.cs:25。PlayerのdataPath親はrepoではなく.app / exe側。autoStartBleBridge=1でもTools/mac_ble_udp_bridge.py不在でIMU自動接続できずCamera-onlyになる。最小案は外部script絶対パスoverride＋launcherから注入。Windowsはpython3/bleakの別配備も必要。未実装。
- 中: PhoneSaberTriageReceiverLauncher.cs:25。配布Macでは.app/PhoneSaber/...を探索し、診断自動受信不可。外部launcherの明示指定を提案。P2PBridgeProcess.cs:71は既存overrideで解決するが、直接.app起動はLAN-only。WindowsのBonjour/P2PはOS guardにより非対応（意図された差）。
- 中・未確定: Assets/Scripts/Notes/NoteVisuals.cs:437 の_EMISSION、LongNoteCountStyle.cs:49 のOUTLINE_ON/UNDERLAY_ON、UI/GameStartCountdown.cs:246 のUNDERLAY。runtime variantがstripされた場合は発光/縁取りだけ消えて例外ゼロになり得る。URP unused-variant stripはON。保持variantと実画像は未検証、対象variantの保持を提案のみ。
- 中: Assets/Tests/Editor/PhoneSaberBuildShaderTests.cs:36 は全AssetsのMaterialを包含根拠にする。未使用Materialでも合格するため、enabled sceneの依存＋Resources等へ限定する案。今回未修正。名前包含だけではvariantを証明しない。
- 既修正の重大障害: Assets/Scripts/Saber/SaberRig.cs:120、Notes/BarLineSpawner.cs:201 はUnlit不在でnew Material(null)。現GraphicsSettingsにはURP/Unlit GUID 650dd952...が存在。LitはGamePlayManager.cs:523、UI/TitleStartNote.cs:31等でも動的検索。再ビルド後の実包含は未確認。
- 情報: Assets/Scripts/Game/UnlockFrameRate.cs:12 はvsyncを切り、GManager.cs:36には30fps設定もある。Editor表示fpsとPlayerの実窓fpsは一致しない。SaberTracker.cs:25はdeltaTime、曲時計はSongPlayerのDSP。低fps/長いframeでの操作感は未検証。
- 情報: Game/CalibrationController.cs:302 はfocus喪失で練習を止めるが、通常GameはrunInBackground=1で続く。SongPlayer.cs:130はOS pauseを扱う。非focus/復帰/timeScale=0の実Player試験は未実施。ScreenTransition.cs:111のQuitはEditorでは終了しない。
- 情報: Game/GameSession.cs:64、PhoneSaberDisplaySync.cs:17等の同名PlayerPrefsでも、現MacはEditor(com.DefaultCompany.ServTechSlash)とPlayer(bundle com.DefaultCompany.2D-URP)で保存先が異なる。WindowsもUnityEditor/Playerのregistryは別。Editorで合わせた補正・vsyncがPlayerへ移るとは限らない。[Unity PlayerPrefs](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/PlayerPrefs.html)
- 情報: InputPoint.cs:305 / SaberInputBridge.cs:87のFREEZE診断はReleaseでは除外。P2Pのsession prebuild/再compile cleanupもEditorのみ。通常受信/表示の処理は共通。
- 静的確認: Resources内のshader/font/SFX参照は存在。歌曲はStreamingAssetsで読み、Path.CombineとUri.AbsoluteUriを使用。数値入力はInvariantCulture。Mac/Windows Player compileにUnityEditor DLLなしで成功。repo相対の必須ゲームasset、exe隣への無条件保存、Editor限定型の漏出は見つからなかった（native/import/codec動作は未保証）。

実行した検証:

- `python3 -B PhoneSaber/tools/check_player_smoke.py --before-ref HEAD`: Unity6000.3.9f1実DLLで全206件×Mac/Windows Player/Editorの4構成PASS、PhoneSaber Editor群と変更Editor testのcompile PASS。native stubなし。
- 同コマンドのstandalone NUnit: 旧smoke 6 PASS / 8 FAIL（回帰再現）、現行14 PASS / 0 FAIL。実smokeの制御・ログ・保存復元を実行。Unity native/UDP送受信/切断はhost代替で、実動作成功とは扱わない。
- `python3 -B PhoneSaber/tools/check_pc_latency.py`: 4構成＋既存テストcompile PASS、Windows PInvoke宣言compile PASS、standalone NUnit 34 PASS / 0 FAIL。新smoke依存に合わせhost stubのみ更新。
- `PYTHONPATH=PhoneSaber/tools python3 -B -m unittest test_unity_player_smoke test_unity_copy_failures -v`: 13件PASS。旧shellへ旧成功ログを与える回帰1件FAIL（期待どおり）。openは代替しGUIなし。
- `bash -n PhoneSaber/tools/unity_copy.sh` / `git diff --check`: PASS。ログは `/private/tmp/phonesaber-playeraudit/`。

未検証:

- Unity Editor/Player起動、EditMode/PlayMode runner、Mac/Windows build、実UDP・両色cut・調整復帰・実fps、shader variant/音声/8時間耐久、公式verify。sandbox制約とownerのGUI禁止により未実施。
- operator実行対象: `PhoneSaber/tools/unity_copy.sh editmode`、`PhoneSaber/tools/unity_copy.sh build-mac`、`PhoneSaber/tools/unity_copy.sh smoke`。`smoke-window`は窓を開くためownerの作業外でのみ対象。
- Unity.app/Contents/PlaybackEnginesにはMacStandaloneSupportのみ。WindowsStandaloneSupportなしでBuildWindowsは現環境ではビルド不可。モジュール導入後のMono版cross-buildは可能、Windows IL2CPPはWindows toolchainが必要。[Unity backend](https://docs.unity.com/en-us/engine/6000.3/manual/programming-environment/scripting-backends/intro)
- Windows PC未確認項目: `_Data/StreamingAssets/Songs`同梱、窓ありsmokeのError/両色cut/Result/調整復帰・各scene fps、UDP 5005–5007 firewall、音声codec、focus復帰、保存先、LAN discovery、watchdog heartbeat更新。窓のfpsはプロジェクターの実表示Hzや入力遅延の測定ではない。
