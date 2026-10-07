import unittest

import latency_report


SAMPLE = [
    "2026-10-08T01:00:00.000Z station=A colour=RED event=latency-block block=0 max-queued-frames=1 median-ms=100 p95-ms=130 n=15 red-pkt-per-s=58.2 route=P2P-bridge platform=OSXEditor editor=True rejected=0 misses=0",
    "2026-10-08T01:00:20.000Z station=A colour=RED event=latency-block block=1 max-queued-frames=2 median-ms=110 p95-ms=150 n=15 red-pkt-per-s=59.0 route=P2P-bridge platform=OSXEditor editor=True rejected=0 misses=0",
    "2026-10-08T01:00:40.000Z station=A colour=RED event=latency-block block=2 max-queued-frames=1 median-ms=120 p95-ms=140 n=15 red-pkt-per-s=29.5 route=P2P-bridge platform=OSXEditor editor=True rejected=1 misses=0",
    "2026-10-08T01:01:00.000Z station=A colour=RED event=latency-block block=3 max-queued-frames=2 median-ms=999 p95-ms=999 n=15 red-pkt-per-s=44.0 route=P2P-bridge platform=OSXEditor editor=True rejected=0 misses=0",
    "2026-10-08T01:01:10.000Z station=A colour=RED event=latency-loop median-ms=1 p95-ms=1",
]


class LatencyReportTests(unittest.TestCase):
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
