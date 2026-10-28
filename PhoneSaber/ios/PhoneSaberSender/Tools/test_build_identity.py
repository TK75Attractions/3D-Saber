"""ビルド時plist更新。git/LFSやXcodeを変更せず、一時repoで確認する。"""
from datetime import datetime, timezone
import os
from pathlib import Path
import plistlib
import shutil
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("write_build_identity.sh")


class BuildIdentityTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which("git-lfs"), "git-lfs required")
    def test_hydrated_lfs_file_is_clean_until_modified(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repo = root / "repo"
            repo.mkdir()

            def git(*args):
                return subprocess.check_output(["git", "-C", str(repo), "-c", "user.name=Test",
                    "-c", "user.email=test@example.invalid", *args], stderr=subprocess.DEVNULL, text=True).strip()

            git("init")
            git("lfs", "install", "--local")
            (repo / ".gitattributes").write_text("*.dat filter=lfs diff=lfs merge=lfs -text\n")
            (repo / "large.dat").write_bytes(b"test binary\x00" * 1000)
            git("add", ".gitattributes", "large.dat")
            git("commit", "-m", "LFS fixture")
            self.assertEqual(self.run_script(repo, root / "Info.plist")["PhoneSaberGitDirty"], "false")
            (repo / "large.dat").write_bytes(b"changed binary\x00" * 1000)
            self.assertEqual(self.run_script(repo, root / "Info.plist")["PhoneSaberGitDirty"], "true")

    def test_clean_dirty_and_staged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            repo = root / "repo"
            repo.mkdir()

            def git(*args):
                return subprocess.check_output(["git", "-C", str(repo), "-c", "user.name=Test",
                    "-c", "user.email=test@example.invalid", *args], stderr=subprocess.DEVNULL, text=True).strip()

            git("init")
            (repo / "source").write_text("original")
            git("add", "source")
            git("commit", "-m", "fixture")
            for state in ("clean", "modified", "staged", "untracked"):
                with self.subTest(state=state):
                    if state == "modified": (repo / "source").write_text("changed")
                    if state == "staged": git("add", "source")
                    if state == "untracked":
                        git("commit", "-m", "change")
                        (repo / "new").write_text("new")
                    values = self.run_script(repo, root / "Info.plist")
                    self.assertEqual(values["PhoneSaberGitCommit"], git("rev-parse", "--short=12", "HEAD"))
                    self.assertEqual(values["PhoneSaberGitDirty"], "false" if state == "clean" else "true")

    def run_script(self, repo, plist, *, no_git=False):
        with plist.open("wb") as handle:
            plistlib.dump({"CFBundleVersion": "20261024", "other": 42}, handle)
        env = dict(os.environ)
        if no_git: env["PATH"] = str(plist.parent / "no-executables")
        before = datetime.now(timezone.utc).replace(microsecond=0)
        result = subprocess.run(["/bin/sh", str(SCRIPT), str(repo), str(plist)], env=env, capture_output=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        with plist.open("rb") as handle: values = plistlib.load(handle)
        self.assertEqual(values["CFBundleVersion"], "20261024")
        self.assertEqual(values["other"], 42)
        built = datetime.strptime(values["PhoneSaberBuildTime"], "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
        self.assertGreaterEqual(built, before)
        self.assertLessEqual(built, datetime.now(timezone.utc))
        return values

    def test_no_git_and_non_repo_are_unknown_and_successful(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for no_git in (True, False):
                with self.subTest(no_git=no_git):
                    values = self.run_script(root, root / "Info.plist", no_git=no_git)
                    self.assertEqual(values["PhoneSaberGitCommit"], "unknown")
                    self.assertEqual(values["PhoneSaberGitDirty"], "unknown")


if __name__ == "__main__":
    unittest.main()
