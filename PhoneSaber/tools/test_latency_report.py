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

    def test_line_cut_mid_write_does_not_create_a_fake_condition_or_session(self):
        cut = SAMPLE[1][:SAMPLE[1].index("route=") + len("route=P2P")]  # crash while writing
        blocks = latency_report.parse([SAMPLE[0], cut, SAMPLE[2]])
        self.assertEqual([b["block"] for b in blocks], [0, 2])
        self.assertEqual([b["session"] for b in blocks], [1, 1])

    def test_crlf_and_non_utf8_bytes_are_read(self):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "events.log"
            log.write_bytes(b"\xff\xfe broken\r\n" + SAMPLE[0].encode() + b"\r\n")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(latency_report.main([str(log)]), 0)
            self.assertIn("OSXEditor", output.getvalue())

    def run_since(self, since):
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "events.log"
            log.write_text("\n".join(SAMPLE) + "\n", encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output), contextlib.redirect_stderr(io.StringIO()):
                try:
                    code = latency_report.main([str(log), "--since", since])
                except SystemExit as exit:
                    code = exit.code
            return code, output.getvalue().replace(str(log), "events.log")

    def test_since_compares_times_not_strings_and_accepts_utc_offsets(self):
        # Log times are UTC (…Z). 10:00:30+09:00 == 01:00:30Z: keeps only blocks 2 and 3.
        code, output = self.run_since("2026-10-08T10:00:30+09:00")
        self.assertEqual(code, 0)
        self.assertIn("     30       1", output)
        self.assertNotIn("     60", output)
        self.assertEqual(self.run_since("2026-10-08T01:00:30Z")[1], output)
        self.assertEqual(self.run_since("2026-10-08T01:00:30")[1], output, "no offset means UTC")
        self.assertEqual(self.run_since("2026-10-08")[0], 0)

    def test_unreadable_since_or_missing_log_is_an_error_not_an_empty_report(self):
        self.assertEqual(self.run_since("2026/10/08 10:00")[0], 2)
        with contextlib.redirect_stderr(io.StringIO()) as error:
            self.assertEqual(latency_report.main(["/nonexistent/縁日 log/events.log"]), 2)
        self.assertIn("を読めません", error.getvalue())

    def test_120fps_is_not_mislabeled_as_60_and_transition_blocks_are_excluded(self):
        lines = [SAMPLE[0], SAMPLE[1].replace("red-pkt-per-s=59.0", "red-pkt-per-s=119.5"),
                 SAMPLE[2].replace("red-pkt-per-s=29.5", "red-pkt-per-s=90.0")]
        rows = latency_report.summarize(latency_report.parse(lines))
        self.assertEqual([(r["fps"], r["median"]) for r in rows], [(60, 100), (120, 110)])
        for rate in [73, 90, 104.9, 133, float("nan"), float("inf")]:
            self.assertIsNone(latency_report.phone_fps(rate))
        self.assertEqual(latency_report.phone_fps(105), 120)
        self.assertEqual(latency_report.phone_fps(132), 120)

    def test_sender_metrics_identify_overloaded_120_and_exclude_switching_blocks(self):
        metrics = latency_report.parse_sender_metrics([
            "[CAPTURE_METRICS] 2026-10-08T01:00:00Z station=A requested-fps=120 capture-fps=120",
            "[CAPTURE_METRICS] 2026-10-08T01:00:30Z station=A requested-fps=120 capture-fps=30",
            "[CAPTURE_METRICS] 2026-10-08T01:00:50Z station=B requested-fps=60 capture-fps=60",
        ])
        rows = latency_report.summarize(latency_report.parse(SAMPLE), sender_metrics=metrics)
        # block 1 の受信は59/sでも、設定は120。block 2 は熱降格をまたぐので除外。
        self.assertEqual([(r["fps"], r["median"]) for r in rows], [(30, 999), (120, 110)])
        self.assertEqual(latency_report.summarize(latency_report.parse(SAMPLE), sender_metrics=[]), [])
        self.assertEqual(latency_report.parse_sender_metrics([
            "[CAPTURE_METRICS] bad-time station=A requested-fps=120 capture-fps=120"]), [])

    def test_sender_metrics_cli_does_not_fall_back_to_packet_rate(self):
        with tempfile.TemporaryDirectory() as directory:
            log, metrics = Path(directory) / "events.log", Path(directory) / "sender.log"
            log.write_text("\n".join(SAMPLE), encoding="utf-8")
            metrics.write_text("[CAPTURE_METRICS] 2026-10-08T01:00:00Z station=A requested-fps=120 capture-fps=120", encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(latency_report.main([str(log), "--sender-metrics", str(metrics)]), 0)
            self.assertIn("fps-source=sender-metrics", output.getvalue())
            self.assertIn("    120", output.getvalue())

    def test_release_connectivity_log_records_identify_capture_mode(self):
        modes = latency_report.parse_sender_metrics([
            "2026-10-13T01:00:00.123Z [CAPTURE_MODE] station=A requested-fps=120 capture-fps=120 low-latency=false format=640x480 BGRA portrait",
            "2026-10-13T01:00:10.123Z [CAPTURE_METRICS] route=LAN capture-mode-fps=120 capture-fps=58.0 processed-fps=57.0",
        ])
        self.assertEqual(len(modes), 1)
        self.assertEqual((modes[0]["station"], modes[0]["fps"]), ("A", 120))

    def test_phone_fps_buckets(self):
        self.assertEqual(latency_report.phone_fps(58), 60)
        self.assertEqual(latency_report.phone_fps(30), 30)
        self.assertIsNone(latency_report.phone_fps(44))


class AutoEstimateReportTests(unittest.TestCase):
    def line(self, colour="RED", station="A", fps=60, total=54, phone=37, pc=7):
        return (f"2026-10-13T01:00:00Z station={station} colour={colour} event=latency-estimate "
                f"status=estimate platform=ios fps-mode={fps} route=LAN c2s-end=detection "
                f"c2s-median-ms={phone} network-ms=2 pc-median-ms={pc} display-ms=8 total-ms={total} "
                "c2s-n=100 pc-n=100 rtt-n=8 phone-age-s=1 rtt-age-s=1 thermal=nominal")

    def test_estimates_separate_station_colour_fps_and_summarize_terms(self):
        lines = [self.line(), self.line(total=64, phone=47), self.line(colour="BLUE"),
                 self.line(station="B"), self.line(fps=30)]
        rows = latency_report.summarize_estimates(latency_report.parse_estimates(lines))
        self.assertEqual(len(rows), 4)
        red = next(row for row in rows if row["station"] == "A" and row["colour"] == "RED" and row["fps"] == 60)
        self.assertEqual(red["terms"]["total-ms"], (59, 64))
        self.assertEqual(red["terms"]["c2s-median-ms"], (42, 47))

    def test_missing_stale_nan_negative_and_truncated_are_excluded(self):
        good = self.line()
        lines = [good.replace("status=estimate", "status=missing-rtt"),
                 good.replace("total-ms=54", "total-ms=NaN"),
                 good.replace("total-ms=54", "total-ms=-1"),
                 good.replace("phone-age-s=1", "phone-age-s=16"),
                 good.replace("rtt-age-s=1", "rtt-age-s=11"),
                 good.replace(" c2s-n=100", " c2s-n=0"), good.split("total-ms")[0]]
        self.assertEqual(latency_report.parse_estimates(lines), [])

    def test_estimates_cli_needs_only_pc_log_and_handles_rotation(self):
        with tempfile.TemporaryDirectory() as directory:
            first, second = Path(directory) / "events.log", Path(directory) / "events.log.1"
            first.write_text(self.line(), encoding="utf-8")
            second.write_text(self.line(total=64), encoding="utf-8")
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(latency_report.main([str(first), str(second), "--estimates"]), 0)
            self.assertIn("total-ms", output.getvalue())
            self.assertIn("59.0/64.0", output.getvalue())


if __name__ == "__main__":
    unittest.main()
