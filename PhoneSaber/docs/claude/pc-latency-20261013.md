# PC Player の遅延・コマ揺れ（2026-10-13）

結論: 本番の60 Hzプロジェクターでは、**fullscreen window・vsync=1・target=-1**を起動時と各シーンで揃える案を推奨する。Windowsの先行キューは既存の1を維持。MacのMetalではこのキュー設定は無効。Gameの900 fpsを60 fpsへ揃えると最速の入力取り込み間隔は悪化し得るため、表示専用の遅い再サンプリングと組で比較する。数字の手入力は不要で、10秒ごとの `event=perf` とF8に残る。

今回は入力順序・表示同期・フレーム上限を自動変更していない。体感が変わる変更は提案のみという作業ルールに従い、計測と明示呼び出し用APIを実装した。**実機での遅延削減、900 fpsの8時間熱試験は未測定**。認識、UDP `x1,y1,x2,y2`、corpus、Scene/Prefab/.meta/ProjectSettings、SaberInputBridge、予測・フィルタ、PlayerSmokeは未変更。commit / pushなし。

## 重大度順の所見

| 重大度 | 根拠の場所 | 具体的な失敗シナリオ | 対応 |
|---|---|---|---|
| 高・実測済みの環境問題 | `PhoneSaber/mac/saber-watchdog.sh:177` | 対象Macの低電力モードでglassが30更新/秒。GameのCPUループが900 fpsでもglassは追いつかない | 既存警告を維持し、各再起動でも電源情報を採取してPlayerへ渡す。解除は運営操作。全Macで必ず30 Hzになるという一般則ではない |
| 高・設定提案 | `Assets/Scenes/Game.unity:2795`、`Assets/Scripts/Game/UnlockFrameRate.cs:12` | メニューはvsync=1、Gameで0/-1となり約900 fps。固定60 Hzの投影では破れ・不均一なフレーム選択が生じ得る。fanless機で不要なCPU/GPU負荷を継続 | Gameのvsync=1を提案。Scenes・ゲームコードは変更していない |
| 中・取り込み順序の制約 | `InputPoint.cs:598`、`Assets/Scripts/Saber/SaberInputBridge.cs:373` | InputPoint.Update後の新着は次のUpdateまで待ち、Bridge.Updateで確定した表示は描画直前に公開値だけ更新しても変わらない | `InputPoint.ResampleLatestInput()` を追加。自動呼び出しなし。消費者の表示更新も遅くする案は下記 |
| 中・計測の実不具合、修正済み | `PhoneSaberPerfLog.cs:58` | Menuの5秒＋Gameの5秒をGame名で報告して、Game単体のfpsと誤認。途中のvsync/target変更でも同じ | シーン・描画条件ごとに窓を開始し直す。旧実装で3ケースFAIL、修正後PASS |
| 中・診断の不足 | `ProjectSettings/ProjectSettings.asset:156` | `enableFrameTimingStats: 0` のRelease PlayerでGPU計測がなく、30 fpsをGPUかOSか断定できない | `gpu-samples=0`、時間=`unknown`。Player設定のFrame Timing Statsを計測用buildでONにする提案 |

表のPhoneSaber入力ファイルは `Assets/Scripts/Managers/Inputsystem/Input/` 配下。低電力解除やvsync設定変更による期待値と、確認済みの不具合修正は区別する。

## 全体の遅延予算

2026-10-07〜13に渡された実測値。段階ごとの独立した同時計測ではないため、中央値を足して厳密な総中央値とはしない。

| 区間 | 時間 | 根拠・重複・残り |
|---|---:|---|
| 手の動き→次のカメラ露光 | 位相待ち0〜16.7 ms（60 fps）、0〜33.3 ms（30 fps） | 理想的な周期モデル。露光長・readout・ISPは別途、機種依存 |
| iPhone capture→send | 約37 ms @60 fps / 約50 ms @30 fps | 実測。検出約3 msはこの中に含まれ、もう一度足さない。Androidの値は未測定 |
| phone→PC受信（LAN / P2P fallback） | 未分離 | 一方向の絶対時刻なしの4要素payloadからは分離できない。PC受信間隔は自動ログ。P2P bridgeの転送も含む |
| F9画面→Unity受信 | 中央値約135 ms | 実測の合成区間。元のPC表示、カメラの露光/readout/ISP、検出1フレームなどを含む。capture→sendの37 msやPC表示を別に足さない |
| Unity受信→描画直前（初めて使ったフレーム） | 中央値約7 ms @120 fps | 実測。GPU実行・present・scanout・プロジェクター処理は含まない |
| GPU/Metal/DXGI→表示開始 | GPU実行＋待ち、未測定 | 下表の同期・キュー・合成。CPUのUpdate fpsとは別 |
| 表示開始→画面内の位置 | scanout 0〜16.7 ms @60 Hz / 0〜8.3 ms @120 Hz / 0〜33.3 ms @30 Hz | ラスター走査の目安。実機のパネル/プロジェクター内部方式は異なり得る |
| プロジェクター画像処理・画素応答 | 未測定 | Game/低遅延モード、補間・ノイズ低減・台形補正で変わる。設定画面と機種仕様で確認 |

F9の同じフィードバック経路の「135→受信→描画直前」は粗く約142 ms。そこから最後のpresent/glassはさらに加わる。この142 msを手振り→glassの測定値と呼ばない。残る大きな項目はカメラ/phone/元画面を含む合成区間であり、PC側の数msだけで全体が解決する証拠はない。

## フレームがglassに届く道

Mac: InputPoint.Update→Bridge.Updateのpose→Unityのrender submission→Metal GPU完了→drawable present→WindowServer/表示ハードウェア→scanout→画素。
Windows: 同じ入力/pose→D3D GPU→DXGI swapchain present→DWM合成またはIndependent Flip→scanout→画素。

`T=1000/表示Hz`。以下は**追加段階の目安**であり、全段が独立して足される保証はない。キューが空なら0、深くなれば複数T、deadlineを逃せばさらにT。

| 状態・段階 | 追加遅延の目安（60 / 120 / 30 Hz） | 取り除く・減らす設定と限界 |
|---|---|---|
| vsync=1、固定リフレッシュ | 次の同期まで0〜T、ランダム位相平均 T/2 = 8.3 / 4.2 / 16.7 ms | vsync=0は同期待ちを外せるが、投影のtear-free保証を失う。vsync=1は本番推奨 |
| vsync=2 | 有効更新30 / 60 / 15 fps。同期間隔2T | 1へ。60 Hzで30 fps制限を外し、更新周期33.3→16.7 ms |
| target=30、vsync=0 | Updateの次回まで0〜33.3 ms | target=-1または適切な上限。vsync>0ではdesktopのtargetは無視される |
| driver先行キューが1段余分 | 余分な段の仕事量。飽和・表示律速時に約T = 16.7 / 8.3 / 33.3 msになり得る | Windows D3D11/12 `maxQueuedFrames=1`。既存Overlay:46で実施済み。数値は実際のキュー深さやGPU時間次第 |
| Mac Metal drawable / GPU queue | 未測定、0〜複数Tになり得る | UnityのmaxQueuedFramesでは削れない。900 fpsを避け、GPUがdisplay deadlineに収まるようにする。nativeのdrawable count/late presentをC#値から推定しない |
| Mac fullscreenMode=1（FullScreenWindow） | fullscreen自体の定額msはない | Apple siliconでopaque/RGBの全画面Metal layerなど条件が揃うとdirect-to-display。Unityのモード1だけでは成立の証拠にならない |
| macOS compositor経由 | 同期/合成のdeadline次第で0〜T以上。固定で16.7 msとは言えない | 全画面、重ねるウィンドウ/通知を避ける。direct-to-displayが成立すれば合成仕事を省ける。Metal System Trace等の実機確認が必要 |
| 60 Hz / ProMotion 120 Hz | 更新周期16.7 / 最大120 Hzなら8.3 ms | 外部投影先のHzが優先。ProMotionは可変で、内蔵120 Hzは外部60 Hzを速くしない。nominal Hzはglass実測ではない |
| 対象MacのLow Power Mode | 実測30更新/秒なら周期33.3 ms。60 Hz比で位相平均+8.3 ms、中央scanout目安+8.3 ms | AC給電＋低電力解除を運営が行う。アプリのfps変更で解除できない。Apple資料は全機種30 Hzを保証していない |
| Windows borderless＋flip model＋Independent Flip | DWM追加合成を省ける。Unity資料の旧経路比1フレーム = 16.7 / 8.3 / 33.3 ms削減目安 | `useFlipModelSwapchain=1`は既存ProjectSettings:98。FullScreenWindow維持、D3D11/12、ウィンドウ重ねを避ける。成立はPresentMonで確認 |
| Windows DWM composed / BitBlt | 転送・合成とdeadlineによる追加、典型比較で約1T、実値不明 | flip modelと全画面で改善し得る。DWMを永久に無効化しない。表示倍率の100%化で必ず1T消える根拠はない |
| Windows exclusive fullscreen | 固定の削減量なし | Unity 6000.3 D3D11ではexclusiveがBitBltへfallbackし、waitable-object経路を失う。既定として推奨しない。D3D12/driver差は実測 |
| G-Sync/VRR対応の出力 | 範囲内なら次の固定周期までの待ちを減らせる。定額msの保証なし | VRR出力に限り、上限未満cap＋tear防止用driver vsync。60 Hz projectorにVRR対応があると仮定しない |

参照: [Unity 6000.3 maxQueuedFrames](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/QualitySettings-maxQueuedFrames.html)、[targetFrameRate](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Application-targetFrameRate.html)、[flip model](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/PlayerSettings-useFlipModelSwapchain.html)、[Microsoft Independent Flip](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model)、[Apple Metal全画面](https://developer.apple.com/documentation/metal/managing-your-game-window-for-metal-in-macos)、[Mac電源モード](https://support.apple.com/en-us/101613)、[NVIDIA VRRと上限](https://nvidia.custhelp.com/app/answers/detail/a_id/4958/related/1)。表のmsは周期からの推定で、資料の測定値ではない。

## 起動時の推奨設定と900 fps

本番案はMac/Windowsともモード1、vsync=1、target=-1。Windows queue=1は既存。OS、解像度、表示倍率、Game Mode、GPU driverを自動変更しない。`UnlockFrameRate.Awake`が起動時の同期を上書きするため、PhoneSaber起動時に1を代入するだけでは不足する。シーン移動でも揃えること。

fanless Macで8時間の900 fpsは推奨しない。60 Hzに対し15倍のフレーム計算をし、表示されないフレームも発熱へ回る。**熱スロットリングが実際に起きたとは未確認**。900→120 fpsで取り込み待ちの理想平均は約0.56→4.17 ms（+3.61 ms）、900→180なら+2.22 ms、900→240なら+1.53 ms。これらはphone/network/GPU/glassを含まない。

- tear-freeの本番: 60 Hzならvsync=1で60 fps、120 Hzなら120 fps。30 Hzになったらcapの工夫ではなく電源・出力設定を直す。
- vsync OFFを比較する研究用: 60 Hzでは120 fpsを第一候補、180 fpsを比較。120 Hzでは240 fps。固定refreshの整数倍でも位相は同期されず、破れ・judderの保証はない。ソフト上限自体の揺れもある。
- VRR専用案: 60/120/144 Hzの上限に対し57/117/141 fpsを比較開始値にし、driver側vsyncを併用。Unity vsync=1ではtarget上限が無視されるので別経路が必要。本番projector案とは別。

60 Hz・phone60 fpsで描画だけ900 fpsにしても入力更新は最大60回/秒。900 fps維持とcap案を同じ会場・投影先で、開始直後／1時間後／8時間後に比較する。perfは自動保存されるので、所有者にfpsやmsの入力を頼まない。熱の断定はCPU/GPUの継続時間と実機のthermal/presentデータが揃ってから。

## Samplingと表示専用の接続案

最新版をUpdate頭で読む場合、render時の座標年齢は「その時点で選んだpacketの受信からの年齢＋Update→renderのD」。present/glassにはGPU/present/scanoutの待ちがさらに加わる。phone60 fpsで到着周期S≈16.7 msなら、位相が独立で定常というモデルのUpdate時の最新packet年齢は平均S/2≈8.3 ms。phone30なら16.7 ms。同期している位相、バースト、停止ではこの仮定は崩れる。

| PC条件 | 次のUpdateまでの機会待ち | 最新値のrender時年齢（phone60、単純モデル） | 遅い再取得の上限効果 |
|---|---:|---:|---|
| vsync ON 60 Hz | 0〜16.7 ms | 約8.3 ms＋D | Update後から再取得までに新着があった時だけ、最大約16.7 ms。典型のDが小さければ効果も小さい |
| vsync ON 30 Hz | 0〜33.3 ms | 約8.3 ms＋D | 最大約33.3 ms。途中packetは最新値に上書きされ、未使用packetの待ちを統計に混ぜない |
| vsync OFF、target=60 / 30 | 同じく0〜16.7 / 33.3 ms | 約8.3 ms＋D | 上限は同様だがpresentの位相・tearは別問題 |
| vsync OFF、約900 fps | 0〜1.11 ms | 約8.3 ms＋小さいD | 取り込みの改善幅は小さい。OSがglass30 Hzに抑えることは防げない |

「packetが来たら次のUpdateまでT/2」と「使った最新packetの年齢S/2」は異なる。PC30/phone60では途中packetを使わないため、使ったpacketだけの受信→初回使用は全packet待ちの中央値ではない。実測7 ms@120 fpsはこのモデルの校正材料であり、60/30 Hzへ比例倍して実測扱いしない。

実装した `InputPoint.ResampleLatestInput()` はmain threadでだけ呼ぶ。受信済みの最新値を既存のlock・変換・位置補正で公開し、新着なしなら座標・有効端点・最終受信時刻・IMUを再更新しない。途中packetをキュー化せず、4要素payloadを変更しない。通常Updateは従来どおりIMU fallbackを1回だけ実行。

**ゲーム側で提案する接続**（今回は未実装）:

1. `SaberInputBridge.cs:373`のUpdateで行うゲーム判定・予測・フィルタの更新を維持し、表示poseを適用する処理と分ける。
2. 表示用consumerのLateUpdate、または `Application.onBeforeRender`（計測のorder=10000より前）で、まず `ip.ResampleLatestInput()`、その後 `LocalStickRawA/B` または `LocalStickRawA2/B2` を読み、表示poseを適用する。InputPointだけ再取得してBridgeを動かさなければ表示は古いまま。
3. 同じframeに予測・フィルタ・trail・音・判定・Swing履歴を二度進めない。既存Update全体の二重呼び出しは禁止。表示と判定のpose差がownerに見える変更なので、採用前に別レビュー・PlayMode・実機比較が必要。

[Unity onBeforeRender](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Application-onBeforeRender.html)は非VRを含む全platformの描画前callback。ただしそこから先のGPU/presentを消すものではない。今回のcallbackは記録のみ。

## 自動ログ・F8

`event=perf` の既存字段は維持し、赤青それぞれ次を追加（ms、n、median、p95）。

- `*-receive-apply`: socket ReceiveFrom完了→InputPointの座標公開。
- `*-receive-first-use`: 同じ公開値のLocalPosition / LocalStickRaw端点getterを消費者が初めて読むまで。一packet一回。未使用・上書きされたpacketを使用扱いにしない。
- `*-receive-first-render`: その読み出し値が初めて描画前callbackへ達した時刻。GPU/present/glassは含まない。
- `*-coord-age-render`: 描画前での、最後に読み出された座標の受信時刻からの年齢。renderフレームで重み付け。新着がない間は年齢が増える。
- `*-timing-dropped`: 各リスト上限16384件を超えた時の記録不足。10秒900 fpsでは通常上限内。空の統計は0 msではなく `unknown`。

getter計測は実際のconsumerの読み出しを測るが、描画した証明ではない。デバッグ表示が同じgetterを読んだ場合も使用に含まれ、マウスfallback・IMU表示・consumerの無効化では最後のUDP座標と実poseが異なる場合がある。F9の光学測定と同じものではない。

`fullscreen` / `graphics-api` / `queued-frames` / `gpu-samples` / `gpu-ms-mean/max` / `limiter` / `low-power-at-launch` / `glass-hz=unknown` も記録。Frame Timing StatsがONで取れたGPU値を使い、瞬間最大値だけでGPU律速と決めない。

`limiter=vsync/target` は観測fpsが設定上限±10%に一致する場合。上限未達かつ起動時低電力ONなら `os-low-power-suspected`、平均GPU時間がframe時間の90%以上なら `gpu-suspected`、それ以外はCPU/OS不明。10%/90%は**診断表示用の目安**であり、認識のthreshold・ゲーム挙動には使用しない。GPU、CPU、OSが同時に原因になる場合もある。

OS/compositorがglassだけ30 Hzに抑え、Update900 fpsのままならUnity fpsだけでは検出できない。`Screen.currentResolution`もnominal値であり、glass制限を断定しない。Macのdirect-to-display、WindowsのIndependent Flip、VRRの実成立もこのC# APIからは見えない。必要な追加計測はnativeのpresented-time/Metal trace、Windows PresentMon（GPU/OSによる自動採取）。今回その収集経路は未実装。所有者に数値入力を求めて補完しない。

F8にfps、律速判定（推定を明記）、受信→描画直前の赤青median/p95を1項目で表示する。シーン変更後は新たな10秒窓を待つ。既存のevents.logローテーションを使う。

## ゲーム所有者が変更する場所（提案のみ）

| ファイル:行 | 変更する値・操作 | 期待効果・条件 |
|---|---|---|
| `Assets/Scenes/Game.unity:2796` | EditorでUnlockFrameRateのvSyncCountを0→1。target=-1維持 | 60 Hz投影でtear-freeの同期。900→60 fps、電力減。現状より絶対遅延が下がる保証はない |
| `Assets/Scripts/Game/UnlockFrameRate.cs:8` | defaultを1へ、起動・各sceneの同期方針を統一 | 新規componentも同じ設定。Sceneのserialized 0はdefault変更だけでは変わらない |
| `Assets/Scripts/Managers/GManager.cs:36` | lockFrameRate30をfalse維持、別の初期化でtarget=30にしない | 偶発的な33.3 msループを防ぐ。現状falseなので無条件の改善量は0 |
| `Assets/Scripts/Saber/SaberInputBridge.cs:373` | 上記の表示専用late samplingを接続 | 新着があるframeで最大1周期ぶんを削る可能性。二重処理回避・判定との差のレビュー必須 |
| `ProjectSettings/ProjectSettings.asset:111` | FullScreenWindow (1)維持 | Mac full screen / Windows flip向け。exclusiveへ機械的に変えない |
| `ProjectSettings/ProjectSettings.asset:98` | useFlipModelSwapchain=1維持 | Windows旧合成経路比で1表示frame削減の可能性。既に1なので今回追加削減0 |
| `ProjectSettings/ProjectSettings.asset:156` | Player > Other Settings > Frame Timing Statsを計測用buildでON | GPU判定の根拠を自動記録。直接の遅延削減ではない。計測負荷を通常buildとも比較 |
| `ProjectSettings/QualitySettings.asset:292` / `:331` | Standalone既定quality5のvsync=1を維持、他qualityにも方針を適用 | メニューの設定は既に1。Gameの上書きだけが残る。quality0/1の0へ切替時は要注意 |

PhoneSaberOperatorOverlay:46の `maxQueuedFrames=1` は変更不要。Mac改善としてこの値を0へ下げる案は採らない。

## Launcher

Macは各launch/relaunchでpmsetの低電力と給電元をログ化し、`PHONESABER_LOW_POWER_MODE=0/1/unknown` をそのPlayerへ渡す。起動中に電源モードが変わった場合は「起動時の状態」のまま。pmsetの設定変更はしない。

Windowsは毎run、active power-plan、GameBarのAutoGameModeEnabled（レジストリの手掛かり、実際の適用証明ではない）、AC/Battery Saver、**全active出力**のpixels/Hz/primary/logical boundsをログ化。60 Hz未満や省電力に日本語通知。ゲームの出力先は別途F8と照合する。logical boundsはDPI awarenessの影響を受けるため、各monitorの正確な倍率の測定とは呼ばない。電源プラン、Game Mode、倍率、出力Hzは自動変更しない。API確認に失敗しても起動を継続する。

Windowsの省電力は [GetSystemPowerStatus](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getsystempowerstatus) の情報。Game Modeは設定 > ゲーム > ゲームモードで運営が確認し、投影側は60 Hz以上・低遅延モード・補間OFFを候補にする。倍率を無条件100%に変える根拠はない。

## 検証結果と運営コマンド

この作業環境ではUnity Editor/Playerを起動しない指示のため、実Playerの動作・GPU/present計測・実機入力・Unity Test Runnerは未実行。公式verifyも運営側が実行する。

実行済み:

- `python3 PhoneSaber/tools/check_pc_latency.py --before-ref HEAD`: 実Unity 6000.3.9f1 DLLでruntime PhoneSaber*.cs＋InputPoint＋SwingEventStreamを4構成（Editor Mac/Windows・Player Mac/Windows）compile **PASS**。ゲーム側UdpImuBridge/GameSession/SaberInputBridgeだけhost stub。Unity APIをstubで通したcompileではない。Editor拡張/ゲーム全体のbuildではない。
- 同commandのstandalone NUnit: 旧HEADの実PerfLogで窓切替3件 **0 PASS / 3 FAIL（期待どおり）**、現行 **17 PASS / 0 FAIL**。現行はno-new sample identity、未読packet、初回使用/描画の重複、窓reset、容量上限、GPU/OS/vsync/target判定を検証。純粋な計測hot path 10000回は **0 bytes allocated**。Unity nativeのInputPoint lifecycle/resamplingを実行したものではない。
- Windows launcher内の実P/Invoke宣言をMono/Roslynでcompile **PASS**。Windows native API・PowerShell 5.1実行は未実行。
- `python3 -B -m unittest -v PhoneSaber.tools.test_ops_watchdog`: **12件、7 PASS / 5 SKIP**。Macの偽open＋実process監視5件とWindows source contract2件。Windows native統合5件はOS条件でskip。
- `bash -n PhoneSaber/mac/saber-watchdog.sh`: **PASS**。
- `python3 -B -m unittest -v PhoneSaber.tools.test_unity_editmode_results PhoneSaber.tools.test_unity_playmode_batches`: EditMode結果validatorの15件PASS、PlayMode側importが `PYTHONPATH` 不足で1 ERROR。`PYTHONPATH=PhoneSaber/tools python3 -B -m unittest -v test_unity_playmode_batches` で正しいimport経路から再実行し、11件PASS。Unityテスト自体の実行ではない。
- `git diff --check`: **PASS**。

最初に使った既存 `/private/tmp/phonesaber-ops/compile_api.py` は現行PlayerSmokeのGameSession/SaberInputBridge依存をstubしておらずcompile失敗。新しい再現可能なcheckerでそれらゲーム依存だけを明示stubして4構成を再検証した。最初のNUnit試行は期待値の計算違いとAssert引数のboxingを測ったテスト側の誤りで2件FAILし、テストを訂正して再実行した。

運営がrepo rootで実行するコマンド:

```bash
python3 PhoneSaber/tools/check_pc_latency.py --before-ref HEAD
PhoneSaber/tools/unity_copy.sh sync
PhoneSaber/tools/unity_copy.sh editmode '^(InputPoint|PhoneSaber)'
PhoneSaber/tools/unity_copy.sh build-mac
PhoneSaber/tools/unity_copy.sh smoke
```

`build-mac` はCOPYでbuild→smoke→旧appを退避して差し替える既存コマンド。smokeは自身のUDPを使うので、Editor Play/既存Playerを停止してから。smokeのCPUfpsはbatchmodeなので実投影の合否にしない。Unity_copyのEditModeにはtools側standalone NUnitは含まれず、上のcheckerで別に実行する。

WindowsはコピーしたUnity projectのTools > PhoneSaber > Build > Windows Player（`PhoneSaberPlayerBuild.BuildWindows`）でbuildし、A/B batから実投影先で起動。`unity_copy.sh` にbuild-windows subcommandはないため存在しないコマンドは案内しない。Windowsで `python -B -m unittest -v PhoneSaber.tools.test_ops_watchdog` も実行する。

PlayMode全体はCOPY上で既存 `unity_playmode_batches.py`、正式な総合verifyは運営の `PhoneSaber/tools/verify_phone_saber.sh`。今回認識の実コードもformal corpusも変更していないが、公式verifyのPASSを代用・主張しない。

実機はLAN/P2P各経路（Mac/iPhone）とWindows/Androidで、メニュー→Game→メニューを操作し、各条件10秒以上置く。F8とevents.logにmedian/p95/判定が自動で出る。二人同時、停止・再開、低電力ON/OFF、projector60 Hz、8時間の負荷を確認。late sampling採用時は新着なし／赤だけ／青だけ／両色／IMU fallback／予測とfilter各状態で表示と判定を確認する。

## 残りを削る優先順

1. 対象Macの30 Hz化を解除、投影出力60 Hz以上・低遅延モード確認。期待: 更新周期33.3→16.7 ms、平均位相待ち約8.3 ms削減。パネル処理は機種次第。
2. 900 fpsを止め、全シーンでtear-freeのpacingを統一。期待: 電力・熱とjudder改善。入力待ち自体は悪化し得るので3と比較。8時間の持続性は未確認。
3. 表示用late resampling。期待: 新着がUpdate後に来た時に最大16.7 ms@60 Hz、33.3 ms@30 Hz。毎frame1周期必ず速くなるとは言わない。
4. WindowsのIndependent Flip成立、不要な重なり・描画キューを避ける。旧合成経路なら約1T削減候補。flip=1/queue=1は既に設定済み。
5. 合成135 ms区間を光学/nativeデータで分離。phone60 fpsの37 ms対30 fpsの50 msという約13 ms差は既知、PCだけで消せない。capture/ISP/露光・F9元画面・送信の整理は別仕事。recognitionは触らない。
6. GPU律速が実測された時だけ解像度/描画負荷/URP設定をゲーム側で検討。今回の900 fpsからGPU飽和を決めつけない。Metalのnative queue/present調整、VRR/driver調整も実機根拠と別レビューが必要。
