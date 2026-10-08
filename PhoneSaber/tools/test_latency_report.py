import unittest
import contextlib
import io
import tempfile
from pathlib import Path

import latency_report


SAMPLE = [
    "2026-10-08T01:00:00.000Z station=A colour=RED event=latency-block block=0 max-queued-frames=1 median-ms=100 p95-ms=130 n=15 red-pkt-per-s=58.2 route=P2P-bridge platform=OSXEditor editor=True rejected=0 misses=0",
    "2026-10-08T01:00:20.000Z station=A colour=RED event=latency-block block=1 max-queued-frames=2 median-ms=110 p95-ms=150 n=15 red-pkt-per-s=59.0 route=P2P-bridge platform=OSXEditor editor=True rejected=0 misses=0",
    "2026-10-08T01:00:40.000Z station=A colour=RED event=latency-block block=2 max-queued-frames=1 median-ms=120 p95-ms=140 n=15 red-pkt-per-s=29.5 route=P2P-bridge platform=OSXEditor editor=True rejected=1 misses=0",
    "2026-10-08T01:01:00.000Z station=A colour=RED event=latency-block block=3 max-queued-frames=2 median-ms=999 p95-ms=999 n=15 red-pkt-per-s=44.0 route=P2P-bridge platform=OSXEditor editor=True rejected=0 misses=0",
    "2026-10-08T01:01:10.000Z station=A colour=RED event=latency-loop median-ms=1 p95-ms=1",
]


class LatencyReportTests(unittest.TestCase):
    def test_restarted_measurements_do_not_average_together(self):
        restarted = SAMPLE[0].replace("01:00:00", "02:00:00").replace("median-ms=100", "median-ms=300")
        rows = latency_report.summarize(latency_report.parse([SAMPLE[0], restarted]))
        self.assertEqual([r["median"] for r in rows], [100.0, 300.0])
        self.assertEqual([r["session"] for r in rows], [1, 2])

    def test_interleaved_stations_keep_independent_block_sequences(self):
        other = SAMPLE[0].replace("station=A", "station=B").replace("median-ms=100", "median-ms=500")
        later = SAMPLE[0].replace("block=0", "block=2").replace("median-ms=100", "median-ms=120")
        rows = latency_report.summarize(latency_report.parse([SAMPLE[0], other, later]))
        self.assertEqual([(r["station"], r["blocks"], r["median"]) for r in rows],
                         [("A", 2, 110.0), ("B", 1, 500.0)])

    def test_end_record_splits_even_when_next_log_starts_mid_measurement(self):
        end = "2026-10-08T01:01:00Z station=A colour=RED event=latency-loop-end n=0 platform=OSXEditor"
        later = SAMPLE[0].replace("block=0", "block=3")
        blocks = latency_report.parse([SAMPLE[0], end, later])
        self.assertEqual([b["session"] for b in blocks], [1, 2])

    def test_truncated_log_continues_until_block_number_restarts(self):
        lines = [SAMPLE[0].replace("block=0", f"block={index}") for index in (5, 7, 0)]
        blocks = latency_report.parse(lines)
        self.assertEqual([b["session"] for b in blocks], [1, 1, 2])
        self.assertEqual(len(latency_report.summarize(blocks)), 2)

    def test_cli_keeps_files_separate_and_displays_measurement_identity(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "first.log", Path(directory) / "second.log"
            first.write_text(SAMPLE[0], encoding="utf-8")
            second.write_text(SAMPLE[0].replace("block=0", "block=1"), encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(latency_report.main([str(first), str(second)]), 0)
            self.assertIn(f"log={first} station=A session=1", output.getvalue())
            self.assertIn(f"log={second} station=A session=1", output.getvalue())

    def test_groups_by_condition_and_skips_mixed_fps_blocks(self):
        rows = latency_report.summarize(latency_report.parse(SAMPLE))
        keys = [(r["fps"], r["queued"], r["median"]) for r in rows]
        self.assertEqual(keys, [(30, 1, 120.0), (60, 1, 100.0), (60, 2, 110.0)])

    def test_phone_fps_buckets(self):
        self.assertEqual(latency_report.phone_fps(58), 60)
        self.assertEqual(latency_report.phone_fps(30), 30)
        self.assertIsNone(latency_report.phone_fps(44))


if __name__ == "__main__":
    unittest.main()
