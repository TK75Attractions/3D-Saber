# スマホ1台の追跡実験

## 連係カメラの低遅延取得（追加の実験経路）

Larixを使わず、AVFoundationを直接利用する`native`経路を追加しました。
従来のOpenCV経路も残しています。実機での遅延短縮が未検証のため、既定値はまだOpenCVです。
Mac取得後の縮小だけではなく、カメラの`activeFormat`とフレーム周期を明示的に設定します。
ただし、連係カメラ内部の無線ビットレートは公開されていないため、通信量が減ったとは断定できません。

```bash
cd "/Users/satoshi/縁日/GitHub/school-festival"
python3 smartphone_camera.py --capture-backend native --width 640 --height 480 --fps 30 --preview-only
```

初回だけ付属のObjective-CアダプターをMac標準の開発ツールで自動ビルドします。Swiftや追加Pythonパッケージは不要です。macOS 13以上が必要です。
カメラ権限を求められたら許可してください。拒否される場合は「システム設定 → プライバシーとセキュリティ → カメラ」で起動元アプリの許可を確認します。権限設定をプログラムでリセットすることはありません。

- `native`ではiPhoneの連係カメラだけを自動選択し、Mac内蔵カメラへ勝手に切り替えません。
- `--camera 0`はOpenCV専用の番号です。nativeで個別指定する場合は`--device-id`を使います。
- 最新の1枚だけを保持し、処理が遅れた分の古いフレームは読み飛ばします。動画再圧縮や別プロセスへの映像転送はありません。
- 指定FPSが未対応の場合は、別FPSに置き換えずエラーにします。
- `active_format`は開始時のカメラ形式、画面の`Input`は実際に届いた画像サイズです。両方を確認してください。
- 10秒間新しい映像が届かなければ、古い映像で追跡を続けずエラー終了します。
- 640×480と1280×720では縦横比・画角が変わる場合があります。カメラを固定し、ゲーム画面との座標対応と赤青の検知を確認してください。

本番の軽量`camera.py`でも、既定のOpenCV取得を維持したまま、環境変数で既存native取得を明示選択できます。nativeの実機動作・遅延短縮は未測定です。

```bash
CAMERA_CAPTURE_BACKEND=native python3 camera.py
```

`CAMERA_NATIVE_DEVICE_ID`、`CAMERA_NATIVE_WIDTH`、`CAMERA_NATIVE_HEIGHT`、`CAMERA_NATIVE_FPS`、`CAMERA_NATIVE_FORMAT_INDEX`で既存CLI相当のnative設定を指定できます。新しいフレームが`CAMERA_FRAME_TIMEOUT`秒（既定10秒）届かない場合は、古いフレームを処理せず終了します。nativeの送信座標は`active_format`の実解像度を基準にし、既存のOpenCV時と同じUDP形式を使います。

対応形式の一覧:

```bash
python3 smartphone_camera.py --list-cameras
```

2026-09-06の実機一覧では、iPhoneに640×480 / 1280×720などの30・60FPS形式があることを確認しました。
ただし、このアプリからのカメラ許可はまだ未決定（authorization_status=0）で、実際のFPS・遅延改善は未検証です。拒否確定とは区別しています。

## 遅延を比較する

`camera_benchmark.py`は、Macに表示した変化するマーカーをiPhoneで読み戻し、表示要求からPythonに画像が届くまでを測ります。棒にマーカーを貼る必要はありません。カメラ画像は保存せず、数値だけを保存します。

1. 他のカメラ使用アプリ・追跡プログラムを止める。
2. iPhoneを固定し、Mac画面に出る大きな白黒マーカーへ向ける。マーカーの白い余白まで画面内に入れる。
3. 次のコマンドを実行する。OpenCV、native 640×480・30FPS、同60FPS、native 1280×720・30FPSを順番に比較する。

```bash
python3 camera_benchmark.py --optical --camera 0 --output latency-run1.json
```

`--camera 0`はこれまでiPhoneが開いていた番号を指定します。native側は連係カメラを自動選択します。複数のiPhoneがある場合は`--device-id`で一覧のIDを指定してください。
各設定12秒、Qで中断。途中までの結果も保存します。既存の結果ファイルは上書きしません。
同条件で出力名を`latency-run2.json`、`latency-run3.json`に変えて繰り返してください。
単独比較には`--profile native30`、`native60`、`native720`、`opencv`を指定できます。

評価する値:

- `optical.median_ms`：通常時の遅れ。
- `optical.p90_ms`：遅い側の遅れ。平均だけ良くても、この値が大きい設定は避ける。
- `input_fps`・`frame_interval_p90_ms`：届く映像の頻度と途切れ。
- `valid`：実フレームshapeが要求width/heightと一致し、実測FPSが要求値と一致した場合のみtrue。光学モードではこれに加えて60個以上の異なるマーカーが必要で、これだけで検知精度合格を意味しない。
- `actual_sizes`：callbackに届いた画像の実サイズ。要求値やwireless転送元の解像度ではない。

画面の更新待ち・撮影位相が含まれるため、無線だけの遅延や厳密な撮影時刻ではありません。
比較の終点は両方式とも「PythonへBGR画像が返った時刻」に揃えています。
表示更新の粒度（通常十数ms以上）があるため、数msの差で優劣を断定せず、複数回の中央値とp90を比較します。
`--optical`なしの測定はFPSとMac取得後の時間だけで、無線遅延の改善判定には使えません。
検知画面への再表示時間・検知処理・Unity側はこの測定に含みません。

速かった設定を選んだ後、プレビュー専用フラグを外して棒の検知を確認します。30FPSの例:

```bash
python3 smartphone_camera.py --capture-backend native --width 640 --height 480 --fps 30 --imu-stick red --imu-axis z --show
```

IMUブリッジの起動は従来どおり別途必要です。60FPSは対応していても通信・処理負荷が増えて悪化する可能性があるため、計測なしでは既定値にしていません。
赤・青それぞれで遠距離、速い振り、交差、赤い服の背景を試し、精度が落ちる場合は1280×720形式も比較してください。

根拠: [OpenCV 4.13.0のMac取得実装](https://github.com/opencv/opencv/blob/4.13.0/modules/videoio/src/cap_avfoundation_mac.mm)、[AppleのactiveFormat](https://developer.apple.com/documentation/avfoundation/avcapturedevice/activeformat)、[遅れたフレームの破棄](https://developer.apple.com/documentation/avfoundation/avcapturevideodataoutput/alwaysdiscardslatevideoframes)。OpenCVも遅れたフレームを破棄します。nativeの主な追加点は撮影形式の明示選択とコピー経路の削減で、破棄機能自体が新しく無線遅延を消すわけではありません。

自動テスト:

```bash
python3 -B -m unittest discover -v
```

ネイティブ部分の行間パディング、最新フレーム保持、同時読み書き、画像サイズ変更、Python側の所有権、光学測定の符号読み取り、赤青の検知を検証します。実際の無線遅延と本番環境の検知精度は実機試験が必要です。

遅延切り分けは `python3 smartphone_camera.py --camera 0 --fps 30 --preview-only` で検知・UDPなしの表示と比較できます。時計とプレビューを並べ、同じ条件で複数回差を測ります。再帰表示になると検知モードは赤青の描画文字も候補にするので、棒の検知試験とは分けてください。
検知中は候補の範囲内だけで発光軸を解析し、背景差分もOpenCVで計算します。これらはMac側の軽量化で、無線自体の遅延削減を保証するものではありません。

Macカメラとステレオ校正を使わず、スマホ映像だけで赤・青の棒を追跡します。

カメラには640x360・30 FPSを要求します。実際の取得解像度とカメラの報告FPSを起動時に表示します。連係カメラが要求を無視する場合や内部で別解像度を転送する場合もあるため、無線通信量の削減は保証できません。
`--show`は追跡画面1枚だけを表示します。診断用マスク2枚も必要な場合だけ`--show-masks`を追加してください。
2秒ごとの`Input`は取得FPS、`processed`は検知FPS、`work`は表示を含む処理時間です。`since-available`はMacで取得可能になった後の経過時間で、無線を含む撮影からの遅延ではありません。nativeは撮影APIのコールバック時刻、OpenCVはread完了時刻が起点なので、この値だけで両方式の総遅延を比較しないでください。

## 実行

```bash
cd "/Users/satoshi/縁日/GitHub/school-festival"
python3 smartphone_camera.py --camera 0 --show
```

Mac内蔵カメラが開く場合だけ`--camera 1`や`--camera 2`へ変更します。起動時に表示される`Camera opened: index=...`で確認できます。

起動直後の2秒は背景学習です。この間だけ赤・青の棒と人を画面外に出し、カメラを動かさないでください。`Background ready`と表示されたら棒を入れます。

## 動作

- 背景との差分がある物体だけを色検知へ渡し、固定された背景の色を除外
- 新しい棒は10フレーム以上かつ400 ms以上、形と位置がつながった場合に追跡開始
- 初回候補スコアが18未満の弱い色ノイズは採用しない
- 追跡中も、明るい有彩色画素が領域の15%以上あり、棒の長さの55%以上に分布することを要求する。暗い腕などの細長い領域を除外するが、暗く写る本物の棒を見失う可能性はある。
- 追跡開始後はスマホの各フレームを即座にUDP送信
- 位置が突然飛んだ候補は無視
- 120 ms以内の欠落は、直前の移動速度と回転速度から予測
- 350 ms見失うと追跡を解除し、再び初回確認
- 赤を5005、青を5006へ従来形式で送信

黄色い線は予測中です。画面左上の`jumps`が増える場合は、誤検知候補または本物の動きが飛び判定で除外されています。

## IMUを使う場合

既存BLEブリッジはUnity用UDP 9002に加えて、Python用UDP 9003へ同じIMUデータを複製できます。

```bash
cd "/Users/satoshi/縁日/GitHub/3D-Saber"
python3 Tools/mac_ble_udp_bridge.py
```

IMUを赤い棒へ付けた例:

```bash
cd "/Users/satoshi/縁日/GitHub/school-festival"
python3 smartphone_camera.py --camera 0 --imu-stick red --imu-axis z --show
```

IMUの軸向きにより予測が逆回転する場合は`--imu-sign -1`を追加します。`z`軸で合わない場合は`--imu-axis x`または`--imu-axis y`を試します。IMUは欠落中の角度だけに使用し、加速度から位置を積分しないため、大きなドリフトは発生しません。

## 最初に試す項目

1. IMUなしで通常速度、速い振り、棒同士の交差を各10回試す。
2. 赤い服を画面へ入れ、棒を一度画面外へ出して再取得を確認する。
3. 黄色い予測線が120 ms以内で本物の動きにつながるか確認する。
4. 本物の高速移動で`jumps`が増えすぎる場合は`--jump-base-px 70`へ上げる。
5. 誤検知へ飛ぶ場合は`--jump-base-px 30`へ下げる。

背景学習を無効にして以前と比較する場合は`--background-seconds 0`を付けます。暗くて本物の棒を初回採用できない場合だけ`--acquire-min-score 14`へ下げます。
