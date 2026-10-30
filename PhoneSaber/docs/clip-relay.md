# 台B Windows → 台A Mac の自動クリップ中継

Unity root の `InputPoint` が受信を開始すると、TCP 8765 が空いている台でだけ中継する。
停止・Play終了・アプリ終了で中継も停止する。Scene / Prefab / ProjectSettings の設定は不要。
Mac は既存 Python 受信機を優先するため、起動時に5秒待ってから bind する。
8765 使用中なら `clip-relay listen skipped port=8765` を1行出して終了し、別portでは待ち受けない。

- 自動宛先: PhoneSaberPeerLog が検証した台A datagram の送信元 IP を優先。
  同じ logger の Settings.Peers と RankingSync.PeerAddresses も候補になる（UDP port は引き継がない）。
  自分の IP / loopback は自動候補から除外。Python の `/health` 成功を確認した候補だけに送る。
- 固定宛先: ゲーム起動前の環境変数 `PHONESABER_CLIP_RELAY_TARGET=192.168.x.x` または `IP:port`。
  IPv4 のみ。誤った指定では自動候補へ切り替えず 503 にする。
- 無効化: `PHONESABER_CLIP_RELAY=0`。環境変数は起動時に読む。
- 台Aでは従来の Python 受信機を 0.0.0.0:8765 で起動しておく。
  台BのLAN IPから受けるため、Python側の保存先・報告書の形式は従来どおり。

## 応答と資源上限

`GET /health` は、宛先が分かり、最近15秒以内の確認が有効なら
Macの200応答と、`{"status":"ready","service":"phonesaber-triage", ...}` のhealth本文をそのまま返す。
確認は通常5秒間隔。宛先不明・不通・確認失敗なら503で受信機なしを示す。
探索も中継と同じ背景スレッドで行い、health の応答待ちは最大1秒、探索は約3秒ずつに分ける。
別の中継機への接続は hop ヘッダで拒否し、循環させない。

`POST /v1/bundle` は Content-Type / Content-Length / X-PhoneSaber-* を保持する。
クリップはファイルにも一時ファイルにも書かず、全体をメモリにも貯めない。
単一の BelowNormal / IsBackground スレッドで、16 KiB のバッファとチャンクごとの休止により最大約2 MiB/s。
接続1秒、読み書きの無通信5秒で打ち切る。要求ヘッダ上限16 KiB、bundle上限512 MiB。
Macの応答は最大64 KiBを完全に確認してから、元のステータス行・Content-Type・本文を返す。
途中の507等も転送し、成功応答の欠落・切断・応答上限超過は503へ変える。
停止時はソケットを閉じ、メインスレッドでJoinやネットワーク待ちを行わない。
ゲームのフレーム処理・UDP座標受信のコードは変更していない。

iPhone は201等の成功応答、またはsessionIDが一致するduplicateの200を受領済みとみなす。
503 / 507 / 切断等なら端末に残り、既存outboxの再試行に戻る。
既存の自動再試行上限を超えた場合は端末から手動再送できる。
転送ごとに既存のイベントログへ
`event=clip-relay result=ok|fail bytes=... status=... target=...` を1行追加する。
クリップ保存は行わないが、従来の診断イベントログへの書き込みは継続する。

## 当日、Windows 実機で確認すること

1. 更新した Windows Player を起動する。管理者 PowerShell で
   `PhoneSaber/windows/Allow-PhoneSaber-Firewall.ps1` を再実行する。
   UDP既存規則に加えTCP 8765をPrivate/Public・送信元LocalSubnetで作成/更新する。
   USB共有の172.20.10.1もWindowsから見たローカルサブネットに入っているか確認する。
2. 台Aで Python 受信機を動かし、台Bから `http://<台A LAN IP>:8765/health` が200になることを確認する。
   台Bの `http://127.0.0.1:8765/health` も200になることを確認する。
   自動検出がまだなら、台AとBのpeer通信を確認するか固定宛先を設定してゲームを再起動する。
3. Wi-Fiに繋がない iPhone 15 Pro Max（USB共有、172.20.10.1）からクリップを送る。
   台Bのevent行、iPhoneの受領表示、台Aのautoclips保存・報告書を照合する。
   Windowsにクリップや一時bundleが生成されないことも確認する。
4. 台A受信機を止めて送るとiPhoneに残り、受信機再開・再試行後に保存されることを確認する。
   途中切断後の再送ではduplicate応答も正しく受領済みになることを確認する。
5. 送信中のゲームfpsと両色UDPの欠落・遅延を実測する。USB共有とLANの経路、
   ファイアウォールの実際の許可、同時転送時の負荷はMac上の自動テストでは保証できない。
