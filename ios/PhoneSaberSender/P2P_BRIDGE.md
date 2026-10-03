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
  複数見つかったときは、service 名の辞書順で最初のものを使います。
- Mac: `NWListener`(UDP、`includePeerToPeer = true`)が同じ service を公開します。
- 同じ Wi-Fi にいるときは、通常の Wi-Fi 経由でつながることもあります(UI に `P2P Connected (en0)` のように
  経由した interface を表示します)。iPhone が Wi-Fi のどこにも参加していない場合は、AWDL(`awdl0`)になります。
- **送信は認識とは別の queue** で行います。色ごとに「送信中の 1 件 + 最新の 1 件」しか保持しないので、
  詰まっても古い座標が溜まりません(既存の UDPSender と同じ方針)。

## fallback の条件

座標は、次の順に、使える最初の経路で送ります。

1. **P2P**: bridge が ping に pong を返している間だけ使います。UDP の「ready」だけでは使いません。
   - ping は 0.25 秒ごと。最後の pong から **1.5 秒** で P2P をやめ、LAN へ戻ります(ゲームの再起動は不要)。
   - pong が **4 秒** 来ない(または新しい接続で 4 秒以内に最初の pong が来ない)と、接続を張り直します。
   - pong が戻れば、自動で P2P に戻ります。bridge を再起動した場合も同じです。
2. **LAN(Bonjour)**: 既存の `_phonesaber._udp` で見つけた Mac の IP。
3. **手動 IP**: 既存の手動入力欄。

その他の動作:

- P2P と LAN は同時には使いません(Unity に同じ座標が二重に届かないようにするため)。
- 「P2P優先」toggle を OFF にすると、従来とまったく同じ LAN だけの動作になります(設定は保存されます)。
- P2P が ON なら、LAN の送信先が未発見のままでも開始できます(従来は「Macを検索中」で開始できませんでした)。
  その場合、P2P が届くまでは座標を送りません。あとから LAN の Mac が見つかれば、LAN の fallback が自動で加わります。

## 表示とログ

- iPhone の画面:「経路: P2P Connected (awdl0) / LAN Connected / Manual IP / Reconnecting / Failed」と、
  P2P の状態(`P2P Searching`、`P2P Connecting`、`P2P Reconnecting`、`P2P Failed`、`P2P Off`)。
- Mac の bridge のログ(毎フレームは出しません):
  - `[P2P] listening ...`、`[P2P] Bonjour registered ...`
  - `[P2P] peer connected ...`、`[P2P] peer alive ...`
  - `[P2P] RED received ...` / `[P2P] BLUE received ...`(送信元 session ごとに最初の 1 回だけ)
  - `[P2P] no ping for 3s; iPhone falls back to LAN (fallback to LAN)`
  - 10 秒ごとの集計(`RED=… BLUE=… stale=… malformed=…`)

## 起動方法(Mac)

```bash
"ios/PhoneSaberSender/Tools/Start PhoneSaber P2P Bridge.command"
```

初回は Swift の bridge を自動で build します(`~/Library/Caches/PhoneSaber/p2p-bridge/` に保存し、
source が変わったときだけ build し直します)。止めるときは Ctrl-C です。
引数は `--` の後ろに書きます。例: `-- --name "Saber Mac"`。
Unity と既存の Start PhoneSaber(triage receiver)とは独立しているので、並べて起動してかまいません。

macOS が「ローカルネットワーク」へのアクセス許可を求めたら、bridge を起動したアプリ(Terminal など)を許可してください。

## 実機での確認手順

| 手順 | 内容 | 期待結果 |
|---|---|---|
| A | Mac を学校 Wi-Fi に接続する。iPhone は学校 Wi-Fi に**参加しない**(Wi-Fi 自体は ON のまま)。Personal Hotspot は OFF。 | |
| B | Mac で `Start PhoneSaber P2P Bridge.command` を起動し、Unity を再生する。 | `[P2P] listening ...` と `Bonjour registered` が出る |
| C | iPhone で PhoneSaberSender を起動し、「P2P優先」を ON にして開始する。初回はローカルネットワークの許可を求められる。 | |
| D | 画面の「経路」を確認する。 | `P2P Connected (awdl0)`。Mac に `peer connected` と `RED received` / `BLUE received` が出る |
| E | Mac で Codex やブラウザを使いながら saber を振る。 | Unity に座標が届き続ける。インターネットも使える |
| F | iPhone のモバイルデータ通信を OFF にする。 | P2P の送信が続く(cellular は最初から禁止しています) |
| G | bridge を Ctrl-C で止める。 | 約 1.5 秒で「経路」が LAN か Reconnecting になる。LAN の Mac が見つかっていれば、Unity への送信が LAN で続く。bridge を再起動すると P2P に戻る |

補足:

- G で LAN に戻るには、iPhone と Mac が同じ LAN にいて、Unity の Bonjour(`_phonesaber._udp`)が見つかっているか、
  手動 IP が入っている必要があります。A の構成(iPhone が学校 Wi-Fi にいない)では LAN の経路がないので、
  `Reconnecting` で待ち、bridge が戻ると P2P で再開します。
- 「P2P優先」を OFF にすると、従来の LAN だけの動作になります。

## テスト

- `PhoneSaberSenderTests/P2PTransportTests.swift`: 形式の往復と payload 不変、不正な packet、重複と順序入れ替え、
  liveness による fallback と再接続の判定、loopback の偽 bridge を使った RED/BLUE の配送、bridge の停止と再開、
  最新値の優先、CameraViewModel の経路選択(P2P 優先、P2P が落ちたら LAN、P2P OFF なら従来どおり)。
- 任意実行: `TEST_RUNNER_PHONESABER_P2P_DISCOVERY_TEST=1` を付け、Mac で
  `--red-port 47431 --blue-port 47432` の bridge を起動しておくと、Simulator が実際の Bonjour で bridge を見つけ、
  localhost への転送まで確認します。
- `Tools/test_phone_saber_p2p_bridge.py`: 実際の Swift bridge を build して起動し、loopback で、
  RED/BLUE の振り分け、本文の不変、不正な packet、重複、アプリ再起動(新 session)、2 台の peer、bridge の再起動、
  ping/pong、log の頻度制限、build の cache を確認します。
- 実機でしか確認できないこと: AWDL での接続、学校 Wi-Fi との同時使用、iPhone がどの Wi-Fi にも参加していない状態。

## rollback

- その場で戻す: iPhone の「P2P優先」を OFF にします。従来の LAN だけの動作になり、bridge も不要です。
- Mac 側: bridge を起動しなければ、何も変わりません(Unity と既存の Bonjour は触っていません)。
- コードを戻す: P2P を追加した commit を `git revert <commit>` します。追加したのは新しいファイル
  (`P2PProtocol.swift`、`P2PSender.swift`、`Tools/p2p_bridge/`、`Tools/phone_saber_p2p_bridge.py`、
  `Start PhoneSaber P2P Bridge.command`、tests)と、`CameraViewModel.swift` の経路選択、`ContentView.swift` の表示、
  `BonjourInfo.plist` の service 1 行、project の file 登録です。
