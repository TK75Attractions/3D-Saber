# 3D-Saber 入力システム仕様

## 目的

文化祭で、現実の2本のセーバーを振って遊ぶBeat Saber系ゲームを安定運用する。入力はCameraとIMUで役割を分け、一方の障害がもう一方の受信ループやUnityの実行を止めない。

## 役割分担

- Cameraはセーバーの位置、色、必要な場合は実際の軌跡方向を取得する。約100 msの遅延がある前提とする。
- IMUは「どちらの物理セーバーが、今振られたか」だけを低遅延で通知する。位置や最終的な振り方向は判定しない。
- Unityは現段階でCamera-only gameplayを維持する。CameraとIMUの最終hit統合はTask 1の別作業である。

## アーキテクチャ

```text
XIAO-SABER-L / XIAO-SABER-R -- BLE Notification -- Mac BLE bridge -- UDP 9002 --> Unity

iPhone Camera -- red  x1,y1,x2,y2 / UDP 5005 --+
                                                    +--> Unity InputPoint / SaberInputBridge
              -- blue x1,y1,x2,y2 / UDP 5006 --+
```

BLE bridgeはUnity内にBLEライブラリを持ち込まず、macOSでBleakを使う独立Pythonプロセスとする。Unityはlocalhost UDPだけを扱う。

## Left / Right識別

firmwareは同一sourceを左右profileでbuildし、BLE名を `XIAO-SABER-L` / `XIAO-SABER-R` とする。
Mac bridgeは2台を独立にscan/connect/reconnectし、binary Notificationを
`SWING:LEFT,...` / `SWING:RIGHT,...` へ変換する。Unityはsideを `SwingEvent.Side` に保持し、
左右別sequence trackerとイベントを使う。旧sideなしpacketはUnknownとして互換受理する。

## 通信契約

### Camera

- Red: UDP 5005
- Blue: UDP 5006
- payload: ASCII `x1,y1,x2,y2`

この仕様はTask 3 / 4で変更しない。

### IMU

firmwareのbinary BLE payloadは変更しない。bridgeはbinary Swingを物理side付き
`SWING:LEFT,...` / `SWING:RIGHT,...` としてUDP 9002へ転送する。旧文字列通知と
Virtual IMUのsideなし `SWING:` packetもUnityが互換受理する。UDP 9001はbridge
生存確認用 `PING` に使う。

## BLE bridge lifecycle

1. `Game.unity` が `UdpImuBridge` を確保し、UDP 9002を先にlistenする。
2. `Auto Start Ble Bridge` が明示的にONの場合だけ、UDP 9001へ `PING` する。
3. 既存bridgeの `STATE:BRIDGE_READY` 応答がなければ、`Tools/mac_ble_udp_bridge.py` をUnityの子プロセスとして1件だけ起動する。
4. bridgeはscan / connect / subscribeし、未発見・切断時はUnityを止めず再探索する。
5. Play終了時は、そのUnity Playが起動した子プロセスだけを終了する。手動起動bridgeは停止しない。
6. Test Runnerの一時sceneや `Game.unity` 以外では自動起動しない。

bridgeプロセスの起動とBLE Connectedは別の状態である。`Connected`はBLE connection成立後、`Swing notifications active`はNotification subscription完了後にだけ表示する。

## Camera-only fallback

XIAOがない、Bleak未導入、Bluetooth OFF、bridge切断のいずれもFatalにしない。`InputPoint` / `SaberInputBridge` / 従来の `SaberCutJudge`は5005/5006だけで従来どおり動く。IMUが無いことをCamera受信の条件にしない。

## Task 1〜4

- Task 1: Camera位置とIMU Swing時刻の最終hit統合。今回は実装しない。
- Task 2: 実写に基づくiPhone Camera認識改善。今回は実装しない。
- Task 3: 240 Hz IMU取得、低遅延SwingStart判定、左右firmware build、2台同時bridge、side付きUnity event。
- Task 4: Unity Play時のbridge自動起動、状態表示、自動再接続、二重起動防止。

## 本番時の起動フロー

初回のみFinderで `Tools/setup_ble_bridge.command` をダブルクリックし、
プロジェクト専用のBleak環境を用意する。これはシステムPythonを変更しない。

1. 必要な場合だけGamePlayManagerの `Auto Start Ble Bridge` をONにする。
2. XIAOの電源を入れる。後から入れてもよい。
3. Unityで `Assets/Scenes/Game.unity` をPlayする。
4. Consoleでbridge starting / searching / Connected / notifications activeを確認する。
5. iPhone Cameraの通常送信を開始する。
6. Cameraは常に独立動作し、IMU接続時だけIMU入力が追加される。

## 障害時

- bridgeが起動できない: ConsoleのPython / Bleakエラーを確認する。Camera-onlyでゲームは継続可能。
- XIAOが見つからない: Camera-onlyを続けながらbridgeは再探索する。
- 途中切断: bridgeがDisconnectedを通知し、再scanする。UnityとCameraは停止しない。
- Virtual IMU: `Tools/start_virtual_imu.command` で従来どおり9002へ送信できる。

## 実機確認が必要な範囲

BLE scan、macOSのBluetooth権限、Notification受信、XIAO電源OFF/ON後の再接続は自動テストで実機成功とは扱わない。
