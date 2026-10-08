# Saber Tap Studio：25改善

録音済み譜面の仕上げと、スマホだけでの曲管理を中心に改善しました。すべて端末内で処理し、音源は送信しません。

## S01 全曲のms移動

- 問題：既存の区間移動では全曲を選ぶためにA/Bを指定する必要があり、110ms遅れを直接まとめて直す導線がない。
- 変更：曲全体を既定対象にしたms移動を追加。負数で早め、変更件数を確認後に適用する。
- 検証：-110ms移動、offset維持、ロング長維持、1回Undo、音源境界のテスト。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S02 同時押しの高さを統一

- 問題：左右を別々に録音した同時ノーツのYをまとめてそろえられない。
- 変更：同時グループのYを平均にそろえる。0〜50msの同時許容幅を選べる。
- 検証：高さ平均、許容幅、隣の拍へ連鎖しないグループ境界を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S03 同時ロングの回数を最大へ統一

- 問題：同時ロングの左右回数の不一致を個別に見つけて直す必要がある。
- 変更：同時ロングのcountを最大へそろえ、tapと長さを保持する。暗黙長の旧ノーツも長さを固定して保持する。
- 検証：3/5回→5/5回、tapの1回保持、暗黙長1.4秒の保持を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S04 左右の位置を中心対称化

- 問題：Yだけでなく左右の距離にも手打ちのばらつきが残る。
- 変更：同時青赤の絶対X平均を半径とし、青を左・赤を右の対称位置へそろえる。
- 検証：-0.7/+1.3→-1/+1、金と単独ノーツ保持、座標スケール保持を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S05 録音済みノーツへの近接拍補正

- 問題：近い拍だけ補正する機能は新規入力に限られ、既に作った譜面へ適用できない。
- 変更：対象ノーツの近い拍だけを最大20/35/50ms幅で補正する。
- 検証：近い520ms→500ms、遠い560ms維持、ロング長保持を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S06 ロング終端だけを拍へ整列

- 問題：始点を変えずに複数ロングの終わりをそろえる操作がない。
- 変更：指定刻みでロング終端だけ整列。開始時刻は固定し、0長や音源外は適用を中止する。
- 検証：620ms長→625ms長、始点維持、音源外終端の拒否を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S07 A–Bの一括削除

- 問題：不要な録音区間を一覧から1ノーツずつ消す必要がある。
- 変更：曲全体/A–Bと色で選んだノーツを、件数確認後に削除できる。
- 検証：offset付きA含む/B含まない範囲と色フィルターを検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S08 完全重複ノーツの除去

- 問題：追記録音や区間コピーで完全に同じノーツが重なっても除去方法がない。
- 変更：編集ID以外の全プロパティが一致するノーツのみ1個残す。
- 検証：対称同時押し、異なるbeat、未知メタデータを保持し重複だけ除去するテスト。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S09 区間の色を一括変更

- 問題：片手パートを別の手や金ノーツへ変えるには個別編集が必要。
- 変更：曲全体/A–Bと元色で絞り、青・赤・金・指定なしへ変更できる。
- 検証：選択区間外の色と他属性の保持を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S10 区間の矢印を一括変更

- 問題：複数ノーツを同方向のフリックへ、または通常タップへ戻す操作がない。
- 変更：8方向と矢印なしをまとめて設定し、tap/direction/longの種別も正しく維持する。
- 検証：通常→direction→tap、ロング種別保持を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S11 ロング回数の一括指定

- 問題：長いパート全体のロング回数を同じ数へ変えるには個別作業が必要。
- 変更：対象のロングだけ2〜99回へ変更し、長さと通常ノーツを保持する。
- 検証：tap保持とcount4→9、暗黙長保持、範囲外回数の拒否を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S12 配置をまとめて平行移動

- 問題：録音パート全体が上下左右へ偏ったとき、まとめて位置を動かせない。
- 変更：画面上のX/Y移動量を指定して、coordScaleを考慮し選択配置を移動する。
- 検証：scale2の座標変換とパッド外移動の全体中止を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S13 配置と矢印の上下反転

- 問題：既存の左右反転だけでは上下を入れ替えた配置パターンを作れない。
- 変更：Yと上下を含む矢印を同時に反転。時刻・色・Xは保持する。
- 検証：斜め方向反転、水平矢印保持、2回反転で元に戻ることを検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S14 見直すノーツを診断して選択

- 問題：音源外、画面外、重複、同手ロングの重なりを一括で見つける画面がない。
- 変更：確認候補を時刻付きで表示し、選ぶと該当ノーツの編集画面を開く。意図した配置もあるため自動修正はしない。
- 検証：各問題の検出、編集IDの保持、元譜面を変更しないことを検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S15 密度と左右・種別の統計

- 問題：全体ノーツ数だけでは休符・混雑・左右の偏りを探しにくい。
- 変更：色/種別数、平均・最大2秒密度、10秒バケットを表示。バケットから試聴位置へ移動できる。
- 検証：無音バケット、色/種別集計、最大密度とその位置を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`、`Tools/MobileChartStudio/dist/style.css`

## S16 前後のノーツ時刻へジャンプ

- 問題：5秒送りだけでは疎な譜面の次の入力位置へ正確に移動しにくい。
- 変更：前/次ノーツの時刻へ停止状態でシークする操作を追加。
- 検証：同時刻重複を飛ばす、offset考慮、端点のクランプを検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S17 拍単位でシーク

- 問題：5秒移動から拍単位のA/Bを指定しづらい。
- 変更：現在位置から前/次の拍へ、BPM・拍原点・offsetを考慮して移動する。
- 検証：拍上と拍の間、負方向、原点+offsetの計算を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S18 現在位置から拍原点を設定

- 問題：拍の始まりmsを音源を聴きながら数値転記する必要がある。
- 変更：今の位置を拍の始まりへ設定し、ノーツ時刻を保ったままbeat表現を追従させる。
- 検証：現在位置−offsetを拍原点へ設定、beat追従、元時刻・元譜面の保持を自動テストで検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S19 名前付きブックマーク

- 問題：サビや直したい場所へ戻るための時刻の記憶・転記が必要。
- 変更：名前付き目印を最大100個保存し、ワンタップで移動。下書きと.saberに含める。
- 検証：時刻/名前検証、整列、日本語タイトルと元音源を含むバックアップ往復。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/backup.js`、`Tools/MobileChartStudio/dist/app.js`、`Tools/MobileChartStudio/dist/index.html`

## S20 元音源をそのまま書き出し

- 問題：音源の単独出力がWAVだけで、スマホ上で容量が増える。
- 変更：保存済みBlobを元の名前・形式のままダウンロードできる。
- 検証：同じ元Blobを渡す経路のコード検査、バイナリバックアップ一致の既存/新規テスト。
- ファイル：`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`

## S21 下書きを曲名・難易度で検索

- 問題：曲や難易度の下書きが増えると一覧を見続ける必要がある。
- 変更：曲名・難易度を大文字小文字と全角差を吸収して検索し、表示件数も出す。
- 検証：日本語名、全角HARD、大小英字、空検索を検証。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/app.js`、`Tools/MobileChartStudio/dist/index.html`

## S22 不要な下書きと未使用音源を削除

- 問題：新規音源・復元・難易度コピーを増やすと保存領域を整理できない。
- 変更：削除確認を追加。同じ音源を使う下書きがなくなったときだけ、同一トランザクションで音源も削除する。
- 検証：NORMAL削除でHARDと共有音源保持、最後の下書きで音源解放、他曲保持を検証。
- ファイル：`Tools/MobileChartStudio/dist/storage.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/app.js`、`Tools/MobileChartStudio/dist/index.html`

## S23 確認モードのA–Bループ

- 問題：短い区間を繰り返し聴くたび再生操作が必要。
- 変更：確認モード・指定区間に限ってB到達後Aから再生。録音・全曲試聴・中断時にはループしない。
- 検証：既存の区間音声停止予約テスト、開始/停止とループ条件をコード検査。統合ブラウザQA対象。
- ファイル：`Tools/MobileChartStudio/dist/app.js`、`Tools/MobileChartStudio/dist/refinement-ui.js`、`Tools/MobileChartStudio/dist/index.html`、`Tools/MobileChartStudio/dist/backup.js`

## S24 音源と入力・拍音を個別音量調整

- 問題：曲が大きい/小さいとタップやメトロノームの確認がしづらい。
- 変更：音源用Gainとフィードバック音量を分け、再生中にも変更可能。設定を下書き/.saberへ保存する。
- 検証：音量独立、ミュート、範囲クランプ、音声時計不変、設定バックアップの検証。
- ファイル：`Tools/MobileChartStudio/dist/audio.js`、`Tools/MobileChartStudio/dist/app.js`、`Tools/MobileChartStudio/dist/backup.js`、`Tools/MobileChartStudio/dist/index.html`

## S25 入力位置の任意グリッド

- 問題：タップ位置の高さや左右位置を最初から一定間隔に置きにくい。
- 変更：自由配置を既定のまま0.25/0.5/1.0間隔を選択可能にする。色は補正後でなく実際に押した側を採用する。
- 検証：自由配置保持、座標の丸め・端点・設定復元、左右色のコード検査。
- ファイル：`Tools/MobileChartStudio/dist/refinement.js`、`Tools/MobileChartStudio/dist/app.js`、`Tools/MobileChartStudio/dist/backup.js`、`Tools/MobileChartStudio/dist/index.html`

## 検証範囲

Node 67テスト（実譜面27件の往復検証を含む）、全JavaScriptの構文検査、HTML参照IDの整合性。新規UIの実ブラウザ確認は統合レポート参照。スマートフォン実機・Bluetoothの確認は含みません。
