# school-festival
rosin

## Camera tracking

- Basic red/blue tracking: `camera.py`
- Smartphone-only tracking experiment: `smartphone_camera.py`
- Marker-assisted tracking without stereo calibration: `aruco_camera.py`
- Calibrated 3D stereo tracking: `stereo_camera.py`

See `SMARTPHONE_CAMERA.md` for the current single-camera experiment.

## Phone Saber latency test

Run `run_debug.command`, then use **Latency Test** in the browser dashboard.
Point the iPhone camera at the whole test display and start normal sending. The
test waits for two packets from the currently displayed side before each A/B
switch, measures the following matching UDP arrival using the Mac monotonic
clock, and saves completed/partial results in `latency_results/` as CSV.
Timeouts are recorded as failures and excluded from the summary statistics.

Normal operation is **Enter Test View** (or **Prepare Test**) → fullscreen
**Ready** → **Start Test** inside Test View. Point the iPhone at the display
before starting. Controls hide, then a 500 ms settling interval precedes trials.
Completion returns to the dashboard; Escape or the small Stop button cancels.
The target is 3% of the viewport width with a narrow bright core and no glow.
Network packet and
coordinate details, legacy display controls, and the result-folder operation
are under the collapsed Details sections.

The value begins when the browser has drawn a switch and the local dashboard
receives its acknowledgement. It therefore measures the normal camera,
recognition, fresh-only UDP, and Mac receive path without iPhone/Mac clock
synchronization; display refresh/compositor timing remains an external
measurement uncertainty.

## PhoneSaber verification

Run the complete automated verification from the `school-festival` repository:

```bash
./tools/verify_phone_saber.sh
```

The command runs the full iOS XCTest suite (including `DetectionCoreTests`)
serially on one Simulator, with parallel test execution disabled and the worker
count capped at one. It also runs the dedicated static BGRA Detection tests,
the lossless fixture regression, PhoneSaber Tools unittests, an iOS Release
build, a read-only Unity Editor capability check, and `git diff --check` for
`school-festival`. Set `PHONESABER_VERIFY_UNITY_RUN=1` only when you intend to
run Unity EditMode, PlayMode, and compile checks in the separate `3D-Saber`
project. Every command's stdout and stderr and Xcode result bundles are saved under
`.verify-logs/phone-saber/<run timestamp>/`.
The iOS logs also include the xcresult summary and
`ios-xctest-classification.stdout.log`, which separates assertion failures,
worker kills, other execution failures, and passing runs.

By default the Unity project is expected at the sibling path `../3D-Saber`.
Set `UNITY_PROJECT_PATH` if it is elsewhere. Set `UNITY_EDITOR` to select a
Unity Editor executable, or `PHONESABER_IOS_SIMULATOR_ID` to choose an installed
iPhone Simulator explicitly. If the Unity Editor already has the target
project open and prevents batch-mode execution, Unity stages report `BLOCKED`;
close that Editor and rerun. The command exits `0` when all stages pass, `1` on
a failure, and `2` when a stage is blocked.

## 新しいMacへの移行

1. `school-festival` と `3D-Saber` を同じフォルダへ clone します。
2. `school-festival/setup_mac.command` を実行します（Finderからダブルクリックも可能）。
3. 最後に表示される `MANUAL ACTION REQUIRED` を実施します。
4. Desktop の `Start PhoneSaber.command` を起動します。

```bash
git clone https://github.com/setasato/school-festival.git
git clone https://github.com/TK75Attractions/3D-Saber.git
cd school-festival
./setup_mac.command
```

setupは、Gitに入っていないワークスペースのファイル（`../../AGENTS.md` など、`~/.claude/CLAUDE.md`）も `workspace/` から非破壊で置きます。家のMacと持ち運びMacの2台で使う手順は [docs/claude/SECOND_MAC_SETUP.md](docs/claude/SECOND_MAC_SETUP.md) を参照してください。

Unity側を別の場所にcloneした場合は `./setup_mac.command --3d-saber "/path/to/3D-Saber"` を使用します。`--check` は書き込みなしの環境確認、`--verify` は通常setupに加えてセットアップツール自身のテストも実行します。通常setupはGit LFSを導入済みなら初期化し、Unity assetを取得して、既存のinstaller経由でDesktop launcherを設置します。Homebrewがあれば不足したGit LFSを `brew install git-lfs` で導入します。Homebrew、[Codex CLI](https://learn.chatgpt.com/docs/codex/cli)、Xcode、Unity Hub/Editor、iPhoneの署名と実機実行は画面の案内に従い手動で用意してください。Codex CLIの認証は `codex login` で本人が行います。Makinas fontは別途ローカルに設置しますが、EditModeテスト用のNotoSansJP-Lightはリポジトリに含まれています。

通常setupの報告は `~/Library/Logs/PhoneSaber/setup-latest.log` に保存されます。`--check` はログも作りません。終了コードは `0` が準備完了、`1` が必須処理の失敗、`2` が手動作業ありです。通常setupはPhoneSaber Toolsのテスト、Python構文、`git diff --check` を実行します。受信プロセス、Unityの長いPlayModeテスト、iOS実機テストは起動しません。iOS/Unityの全テストが必要な場合は既存の `./tools/verify_phone_saber.sh` を使います。

## 開発の状況と調査結果

残作業は [STATUS](docs/claude/STATUS.md) の優先順リスト、過去の調査と採用・見送りの根拠は [FINDINGS](docs/claude/FINDINGS.md) を参照してください。ラベル・数値一覧は `docs/claude/data/`、当日の手順は [運用手順](docs/claude/EVENT_DAY_RUNBOOK.md) にあります。
