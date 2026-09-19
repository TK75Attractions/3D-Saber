# 判定の説明・コンボ・曲終了の演出

2026-09-19。Fableの候補から実装後、ユーザーの「こういう判定表示は嫌だからさっきのやつにそこだけ戻してほしい」に従い、ノーツ付近の表示（②③⑤）を取り消した。現在は⑥⑧⑨⑩を維持し、Perfect／Great／Good／Bad／Missとタイミングの表示は従来の画面下のGameHUDSkinに戻している。Great以下やロング途中への成功の追加光、判定・譜面・音源・速度閾値は変更しない。

## 参考にした既存作品・設計資料

映像を見たという記録ではなく、以下の公式ページ／開発者更新記事の説明を確認した。個別の形・配色・タイミングは3D-Saber用の設計で、他作品の実装そのものを再現したものではない。

- [Beat Saber公式FAQ](https://beatsaber.com/faq.html)：ノーツの後ろの軌跡が、当たったかどうかの誤認につながる例がある。初回の不受理説明の検討に参照したが、その表示はユーザーの希望で取り消した。Beat Saberの判定が本作の判定を保証するという意味ではない。
- [CHUNITHM公式オプション説明](https://chunithm.sega.jp/play/option/)：ノーツの判定結果の表示位置を調整する考え方を参考にした。初回の切断付近のラベルは取り消し、現在は従来の画面下の判定表示を使う。
- [Synth Riders開発者更新、2020-10-16](https://store.steampowered.com/news/posts/?appids=885000&enddate=1602972734&feed=steam_community_announcements)：FlashとParticleの強度段階や表示先を分け、高速曲での見やすさを選べる。本作では選曲画面の投影設定の隣にEFFECTS: FULL/LOWを追加し、装飾の強度を落としても判定の説明は残す。
- [CHUNITHM公式更新、Version 2.16](https://info-chunithm.sega.jp/5429/)：CLEAR、FULL COMBOなどの達成表示を区別する設計を参考にする。本作のFC条件は本作固有で、Badもコンボを切るため除外する。
- [Xbox Accessibility Guideline 103](https://learn.microsoft.com/en-us/xbox/accessibility/xbox-accessibility-guidelines/103)：重要な情報を複数の伝達手段で示す考え方を初回検討に参照。ノーツ付近の×や矢印の説明札は取り消した。現在も判定やFC状態は色と文字で伝える。
- [Xbox Accessibility Guideline 117](https://learn.microsoft.com/en-us/xbox/accessibility/xbox-accessibility-guidelines/117)：動きや画面効果の調整を参考にする。LOWではHUDの判定文字の上昇、コンボの拡大、曲末ラインの伸長を止める。

## 現在の実装

| 元候補 | 実装 |
|---|---|
| ② 不受理の理由 | 取り消し。LEFT HAND、TOO SLOWなどの説明札と、その表示専用の接触監視を削除。 |
| ③ Miss | 対象付近の×付き札を取り消し。以前の画面下のMISS表示を使用。 |
| ⑤ 判定位置 | 以前の画面下へ戻した。以前の文字の見た目、EARLY/LATE、FLICKの注意表示を使用。 |
| ⑥ コンボ節目 | 50の倍数で短いCHAIN表示。標準では小さく拡大し、LOWは静止表示。全レーンを光らせない。 |
| ⑧ FC維持 | 右上にFC READY / FC ACTIVE / FC LOST。開始前を達成扱いせず、BadまたはMiss以降は回復しない。色だけでなく文字でも状態を示す。 |
| ⑨ 曲の締め | 全ノーツの判定と曲音源の終了を待ってFULL COMBO / TRACK CLEAR、最高コンボとスコア、短いライン。最終MissでもTRACK CLEARが成立。既存のリザルト遷移時刻は変えず、余韻の時間内に収める。LAST CUTの札は判定表示の変更とともに取り消し。 |
| ⑩ 控えめ設定 | 選曲画面のEFFECTS: FULL/LOW。クリック・ノーツ斬りの双方、設定保存に対応。LOWは切断火花を8→2、光の強さを30%にし、床／舞台反応とHUDの拡大を抑える。刃・ノーツの本体・判定文字の可読性は維持。 |

①Great/Goodへの追加バースト、④速度で刃本体を暗く細くする表現、⑦ロング途中の追加点灯は採用していない。既存の切断、効果音、振動、ロングのひびと残数表示を引き続き使う。

## 接続と検証

`GamePlayManager`がコンボ・FC・曲末用のプレゼンターを生成してTickを駆動。ScoreManagerの通知だけを購読し、シーン終了で解除する。ノーツ付近の札・記号・通知と表示専用の接触履歴は取り除いた。CuttableNoteとSaberCutJudgeはこの追加前の版（3b2249e）と一致する。

演出に使う英数字は曲開始前にフォントへ登録する。実時間テストの最大フレーム間隔と取りこぼし時刻の診断、404ノーツ成功という既存の検証条件は維持する。

`PlayFeedbackTests`はFC、節目、終了条件を検証する。`PlayFeedbackPlayTests`は実Gameシーンで従来の画面下の判定の復帰、ノーツ付近の札が生成されないこと、FCと曲末を確認し、SongSelectの設定保存、LOW時の描画量、Perfect限定も検証する。

実機センサーの体感とプロジェクターへの実投影は未確認。実シーンの撮影では人工ノーツ・人工操作を使用する。継続的なFPSの実測は別途必要。
