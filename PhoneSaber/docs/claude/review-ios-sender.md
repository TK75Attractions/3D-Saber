# iPhone 送信アプリ（認識以外）レビュー — 2026-10-10

対象: `PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/` の CameraViewModel・FrameProcessor（検出以外）・
UDPSender（LANLivenessProbe / CoordinateDelivery を含む）・P2PSender・P2PProtocol・EventRecovery・ContentView・
BonjourDiscovery、および `PhoneSaberSenderTests`。
DetectionCore.swift / BGRADetection.swift は変更していない。認識結果、UDP payload、ポート（RED 5005 / BLUE 5006 / 探索 5007）も変えていない。

重大度: 高 = 当日に座標が長く止まる・落ちる / 中 = 数秒の途切れ・遅い経路への固定・係員が誤判断しうる表示 / 低 = 表示の揺れ・わずかなメモリ・ほぼ起きない競合。

## 修正したもの

| # | 場所 | 重大度 | 内容 | テスト |
|---|---|---|---|---|
| F1 | CameraViewModel.swift:1086 `finishCameraStart`、EventRecovery.swift:56 | 中 | 古い start の完了で新しい run のカメラを止めていた | `EventRecoveryTests.testStaleCameraStartCompletionNeverStopsANewerRun` |
| F2 | CameraViewModel.swift:1322-1325 `receiveBonjourUpdate` | 中 | Mac の IP が変わっても LAN 生存確認が旧 IP を見続けた | `DetectionCoreTests.testRunningBonjourHostUpdateRebuildsBothDestinations`（追記） |
| F3 | UDPSender.swift:629-656 `LANLivenessProbe` | 中（予防） | 受信待ちが error で終わると、その接続では二度と応答を受けなかった | `P2PTransportTests.testLANLivenessProbeRebuildsTheConnectionAfterAReceiveError` |
| F4 | UDPSender.swift:613-622 `LANLivenessProbe.tick` | 低 | host 切替と tick が重なると旧 host の接続が残った | 同上（host 変更後に旧応答を数えないことを確認） |
| F5 | CameraViewModel.swift:940 `networkStateLabel` | 中（表示） | LAN で送っているのに見出しが「NETWORK READY (P2P)」 | `P2PTransportTests.testNetworkStateLabelFollowsTheVerifiedLANLikeTheRoute` |
| F6 | UDPSender.swift:180 状態通知 | 低 | 解放済みの旧接続の通知を「現在の接続」と誤判定しえた | なし（解放タイミング依存で決定的に再現できない） |
| F7 | UDPSender.swift:71, 98 | 低 | テスト用ハンドラ履歴が本番でも configure ごとに増えた | 既存 `sendStaleUpdateForTesting` テスト（Debug）で維持 |

### F1 停止→即再開で新しい run のカメラが止まる
- 旧コード: `guard lifecycleGeneration == lifecycle, running else { sessionRunner.stop(); return }`。
- 状況: 開始直後（`startRunning` が終わる前、端末で約0.3〜1秒）に「停止」→「開始」、または詳細設定の fps を素早く 60→30→60 と切り替える（`selectCameraFPS` は `stop(); start()` を同期で行う）。
- 流れ: session キュー上で start#1 → stop()（同期）→ start#2 の順に並ぶ。start#1 の完了は MainActor に遅れて届き、世代不一致なので `sessionRunner.stop()` を投入する。これは start#2 の後ろに並ぶため、新しい run の session を止める。完了 #2 は成功として届くので UI は「CAMERA STARTING/LIVE」のまま、2秒後に STALLED と判定され、backoff（1秒）後の再起動まで約3秒以上座標が途切れる。
- 修正: 判定を `CameraStartCompletionPolicy` に分け、「新しい run が送信中なら何もしない」「送信停止中なら従来どおり止める」にした。stop() は既に `stopSynchronously()` で前の start の後に止めているので、送信中の場合に止める必要はない。
- 実機確認: シミュレータにはカメラがないため、完了順序そのものはテストできない。判定関数を単体テストした。

### F2 Bonjour で IP が変わったとき LAN 生存確認が旧 IP のまま
- 旧コード: 送信中に Bonjour の解決 IP が変わると `sender.updateHost(update.ip)` だけを呼び、`lanProbe` は旧 IP へ 5007 の探索を送り続けた。
- 状況: Mac の DHCP 更新・Wi-Fi 再接続で IP が変わる、または有線＋無線の Mac で 30 秒ごとの再探索が別の IPv4 を先に返す。
- 影響: 座標は新 IP へ LAN で届くのに、旧 IP からは応答がないため「LAN 未確認」になる。P2P が使えると遅い P2P（中央値で約45ms遅い）へ回り続ける。P2P が無ければ LAN で送るので途切れはしない。
- 修正: `lanProbe.start(host: update.ip)` を追加（同じ host なら何もしない既存仕様のまま）。

### F3 LAN 生存確認の受信ループが error で止まる（予防的修正）
- 旧コード: `receiveMessage` が error を返すと再登録せず、tick は state が `.failed/.cancelled` のときしか接続を作り直さない。
- 状況: 受信 error の後も接続が `.ready` のままなら、その後に Unity が起動しても応答を受け取れない。LAN 未確認のまま P2P へ回り続ける。
- 確認状況: Mac の loopback で「閉じたポートへ送信」を試した範囲では、Network.framework は受信 error を出さなかった。端末の Wi-Fi 上で ICMP port unreachable などにより error になるかは未確認。
- 修正: 受信 error の接続を `connection` から外して cancel する。次の tick（0.5秒後）で作り直す。現在の接続でないもの（停止済み）には何もしない。
- テスト: 空きポートの偽 Unity（NWListener）に実ソケットで問い合わせ、生存になること、error 相当の後に接続が作り直されること、`aliveWindow` を過ぎても新しい接続の応答で生存のままであることを確認した。

### F4 LAN 生存確認の tick と host 切替の競合
- 旧コード: tick は host を読んだ後、ロックの外で接続を作って最後に `self.connection` へ代入していた。その間に `start(host: 新)` が入ると、旧 host への接続が新しい run の接続として残り、旧 host へ送り続ける（失敗するまで作り直さない）。停止・host 変更後に届いた旧接続の応答も `lastReply` を更新できた。
- 修正: 代入時に host が変わっていないことをロック内で確認する。変わっていれば作った接続を使わない。応答は現在の接続から来たものだけを数える。
- 頻度: 0.5秒ごとの tick の数十µs の窓なので、ほぼ起きない。F2 で host 変更が増えるため合わせて閉じた。

### F5 「NETWORK READY (P2P)」と実際の経路の不一致
- 旧コード: `p2pState.isConnected` なら常に「NETWORK READY (P2P)」。一方、経路（`CoordinateDelivery.route`・`transportLabel`）は 2026-10-08 から「Unity 応答で確認済みの LAN」を優先する。
- 状況: Mac で P2P bridge と Unity の両方が動いている通常構成では、見出しが「(P2P)」、下の行が「経路: LAN Connected」と食い違う。係員が「P2P で送っている」と誤判断しうる。
- 修正: 見出しも確認済み LAN を優先する。LAN 未確認なら従来どおり「(P2P)」。

### F6 UDPSender の状態通知で nil === nil
- 旧コード: `[weak connection]` を `self.connections[port] === connection` で比べていた。旧接続が解放済み（nil）で、再接続待ちのため `connections[port]` も nil だと一致とみなし、旧接続の `cancelled/failed` で状態表示や phase を上書きしえた（再接続待ちの間「cancelled」「failed」と表示）。
- 修正: `guard let connection` を先に置く。現在の接続は辞書が保持しているので nil にならず、正しい通知は落とさない。

### F7 テスト用の更新ハンドラ履歴
- `updateHandlersForTesting.append(onUpdate)` が本番でも configure（手動 IP 時は経路変化ごと、Bonjour 再出現ごと、開始ごと）に積み上がっていた。1件は小さく実害はほぼないが、長時間運用で単調増加する。`#if DEBUG` に限定した。

## 修正しなかったもの（理由つき）

| # | 場所 | 重大度 | 内容 | 直さなかった理由 |
|---|---|---|---|---|
| N1 | CameraViewModel.swift:848-870 | 中 | 経路変化通知のたびに（自動モードでは）LAN を止め、Bonjour 再解決まで LAN が無い | 意図的な設計（Wi-Fi 切替で再探索）。間引くと本物の切替を見逃す危険があり、実機ログで不要な通知の頻度を確かめてから |
| N2 | CameraViewModel.swift:1141, 226, 1021 | 低〜中 | 中断中も watchdog が復旧を始め、中断終了の通知を backoff 中は使わない | 下記。端末の中断の挙動を実機で確かめてから |
| N3 | CameraViewModel.swift:2616 | 中（環境次第） | Bonjour の IPv4 は `addresses` の先頭。有線＋無線の Mac では iPhone から届かない側を選びうる | 選び方の変更は送信先の仕様変更に当たる。当日は Mac を1つのネットワークだけにする運用で避ける |
| N4 | UDPSender.swift:726-740 | 低 | P2P→LAN へ切り替えるとき、P2P に残る1件（色ごと）が LAN の新しい座標の後に届きうる | 最大1フレーム・色ごと1件。逆向き（LAN→P2P）は破棄済み |
| N5 | CameraViewModel.swift:1908 | 低 | P2P 接続のたびに LAN の待ち座標を捨てる。LAN が確認済みで LAN を使っている間も捨てる | 捨てるのは待ち（最大1件／色）だけで、ready のポートではほぼ空。経路側の切替時破棄と重複しているが、片方だけ外すと別の順序問題を作りうる |
| N6 | P2PSender.swift:564 | 低 | 接続が `.ready` に戻るたびに受信ループを追加する（waiting→ready を繰り返すと同時受信待ちが増える） | 機能上の害はなく、増え方も ready 遷移の回数まで。実機で waiting→ready の頻度を見てから |
| N7 | CameraViewModel.swift:992-995 | 低（表示） | 非アクティブ→アクティブ（コントロールセンターを下ろす等）だけでも「自動で送信を再開しました」が出て、停止まで残る | 誤解は小さい（緑の通知）。カメラ状態の条件を足すと実機の中断挙動に依存するため N2 と一緒に見直す |
| N8 | CameraViewModel.swift:2560, 1303 | 低（表示） | 送信中は30秒ごとに Bonjour を再起動し、そのたびに「Mac: 未発見 IP: -」へ一瞬戻る | 送信先（host）は保持されるので送信には影響しない。保持すると、Mac が消えたときに古い名前が残る |
| N9 | ContentView.swift:73 | 低（操作） | 自動モードで手動 IP 欄を最後の1文字まで消すと、即座に Bonjour の IP が入り直す | 自動へ戻す操作を兼ねている。手動 IP は全選択して上書き入力すればよい（送信中は編集不可） |
| N10 | テスト全般 | 低 | CameraViewModel 内の LAN 生存確認は 127.0.0.1:5007 を探索する。開発 Mac で Unity が動いていると、P2P 前提の既存テストが LAN を選んで失敗しうる | テスト環境の注意点。生存確認を注入できるようにするのは今回の範囲外 |

### N1 経路変化での LAN 停止
- `pathMonitor` の通知は、使用中の Wi-Fi に変化がなくても、セルラーの可用性や `isExpensive` などの変化で届く。自動モードでは毎回 `lanConfigured = false; sender.stop(); lanProbe.stop()` を行い、Bonjour の再解決（通常は数百ms以内）まで LAN が無い。その間は P2P、P2P も無ければ noRoute で座標を捨てる。
- 当日の影響: 電波の弱い場所ではセルラーの出入りで1日に何度か短く途切れる可能性がある。
- 次の手: 送信中の経路変化通知を実機ログ（status・インターフェース名・gateway）で数え、不要な通知が多ければ「状態・Wi-Fi インターフェース・gateway が同じなら止めない」を検討する。

### N2 カメラ中断中の復旧
- 中断（例: 高温で `videoDeviceNotAvailableDueToSystemPressure`）が始まると、`cameraState.canRetry` が真のため 250ms 以内に watchdog が backoff 付き再起動を始める（:1141）。中断が終わったとき、状態がすでに `.interrupted` でないと `interruptionEnded` は何もしない（:1021）。
- `.recovering` の間に届いた frame は lifecycle では数えない（:226）。ただし座標は新しい世代で送られ続ける（復旧予約時に processor をリセットするため）。
- 影響: 中断が終わって AVCaptureSession が自動で再開しても、表示が RECOVERING/STALLED のまま残り、残りの backoff（最大30秒）後に不要な再起動が1回入る（約1秒途切れる）。
- 中断中の再起動を止めるかは、端末で中断と再開の通知がどう届くかを確かめてから決める。

## 確認して問題なしとしたもの
- CoordinateDelivery のロック順: delivery lock → (LANLivenessProbe lock / P2PSender usableLock)。UDP・P2P キューでは delivery lock を取るだけで、delivery lock を持ったまま `queue.sync` する経路はない。デッドロックはない。
- 検出キューからの送信: 設定はロック下のスナップショットで、`processorGeneration` と `lifecycleGeneration` の一致を enqueue 時と送信直前（`isCurrent`）の両方で確認している。停止・復旧（`resetProcessor` が先に `delivery.suspend()`）の後に旧世代の座標は出ない。
- FrameProcessor の mailbox / reset: reset は `queue.sync` で処理中のフレームを待ち、世代を進める。旧世代の結果は delivery と UI の両方で拒否される。
- Release でのクラッシュ要因: 対象範囲の `precondition` は BonjourDiscovery の main thread 確認だけで、呼び出し元は全て MainActor。強制アンラップは `Data(count: 20+)` の baseAddress・`CameraPreview` の layer 型・`RecoveryBackoff` の直前代入値だけで、いずれも nil にならない。Network の完了キュー検査は Debug 限定（822af79）。
- 長時間のメモリ: P2PRoundTripStats（40件窓・2秒で失効）、P2PSequenceFilter（64件で掃除）、DeviceHealthMeter（5秒窓・上限1200）、UI 診断（120件窓・2秒窓）はいずれも上限がある。F7 以外に単調増加はなかった。
- P2P の send watchdog・backoff・hysteresis は既存テストのとおりに動く。`stop()` の `queue.sync` は MainActor からだけ呼ばれ、P2P のコールバックは `Task { @MainActor }` で非同期に戻すため、デッドロックしない。

## 検証
- `bash PhoneSaber/tools/verify_phone_saber.sh`（worktree root、2026-10-10）: iOS XCTest PASS、Detection PASS、Lossless PASS（40/40）、Tools PASS、iOS Release PASS、Diff Check PASS。Unity 3段は NOT_RUN（範囲外）。
- 追加・追記したテスト4件はいずれも上記の XCTest で実行され、PASS した。検証後、一時シミュレータ（PhoneSaber-verify-*）は残っていない。
- 自己レビュー: DetectionCore.swift / BGRADetection.swift と FrameProcessor の検出・追跡部分には差分がない。payload の生成（`payload` / `timestampedPayload`）、ポート 5005/5006/5007、探索文字列、CoordinateDelivery の経路判定式も変えていない。
