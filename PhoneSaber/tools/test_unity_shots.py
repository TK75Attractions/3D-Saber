"""実アプリを起動せず、撮影成果物の成功判定を確かめる。"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SCENES = ('Title', 'SongSelect', 'Game', 'Result', 'Calibration', 'CalibrationReturn', 'TitleReturn')
SUCCESS = 'done exceptions=0 errors=0 input=ok cuts=3 flow=ok'

class UnityShotsTests(unittest.TestCase):
    def run_shots(self, scenes=" ".join(SCENES), completion=SUCCESS, empty="",
                  stale=False, launch_failure=False, cleanup_failure=False, launch_exit=0):
        source = Path(__file__).with_name("unity_copy.sh").read_text()
        function = "shots_player() {" + source.split("shots_player() {", 1)[1].split("\n}\n", 1)[0] + "\n}\n"
        # 起動だけ置き換え、リポジトリの判定処理はそのまま実行する。
        mock = '''
run_hidden() {
    if [[ "$MOCK_LAUNCH_FAILURE" == 1 ]]; then return 1; fi
    for scene in $MOCK_SCENES; do
        printf png > "$OUT/shots/$scene.png"
    done
    if [[ -n "$MOCK_EMPTY" ]]; then : > "$OUT/shots/$MOCK_EMPTY.png"; fi
    printf '[PhoneSaberSmoke] %s\n' "$MOCK_COMPLETION" > "$OUT/shots.log"
    return "$MOCK_LAUNCH_EXIT"
}
pgrep() { return 1; }
if [[ "$MOCK_CLEANUP_FAILURE" == 1 ]]; then rm() { return 1; }; fi
'''
        with tempfile.TemporaryDirectory() as tmp:
            app = Path(tmp) / "Fake.app"
            exe = app / "Contents/MacOS/fake"
            exe.parent.mkdir(parents=True)
            exe.write_text("unused")
            exe.chmod(0o700)
            if stale:
                shots = Path(tmp) / "shots"
                shots.mkdir()
                for scene in ("Title", "SongSelect", "Game"):
                    (shots / (scene + ".png")).write_bytes(b"old png")
                (Path(tmp) / "shots.log").write_text("[PhoneSaberSmoke] done exceptions=0 input=ok\n")
            return subprocess.run(
                ["bash", "-c", function + mock + 'shots_player "$APP"'],
                env=dict(os.environ, OUT=tmp, APP=str(app), MOCK_SCENES=scenes,
                         MOCK_COMPLETION=completion, MOCK_EMPTY=empty,
                         MOCK_LAUNCH_FAILURE=str(int(launch_failure)),
                         MOCK_CLEANUP_FAILURE=str(int(cleanup_failure)), MOCK_LAUNCH_EXIT=str(launch_exit)),
                text=True, capture_output=True, timeout=10)

    def test_all_scenes_and_successful_completion_pass(self):
        result = self.run_shots()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_exception_after_successful_completion_fails(self):
        # 終了処理の例外は、既に出力された done の件数には入らない。open はそれでも 0 を返す。
        for exception in ("NullReferenceException: shutdown failed", "System.IO.IOException: shutdown failed"):
            with self.subTest(exception=exception):
                result = self.run_shots(completion=SUCCESS + "\n" + exception)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_nonzero_exit_after_successful_completion_fails(self):
        for code in (1, 137):
            with self.subTest(code=code):
                result = self.run_shots(launch_exit=code)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_missing_scene_fails(self):
        for missing in SCENES:
            with self.subTest(missing=missing):
                scenes = " ".join(s for s in SCENES if s != missing)
                self.assertNotEqual(self.run_shots(scenes=scenes).returncode, 0)

    def test_empty_image_fails(self):
        self.assertNotEqual(self.run_shots(empty="Game").returncode, 0)

    def test_missing_or_failed_completion_fails(self):
        for completion in ("shot Title saved", "done exceptions=0 input=ok",
                           SUCCESS.replace('exceptions=0', 'exceptions=1'),
                           SUCCESS.replace('errors=0', 'errors=1'),
                           SUCCESS.replace('input=ok', 'input=FAILED'),
                           SUCCESS.replace('cuts=3', 'cuts=0'), SUCCESS.replace('flow=ok', 'flow=FAILED')):
            with self.subTest(completion=completion):
                self.assertNotEqual(self.run_shots(completion=completion).returncode, 0)

    def test_duplicate_completion_is_failure(self):
        self.assertNotEqual(self.run_shots(completion=SUCCESS + '\n[PhoneSaberSmoke] ' + SUCCESS).returncode, 0)

    def test_old_success_is_not_reused_when_launch_fails(self):
        self.assertNotEqual(self.run_shots(stale=True, launch_failure=True).returncode, 0)

    def test_old_images_do_not_complete_a_partial_new_run(self):
        self.assertNotEqual(self.run_shots(stale=True, scenes="Title").returncode, 0)

    def test_cleanup_failure_cannot_reuse_old_success(self):
        result = self.run_shots(stale=True, launch_failure=True, cleanup_failure=True)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
