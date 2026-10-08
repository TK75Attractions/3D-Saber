# ゲーム進行・譜面・採点の改善25項目

2026-10-07着手、2026-10-08実装・統合検証。検証結果は Improvement100.json / Improvement100.html を参照。曲JSON・音源・判定幅・音量・入力プロトコルは変更していない。

## G01 破損・BOM付きJSONの読み込み復旧

対象: `Assets/Scripts/Chart/ChartLoader.cs`

問題: ParseはFromJsonの例外を外へ伝播し、BOMや空白だけの入力も直接渡していた。

変更: BOM/空白を除去し、不正JSONは警告と空譜面を返す。

効果: 編集中の不完全な書き出しで選曲画面・本編の起動処理が停止しない。

検証: GameplayReliabilityTests.BrokenJsonDoesNotCrashTheSongBrowser / GameplayReliabilityTests.UnicodeBomAndSurroundingWhitespaceDoNotHideAChart

## G02 壊れたノーツだけを読み飛ばす

対象: `Assets/Scripts/Chart/ChartLoader.cs`

問題: JsonUtilityは配列中のnullを既定値ノーツへ変換し、0秒・中央の意図しないノーツを作っていた。非有限の座標も描画へ渡っていた。

変更: JSON上の明示null位置と非有限の時刻/座標を除外し、時刻0・座標0を含む有効な作者データは保持する。

効果: 1つの不正ノーツのために有効な譜面全体が失われない。

検証: GameplayReliabilityTests.NullRecordsDoNotDiscardTheOtherNotes / GameplayReliabilityTests.RemovingExplicitNullKeepsARealZeroTimeCenterNoteAndIgnoresNestedNulls

## G03 同時押しの作者順を維持

対象: `Assets/Scripts/Chart/ChartLoader.cs`

問題: List.Sortの同時刻要素の順序は安定ではなく、生成・同時押しリンクの順番が入れ替わり得た。

変更: 安定したOrderByで時刻順に並べる。

効果: 同時押しの左右・多重ノーツが書き出し時の並びを維持する。

検証: GameplayReliabilityTests.EqualTimeNotesKeepAuthorOrderAfterSorting

## G04 書き出し文字列の空白で色・方向を失わない

対象: `Assets/Scripts/Chart/ChartLoader.cs`

問題: color/direction/typeに前後空白があるとPrefab・手・フリック方向の照合に失敗した。

変更: 文字列の前後空白と大小文字を統一し、未指定は既定の値を補う。

効果: 他のエディターで作成した譜面も意図した色・方向で遊べる。

検証: GameplayReliabilityTests.WhitespaceInExportedKindsDoesNotLoseTheColorOrFlick

## G05 壊れた譜面メタデータで描画・拍表示を崩さない

対象: `Assets/Scripts/Chart/ChartLoader.cs`

問題: coordScale=0は全ノーツを中央へ潰し、不正BPM/offsetはガイド・時刻を壊した。

変更: 無効なメタデータにのみ有効な既定値を使い、拍子マップの不正・重複指定を正規化。

効果: ノーツの時刻・配置そのものを補正せず、表示可能な譜面を維持する。

検証: GameplayReliabilityTests.InvalidMetadataCannotCollapseAllNotesToTheCenter / GameplayReliabilityTests.MeterDuplicatesUseLastDefinitionWithoutMovingNotes

## G06 編集中のファイルロックで停止しない

対象: `Assets/Scripts/Chart/ChartLoader.cs`

問題: File.Exists後のReadAllTextにはロック・アクセス拒否・読み込み途中の削除への対処がなかった。

変更: IO/アクセス例外を警告と空譜面へ変換する。

効果: 外部エディターや同期中のファイルでゲーム全体が例外停止しない。

検証: GameplayReliabilityTests.AChartLockedByItsEditorDoesNotCrashTheGame

## G07 ロングの指定実長を曲末判定へ反映

対象: `Assets/Scripts/Game/GamePlayManager.cs; Assets/Scripts/Chart/ChartData.cs; Assets/Scripts/Notes/NoteSpawner.cs`

問題: 生成側はlengthMsを使うのに、本編の切り詰め・終了時刻だけ(count-1)*0.7を使い、44回ロング等を誤削除/過剰待機させていた。

変更: NoteData.LingerSecondsで実長計算を共通化する。

効果: 指定された長さのロングが曲末で消えたり、演奏後に不必要に長く待たされたりしない。

検証: GameplayReliabilityTests.ExplicitLongDurationWinsOverTheCutCountEstimate / 既存LongNoteLengthTests/LongNoteScoringIntegrationTestsの一括実行を親に依頼

## G08 選曲後に空になった譜面を成功扱いしない

対象: `Assets/Scripts/Game/GamePlayManager.cs`

問題: 選曲時の確認後にファイルが変更・削除されると、本編は空譜面でも進行して結果へ到達した。

変更: 本編読み込み後に有効ノーツを確認し、空なら選曲へ戻す。正常な開始だけ結果IDを再初期化。

効果: 未制作・読み込み失敗の曲を誤ってクリア結果にしない。

検証: 起動コルーチンの分岐をコードレビュー / 既存SongChartAvailabilityPlayTestsの一括実行を親に依頼

## G09 ロングの実カット物量を難易度推定に反映

対象: `Assets/Scripts/Chart/ChartDifficultyRater.cs`

問題: 2回ロングも44回ロングも同じ1ノーツとして密度を数え、lengthMsも無視していた。

変更: 平均密度は必要カット数とロング終端、ピーク密度は指定時間内のロング切断密度も考慮。既存係数は維持。

効果: 短時間に何十回も切る譜面の負荷が選曲の自動難易度へ反映される。作者指定displayLevelは変更しない。

検証: GameplayReliabilityTests.LongDifficultyUsesRequiredCutsAndTheAvailableTime / Python独立計算: ElDorado Easy4/Normal5/Hard9、既存テストの帯域条件内

## G10 音源読み込み失敗時に別曲を再生しない

対象: `Assets/Scripts/Game/GamePlayManager.cs`

問題: LoadAudioの全候補が失敗すると、SongPlayerに元から割り当てられたClipを残していた。

変更: ロード開始時に旧音源の所有資源を解放し、Clipを空にする。

効果: 譜面と関係のないシーン既定音源や以前の音源が流れない。

検証: LoadAudio成功/全失敗の分岐をコードレビュー

## G11 音源読み込みの永久待ちを防ぐ

対象: `Assets/Scripts/Game/GamePlayManager.cs`

問題: UnityWebRequestのタイムアウトが未設定で、同期ストレージ等の停止時に開始処理が待ち続けた。

変更: 各候補の読み込みに20秒の上限を設け、失敗後は次形式へ進む。

効果: アクセスできない音源でゲーム開始画面が戻らなくなることを防ぐ。

検証: リクエスト設定・usingによる解放・候補継続をコードレビュー

## G12 OS中断中の譜面時計を止めて再開

対象: `Assets/Scripts/Game/SongPlayer.cs; Assets/Scripts/Game/GamePlayManager.cs`

問題: 音声・アプリが中断されてもscheduledとDSP基準時計は進行扱いで、復帰時に大量MISSになる可能性があった。

変更: Pause/Resumeを追加し、開始前の残り時間・演奏中のサンプル位置を保持。本編更新も中断する。

効果: アプリの中断から同じ位置へ戻ってプレイを続けられる。

検証: SongPlayerRecoveryPlayTests.PauseBeforeStartPreservesTheRemainingLeadIn / SongPlayerRecoveryPlayTests.PauseDuringPlaybackFreezesBothAudioAndTheJudgmentClock

## G13 極短音源を観測し損ねても曲が終わる

対象: `Assets/Scripts/Game/SongPlayer.cs`

問題: 最初のSongTime読取より前に音源が終わりtimeSamples=0になると、同期待ちが永久に0を返した。

変更: 正常な同期待ちには猶予を残し、終了した音源を観測し損ねた場合はDSP時計へ復帰する。

効果: 短い音源や開始フレームの遅延で譜面とリザルトが停止しない。

検証: SongPlayerRecoveryPlayTests.ClipWhichEndsBeforeTheFirstClockReadDoesNotFreezeAtZero

## G14 音源差し替え・無効化で旧予約を解除

対象: `Assets/Scripts/Game/SongPlayer.cs`

問題: Clipのsetterは音源だけを差し替えてscheduledと時刻を残し、コンポーネント無効化時も音が継続した。

変更: 異なるClipへの切替とOnDisableでStopして予約と同期状態を解除する。

効果: 別の音源が前曲の途中時刻から始まる・無効なプレイヤーの音だけ鳴る症状を防ぐ。

検証: SongPlayerRecoveryPlayTests.ReplacingTheClipCancelsTheOldSongSchedule / SongPlayerRecoveryPlayTests.DisablingSongPlayerAlsoStopsItsSoundAndSchedule

## G15 前回の誤フリック・手・EARLY/LATEを持ち越さない

対象: `Assets/Scripts/Game/ScoreManager.cs`

問題: ResetはLastWasWrongFlick/LastCutHand/LastErrorValid等を消さず、直接RegisterHitも前ノーツの属性を使った。

変更: 判定属性のリセットを共通化し、ノーツ由来の採点と属性なしの直接採点を分離。

効果: リトライ直後や直接採点で、誤フリック表示・金音・振動・時間誤差が前回のまま出ない。

検証: GameplayReliabilityTests.ReplayResetClearsWrongFlickAndThePreviousHand / GameplayReliabilityTests.DirectJudgmentsDoNotReuseThePreviousCutSfxOrHapticContext / GameplayReliabilityTests.ResetAlsoClearsThePreviousTapTimingError

## G16 採点の再接続で欠落・二重購読を防ぐ

対象: `Assets/Scripts/Game/ScoreManager.cs; Assets/Scripts/Notes/CuttableNote.cs`

問題: Bindは生成イベントだけに購読し既に画面にいるノーツを取りこぼし、旧ノーツのOnCut購読は外さなかった。

変更: 表示中ノーツへ再接続し、追跡集合とOnRetiredで重複・古い購読を解除。

効果: 途中のUI/採点再接続で切れたのに得点しない、古いノーツが別のプレイに加点する症状を防ぐ。

検証: GameplayReliabilityTests.BindingAfterSpawnStillScoresTheVisibleNoteOnce / GameplayReliabilityTests.DetachingScoreStopsTheOldVisibleNotesFromScoring

## G17 Spawner単体の破棄で表示ノーツを残さない

対象: `Assets/Scripts/Notes/NoteSpawner.cs`

問題: OnDestroyはpoolKeysだけを破棄し、非プールのliveNotesが外部noteRootに残る場合があった。

変更: 生成方式に関係なく所有するliveNotesを片付ける。

効果: 表示の再構築や練習終了後に古いノーツ・判定対象が残らない。

検証: GameplayReliabilityTests.RemovingOnlyTheSpawnerAlsoRemovesUnpooledNotes / SongPlayerRecoveryPlayTests.DestroyingOnlyASpawnerRemovesItsUnpooledNotesFromAnExternalRoot

## G18 時間切れロングの残数を消す

対象: `Assets/Scripts/Notes/CuttableNote.cs`

問題: MarkMissでノーツは無効になるが、切断残数ラベルは表示されたままだった。

変更: MISS確定時に残数ラベルも非表示にする。

効果: もう切れないノーツの数字に誘導されず、次のノーツへ移れる。

検証: GameplayReliabilityTests.TimedOutLongHidesTheRemainingCount

## G19 非表示ノーツを遅延入力で切らない

対象: `Assets/Scripts/Notes/CuttableNote.cs`

問題: CutCoreはIsCut/IsMissedだけを確認し、プール待機や無効化されたノーツへの直接コールを受理した。

変更: 無効・非アクティブなノーツへのカットを受け付けない。

効果: 画面から消えたノーツの遅延コールで得点や破片が発生しない。

検証: GameplayReliabilityTests.DisabledNotesCannotBeCutByALateCallback

## G20 不正ランキング行・精度の復旧

対象: `Assets/Scripts/Game/HighScoreStore.cs`

問題: Insertにnull/順不同の行を渡すと停止・誤挿入し、Loadのaccuracyは範囲外やNaNを保持していた。

変更: 有効なスコア行を安定ソートし、入力負点を拒否、精度を有限な0..1へ揃える。

効果: 一部破損した保存データがあってもランキングを表示でき、500%等の精度を出さない。

検証: GameplayReliabilityTests.BadRankingRowsDoNotBreakInsertionOrShowImpossibleAccuracy

## G21 編集用結果オブジェクトからランキングを独立

対象: `Assets/Scripts/Game/HighScoreStore.cs`

問題: Insertは呼び出し元のHighScoreEntry参照をそのまま保存していた。

変更: 挿入時にスナップショットを作る。

効果: 次の結果を作るため同じEntryを書き換えても、確定済み順位の点数・精度が変わらない。

検証: GameplayReliabilityTests.InsertingAnEditableResultTakesAnIndependentSnapshot

## G22 成績保存の失敗でもリザルトへ進む

対象: `Assets/Scripts/Game/HighScoreStore.cs; Assets/Scripts/Game/SongAchievementStore.cs`

問題: PlayerPrefs保存例外が本編終了コルーチンや結果UI作成へ伝播していた。

変更: 保存エラーを警告と失敗値で返し、画面進行を継続する。

効果: 保存容量/アクセス問題があっても今回の成績を見られる。保存成功とは報告しない。

検証: Recordの例外経路と呼び出し側をコードレビュー; OS容量不足の実機注入は未実施

## G23 難易度表記の違いで実績を分裂させない

対象: `Assets/Scripts/Game/SongAchievementStore.cs`

問題: 実績キーは難易度文字列をそのまま連結し、Hard/hardが別集計だった。

変更: 既存の標準キーEasy/Normal/Hardを保ちながら大小文字・前後空白を同じキーへ解決。

効果: 同じHARDの達成人数が表記ゆれで別々に見えない。

検証: GameplayReliabilityTests.AchievementDifficultyAliasesUseTheExistingStandardKey

## G24 過去結果の再表示による実績人数の水増し防止

対象: `Assets/Scripts/Game/SongAchievementStore.cs`

問題: lastRunIdだけの確認ではrun1→run2→run1でrun1を再加算した。

変更: 直近128回のIDを保持し、旧形式lastRunIdも履歴へ引き継ぐ。

効果: 結果画面の再生成や再通知が別の達成プレイとして数えられない。

検証: GameplayReliabilityTests.RedisplayingAnEarlierRunDoesNotCountItAsAnotherPlayer / GameplayReliabilityTests.ExistingAchievementCountsAndLastRunSurviveTheNewHistoryFormat

## G25 保存設定の破損でノーツが停止しない

対象: `Assets/Scripts/Game/GameSession.cs`

問題: 速度設定のNaNはMathf.Clampを通り、保存済み判定オフセットはgetterで範囲確認されなかった。

変更: 速度の非有限値は既定に戻し、判定オフセットは既存許容範囲で読み取る。

効果: 通常の設定値を変えず、破損した値だけで譜面全体が消える・数日ずれる症状を防ぐ。

検証: GameplayReliabilityTests.CorruptPreferencesDoNotFreezeNotesOrMoveTheChartOutOfRange

## 未検証・補足

初回統合EditModeの担当2件は、JsonUtilityのnull変換と未実行MonoBehaviourの破棄イベントが原因と判明。明示null検出と適切なライフサイクル試験へ修正し、再検証待ち。実OS中断・音声デバイス・同期ストレージタイムアウト・PlayerPrefs容量不足・実機セーバープレイは未確認。成績合計overflow/パス検証/終了時の全生成確認は補強変更で別件数に数えていない。
