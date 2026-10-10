# Unity PhoneSaber 受信側レビュー（2026-10-10）

対象: `Assets/Scripts/Managers/Inputsystem/Input/{InputPoint.cs, PhoneSaber*.cs}`、`Assets/Scripts/Saber/SaberInputBridge.cs`、
関連テスト `Assets/Tests/Editor/PhoneSaber*Tests.cs`・`Assets/Tests/PlayMode/PhoneSaber*.cs`。
branch `opt/review-unity-phonesaber`。UDP の payload 形式・port（5005 / 5006 / 5007）、運営操作（F8 開閉 / F7 位置補正 / F9 遅延テスト）、
認識結果・座標変換・フィルタ/予測の既定 OFF 経路は変更していない。

## 結論

- 受信 thread のライフサイクル（bind 失敗の backoff 再試行、意図的停止、Join timeout 後の再起動、シーン跨ぎ、RED/BLUE の独立性）は、
  既存の PlayMode テストを含めて堅牢。`Thread.Abort` はもう使っていない（ルートの `CLAUDE.md` の記述は古い）。
- 修正した不具合は2件（いずれも当日の障害時に効く）:
  1. イベントログの書込失敗で行が消える／Windows で世代交代が詰まると以後の記録が全部止まる。
  2. 受信 thread が lifecycle lock を持ったまま外部 process（dns-sd / P2P bridge）を起動・停止する間、
     `SaberInputBridge` が毎フレーム読む `InputPoint.StationLabel` が同じ lock で待たされ、main thread が止まる。
- 残りは「当日のリスク・既知の制約」として記録（コードは変更せず）。特に **built .app では P2P 予備経路と診断受信側が既定で起動しない**点は、
  runbook の手順どおり `PHONESABER_P2P_BRIDGE_SCRIPT` の設定が必要。

## 修正した項目

### F1. イベントログ: 書込失敗で行を失う・世代交代失敗で記録が止まる（中）

- 場所: `PhoneSaberEventLog.cs` `Flush()`（修正前 l.81–99）。
- 修正前: キューから行を `Dequeue` してから書き込むため、`FileStream` が例外を出すとその行は消えていた（書込先の一時的な失敗のたびに1行ずつ欠落）。
  さらに現行ファイルが 1MB 以上の状態で `Rotate()`（`File.Move`）が失敗すると、以後のすべての Flush が同じ所で例外になり、
  1回ごとに1行ずつ捨てながら記録が永久に止まる。Windows では、スタッフが古い世代（`events.log.1` など）を
  削除共有なしで開いているだけで `File.Move` が失敗し得る。
- 障害シナリオ: 当日の receiver-failed / input-silent などの肝心な記録が、ログ書込の一時失敗や世代交代の失敗で抜ける／止まる。
- 修正: 書込に失敗した行を `retryLine` に保持して次回の Flush で最初に書き直す（順序維持・二重書きなし）。
  世代交代に失敗しても記録は止めずに現行ファイルへ追記を続け、`LastError` に `rotate <例外名>` を出す
  （運営表示 F8 の「ログ書込失敗」に出る。上限超過は許容）。成功時の挙動・形式・容量判定は不変。
- テスト（EditMode, `PhoneSaberEventLogTests`）:
  - `FailedWriteKeepsTheLineAndWritesItInOrderAfterRecovery`: 書込先をディレクトリにして失敗させ、回復後に2行が元の順序で1回ずつ書かれる。
  - `BlockedRotationKeepsAppendingAndReportsTheError`: 世代1の位置を空でないディレクトリにして世代交代を失敗させ、
    行が現行ファイルへ追記され `LastError` が `rotate ` で始まる。

### F2. `StationLabel` 等の読み出しが受信 thread の lifecycle lock で待たされる（中〜低）

- 場所: `InputPoint.cs` `StationLabel` / `BonjourPublisherRunning` / `DiscoveryResponderRunning`（修正前 l.118–133）、
  呼び出し側 `SaberInputBridge.cs` l.173（`ResolveFilterMode`、ブレード表示中は毎フレーム・各色）。
- 修正前: 3つとも `networkLifecycleLock` を取っていた。この lock は受信 thread の `SetReceiverAlive`（l.917–）が保持したまま
  `PhoneSaberBonjourPublisher.Start/Stop`（`Process.Start` / `Kill` + `WaitForExit(1000)`）、
  `PhoneSaberDiscoveryResponder.Stop`（socket close + `Join(1000)`）、`PhoneSaberP2PBridgeProcess.Start/Stop`
  （`pkill` 起動・待機、`Kill`、`WaitForExit` 各最大 1 秒）を実行する。
- 障害シナリオ: プレイ中に片色の receiver が異常終了→再 bind すると、そのたびに dns-sd / P2P bridge の停止・再起動が走り、
  その間 `SaberInputBridge.Update` が `StationLabel` で待たされて画面が数十 ms〜最悪数秒止まる。
- 修正: `phoneSaberStation` を `volatile` にして lock なしで読む（書くのは main thread の `StartNetworkServices` だけで、
  受信 thread は以前から lock なしで読んでいた）。`BonjourPublisherRunning` / `DiscoveryResponderRunning` も参照を
  `Volatile.Read` で取ってから問い合わせる（停止直後の古い参照でも、その object 自身が停止済みとして false を返す）。
  値・意味は不変。
- テスト（EditMode, 新規 `PhoneSaberReceiverStateReadTests`）: 別 thread が `networkLifecycleLock` を 1.5 秒保持している間に、
  3つの読み出しが 500 ms 未満で正しい値を返す。修正前のコードでは約 1.5 秒待って失敗する。

## 記録だけした項目（未修正）

重要度は当日への影響。行番号は修正後のもの。

| # | 重要度 | 場所 | 内容・障害シナリオ | 推奨 |
|---|---|---|---|---|
| R1 | 中 | `PhoneSaberP2PBridgeProcess.cs` l.67 `ResolveLauncherPath`、`PhoneSaberTriageReceiverLauncher.cs` l.21 | built `.app`（`Builds/Mac/3D-Saber.app`）では `Application.dataPath` が `.app/Contents` なので、既定の launcher 探索先 `.app/PhoneSaber/...` は存在しない。`Start-Saber-*.command` は `open` 経由で起動し `PHONESABER_P2P_BRIDGE_SCRIPT` を渡していないため、**本番 Player では P2P 予備経路と診断受信側が起動しない**（警告ログのみ、LAN 受信は動く）。mac/README・runbook には記載済み。 | 当日前に Player で F8 の「P2P bridge」が ON になるか確認する。自動化するなら `.app` から見た repo 相対 (`<dataPath>/../../../../PhoneSaber/...`) を候補に加えるか、launcher で `open --env` を使う（挙動変更なのでユーザー判断）。 |
| R2 | 低 | `InputPoint.cs` l.408–452 | port 競合（Editor と Player の二重起動など）が続くと、色ごとに 2 秒ごと `receiver-failed` + `receiver-restart` の2行（約 200 byte）を記録し続ける。1色で約 350 KB/時、5 世代 × 1 MB の容量は約 7 時間で一巡し、朝の記録が押し出される。 | 同一理由の連続失敗は回数を間引いて記録する（例: 1,2,4,8…回目と理由変化時）。運営表示では「受信機 停止」で気付ける。 |
| R3 | 低 | `InputPoint.cs` l.917–936 | 片色の receiver が落ちるたびに Bonjour / 探索応答 / P2P bridge をすべて停止し、再 bind 時に再起動する（P2P で接続中の iPhone は両色とも一度切れる）。設計どおりだが、失敗が繰り返すと process の起動・停止が続く。F2 で main thread への波及は解消済み。 | 現状維持。頻発するならデバウンスを検討。 |
| R4 | 低 | `PhoneSaberDiscoveryResponder.cs` l.80–92 | 受信 loop が共有の `running` フラグだけを見ている。Stop の `Join(1000)` が timeout した直後に Start されると、古い thread が新しい `running=true` を見て閉じた socket で例外→即 continue の空回り（CPU 100%）になり得る。また SocketTimeout 以外の例外が持続した場合も sleep なしで回る。通常は close で recv が即座に解除されるため発生は極めて稀。 | 起動ごとの世代番号（または socket の同一性）を loop 条件にし、想定外例外では短い sleep を入れる。5007 固定のためテストは実 port を使う必要があり、今回は見送り。 |
| R5 | 低 | `PhoneSaberP2PBridgeProcess.cs` l.280、`PhoneSaberBonjourPublisher.cs` | bridge が非 0 で終了すると `startFailed` により Player 再起動（Editor は再 compile）まで再起動しない。code 0 終了や dns-sd の終了（mDNSResponder 再起動など）も、次の receiver 再 bind まで再公開されない。 | F8 の「Bonjour / P2P bridge 停止」を当日チェック項目にする。必要なら Update から一定間隔で再公開する watchdog を追加。 |
| R6 | 低 | `PhoneSaberPacketParser.cs` l.59–60 | `float.TryParse(NumberStyles.Float, Invariant)` は `NaN` / `Infinity` を受理する（旧 parser との parity テストで意図的に維持）。iOS/Android の送信は整数ピクセルなので本番では起きないが、Python 等の開発用送信元が `nan` を送ると、フィルタ/予測 OFF の既定経路では `transform.position` に NaN が渡り Unity のエラーが出る（ON の経路は非有限値を素通し）。 | payload 仕様を変えない範囲で、`SaberInputBridge` で非有限の端点をそのフレームだけ不採用にするのが安全（既定経路の挙動変更になるため今回は見送り）。 |
| R7 | 低 | `PhoneSaberDiscoveryResponder.cs` l.138–145 `PhoneSaberStation.Resolve` | 台名が不正（全角「Ａ」、空白入り、17 文字以上）だと黙って「台なし」になり、両台が同名 `Phone Saber Unity` を公開し得る（dns-sd が自動改名）。環境変数が不正値／空文字のときは PlayerPrefs にも戻らない。F8 の見出し「台: 指定なし」とイベントログ `station=none` では分かる。 | 生の値が空でないのに正規化で空になった場合は警告ログを出す。当日は F8 で台表示を確認。 |
| R8 | 低 | `InputPoint.cs` l.875–876、`StopNetworkServices` | Join はRED/BLUE 順に各 2 秒まで待つので、最悪 4 秒 main thread が止まる。timeout 後に OnDestroy 等で再度呼ばれると、再び最大 4 秒待つ。close で recv が解除されるため通常は数 ms。 | 現状維持（timeout 後も再起動しない設計は正しい）。 |
| R9 | 低 | `InputPoint.cs` l.390 | `StartNetworkServices` が lifecycle lock を持ったまま `/bin/sh` を起動し `WaitForExit(2000)` する（Mac のみ・30 秒に1回まで）。起動時のみで通常は数十 ms。 | 現状維持。 |
| R10 | 情報 | `InputPoint.cs` l.467 | 受信 thread で `ReceiveFrom(ref EndPoint)` が packet ごとに `IPEndPoint` を確保する（Mono の仕様）。約 120 packet/s で数 KB/s。main thread のフレーム内確保ではなく、受信統計・座標経路に影響しない。 | 対応不要。 |
| R11 | 情報 | `PhoneSaberOperatorOverlay.cs` l.254 | F8 表示中は `DrawFilter` が OnGUI のたびに `PlayerPrefs` を読み、キー文字列を確保する。表示中のみ。 | 対応不要（必要なら `PhoneSaberFilterSettings.Revision` でキャッシュ）。 |

### Windows Player と Mac の違い（確認結果）

- 受信 socket は送信しないため、Windows 特有の UDP `WSAECONNRESET`（ICMP port unreachable）は受信 loop に影響しない。
  探索応答（5007）は送信するが、例外は loop 内で捕捉して継続する。
- Bonjour / P2P bridge / 診断受信側の起動は `UNITY_EDITOR_OSX || UNITY_STANDALONE_OSX` でのみ有効。Windows では F8 に「未対応」と出るのが正常。
- ファイアウォール（Public プロファイルで UDP 受信が黙って捨てられる）はコードでは検知できない。runbook と `windows/README.md` の
  `Allow-PhoneSaber-Firewall.ps1` / Private 設定の手順どおり。F8 の「1秒以上届いていません」警告が手掛かり。
- イベントログは `Application.persistentDataPath/PhoneSaber/events.log`（Windows は `AppData\LocalLow\...`）。F1 で Windows のファイルロックによる停止を解消。

### 既定 OFF の同一性（フィルタ・予測）

- `SaberInputBridge.Update` は filter OFF・予測 0 ms のとき端点に一切演算を加えず、`ResetPrediction()` だけを行う。
  既存テスト `BladeUpdateKeepsRawObservationAndOffEndpointBits`（符号付きゼロを含むビット一致）等で担保されており、今回の変更も OFF 経路に触れていない。
- 位置補正の保存（`PlayerPrefs` + `Save()`）、台ごとのキー分離、無効時の生座標維持も既存テストで担保。

## 検証

- Unity 6000.3.9f1 batchmode、プロジェクトのコピー（scratchpad の buildproj2）で実行。ユーザーの Editor が開いている本体は開いていない。
- EditMode 全体: **1698/1698 PASS**（基準 1694 + 新規 4）。
- PlayMode（`PhoneSaberOperatorControlsPlayTests` と `PhoneSaberReliabilityPlayTests`）: **17/17 PASS**（基準どおり）。
- 赤確認: 修正前の `PhoneSaberEventLog.cs` / `InputPoint.cs` に戻したコピーでは、新規テストのうち
  `FailedWriteKeepsTheLineAndWritesItInOrderAfterRecovery`・`BlockedRotationKeepsAppendingAndReportsTheError`・
  `StationAndServiceStatusDoNotWaitForReceiverLifecycleLock` の3件が失敗し、修正後は通ることを確認。
- `PhoneSaber/tools/verify_phone_saber.sh`（iOS/Python 側）は、PhoneSaber/ 配下のコード変更がない（本書のみ）ため未実行。
