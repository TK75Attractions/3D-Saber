# unity-gc — Unity PhoneSaber受信のGC削減

2026-10-09、branch `opt/unity-gc`。指定のInputPoint/PhoneSaberコード・新規Editorテストと.meta・Assets外のharnessだけを変更。ユーザー指定に従いcommitのみを予定し、pushなし。

受信バッファ、ASCII/UTF-8文字バッファ、送信元endpoint/IP、統計作業領域を再利用する。float/doubleは旧版と同じInvariantCulture/NumberStyles.Floatのspan版TryParseで変換。受信直後のmonotonic/壁時計、UDP形式・5005/5006/5007、認識・座標演算の順序・診断の窓/閾値/文言は保持。非表示overlayは既存のRefresh前returnを維持し、通常のevent polling/空Flushの割り当てゼロを確認した。接続遷移や表示中の診断文字列は保持。

同一Mac arm64 / Darwin 25.6.0、1000回warmup後の100,000回（bytes）：

| 項目 | .NET 9.0.8 before → after | Unity6000.3.9f1同梱Mono6.13 before → after |
| --- | ---: | ---: |
| parser | 41,600,000 → 0 | 42,400,000 → 0 |
| 統計Record+Read | 484,808,784 → 0 | 512,008,784 → 0 |
| UDP受信＋送信元 | 25,600,000 → 7,200,000 | 39,200,000 → 9,600,000 |

UTF-8 parserと通常Packet+Poll+空Flushも両runtimeで0 bytes。**Socket.ReceiveFrom内部は.NET9で72、Monoで96 bytes/packet残るため、受信全体のゼロ割り当ては未達。** 送信元変更・queue capacity超過・実ログ出力は割り当てを伴い得る。Editor/Playerや実機でのframe-time改善は未測定。

検証：
- .NET/Mono harness：旧parser対29,575件の受理・座標/epochビット一致、旧統計対10,000スナップショット＋2048件上限、一括sender snapshot/port/IP/8-byte native endpoint互換はPASS。
- C#9 / .NET Standard2.1互換build：PASS、警告0。Mono harnessもUnity同梱Roslynでcompile/runした。
- 新規EditMode：parser差分・通常処理の割り当て・非表示overlay Update・endpoint再利用のテストを追加。Unity依存部分のcompile、EditMode/PlayModeはオペレーター側で未実行（本タスクはEditor起動なし。公式scriptも既存Editorのproject所有を検出してNOT_RUN）。
- `verify_phone_saber.sh`：Detection PASS、formal lossless 40/40 PASS、diff check PASS。総合はFAIL。XCTestはCoreSimulatorServiceへの接続拒否でNOT_RUN。Pythonは384件中failure1/error1/skip1（P2P compiler子process検出がsandboxのpgrep制限で不能、tracking E2Eの容量APIがavailable=0を返してinsufficientDiskSpace）。iOS ReleaseはSwift macro plugin serverのsandbox起動失敗でmalformed response。gateやfixture・期待値を変更せず、これらをPASS扱いしていない。
- 自己レビュー：iPhone認識/ranking/scoring/eligibility、game/chart、scene/prefab、既存.metaは変更なし。Unity midpoint/endpoint計算の浮動小数点演算は変更なし。

再現コマンド・計測定義は `PhoneSaber/tools/unity_alloc_bench/README.md`。公式検証ログは `/private/tmp/phonesaber-unity-gc-verify/20261009-010223/`（生成物はcommitしない）。

## commitの制約

実装・検証後の `git add` が `/Users/satoshi/縁日/GitHub/3D-Saber/.git/worktrees/3D-Saber-unity-gc/index.lock: Operation not permitted` で拒否された。main .gitをcwdにして明示的なgit-dir/work-treeを指定しても同じ結果。書き込み可能rootの指定にかかわらず、実際のsandboxがworktree indexを拒否するためcommit未実施。Git管理ファイルは変更していない。承認ポリシーneverのため権限昇格は実行できない。

予定commit messageは `/private/tmp/phonesaber-unity-gc-commit-message.txt` に用意した。変更ファイルは本タスクのscopeのみ。

## レビュー後の変更（Claude, 2026-10-09）

独自 `EndPoint`（`PhoneSaberReceiveEndpoint`）は Mono の SocketAddress サイズ差（macOS で 8）に依存しており、Windows Player での挙動を保証できないため採用しなかった。受信は標準 `IPEndPoint` + `Socket.ReceiveFrom`（再利用 buffer）とし、送信元 IP 文字列は IP が変わったときだけ作る。Unity Mono 計測で UDP 受信＋送信元は 39,200,000 → 27,200,000 bytes / 100k packets（parser・統計は 0 のまま）。
