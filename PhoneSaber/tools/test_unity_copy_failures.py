"""実アプリを起動せず、検証結果の初期化と Player 配布の失敗を再現する。"""
import os
from pathlib import Path
import plistlib
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).with_name("unity_copy.sh")
HELPERS = SOURCE.read_text().split("unity_child() {", 1)[1].split("# Title →", 1)[0]
HELPERS = "set -u\nunity_child() {" + HELPERS


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
run_unity() { run_hidden "$UNITY" "$@"; }
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
            command = HELPERS + smoke + stubs + (('smoke_player "$COPY/Builds/Mac/3D-Saber.app"') if mode == "smoke" else branch)
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


class UnityEnvironmentTests(unittest.TestCase):
    def bash(self, command, **kwargs):
        return subprocess.run(["/bin/bash", "-c", HELPERS + command],
                              capture_output=True, text=True, timeout=15, **kwargs)

    def test_license_log_patterns(self):
        patterns = [
            "[Licensing::Client] Failed to acquire global mutex. Another instance is already running",
            "Another instance of Unity.Licensing.Client is already running",
            "Another instance of the Unity Licensing Client is already running",
            "Licensing initialization failed",
            "Timed-out after 60 seconds waiting for Licensing to initialize",
            "Timed out waiting for Licensing",
            "[Package Manager] Unknown entitlement status",
        ]
        with tempfile.TemporaryDirectory() as tmp:
            log = Path(tmp) / "unity.log"
            for message in patterns + ["Result: Success", "License activated successfully",
                                       "error CS0234: UnityEngine.InputSystem / TMPro / UIModule",
                                       "Timed-out waiting for Package Manager", ""]:
                with self.subTest(message=message):
                    log.write_text(message)
                    result = self.bash('unity_license_failure "$LOG"', env=dict(os.environ, LOG=str(log)))
                    self.assertEqual(result.returncode, 0 if message in patterns else 1, result.stderr)
            log.unlink()
            result = self.bash('unity_license_failure "$LOG"', env=dict(os.environ, LOG=str(log)))
            self.assertNotEqual(result.returncode, 0)

    def run_step(self, mode="editmode", editor="none", messages=None, code=0, client=True,
                 ps_failure=False, client_stuck=False, reused_pid=False, cleanup_failure=False):
        with tempfile.TemporaryDirectory(prefix="unity-environment-") as tmp:
            root = Path(tmp)
            out = root / "out"
            out.mkdir()
            processes = root / "processes"
            rows = ["88 /Applications/Unity Hub.app/Contents/Frameworks/UnityLicensingClient.app/Contents/MacOS/Unity.Licensing.Client"] if client else []
            if editor != "none":
                rows.append("42 /Applications/Unity/Unity.app/Contents/MacOS/Unity")
            # Player や Unity Hub は対話 Editor と混同しない。
            rows.extend(["43 /Applications/Unity Hub.app/Contents/MacOS/Unity Hub",
                         "44 /tmp/3D-Saber.app/Contents/MacOS/3D-Saber"])
            processes.write_text("\n".join(rows) + "\n")
            for index, message in enumerate(messages or ["Licensing initialization failed", "Result: Success"]):
                (root / f"log-{index}").write_text(message)
            stubs = r'''
calls=0
ps() {
    [[ "$PS_FAILURE" == 1 ]] && return 1
    case "$*" in
        '-axww -o pid=,comm=') cat "$ROOT/processes" ;;
        '-ww -p 42 -o args=')
            [[ "$EDITOR" == unknown ]] && return 1
            printf '/Applications/Unity/Unity.app/Contents/MacOS/Unity -projectPath /tmp/copy'
            [[ "$EDITOR" == batch ]] && printf ' -batchmode'
            # 別の引数に含まれる文字列は batchmode フラグではない。
            [[ "$EDITOR" == interactive ]] && printf ' -logFile /tmp/-batchmode.log'
            printf '\n' ;;
        '-ww -p 88 -o comm=')
            if [[ "$REUSED_PID" == 1 ]]; then echo /Applications/Unity/Unity.app/Contents/MacOS/Unity
            else echo /tmp/Unity.Licensing.Client; fi ;;
        *) return 1 ;;
    esac
}
kill() {
    if [[ "$1" == -0 ]]; then [[ "$CLIENT_STUCK" == 1 ]]; return; fi
    printf '%s\n' "$*" >> "$ROOT/signals"
}
sleep() { :; }
run_unity() {
    # 再試行前の成果物の初期化を実際に確認する。
    [[ ! -e "$OUT/editmode.xml" && ! -e "$COPY/Builds/Mac" ]] || return 99
    cp "$ROOT/log-$calls" "$LOG"
    mkdir -p "$COPY/Builds/Mac"
    printf 'old XML' > "$OUT/editmode.xml"
    if [[ "$MODE" == editmode ]]; then rm -rf "$COPY/Builds/Mac"
    else rm -f "$OUT/editmode.xml"; fi
    calls=$((calls + 1)); echo "$calls" > "$ROOT/calls"
    return "$CODE"
}
if [[ "$CLEANUP_FAILURE" == 1 ]]; then
    rm() { [[ "$calls" -gt 0 ]] && return 1; command rm "$@"; }
fi
run_unity_step "$MODE" "$LOG" -batchmode
'''
            result = self.bash(stubs, env=dict(
                os.environ, ROOT=str(root), OUT=str(out), COPY=str(root / "copy"),
                LOG=str(out / f"{mode}.log"), MODE=mode, EDITOR=editor, CODE=str(code),
                PS_FAILURE=str(int(ps_failure)), CLIENT_STUCK=str(int(client_stuck)),
                REUSED_PID=str(int(reused_pid)), CLEANUP_FAILURE=str(int(cleanup_failure))))
            calls = int((root / "calls").read_text()) if (root / "calls").exists() else 0
            signals = (root / "signals").read_text() if (root / "signals").exists() else ""
            first = out / f"{mode}.log.first-try"
            return result, calls, signals, first.read_text() if first.exists() else None

    def test_retry_once_with_no_interactive_editor_for_both_steps(self):
        for mode in ("editmode", "build-mac"):
            for editor in ("none", "batch"):
                with self.subTest(mode=mode, editor=editor):
                    result, calls, signals, first = self.run_step(mode=mode, editor=editor)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertEqual(calls, 2)
                    self.assertEqual(signals, "-TERM 88\n")
                    self.assertEqual(first, "Licensing initialization failed")

    def test_interactive_editor_or_unknown_process_state_never_sends_signal(self):
        for options in ({"editor": "interactive"}, {"editor": "unknown"}, {"ps_failure": True},
                        {"client": False}, {"reused_pid": True}):
            with self.subTest(options=options):
                result, calls, signals, _ = self.run_step(**options)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, 1)
                self.assertEqual(signals, "")
                if options.get("editor") == "interactive":
                    self.assertIn("対話 Editor が起動中", result.stdout)

    def test_repeated_licensing_failure_stops_after_second_launch_even_with_exit_zero(self):
        result, calls, signals, _ = self.run_step(messages=["Unknown entitlement status"] * 2)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(calls, 2)
        self.assertEqual(signals, "-TERM 88\n")

    def test_unrelated_failure_or_success_keeps_exit_code_without_killing_or_retrying(self):
        for code in (0, 1, 2):
            result, calls, signals, first = self.run_step(messages=["error CS0234: UnityEngine.InputSystem"], code=code)
            self.assertEqual(result.returncode, code, result.stdout + result.stderr)
            self.assertEqual(calls, 1)
            self.assertEqual(signals, "")
            self.assertIsNone(first)

    def test_stuck_client_or_failed_retry_cleanup_prevents_second_launch(self):
        for options in ({"client_stuck": True}, {"cleanup_failure": True}):
            with self.subTest(options=options):
                result, calls, _, _ = self.run_step(**options)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, 1)

    # OS の上限は変更せず、実際の子 bash / 隠しランチャーに模擬値を継承させる。
    # setter は soft の指定だけを受け付け、hard の変更や想定外の呼び出しは失敗させる。
    LIMIT_STUB = r'''
export TEST_SOFT TEST_HARD TEST_LIMIT_ERROR
ulimit() {
    case "$*" in
        '-S -n') [[ "$TEST_LIMIT_ERROR" != read ]] || return 1; echo "$TEST_SOFT" ;;
        '-H -n') echo "$TEST_HARD" ;;
        '-S -n 65536')
            [[ "$TEST_LIMIT_ERROR" != set ]] || return 1
            [[ "$TEST_HARD" == unlimited || "$TEST_HARD" -ge 65536 ]] || return 1
            export TEST_SOFT=65536 ;;
        *) echo "unexpected ulimit: $*" >&2; return 99 ;;
    esac
}
export -f ulimit
'''

    def limit_bash(self, command, soft, hard, error="", **environment):
        return self.bash(self.LIMIT_STUB + command, env=dict(
            os.environ, TEST_SOFT=str(soft), TEST_HARD=str(hard), TEST_LIMIT_ERROR=error,
            **environment))

    def test_soft_limit_is_set_only_in_child_and_hard_limit_is_preserved(self):
        for initial, hard, expected in ((1024, 1048576, 65536), (65536, 65536, 65536),
                                        (1048576, 1048576, 65536), ("unlimited", "unlimited", 65536),
                                        (1024, 16384, 1024), (16384, 16384, 16384)):
            with self.subTest(initial=initial, hard=hard):
                result = self.limit_bash('''
UNITY=/bin/bash
run_unity -c 'echo "child=$(ulimit -S -n) hard=$(ulimit -H -n)"' || exit $?
echo "parent=$(ulimit -S -n) hard=$(ulimit -H -n)"
''', initial, hard)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.splitlines(),
                                 [f"child={expected} hard={hard}", f"parent={initial} hard={hard}"])
                if expected != 65536:
                    self.assertIn("上げられません", result.stderr)
                else:
                    self.assertEqual(result.stderr, "")

    def test_low_hard_limit_is_respected(self):
        result = self.limit_bash('''
unity_child /bin/bash -c 'echo "$(ulimit -S -n) $(ulimit -H -n)"'
''', 1024, 1024)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "1024 1024")
        self.assertIn("上げられません", result.stderr)

    def test_limit_read_or_unsafe_reduction_failure_prevents_launch(self):
        for soft, error in ((1024, "read"), (1048576, "set"), ("unlimited", "set")):
            with self.subTest(soft=soft, error=error):
                result = self.limit_bash("unity_child /bin/echo launched", soft, "unlimited", error)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertEqual(result.stdout, "")
                if error == "set":
                    self.assertIn("下げられません", result.stderr)

    def test_hidden_launcher_applies_limit_and_preserves_arguments_and_cleanup(self):
        for hard, expected in ((1048576, 65536), (16384, 1024)):
            with self.subTest(hard=hard):
                with tempfile.TemporaryDirectory(prefix="unity wrapper '") as tmp:
                    root = Path(tmp)
                    exe = root / "Unity.app/Contents/MacOS/Unity"
                    exe.parent.mkdir(parents=True)
                    exe.write_text('#!/bin/bash\necho "$(ulimit -S -n)"\nprintf "<%s>\\n" "$@"\nexit 7\n')
                    exe.chmod(0o755)
                    result = self.limit_bash('''
        run_hidden() {
            cp "${1%/MacOS/*}/Info.plist" "$CAPTURE"
            printf '%s' "${1%/Unity.app/*}" > "$WRAPPER_PATH"
            "$@"
        }
        run_unity -batchmode 'space arg' '$(literal)'
        ''', 1024, hard, UNITY=str(exe), TMPDIR=str(root), PHONESABER_UNITY_FOREGROUND="",
                      CAPTURE=str(root / "Info.plist"), WRAPPER_PATH=str(root / "wrapper-path"))
                    self.assertEqual(result.returncode, 7, result.stderr)
                    self.assertEqual(result.stdout.splitlines(), [str(expected), "<-batchmode>", "<space arg>", "<$(literal)>"])
                    plist = plistlib.loads((root / "Info.plist").read_bytes())
                    self.assertEqual(plist["CFBundleExecutable"], "launch")
                    self.assertFalse(Path((root / "wrapper-path").read_text()).exists())


if __name__ == "__main__":
    unittest.main()
