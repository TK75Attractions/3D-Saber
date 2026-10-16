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


class AutoLatencyPlayerLogTests(unittest.TestCase):
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
                        mock.patch.object(check.subprocess, "Popen", return_value=player), \
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
                        mock.patch.object(check.subprocess, "Popen", return_value=player), \
                        mock.patch.object(check.socket, "socket", return_value=sender), \
                        mock.patch.object(check.time, "sleep"), \
                        mock.patch("sys.argv", ["check"]), contextlib.redirect_stdout(io.StringIO()) as output:
                    self.assertEqual(check.main(), 0, output.getvalue())
                    self.assertIn("latency-estimate 1 行", output.getvalue(), "以前の実行結果は数えない")


if __name__ == "__main__":
    unittest.main()
