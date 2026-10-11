# PhoneSaber STATUS

資料の索引と、実行ファイル・道具の場所は [README.md](README.md)。

## ユーザー待ち

2026-10-11時点。最新版を実機に入れて確認する。手順は[当日runbook](EVENT_DAY_RUNBOOK.md)。

**次に iPhone を Xcode で入れ直したら（10-11 の修正の確認、合計5分ほど）:**
- **振りだけ録画（最優先）**: 本番の場所（壁画が映る画角）で、露出 1/100 秒、Debug Recording の「振りだけ録画を開始」を押して振る位置へ。約50秒で自動終了。2本撮る（別々の振りの証拠が要るため）。core-line 過連結 guard と壁画の青 FP 対策の採用判断に使う（[解析](coreline-overlink-and-mural.md)）。
- **Wi-Fi 切替の復帰**: 送信中に Mac の Wi-Fi を別のネットワークへ切り替え、何秒で Unity に座標が戻るか（10-10 は約100秒）。iPhone に「PC（Unity）から応答がありません」が出ること、「通信ログ」に経路の切替が残ることも見る。
- **F9**: 画面に「比較用の記録 n/8 ブロック」が出る。8 ブロック（約2分）まで、iPhone の「fps自動比較」ON でカメラを画面に向けたまま待つ。

0. **最適化の実機確認（最優先・数字の入力不要）**: Mac `git pull`→Unity Play、iPhone を Xcode で入れ直し（60fps 既定・LAN 優先・送信経路と認識の高速化・UI 12Hz）。iPhone と Mac を同じ Wi-Fi にして Debug Performance の「fps自動比較」ON → F9 を数分→F9。AQUOS は Mac に USB 接続すれば Claude が入れて 60fps 維持・JNI 中央値を確認。剣の遅延補正は F8 で 20ms を試し、振り心地を一言もらう。

0b. **Mac 起動キットの確認（本番はビルド済みアプリ。10-10 決定）**: Unity の Play を止めて `PhoneSaber/mac/Start-Saber-A.command` をダブルクリック → F8 で P2P bridge が ON、強制終了（⌘⌥Esc）で5秒後に再起動、ゲーム内 Quit では再起動しない。

1. **剣あり・iPhone＋Macのガイド付き録画v3**: 露出1/100秒で1本。最初の「saberなし」の画像、赤・青の静止／速振り／先端向けを残す。遠い・淡い青、画面端も確認。会場に近い距離（画面から2.7〜3.1m、範囲直径1.5m）で、背後の人・投影中・頭上の振りを含める。
2. **AndroidをAQUOS sense9で残りを確認**: 2026-10-07にWindowsホットスポット経由で両色の送信・F8受信・30fpsを確認済み。残りは台A/B探索、向き・四隅、反転保存、開始／停止／前面復帰／再起動／Wi-Fi切替。
3. **Windows実行の残り**: 10-07にEditor Playで受信を確認（ホットスポットはPublic扱いのため全プロファイルでUDP 5005-5007を許可、UnityのBlock規則を無効化）。残りはビルド版＋bat監視、A/B探索、F7、異常終了後再起動／正常Quit。
4. ~~Unity EditMode Test Runner~~: 2026-10-09 に Claude がプロジェクト複製の batchmode で実行し 1678/1678 PASS（e512a67）。Editor 内での再実行は不要。
5. **30分〜1時間の連続運転**: 本番の箱・給電・配置で送信し、発熱・電池・処理／送信／受信fps・処理中央値・到着間隔・メモリを記録。途中で前面復帰・Wi-Fi切替・アプリ再起動を試し、停止後は前面復帰だけでは再開しないことも確認。遅延計測は確認後OFFへ。
6. **剣なしの Debug Recording を2本**(剣がなくてもできる): 露出1/100秒、区間ラベル「saberなし」で開始、各1〜2分、Capture Lossless を2〜3回。① 窓や空が映る明るい場所(青のルールの確認)、② 人の肌・服が前を動く場所(赤の残りの誤検出)。

## 現在の仮説と確度

- **中（新・10-11）**: 実剣の上での端点の飛びは「core-line の過連結」（剣と遠くの明点を1本につなぐ。大差で勝つので僅差の CASE A とは別）。機構は特定済み、限定 guard は offline で有効。別 swing の実剣証拠が未達。本番の背景には青緑の壁画が一部映り、消灯時の BLUE FP になる。
- **高**: 肌・照明・背景の誤検出と、速振りのブレによる候補消失が主因。旧CASE A代表例は背景同士で、実剣の選択修正gateは未達。
- **高**: AWDLの詰まりは認識とは別の遅延原因。interactiveVoiceの実機効果は未確認。F8で間隔・同期時の片道時計差を比較できるが、画面全体の遅延ではない。
- **確認済み**: score決定性は `bbc1f32`、赤warmNoDeepRedは `8363024`。10-06の実機v2では最初の剣なし赤FPが598/598→0/598。ただし別の背景FP・小さい／淡い剣の保持は追加確認が必要。
- **採用済み**: 青blueNoDeepSupportは `fa5db16`。formal 40/40、既存正解187/187保持、剣なし青FP 29→18（offline）。遠い・淡い・ぶれた青の実機確認は未了。Wは淡いピンク剣を失うため不採用、fixture 89は窓期待値のまま。
- **実装済み・実機待ち**: 台指定、F8、両OSキット・監視・イベントログ、端末状態、自動復帰、反転、遅延確認、座標再生、F7位置補正。Mac＋iPhoneの台指定は確認済み。Android JNIはAPI 37 ARM64エミュレータで正式35 PNG／40期待値を確認（35/35 PASS）。内部浮動小数点のbit一致や実カメラの一致は保証しない。

根拠・残存FP・却下案は[FINDINGS](FINDINGS.md)。同文書とAndroid READMEの過去の「未commit／未実行」は、下の採用・検証commitを参照する。

## 次にやること

0. **誤検出ルールの採用判断（実機 capture 待ち）**: benchmark（recognition_benchmark.py）の分析で、赤は high_value_ratio ≥ 0.65 で剣なし FP 56→29（剣 29/29 保持）、青は color_purity ≥ 0.25 で FP 9→3（剣 31/31 保持、unknown 変化 1/346）。ラベル付きの剣は静止・近距離中心なので、ガイド付き録画 v3 の速振り・遠距離・淡い青に手ラベルを付け、剣を落とさないことと formal 40/40 を確認してから threshold を決める（docs/claude/red-fp-features.md）。

優先順。実装済み機能は実機で確認し、不具合が出た箇所を直す。

1. **本番の端末構成を通す**: 上のAQUOS／Windows／Test Runner結果を確認。F8・反転・F7・A/B保存、1920×1080の端点・最新入力・未検出時、開始／停止／復帰を実機とPlayModeで確認する。
2. **ガイドv3を解析**: ORIGINALを目視し、赤・青ルールの背景FPと小さい／淡い剣の保持を比較。自動露出／1/100秒、距離0.5〜3m・端からの向き・昼夜の速振りを比較する。既定露出変更はユーザー判断。
3. **本番の遅延・切断復帰を比較**: F8のp95／最大間隔、同期時の片道差、P2P RTT、画面全体の遅延を別々に測る。会場Wi-Fi・ネット使用中・2台Mac・LAN退避を同条件で比較し、許容値を決める。
4. **連続運転と復旧を確認**: 発熱・fps・電池・保存量・memoryHeadroom、低電池・カメラ中断・再接続・監視再起動を評価。録画256MiB上限の余裕を別端末でも確認する。
5. **残存背景FPとfixture 89**: 別人・別場所の点灯／OFF対、halo・点LED列を比較。窓と淡い剣の共存証拠、画像外1pxの端点を再審査し、認識と回帰を直す。期待値だけ変えると39/40。Wは再採用しない。静的マスクは固定背景だけの案で、静止剣の保持証拠とユーザー判断が必要。
6. **CASE A/B/Cを分けて調査**: bundleコピーでtriage dry-run／selection replayを行い、ORIGINALと全eligibleを照合。Aは別swingで実剣が僅差で負ける2event以上のgate後に修正。Bが多ければ生成前の棄却componentを記録。Cの端点修正はAと別にし、長い剣・分離LEDを保護。200ms以下の補間／外挿も誤出力の延長を比べ、ユーザー判断後に変更する。
7. **Mac計測画面を実操作**: 開始／停止／再開、CSV保存・結果フォルダ・失敗統計・実UDP・同一時計の表示→受信計測・Unityとのポート競合を確認する。
8. **診断受信と旧bundleを確認**: `/health`、安全な再起動、転送失敗後の再送、report／overviewを通す。234740／155919は手動再解析候補。旧010049の32KB超過は上限を維持する。
9. **会場でrunbookを通す**: 署名・権限、固定配置、背後の幕、投影・照明、箱の熱・電源、予備経路、終了、[2台目Mac](SECOND_MAC_SETUP.md)を確認。床の範囲表示、回転率・タイトル復帰・判定タイミングも確認。本番構成はMac＋iPhone／Windows＋Androidが候補で未確定。Mac はビルド済み `3D-Saber.app` を起動キット（Start-Saber-A/B.command）で回す（10-10のユーザー判断。Editor Play ではない）。B台の一時停止キーは追加しない（10-06のユーザー判断）。
10. **解析費用とログ保持を整理**: high既定／必要時maxの費用・精度を比較し、needs_captureへの長時間解析を減らす。inbox／実ログの保持期間・削除対象はユーザー判断。テストログ隔離と失敗記録を保つ。
11. **将来の構成を比較**: AWDLが不安定ならUSB/usbmuxd/TCPを試すか判断し、座標互換・滞留・切断復帰・画面全体の遅延を測る。Mac内蔵／USBカメラ直接認識は精度・端点・配置・遅延を比較して採否を決める。Continuity Cameraは別の無線経路で、縮小／FPSだけを通信遅延改善と扱わない。

## 作業ログ

### 2026-10-11 午後 Codex による点検と修正（Claude が確認・検証して取り込み）
- **Codex 無人 runner**: Codex の5時間枠を6並列で約30分で使い切り、6ジョブが途中で止まった（13:31）。Claude なしで続きを回す runner を起動（`~/Library/Logs/PhoneSaber/codex-runner/`、`runner.log`、止めるには `STOP` を作る）。枠が戻ると3レーンで実行し、結果は各作業 branch に自動 commit する。**main には入らない。Claude が差分確認と verify をしてから取り込む。** 18:05 に再開。runner が正常終了を「上限で中断」と誤判定してやり直させる不具合があり 18:42 に修正（出力に写った指示文の語で判定していた）。
  - 優先順（ユーザー指示 10-11: 撮り逃しは減った。それ以外、特にバグを残さない）: A = 録画の再送 → 10-10〜11 に入れた変更のバグ探し（`cx/review-today-20261011`）→ guard G → 認識の集計。B = iPhone 当日対策 → 部品間の取り決めの食い違い探し（`cx/protocol-20261011`）→ iPhone 遅延。C = 運用4点 → 手順書と実物の照合（`cx/ops-20261011` 上）→ Android 遅延。
- **取り込み済み・夜（verify: iOS XCTest / Detection / Lossless 40 / iOS Release / Diff Check PASS、Tools 428＋126 PASS、Unity EditMode 1903 中 1901 PASS・失敗2件は譜面エディタ）**
  - 録画の再送と受信確認（`cx/outbox`、Codex）: 永続 outbox（アプリ再起動後も残る、前面復帰・経路復旧・送信開始で再試行、録画中は送らない）、画面に 転送待ち／転送中／Mac 受信済み／転送失敗 と未転送件数と「今すぐ送る」、同じ内容の再送だけ 200-duplicate で受領扱い（別内容と裸の 409 は失敗のまま）、bundle 確定直後に転送開始（overlay 生成を待たない）。「録画完了」は「端末に録画を保存」に変更。
  - iPhone 当日対策（`cx/ios2`、Codex）: 発熱 serious/critical で実効 30fps・nominal/fair が120秒続いたら復帰（保存設定は不変）、カメラ復旧は15分後も60秒ごとに無期限、非同期停止完了時に前面を再確認、未充電・低電池（20% 非充電／10%）・低電力モードの警告、手動 IP 指定中の表示と解除ボタン、選択中の PC を正しく表示、fps 自動比較は5分で自動 OFF＋警告、DEBUG の callback 履歴を16世代に制限。
  - 運用（`cx/ops`、Codex）: 固まり検知（Player が毎秒 heartbeat、初回確認後45秒止まったら kill して再起動。`PHONESABER_HANG_SECONDS=0` で無効）、Windows 二重起動防止、P2P bridge の自動再起動（2/5/15/60秒）、Mac Status ツールが built Player を認識。
  - Claude が追加: **監視自身が10秒以上止まっていた（PC のスリープ復帰）直後は固まり判定を1周期休む**（復帰直後に正常なゲームを kill する穴）、新規 C# 3ファイルの .meta、host 用 stub（tracking e2e・sender soak）を転送 API の変更に追随。
  - いずれも実機・Windows 実機では未確認。
- **取り込み済み（verify 全項目 PASS）**
  - Mac P2P bridge（903e4ba 相当）: 診断 relay の背圧、応答末尾の欠落、build cache、Bonjour 登録解除のログ。bridge テスト 27/27。10-10 の100秒の空白は Mac 側からも原因を断定できず。
  - Android（ca7c868 相当）: 選択中の PC が 5007 に3秒応答しないと注意表示、lint と fixture APK の同時 build。JVM 56/56。実機未確認。
  - Unity 受信（116049d 相当）: NaN・無限大・桁外れの座標を無効扱い、sender-change は1秒1行、ログ回転失敗で世代を消さない、5007 bind 失敗時の socket 解放。
  - Unity 追加（46254ea 相当）: **送信元ゲート**（同じ port へ2台が交互に送ると先にいた1台に固定、F8 に警告、LAN⇄P2P の単発切替は取りこぼさない）、F7 採取のキャンセル、F9 が maxQueuedFrames を元に戻す。EditMode 1897 中 1895 PASS（失敗2件は譜面エディタのテスト）。
  - 録画→Mac 転送（e0052e8 相当、[詳細](review-debug-bundle-20261011.md)）: **Mac 側の解析契約が振りだけ録画の6枚を拒否していた**のを修正、予約した lossless が bridge と重なる・容量で押し出されるのを修正、manifest と summary の session 不一致・NUL 入りファイル名を拒否。
  - iPhone 送信（[詳細](review-ios-event-day.md)）: ready 通知が送信中に来ると最新の待機座標を捨てる欠陥を修正。
  - `run-static-tests.sh` の DeviceHealth テスト件数の決め打ち（5件ちょうど）を「6件以上」に変更（テスト追加で verify の Detection 段が落ちた。認識の失敗ではない）。
- **当日の故障予測**（[premortem](premortem-20261011.md)）: 上位は背景の誤認識、箱内の発熱、B 台の通信。ゲーム本体に関わる2点（通信断で赤だけマウス操作になる、タイトルの×が確認なしで終了）は**ユーザー判断で対応しない**（10-11）。

### 2026-10-11 10-10 の実機テストで出た不具合の修正（Claude＋Codex、verify 全項目 PASS）
- **Wi-Fi 切替後に約100秒届かない**（0058315）: P2PSender は失敗した接続の次から、browser が報告する interface（awdl 優先）へ順に固定して試す。最初の接続と pong 後は従来どおり OS 任せ。原因は仮説（UDP が切れたネットワークの cached record へつなぎ直し続け、失敗が通知されない）で、実機未確認。iPhone のログが無く特定できなかったため、**通信ログ**（経路切替・P2P 探索/再接続・LAN 宛先変更。画面に直近12行、端末の Library/Logs/connectivity.log）を追加。
- **届いていないのに NETWORK READY**（同）: 送信中に Unity の応答も P2P も3秒無いと「PC（Unity）から応答がありません」を表示（見出しの文言は不変）。runbook に「PC の Wi-Fi 自動接続を切る」を追記。
- **手動録画に振りの画像が残らない**（9212734、原因調査と保持修正は Codex、[詳細](debug-retention-20261011.md)）: Stop 待ち候補の間引きが中間の最良窓を捨てる欠陥を修正。`memory` と出ていた ledger は実際には予算超過ではなく tracking 窓への置換だった（`tracking_mode` / `not_retained` に分離。**10-10 に「メモリ上限で4件捨てられた」と報告したのは誤り**）。55.40秒の event が末尾5秒扱いにならなかった件は現ソースで再現せず、実際の終了時刻（frame 数/60 より長い可能性）を `recordingEndTimeSeconds` に記録するようにした。止めに来る場面のほうが instability が高い、という設計上の限界は残るため、**振りだけ録画**（swing_only_2026_10_11 v1、51秒、消灯5秒→大きく振る→止める→速く振る、lossless 6枚、自動終了）を追加。
- **F9 の比較が成立しない**（f32e771）: 記録ブロック数と「8 ブロックで十分」を画面に表示。
- **解析ツール**（43c8e52、review-python-tools.md）: latency_report の `--since` を時刻として解釈（JST 指定で全件落ちていた）、書きかけの latency-block 行を無視、triage receiver に 150 秒の idle timeout、selection replay が壊れた JSON で落ちない、PlayMode batch が 0 件実行を成功にしない。
- **Unity の受信→描画直前**（b46352b、unity-frame-cost.md）: 中央値 7.0ms / p95 7.6ms（フレーム間隔 8.33ms）、Update 先頭→描画直前 0.18ms。余分な1フレーム遅れは無い。
- **core-line 過連結と壁画 FP の offline 解析**（327c642、Codex、[詳細](coreline-overlink-and-mural.md)）: 暗い隙間への無条件補間（pipeline.cpp:179–184 / BGRADetection.swift:390–398）と raw 長に依存する加点が原因。限定 guard G(.6) で 21952 BLUE / 21953 RED が正しい候補に戻り、formal 40/40、旧ラベルの剣 赤29・青31 の検出を保持（端点の正解までは未確認）。壁画 BLUE FP は 9→4 まで、0 にできる rule は無し。**本番採用は保留**（別 swing の実剣 2 event が未達）。
- **iPhone 送信処理の連続運転**（[soak.md](soak.md)）: Mac 上で 15 分×2 条件。footprint +0.7〜0.8MB、スレッド・fd・送信レート・送信時間は劣化なし。
- 検証: verify_phone_saber.sh（iOS XCTest / Detection / Lossless 40 / Tools / iOS Release / Diff Check）全 PASS。Unity EditMode は PhoneSaber 系 PASS、全体 1870 件中の失敗 2 件は譜面エディタのテスト（ChartRecordingWorkflowTests、今回の変更と無関係）。
- **Codex レビュー後の修正**（8a86b0a、P1 なし・P2/P3 の4点）: 経路の順番は「リンクが使える状態に戻ったとき」だけリセット（1回だけ pong が返る経路へ戻り続ける穴）、試行中の interface の広告の出入りではやり直さない・やり直しは 4 秒以上あける、警告は送信要求に連動（P2P だけで開始した場合も出る）・手動 IP では P2P 接続を証拠にしない、手動 IP は別の文言。
- **レビュー指摘のテスト追加**（Codex）: 警告の4パターン（P2P のみで bridge 停止、LAN 応答の途切れと回復、手動 IP、停止）と、保持処理の最悪条件（60fps で上昇し続けるスコア）。停止直後に警告が残る点を1行で修正。pending は 5 秒窓ぶん（約300件）で頭打ちになり、捨てた payload は全件返ることを確認。この最悪条件では挿入ごとに全 pending を見直すため Debug の simulator で 1 フレーム約 5ms かかる（通常の録画では pending は少数。影響は Debug Recording 中だけ）。
- **core-line guard G の実装準備**（ローカル branch `opt/coreline-guard-prototype` fa2aaad、main には未投入・push していない）: C++ / Swift 共通の compile-time フラグ `PHONESABER_CORELINE_GUARD`、既定 OFF。OFF は main と 662/662 bit 一致、ON は formal 40/40・選択変更 0、shadow の数値を再現、C++ core 1.648→1.674 ms/frame。採用は別 swing の実剣 2 event の証拠後。
- Codex 週間枠は 98% 使用（10-14 頃リセット）。

### 2026-10-10 学校での実機 capture（iPhone＋Mac、Claude 解析）
- F9（Editor、iPhone テザリング経由 LAN、60fps、n=22）: 中央値 135ms、p95 179ms、最小 103ms。10-07 の LAN（30〜40fps、中央値 132〜150ms、p95 204〜347ms）と中央値は同等。1ブロックだけなので fps 自動比較は未成立。
- 16:30:15 頃 Mac がテザリングから別 Wi-Fi（192.168.31.x、DHCP 16:30:24）へ切替。P2P も en0（テザリング回線）経由だったため LAN と同時に切れ、awdl0 で座標が戻ったのは 16:31:57（約100秒 無受信）。録画が原因ではない。つなぎ直しに100秒かかった理由は未特定。
- phonesaber_20261010_163034_875（手動 43秒）: 手持ちで床の Mac 画面を撮影。剣なし。画面の色を RED 49.7% / BLUE 92.4% で検出（画面の誤検出の証拠としてのみ有効）。
- phonesaber_20261010_163949_658（手動 60秒、1/100秒、屋外に面した明るい場所、剣は約5m 以上・逆光、ORIGINAL で赤・青の実剣を確認）: 検出率 RED 66.6% / BLUE 55.9%（移動中を含む）。**実剣がほぼ静止している frame で、剣と遠くの明るい点を1本につないだ core-line 候補が大差で winner になる**。
  - frame 21952 BLUE: winner core-line 端点 [16,66]-[310,114]（rawPCASpan 300px、score 108.7）。正しい候補（color-mask [14,66]-[52,76]、61.6）は eligible のまま rank 3。前後 frame（21954/21955）は正しい core-line [16,68]-[56,76]。
  - frame 21953 RED: winner core-line [14,30]-[90,118]（score 118.1）。正しい候補（core-halo [80,84]-[88,136]、63.4）は eligible rank 2。
  - 判定: 正しい候補は eligible に残るが、**僅差ではなく大差**（gap 47〜55点）で長い core-line が勝つ。CASE A（僅差のすり替え）の定義には合わない新しい型（core-line の過連結）。1 recording・同じ構えの中の連続 frame なので、修正 gate（別 swing で2 event 以上）は未達。会場に近い屋内条件で再現するかを次の capture で確認する。認識コードは未変更。
- phonesaber_20261010_164236_960（手動 57秒、1/100秒、屋内・青緑の壁画が背景の別画角）: 検出率 RED 84.6% / BLUE 89.7%。ユーザーの体感も「判定はいい感じ」。ただし bundle の12枚はすべて録画末尾（停止しに来る場面、剣は消灯・手持ち）で、振っている最中の ORIGINAL が無い。末尾では壁画を BLUE（color-mask、端点間 約420px）、手を RED と誤検出。過連結がこの画角で出るかは画像が無く判定不能。次は振りの最中に lossless を残す（Capture Lossless か ガイド付き録画）。


### 2026-10-10 Claude 並列レビュー（iPhone / Unity / Android）と修正
- iPhone（5680fa9, review-ios-sender.md）: 開始直後の停止→開始で古い完了通知が新しい送信のカメラを止める（F1）、Bonjour の IP 変更後も LAN 生存確認が旧 IP を見続け P2P に回り続ける（F2）、生存確認の受信 error 後の再接続、LAN 送信中の「P2P」表示、など7件。verify 全段 PASS。
- Unity（32821c4, review-unity-phonesaber.md）: イベントログの書込失敗で行が消える・世代交代失敗で記録が止まる、受信 lock 中に毎フレームの台名読み出しが待つ。EditMode 1698/1698、PhoneSaber PlayMode 17/17。
- Android（86a350c, review-android.md）: IPv6 RA/DHCP の LinkProperties 通知のたびに PC 選択を消していた（会場 Wi-Fi で数分ごとに途切れうる）、前回 PC を名前だけで照合、カメラ起動の例外、エラー文、画面再生成で閾値が戻る。unit 47/47。
- Mac launcher（b41ae3f）: built .app でも P2P 予備経路が起動するよう open --env で bridge の場所を渡す（実 Player では未確認）。
- PlayMode 全件を小分け実行するツール（45e8ecf, unity_playmode_batches.py）: 6 run・211 pass・crash 0、既知の Calibration 2件のみ失敗。
- Windows kit（d3d4b58, review-windows-kit.md）: Player の受信 Block 規則（表示名 ServTechSlash）を firewall スクリプトが拾えず無効化できなかった、標準入力リダイレクト時に再起動待ちの timeout が失敗して1回目のクラッシュで監視終了、STOP 残存時に無言で閉じる。Windows 実機では未実行（確認5項目はレビュー文書末尾）。
- docs（41b79e9）: スタッフ手順・runbook・P2P/iOS/Android README・CLAUDE.md/AGENTS.md を現行コードに合わせた（LAN 優先、P2P は launcher が自動、Thread.Abort の古い記述を削除）。
- 実機で確認すること: iPhone 開始直後の停止→開始でカメラが止まらない／Mac の IP 変更後に LAN 表示へ戻る、Android を IPv6 のある Wi-Fi で10分以上送信して途切れない／Unity 再起動後に同じ PC へ戻る、built .app の F8 で P2P bridge ON。

### 2026-10-07〜10 遅延・負荷の最適化（まとめ。詳細は各 docs/claude/*.md と commit message）
- 計測: F9 遅延テスト（画面→受信、20回ごとに events.log、latency_report.py）、両スマホの「撮影→送信」、Unity frame cost（PhoneSaberFrameCost.cs: ゲーム約39B/フレーム、Editor 計測の500KB/フレームは Editor 拡張分）。
- 経路: iPhone は LAN 優先（Unity の 5007 応答で確認）・P2P 予備。10-07 実測で LAN は P2P より中央値約45ms速く p95 約半分。iPhone 60fps 既定（撮影→送信 50→37ms）。
- iPhone: 認識を bit 一致のまま 3 段階で高速化（XCTest 明るいフレーム 20.7→3.3ms、Mac native 2.3〜2.9ms）、座標を検出キューから直接送信（MainActor 待ち約10ms→ほぼ0）、UI 12Hz 集約、送信の割り当て・dispatch 削減、Release で落ちる queue precondition を DEBUG 限定に。
- Android: Debug でも -O2（-O0 で全送信が鮮度切れだった）、C++ core を4段階で高速化（Mac 12.0→約1.2ms）、回転を JNI 内へ、60fps（対応時）、Wi-Fi 低遅延ロック。JNI parity 38/38（エミュレータ）。
- Unity: 受信の割り当て削減（parser/統計 0）、HUD/UI の毎フレーム文字列・TMP 再設定削減、運営表示/F9 の OnGUI は表示中だけ、実行順固定、maxQueuedFrames=1、Player 通常 Log の stack trace 省略、任意の揺れ補正（OFF/弱/中）と遅延補正（0〜60ms、実データで効果小のため既定 OFF）、1クリック/バッチの Player ビルド、Windows firewall スクリプトを Public（ホットスポット）対応。
- 検証基盤: Unity EditMode を batchmode（プロジェクト複製）で 1694/1694、PhoneSaber PlayMode 17/17（入力の focus を無視する設定が必要）。全 PlayMode は Enlighten の native クラッシュで途中終了することがある（main でも）。
- 分析: recognition_benchmark.py（ORIGINAL 601枚、剣なし FP 赤56/83・青9/32）、修正B offline（静止青ぶれ 139→13px だが formal 5件失敗）、赤/青 FP 特徴（red-fp-features.md）、ぶれ原因（jitter-analysis.md）、予測の実データ評価（predict-real.md）。
- 不採用/保留ブランチ: opt/android-energy・opt/android-startup（探索・起動順、実機確認待ち）、opt/android-pipeline・opt/ios-debug-load（効果小）。

以下は履歴に記録された検証結果。今回の文書整理で再実行したものではない。

- 2026-10-07 / 台別位置補正 `0631d2c`: F7で4隅採取・保存・ON/OFF・台別リセット。Windows／MacのruntimeとEditModeテストはRoslynコンパイルPASS、Editor実行・実機は未了。
- 2026-10-07 / Android JNI正式回帰 `44dafd8`: API 37 ARM64エミュレータで35/35 PASS（40色別期待値・両色の端点／payload・RGBA hash・padding）。内部doubleのbit一致とCameraX実画像は対象外。
- 2026-10-07 / F8ネットワーク確認 `d1ca0dc`: 直近5秒の間隔／片道時計差・判定、両アプリの計測スイッチ。commit記録ではmerge後にUnity compile・iOS verify・Android build検証、実機比較・Editorテストは未了。
- 2026-10-07 / 剣なし座標再生 `d3b8832`: synthetic／保存track・診断bundle読取抽出、遅延・欠落・誤検出・台探索。ツール単体テストPASS（localhostのみ）、ゲーム内動作は要確認。
- 2026-10-07 / スマホ自動復帰 `18d8a78`: 送信意思保存、前面復帰／再起動、カメラ再試行、同じ台への再探索、自動ロック防止。公式verify・Android Debug build／unit tests PASS、実機復帰・長時間は未了。
- 2026-10-07 / PC監視・Macキット・ログ `7506dd6`: 異常終了5秒後の再起動、正常Quit／STOP、5世代イベントログ、背景受信・スリープ防止。両OSのRoslyn compile・Mac監視mock PASS、Windows実行は未了。
- 2026-10-07 / 反転設定 `18541a7`: Androidに左右／上下反転、両アプリで保存（既定OFF）。公式verify・Android Debug build・JVM 27件PASS、実機の向きは要確認。
- 2026-10-07 / 端末状態・Android開発モード `80a07b9`: 発熱・電池・実測fps・処理中央値、エミュレータ用非Wi-Fi手動送信。公式verify・Android Debug build・JVM 24件PASS、実機の熱は未確認。
- 2026-10-07 / 赤青支持画素走査の高速化 `eaa07cb`: 判定不変、明るいframe平均52→20.7ms。269 PNG・541遷移・27合成でSwift/C++差分0、formal 40/40・公式verify PASS。
- 2026-10-07 / F8運営表示・Windowsキット `2f3c0fb`: 台・両色の受信／解析・経路・探索・無受信警告、台別bat・Private UDP許可。Roslyn compile PASS、Windows／Editor実操作は未了。
- 2026-10-06 / 青ルール採用 `fa5db16`: deep-blue支持なしのeligible候補を却下・再選択。BLUE変更はformal 5/35＋inbox 38/234、RED変更0/269（[一覧](data/2026-10-06_blue_no_deep_support_output_changes.csv)）。formal 40/40、269 PNG×3経路・541遷移・27合成で差分0、公式verify・Android build／unit tests PASS、実機青の再試験待ち。
- 2026-10-06 / 台指定 `1132754`（merge `ad0b5a4`）、確認 `fe4cee7`: PC／スマホのA/B、P2P／診断も台を限定。Mac＋iPhoneで同じ台だけ接続することを実機確認済み、Android／Windowsは未確認。
- 2026-10-06 / 剣なし録画・手動capture受理 `689f3ee`、`2ab0813`: 163345_325／163444_198（1/100秒、P2P）は赤FP 9.7%／11.3%、青3.2%／27.3%。赤は白に近い暖色、青は深青支持0の空色。手動録画のlossless 3枚を受理する修正を実施。
- 2026-10-06 / Android core／sender／PC探索 `b0efcc2`、`a61dd1a`、`170fd79`、`33feead`: Mac Swift/C++ 248画像×3経路・541遷移・18合成でbit差分0、formal 40/40、Android build・JVM 16件PASS。bionicは237/237 selected出力一致、内部値は1ulp差。AQUOS／Windows受信は未確認。
- 2026-10-06 / 実機赤・露出・ガイドv3 `8f9a347`、`461ac03`、`4a841f4`: v2最初の剣なし赤598/598→0/598、棚の橙色FPは残存。1/100秒で横切り72%→99.6%・先端向け96.6%→100%、別の剣なし区間は3.5%／100%で画像不足。露出上限・診断schemaを修正し、v3に最初の剣なし画像と5枠を追加。公式verify PASS、v3実機待ち。
- 2026-10-06 / repo統合・運用案内 `344b4b7`、`93452ce`、`9cd6697`: Git／Unityルートを3D-Saber、ツールをPhoneSaberへ統一し、4組み合わせの手順を整理。公式verify・Android build／unit tests・変更C# compile PASS。
- 2026-10-05 / 整理: `af20a7b` shadow R7e/PF22 記録と一度きりの調査ツールを削除（旧 bundle は引き続き読める）、`376ddcc` FINDINGS に調査を統合・残作業を一本化（公式 verify 全 PASS）。
- 2026-10-05 / 残存 FP・W: `cc47927`、`5888bb5`、`5fee717`。全件分類、青 D は保留、淡い剣を失う W は不採用（研究テスト 27 PASS、D formal 40/40）。
- 2026-10-05 / 赤の認識: `43f6b61`、`06a18c5`、`8363024`。hue/深赤単独は見送り、warmNoDeepRed 採用（公式 verify 全 PASS、formal 40/40、198 枚 mismatch 0、要実機再試験）。
- 2026-10-05 / 診断転送・起動: `9661ec2`、`fb6dc17`、3D-Saber `9613d60`。ガイドの swing 4 枚を受理、Unity Play で受信側起動（会場確認未了）。
- 2026-10-04 / 撮影・解析: `904faba`、`7823070`、`ded9bba`、`2a82db8`、`60f1933`。2 本は振りの証拠不足、ガイド v2、肌・照明 FP を確認（CASE A gate 未達、実機再試験待ち）。
- 2026-10-04 / 旧 shadow 比較: `f70dcde`、`431555c`。R7e/PF22 を当時集計したが昇格せず（formal 40/40、露出感度・実機証拠不足）。
- 2026-10-04 / Mac 運用: `6ac28ca`、`a2faab5`、`d5e547c`、`96a144f`。古い受信コード・ログ隔離、一覧、2 台目 setup、負荷依存テスト対策（6ac28ca: Tools 366 件 OK・1 skip、実機運用未確認）。
- 2026-10-03 / P2P 改善: `0aeeb8f`、`1c751df`、`36d9d93`、`63eca9d`、`5723323`、`4def143`。通信優先度・復帰・Mac 固定、露出実験、runbook、検証用 Simulator 分離（verify PASS、効果は実機待ち）。
- 2026-10-03 / 解析・診断: `59383b3`、`f88fa50`、`a18816a`、`ad25839`。不採用候補 12 件、test/短録画の転送停止、解析 25 分＋high 再試行（144936 は CASE B、155919 は背景）。
- 2026-10-03 / P2P 導入: `09ffdcd`、`152397c`、`06362f7`。LAN fallback・診断 relay・RTT（導入時 XCTest 177、Python 277、formal 40/40、Release PASS）。
- 2026-10-03 / 決定性・解析基盤: `bbc1f32`、`1ad12f9`、`31ad94c`、`7449bc7`、`67597e4`、`312f63c`、`d55294c`、`4a38f53`。score 固定・bit parity・emitter/露出・区間・解析耐性・hotspot（formal 40/40、当時 verify PASS）。
- 2026-10-02 / 診断・背景再解析: `6bf2cf3`、`a93658e`、`40986a2`、`ecbebc8`、`bdd684e`、`cf3a873`、`ebb398a`、`23b667f`、`b2d74dc`。窓・CASE・メモリ・report/benchmark（XCTest 150→155、Python 182→220、formal 40/40、背景 FP 7/8）。
