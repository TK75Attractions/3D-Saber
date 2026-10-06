"""phone_saber_status.py のテスト。subprocess・HTTP・ファイルはすべて偽物で、実際の network や process は使わない。"""

from __future__ import annotations

import contextlib
import io
import json
import os
import tempfile
import time
import unittest
from pathlib import Path
from unittest import mock

import phone_saber_status as status
from phone_saber_status import CommandResult


LSOF_HEADER = "COMMAND   PID    USER   FD   TYPE  DEVICE SIZE/OFF NODE NAME\n"


class FakeRunner:
    """args の先頭部分(文字列の連結)で応答を引く。未登録の command は None(実行できなかった扱い)。"""

    def __init__(self, responses: dict[str, CommandResult | None] | None = None):
        self.responses = responses or {}
        self.calls: list[list[str]] = []

    def __call__(self, args, timeout):
        self.calls.append(list(args))
        assert timeout > 0
        joined = " ".join(str(a) for a in args)
        for prefix, response in self.responses.items():
            if joined.startswith(prefix):
                return response
        return None


def ok(stdout: str = "") -> CommandResult:
    return CommandResult(0, stdout)


class StatusTestCase(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.home = self.root / "home"
        self.inbox = self.home / "inbox"
        self.inbox.mkdir(parents=True)
        self.unity = self.root / "3D-Saber"
        self.repo = self.unity / "PhoneSaber"
        self.repo.mkdir(parents=True)
        (self.unity / ".git").mkdir(parents=True)
        self.now = time.time()

    def ctx(self, runner=None, http=None, which=None) -> status.Context:
        return status.Context(
            home=self.home, repo_root=self.repo, unity_root=self.unity, inbox=self.inbox,
            runner=runner or FakeRunner(), http_get=http or (lambda url, timeout: None),
            which=which or (lambda name: None), now=lambda: self.now,
        )

    def write_editor_log(self, text: str) -> Path:
        path = self.home / "Library" / "Logs" / "Unity" / "Editor.log"
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")
        return path

    def write_receiver_log(self, name: str, text: str, link_latest: bool = False) -> Path:
        directory = self.home / "Library" / "Logs" / "PhoneSaber"
        directory.mkdir(parents=True, exist_ok=True)
        path = directory / name
        path.write_text(text, encoding="utf-8")
        if link_latest:
            latest = directory / "latest.log"
            if latest.exists() or latest.is_symlink():
                latest.unlink()
            latest.symlink_to(path)
        return path


class HelperTests(StatusTestCase):
    def test_parse_etime(self):
        self.assertEqual(status.parse_etime("05:07"), 307)
        self.assertEqual(status.parse_etime("02:00:01"), 7201)
        self.assertEqual(status.parse_etime("1-00:00:00"), 86400)
        self.assertIsNone(status.parse_etime("garbage"))

    def test_parse_lsof_skips_header(self):
        rows = status.parse_lsof(LSOF_HEADER + "Unity   4242 me 80u IPv4 0x1 0t0 UDP *:5005\n")
        self.assertEqual(rows, [{"command": "Unity", "pid": 4242, "name": "*:5005"}])

    def test_parse_stats(self):
        values = status.parse_stats("BLUE=313 RED=310 maxGapMsRED=121 maxGapMsBLUE=95 peers=1")
        self.assertEqual(values["RED"], 310)
        self.assertEqual(values["maxGapMsBLUE"], 95)

    def test_read_tail_drops_partial_first_line(self):
        path = self.root / "log.txt"
        path.write_text("aaaa\nbbbb\ncccc\n", encoding="utf-8")
        self.assertEqual(status.read_tail(path, 7), "cccc\n")
        self.assertIsNone(status.read_tail(self.root / "missing", 10))


class ReceiverTests(StatusTestCase):
    def test_ready_receiver_is_ok(self):
        body = json.dumps({"status": "ready", "service": "phonesaber-triage"})
        checks = status.check_receiver(self.ctx(http=lambda url, timeout: (200, body)))
        self.assertEqual(checks[0].level, "OK")

    def test_missing_receiver_is_ng(self):
        checks = status.check_receiver(self.ctx(runner=FakeRunner({"lsof": ok(LSOF_HEADER)})))
        self.assertEqual(checks[0].level, "NG")
        self.assertIn("Start PhoneSaber", checks[0].hints[0])

    def test_port_held_without_health_is_ng(self):
        runner = FakeRunner({"lsof": ok(LSOF_HEADER + "nc 77 me 3u IPv4 0x1 0t0 TCP *:8765 (LISTEN)\n")})
        checks = status.check_receiver(self.ctx(runner=runner))
        self.assertEqual(checks[0].level, "NG")
        self.assertIn("nc(pid 77)", checks[0].detail)

    def test_receiver_older_than_head_warns(self):
        body = json.dumps({"status": "ready", "service": "phonesaber-triage"})
        runner = FakeRunner({
            "lsof": ok(LSOF_HEADER + "python3 15 me 3u IPv4 0x1 0t0 TCP *:8765 (LISTEN)\n"),
            "ps -o etime=": ok("02:00:00\n"),
            f"git -C {self.repo} log": ok(f"{int(self.now) - 600}\n"),
        })
        checks = status.check_receiver(self.ctx(runner=runner, http=lambda url, timeout: (200, body)))
        self.assertEqual([c.level for c in checks], ["OK", "WARN"])

    def test_health_reported_stale_code_wins_over_head_heuristic(self):
        body = json.dumps({"status": "ready", "service": "phonesaber-triage", "pid": 15,
                           "startedAt": "2026-10-03T14:51:41+09:00", "codeChangedSinceStart": True,
                           "changedFiles": ["phone_saber_triage_codex.py"], "idle": False,
                           "analysis": {"mode": "automatic", "running": "phone_saber_triage_x", "queued": 1},
                           "uploadsInProgress": 0})
        runner = FakeRunner({"lsof": ok(LSOF_HEADER + "python3 15 me 3u IPv4 0x1 0t0 TCP *:8765 (LISTEN)\n")})
        checks = status.check_receiver(self.ctx(runner=runner, http=lambda url, timeout: (200, body)))
        self.assertEqual([c.level for c in checks], ["OK", "WARN", "INFO"])
        self.assertIn("phone_saber_triage_codex.py", checks[1].detail)
        self.assertIn("終わってから", checks[1].hints[0])
        self.assertIn("phone_saber_triage_x", checks[2].detail)
        self.assertNotIn(["ps"], [call[:1] for call in runner.calls])  # no HEAD-time heuristic needed

    def test_health_reported_current_code_is_quiet(self):
        body = json.dumps({"status": "ready", "service": "phonesaber-triage", "pid": 15,
                           "codeChangedSinceStart": False, "changedFiles": [], "idle": True,
                           "analysis": {"mode": "automatic", "running": None, "queued": 0},
                           "uploadsInProgress": 0})
        checks = status.check_receiver(self.ctx(http=lambda url, timeout: (200, body)))
        self.assertEqual([c.level for c in checks], ["OK"])


class UnityPortTests(StatusTestCase):
    def test_both_ports_bound_by_unity(self):
        runner = FakeRunner({
            "pgrep -x Unity": ok("4242\n"),
            "lsof": ok(LSOF_HEADER + "Unity 4242 me 80u IPv4 0x1 0t0 UDP *:5005\n"
                       "Unity 4242 me 81u IPv4 0x2 0t0 UDP *:5006\n"),
        })
        self.assertEqual([c.level for c in status.check_unity_ports(self.ctx(runner=runner))], ["OK", "OK"])

    def test_port_conflict_is_ng(self):
        runner = FakeRunner({
            "pgrep -x Unity": ok("4242\n"),
            "lsof": ok(LSOF_HEADER + "Python 99 me 3u IPv4 0x1 0t0 UDP *:5005\n"
                       "Unity 4242 me 81u IPv4 0x2 0t0 UDP *:5006\n"),
        })
        checks = status.check_unity_ports(self.ctx(runner=runner))
        self.assertEqual(checks[0].level, "NG")
        self.assertIn("Python(pid 99)", checks[0].detail)
        self.assertEqual(checks[1].level, "OK")

    def test_unity_not_playing(self):
        runner = FakeRunner({"pgrep -x Unity": ok("4242\n"), "lsof": CommandResult(1, "")})
        checks = status.check_unity_ports(self.ctx(runner=runner))
        self.assertTrue(all(c.level == "NG" and "Play" in c.detail for c in checks))

    def test_lsof_unavailable_is_warn(self):
        checks = status.check_unity_ports(self.ctx(runner=FakeRunner({"pgrep": ok("")})))
        self.assertEqual(checks[0].level, "WARN")


class BridgeTests(StatusTestCase):
    def test_running_bridge(self):
        runner = FakeRunner({"pgrep -fl": ok("555 /Users/me/Library/Caches/PhoneSaber/p2p-bridge/x/PhoneSaberP2PBridge --name a\n")})
        checks = status.check_bridge_process(self.ctx(runner=runner))
        self.assertEqual(checks[0].level, "OK")
        self.assertIn("555", checks[0].detail)

    def test_building_bridge(self):
        runner = FakeRunner({"pgrep -fl": ok("556 swiftc PhoneSaberP2PBridge.swift\n")})
        self.assertIn("build", status.check_bridge_process(self.ctx(runner=runner))[0].detail)

    def test_no_bridge(self):
        checks = status.check_bridge_process(self.ctx(runner=FakeRunner({"pgrep -fl": CommandResult(1, "")})))
        self.assertEqual(checks[0].level, "WARN")


EDITOR_LOG = """\
[PhoneSaber][P2P] bridge starting (first run may build): "Phone Saber Unity P2P (Mac)" RED 5005 / BLUE 5006
[PhoneSaber][P2P] last 10s: BLUE=1 RED=1 maxGapMsRED=900 maxGapMsBLUE=900 peers=1
[PhoneSaber][P2P] bridge stopped
[PhoneSaber][P2P] bridge starting (first run may build): "Phone Saber Unity P2P (Mac)" RED 5005 / BLUE 5006
[PhoneSaber][P2P] listening on UDP 61000 (_phonesaber-p2p._udp, peer-to-peer enabled)
[PhoneSaber][P2P] peer connected fe80::1%awdl0.5000 (peers: 1)
UnityEngine.Debug:LogFormat (stack trace line)
[PhoneSaber][P2P] last 10s: BLUE=313 RED=313 maxGapMsRED=121 maxGapMsBLUE={gap} peers=1
"""


class EditorLogTests(StatusTestCase):
    def test_only_current_play_session_is_used(self):
        self.write_editor_log(EDITOR_LOG.format(gap=95))
        checks = status.check_editor_log(self.ctx(), bridge_running=True)
        stats = checks[0]
        self.assertEqual(stats.level, "OK")
        self.assertIn("RED=313", stats.detail)
        self.assertIn("直近1回", stats.detail)  # 前の Play の 900 ms は数えない
        self.assertEqual(checks[1].title, "P2P 最後の接続イベント")
        self.assertIn("iPhone が接続", checks[1].detail)

    def test_gap_thresholds(self):
        for gap, expected in ((149, "OK"), (150, "WARN"), (499, "WARN"), (500, "NG"), (1800, "NG")):
            self.write_editor_log(EDITOR_LOG.format(gap=gap))
            self.assertEqual(status.check_editor_log(self.ctx(), bridge_running=True)[0].level, expected, gap)

    def test_history_is_info_when_bridge_stopped(self):
        self.write_editor_log(EDITOR_LOG.format(gap=1800))
        checks = status.check_editor_log(self.ctx(), bridge_running=False)
        self.assertEqual(checks[0].level, "INFO")
        self.assertIn("過去の記録", checks[0].detail)

    def test_fallback_event_and_warnings(self):
        self.write_editor_log(EDITOR_LOG.format(gap=95)
                              + "[PhoneSaber][P2P] no ping for 3s; iPhone falls back to LAN (fallback to LAN)\n"
                              + "[PhoneSaber][RED] receiver failed: SocketException: Address already in use\n"
                              + "[PhoneSaber][P2P] diag relay: upload from x closed after 10 bytes (done)\n")
        checks = {c.title: c for c in status.check_editor_log(self.ctx(), bridge_running=True)}
        self.assertEqual(checks["P2P 最後の接続イベント"].level, "WARN")
        self.assertIn("LAN に戻る", checks["P2P 最後の接続イベント"].detail)
        self.assertIn("Address already in use", checks["Unity Console の警告(直近の Play 以降)"].detail)
        self.assertIn("closed after 10 bytes", checks["診断 relay(P2P 経由の bundle 転送)"].detail)

    def test_missing_log(self):
        checks = status.check_editor_log(self.ctx(), bridge_running=False)
        self.assertEqual([c.level for c in checks], ["INFO"])

    def test_no_stats_yet(self):
        self.write_editor_log("[PhoneSaber][P2P] bridge starting (first run may build)\n")
        self.assertIn("まだ無い", status.check_editor_log(self.ctx(), bridge_running=True)[0].detail)


class InboxTests(StatusTestCase):
    def make_bundle(self, session: str, mtime_offset: float = 0) -> Path:
        bundle = self.inbox / f"phone_saber_triage_{session}"
        bundle.mkdir()
        (bundle / "summary.json").write_text("{}", encoding="utf-8")
        os.utime(bundle, (self.now + mtime_offset, self.now + mtime_offset))
        return bundle

    def test_empty_inbox(self):
        self.assertEqual(status.check_inbox(self.ctx())[0].level, "INFO")

    def test_missing_inbox_is_tolerated(self):
        ctx = self.ctx()
        ctx.inbox = self.root / "nope"
        self.assertEqual(status.check_inbox(ctx)[0].level, "INFO")

    def test_completed_analysis_on_latest_bundle(self):
        self.make_bundle("old", -100)
        bundle = self.make_bundle("new")
        (self.inbox / f"{bundle.name}.report.md").write_text("# r", encoding="utf-8")
        (bundle / "analysis_report.json").write_text(json.dumps({"analysisExecuted": True}), encoding="utf-8")
        checks = status.check_inbox(self.ctx())
        self.assertIn("phone_saber_triage_new", checks[0].detail)
        self.assertEqual([c.level for c in checks], ["OK", "OK"])

    def test_precheck_failure(self):
        bundle = self.make_bundle("s1")
        (bundle / "analysis_report.json").write_text(json.dumps({
            "analysisExecuted": False, "precheck": {"reasonCodes": ["temporalEvidenceMissing"]}}), encoding="utf-8")
        checks = status.check_inbox(self.ctx())
        self.assertEqual(checks[0].level, "WARN")  # .report.md が無い
        self.assertEqual(checks[1].level, "WARN")
        self.assertIn("temporalEvidenceMissing", checks[1].detail)

    def test_timeout_found_in_previous_receiver_run(self):
        self.make_bundle("s2")
        self.write_receiver_log("triage-20261003-145141.log",
                                "[AUTO_REPAIR][ANALYSIS] sessionID=s2 source=new_upload result=starting\n"
                                "[AUTO_REPAIR][ANALYSIS] sessionID=s2 source=new_upload elapsed=600.5s "
                                "subprocess=codex read-only result=FAIL CLI_TIMEOUT: Codex CLI exit_code=None\n")
        self.write_receiver_log("triage-20261003-162835.log", "[PHONE_SABER][WAITING]\n", link_latest=True)
        checks = status.check_inbox(self.ctx())
        self.assertEqual(checks[1].level, "WARN")
        self.assertIn("CLI_TIMEOUT", checks[1].detail)
        self.assertIn("phone_saber_triage_codex.py", checks[1].hints[-1])

    def test_precheck_failure_in_log_is_explained_in_japanese(self):
        self.make_bundle("s4")
        self.write_receiver_log("triage-1.log",
                                "[AUTO_REPAIR][ANALYSIS] sessionID=s4 source=new_upload elapsed=0.0s "
                                "subprocess=codex read-only result=FAIL PRECHECK_FAILED frameMappingMissing: "
                                "frame context exceeds its size limit: frames/a.json is 41350 bytes > 32768\n",
                                link_latest=True)
        checks = status.check_inbox(self.ctx())
        self.assertEqual(checks[1].level, "WARN")
        self.assertIn("32KB", checks[1].hints[0])

    def test_storage_reports_unanalysed_bundles_and_codex_logs(self):
        done = self.make_bundle("done", -10)
        (done / "analysis_report.json").write_text("{}", encoding="utf-8")
        self.make_bundle("a_pending", -5)
        self.make_bundle("b_pending")
        logs = self.home / "Library" / "Logs" / "PhoneSaber" / "codex"
        logs.mkdir(parents=True)
        for index in range(3):
            (logs / f"{index}.json").write_text("{}", encoding="utf-8")
        before = sorted(p.relative_to(self.home) for p in self.home.rglob("*"))
        checks = status.check_storage(self.ctx())
        self.assertEqual(checks[0].level, "INFO")
        self.assertIn("inbox 3 bundle", checks[0].detail)
        self.assertIn("Codex 記録 3 件", checks[0].detail)
        self.assertIn("解析結果なし 2 件", checks[0].detail)
        self.assertIn("b_pending, a_pending", checks[0].hints[0])
        self.assertEqual(before, sorted(p.relative_to(self.home) for p in self.home.rglob("*")))

    def test_analysis_running(self):
        self.make_bundle("s3")
        self.write_receiver_log("triage-1.log", "[AUTO_REPAIR][ANALYSIS] sessionID=s3 result=starting\n",
                                link_latest=True)
        self.assertIn("実行中", status.check_inbox(self.ctx())[1].detail)


class GitAndCodexTests(StatusTestCase):
    def test_clean_synchronized_main(self):
        runner = FakeRunner({
            f"git -C {self.unity} rev-parse --abbrev-ref": ok("main\n"),
            f"git -C {self.unity} status": ok(""),
            f"git -C {self.unity} rev-list": ok("0\t0\n"),
        })
        check = status.git_state(self.ctx(runner=runner), "3D-Saber", self.unity)
        self.assertEqual(check.level, "OK")

    def test_behind_and_dirty_without_upstream(self):
        runner = FakeRunner({
            f"git -C {self.unity} rev-parse --abbrev-ref": ok("main\n"),
            f"git -C {self.unity} status": ok(" M Assets/a.cs\n"),
            f"git -C {self.unity} rev-list --left-right --count HEAD...@{{upstream}}": CommandResult(128, ""),
            f"git -C {self.unity} rev-list --left-right --count HEAD...origin/main": ok("0\t5\n"),
        })
        check = status.git_state(self.ctx(runner=runner), "3D-Saber", self.unity)
        self.assertEqual(check.level, "WARN")
        self.assertIn("origin/main と比べて 0 ahead / 5 behind", check.detail)
        self.assertEqual(len(check.hints), 2)

    def test_missing_repository(self):
        self.assertEqual(status.git_state(self.ctx(), "3D-Saber", self.root / "absent").level, "INFO")

    def test_git_commands_are_read_only(self):
        runner = FakeRunner({"git": ok("main\n")})
        status.git_state(self.ctx(runner=runner), "3D-Saber", self.unity)
        subcommands = {call[3] for call in runner.calls}
        self.assertTrue(subcommands <= {"rev-parse", "status", "rev-list"}, subcommands)

    def test_codex_found(self):
        runner = FakeRunner({"/x/codex --version": ok("codex-cli 9.9\n")})
        checks = status.check_codex(self.ctx(runner=runner, which=lambda name: "/x/codex"))
        self.assertEqual(checks[0].level, "OK")
        self.assertIn("codex-cli 9.9", checks[0].detail)

    def test_codex_missing(self):
        with mock.patch.object(Path, "is_file", return_value=False):
            checks = status.check_codex(self.ctx())
        self.assertEqual(checks[0].level, "WARN")


class EndToEndTests(StatusTestCase):
    def test_collect_tolerates_everything_missing(self):
        """何も無い Mac(command も実行できない)でも例外を出さずに結果を返す。"""
        with mock.patch.object(Path, "is_file", return_value=False):
            checks = status.collect(self.ctx())
        self.assertTrue(checks)
        self.assertIn(status.exit_code(checks), (1, 2))
        text = status.render(checks, self.now)
        self.assertIn("まとめ: NG", text)
        self.assertIn("読み取りのみ", text)

    def test_deadline_stops_further_commands(self):
        runner = FakeRunner({"": ok("")})
        ctx = self.ctx(runner=runner)
        ctx.deadline = time.monotonic() - 1
        self.assertIsNone(ctx.run(["pgrep", "x"]))
        self.assertEqual(runner.calls, [])

    def test_exit_codes(self):
        self.assertEqual(status.exit_code([status.Check("OK", "a")]), 0)
        self.assertEqual(status.exit_code([status.Check("OK", "a"), status.Check("WARN", "b")]), 1)
        self.assertEqual(status.exit_code([status.Check("NG", "a"), status.Check("WARN", "b")]), 2)

    def test_main_prints_report(self):
        fake = [status.Check("OK", "受信側", "起動中")]
        with mock.patch.object(status, "collect", return_value=fake), \
                mock.patch.object(status, "resolve_unity_root", return_value=None), \
                contextlib.redirect_stdout(io.StringIO()) as out:
            code = status.main(["--inbox", str(self.inbox)])
        self.assertEqual(code, 0)
        self.assertIn("[OK  ] 受信側: 起動中", out.getvalue())


if __name__ == "__main__":
    unittest.main()
