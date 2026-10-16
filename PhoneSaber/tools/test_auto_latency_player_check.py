"""Player を起動せず、検証中の events.log 世代交代を再現する。"""
import contextlib
from datetime import datetime, timedelta, timezone
import errno
import io
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest import mock

import auto_latency_player_check as check


SUCCESS = "[PhoneSaberSmoke] done exceptions=0 errors=0 input=ok cuts=3 flow=ok\n"


def launch_player(player, log_text=SUCCESS):
    def launch(command):
        if log_text is not None:
            Path(command[command.index("-logFile") + 1]).write_text(log_text)
        return player
    return launch


class AutoLatencyPlayerLogTests(unittest.TestCase):
    def test_sender_setup_failure_does_not_launch_an_unmonitored_player(self):
        for stage in ("socket", "bind"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as tmp:
                sender = mock.Mock()
                error = (OSError(errno.EMFILE, "Too many open files") if stage == "socket"
                         else OSError(errno.EADDRNOTAVAIL, "Can't assign requested address"))
                if stage == "bind":
                    sender.bind.side_effect = error
                with mock.patch.object(check, "EVENTS", Path(tmp) / "events.log"), \
                        mock.patch.object(check, "lan_address", return_value="192.0.2.2"), \
                        mock.patch.object(check.subprocess, "run", return_value=SimpleNamespace(stdout=b"")), \
                        mock.patch.object(check.subprocess, "Popen") as popen, \
                        mock.patch.object(check.socket, "socket", side_effect=error if stage == "socket" else None,
                                          return_value=sender), \
                        mock.patch.dict(check.os.environ, {"TMPDIR": tmp}), \
                        mock.patch("sys.argv", ["check"]), contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(check.main(), 1)
                popen.assert_not_called()
                if stage == "bind":
                    sender.close.assert_called_once_with()

    def test_player_launch_failure_closes_the_prepared_sender(self):
        with tempfile.TemporaryDirectory() as tmp:
            sender = mock.Mock()
            with mock.patch.object(check, "EVENTS", Path(tmp) / "events.log"), \
                    mock.patch.object(check, "lan_address", return_value="192.0.2.2"), \
                    mock.patch.object(check.subprocess, "run", return_value=SimpleNamespace(stdout=b"")), \
                    mock.patch.object(check.subprocess, "Popen", side_effect=OSError("launch failed")), \
                    mock.patch.object(check.socket, "socket", return_value=sender), \
                    mock.patch.dict(check.os.environ, {"TMPDIR": tmp}), \
                    mock.patch("sys.argv", ["check"]), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(check.main(), 1)
            sender.bind.assert_called_once_with(("192.0.2.2", 0))
            sender.close.assert_called_once_with()

    def run_completed_check(self, tmp, log_text, exit_code=0):
        events = Path(tmp) / "events.log"
        player = mock.Mock()
        player.poll.return_value = exit_code

        def launch(command):
            events.write_text("event=latency-estimate sender=192.0.2.2 status=estimate c2s-median-ms=36.9\n")
            return launch_player(player, log_text)(command)

        with mock.patch.object(check, "EVENTS", events), \
                mock.patch.object(check, "lan_address", return_value="192.0.2.2"), \
                mock.patch.object(check.subprocess, "run", return_value=SimpleNamespace(stdout=b"")), \
                mock.patch.object(check.subprocess, "Popen", side_effect=launch) as popen, \
                mock.patch.object(check.socket, "socket") as socket_factory, \
                mock.patch.dict(check.os.environ, {"TMPDIR": tmp}), \
                mock.patch("sys.argv", ["check"]), contextlib.redirect_stdout(io.StringIO()):
            result = check.main()
        if popen.called:
            socket_factory.return_value.close.assert_called_once_with()
        return result, popen.called

    def test_estimate_before_abnormal_player_completion_is_not_success(self):
        for log_text, exit_code in ((None, 0), ("Player crashed\n", 0),
                                   (SUCCESS.replace("errors=0", "errors=1"), 0),
                                   (SUCCESS.replace("exceptions=0", "exceptions=1"), 0),
                                   (SUCCESS.replace("input=ok", "input=FAILED"), 0),
                                   (SUCCESS.replace("cuts=3", "cuts=0"), 0),
                                   (SUCCESS.replace("flow=ok", "flow=FAILED"), 0),
                                   (SUCCESS + SUCCESS, 0), (SUCCESS, 1)):
            with self.subTest(log_text=log_text, exit_code=exit_code), tempfile.TemporaryDirectory() as tmp:
                result, launched = self.run_completed_check(tmp, log_text, exit_code)
                self.assertTrue(launched)
                self.assertEqual(result, 1, "遅延推定が出ていても Player の異常終了を成功にしない")

    def test_old_success_log_is_not_reused_after_player_crash(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "phonesaber-auto-latency-check.log").write_text(SUCCESS)
            result, launched = self.run_completed_check(tmp, None)
            self.assertTrue(launched)
            self.assertEqual(result, 1)

    def test_log_cleanup_failure_stops_before_player_launch(self):
        with tempfile.TemporaryDirectory() as tmp:
            (Path(tmp) / "phonesaber-auto-latency-check.log").mkdir()
            result, launched = self.run_completed_check(tmp, None)
            self.assertEqual(result, 1)
            self.assertFalse(launched)

    def test_estimate_with_normal_player_completion_succeeds(self):
        with tempfile.TemporaryDirectory() as tmp:
            result, launched = self.run_completed_check(tmp, SUCCESS)
            self.assertTrue(launched)
            self.assertEqual(result, 0)

    def test_temporary_send_error_recovers_for_metrics_and_both_coordinates(self):
        for failed_port in (5007, 5005, 5006):
            with self.subTest(failed_port=failed_port), tempfile.TemporaryDirectory() as tmp:
                events = Path(tmp) / "events.log"
                player = mock.Mock()
                player.poll.side_effect = [None, None, 0, 0]
                sender = mock.Mock()
                failed = False

                def receive(data, destination):
                    nonlocal failed
                    if destination[1] == failed_port and not failed:
                        failed = True
                        raise OSError(errno.ENETUNREACH, "Network is unreachable")
                    if destination[1] == 5007:
                        events.write_text("event=latency-estimate sender=192.0.2.2 "
                                          "status=estimate c2s-median-ms=36.9\n")

                sender.sendto.side_effect = receive
                with mock.patch.object(check, "EVENTS", events), \
                        mock.patch.object(check, "lan_address", return_value="192.0.2.2"), \
                        mock.patch.object(check.subprocess, "run", return_value=SimpleNamespace(stdout=b"")) as run, \
                        mock.patch.object(check.subprocess, "Popen", side_effect=launch_player(player)), \
                        mock.patch.dict(check.os.environ, {"TMPDIR": tmp}), \
                        mock.patch.object(check.socket, "socket", return_value=sender), \
                        mock.patch.object(check.time, "monotonic", side_effect=[0, 0, 0, 5, 5]), \
                        mock.patch.object(check.time, "sleep"), \
                        mock.patch("sys.argv", ["check"]), contextlib.redirect_stdout(io.StringIO()) as output:
                    self.assertEqual(check.main(), 0, output.getvalue())
                self.assertTrue(failed)
                self.assertEqual([call.args[1][1] for call in sender.sendto.call_args_list[-3:]],
                                 [5007, 5005, 5006])
                self.assertEqual(player.poll.call_count, 4, "Player の終了まで監視を続ける")
                self.assertEqual(run.call_count, 1, "回復した Player を強制停止しない")

    def test_estimate_is_found_when_events_rotate_during_the_check(self):
        for rotate_after_estimate in (False, True):
            with self.subTest(rotate_after_estimate=rotate_after_estimate), tempfile.TemporaryDirectory() as tmp:
                events = Path(tmp) / "events.log"
                events.write_text("old log\n" * 500)
                old = "2000-01-01T00:00:00.000Z event=latency-estimate sender=192.0.2.2 status=estimate c2s-median-ms=36.9\n"
                events.with_suffix(".log.2").write_text(old)
                player = mock.Mock()
                player.poll.side_effect = [None, 0, 0]
                sender = mock.Mock()

                def receive(data, destination):
                    if destination[1] != 5007:
                        return
                    timestamp = (datetime.now(timezone.utc) + timedelta(seconds=1)).isoformat(timespec="milliseconds").replace("+00:00", "Z")
                    line = timestamp + " event=latency-estimate sender=192.0.2.2 status=estimate c2s-median-ms=36.9\n"
                    if rotate_after_estimate:
                        with events.open("a") as handle:
                            handle.write(line)
                        events.rename(events.with_suffix(".log.1"))
                        events.write_text(timestamp + " event=perf fps=60\n")
                    else:
                        events.rename(events.with_suffix(".log.1"))
                        events.write_text(line)

                sender.sendto.side_effect = receive
                with mock.patch.object(check, "EVENTS", events), \
                        mock.patch.object(check, "lan_address", return_value="192.0.2.2"), \
                        mock.patch.object(check.subprocess, "run", return_value=SimpleNamespace(stdout=b"")), \
                        mock.patch.object(check.subprocess, "Popen", side_effect=launch_player(player)), \
                        mock.patch.dict(check.os.environ, {"TMPDIR": tmp}), \
                        mock.patch.object(check.socket, "socket", return_value=sender), \
                        mock.patch.object(check.time, "sleep"), \
                        mock.patch("sys.argv", ["check"]), contextlib.redirect_stdout(io.StringIO()) as output:
                    self.assertEqual(check.main(), 0, output.getvalue())
                    self.assertIn("latency-estimate 1 行", output.getvalue(), "以前の実行結果は数えない")


if __name__ == "__main__":
    unittest.main()
