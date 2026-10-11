# iPhone 送信処理の連続運転テスト（Mac 上、2026-10-11、Claude）

`PhoneSaber/tools/sender_soak.py` は、本番の FrameProcessor / payload / CoordinateDelivery / UDPSender / LANLivenessProbe を
`PhoneSaber/ios/PhoneSaberSenderTests/SenderSoakHarness.swift` と一緒に Mac 用に build し、60fps で loopback の UDP 受信へ送り続ける。
ポートは自動割当で、Unity の 5005/5006/5007 は使わない。カメラ、無線、P2P、SwiftUI は対象外（実機の発熱・電池・Wi-Fi は別に確認する）。

```bash
python3 PhoneSaber/tools/sender_soak.py --out /tmp/sender-soak --seconds 900 --variants release
```

## 結果（Release 相当 -O、各 15 分、10 秒ごとに記録）

| 指標 | Unity 応答あり（開始→終了） | Unity 応答なし（開始→終了） |
|---|---|---|
| footprint | 18.6 → 19.3 MB（+32 KB/分、後半 +2.7 KB/分） | 18.6 → 19.4 MB（+40 KB/分、後半 +18 KB/分） |
| resident | 37.9 → 27.3 MB | 37.8 → 27.2 MB |
| スレッド数 | 9 → 10 | 8.7 → 8 |
| file descriptor | 11 → 11 | 10 → 10 |
| 送信（赤＋青） | 118.0 → 120.0 /秒 | 118.0 → 120.0 /秒 |
| 受信（赤） | 59.0 → 60.0 Hz | 59.0 → 60.0 Hz |
| 送信完了 p95 | 0.10 → 0.08 ms | 0.10 → 0.08 ms |
| 生存確認の接続作り直し | 1 のまま | 1 のまま |

- スレッド・fd・接続の作り直しは増えない。送信レートと送信時間も 15 分で劣化しない。
- footprint は 15 分で約 0.7〜0.8 MB 増える。後半の傾きは小さく、1 時間続けても数 MB の見込み。リークと断定できる増え方ではないが、30 分以上の実行で傾きが 0 に近づくかは未確認。
- 「Unity 応答なし」は LAN の生存確認に応答が無い状態（P2P なしなら未確認の LAN へ送り続ける経路）で、応答ありと差が無い。
- Debug 相当（-D DEBUG）と 30 分以上の実行は未実施。実機の連続運転（STATUS のユーザー待ち 5）の代わりにはならない。
