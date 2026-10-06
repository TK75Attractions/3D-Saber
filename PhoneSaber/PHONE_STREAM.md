# iPhone側で転送量を制限する

1. iPhoneにLarix Broadcasterをインストールする。公式: https://softvelum.com/larix/ios/
2. iPhoneとMacを同じWi-Fiへ接続する。学校のWi-Fiなど端末間通信を禁止するネットワークでは利用できない。
3. 既存のカメラとBLEブリッジを終了し、Macで次を実行する。

```sh
cd "/Users/satoshi/縁日/GitHub/3D-Saber/PhoneSaber"
python3 start_phone_stream.py --imu-stick red
```

4. 表示された `rtsp://...:8554/saber-...` をLarixのConnectionsに登録する。接続先は起動ごとに変わる。映像のみ、RTSP TCPを選ぶ。
5. Capture and encoding / VideoでH.264、640x360（選べなければ640x480）、30 FPS、1000 kbps、キーフレーム間隔1秒を設定する。横向き固定、HDRはオフにする。
6. Larixで配信開始。カメラとローカルネットワークのアクセスを許可する。Macの受信接続許可も必要な場合がある。
7. Macが映像を検出すると検知が起動する。最初の2秒は背景だけを映す。Qで検知・受信・起動したIMUブリッジが終了する。

これは送信エンコーダの目標1Mbps（映像約125kB/秒）を指定する。実際の通信量には変動とプロトコルの追加量がある。従来の連係カメラの実ビットレートは不明なので削減率は未計測。
Larixの配信統計で実ビットレートを確認する。棒が圧縮で崩れる場合は1500〜2000 kbpsに上げる。1Mbpsは開始点であり、精度や遅延は実写で比較する必要がある。
受信側は再エンコードしない。ログのInputは復号された解像度、FPSは受信フレーム数で、撮影からの遅延ではない。
Larixの無料枠・時間制限・購入条件はアプリ上で確認する。購入や契約は自動で行わない。
