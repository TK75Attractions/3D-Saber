"""Lifecycle, logging, path, and installer tests for the macOS launcher."""

from __future__ import annotations

import contextlib
import io
import os
import select
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import phone_saber_receiver_launcher as launcher


TOOLS_SOURCE = Path(__file__).resolve().parent


def make_repo(parent: Path, name: str = "縁日 workspace") -> tuple[Path, Path]:
    repo = parent / name
    tools = repo / "ios" / "PhoneSaberSender" / "Tools"
    tools.mkdir(parents=True)
    for filename in (
        "Start PhoneSaber.command",
        "Open PhoneSaber Log.command",
        "Open Latest PhoneSaber Images.command",
        "install_phone_saber_launcher.command",
        "phone_saber_open_images.py",
        "phone_saber_receiver_launcher.py",
    ):
        shutil.copy2(TOOLS_SOURCE / filename, tools / filename)
    subprocess.run(["git", "init", "-q"], cwd=repo, check=True)
    subprocess.run(["git", "checkout", "-qb", "main"], cwd=repo, check=True)
    subprocess.run(["git", "config", "user.name", "PhoneSaber Test"], cwd=repo, check=True)
    subprocess.run(
        ["git", "config", "user.email", "phonesaber-test@example.invalid"], cwd=repo, check=True,
    )
    (repo / "initial.txt").write_text("fixture\n", encoding="utf-8")
    subprocess.run(["git", "add", "initial.txt"], cwd=repo, check=True)
    subprocess.run(["git", "add", "ios"], cwd=repo, check=True)
    subprocess.run(["git", "commit", "-qm", "fixture"], cwd=repo, check=True)
    subprocess.run(["git", "update-ref", "refs/remotes/origin/main", "HEAD"], cwd=repo, check=True)
    return repo, tools


def snapshot_for(repo: Path, *, dirty: bool = False) -> launcher.GitSnapshot:
    return launcher.GitSnapshot(
        repo_root=repo,
        branch="main",
        status_short="## main\n" + ("?? local-change.txt\n" if dirty else ""),
        dirty=dirty,
        ahead=0,
        behind=0,
    )


class PhoneSaberReceiverLauncherTests(unittest.TestCase):
    def test_dry_run_resolves_japanese_path_and_reports_dirty_main(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo, tools = make_repo(root)
            home = root / "home"
            home.mkdir()
            env = {**os.environ, "HOME": str(home)}

            clean = subprocess.run(
                ["bash", str(tools / "Start PhoneSaber.command"), "--dry-run"],
                cwd=root, env=env, check=False, capture_output=True, text=True,
            )
            self.assertEqual(clean.returncode, 0, clean.stderr)
            self.assertIn(f"Repo:           {repo.resolve()}", clean.stdout)
            self.assertIn("Branch:         main", clean.stdout)
            self.assertIn("Git:            clean / 0 ahead / 0 behind", clean.stdout)
            self.assertIn("DRY RUN (receiver not started)", clean.stdout)
            self.assertFalse((home / "Library" / "Logs" / "PhoneSaber").exists())

            (repo / "local-change.txt").write_text("preserve me\n", encoding="utf-8")
            dirty = subprocess.run(
                ["bash", str(tools / "Start PhoneSaber.command"), "--dry-run"],
                cwd=root, env=env, check=False, capture_output=True, text=True,
            )
            self.assertEqual(dirty.returncode, 0, dirty.stderr)
            self.assertIn("Git:            dirty / 0 ahead / 0 behind", dirty.stdout)
            self.assertIn("WARNING: working tree has uncommitted changes", dirty.stdout)
            self.assertTrue((repo / "local-change.txt").exists())

    def test_missing_repository_has_a_clear_error(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            tools = root / "nested" / "ios" / "PhoneSaberSender" / "Tools"
            tools.mkdir(parents=True)
            for filename in ("Start PhoneSaber.command", "phone_saber_receiver_launcher.py"):
                shutil.copy2(TOOLS_SOURCE / filename, tools / filename)
            result = subprocess.run(
                ["bash", str(tools / "Start PhoneSaber.command"), "--dry-run"],
                cwd=root, check=False, capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 2)
            self.assertIn("repository not found at expected path", result.stderr)
            self.assertIn("school-festival repository", result.stderr)

    def test_installer_dry_run_and_idempotent_three_desktop_links(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo, tools = make_repo(root)
            home = root / "home"
            env = {**os.environ, "HOME": str(home)}
            command = ["bash", str(tools / "install_phone_saber_launcher.command")]

            dry_run = subprocess.run(
                [*command, "--dry-run"], cwd=root, env=env, check=False,
                capture_output=True, text=True,
            )
            self.assertEqual(dry_run.returncode, 0, dry_run.stderr)
            self.assertIn("would install", dry_run.stdout)
            self.assertFalse((home / "Desktop").exists())

            first = subprocess.run(command, cwd=root, env=env, check=False, capture_output=True, text=True)
            self.assertEqual(first.returncode, 0, first.stderr)
            desktop = home / "Desktop"
            launchers = (
                "Start PhoneSaber.command",
                "Open PhoneSaber Log.command",
                "Open Latest PhoneSaber Images.command",
            )
            links = [desktop / name for name in launchers]
            targets = [link.resolve() for link in links]
            for link, name, target in zip(links, launchers, targets):
                self.assertTrue(link.is_symlink())
                self.assertEqual(target, (tools / name).resolve())

            second = subprocess.run(command, cwd=root, env=env, check=False, capture_output=True, text=True)
            self.assertEqual(second.returncode, 0, second.stderr)
            self.assertEqual([link.resolve() for link in links], targets)

    def test_receiver_output_is_streamed_saved_and_latest_is_updated(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = root / "repo"
            repo.mkdir()
            log_dir = root / "Library" / "Logs" / "PhoneSaber"
            log_dir.mkdir(parents=True)
            for index in range(52):
                old_log = log_dir / f"triage-20200101-{index:06d}.log"
                old_log.write_text(f"old {index}\n", encoding="utf-8")
                os.utime(old_log, ns=(index + 1, index + 1))
            receiver = root / "fake_receiver.py"
            receiver.write_text(
                "import sys\n"
                "print('[triage] listening on 0.0.0.0:8765; inbox=test', flush=True)\n"
                "print('[triage] received phone_saber_triage_phonesaber_test from 127.0.0.1 → /tmp/bundle', flush=True)\n"
                "print('stdout marker', flush=True)\n"
                "print('stderr marker', file=sys.stderr, flush=True)\n",
                encoding="utf-8",
            )
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                exit_code = launcher.run_receiver(
                    repo,
                    snapshot_for(repo),
                    log_dir,
                    receiver_command=[sys.executable, str(receiver)],
                    listener_finder=lambda _port: None,
                )
            self.assertEqual(exit_code, 0)
            terminal_text = output.getvalue()
            self.assertIn("[PHONE_SABER][WAITING]", terminal_text)
            self.assertIn("[PHONE_SABER][SESSION] sessionID=phonesaber_test", terminal_text)
            self.assertIn("stdout marker", terminal_text)
            self.assertIn("stderr marker", terminal_text)

            latest = log_dir / "latest.log"
            self.assertTrue(latest.is_symlink())
            log_path = latest.resolve()
            self.assertRegex(log_path.name, r"^triage-\d{8}-\d{6}\.log$")
            saved = log_path.read_text(encoding="utf-8")
            self.assertIn("[PHONE_SABER][START]", saved)
            self.assertIn("[PHONE_SABER][WAITING]", saved)
            self.assertIn("sessionID=phonesaber_test", saved)
            self.assertIn("stdout marker", saved)
            self.assertIn("stderr marker", saved)
            self.assertLessEqual(len(list(log_dir.glob("triage-*.log"))), 50)

    def test_managed_duplicate_does_not_start_a_second_receiver(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = root / "repo"
            repo.mkdir()
            log_dir = root / "logs"
            log_dir.mkdir()
            lock, _ = launcher._acquire_single_instance_lock(log_dir)
            self.assertIsNotNone(lock)
            assert lock is not None
            active_log = log_dir / "triage-active.log"
            launcher._write_lock_metadata(lock, pid=4242, repo_root=repo, log_path=active_log)
            marker = root / "started.txt"
            fake_receiver = root / "fake_receiver.py"
            fake_receiver.write_text(
                f"from pathlib import Path\nPath({str(marker)!r}).write_text('started')\n",
                encoding="utf-8",
            )
            output = io.StringIO()
            try:
                with contextlib.redirect_stdout(output):
                    exit_code = launcher.run_receiver(
                        repo,
                        snapshot_for(repo),
                        log_dir,
                        receiver_command=[sys.executable, str(fake_receiver)],
                        listener_finder=lambda _port: self.fail("port check must not run after lock detects duplicate"),
                    )
            finally:
                lock.close()
            self.assertEqual(exit_code, 0)
            self.assertIn("PhoneSaber receiver is already running or starting", output.getvalue())
            self.assertIn(str(active_log), output.getvalue())
            self.assertFalse(marker.exists())

    def test_tcp_duplicate_detection_identifies_existing_receiver(self) -> None:
        with mock.patch.object(launcher.shutil, "which", side_effect=["/usr/sbin/lsof", "/bin/ps"]):
            with mock.patch.object(
                launcher.subprocess,
                "run",
                side_effect=[
                    SimpleNamespace(returncode=0, stdout="731\n", stderr=""),
                    SimpleNamespace(returncode=0, stdout="python phone_saber_triage_receiver.py", stderr=""),
                ],
            ):
                listener = launcher.find_existing_receiver()
        self.assertIsNotNone(listener)
        assert listener is not None
        self.assertTrue(listener.is_phone_saber_receiver)
        self.assertEqual(listener.pids, (731,))

    def test_ctrl_c_stops_receiver_process_group_and_waits_for_child(self) -> None:
        if not hasattr(signal, "SIGINT"):
            self.skipTest("SIGINT is unavailable")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            repo = root / "repo"
            repo.mkdir()
            log_dir = root / "logs"
            fake_receiver = root / "fake_receiver.py"
            child_pid_file = root / "child.pid"
            child_ready_file = root / "child-ready.txt"
            stopped_file = root / "stopped.txt"
            child_code = (
                "import pathlib, signal, time; "
                "signal.signal(signal.SIGINT, signal.SIG_IGN); "
                f"pathlib.Path({str(child_ready_file)!r}).write_text('ready'); "
                "time.sleep(60)"
            )
            fake_receiver.write_text(
                "import pathlib, subprocess, sys, time\n"
                f"child = subprocess.Popen([sys.executable, '-c', {child_code!r}])\n"
                f"while not pathlib.Path({str(child_ready_file)!r}).exists(): time.sleep(0.01)\n"
                f"pathlib.Path({str(child_pid_file)!r}).write_text(str(child.pid))\n"
                "print('[triage] listening on 0.0.0.0:8765; inbox=test', flush=True)\n"
                "try:\n"
                "    while True: time.sleep(0.1)\n"
                "except KeyboardInterrupt:\n"
                "    pass\n"
                "finally:\n"
                f"    pathlib.Path({str(stopped_file)!r}).write_text('shutdown complete')\n",
                encoding="utf-8",
            )
            harness = root / "run_launcher.py"
            harness.write_text(
                "import pathlib, sys\n"
                f"sys.path.insert(0, {str(TOOLS_SOURCE)!r})\n"
                "import phone_saber_receiver_launcher as launcher\n"
                "repo = pathlib.Path(sys.argv[3])\n"
                "snapshot = launcher.GitSnapshot(repo, 'main', '## main', False, 0, 0)\n"
                "command = [sys.executable, sys.argv[1]]\n"
                "raise SystemExit(launcher.run_receiver(repo, snapshot, pathlib.Path(sys.argv[2]), "
                "receiver_command=command, listener_finder=lambda _port: None))\n",
                encoding="utf-8",
            )
            process = subprocess.Popen(
                [sys.executable, str(harness), str(fake_receiver), str(log_dir), str(repo)],
                cwd=repo,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                bufsize=0,
                start_new_session=True,
            )
            assert process.stdout is not None
            transcript: list[str] = []
            # Event-driven readiness wait: returns as soon as the launcher reports
            # WAITING; the bound only guards against a hung start on a loaded host.
            deadline = time.monotonic() + 30
            try:
                while time.monotonic() < deadline:
                    ready, _, _ = select.select([process.stdout], [], [], 0.2)
                    if ready:
                        chunk = os.read(process.stdout.fileno(), 4096).decode("utf-8", errors="replace")
                        transcript.append(chunk)
                        if "[PHONE_SABER][WAITING]" in "".join(transcript):
                            break
                    if process.poll() is not None:
                        break
                self.assertTrue(child_pid_file.exists(), "fake receiver child did not start")
                self.assertTrue(
                    "[PHONE_SABER][WAITING]" in "".join(transcript),
                    "".join(transcript),
                )
                os.kill(process.pid, signal.SIGINT)
                # Safety bound only. Correct shutdown returns within ~1 s, but the
                # launcher's own escalation path may legitimately take
                # 10 s (SIGINT wait) + 3 s (SIGTERM wait) + 2 s (group cleanup), so
                # the old 12 s bound was shorter than the code under test. The
                # assertions below still fail if shutdown needed SIGTERM/SIGKILL
                # (the receiver's finally block would not write stopped.txt).
                tail, _ = process.communicate(timeout=40)
                transcript.append(tail.decode("utf-8", errors="replace"))
                self.assertEqual(process.returncode, 130, "\n".join(transcript))
                self.assertTrue(stopped_file.exists(), "receiver did not run its shutdown path")
                child_pid = int(child_pid_file.read_text(encoding="utf-8"))
                try:
                    os.kill(child_pid, 0)
                except ProcessLookupError:
                    pass
                else:
                    child_state = subprocess.run(
                        ["ps", "-o", "stat=", "-p", str(child_pid)],
                        check=False, capture_output=True, text=True,
                    ).stdout.strip()
                    self.assertTrue(child_state.startswith("Z"), f"child process is still running: {child_state}")
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=3)
                if child_pid_file.exists():
                    try:
                        os.kill(int(child_pid_file.read_text(encoding="utf-8")), signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                if process.stdout is not None:
                    process.stdout.close()


if __name__ == "__main__":
    unittest.main()
