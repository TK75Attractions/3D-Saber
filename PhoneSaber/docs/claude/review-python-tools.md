# PhoneSaber Python tools レビュー(2026-10-10)

対象: `PhoneSaber/tools/*.py` と `PhoneSaber/ios/PhoneSaberSender/Tools/*.py`。
当日運用と遅延解析で使うもの(latency_report、P2P bridge launcher、診断受信 server と launcher)を優先した。
recognition、UDP payload、bridge の転送仕様は変更していない。

## 前提の確認

- Unity は bridge launcher と受信 launcher を `/usr/bin/python3`(macOS 標準 **3.9.6**)で起動する
  (`PhoneSaberP2PBridgeProcess.cs`、`PhoneSaberTriageReceiverLauncher.cs`)。受信 server は launcher の
  `sys.executable` で動くので、これも 3.9 で動く。
- 全 `.py` を 3.9 の parser で構文確認し、問題はなかった。受信側の import と
  receiver/launcher/protocol/report/overview/status の test 132 件も 3.9 で PASS した。
- `env -i` の最小環境でも stdout と locale は UTF-8 なので、`縁日` を含む path を print しても落ちない。

## 指摘と対応

| # | 重要度 | 対象 | 内容 | 対応 |
|---|---|---|---|---|
| 1 | 高 | `phone_saber_triage_receiver.py` | request handler に socket timeout が無かった。upload の途中で Wi-Fi が切れると(FIN/RST が届かない)、handler thread が永久に block していた。その間 `uploadsInProgress` が 1 のまま残るので、`/health` が idle にならない。そのため launcher は、古いコードで動く受信側の自動再起動をずっと拒否していた。fd と thread も残り続けていた。 | 修正済み。`timeout = 150 s`(iPhone 側 `timeoutIntervalForRequest = 120 s` より長いので、生きている upload は切らない)。timeout は既存の 400 経路に入る。staging dir も削除される。 |
| 2 | 中 | `latency_report.py` | `--since` を UTC の時刻文字列と**文字列比較**していた。`+09:00` 付きの指定や `2026/10/08` のような書式だと、黙って全件が落ちる。その結果「latency-block がありません」という誤った結論になっていた。 | 修正済み。時刻として解釈する(`Z` と `+09:00` を受け付け、時差なしは従来どおり UTC)。読めない値は exit 2 で知らせる。 |
| 3 | 中 | `latency_report.py` | 書込途中で切れた最終行(例 `route=P2P` で終わる行)を受理していた。切れた route と、欠けた platform(`?`)から偽の条件行ができていた。さらに stream key が変わるので、偽の測定区切りもできていた。 | 修正済み。Unity が最後に書く `misses=` の無い latency-block 行は捨てる(形式は導入時の 8389adc から不変)。 |
| 4 | 低 | `latency_report.py` | 存在しない log を指定すると traceback で落ちていた。 | 修正済み。stderr に知らせて exit 2。 |
| 5 | 低 | `phone_saber_selection_replay.py` `load_sequences` | non-UTF-8 の frame context(`UnicodeDecodeError` は `JSONDecodeError` ではない)、list の JSON、色の値が dict でない frame で例外が出て、tool 全体が落ちていた。session report と jitter_analysis も同じ関数を使う。 | 修正済み。その file や frame だけを飛ばす。 |
| 6 | 低 | `unity_playmode_batches.py` | `-testFilter` が1件も当たらないと(クラス名変更など)、0 件実行で exit 0 になり、偽の成功になっていた。 | 修正済み。total=0 なら exit 1 にする。 |
| 7 | 情報 | `latency_report.py` | CRLF と non-UTF-8 は、既存の `strip()` と `errors="replace"` で正しく読めていた。 | 回帰 test を追加した。 |
| 8 | 情報 | `phone_saber_p2p_bridge.py` | 3.9 互換、`xcrun` の絶対 path、build 中の SIGTERM で swiftc を止める処理、pid 名の一時 file と atomic replace、`--` 以降の引数の受け渡しを確認した。明確な robustness bug は無かった。cache key は source だけで、compile flag は含まない(flag を変えたら `--rebuild` が必要)。 | 変更なし。 |
| 9 | 情報 | `unity_playmode_batches.py` | Unity の subprocess に timeout が無いので、hang すると止まらない。妥当な上限値の根拠が無いので、値は入れていない。 | 未修正(記録のみ)。 |
| 10 | 情報 | percentile 実装 | 各 tool の percentile は nearest-rank で一貫している(`recognition_benchmark`、`fixb_offline`、`analyze_session_metadata`、`endpoint_prediction_eval`)。空入力も扱えている。`jitter_analysis` と `selection_replay` は四捨五入 index を使うが、doc の記述と一致している。 | 変更なし。 |

## 検証

- 追加した test: `test_phone_saber_receiver_health`(停止した upload)、`test_latency_report`(4件)、
  `test_phone_saber_selection_replay`(壊れた context)、`test_unity_playmode_batches`(0 件実行)。
  latency の 3 件は旧コードで FAIL することを確認した。
- `/usr/bin/python3`(3.9.6)と `python3`(3.13)の両方で unittest を実行した。
