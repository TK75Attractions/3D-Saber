"""会場画像を読まず、合成 PNG・合成端点だけを使用する。"""

import contextlib
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zlib

import field_regression as field


def points(x1=20, y1=30, x2=100, y2=30):
    return [{"x": x1, "y": y1}, {"x": x2, "y": y2}]


def synthetic_png():
    def chunk(kind, payload):
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 160, 120, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress((b"\x00" + b"\x00\x00\x00" * 160) * 120)) + chunk(b"IEND", b""))


class FieldRegressionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="field-synthetic-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.label = {"sourceClip": "phone_saber_triage_autoclip_20261020_154404_000_test",
                      "image": "image_10_event_1.png", "exposure": -2,
                      "date": "2026-10-20T15:44:04+09:00", "red": "absent", "blue": points()}
        self.inbox = self.root / "inbox"
        clip = self.inbox / self.label["sourceClip"]
        (clip / "images").mkdir(parents=True)
        self.source = clip / "images" / self.label["image"]
        self.source.write_bytes(synthetic_png())
        (clip / "summary.json").write_text(json.dumps({"autoClip": {
            "frameSize": {"width": 160, "height": 120}, "camera": {"exposureBias": -2}}}))
        self.labels = self.root / "labels.json"
        self.destination = self.root / "set"
        self.save_labels()

    def save_labels(self):
        self.labels.write_text(json.dumps({"schemaVersion": 1, "frames": [self.label]}))

    def add(self):
        return field.add_frames(self.labels, self.inbox, self.destination)

    def test_add_copies_original_bytes_and_pins_private_label(self):
        self.assertEqual(self.add(), 1)
        [(path, label)] = field.load_set(self.destination)
        self.assertEqual(path.read_bytes(), self.source.read_bytes())
        self.assertEqual(label["sha256"], hashlib.sha256(self.source.read_bytes()).hexdigest())
        self.assertEqual(label["blue"], self.label["blue"])
        self.assertEqual(self.destination.stat().st_mode & 0o777, 0o700)
        for path in self.destination.iterdir():
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
        with self.assertRaisesRegex(ValueError, "重複"):
            self.add()

    def test_invalid_label_resolution_exposure_and_traversal_rejected(self):
        for update in ({"red": None}, {"blue": points(-1)}, {"blue": points(float("nan"))},
                       {"exposure": 0}, {"date": "wrong"}, {"image": "../event.png"},
                       {"sourceClip": "../clip"}, {"expectedFail": {"blue": {"failure": "miss"}}}):
            with self.subTest(update=update):
                original = copy.deepcopy(self.label)
                self.label.update(update)
                self.save_labels()
                with self.assertRaises((ValueError, KeyError)):
                    self.add()
                self.label = original
        self.save_labels()
        (self.source.parent.parent / "summary.json").write_text(json.dumps({"autoClip": {
            "frameSize": {"width": 320, "height": 240}, "camera": {"exposureBias": -2}}}))
        with self.assertRaisesRegex(ValueError, "フル解像度"):
            self.add()
        self.assertFalse(self.destination.exists())

    def test_git_directories_and_symlink_alias_are_rejected(self):
        repo = self.root / "repo"
        repo.mkdir()
        (repo / ".git").write_text("gitdir: elsewhere")
        alias = self.root / "alias"
        alias.symlink_to(repo, target_is_directory=True)
        for path in (repo / "private", alias / "private"):
            with self.assertRaisesRegex(ValueError, "Git"):
                field.outside_git(path)
        self.destination = repo / "set"
        with self.assertRaisesRegex(ValueError, "Git"):
            self.add()

    def test_missing_or_tampered_data_cannot_pass(self):
        with self.assertRaises(ValueError):
            field.load_set(self.destination)
        self.add()
        [(path, _)] = field.load_set(self.destination)
        path.write_bytes(path.read_bytes() + b"tamper")
        with self.assertRaisesRegex(ValueError, "ハッシュ"):
            field.load_set(self.destination)
        path.unlink()
        with self.assertRaises(ValueError):
            field.load_set(self.destination)

    def test_each_endpoint_order_tolerance_boundary_and_long_blade(self):
        self.assertEqual(field.evaluate(self.label, "blue", points()[::-1])["status"], "PASS")
        self.assertEqual(field.evaluate(self.label, "blue", points(32, 30, 112, 30))["status"], "PASS")
        # 平均誤差なら通るが片端だけ13pxずれたものは失敗。
        self.assertEqual(field.evaluate(self.label, "blue", points(20, 30, 113, 30))["failure"], "endpoints")
        self.label["blue"] = points(0, 0, 200, 0)
        self.assertEqual(field.evaluate(self.label, "blue", points(30, 0, 230, 0))["status"], "PASS")
        self.assertEqual(field.evaluate(self.label, "blue", points(31, 0, 230, 0))["status"], "FAIL")

    def test_miss_false_positive_and_explicit_expected_fail(self):
        self.assertEqual(field.evaluate(self.label, "red", None)["status"], "PASS")
        self.assertEqual(field.evaluate(self.label, "red", points())["failure"], "false-positive")
        self.assertEqual(field.evaluate(self.label, "blue", None)["status"], "FAIL")
        self.label["expectedFail"] = {"blue": {"failure": "miss", "reason": "合成の既知未検出"}}
        self.assertEqual(field.evaluate(self.label, "blue", None)["status"], "XFAIL")
        self.assertEqual(field.evaluate(self.label, "blue", points())["status"], "XPASS")
        self.assertEqual(field.evaluate(self.label, "blue", points(80, 0, 150, 0))["status"], "FAIL")

    def test_run_reuses_host_harness_and_cli_exit_codes(self):
        self.add()
        analysis = {"colors": {"red": {"selected": None}, "blue": {"selected": points()}}}
        with patch.object(field.lossless, "compile_harness") as compile_host, \
                patch.object(field.lossless, "analyze_png", return_value=analysis) as analyze, \
                contextlib.redirect_stdout(io.StringIO()) as output:
            self.assertEqual(field.main(["run", "--set-dir", str(self.destination)]), 0)
            compile_host.assert_called_once()
            analyze.assert_called_once()
            self.assertIn("赤 | PASS", output.getvalue())
            self.assertIn("青 | PASS", output.getvalue())
            analysis["colors"]["blue"]["selected"] = None
            self.assertEqual(field.main(["run", "--set-dir", str(self.destination)]), 1)
            analysis["colors"]["blue"]["selected"] = points()
            analysis["colors"]["red"]["selected"] = points()
            self.assertEqual(field.main(["run", "--set-dir", str(self.destination)]), 1)
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(field.main(["run", "--set-dir", str(self.root / "absent")]), 2)

    def test_verify_field_step_absent_pass_and_fail(self):
        # 総合検証の Field ブロックそのものを実行し、他ステップは起動しない。
        script = (Path(__file__).resolve().parents[3] / "tools/verify_phone_saber.sh").read_text()
        block = script[script.index('FIELD_SET="$HOME/'):script.index('report_stage "Field"')]
        for present, code, expected in ((False, 1, "SKIP"), (True, 0, "PASS"), (True, 1, "FAIL")):
            with self.subTest(expected=expected):
                home = self.root / expected
                if present:
                    (home / "Library/Application Support/PhoneSaber/field-regression").mkdir(parents=True)
                stub = ('FIELD_STATUS=SKIP\nREPO_ROOT=unused\n'
                        f'run_logged_command() {{ LAST_EXIT={code}; }}\n'
                        'mark_not_run() { LAST_EXIT=1; }\n')
                result = subprocess.run(["bash", "-uc", stub + block + '\nprintf "%s" "$FIELD_STATUS"'],
                                        env={**os.environ, "HOME": str(home)}, capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout, expected)
        self.assertIn('"$LOSSLESS_STATUS" "$FIELD_STATUS"', script)

    def test_cli_expected_fail_report_and_private_output_validation(self):
        self.label["expectedFail"] = {"blue": {"failure": "miss", "reason": "合成の既知未検出"}}
        self.save_labels()
        self.add()
        analysis = {"colors": {"red": {"selected": None}, "blue": {"selected": None}}}
        report = self.root / "report.json"
        with patch.object(field.lossless, "compile_harness"), \
                patch.object(field.lossless, "analyze_png", return_value=analysis), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            args = ["run", "--set-dir", str(self.destination)]
            self.assertEqual(field.main(args + ["--json", str(report)]), 0)
            self.assertEqual(json.loads(report.read_text())["rows"][1]["status"], "XFAIL")
            self.assertEqual(field.main(args + ["--json", str(report)]), 2)
            self.assertEqual(field.main(args + ["--json", str(self.destination / "report.json")]), 2)
            self.assertEqual(field.main(args + ["--json", str(Path(__file__).parent / "private.json")]), 2)
            # 既知の未検出が別の誤端点へ変わった場合は新しい失敗。
            analysis["colors"]["blue"]["selected"] = points(80, 0, 150, 0)
            self.assertEqual(field.main(args), 1)

    def test_host_failure_is_nonzero_and_restores_cache_environment(self):
        self.add()
        previous = {key: os.environ.get(key) for key in ("CLANG_MODULE_CACHE_PATH", "SWIFT_MODULECACHE_PATH")}
        with patch.object(field.lossless, "compile_harness", side_effect=RuntimeError("synthetic compile failure")), \
                contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(field.main(["run", "--set-dir", str(self.destination)]), 2)
        self.assertEqual(previous, {key: os.environ.get(key) for key in previous})


if __name__ == "__main__":
    unittest.main()
