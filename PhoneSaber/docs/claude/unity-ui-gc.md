# unity-ui-gc（2026-10-09）

`Assets/Scripts/UI`、`Notes`、`Game` のUpdate/LateUpdateとそこから呼ばれる表示更新を確認。変更はUIの2スクリプトのみ。

- `SongSelectChartPreview.StatusText`：再生中の表示秒（従来どおりCeiling）、Window.Duration、Mutedが変わるときだけ文字列を生成。SongTimeは従来どおり呼ぶたびに読み取り、DSP同期の副作用を維持。NaN/Infinity/負値の文字列変換は既存Durationに任せる。通常の同一表示秒の呼び出しは書式化2回＋文字列連結1回から0回になる（ソース上の呼出数、heap bytesやFPSの実測ではない）。
- `SongSelectCorridor.Update`：捕捉変数tを使うRemoveAll predicateを、後ろからのRemoveAtに変更。残るwaveTimesの順序、比較式、表示計算の順序は維持。毎frameのclosure/delegate生成箇所1→0。
- `SongSelectCorridor.Fire`：不変のColor[4]をprivate static readonlyへ。downbeatごとの配列生成1→0。4色・barNumberの選択式は同じ。

触らなかった範囲:
- ScoreHUD/GameHUDSkin/TitleTutorialPrompt：別のhud-gcタスクで扱うため、このbranchには含めない。
- SongSelectRubyText：サイズ・font・dirty変更時だけForceMeshUpdateする既存guardがある。
- DifficultyTileItem：選択アニメ中だけ更新する既存guard。DifficultyRibbonItem、SongWheelView、SongRowFX、UIHoverEffect、ResultReveal等の色・サイズ・位置は連続アニメであり、更新が必要。通常のGraphic/Text/fontSizeのsetterにはUnity/TMP自身の同値guardもある。
- TitleSceneSkinのgradientは構築時だけ。GameStartCountdown、SongSelectSkinの表示秒は既に変更時だけ書式化。
- NotesのNoteVisualsは発光strengthの既存guardがある。SlicePieceDecay、FollowTransformWorldOffset、SimultaneousNoteLinkの動き・線形状は毎frameの見た目に必要。
- GamePlayManagerは判定時計・入力・舞台演出を駆動し、イベント時のオブジェクト生成はper-frameの固定UI更新ではない。ゲームロジックは変更しない。
- 構築時のLINQ/new配列、イベント時の文字列、音源/譜面読込、描画mesh生成はこの小さなGC修正の対象外。

シーン・prefab・既存meta・UDP・F7/F8/F9は変更しない。追加scriptはなし。

## 検証と引き継ぎ

- EditMode：1692/1692 PASS。
- 全PlayModeを実行したが、133件終了（130 PASS/3 FAIL）後の画面遷移中にEnlighten native workerでsegv/double fault、exit255。最終XMLは出力されなかった。
- 未完了/失敗クラスを新しいUnityで再実行。BLE環境修正後、BLEはPASS。キャリブレーション2件は再FAIL。その後の選曲中に同じnative workerで2回目のsegv/double fault、exit255。
- 2実行の終了ログを統合すると210件完了（208 PASS/2 FAIL）。未完了17件を別プロセスで実行し16 PASS/1 FAIL。StageReactiveEffectsのimpact>0.1が0だったが、他のUnity計測終了後の単独再実行で1/1 PASS。
- 全227件のunique test名を元のfull XMLと照合し、未実行0件。最新結果を統合すると225 PASS/2 FAIL。ただし単一のgreen全PlayMode実行ではない。
- 残る2件は`CalibrationCountdownPlayTests.BackRestoresSelectedSongDifficultyAndPreview`と`SaveAndReturnRestoresSelectedSongDifficultyAndPreview`。WaitForSelectionの20秒待ち後にIsReady=false。hud-gcとorigin/mainの比較でも同じ2件が失敗している。期待値や待ち時間、ゲームコードは変更しない。
- テストコピーのToolsが初回不足していたためコピー追加。初回BLEは選ばれたPythonにbleakがなく失敗した。コピーにローカルvenv（既にbleakが入ったPython3.12のsystem-site-packagesを使用）を用意し、importとBLEテストPASSを確認。worktreeのvenvはignored。
- 別担当のbuildproj2 Unity計測が途中で起動していることを検出。こちらの実行終了後、別計測の終了を確認して舞台反応単独再試験を実行した。別プロセスは停止・変更していない。
- `git diff --check`と自己レビューPASS。認識・UDP・スタッフ操作・Game/Notes・既存metaに差分なし。

全green条件を満たさないため**未コミット**、変更と本メモを保持。native crashの原因は未確定。視覚の実機確認、heap bytes、FPSは未測定。

ログはoperator scratchpadの`unity-ui-gc-{edit,play,resume,final,stage-retry}.{log,xml}`。play/resumeはcrashでXMLなし。`unity-ui-gc-completed.json`は各testの最新結果、`unity-ui-gc-remaining-filter.txt`/`unity-ui-gc-final-filter.txt`は再試験対象。
