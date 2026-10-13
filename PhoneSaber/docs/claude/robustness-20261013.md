# PhoneSaber robustness — 2026-10-13

対象は自作 festival tools。Mac の loopback・動的ポート・temporary inbox だけを使用した。
GUI、Dock icon、Simulator、browser、Unity Editor/Player は起動していない。
commit / push / 公式 verify は実施していない（operator がレビュー・検証・commit する）。

## 発見事項（重大度順、行番号は修正後）

### 1. P2: 完了した診断 relay 接続のオブジェクトを保持

`PhoneSaber/ios/PhoneSaberSender/Tools/p2p_bridge/PhoneSaberP2PBridge.swift:172`、`:177`、`:240`。
HTTP upload を half-close → 応答受信 → 正常完了しても、300秒後の `asyncAfter` が
`close` と両側の `NWConnection` を保持していた。短い upload を繰り返すと RSS が増える。
**FD が未閉鎖という発見ではない**。socket は cancel されており、FD 数は修正前も安定していた。

弱参照で両 connection を観測する通常テスト
`DiagnosticsRelayTests.test_completed_upload_releases_connection_objects_before_timeout` は、
修正前 `released=false` で FAIL、修正後 `released=true` で PASS。
修正は cancel できる DispatchSourceTimer と完了時の timer/state handler 解放。
300秒の期限、half-close、byte 転送、backpressure は維持した。

同一 seed・同じ負荷・120秒の前後比較（自己計測を足した tmp コピーを build）:

| 項目 | 修正前 | 修正後 |
|---|---:|---:|
| 正常 BLUE 照合転送 | 4,070 / 4,070 | 4,043 / 4,043 |
| 異常/valid 混合 UDP | 48,840 | 48,516 |
| 完了 half-close upload | 4,070 | 4,043 |
| 最初の RSS | 9,248 KiB | 9,168 KiB |
| 約30秒の RSS | 25,744 KiB | 17,904 KiB |
| 約60秒の RSS | 35,872 KiB | 19,360 KiB |
| 最終サンプル RSS | 51,104 KiB（116.69秒） | 19,712 KiB（116.95秒） |
| 数値 FD の最大 | 73 | 74 |
| 最終累積 CPU 時間 | 6.256秒 | 7.664秒 |

最終 CPU 時間 / 経過時間は約5.4% / 6.6%。負荷の並行実行もあり、CPU 改善とは主張しない。
RSS がゼロへ戻ることや任意の長期負荷で上限内に収まることも主張しない。
8個の読まない TCP 受信先、低速送信、48 UDP source、65,000 byte datagram、消失/reconnect を混在。
RED の転送先 UDP socket は bind したまま一度も読まず、BLUE が継続転送されることを確認した。
大容量 TCP upload の backpressure は別途既存の32 MiB試験で確認した。

### 2. P2: HTTP body の宣言範囲外の manifest を待つ

`PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_triage_protocol.py:151`。
`Content-Length: 8`、body=`PSBT` + BE UInt32(100) を送り、接続を開いたままにすると、
旧コードは body に含まれない100 byteを `_read_exact` で待つ。
受信側の30秒 idle timeout まで upload thread と受信中カウンタが残り、同種の接続が積み重なる。

manifest の長さが `content_length - 8` を超えた時点で BundleError にする検査を追加した。
`test_manifest_larger_than_http_body_is_rejected_before_reading_it` は旧コードで
read 回数 `[4,4,100]` の FAIL、修正後 `[4,4]` の PASS。
正常な bundle や宣言サイズ上限には変更なし。

### 3. P2: 不正 JSON の型/深さで upload handler が例外終了

`PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_triage_protocol.py:155`、`:231`、`:308`、`:357`
（pack 側は `:92`）。
summary の `images[0].color=[]` / `{}` で `TypeError: unhashable type`。
manifest / summary / frame context の10,000段 JSON array で `RecursionError`。
HTTP handler は BundleError/OSError だけを捕捉するため、その request thread が traceback を出して
正常な400応答を返さず終了する。**受信 server 全体の停止ではない**。
pack_bundle も深い summary で意図した BundleError にならなかった。

色名の str 検査と JSON decode の ValueError/RecursionError → BundleError 変換を追加。
一時ディレクトリの削除、原子的な公開、重複確認、schema/サイズの許容範囲は維持した。
通常テストで深い manifest / summary / context / packaging と配列・object 色名を再現する。
HEAD 版をメモリに読み込んだ baseline の6テストは **1 FAIL + 5 ERROR**、修正後は全 PASS。
HTTP 400、ENOSPC 後の空 inbox、受信中カウンタ0、/health 200 も検証した。

### 4. P3: status が壊れた JSON / 巨大な整数ログで終了

`PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_status.py:195`、`:224`、`:353`、`:494`、`:500`。
`/health` が `[]` / `null`、`changedFiles:3` を返すと AttributeError/TypeError。
analysis_report.json が `[]` / `null` / 深い array、または reasonCodes が `[{},3]` でも例外。
Unity log の `RED=` に10,000桁の整数を入れると Python 3.12 の整数変換上限による ValueError。
operator が状態確認するコマンド自体が途中終了する。

JSON root / reasonCodes / changedFiles の型検査、深い JSON の捕捉、整数変換失敗の無視を追加。
既存の通常データの表示・判定閾値は変えていない。
baseline の4テストは subtest を含め **9 ERROR**、修正後は全 PASS。

### 5. P3（未修正・提案）: latency-block の NaN が集計結果へ入る

`PhoneSaber/tools/latency_report.py:97`、`:115`。
正常行の `median-ms=100` を `median-ms=nan` にすると、`float()` は成功し、
通常の sample として保存される。最終レポートの中央値が `nan` になる。`inf` も同様に再現した。

集計対象の変更は operator に見えるため **production code は変更していない**。
最小通常テスト `test_nonfinite_block_measurement_does_not_poison_report` は expectedFailure とし、
未解決を明示した。提案 diff（未適用）:

```diff
--- a/PhoneSaber/tools/latency_report.py
+++ b/PhoneSaber/tools/latency_report.py
@@
         except (KeyError, ValueError):
             continue
+        if not all(math.isfinite(block[key]) for key in ("median", "p95", "rate")):
+            continue
         previous = sessions.get(stream)
```

閾値変更ではなく壊れた測定値の除外だが、集計対象と測定区切りへの影響を operator が確認する必要がある。

### 6. P3: 起動待ちに失敗したテストが bridge を残す

`PhoneSaber/ios/PhoneSaberSender/Tools/test_phone_saber_p2p_bridge.py:87`。
`P2PBridgeTests.setUp()` は `start_bridge()` の後で stop cleanup を登録していた。
listener ready の待機 assertion に失敗すると cleanup が未登録で子プロセスが残る。
上記の一過性失敗が実際にこの状態を作った。

cleanup 登録を start の前へ移動。`BridgeFixtureTests.test_setup_failure_also_stops_its_bridge` は
起動失敗を mock で再現し、旧コードでは terminate 呼出し0回で FAIL、修正後は terminate/wait が1回で PASS。
成功時の起動・ping・RED/BLUE 転送も再確認した。

今回残った **PID 56008** は lsof で自分の失敗 run の temporary binary/log と照合したが、
`kill -TERM 56008` は sandbox に `operation not permitted` と拒否された。
**この PID の終了だけ operator が sandbox 外で行う必要がある**（再利用されていないことを確認してから終了）。
ほかの作業で起動している bridge は停止していない。

## 実施した build / test と結果

環境: macOS、Python 3.13.11（初回 fuzz/テスト）、Python 3.12.1（最終 unit/追加試験）、
`xcrun swiftc -O`、Unity 6000.3.9f1 の同梱 Mono/compiler。
全 generator の seed は **20261013**。コマンドは `tools/robustness/README.md` に記載。
120秒はランダム部分の時間で、Swift/C# の build 時間と relay の終了確認は別。

| 実行 | 結果 |
|---|---|
| `test_robustness_regressions test_phone_saber_triage_protocol test_phone_saber_status`（Python 3.12） | **73/73 PASS** |
| `test_phone_saber_p2p_bridge`（Python 3.12、実 Swift build） | **30/30 PASS**（完了解放、重複/逆順、idle、reconnect、listener restart、half-close、32 MiB backpressure、concurrent large reply 等） |
| `test_latency_report test_protocol_contracts`（Python 3.12） | 24件: **23 PASS + 1 expected failure**（未修正 NaN）; 実 Swift/C# 共通ベクトルも build/run |
| `test_ios_p2p_encoding`（Python 3.12） | **1/1 PASS**、4,626 encoding と旧 byte列の一致 |
| `triage_fuzz.py --seconds 120` 修正後 | **PASS**、1,278,874 iteration、65,442 accepted mutation、600 truncation boundary、部分 inbox なし |
| `receiver_disconnects.py`（Python 3.12） | **PASS**、1 PNG/context 付き body の1,471 byte境界、2.51秒。400応答・空 inbox、ENOSPC、同時 retry の201/200、/health、未処理例外0 |
| `run_host.py csharp --seconds 120`（最終版） | **PASS**、7,134,742 random比較、5,083,100 stepで conflict lock を通過。detect-only拒否0、ownership解放、counter/timing/event不変条件 |
| `run_host.py swift --seconds 120`（SplitMix64） | **PASS**、10,667,986 iteration。codec、座標整形、malformed、filter oracle |
| Swift の明示的 UInt32/UInt64 端値・30秒境界追加後 `--seconds 2` | **PASS**、136,714 iteration（先行120秒版の random処理は同一） |
| `log_fuzz.py --seconds 120`（Python 3.12、64 MiB実ファイルを含む） | **PASS**、757,001 iteration。latency CLI / status tail、非UTF-8、途中行、巨大整数 |
| `relay_soak.py --seconds 120` 前後 | **PASS**、上表。正常 phone 転送が継続 |
| cleanup 回帰 + 成功時 ping / RED・BLUE 転送（最終追加） | **3/3 PASS** |
| README の回帰テスト discover コマンド | **11/11 PASS** |
| 新規 Python の AST parse / `git diff --check` | **PASS** |

合計、最終 unit の重複を除くと **129件（128 PASS、1 expected failure）**。
途中の Python 3.12 統合実行102件では、1件の UDP test が listener ready を10秒以内に観測できず
setUp で FAIL（転送の assertion に至る前）。同じ bridge suite の再実行は30/30 PASS。
この起動待ちの一過性失敗は残し、timeout や期待値を緩めていない。追加の cleanup 回帰1件も PASS。

試験 script の準備中には Mac の UDP send buffer 上限（SO_SNDBUF を指定して65,000 byteを送信）、
sandbox の `ps` 禁止（自己計測へ変更）、Swift の throwing autoclosure、
Python HTTPConnection の context manager 非対応を修正した。
これらは試験コードの問題で、production defect の件数に含めていない。
初回の不完全な run は資源前後比較や PASS 件数に使っていない。

## 変更範囲・未実施・残るリスク

production 差分は **Mac diagnostics relay の解放、triage protocol の異常入力拒否、status の例外耐性**。
テストの起動失敗 cleanup も修正し、最小再現 unittest、seed固定 generator、README と本報告を追加した。
認識 ranking / eligibility / scoring / PCA / threshold、UDP `x1,y1,x2,y2`、正式 corpus、
Unity scenes / prefabs / .meta / ProjectSettings は変更していない。
並行作業対象 `PhoneSaberInputStats.cs`、`UDPSender.swift`、`P2PProtocol.swift` は読み取り/build のみ。
今回これら3ファイルに対して修正が必要な再現不具合は見つからず、適用・提案 diff はない。
`DetectionCore.swift` も座標整形の host build 用に読むだけで、変更も認識の実行もしていない。

- **未実施:** 公式 `verify_phone_saber.sh`、XCTest/Simulator、iOS Release build、formal lossless 40件、
  Unity Test Runner / Editor / Player、iPhone実機、AWDL、Windows/2台PCでの試験。
  operator が公式 verify を実施する。loopback 結果を実無線・展示会負荷の保証にしない。
- 300秒の実時間 timeout 到達まで待つ試験は実施していない（各 randomized run は120秒）。
  完了時の早期解放と既存300秒設定を確認したが、長時間 slow upload の打切りは要追加確認。
- `announcedSessions` は session ごとの初回ログを保持し、接続/同時uploadの総数にも hard cap はない。
  短時間試験で forwarding 停止は出なかったが、無制限の session churn / 同時接続は別の資源リスク。
  cache expiry、peer/upload上限、受信側の絶対upload期限は正常端末の受理・ログ・slow upload の扱いを
  変えるため **提案のみ、実装していない**。既存の idle timeout だけでは少量の継続 trickle を止めない。
- status のログ tail は bounded だが、analysis_report.json は全体を読む。非常に大きな JSON に対する
  明示的サイズ上限は今回追加していない。表示や受理の変更になる上限値は operator と決める必要がある。
- timing / sender の試験は、実 receive loop の契約どおり単調増加する時刻と非nullの送信元を使用した。
  座標整形は実機範囲内の入力を対象とし、任意の巨大 Int / 不正解像度を新しく許容する変更はしていない。
- seed は生成順序を固定する。時間内件数・OS network scheduling・資源値までは固定されない。

生ログ/サンプルは今回の Mac の `/private/tmp/phonesaber-*` にあり、repo には入れていない。
主なもの: `phonesaber-relay-before.json` / `phonesaber-relay-after.json`、
`phonesaber-python-tests-final.log`、`phonesaber-relay-tests-final.log`、
`phonesaber-csharp-complete.log`、`phonesaber-swift-splitmix.log`、
`phonesaber-disconnect-final.log`、`phonesaber-log-fuzz-final.log`。
