import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

import phone_saber_peer_log_report as report


def peer(event, detail="", at=0, station="B", colour="SYSTEM", sender=None):
    stamp = f"2026-10-29T01:00:{at:02d}.000Z"
    return (f"2026-10-29T03:00:00.000Z station=A colour=PEER event=peer-log from-station={sender or station} "
            f"{stamp} station={station} colour={colour} event={event} {detail}\n")


class PeerLogReportTests(unittest.TestCase):
    def test_song_latency_packet_rate_and_interruptions_use_source_time(self):
        rows = [
            peer("peer-context", "scene=Game song=song-1", 1),
            peer("perf", "scene=Game fps=28 red-pkt-per-s=24 blue-pkt-per-s=20 frame-ms-max=140", 2),
            peer("latency-estimate", "total-ms=180 pc-median-ms=40", 3, colour="RED"),
            peer("latency-estimate", "total-ms=220 pc-median-ms=60", 4, colour="RED"),
            peer("latency-estimate", "total-ms=unknown", 5, colour="BLUE"),
            peer("input-gap", "gap-seconds=0.5", 6), peer("input-silent", at=7),
            peer("input-back", at=8), peer("sender-change", at=9), peer("hitch", "interval-ms=180", 10),
            peer("judge-latency", "applied-ms=90", 11), peer("judge-timing", "judge-ms=-12", 12),
            peer("peer-context", "scene=Result song=song-1", 13),
            peer("peer-context", "scene=Game song=song-2", 14),
            peer("latency-estimate", "total-ms=100", 15, colour="BLUE"),
        ]
        result = report.summarize(reversed(rows))
        first = result["songs"][0]
        self.assertEqual(first["song"], "song-1")
        self.assertEqual(first["start"], "2026-10-29T01:00:01.000Z")
        self.assertEqual(first["metrics"]["RED.total-ms"], {"n": 2, "median": 200, "max": 220})
        self.assertEqual(first["metrics"]["red-pkt-per-s"]["median"], 24)
        self.assertEqual(first["metrics"]["gap-seconds"]["max"], .5)
        self.assertEqual(first["metrics"]["judge-error-ms"]["median"], -12)
        self.assertEqual(list(first["interruptions"].values()), [1, 1, 1, 1, 1])
        self.assertNotIn("BLUE.total-ms", first["metrics"])
        self.assertEqual(result["songs"][-1]["song"], "song-2")
        self.assertIn("200 / 不明", report.markdown(result))

    def test_filters_local_other_station_mismatch_and_duplicate(self):
        good = peer("perf", "fps=30")
        rows = [good, good.replace("03:00:00", "04:00:00"), peer("perf", station="A"),
                peer("perf", station="C", sender="B"), "event=peer-log from-station=B garbage",
                "2026-10-29T01:00:00.000Z station=B colour=RED event=perf fps=20\n"]
        self.assertEqual(report.summarize(rows)["peer_rows"], 1)
        self.assertEqual(report.summarize(rows, "A")["peer_rows"], 1)
        self.assertEqual(report.summarize([])["songs"], [])

    def test_repeated_song_and_restart_are_separate_runs(self):
        rows = [peer("peer-context", "scene=Game song=%E6%9B%B2%201", 1),
                peer("perf", "fps=20", 2), peer("peer-context", "scene=Result song=x", 3),
                peer("peer-context", "scene=Game song=%E6%9B%B2%201", 4), peer("session-quit", at=5),
                peer("session-start", at=6), peer("peer-context", "scene=Game song=%E6%9B%B2%201", 7)]
        songs = [group for group in report.summarize(rows)["songs"] if group["song"] == "曲 1"]
        self.assertEqual([group["run"] for group in songs], [1, 2, 3])

    def test_cli_multiple_logs_json_unknown_and_invalid_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "events.log"
            path.write_text(peer("latency-estimate", "total-ms=nan pc-median-ms=inf", colour="RED"), encoding="utf-8")
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(report.main([str(path), str(path), "--json"]), 0)
            result = json.loads(out.getvalue())
            self.assertEqual(result["peer_rows"], 1)
            self.assertEqual(result["songs"][0]["metrics"], {})
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(report.main([str(path)]), 0)
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                report.main([str(path), "--station", "bad station"])
            with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
                report.main([str(path) + ".missing"])


if __name__ == "__main__":
    unittest.main()
