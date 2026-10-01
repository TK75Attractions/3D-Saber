# PhoneSaber / 縁日プロジェクト — Claude Code 運用ルール

このセッションはプロジェクトのメイン開発者 兼 管理者です。
ユーザーの許可を待たずに、調査 → 実装 → 検証 → commit → push まで自律的に進めてください。
ユーザーに頼むのは、物理的にユーザーしかできないことだけです(§6)。

- repo: /Users/satoshi/縁日/GitHub/school-festival
- Unity: /Users/satoshi/縁日/GitHub/3D-Saber
- 公式検証: tools/verify_phone_saber.sh
- 進捗ログ: docs/claude/STATUS.md(なければ作る。§7)
- 一般的な構成・コーディング規約は AGENTS.md を参照。

## 1. 絶対に守ること(自律でも例外なし)
- branch は main 一本。通常の commit / push のみ。
- git push --force、git reset --hard、git clean は禁止。
- 自分が作っていない未commit変更を消したり、上書きしたりしない。
- 同じ working tree を、ほかの AI セッションと同時に編集しない。ほかの Claude は read-only 分析専用。
- repair gate / safety gate を、テストを通す目的で緩めない。
- formal lossless corpus(40件)を削らない、弱めない。期待値も書き換えない。
- production recognition の threshold を、証拠なしに変更しない。
- Unity 側の原因調査はしない。不安定は iPhone の判定時点で確認済み。
- 32KB preflight 上限は維持する。

## 2. 自律で進めてよい範囲
許可不要(検証 PASS なら commit / push まで進める):
- diagnostics / Debug Recording / tools / tests / docs の改善
- 新しい実機 session の read-only 分析と、CASE A/B/C の判定(§5)
- 既存 session の再解析
- バグ修正のうち、production の recognition 結果を変えないもの

条件付きで自律可(§4 の gate を満たした場合のみ):
- production の candidate selection 変更(修正 A)
- production の endpoint/body/tail 変更(修正 B)

commit 前に毎回必須:
1. tools/verify_phone_saber.sh で全項目 PASS(XCTest、Detection、formal lossless 40/40、Python、iOS Release)
2. git diff --check が PASS
3. 自己レビュー。production recognition のコードパス(ranking / eligibility / scoring / PCA・fallback / UDP)に、意図しない差分がないことを明示的に確認する。
4. commit message に、目的・影響範囲・検証結果を書く。

Simulator の起動エラーは環境要因のことが多いので、リトライしてよい。同じテストが2回連続で失敗したら、本物の失敗として扱う。

## 3. 現状(2026-10-02 時点)
主問題: iPhone 側の recognition 結果そのものが不安定(ぐわんぐわん)。

主仮説(部分支持): candidate selection の瞬間的なすり替え。
既存 session の横断分析の結果:
- candidateSwitch:7件
- 100px以上のジャンプ:9件
- そのうち endpointPathChanged=false:6/9件

代表例:
- phonesaber_20261002_005850_489 frame 2537:color-close → color-sparse-raw、約422px のジャンプ、次 frame で元の領域へ戻る
- phonesaber_20261002_013205_087 frame 255:core-halo → color-sparse-raw、約430px のジャンプ

candidate selection では説明できない例もある:
- 例B frame 660:eligible が1件だけなのに約437px のジャンプ
- 例B frame 665:raw PCA の tail が伸びる(raw span 46→218px、robust 46→52px)

未commitの diagnostics 改善(working tree にある):
- bridge dropout 検出(前後3 frame を1 evidence とし、original と annotated を別保存)
- Active Color 選択(RED / BLUE / BOTH)
- context の compact 化
- 全 eligible candidate の geometry 記録(eligibleRank、truncation の明示、件数照合)
- matchToPreviousWinner(centroid 距離 / IoU / 面積比 / span 比 / 向きの差)
- [AUTO_REPAIR][CANDIDATE_AUDIT] の CASE ヒント(bCause を4分類。gate には使わない)
- tracking 窓(不安定がある event では最低5 frame を確保。bridge と共存する場合は8 frame)
- toggle 成分を tracking ranking から除外
- retained BGRA の上限を 256MiB に変更(Debug Recording 時のみ)
- gate:bridge は1 event として数える。annotated は証拠から除外する
検証:XCTest 149/149、formal 40/40、Python 182/182、Release PASS。

## 4. production 修正の gate
修正 A(candidate temporal consistency)を実装してよい条件:
新しい実機 capture で、CASE A を別々の swing で2 event 以上確認できたとき。
CASE A の条件:
- original PNG で本物の saber が見える
- その saber に対応する candidate が、event frame でも eligible として残っている
- 遠方の別 candidate が、僅差で一瞬 winner になっている

設計方針:
- 常に「現 frame の eligible candidate」から選ぶ。過去の endpoint を出力し続けない。
- candidate の identity に index を使わない。geometry で対応を取る。
- 現 frame の最高 score を R とする。前回の physical candidate に対応する current eligible を M とする。
- R と M が僅差で、かつ M への対応が強いときだけ M を選ぶ。
- 選好には期限を付ける。override しても期限は延長しない。
- M が消失した、または ineligible になったら即座に解除する。eligible が唯一ならそれを受理する。
- margin<5 / 150px / 15frame のような固定値を、根拠なく production threshold にしない。threshold は実 capture の分布から決め、その根拠を commit message に書く。
- 実装候補の場所:BGRADetection.swift の winner selection 付近と、FrameProcessor.swift の色別 temporal state。

実装後は必ず次を行う:
- formal 40/40
- regression
- CASE A を記録した session での before/after 比較
- 「要実機再試験」として、§6 でユーザーに実機確認を依頼する

修正 B(raw PCA tail):
- 修正 A とは別 commit にし、A の後に進める。
- 条件:raw/robust の乖離 AND body が単独で棒形状を説明する AND tail の支持が弱い AND 分離 LED ではない AND body PCA が妥当。
- 長い saber や分離 LED を短縮しない regression テストを必須とする。

やらないこと:
- 証拠なしの production 修正
- A と B の同時実装
- gate の緩和
- video clip 方式
- Unity 側の調査

## 5. 新しい実機 session が届いたら
1. bridge / tracking event が意図どおり選ばれているかを確認する(tracking 窓の枚数、bridge_priority)。
2. original PNG で、本物の saber の位置を確認する。annotated は ground truth ではない。
3. event frame の全 eligible candidate と winner を照合する(eligibleRank、matchToPreviousWinner、score 差)。
4. 分類する:
   - CASE A:正しい candidate は eligible のまま残っていて、遠方の candidate が僅差で winner になる → 修正 A の根拠
   - CASE B:正しい candidate が生成されていない、または ineligible → generation / eligibility を調査(unknownTruncated の場合は断定しない)
   - CASE C:winner は正しい領域だが、PCA / endpoint が壊れる → 修正 B の領域
5. 判定と根拠(session 名、frame、数値)を STATUS.md に追記する。
6. CASE B が多い場合、次の diagnostics として「candidate 生成前に落ちた component(bbox、area、落ちた理由)」の記録を追加する。

## 6. ユーザーに頼むこと(これ以外では止まらない)
- Start PhoneSaber の起動
- iPhone 実機での Debug Recording(どんな動きを撮ってほしいか、具体的に伝える)
- production 修正後の実機再試験
- 目標そのものを変える判断

頼むときは、STATUS.md の先頭の「ユーザー待ち」欄に書いたうえで、チャットにも短く伝える。
待っている間も、ほかに進められる作業(tests、tools、既存 session の再解析)を続ける。

## 7. STATUS.md の書き方
先頭に、次の3つを常に最新の状態で置く:
- ユーザー待ち(なければ「なし」)
- 現在の仮説と確度
- 次にやること
その下に、作業ログを新しい順に書く(日付、内容、commit hash、検証結果)。古いログは、ときどき要約にまとめる。

## 8. 最初のタスク(この順で)
1. git status / git diff --stat で、未commit変更(15 変更 + 新規2ファイル)が §3 と一致するかを確認する。
2. production recognition のコードパスに差分がないことを、diff を見て確認する。
3. 既存 session で回帰を確認する。phonesaber_20261002_005850_489 frame 2537 と phonesaber_20261002_013205_087 frame 255 が、新しい選択ロジックでも tracking event として選ばれ、窓に含まれるかを確認する。含まれなければ修正する。
4. 全検証 PASS を確認し、commit → push する。
5. Debug Recording 時に os_proc_available_memory() の最小値をログに出す(未実装の場合のみ)。256MiB の実機メモリ余裕を確認するため。
6. STATUS.md を作り、ユーザーに実機 Debug Recording を1回依頼する。依頼内容:「棒を大きく速く振る swing を数回、画面端への出入りも含める」
