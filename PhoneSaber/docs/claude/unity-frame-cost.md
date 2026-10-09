# Unity 本編のフレームコスト計測

対象: origin/main `35afe44`、Unity 6000.3.9f1。追加ファイルは Editor 専用の `Assets/Editor/PhoneSaberFrameCost.cs` と新規 GUID の meta、本書のみ。ゲームコード、Scene、Prefab、ProjectSettings、既存 meta、認識、UDP 仕様、F8/F9/F7 は変更しない。通常の Player ビルドには Editor script が含まれない。

## 使い方

ユーザーの Editor が開いている本 checkout では実行しない。Assets / Packages / ProjectSettings と fixture を scratchpad/buildproj2 へ同期した専用コピーを使用し、他の Unity batch process が動いていないことと UDP 5005/5006 が空いていることを確認する。ツールも起動前に両ポートを bind して確認し、占有中なら送信せずエラーにする。

```bash
/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity \
  -batchmode -projectPath <copy> \
  -executeMethod PhoneSaberFrameCost.Measure \
  -phonesaberFrameCostOutput <scratchpad/frame-cost-counters> \
  -logFile <scratchpad/frame-cost-counters.log>
```

`-phonesaberFrameCostQuality 5` を追加すると今回の Standalone 既定（Ultra）で測定する。指定しなければコピーの Editor quality を保持する。`-quit` / `-nographics` は付けない。Play Mode に入って完了時にツールが exit する。allocation の親 marker を確認する別 run は、出力先を変えて `-phonesaberFrameCostProfile` を追加する。

## 計測の範囲

- Game シーン・ElDorado normal の実譜面と音源をロードし、曲再生と両 UDP receiver の起動を待つ。3秒 warmup 後、10秒測定する。90秒で終了しなければ FAIL。計測中だけ targetFrameRate=120（既定）を指定し、batch Editor の無制限 loop で sender が飢餓になるのを避ける。`-phonesaberFrameCostFps <1〜1000>` で変更できる。production の設定やゲームコードは変更しない。
- 別 thread が2秒周期の動く棒の4座標 ASCII payload を127.0.0.1:5005/5006へ各60 Hzで送る。事前生成した bytes を再利用する。遅延時の追い付き burst は送らず、測定10秒内の送信数・Hz、warmup を含む sender 全期間の最大遅延を記録する。測定送信率が58〜62Hzを外れれば FAIL。5007 へは合成入力を送らない。
- BLE 自動起動を Resources asset のメモリ上だけで無効化し、P2P / triage / discovery の自動起動もテストと同じスイッチで無効化する。Scene や asset の保存は呼ばない。実機 IMU swing は送らないため、カット・score・hit feedback が多発する負荷とは異なる。
- ProfilerRecorder の `GC Allocated In Frame`（bytes）、`Main Thread`（ns → ms）、`PlayerLoop`（ns → ms）を取得する。測定中は managed List / string / CSV を生成せず、native recorder buffer に各フレームを記録する。停止後に集計・書き出しする。buffer wrap、marker 不在、サンプル不足、受信・端点解析失敗は FAIL。
- Main Thread は描画待ちや frame pacing を含む marker の経過時間であり、純粋な CPU 使用時間や input-to-photon latency ではない。PlayerLoop も script のみの時間ではない。Editor 自体や sender/receiver thread の割当も含まれ得る。quality / API / vSync / targetFrameRate / maxQueuedFrames を結果に残す。
- profiler 付き run は binary profile と allocation callstacks を有効にする。保存終了後に binary profile を読み戻し、GC.Alloc の metadata bytes を直近の親 marker ごとに集計し、bytes 順（同値は名前順）の上位20を返す。Profiler の保持フレーム範囲を必ず出力する。完全な全10秒の順位や C# allocation source 行の順位とは限らない。raw profile を別途 Profiler で開けば詳細を調べられる。通常計測の速度と混同しない。

出力は `summary.json`、`gc-bytes.csv`、`main-thread.csv`、`player-loop.csv`。各 CSV の sample_index はその recorder の収集順で、異なる recorder 間の index 一致を保証しない。診断 run は `allocation-profile.raw` も生成する。生成物は repo へ commit しない。

仕様: [ProfilerRecorder](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.ProfilerRecorder.html)。Unity 6000.3.9f1 同梱 XML で RawFrameDataView、Recorder options、allocation callstack API を確認した。

## 検証・測定結果

Roslyn + Unity 同梱 reference + SaberGame assembly による Editor script 単独コンパイル PASS（exit0、warningなし）。初回 uncapped run は平均送信37.85Hz（profile run14.18Hz）と60Hz条件未達のため不採用。無制限 run の recorder は11400 samples / 10秒（約1140fps）で、実運用の frame pacing を表さない。さらに binary log のみでは ProfilerDriver の frame history が空だったため、保存後の LoadProfile を追加した。これらの初回値を60Hzの性能結果としては使わない。

120fps cap / Ultra / Metal の再計測結果、EditMode、公式 verify を下に追記する。
