# 2台目の Mac を整える(家 Mac + 持ち運び Mac)

## 役割分担

| | 家の Mac(スマホから操作) | 持ち運び Mac(MacBook Air、学校・縁日) |
|---|---|---|
| 主な仕事 | コード修正、テスト、`verify_phone_saber.sh`、過去 session の解析 | iPhone 実機テスト、Unity、Start PhoneSaber、診断の受信 |
| iPhone との通信 | 届かない(P2P も LAN も、iPhone の近くにある Mac だけ) | ここで受ける |
| Claude Code | `claude remote-control` で常駐させ、スマホの Claude アプリから操作 | 普段どおり |

コードは GitHub の main で受け渡しします。どちらの Mac でも、**作業を始める前に `git pull`、終わったら `git push`** します。
両方で同時に同じファイルを直すとぶつかります。片方で作業しているときは、もう片方は読むだけにしてください。

## 家の Mac で1回だけやること

1. Xcode、Python 3、Git、[Homebrew](https://brew.sh) を入れる。Unity Hub と Unity 6000.3.9f1 は、Unity を開く予定があれば入れる。
2. 同じ場所に clone する(ユーザー名が違っても、`~/縁日/GitHub/` の形にしておけば手順は同じです)。

   ```bash
   mkdir -p ~/縁日/GitHub && cd ~/縁日/GitHub
   git clone https://github.com/setasato/school-festival.git
   git clone https://github.com/TK75Attractions/3D-Saber.git
   cd school-festival && ./setup_mac.command
   ```

   `setup_mac.command` は、Git に入っていないワークスペースのファイルも置きます(上書きはしません)。
   - `~/縁日/AGENTS.md`、`~/縁日/PROJECT_STRUCTURE.md`、`~/縁日/docs/ennichi-camera-system.md`
   - `~/.claude/CLAUDE.md`(「報告は必ず日本語」の指示)

   すでに違う内容のファイルがあると、`<名前>.from-repo` を横に置いて知らせるので、手で見比べてください。
3. 最後に出る `MANUAL ACTION REQUIRED` をこなす(`codex login`、Xcode の署名など)。
4. Claude Code を入れてログインし、`~/縁日/GitHub/school-festival` で次を実行する。

   ```bash
   claude remote-control --name "家Mac"
   ```

   スマホの Claude アプリの Code タブに「家Mac」が出れば完了です。家の Mac はスリープしない設定にしておきます。

## 任意:過去の診断データも家で見たいとき

診断 bundle は Git に入れていません(約 60MB、iPhone の撮影画像を含むため)。持ち運び Mac の
`~/Library/Application Support/PhoneSaber/diagnostics-inbox` をフォルダごと AirDrop などで送り、家の Mac の同じ場所に置きます。
解析ツール(session report、`phone_saber_pf22_check.py` など)はこのフォルダを読みます。

## ワークスペースのファイルを直したとき

`~/縁日/AGENTS.md` などは Git の外にあるので、直したら `school-festival/workspace/` の同名ファイルにも同じ変更を入れて push してください。
もう一台では `python3 tools/install_workspace_files.py` を実行すると、違いが `.from-repo` として出てきます。
`--check` を付けると、何も書かずに状態だけ表示します。
