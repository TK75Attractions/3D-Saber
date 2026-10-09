# 当日 Player 設定の監査（macOS / Windows）

対象: origin/main `35afe44`、Unity 6000.3.9f1。Unity Editor が開いている本 checkout は変更せず、`opt/unity-player-settings` と scratchpad/buildproj2 で確認した。認識、UDP、ゲームコード、Scene、Prefab、既存 meta は変更しない。

## 採用する変更

通常の `Log` だけ Stack Trace Logging を ScriptOnly → None にする。本文、ログ出力先、Warning / Error / Assert / Exception のスタックは保持する。通常の接続・起動・受信率ログで呼び出し元の探索が不要になる。F8/F9/F7 とイベントログの形式は変更しない。PlayerSettings の設定なので Editor の通常ログにも適用される（調査時は Console の Stack Trace Logging で戻せる）。

Unity はスタック解決を高コストと説明し、種類ごとの制限を推奨している: [Stack trace logging](https://docs.unity3d.com/6000.3/Documentation/Manual/stack-trace.html)。これは通常ログ1回につき managed stack 抽出1回 → 0回の削減であり、ログの文字列生成やファイル I/O 自体を消す変更ではない。

## 変更しない設定と理由

| 項目 | 現状 / 判断 |
| --- | --- |
| Incremental GC | `gcIncremental: 1`。長い一括 GC 停止を避ける設定が既に有効。無効化しない。常駐 heap や割当量を減らすものではない。 |
| Scripting Backend | Editor API で Standalone は Mono2x、managed stripping は Disabled、Graphics Jobs は false と確認（Android だけ IL2CPP 明示）。IL2CPP は AOT、strip、プロセス起動、反射に影響する。実機の性能比較と当日の機能確認なしで変更しない。浮動小数点の同一性も新 backend では別検証が必要。 |
| API Compatibility | `apiCompatibilityLevel: 6` / platform override なし。API は NET_Standard_2_0 と表示（NET_Standard の deprecated alias、現行 .NET Standard profile）。API の切替は互換性とビルド構成を変えるので性能目的では変更しない。 |
| Managed Stripping | override なし。Mono は Disabled と実測。Reflection、JsonUtility、Prefab からの型参照へのリスクに対して、CPU 削減の根拠がない。強化しない。 |
| Graphics Jobs | platform override なし。Editor API は false。Unity の experimental 機能でクラッシュの可能性があり、Camera.Render のボトルネックをまだ実測していない。変更しない。 |
| Multithreaded Rendering | `m_MTRendering: 1`。既に有効。変更しない。 |
| vSync / FPS | Standalone default quality は Ultra（5）、vSync=1。本編 Game / Experiment の UnlockFrameRate.Awake は vSync=0・targetFrameRate=-1、GManager の既定 lockFrameRate30=false。Title などの pacing を一括変更すると tearing、電力、画面遷移の動作が変わる。scene を変更せず保持。 |
| Max Queued Frames | PhoneSaberOperatorOverlay が起動時に1へ設定。F9 は1/2を比較し終了時1へ戻す。Unity の [maxQueuedFrames API](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/QualitySettings-maxQueuedFrames.html) は D3D11/12・Vulkan のみ対応、Metal では無視。Mac の遅延改善として数えない。Windows での既存設定は保持。 |
| Metal / DX API | Auto Graphics API は Mac=true / Metal、Windows=true / Direct3D12, Direct3D11 と API で確認。Metal API Validation=1 は Editor の検証であり、通常 Player の高速化にならないため変更しない。Windows の useFlipModelSwapchain=1 は既に有効。DX12 強制や Graphics Jobs 変更は当日 Windows GPU 未検証。 |
| Metal Framebuffer Only | `metalFramebufferOnly: 0`。画面読み取りを禁止する設定のため、URP の最終出力、capture、将来の診断との互換性を実測せず変更しない。 |
| Retina / resolution | macRetinaSupport=1、native resolution=1、1920×1080 既定。画質・座標写像・会場の表示条件が変わるため変更しない。 |
| Fullscreen | `fullscreenMode: 1` = FullScreenWindow。Mac/Windows 共通の既存運用を維持。Windows exclusive mode への変更は会場 display と focus 切替の確認が必要。 |
| Run In Background | `runInBackground: 1`、overlay も true を保証。受信・運営操作を継続するため保持。sleepTimeout=NeverSleep も既存コード。 |
| Player Log | `usePlayerLog: 1` を保持。障害時の本文と Warning/Error のスタックは必要。完全停止は不採用。Warning のスタック省略も診断を弱めるため不採用。 |
| Development Build | PhoneSaberPlayerBuild.BuildMac / BuildWindows は BuildOptions.None を指定。Profiler 接続、Deep Profiling、Script Debugging を追加しない。Build Profile / UI のチェック状態だけに依存しない。 |
| Frame Timing Stats | `enableFrameTimingStats: 0`。通常 Player で常時計測の追加コストを導入しない。計測専用 tool は別タスク。 |

仕様の確認: [macOS Player settings](https://docs.unity3d.com/6000.3/Documentation/Manual/PlayerSettings-macOS.html)、[Incremental GC](https://docs.unity3d.com/6000.3/Documentation/Manual/performance-incremental-garbage-collection.html)。

## 計測・検証

### 公式 verify

`bash PhoneSaber/tools/verify_phone_saber.sh` を worktree root で実行。Detection PASS、formal lossless 40/40 PASS、Tools PASS、iOS Release PASS、diff check PASS。Unity は linked worktree のため NOT_RUN（専用 copy で別途実行する）。XCTest は P2PTransportTests.testFallbackWithoutLANCountsTheDroppedCoordinate の XCTAssertTrue（行633/634）で FAIL、275 tests / 1 skipped / 1 failed test（2 assertions）。今回の設定変更前からの origin/main コードの失敗で、他 worker の baseline 記録でも同じ失敗。iOS コードや gate は変更しない。全項目 PASS の commit gate を満たさない。

ログ: scratchpad/player-settings-verify.log と worktree の PhoneSaber/.verify-logs/phone-saber/20261009-171510/。

### 無効として扱う初回計測

初回に ps の comm 列の短縮表示を実行パスと誤認し、他 worker の Unity batch（PID3224）を見落とした。buildproj2 の初期コピーと before/after ビルドが重複実行になったため、数値を性能の根拠として採用しない。headless Player も UDP 5005/5006 の bind 競合と nographics での ArgumentNullException があり、無効。プロセス検査を ps の args 列の完全な実行パスによる一致に修正し、自分の実行を止め、他 worker の batch 終了後にコピー・計測を再実行する。他 worker のプロセスは停止していない。

### 再計測

他 worker の終了後、Library を再コピーしてから逐次実行した。再計測は他の Unity batch と重ならないことを args 完全パスで継続確認した。

| 項目 | before | after |
| --- | ---: | ---: |
| BuildReport size / bundle 論理容量 | 236,201,622 bytes | 236,201,622 bytes（差0） |
| BuildReport time | 18.066477 s | 6.408463 s |
| BuildReport errors | 0 | 0 |
| Editor API: 通常 Log stack trace | ScriptOnly | None |
| その他4種類の stack trace | ScriptOnly | ScriptOnly |
| 10秒 headless run、停止後 exit | 0 | 0 |
| user / system CPU seconds（起動・終了込み） | 8.118440 / 0.809298 | 5.996535 / 1.843246 |
| headless log 容量 | 3,604 bytes | 3,604 bytes |

Build time は二回目の cache が暖まっており、設定の速度改善を示さない。headless は両方で Null graphics device の shader=null（SaberInputBridge.EnsureBladeOutline）という同じ ArgumentNullException があり、GPU・正常描画・遅延の測定としては不成立。ポート競合は再計測では0件、指定10秒の後に SIGTERM で両方正常終了した。CPU は単発かつ無描画で起動・終了を含むため改善率を出さない。ログ容量も減っておらず、通常ログ stack trace 削減の実運用 CPU 効果は未実測。

EditMode: **1692/1692 PASS**、failed/skipped/inconclusive=0、32.0076785秒。XML は scratchpad/player-settings-C-edit.xml、ログは player-settings-C-edit.log。before/after build と smoke は player-settings-C-{before,after}-{build.log,player.log,run.json}。

自己レビュー: 差分は ProjectSettings.asset の m_StackTraceTypes 1行と本書のみ。認識、UDP、診断本文、staff controls、ゲームコード、Scene、Prefab、既存 meta に差分はない。通常 Log の呼出元だけを省略する設定候補は、公式 verify の P2P XCTest gate 未達のため **未commitのまま**。Windows build / 会場 GPU / input-to-photon / 実機 frame pacing は未測定。
