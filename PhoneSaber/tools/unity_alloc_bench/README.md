# Unity PhoneSaber allocation benchmark

Git root から実行する。NuGet 外部依存なし。SDK 9 と .NET Standard 2.1 reference pack を使う。

```sh
dotnet run --project PhoneSaber/tools/unity_alloc_bench/UnityAllocBench.csproj -c Release
dotnet build PhoneSaber/tools/unity_alloc_bench/ParserCompatibility.csproj -c Release
bash PhoneSaber/tools/unity_alloc_bench/run_mono.sh
```

最後のコマンドは Unity 6000.3.9f1 同梱 Mono/Roslyn を単独起動する（Editor は起動しない）。別配置では `UNITY_MONO_ROOT` で `MonoBleedingEdge` ディレクトリを指定できる。生成物は一時ディレクトリに置いて終了時に削除する。

`LegacyInputStats.cs` は変更前（dca0c41a7c65e4e1bbf6cbf2d894805077e432c5）の統計コードを Legacy namespace に保存した比較用コピー。旧 receive parser は `PhoneSaberParserTestCorpus.LegacyParse` にそのまま保存し、production parser・EditMode 差分テストの入力集合を harness にリンクする。数値変換を別実装に置き換えていない。

受理/拒否・受理時の4座標・送信 epoch を29,575件で比較する（float/double はビット比較）。不正 UTF-8、Unicode 空白、符号、負のゼロ、丸め境界、指数、巨大数、NaN/Infinity、空要素、prefix、余分な要素/末尾も含む。統計は時計の巻き戻り・無音・送信元変更・リセット・最大サンプル数・窓の期限を含む10,000スナップショットで旧版とビット比較する。

1000回 warmup の後、10万回の `GC.GetAllocatedBytesForCurrentThread` の差を測る。構築、入力生成、表示、初回 sender-change は計測外。statistics は60Hzで毎回 Record+Readを実行する stress 計測で、hidden overlay が実際に10万回 Readするという意味ではない。ソケットは localhost の空きポートに1送信ずつ送り、送信処理を計測外にして受信＋送信元だけを計測する。ゲームの5005/5006/5007を使わない。

2026-10-09、同じMac（arm64 / Darwin 25.6.0）での結果（bytes / 100,000回）：

| 計測 | .NET 9.0.8 before → after | Unity同梱 Mono 6.13.0 before → after |
| --- | ---: | ---: |
| parser（tsあり4座標） | 41,600,000 → 0 | 42,400,000 → 0 |
| 統計 Record+Read | 484,808,784 → 0 | 512,008,784 → 0 |
| UDP受信＋送信元 | 25,600,000 → 7,200,000 | 39,200,000 → 9,600,000 |

UTF-8互換経路のparser、通常のPacket+Poll、空のFlushも両runtimeで0 bytes。稀な接続イベント・有効なログの書き出し・表示中の文字列生成は保持する。

**受信全体のゼロ割り当ては達成していない。** 指定された .NET Standard 2.1 の `Socket.ReceiveFrom(byte[], ..., ref EndPoint)` 自体に残るruntime内部の割り当ては .NET 9で72 bytes/packet、Monoで96 bytes/packet。payload配列・文字列・Split・IP文字列の毎packet生成は除去済み。初回受信/送信元IP変更時だけIP文字列を作る。これは standalone Mono harness の実測であり、Unity Editor/Player・実機のフレーム時間の測定結果ではない。上限を超えるpacket rateや時計の巻き戻りで、従来どおり無制限の1秒packet queueが初期capacityを超えた場合は拡張が起こり得る（統計の受理やカウントを変える上限は追加しない）。

標準互換buildは新parser・endpoint・統計・イベントコードの C# 9 / .NET Standard 2.1 API互換だけを検証する。InputPoint と overlay のUnity API依存部分、EditMode/PlayModeは別途オペレーターの検証対象。
