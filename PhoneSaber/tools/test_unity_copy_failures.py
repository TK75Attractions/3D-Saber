"""実アプリを起動せず、検証結果の初期化と Player 配布の失敗を再現する。"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).with_name("unity_copy.sh")


class UnityCopyFailureTests(unittest.TestCase):
    def run_branch(self, mode, cleanup_failure=False, copy_failure=False, promotion_failure=False, game_starts=False,
                   smoke_results=None, icon_result=None):
        source = SOURCE.read_text()
        smoke = "smoke_player() {" + source.split("smoke_player() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        branch = source.split(mode + ")\n", 1)[1].split("\n    ;;", 1)[0]
        with tempfile.TemporaryDirectory(prefix="unity-copy-failure-") as tmp:
            root = Path(tmp)
            app = root / "Builds/Mac/3D-Saber.app"
            app.mkdir(parents=True)
            (app / "previous-player").write_text("working player")
            if icon_result is not None:
                icon = root / "PhoneSaber/mac/app/icon.icns"
                icon.parent.mkdir(parents=True)
                icon.write_text("new icon")
            out = root / "out"
            out.mkdir()
            copy = root / "copy"
            exe = copy / "Builds/Mac/3D-Saber.app/Contents/MacOS/fake"
            exe.parent.mkdir(parents=True)
            exe.write_text("unused")
            exe.chmod(0o700)
            (out / "smoke.log").write_text("[PhoneSaberSmoke] done exceptions=0 input=ok\n")
            (out / "build-mac.log").write_text("Result: Success\n")
            (out / "editmode.xml").write_text("old successful XML")
            called = root / "launched"
            # open は Player の異常終了でも 0 を返す。古いログ以外の成果物は出さない。
            stubs = '''
sync_copy() { return 0; }
run_hidden() { touch "$CALLED"; return 0; }
pgrep_calls=0
pgrep() { pgrep_calls=$((pgrep_calls + 1)); [[ "$GAME_STARTS" == 1 && "$pgrep_calls" -gt 1 ]]; }
python3() { [[ -f "$OUT/editmode.xml" ]]; }
if [[ "$CLEANUP_FAILURE" == 1 ]]; then rm() { return 1; }; fi
'''
            if mode == "build-mac":
                stubs += '''
smoke_calls=0
smoke_player() {
    # SMOKE_RESULTS: 呼び出しごとの結果。flow = 画面の流れだけ失敗、fail = 例外など、ok = 成功。
    smoke_calls=$((smoke_calls + 1)); echo "$smoke_calls" > "$OUT/smoke-calls"
    local result; result=$(echo "${SMOKE_RESULTS:-ok}" | cut -d, -f"$smoke_calls"); result=${result:-ok}
    case "$result" in
        flow) printf '[PhoneSaberSmoke] done exceptions=0 errors=0 input=ok cuts=3 flow=FAILED\\n' > "$OUT/smoke.log"; return 1 ;;
        fail) printf '[PhoneSaberSmoke] done exceptions=1 errors=0 input=ok cuts=3 flow=ok\\n' > "$OUT/smoke.log"; return 1 ;;
        *) return 0 ;;
    esac
}
run_hidden() { touch "$CALLED"; mkdir -p "$COPY/Builds/Mac/3D-Saber.app"; printf 'Result: Success\\n' > "$OUT/build-mac.log"; }
ditto() {
    mkdir -p "$2"
    if [[ "$COPY_FAILURE" == 1 ]]; then printf partial > "$2/incomplete"; return 1; fi
    printf complete > "$2/new-player"
    if [[ -n "$ICON_RESULT" ]]; then
        mkdir -p "$2/Contents/Resources"
        printf original > "$2/Contents/Resources/PlayerIcon.icns"
    fi
}
codesign() { touch "$OUT/signed"; [[ "$ICON_RESULT" != sign ]]; }
if [[ "$ICON_RESULT" == copy ]]; then cp() { return 1; }; fi
if [[ "$PROMOTION_FAILURE" == 1 ]]; then
    mv() { [[ "$1" == */.3D-Saber-new.app ]] && return 1; command mv "$@"; }
fi
'''
            command = smoke + stubs + (('smoke_player "$COPY/Builds/Mac/3D-Saber.app"') if mode == "smoke" else branch)
            result = subprocess.run(["/bin/bash", "-c", command], capture_output=True, text=True, timeout=10,
                                    env=dict(os.environ, ROOT=str(root), COPY=str(copy), OUT=str(out), UNITY="unused",
                                             CALLED=str(called), CLEANUP_FAILURE=str(int(cleanup_failure)),
                                             COPY_FAILURE=str(int(copy_failure)),
                                             PROMOTION_FAILURE=str(int(promotion_failure)),
                                             GAME_STARTS=str(int(game_starts)),
                                             SMOKE_RESULTS=smoke_results or "ok", ICON_RESULT=icon_result or ""))
            self.smoke_calls = int((out / "smoke-calls").read_text()) if (out / "smoke-calls").exists() else 0
            self.signed = (out / "signed").exists()
            icon = app / "Contents/Resources/PlayerIcon.icns"
            self.distributed_icon = icon.read_text() if icon.exists() else None
            return result, called.exists(), (app / "previous-player").is_file()

    def test_flow_only_smoke_failure_is_retried_once(self):
        result, _, _ = self.run_branch("build-mac", smoke_results="flow,ok")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.smoke_calls, 2)
        self.assertIn("1回だけやり直します", result.stdout)

    def test_repeated_flow_failure_or_exception_does_not_replace_the_player(self):
        for results, calls in (("flow,flow", 2), ("fail,ok", 1)):
            result, _, previous = self.run_branch("build-mac", smoke_results=results)
            self.assertEqual(result.returncode, 4, results)
            self.assertEqual(self.smoke_calls, calls, results)
            self.assertTrue(previous, results)

    def test_smoke_cleanup_failure_never_reuses_old_success(self):
        result, launched, _ = self.run_branch("smoke", cleanup_failure=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(launched)

    def test_editmode_cleanup_failure_stops_before_launch(self):
        result, launched, _ = self.run_branch("editmode", cleanup_failure=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(launched)

    def test_build_cleanup_failure_stops_before_launch(self):
        result, launched, previous = self.run_branch("build-mac", cleanup_failure=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(launched)
        self.assertTrue(previous)

    def test_failed_distribution_keeps_the_previous_player_at_its_launch_path(self):
        result, _, previous = self.run_branch("build-mac", copy_failure=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(previous, "コピー失敗で稼働可能な旧 Player を起動パスから移動しない")

    def test_successful_distribution_replaces_the_previous_player(self):
        result, launched, previous = self.run_branch("build-mac")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(launched)
        self.assertFalse(previous)

    def test_failed_icon_copy_or_signing_keeps_the_previous_player(self):
        for failure in ("copy", "sign"):
            with self.subTest(failure=failure):
                result, _, previous = self.run_branch("build-mac", icon_result=failure)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertTrue(previous, "アイコン差し替え・再署名に失敗した Player を配布しない")
                self.assertEqual(self.signed, failure == "sign")

    def test_successful_icon_signing_distributes_the_new_player(self):
        result, _, previous = self.run_branch("build-mac", icon_result="ok")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertFalse(previous)
        self.assertTrue(self.signed)
        self.assertEqual(self.distributed_icon, "new icon")

    def test_failed_promotion_restores_the_previous_player(self):
        result, _, previous = self.run_branch("build-mac", promotion_failure=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertTrue(previous)

    def test_game_started_during_smoke_is_not_replaced(self):
        result, _, previous = self.run_branch("build-mac", game_starts=True)
        self.assertEqual(result.returncode, 3, result.stdout + result.stderr)
        self.assertTrue(previous)


if __name__ == "__main__":
    unittest.main()
