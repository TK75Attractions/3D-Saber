# Virtual Swing Input (No XIAO)

## Quick start

1. Unity で `Assets/Scenes/Game.unity` を開いて Play
2. Finder で `Tools/start_virtual_imu.command` をダブルクリック
3. Terminal で `L` / `R` / `U` / `D` を押す（Enter 不要）
   `T` で0/10/25/50/100 msの人工遅延を順に切り替えられる
4. Unity Console の `[Swing]` ログを確認

```text
Virtual IMU ready
[L] Left  [R] Right  [U] Up  [D] Down
[T] Cycle delay 0/10/25/50/100 ms  [Q] Quit
```

Virtual IMU と実 BLE bridge はどちらも UDP 9002 の同じ形式を使うため、
切替時に Unity コードやシーン設定を変更する必要はない。

```text
SWING:<sequence>,<left|right|up|down>,<strength>,<xiao_timestamp_us>[,<sender_monotonic_ns>]
```

5番目は Virtual IMU が localhost UDP 遅延を測るためだけに付ける任意field。
実 BLE bridge の4 field packetもそのまま受理される。`xiao_timestamp_us` は
Unity時計と直接比較しない。

## Latency simulation

通常起動はキー入力まで何も送らない。自動送信は明示したときだけ有効になる。

```bash
cd Tools
python3 virtual_imu.py --auto --interval 0.5
python3 virtual_imu.py --delay-ms 10
python3 virtual_imu.py --delay-ms 25 --jitter-ms 5
python3 virtual_imu.py --delay-ms 50 --jitter-ms 5 --drop-rate 0.05
python3 virtual_imu.py --delay-ms 100
```

- `--delay-ms`: triggerからUDP sendまでの固定人工遅延
- `--jitter-ms`: 固定遅延へ加える一様分布の±jitter
- `--drop-rate`: 0.0〜1.0。drop時もsequenceを進め、欠番処理を再現
- `--auto`: left/rightを交互に送信
- `--interval`: auto送信間隔（秒）

Unity Console は各eventについて、受信sequence/direction/strength、Mac monotonic
receive time、localhost UDP latency、UDP receive→main-thread callback latencyを表示する。
localhost値は実BLE遅延ではない。main-thread callbackは`Update()` pollingではなく
Unity `SynchronizationContext` へ直接Postするが、Unity APIを安全に触るため次の
PlayerLoopまで最大1 frame待つ可能性がある。従って目安は60 fpsで最大約16.7 ms、
30 fpsで最大約33.3 msであり、実値はConsoleで比較する。

headless PlayMode回帰テストの3試行では localhost UDP `1.780〜2.070 ms`、
receive→main-thread callback `2.487〜3.438 ms`だった。これはEditor batchmodeの値であり、
30/60 fpsの画面描画中の保証値ではない。本番シーンでConsole値を再比較する。

## Event handling

`UdpImuBridge.OnSwingReceived` がmain threadで発火する。受信スレッド側で先に
parse、重複・out-of-order除外を行う。sequence欠番は待たない。main threadへ届く
まで既定150 msを超えたeventはstaleとして捨てる。この値はInspectorの
`Stale Event Seconds`で変更できる。

`Swing8DirectionLogger` はraw IMUから再判定せず、このeventを既存の
`CutDirection` hintへ変換するだけである。Camera座標の5005/5006経路とHit確定は
今回結合していない。

## Tests

Python Virtual IMU:

```bash
python3 -B -m unittest Tools.tests.test_virtual_imu -v
```

Unity Test RunnerではEditorの`SwingEventStreamTests`とPlayModeの
`UdpSwingEventPlayTests`を実行する。後者の`[SwingTest]`ログがlocalhost UDPと
main-thread handoffの実測値を表示する。

---

## Legacy raw IMU mock

XIAO が手元にない間、Unity 側の受信・パース・Haptic送信経路を検証するためのモックです。

## 1. Unity 側

1. シーン内に空の GameObject を作成
2. `UdpImuBridge` をアタッチ
3. Play を押す

スクリプト: `Assets/Scripts/Managers/Inputsystem/Input/UdpImuBridge.cs`

- 受信ポート: `9002` (IMUデータ)
- 送信ポート: `9001` (Hapticコマンド)
- `H` キーで `Haptic.Vibrate(0.15f)` を試せます

## 2. Python モック起動

```bash
cd Tools
python3 mock_imu_bridge.py
```

オプション例:

```bash
python3 mock_imu_bridge.py --host 127.0.0.1 --command-port 9001 --data-port 9002 --hz 50
```

## 3. データ形式

Unityへ送るデータ:

- `IMU:ax,ay,az,gx,gy,gz`
- `STATE:CONNECTED`
- `STATE:DISCONNECTED`

Unityから受けるコマンド:

- `H:1` -> 振動ON
- `H:0` -> 振動OFF

## 4. 実機に切り替える時

- `UdpImuBridge` の送受信仕様はそのまま使えます
- モックの代わりに BLE ブリッジを同じメッセージ仕様で差し替えるだけです

## 5. BLE 実橋 (macOS / Windows, Bleak)

### 5-1. 依存パッケージ

```bash
cd Tools
python3 -m pip install -r requirements_ble_bridge.txt
```

### 5-2. 起動

```bash
cd Tools
python3 mac_ble_udp_bridge.py
```

macOSとWindowsのどちらでも同じコマンドで起動できます。WindowsではPowerShellで
`python` または `py` を使用してください。

```powershell
cd Tools
py mac_ble_udp_bridge.py
```

必要ならデバイス名を明示:

```bash
python3 mac_ble_udp_bridge.py --device-name XIAO-LSM6DSV16X
```

### 5-3. 固定アドレスで接続したい場合

```bash
python3 mac_ble_udp_bridge.py --device-address <address-or-uuid>
```

### 5-4. 役割

- Unity -> UDP(9001) -> Bleak -> ESP32 RX UUID へ `1` / `0` 送信
- ESP32 TX UUID notify -> Bleak -> UDP(9002) へ転送

`mock_imu_bridge.py` の50 Hz raw `IMU:` streamは旧位置fallback確認専用であり、
Swing入力の主経路ではない。Swing統合テストには`virtual_imu.py`を使用する。
