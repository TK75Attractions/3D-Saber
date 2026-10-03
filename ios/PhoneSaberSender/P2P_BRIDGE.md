# PhoneSaber P2P(peer-to-peer Wi-Fi)通信

iPhone → Mac の座標送信だけを、学校 Wi-Fi やテザリングを通さず、Apple の peer-to-peer Wi-Fi
(AWDL)で直接送る**追加機能**です。Mac は学校 Wi-Fi につないだまま、インターネット通信(Codex / Claude など)は
学校 Wi-Fi 経由で続けられます。P2P が使えないときは、従来の LAN(Bonjour / 手動 IP)の UDP に自動で戻ります。

## 通信フロー

```text
PhoneSaberSender (iPhone)
  └─ 認識(FrameProcessor など、変更なし)→ 座標文字列 "x1,y1,x2,y2"(変更なし)
       ├─ [P2P 優先] P2PSender ── Network.framework UDP, includePeerToPeer=true, cellular 禁止
       │      └─ Bonjour `_phonesaber-p2p._udp` ── Mac: PhoneSaberP2PBridge
       │                                              ├─ RED  → 127.0.0.1:5005
       │                                              └─ BLUE → 127.0.0.1:5006 → Unity InputPoint.cs(変更なし)
       └─ [fallback] UDPSender(既存) ── Bonjour `_phonesaber._udp` / 手動 IP ── Mac:5005 / 5006 → Unity
```

- **service type は別にしました**: `_phonesaber-p2p._udp`(bridge が公開)。既存の `_phonesaber._udp`(Unity の
  `PhoneSaberBonjourPublisher` が公開)とは別なので、既存の discovery を誤判定しません。
- **payload は不変**: bridge は datagram の本文を 1 byte も変えずに転送します。計測モードの `ts=...;x1,y1,x2,y2` も同じです。
- **P2P 内部の形式**: 1 本の UDP 接続に、20 byte の header(magic `PSP2`、version、種別、色、送信元 session、
  sequence)+ 本文。定義は `PhoneSaberSender/P2PProtocol.swift`(iPhone と bridge の両方がこの 1 ファイルを使います)。
- **Unity 側の変更はありません**。`InputPoint.cs` は 0.0.0.0:5005/5006 で受信し、送信元を限定しないので、
  bridge からの localhost の UDP をそのまま受け取ります。

## P2P 接続のしかた

- iPhone: `NWBrowser` で `_phonesaber-p2p._udp` を探し(`includePeerToPeer = true`)、見つけた bridge へ
  `NWConnection`(UDP、`includePeerToPeer = true`、`prohibitedInterfaceTypes = [.cellular]`)を張ります。
  複数見つかったとき(Mac が 2 台など)は、最初は service 名の辞書順で最初のものを選び、その名前に**固定**します。
  固定した Mac が Bonjour の一覧から消えたときだけ、別の Mac に切り替えます(後から見つかった Mac に奪われません)。
  P2P 接続中は Bonjour の検索を止め、P2P が使えなくなったら再開します。
- Mac: `NWListener`(UDP、`includePeerToPeer = true`)が同じ service を公開します。
- 同じ Wi-Fi にいるときは、通常の Wi-Fi 経由でつながることもあります(UI に `P2P Connected (en0 · Saber Mac)` のように
  経由した interface と接続先の service 名を表示します。interface は経路が変わるたびに更新し、
  link-local アドレスの scope(`%awdl0` など)を優先します)。iPhone が Wi-Fi のどこにも参加していない場合は、AWDL(`awdl0`)になります。
- **送信は認識とは別の queue** で行います。色ごとに「送信中の 1 件 + 最新の 1 件」しか保持しないので、
  詰まっても古い座標が溜まりません(既存の UDPSender と同じ方針)。

## 診断 bundle(Debug Recording の自動転送)も P2P で送る

Debug Recording の Stop 後に送る triage bundle も、P2P で届くようにしました。

```text
iPhone(DebugBundleTransfer)
  ├─ [P2P 優先] Network.framework TCP(includePeerToPeer、cellular 禁止)── Bonjour `_phonesaber-dp2p._tcp`
  │      └─ Mac の P2P bridge(Unity が自動起動)内の「診断 relay」── 127.0.0.1:8765(既存の受信側、変更なし)
  └─ [fallback] 従来の LAN 転送(Bonjour `_phonesaber-diag._tcp` → HTTP)
```

- iPhone は、転送のたびにまず P2P の relay を約 3 秒探します。見つかれば P2P で送り、見つからなければ従来の LAN 転送を行います。
- 送る中身(HTTP の `POST /v1/bundle` と bundle の byte 列)は LAN 転送と同じです。relay は byte を変えずに中継します。
- 受信側(Start PhoneSaber の receiver)はそのままで、変更はありません。receiver が起動していないと relay は接続を閉じ、iPhone は LAN を試してから再試行します。
- Mac のログ(Unity の Console)には `[P2P] diag relay: upload from … closed after N bytes (done)` が出ます。

## fallback の条件

座標は、次の順に、使える最初の経路で送ります。

1. **P2P**: bridge が ping に pong を返している間だけ使います。UDP の「ready」だけでは使いません。
   - ping は 0.25 秒ごと。最後の pong から **1.5 秒** で P2P をやめ、LAN へ戻ります(ゲームの再起動は不要)。
   - pong が **4 秒** 来ない(または新しい接続で 4 秒以内に最初の pong が来ない)と、接続を張り直します。
   - pong が戻れば、自動で P2P に戻ります。bridge を再起動した場合も同じです。
     ただし一度 stale になった後(または接続を張り直した後)は、**0.5 秒以内の間隔で 3 回続けて** pong が来るまで
     P2P に戻しません(1.5 秒の境目で P2P と LAN が交互になるのを防ぐため)。起動後最初の接続だけは pong 1 回で使います。
   - 座標の送信が **0.5 秒** 終わらない(ping は通るのに座標が詰まる)ときは、その接続を捨てて LAN に戻し、張り直します。
   - 張り直しの間隔は 0.25 秒から 2 倍ずつ伸ばし、最大 4 秒です。pong が 1 回来たら 0.25 秒に戻します。
   - アプリが前面に戻ったときは、最近 pong がある接続には ping をすぐ送り、そうでなければ待たずに張り直します。
2. **LAN(Bonjour)**: 既存の `_phonesaber._udp` で見つけた Mac の IP。
3. **手動 IP**: 既存の手動入力欄。

その他の動作:

- P2P と LAN は同時には使いません(Unity に同じ座標が二重に届かないようにするため)。
- P2P に戻ったとき、LAN 側で送信待ちの座標(port が ready でなかったもの)は捨てます。
  あとで LAN が ready になっても、古い座標が新しい座標の後に届くことはありません。
- P2P が送る直前に使えなくなり、LAN も未設定のときは、その座標は送らずに数えるだけです(`p2pNoRouteCount`)。
- 「P2P優先」toggle を OFF にすると、従来とまったく同じ LAN だけの動作になります(設定は保存されます)。
- P2P が ON なら、LAN の送信先が未発見のままでも開始できます(従来は「Macを検索中」で開始できませんでした)。
  その場合、P2P が届くまでは座標を送りません。あとから LAN の Mac が見つかれば、LAN の fallback が自動で加わります。

## 表示とログ

- iPhone の画面:「経路: P2P Connected (awdl0 · <Mac の service 名>) / LAN Connected / Manual IP / Reconnecting / Failed」と、
  P2P の状態(`P2P Searching`、`P2P Connecting`、`P2P Reconnecting`、`P2P Failed`、`P2P Off`)。
  ローカルネットワークの許可がないときは `P2P Failed (ローカルネットワークの許可が必要: 設定 > PhoneSaberSender)`。
  許可されれば自動で検索を再開します(待機中の検索は 10 秒ごとに作り直します)。
  P2P 接続中は、ping の往復時間(RTT)も出す:`P2P RTT 中央値 … ms / p95 … ms / 最大 … ms / ping欠落 …%`
  (直近 40 回の ping。2 秒以内に pong が来なかった ping を「欠落」に数える)。
- Mac の bridge のログ(毎フレームは出しません):
  - `[P2P] listening ...`、`[P2P] Bonjour registered ...`
  - `[P2P] peer connected ...`、`[P2P] peer alive ...`
  - `[P2P] RED received ...` / `[P2P] BLUE received ...`(送信元 session ごとに最初の 1 回だけ)
  - `[P2P] no ping for 3s; iPhone falls back to LAN (fallback to LAN)`
  - `[P2P] listener failed: …; restarting in …s`(待ち受けが失敗しても process は終了せず、同じ port で張り直す。sleep からの復帰などで起こりうる)
  - 10 秒ごとの集計(`RED=… BLUE=… stale=… malformed=… maxGapMsRED=… maxGapMsBLUE=…`)。
    `maxGapMs` は、座標が届く間隔の最大値(2 秒を超える間隔は saber が見えていないとみなして除外)。
    AWDL が一瞬止まると、ここが大きくなる。

## 起動方法(Mac)

**通常は何もしなくてよい**: Unity(3D-Saber)で Play を押すと、`InputPoint` が UDP 受信を始めるのと同時に
`PhoneSaberP2PBridgeProcess` が bridge を自動で起動します。Play を止める、Unity を終了する、script を
再 compile する、のいずれかで自動的に止まります。Unity が異常終了しても、bridge は `--exit-with-parent` で自分から終了します。

- Unity は、Unity project(`3D-Saber`)と同じ階層にある `school-festival` repo の
  `ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py` を `/usr/bin/python3` で実行します。
  場所が違う場合は、環境変数 `PHONESABER_P2P_BRIDGE_SCRIPT` で launcher の path を指定します。
- 自動起動を止めたいときは、環境変数 `PHONESABER_P2P_BRIDGE=0` を設定します。
- bridge のログは Unity の Console に `[PhoneSaber][P2P] ...` として出ます。
- launcher が見つからない場合や起動に失敗した場合は、Console に警告を1回だけ出します。P2P なしで、従来の LAN 受信だけで動きます。

Unity を使わずに単独で動かす場合(診断など)は、次を実行します。

```bash
"ios/PhoneSaberSender/Tools/Start PhoneSaber P2P Bridge.command"
```

初回は Swift の bridge を自動で build します(`~/Library/Caches/PhoneSaber/p2p-bridge/` に保存し、
source が変わったときだけ build し直します)。そのため、Unity からの初回起動だけ数秒〜十数秒かかります。
止めるときは Ctrl-C です。引数は `--` の後ろに書きます。例: `-- --name "Saber Mac"`。

macOS が「ローカルネットワーク」へのアクセス許可を求めたら、bridge を起動したアプリ(Terminal など)を許可してください。

## 遅延について(2026-10-03 の実測)

- Mac を学校 Wi-Fi につないだまま AWDL(`awdl0`)で送ると、座標の数は正常(約 32 個/秒)でも、
  10 秒ごとの集計で **`maxGapMs` が 170〜300 ms、ときに約 1 秒**になった。30 fps の本来の間隔は 33 ms。
  - Mac の Wi-Fi が「学校の Wi-Fi のチャンネル」と「AWDL のチャンネル」を行き来するために起きる、AWDL の性質。
  - この詰まりが、ゲーム内で剣が「ラグい」原因。iPhone の認識処理時間(中央値約 28 ms)は以前と同じ。
- 対策として、iPhone と bridge の両方の通信に `serviceClass = .interactiveVoice`(低遅延の traffic class)を付けた。
  これは OS へのヒントにすぎないため、効果は実機で `maxGapMs` と iPhone の `P2P RTT` を比べて確かめる。
- それでも詰まる場合の選択肢:
  - iPhone と Mac を同じ Wi-Fi に入れて LAN で送る(「P2P優先」を OFF)。
  - USB ケーブルでつなぐ方式(未実装。最も遅延が安定する)。

## 実機での確認手順

| 手順 | 内容 | 期待結果 |
|---|---|---|
| A | Mac を学校 Wi-Fi に接続する。iPhone は学校 Wi-Fi に**参加しない**(Wi-Fi 自体は ON のまま)。Personal Hotspot は OFF。 | |
| B | Unity で Play を押す(bridge は自動で起動する)。 | Unity の Console に `[PhoneSaber][P2P] listening ...` と `Bonjour registered` が出る |
| C | iPhone で PhoneSaberSender を起動し、「P2P優先」を ON にして開始する。初回はローカルネットワークの許可を求められる。 | |
| D | 画面の「経路」を確認する。 | `P2P Connected (awdl0 · <Mac の service 名>)`。Mac に `peer connected` と `RED received` / `BLUE received` が出る。`P2P RTT` の中央値と最大値、Mac の `maxGapMs` を控えておく(LAN と比べるため) |
| E | Mac で Codex やブラウザを使いながら saber を振る。 | Unity に座標が届き続ける。インターネットも使える |
| F | iPhone のモバイルデータ通信を OFF にする。 | P2P の送信が続く(cellular は最初から禁止しています) |
| G | `PHONESABER_P2P_BRIDGE=0` で Unity を起動し直す(または Terminal で bridge の process を止める)。 | 約 1.5 秒で「経路」が LAN か Reconnecting になる。LAN の Mac が見つかっていれば、Unity への送信が LAN で続く。bridge が戻ると P2P に戻る |

補足:

- G で LAN に戻るには、iPhone と Mac が同じ LAN にいて、Unity の Bonjour(`_phonesaber._udp`)が見つかっているか、
  手動 IP が入っている必要があります。A の構成(iPhone が学校 Wi-Fi にいない)では LAN の経路がないので、
  `Reconnecting` で待ち、bridge が戻ると P2P で再開します。
- 「P2P優先」を OFF にすると、従来の LAN だけの動作になります。

## テスト

- `PhoneSaberSenderTests/P2PTransportTests.swift`: 形式の往復と payload 不変、不正な packet、重複と順序入れ替え、
  liveness による fallback と再接続の判定、loopback の偽 bridge を使った RED/BLUE の配送、bridge の停止と再開、
  最新値の優先、CameraViewModel の経路選択(P2P 優先、P2P が落ちたら LAN、P2P OFF なら従来どおり)。
  2026-10-03 の追加: Mac の固定選択、復帰の hysteresis、張り直しの backoff、interface 表示、許可エラーの文言、
  送信 watchdog(送信 hook で詰まりを再現)、前面復帰、P2P 復帰時の LAN 待ち座標の破棄、LAN なしの fallback の計数。
- 任意実行: `TEST_RUNNER_PHONESABER_P2P_DISCOVERY_TEST=1` を付け、Mac で
  `--red-port 47431 --blue-port 47432` の bridge を起動しておくと、Simulator が実際の Bonjour で bridge を見つけ、
  localhost への転送まで確認します。
- `Tools/test_phone_saber_p2p_bridge.py`: 実際の Swift bridge を build して起動し、loopback で、
  RED/BLUE の振り分け、本文の不変、不正な packet、重複、アプリ再起動(新 session)、2 台の peer、bridge の再起動、
  ping/pong、log の頻度制限、build の cache を確認します。
- 実機でしか確認できないこと: AWDL での接続、学校 Wi-Fi との同時使用、iPhone がどの Wi-Fi にも参加していない状態、
  Mac 2 台での選択、経路変更時の interface 表示、ローカルネットワーク拒否時の表示と許可後の復帰、P2P 接続中の検索停止。

## rollback

- その場で戻す: iPhone の「P2P優先」を OFF にします。従来の LAN だけの動作になり、bridge も不要です。
- Mac 側: 環境変数 `PHONESABER_P2P_BRIDGE=0` で Unity の自動起動を止めます。Unity の受信と既存の Bonjour は変わりません。
- Unity のコードを戻す: 3D-Saber で、bridge 自動起動を追加した commit を `git revert` します
  (`PhoneSaberP2PBridgeProcess.cs` とその test、`InputPoint.cs` の起動・停止の4行)。
- コードを戻す: P2P を追加した commit を `git revert <commit>` します。追加したのは新しいファイル
  (`P2PProtocol.swift`、`P2PSender.swift`、`Tools/p2p_bridge/`、`Tools/phone_saber_p2p_bridge.py`、
  `Start PhoneSaber P2P Bridge.command`、tests)と、`CameraViewModel.swift` の経路選択、`ContentView.swift` の表示、
  `BonjourInfo.plist` の service 1 行、project の file 登録です。
