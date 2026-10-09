# 最適化7ブランチの統合検証（2026-10-09）

`origin/main` (`35afe44c2d5f90cdde3ea6c62bf8c8b9dc369cd4`) から `opt/integration` を作成し、指定順に `--no-ff` マージした。競合は0。mainのworking treeは編集せず、pushしていない。認識のthreshold/ranking/定数・浮動小数点加算順・UDP bytes/5005/5006/5007・F7/F8/F9・既存scene/prefab/metaは変更していない。

| 統合元 | 最終コミット | 統合コミット |
| --- | --- | --- |
| opt/core-r4 | c9b4ba8 | 90063b5 |
| opt/ios-frame-alloc | f00d84c | a863413 |
| opt/ios-sender-syscalls | 843437b | 2c8c0f9 |
| opt/ios-detect3 | b3757f5 | 3dfae0f |
| opt/hud-gc | 4eaffba | ad54c56 |
| opt/unity-ui-gc | 1db8db9 | c6dccb1 |
| opt/unity-player-settings | 36357be | 4547e6d |

個別の変更前後の数値・検証・baseline失敗は元コミットと各reportに保持した。ios-detect3/unity-ui-gc/unity-player-settings reportの「未コミット」は前回の時点の記録であり、今回はoperatorの明示指示で既知失敗を記載してコミットした。

## 統合版の検証

- full verify `20261009-223940`: XCTest PASS（276 PASS・skip1、total277）、Detection PASS、formal lossless40/40 PASS、iOS Release PASS、diff PASS、Unity各stage NOT_RUN。**Tools FAIL**。
- Toolsは385件実行（skip1）後、`TrackingPipelineE2ETests.setUpClass` の録画ハーネスで `appendRejected-raw-jump-8`、ERROR1。初回はタイムアウトではない。単独retry1/retry2はどちらもswiftc180秒タイムアウト・0件実行。retry1で残った自分のswift-frontend56028をTERMした。テスト期待値・timeout・production recorderは変更していない。
- core `test`（core＋Python4件）PASS。`parity` はPNG269（fixture35＋inbox original234）＋合成27＋FrameProcessor541遷移、不一致0。formal40件の期待値失敗0。
- Android unit35/35 PASS・assembleDebug PASS。psparity emulator JNI38/38 PASS（25.994秒）。エミュレータ終了。
- Unityコピー `scratchpad/buildproj`: EditMode1692/1692 PASS、skip0、118.208秒。最初のUnity起動はrsync完了前だったためTERMして採用せず、rsync完了を確認して再実行した。Toolsとfixturesもコピーした。
- PlayMode初回は41件完了後、native Enlighten `BaseWorker::ExecuteCommands` / `MultithreadCpuWorkerCommon::CommandThreadFunction` のsegv/double fault、exit255、XMLなし。前回unity-ui-gcと同じnative stack。既知Calibration2件に加え、異常負荷下の`FocusLossStopsAudioAndClearsTrial`がFAIL。
- 回復グループ1は33/33 PASS。FocusLossは1.9秒でPASS（初回FAIL時277.8秒）。グループ2は5件PASS後、同じnative crash・XMLなし。グループ3は17/17 PASS。残り回復検証は進行中。

## 時間の参考値

`DetectionCoreTests.testOfflineBrightFrameTimingBaseline`（Debug Simulator、12回）:

| 入力 | avg ms | median ms | max ms |
| --- | --- | --- | --- |
| empty | 0.862 | 0.863 | 0.904 |
| bright | 8.790 | 8.645 | 10.724 |
| bright profiled | 8.673 | 8.691 | — |

stage avg ms: total8.740、scan1.095、morphology1.139、component/scoring3.728、traversal3.282、shape0.725、evidence1.014、endpoint0.005、line proposal0.683、line score1.298、selection0.192。native `swiftc -O`、bright PNG480×640・200回: bright avg18.493/median12.078/p9037.228/max92.564 ms、empty avg1.780/median0.797 ms。候補計1800（9/frame）。

複数の別workerによるXcode/Simulator起動・他Unity batch待機と重なり、Mac load averageは複数回700〜900超となった。これらの値を速度・energy・実機60fps達成の根拠に使わない。前回のbright中央値2.962→2.685msとも負荷条件が違い、直接比較しない。

## リスクと引継ぎ

総合検証のTools gateとPlayMode全227件の確認が未達であり、統合ブランチは現時点で検証完了ではない。録画ハーネスERROR、コンパイルtimeout、native Unity crashは別々に記録し、成功した部分の結果で隠さない。認識結果のbit一致はparityの対象全件で確認済み。実機速度・消費電力・heap/FPSは未測定。

ログは `PhoneSaber/.verify-logs/phone-saber/20261009-223940/` と `PhoneSaber/.verify-logs/integration-native/`。Unity・Android・単独再実行は指定scratchpadの `integration-*` に保持した。
