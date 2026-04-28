# Mock IMU Bridge (No XIAO)

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

## 5. Mac BLE 実橋 (Bleak)

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
- ESP32 TX UUID notify -> Bleak -> UDP(9002) -> Unity へ `IMU:...` 転送
