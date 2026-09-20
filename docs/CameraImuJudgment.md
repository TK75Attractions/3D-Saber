# Task 1: Camera + IMU ゲーム判定

## 対象と既存の構造

開発本体 `C:/Users/shike/dev/3D-Saber` の `c2b5d66` を基準に実装。作業場所の `Automation/checkout` は古い検証コピーだったため、最新本体から `Automation/task1-judgment-checkout` を作成した。

`NoteSpawner` が譜面の時刻・座標・色・方向・必要回数を読み、XYを固定してZ方向へ移動させる。通常の早め窓は101.25ms、遅め窓は202.5ms。ロングは滞留時間を遅め窓へ追加する。

従来の `SaberCutJudge` は速度を満たす棒の進入と退出を確認する。Cameraの2端点を使う線分モードと中点を使う点モードがある。`CuttableNote` は逆方向を拒否し、その他の方向違いは `ScoreManager` で降格する。従来経路には `Swing8DirectionLogger` の方向ヒントもある。

`ScoreManager` の段階は Perfect / Great / Good / Bad / Miss。タップは時刻誤差、ロングは最終達成率で評価する。赤ノーツは `SaberHand.Right`、青は `Left`、金・無色は `Any`。

## 新しい流れ

1. `GamePlayManager.Start` が `CameraImuJudgment` と必要なアダプターを用意する。既存シーンの参照・通信設定は変更しない。
2. `GameplaySwingAdapter` が左右と受信時刻を持つ `GameplaySwing` を判定へ渡す。IMU direction は渡さない。
3. 未解決Swingを固定64枠に保持。左右・sequence・XIAO時刻の組を256枠の履歴で重複排除する。300msを過ぎたものや未来の受信時刻は不採用。
4. `SaberInputBridge` が適用した新規Camera入力だけを、変換・中点クランプ後の2端点と時刻・色付きで記録する。マウスや描画用補間点は記録しない。赤・青それぞれ固定128枠のリングを使用し、通常0.75秒保持する。
5. Camera受信時刻から調整可能な遅延量を引く。Swing前後60msと重なる軌跡だけを照合する。100msを超えるCameraサンプル間隔は補間しない。
6. 前後のブレード線分が掃く四辺形を2つの三角形と外周線分で近似し、ノーツXYとの交差を確認する。入力端点の順序反転を補正する。Z位置はSwingの曲時刻から再計算する。
7. 複数候補なら譜面時刻がSwing時刻に最も近いノーツを選び、1つのSwingを1回のカットに消費する。左右は独立。ロングも各カットに別のSwingが必要。
8. 方向指定時はSwing後60msまでのCamera軌跡が届くのを待ち、連続サンプルの中点移動で方向を求める。既存 `CutDirectionHelper.ToVector/Matches` と同じ8方向を使用。許容角度は既定30度。逆方向も拒否せず1段階降格する。
9. `CuttableNote.LastCutSongTime` を採点へ渡し、カメラ待ちの時間をタイミング誤差に加算しない。ノーツ再利用時にこの値をリセットする。方向降格とロング達成率の評価は既存 `ScoreManager` に任せる。
10. `NoteSpawner` は元の判定窓内にある未解決Swingがあるノーツだけ、Miss確定を最大pending期限まで待つ。判定窓自体は延長しない。

曲停止・時刻ジャンプ・譜面リセット・入力セッションリセット・モード/左右設定の変更時は、古い未解決入力を破棄する。通常ループは既存 `GamePlayManager.Update` に統合し、独立した判定Updateは追加しない。

## Inspectorとfallback

本編の `GamePlayManager` に `CameraImuJudgment` を事前に追加して保存すると、起動前に設定を調整できる。未配置なら起動時に自動追加される。

| 設定 | 初期値 / 意味 |
|---|---|
| mode | Auto |
| CameraOnly | 従来の判定を使う |
| Auto | 左右が判明した有効なSwingを初めて受けた側だけ新判定へ移行 |
| ImuAndCamera | Cameraのみでは切れない検証用の明示モード。マウスは従来入力を維持 |
| leftCamera / rightCamera | Blue / Red（既存の棒1=赤・右、棒2=青・左に合わせた変更可能な初期値） |
| cameraLatencyCompensationMs | 100ms |
| pendingSwingWindowMs | 300ms |
| swingCameraToleranceMs | 前後60ms。方向判定では後ろ側まで観測する |
| maxCameraSampleGapMs | 100ms |
| cameraHistorySeconds | 0.75秒 |
| directionToleranceDegrees | 30度 |
| debugJudgment | OFF |
| readLegacyStream | ON |
| legacyStreamSide | Unknown |

AutoはIMUが0台・sideがUnknownならカメラのみで従来どおり遊べる。片側だけ有効なら反対側は従来入力を維持する。振っていない間に自動で必須条件が外れないよう、無入力時間によるfallbackは行わない。切断時は入力側の `ResetSession` または明示的なCameraOnly切り替えで戻す。

左右のmappingを両方同じ色にした場合は曖昧なため従来入力を維持する。物理sideはカメラ選択に使い、ノーツ色の判定は既存の棒のhand設定に従う。`LastCutterHand` の意味も既存どおり。

## Task 3との接続

現時点の `SwingEvent` にはphysical sideがない。`Unknown` を方向名から推測しない。現行受信イベントは読み取り購読のみで、既定Unknownは新判定に使わない。

左右が分かるTask 3のゲーム側接続から、メインスレッドで以下を呼ぶ。

```csharp
adapter.readLegacyStream = false;
adapter.Publish(swingEvent, PhysicalSaberSide.Left); // またはRight
// デバイス/セッションの切り替え・切断時
adapter.ResetSession();
```

別の入力形式なら `GameplaySwing(sequence, side, receiveMonotonicSeconds, xiaoTimestampUs)` をPublishできる。受信時刻は `SwingMonotonicClock` と同じOS monotonic秒が必要。XIAOの時計は同期が保証されていないため採点時計に使わない。

既存の単一ストリームで片側だけ検証する場合に限り `legacyStreamSide` を実際の物理sideへ明示設定できる。2台をこの設定で区別することはできない。

変更していない責務: BLE、bridge、firmware、UDP parser/format、`UdpImuBridge`、`SwingEventStream`、`InputPoint`、iPhone、Bonjour、LED、Haptic。Task 3のtransportファイルとの直接競合はない。新アダプターへの呼び出し部分だけ担当間で合わせる。

## 変更ファイル

- 追加: `Assets/Scripts/Saber/CameraImuJudgment.cs`、`CameraSaberHistory.cs`、`GameplaySwingAdapter.cs`（各metaを含む）
- 変更: `Assets/Scripts/Saber/SaberInputBridge.cs`、`SaberCutJudge.cs`
- 変更: `Assets/Scripts/Notes/NoteSpawner.cs`、`CuttableNote.cs`
- 変更: `Assets/Scripts/Game/GamePlayManager.cs`、`ScoreManager.cs`
- 追加: `Assets/Tests/Editor/CameraImuJudgmentTests.cs`、`Assets/Tests/PlayMode/CameraImuLifecyclePlayTests.cs`（各metaを含む）、本書

## デバッグ

`debugJudgment` で成功・重複・期限切れなどの決定時だけログを出す。成功ログにsequence、physical side、色、受信時刻、XIAO時刻、Swing曲時刻、Camera補正時刻・受信からのage、補正量、Swingとの時間差、note instance ID、位置・swept・方向結果、元/最終tier、理由を出す。`LastDecision` でも最後の決定を取得できる。毎フレーム同じ失敗ログを出さない。

## 検証結果

Unity 6000.3.9f1 の Test Runner をbatchmodeで実行。実機測定は未実施。

| 検証 | 結果 |
|---|---|
| 新規EditMode `CameraImuJudgmentTests` 最終実行 | **48 / 48成功** |
| 既存EditMode（全体実行に含む） | **1,119 / 1,119成功** |
| 関連PlayMode（新規ライフサイクル1件を含む） | **31 / 31成功** |
| 変更差分の空白検査 | 成功 |
| transport / firmware / InputPoint / Hapticの変更確認 | 変更なし |

全EditMode実行は1,167件中1,166件成功で、新規テストの時刻比較が `0.99999999999999822` と `1` の厳密一致を要求していた1件だけ失敗した。比較を許容誤差付きへ修正し、新規48件すべてを再実行して成功した。さらに診断ログに未解決時のCamera時刻・age・位置結果を追加した後の48件が上記の最終結果。全体実行前の初回新規34件では、EditModeが通常MonoBehaviourのOnDisableを自動実行しない前提のテストを修正し、実際のOnDisableは新規PlayModeテストで成功を確認した。

対象内容: Swingなし、位置違い、100ms遅延、点/棒のフレーム間飛び越し、期限切れ、重複packet、同一note、近接noteの選択、8方向、全4段階の降格、IMU directionの無視、左右独立・入れ替え、Unknown、Camera欠損、Camera-only、配送待ち時間、サンプル間隔、方向許容角、ロング部分達成、再利用時の時刻初期化、停止・無効化、GamePlayManager経由の呼び出し順。

PlayModeでは `CameraImuLifecyclePlayTests`、`SaberCutSmokeTest`、`PendingCutLifecyclePlayTests`、`NotePerformancePlayTests`、`GameSceneSmokeTest`、`MenuToGameInputTest`、`CalibrationFlowPlayTests`、`UdpSwingEventPlayTests`、`SongSelectNoteMenuPlayTests` を実行した。UDPのローカル受信も含むが、BLE/iPhone/実物セイバーでの計測を代用しない。

結果XMLは作業場所の `Outputs/Task1GameJudgment/editmode-targeted-final.xml`、`editmode-full-v2.xml`、`playmode-regression-final.xml`。実装は開発本体へ反映済み。適用前の対象6ファイルは同じ場所の `Before` に保存し、既存の未コミット変更は保持した。古い `Automation/checkout` に一時的に加えたTask 1の変更は取り除いた。

## 実機で次に確認すること

- Task 3の左右を接続し、赤/青・物理左右・棒のhand設定の対応を確認する。
- 遅延補正70 / 85 / 100 / 115 / 130msを比較し、入力頻度とばらつきを記録する。
- 速い振り、8方向、左右同時、金ノーツ、ロング連打、IMUなし、Camera停止、再接続を確認する。
- 一振り一カットの新方式では、同じ物理セイバーによる同時刻複数ノーツも一度には切らない。文化祭の譜面にその配置がある場合は、許可するグループ規則を別途決める。従来CameraOnlyにはこの制限を加えていない。
- Camera packetには撮影時刻がないため、既存InputPointが公開するUnityフレーム時刻からの推定。撮影時刻の厳密同期ではなく、約1フレームの受け渡し揺れと固定遅延モデルの限界がある。
- sweptは軽量な四辺形近似で、端点が大きく回転する場合や遮蔽復帰の座標ジャンプは実機で評価する。描画の未来予測は追加していない。
