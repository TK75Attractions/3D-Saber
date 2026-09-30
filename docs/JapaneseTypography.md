# 日本語書体：Makinas優先・同梱Notoフォールバック

2026-09-27。ユーザーが比較画像のB案を選択。日本語を、直線と斜めの角を持つ「マキナス 4 Square」（もじワク研究）へ統一する。

- タイトル・選曲・プレイ・リザルト・判定調整：UISkinKitの共通日本語フォールバック。
- プレイとリザルトでlegacy Textを使う日本語曲名：同じ同梱OTFを明示使用。PCのインストール済み書体に依存しない。
- 英字のOxanium、Chakra Petch、難易度の7セグメントは継続。
- 日本語は使う文字だけを生成する動的アトラス。大型の静的日本語アトラスは新設しない。

## 取得とライセンス

公式配布：https://moji-waku.com/makinas/

公式使用許諾：https://moji-waku.com/mj_work_license/

ゲーム・アプリケーションの表示に使用できる。OFLなどのオープンソース書体ではなく、フォントデータの無断二次配布や改変は禁止されている。書体ファイルはGitの対象から除き、各開発環境で作者の配布元から直接取得する。`.meta`は参照を安定させるために保持する。

新しいPCでも追加操作なしで日本語を表示できる。Makinasと同じ見た目を使いたい場合だけ、リポジトリのルートから以下を実行する。

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Fonts/download-japanese-font.ps1
```

配布ZIPと取り出したOTFのSHA-256を取得スクリプトで検証する。フォントを独自に改変せず使用する。フォントをソース素材として第三者へ渡す際は公式配布元を案内する。

2026-09-30：Makinasが未取得の環境では、同梱の `Assets/Resources/Fonts/NotoSansJP-Light.otf` を自動使用する。legacy TextとTMPの両方で同じ選択を行う。OSへのフォントインストールやネット接続は不要。Makinas取得済みの環境では従来の書体を維持する。

NotoはSIL Open Font License 1.1。ライセンス全文は同じフォルダの `NotoSansJP-OFL.txt` に保持する。既存GUIDは変更しない。この必須OTFだけはGit LFSの対象から外して通常のGitで保存し、LFS未導入の新規PCでもフォント本体を受け取れるようにする（他のLFS素材の取得は従来どおり必要）。

確認：MakinasのOTFとmetaを一時的にAssets外へ退避した環境で `UISkinKitTests.ProductionJapaneseFont_WorksWithoutTheTestOverride` と関連PlayModeテストを実行する。テスト用フォント差し替えを解除し、通常の読み込み経路から日本語曲名・操作ラベルが生成できることを検証する。
