# UI改善25項目

各項目は実装済み。Unityでの統合検証結果は親エージェントのレポートを参照。テスト追加そのものは改善件数に数えていない。

## U01 難易度別譜面だけの曲を選曲に掲載

- 元の問題: chart.jsonが必須で、chart_hard.jsonなどだけを保存した曲が一覧から落ちる。
- 変更: 標準3難易度のいずれかのファイルも曲フォルダーの登録条件に含め、遊べる譜面の検査を継続。
- 利用者への効果: HARDだけ制作した曲もゲームから選べる。
- 主なファイル: `Assets/Scripts/UI/SongSelectController.cs`
- 検証: DifficultyOnlySongIsListedAndSelectsPlayableHard

## U02 曲切替時に遊べる難易度へ合わせる

- 元の問題: 前の曲の難易度を引き継いだ先が未制作だと、開始ボタンが無効になり理由を探す必要がある。
- 変更: 曲が変わる時のみ、近い制作済み難易度へ切り替える。同距離なら易しい側を優先。
- 利用者への効果: HARDだけの製作中なども選曲直後に開始できる。
- 主なファイル: `Assets/Scripts/UI/SongSelectController.cs`
- 検証: DifficultyOnlySongIsListedAndSelectsPlayableHard / SongChartAvailabilityTests

## U03 上下キーで未制作の難易度を飛ばす

- 元の問題: 相対的な難易度送りが空の難易度で止まり、遊べない状態になる。
- 変更: 相対送りで制作済みの難易度だけを循環。数字キーなどの明示選択は従来の拒否動作を維持。
- 利用者への効果: 矢印で遊べる難易度を比較しやすい。
- 主なファイル: `Assets/Scripts/UI/SongSelectController.cs`
- 検証: RelativeDifficultyNavigationSkipsUnmadeChartBothDirections

## U04 同じ曲の再選択で試聴を中断しない

- 元の問題: Selectが同じインデックスでも試聴のキャンセルと再ロードを行う。
- 変更: 同じ曲のSelectは何も変更せず、明示的な再試聴は専用操作に分離。
- 利用者への効果: 同じ盤への操作が音切れ・待ち直しを起こさない。
- 主なファイル: `Assets/Scripts/UI/SongSelectController.cs`
- 検証: ReSelectingCurrentSongDoesNotNotifyOrRestartPreview

## U05 曲一覧の再構築で重複行を除去

- 元の問題: Populateを再実行すると以前生成したボタンが残り、行が二重になる。
- 変更: 自分が生成した行を破棄して作り直し、曲IDで現在の選択位置を保持。装飾の子は消さない。
- 利用者への効果: 一覧再読込後も同じ曲に戻り、ボタンが増殖しない。
- 主なファイル: `Assets/Scripts/UI/SongSelectController.cs`
- 検証: RepopulateReplacesOwnedRowsWithoutRemovingOtherChildren

## U06 キーボード操作を常時案内

- 元の問題: 矢印・1/2/3・Enter・Escapeの操作が選曲画面から分からない。
- 変更: 盤を隠さないヘッダー下の空間に短い操作一覧を表示。
- 利用者への効果: セーバー以外の操作方法を初見で確認できる。
- 主なファイル: `Assets/Scripts/UI/SongSelectSkin.cs`
- 検証: コード・配置レビュー。Unity描画確認は親の統合検証

## U07 曲数と一覧内の現在位置を表示

- 元の問題: 円形に循環する盤では全体の曲数や現在位置が分からない。
- 変更: 左下の盤と実績パネルの間に「現在 / 全曲数」を表示。
- 利用者への効果: 全曲を探したかどうか判断しやすい。
- 主なファイル: `Assets/Scripts/UI/SongSelectSkin.cs`
- 検証: コード・配置レビュー。Unity描画確認は親の統合検証

## U08 譜面がない時に復旧手順を表示

- 元の問題: 遊べる曲がゼロだと盤がない画面だけが残る。
- 変更: 曲なしメッセージと、ノーツ入り譜面を保存し選曲を開き直す案内を表示。
- 利用者への効果: 読み込みの故障と未制作を見分け、次の作業が分かる。
- 主なファイル: `Assets/Scripts/UI/SongSelectSkin.cs`
- 検証: コード・配置レビュー。Unity描画確認は親の統合検証

## U09 未制作難易度を言葉で示す

- 元の問題: レベルが横棒の無効ボタンだけで、未制作なのか不明。
- 変更: 遊べない難易度名の位置に「未制作」を表示。
- 利用者への効果: 押せない難易度の理由がその場で分かる。
- 主なファイル: `Assets/Scripts/UI/SongSelectSkin.cs`
- 検証: SongChartAvailabilityTests / コード・配置レビュー

## U10 BPM・ノーツ数・譜面時間を選曲で比較

- 元の問題: レベル表示だけではテンポや譜面のボリュームを確認できない。
- 変更: 読み取り専用集計でBPM、総ノーツ数、最後のロング終端までの譜面時間を表示。
- 利用者への効果: 曲の速さと所要負荷を開始前に比較できる。
- 主なファイル: `Assets/Scripts/UI/SongChartInsights.cs`
- 検証: ChartInsightsPreserveOrderAndCountLongDurationAndSimultaneousGroups

## U11 ロング・フリック・同時押しの構成を案内

- 元の問題: 選択中の譜面がどの操作を多く要求するか開始前に分からない。
- 変更: ロング本数、フリック数、同時押しの組数を別行で表示。
- 利用者への効果: 苦手な操作の練習曲を選びやすい。
- 主なファイル: `Assets/Scripts/UI/SongChartInsights.cs`
- 検証: ChartInsightsPreserveOrderAndCountLongDurationAndSimultaneousGroups

## U12 試聴の準備・再生・終了・失敗を見える化

- 元の問題: 無音が選曲待ち、ロード、終了、音源不在のどれか分からない。
- 変更: 試聴状態を明示し、再生中は抜粋の経過時間と長さを表示。
- 利用者への効果: 無音の原因と試聴の進み具合を判断できる。
- 主なファイル: `Assets/Scripts/UI/SongSelectChartPreview.cs`
- 検証: MissingAudioReportsFailureAndAllowsFreshRetry

## U13 同じ曲をもう一度試聴

- 元の問題: 10秒の試聴後にもう一度聴くには別の曲へ動かして戻す必要がある。
- 変更: 終了・失敗後に押せる「再試聴」ボタンを追加。
- 利用者への効果: 確認したい部分を同じ難易度のまま聴き直せる。
- 主なファイル: `Assets/Scripts/UI/SongSelectController.cs`
- 検証: MissingAudioReportsFailureAndAllowsFreshRetry / コード・配置レビュー

## U14 試聴だけを消音し設定を保存

- 元の問題: 試聴が常に鳴り、画面内に停止音量の操作がない。
- 変更: 試聴音ON/OFFを追加し、プレビュー用AudioSourceだけに適用、画面を開き直しても保持。
- 利用者への効果: 周囲へ音を出さず選曲し、本編の音量設定は保てる。
- 主なファイル: `Assets/Scripts/UI/SongSelectChartPreview.cs`
- 検証: PreviewMuteOnlyChangesPreviewAndSurvivesReinitialization

## U15 試聴開始失敗から再試行できる

- 元の問題: 音声時計が始まらない場合は再生中のまま永久に待機し、無効な抜粋ではロード状態とクリップが残る。
- 変更: 開始監視・リクエスト期限・無効クリップ破棄を追加し、失敗状態へ戻す。
- 利用者への効果: 音声デバイスやデコードの失敗時にも選曲を続けて再試行できる。
- 主なファイル: `Assets/Scripts/UI/SongSelectChartPreview.cs`
- 検証: MissingAudioReportsFailureAndAllowsFreshRetry / タイムアウト経路はコードレビュー

## U16 画面外の間に選曲制限時間を減らさない

- 元の問題: 別アプリへ切り替えている間も100秒が経過し、意図せず曲が始まる。
- 変更: フォーカス外・曲なし・遊べない譜面の選択中はタイマーを停止。
- 利用者への効果: 画面に戻った時に選曲の続きから操作できる。
- 主なファイル: `Assets/Scripts/UI/SongSelectSkin.cs`
- 検証: コードレビュー、既存カウントダウン回帰テストは親の統合検証

## U17 縦長・横長のジャケットの歪みを修正

- 元の問題: 円形ジャケットへ画像全体を正方形に縮めており、縦横比が違うと人物や文字が変形する。
- 変更: 中心の正方形を切り抜くUVを計算し、Spriteの部分領域も尊重。旧ImageにもpreserveAspectを適用。
- 利用者への効果: 画像の形を保ったジャケット表示になる。
- 主なファイル: `Assets/Scripts/UI/SongSelectDiscGraphic.cs`
- 検証: PortraitCoverKeepsSquarePixelsAndSpriteSubrectangle

## U18 結果から同じ曲へ再挑戦

- 元の問題: 結果の操作がタイトルへ戻るだけで、再度曲と難易度を選び直す必要がある。
- 変更: 演出終了後の「もう一度」を追加。譜面の可用性を再確認して同じ曲・難易度を開始。
- 利用者への効果: 練習を途切れさせず同じ譜面へ挑める。
- 主なファイル: `Assets/Scripts/UI/ResultController.cs`
- 検証: コードレビュー、ResultNavigationPlayTestsによる旧BACK回帰確認は親の統合検証

## U19 結果から選曲へ直接戻り曲と難易度を復元

- 元の問題: 別難易度を試すだけでもタイトルを経由し、選曲位置を失う。
- 変更: 「選曲へ」ボタンと一回だけ消費する曲ID・難易度の引継ぎを追加。タイトル経由には持ち越さない。
- 利用者への効果: 同じ曲の別難易度を続けて比較できる。
- 主なファイル: `Assets/Scripts/UI/ResultController.cs`
- 検証: ResultSelectionIsConsumedOnlyOnce / コードレビュー

## U20 判定内訳に割合を添える

- 元の問題: PERFECTなどの個数だけでは長さの異なる譜面の成績を比較しにくい。
- 変更: 各判定行へ総判定数に対する百分率を表示。判定ゼロは--。
- 利用者への効果: 得意・苦手の割合が曲をまたいで分かる。
- 主なファイル: `Assets/Scripts/UI/ResultSkin.cs`
- 検証: EmptyResultDoesNotCongratulateAFalsePerfectOrRank / 表示式テスト

## U21 自己ベストとの差を表示

- 元の問題: 自己ベスト値と今回値はあるが、あと何点かを暗算する必要がある。
- 変更: 前回ベストと今回点の差、同点、初記録を区別して表示。
- 利用者への効果: 次回の得点目標がすぐ分かる。
- 主なファイル: `Assets/Scripts/UI/ResultImprovementSummary.cs`
- 検証: BestDifferenceDistinguishesFirstTieImprovementAndGap

## U22 次ランクまでの精度差を表示

- 元の問題: 現在ランクだけでは次にどれくらい精度を伸ばせばいいか分からない。
- 変更: ゲームの実ランク閾値から次ランクと必要な精度ポイントを表示。僅差を0へ丸めない。
- 利用者への効果: 次の達成目標を具体的に把握できる。
- 主なファイル: `Assets/Scripts/UI/ResultImprovementSummary.cs`
- 検証: NextRankGoalUsesRealThresholdsAndNeverRoundsSmallGapToZero

## U23 FC・APに向けた判定の目標を表示

- 元の問題: 失敗が少ない結果でもFC/APに何が足りなかったか読み取りづらい。
- 変更: BAD+MISSをFCの課題、FC後はGREAT+GOODをAPの課題として表示し、AP時は達成を明示。
- 利用者への効果: 次のプレイで改善する対象が分かる。
- 主なファイル: `Assets/Scripts/UI/ResultImprovementSummary.cs`
- 検証: AchievementGoalCountsBadAsComboBreakAndGreatAsApGap

## U24 結果にプレイした難易度を表示

- 元の問題: 同じ曲の結果に難易度がなく、比較やスクリーンショットで区別しづらい。
- 変更: 曲名の下へ難易度を表示。ゲーム内MASTERと譜面名HARDの対応も併記。
- 利用者への効果: 製作中HARDの記録をNORMALと混同しにくい。
- 主なファイル: `Assets/Scripts/UI/ResultSkin.cs`
- 検証: BestDifferenceDistinguishesFirstTieImprovementAndGap / コード・配置レビュー

## U25 終了後のスキップ案内を操作案内へ切替

- 元の問題: 結果演出が終わってもCLICK / ANY KEY TO SKIPが残り、まだ待ち時間があるように見える。
- 変更: 演出時計に同期し、終了後はボタン操作とBACKの行き先を案内。
- 利用者への効果: 結果を見終わった後の操作が分かる。
- 主なファイル: `Assets/Scripts/UI/ResultSkin.cs`
- 検証: コードレビュー、ResultNavigationPlayTestsは親の統合検証
