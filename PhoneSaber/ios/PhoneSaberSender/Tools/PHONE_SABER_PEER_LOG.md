# 台どうしの診断ログ

台名 A / B を設定した Player で自動起動する。Editor は RankingSync と同じく
`Tools > Ranking Sync > Editor でも同期する` を入れた場合だけ動く。
`-rankingSync off`・診断用起動・Test Runner では自動起動しない。
Windows は Unity Player への UDP **5018** の受信を許可する。
ファイル共有・認証情報・外部サービスは不要。

相手の探し方は、稼働中 IPv4 の directed broadcast、255.255.255.255、
受信元、RankingSync が知る IP。手動指定は共通の `-rankingPeer <IP>` または
PlayerPrefs `RankingSync.Peers`。ランキング側のポート指定は引き継がず 5018 へ送る。
起動後に RankingSync で追加した相手も3秒ごとに取り込む。
台名が未設定、同名の台どうしでは共有しない。

保存済みの診断行を writer の通知から上限256行のキューへ入れ、背景スレッドで
3秒ごとに送受信する。入力・判定には使用しない。
通常の perf / latency-estimate に加え、途切れ・送信元変更・hitch・判定補正・
judge-timing・剣の安定性・スマホ露光・アプリ状態・起動終了を対象にする。
曲とシーンの変更時だけ `event=peer-context scene=Game song=<URLエスケープした曲ID>` も記録する。

## 形式（UTF-8、版1）

```text
PHONESABER_PEERLOG 1 station=B session=<32桁GUID> seq=123 lines=2 part=1/1
<元のevents.logの行>\n
<元のevents.logの行>\n
```

- UDP 1通は1200 byte以下。本文は最大960 byteずつ、長い行は `part=1/3` 等へ分割する。
  同じ論理メッセージの断片は同じ seq。UTF-8 は復元後に検査する。
- 通常は8行以下で束ねる。1行4096 byte以下、断片最大5、未完成32件・10秒まで。
  行数0・空本文は相手を探すための通知で、events.log には残さない。
- session は起動ごと。station＋session の直近64 seq で重複を除き、順序逆転も受け付ける。
  履歴は最大32 session、無通信10分で解放する。既知の受信先は最大16件。
- 受信は元の station とヘッダを照合し、診断の event だけを採用する。
  全相手合計で毎秒32行まで。peer-log を再送しない。
- 記録例：`<受信UTC> station=A colour=PEER event=peer-log from-station=B <元の行>`。
  元の行の時刻は台Bの UTC。受信時刻との差を片道遅延として扱わない。
- UDP の再送・過去ログの同期は行わない。相手不在中の記録や欠落は復元できず、
  高頻度の judge-timing 等は上限で落ちる。操作パネルには最新の推定遅延または packet rate と元の UTC を1行表示する。

## Mac での集計

```bash
python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_peer_log_report.py /path/to/events.log
python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_peer_log_report.py /path/to/events.log.1 /path/to/events.log --json
# 台Bに残った台Aの診断
python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_peer_log_report.py /path/to/events.log --station A
```

元の台の時刻順に並べ、Game の peer-context ごとに曲・プレイ回を分ける。
推定遅延の中央値、packet/s、gap / silent / back / sender-change / hitch を表示する。
JSON には最大値・サンプル数・PC遅延・判定補正・判定誤差も含む。
文脈を受信できなかった部分は「曲外・不明」。途切れの件数は受信済み行の下限。

```bash
bash PhoneSaber/tools/unity_copy.sh editmode PhoneSaber
python3 -B -m unittest discover -s PhoneSaber/ios/PhoneSaberSender/Tools -p test_phone_saber_peer_log_report.py -v
```

Unity テストは Broadcast=false / LoopbackOnly=true / 空きポートを使う。
ネットワーク不要のプロトコル・キュー試験は Offline=true を使う。
