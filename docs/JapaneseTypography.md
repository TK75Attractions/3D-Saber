# 日本語書体：同梱のマキナス 4 Square

2026-09-27。ユーザーが比較画像のB案を選択。日本語を、直線と斜めの角を持つ「マキナス 4 Square」（もじワク研究）へ統一する。

- タイトル・選曲・プレイ・リザルト・判定調整：UISkinKitの共通日本語フォールバック。
- プレイとリザルトでlegacy Textを使う日本語曲名：同じ同梱OTFを明示使用。PCのインストール済み書体に依存しない。
- 英字のOxanium、Chakra Petch、難易度の7セグメントは継続。
- 日本語は使う文字だけを生成する動的アトラス。大型の静的日本語アトラスは新設しない。

## 取得とライセンス

公式配布：https://moji-waku.com/makinas/

公式使用許諾：https://moji-waku.com/mj_work_license/

ゲーム・アプリケーションの表示への利用が認められている書体。OFLなどのオープンソース書体ではなく、作者独自の使用許諾が適用される。公式条件には無断の二次配布・転売、フォントデータの改変などの禁止事項もある。本プロジェクトのコードのライセンスがフォントへ適用されるわけではない。ソースリポジトリへの同梱という形態の扱いまでは公式ページで明示されていないため、この説明は作者による個別の再配布許可を示すものではない。

2026-09-30：プロジェクト所有者の指定により、改変していないMakinas本体をゲーム素材としてリポジトリに含める。OTFと既存の`.meta`を一緒に取得でき、フォント本体もGit LFSを使わず通常のGitに保存する。新しいPCでも追加操作なしで同じMakinasを使用する。作者・出典は同じフォルダの `Makinas-NOTICE.txt` に記載する。破損・欠損時に公式配布元から復元する場合だけ、以下を実行する。

```powershell
powershell -ExecutionPolicy Bypass -File Tools/Fonts/download-japanese-font.ps1
```

配布ZIPと取り出したOTFのSHA-256を取得スクリプトで検証する。フォントを独自に改変せず使用する。フォントをソース素材として第三者へ渡す際は公式配布元を案内する。

通常は同梱Makinasを使用する。Makinasが欠損した場合の予備として `Assets/Resources/Fonts/NotoSansJP-Light.otf` を保持する。legacy TextとTMPの両方で同じ選択を行い、OSにインストール済みの書体には依存しない。

NotoはSIL Open Font License 1.1。ライセンス全文は同じフォルダの `NotoSansJP-OFL.txt` に保持する。既存GUIDは変更しない。MakinasとNotoのOTFはGit LFSの対象から外して通常のGitで保存し、LFS未導入の新規PCでも日本語フォント本体を受け取れるようにする（他のLFS素材の取得は従来どおり必要）。

確認：MakinasのOTFとmetaを一時的にAssets外へ退避した環境で `UISkinKitTests.ProductionJapaneseFont_WorksWithoutTheTestOverride` と関連PlayModeテストを実行する。テスト用フォント差し替えを解除し、通常の読み込み経路から日本語曲名・操作ラベルが生成できることを検証する。
