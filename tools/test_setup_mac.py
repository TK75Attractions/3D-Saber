"""Isolated checks for the portable Mac setup tool."""
from __future__ import annotations

import os
import plistlib
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import setup_mac as setup

SOURCE_REPO = Path(__file__).resolve().parent.parent


class SetupMacTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary = tempfile.TemporaryDirectory(prefix="セットアップ space ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "school-festival"
        self.unity = self.root / "3D-Saber"
        self.home = self.root / "other user"
        self.home.mkdir()
        for path in (self.repo, self.unity):
            path.mkdir()
            subprocess.run(["git", "init", "-q", "-b", "main", str(path)], check=True)
            subprocess.run(["git", "-C", str(path), "remote", "add", "origin", "https://example.invalid/repo.git"], check=True)
        tools = self.repo / setup.TOOLS
        tools.mkdir(parents=True)
        for name in (*setup.MODEL_PINS, "phone_saber_receiver_launcher.py", "Start PhoneSaber.command",
                     "Open PhoneSaber Log.command", "Open Latest PhoneSaber Images.command",
                     "PhoneSaber Status.command", "install_phone_saber_launcher.command"):
            shutil.copy2(SOURCE_REPO / setup.TOOLS / name, tools / name)
        project = self.repo / "ios/PhoneSaberSender/PhoneSaberSender.xcodeproj"
        project.mkdir(parents=True)
        (project / "project.pbxproj").write_text("INFOPLIST_KEY_NSLocalNetworkUsageDescription = hi;\nINFOPLIST_FILE = BonjourInfo.plist;\nDEVELOPMENT_TEAM = test;\n")
        (self.repo / "ios/PhoneSaberSender/BonjourInfo.plist").write_bytes(
            plistlib.dumps({"NSBonjourServices": sorted(setup.REQUIRED_SERVICES)}))
        version = self.unity / "ProjectSettings/ProjectVersion.txt"
        version.parent.mkdir(parents=True)
        version.write_text("m_EditorVersion: 6000.3.9f1\n")
        font = self.unity / "Assets/Resources/Fonts/NotoSansJP-Light.otf"
        font.parent.mkdir(parents=True)
        font.write_bytes(b"test fixture")

    def fake_system(self, missing: set[str] | None = None):
        original = setup.run
        missing = missing or set()

        def wrapped(*args, **kwargs):
            key = args[0]
            if key in {"sw_vers", "uname", "brew", "python3", "codex", "xcode-select", "xcodebuild"}:
                if key in missing or (key == "codex" and "login" in args and "login" in missing):
                    return subprocess.CompletedProcess(args, 127, "", "not available")
                if key == "python3" and "phone_saber_receiver_launcher.py" in " ".join(args):
                    return subprocess.CompletedProcess(args, 0, "dry-run complete", "")
                value = {"sw_vers": "26.6", "uname": "arm64", "brew": "Homebrew 5", "python3": "Python 3.12",
                         "codex": "codex-cli 0.157", "xcode-select": "/Applications/Xcode.app/Contents/Developer",
                         "xcodebuild": "Xcode 27.0"}[key]
                if key == "codex" and "login" in args:
                    return subprocess.CompletedProcess(args, 0, "", "Logged in using ChatGPT")
                return subprocess.CompletedProcess(args, 0, value, "")
            if args[:3] == ("git", "lfs", "version"):
                return subprocess.CompletedProcess(args, 127 if "lfs" in missing else 0,
                                                   "" if "lfs" in missing else "git-lfs/3.8", "")
            if args[0] == "git" and "lfs" in args:
                return subprocess.CompletedProcess(args, 0, "", "")
            return original(*args, **kwargs)
        return wrapped

    def test_discovery_default_override_and_unicode_space(self):
        self.assertEqual(setup.discover_unity(self.repo, None), self.unity.resolve())
        custom = self.root / "別の 場所" / "3D-Saber"
        self.assertEqual(setup.discover_unity(self.repo, custom), custom.resolve())

    def test_missing_unity_repository_is_error(self):
        with mock.patch.object(setup, "run", side_effect=self.fake_system()), mock.patch.object(setup.sys, "platform", "darwin"):
            report, content = setup.perform(self.repo, self.root / "absent", self.home, True, False)
        self.assertGreater(report.required, 0)
        self.assertIn("3D-Saber", content)
        self.assertIn("directory missing", content)

    def test_missing_dependencies_are_actionable(self):
        with mock.patch.object(setup, "run", side_effect=self.fake_system({"lfs", "codex", "xcodebuild"})), \
             mock.patch.object(setup.sys, "platform", "darwin"), mock.patch.object(setup, "editor_present", return_value=False):
            report, content = setup.perform(self.repo, self.unity, self.home, True, False)
        self.assertGreaterEqual(report.manual, 4)
        for expected in ("Git LFS", "Codex CLI is not installed", "Install/open Xcode", "Install Unity 6000.3.9f1"):
            self.assertIn(expected, content)

    def test_check_does_not_write_or_install(self):
        calls = []
        fake = self.fake_system()
        def spy(*args, **kwargs):
            calls.append(args)
            return fake(*args, **kwargs)
        with mock.patch.object(setup, "run", side_effect=spy), mock.patch.object(setup.sys, "platform", "darwin"), \
             mock.patch.object(setup, "editor_present", return_value=True):
            setup.perform(self.repo, self.unity, self.home, True, False)
        self.assertEqual(list(self.home.iterdir()), [])
        self.assertFalse(any(args[:2] == ("brew", "install") or ("lfs" in args and "pull" in args)
                             or "install_phone_saber_launcher.command" in " ".join(args) for args in calls))

    def test_launcher_installer_integration_is_repeatable(self):
        env = {**os.environ, "HOME": str(self.home)}
        installer = self.repo / setup.TOOLS / "install_phone_saber_launcher.command"
        for _ in range(2):
            result = subprocess.run(["/bin/bash", str(installer)], env=env, capture_output=True, text=True)
            self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(setup.launcher_links(self.repo, self.home))
        viewer = self.home / "Desktop/Open Latest PhoneSaber Images.command"
        self.assertTrue(viewer.is_symlink())
        self.assertEqual(viewer.resolve(), (self.repo / setup.TOOLS / viewer.name).resolve())
        status = self.home / "Desktop/PhoneSaber Status.command"
        self.assertTrue(status.is_symlink())
        self.assertEqual(status.resolve(), (self.repo / setup.TOOLS / status.name).resolve())
        viewer.unlink()
        self.assertFalse(setup.launcher_links(self.repo, self.home))

    def test_launcher_list_matches_installer(self):
        installer = (SOURCE_REPO / setup.TOOLS / "install_phone_saber_launcher.command").read_text()
        for name in setup.DESKTOP_LAUNCHERS:
            self.assertIn(f'"$desktop_dir/{name}"', installer)
        self.assertEqual(installer.count('"$desktop_dir/'), len(setup.DESKTOP_LAUNCHERS))

    def test_setup_upgrades_previous_install_with_status_link(self):
        desktop = self.home / "Desktop"
        desktop.mkdir()
        for name in setup.DESKTOP_LAUNCHERS[:3]:
            (desktop / name).symlink_to((self.repo / setup.TOOLS).resolve() / name)
        start = desktop / "Start PhoneSaber.command"
        before = os.lstat(start)
        self.assertFalse(setup.launcher_links(self.repo, self.home))
        env = {**os.environ, "HOME": str(self.home)}
        installer = self.repo / setup.TOOLS / "install_phone_saber_launcher.command"
        result = subprocess.run(["/bin/bash", str(installer)], env=env, capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(setup.launcher_links(self.repo, self.home))
        after = os.lstat(start)
        self.assertEqual((after.st_ino, after.st_mtime_ns), (before.st_ino, before.st_mtime_ns))

    def test_log_generation_and_secret_not_copied(self):
        fake = self.fake_system()
        def with_secret(*args, **kwargs):
            if args[:3] == ("codex", "login", "status"):
                return subprocess.CompletedProcess(args, 1, "", "sk-secret-credential-value")
            return fake(*args, **kwargs)
        with mock.patch.object(setup, "run", side_effect=with_secret), \
             mock.patch.object(setup.sys, "platform", "darwin"), mock.patch.object(setup, "editor_present", return_value=True):
            _, content = setup.perform(self.repo, self.unity, self.home, True, False)
        path = setup.write_report(self.home, content)
        self.assertEqual((path.parent / "setup-latest.log").resolve(), path.resolve())
        self.assertNotIn("sk-secret-credential-value", path.read_text())
        self.assertIn("Codex login required", path.read_text())


if __name__ == "__main__":
    unittest.main()
