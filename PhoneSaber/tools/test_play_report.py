import contextlib
from datetime import datetime, timedelta, timezone
import io
import json
import os
import tempfile
import unittest

import play_report


BASE = datetime(2026, 10, 17, 7, 46, 0, tzinfo=timezone.utc)


def ts(seconds):
    return (BASE + timedelta(seconds=seconds)).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def perf(t, scene="Game", fps=119.7, over25=0, over50=0, fmax=11.0, red=30.0, blue=30.0):
    return (f"{ts(t)} station=A colour=SYSTEM event=perf scene={scene} fps={fps} frame-ms-max={fmax} "
            f"frames-over-25ms={over25} frames-over-50ms={over50} red-pkt-per-s={red} "
            f"blue-pkt-per-s={blue} screen=1920x1080 refresh-hz=60 vsync=0 target-fps=120")


def estimate(t, colour="RED", sender="192.168.1.20", fps="60.0", thermal="nominal", c2s=30.0,
             rtt=6.0, pc=5.0, total=60.0, exposure=8.0):
    return (f"{ts(t)} station=A colour={colour} event=latency-estimate status=estimate sender={sender} "
            f"platform=ios app=unknown fps-mode={fps} route=LAN c2s-end=detection c2s-median-ms={c2s} "
            f"c2s-p95-ms={c2s + 2} c2s-n=150 rtt-ms={rtt} rtt-n=16 rtt-age-s=1.0 network-ms={rtt / 2} "
            f"pc-median-ms={pc} pc-p95-ms={pc * 3} pc-n=90 display-ms=8.3 "
            f"display-source=configured-half-refresh total-ms={total} thermal={thermal} "
            f"exposure-phase-ms={exposure}")


def judge(t, tier, judge_ms, latency=120, hit=1.0):
    return (f"{ts(t)} station=A colour=SYSTEM event=judge-timing tier={tier} hand=Right "
            f"judge-ms={judge_ms} song-ms={judge_ms + latency} latency-ms={latency} hit-s={hit}")


def write_log(lines):
    handle = tempfile.NamedTemporaryFile("w", suffix=".log", delete=False, encoding="utf-8")
    handle.write("\n".join(lines) + "\n")
    handle.close()
    return handle.name


def run(argv):
    out = io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
        code = play_report.main(argv)
    return code, out.getvalue()


class ParsingTests(unittest.TestCase):
    def test_odd_lines_are_skipped(self):
        path = write_log([
            "",
            "garbage",
            "not-a-time event=perf scene=Game",
            f"{ts(0)} station=A colour=RED event=receiver-stop intentional shutdown",
            f"{ts(1)} station=A colour=SYSTEM event=perf scene=Game fps=unknown frame-ms-max=nan "
            "frames-over-25ms= red-pkt-per-s=inf",
            f"{ts(2)} station=A colour=SYSTEM event=judge-timing tier=Perfect judge-ms=oops",
            "\x00\x01 binary junk ===",
        ])
        try:
            events = play_report.load_events(path)
            self.assertEqual([e["event"] for e in events], ["receiver-stop", "perf", "judge-timing"])
            code, text = run(["--events", path, "--since", ts(0), "--until", ts(60)])
            self.assertEqual(code, 0)
            self.assertIn("全体", text)
        finally:
            os.unlink(path)

    def test_num_rejects_unknown_and_non_finite(self):
        for value in ("unknown", "", "nan", "inf", "-", None, "abc"):
            self.assertIsNone(play_report.num(value), value)
        self.assertEqual(play_report.num("-31"), -31.0)

    def test_time_args(self):
        now = datetime(2026, 10, 17, 16, 0, tzinfo=timezone.utc)
        self.assertEqual(play_report.parse_time_arg("2026-10-17T07:46Z"),
                         datetime(2026, 10, 17, 7, 46, tzinfo=timezone.utc))
        self.assertEqual(play_report.parse_time_arg("2026-10-17T07:46"),
                         datetime(2026, 10, 17, 7, 46, tzinfo=timezone.utc))
        self.assertEqual(play_report.parse_time_arg("07:46Z", now),
                         datetime(2026, 10, 17, 7, 46, tzinfo=timezone.utc))
        local = play_report.parse_time_arg("16:46", now)
        self.assertEqual(local.astimezone().strftime("%H:%M"), "16:46")
        with self.assertRaises(ValueError):
            play_report.parse_time_arg("25:xx")
        self.assertEqual(play_report.parse_window_spec("07:46Z..", now),
                         (datetime(2026, 10, 17, 7, 46, tzinfo=timezone.utc), None))


class SongSplitTests(unittest.TestCase):
    def test_perf_runs_and_judge_bursts_become_songs(self):
        lines = [perf(0, "SongSelect")]
        lines += [perf(t) for t in (10, 20, 30)]          # 曲 1: 0..40
        lines += [judge(t, "Perfect", 0, hit=t - 2) for t in (5, 15, 25)]
        lines += [perf(40, "Result"), perf(50, "SongSelect")]
        lines += [perf(t) for t in (60, 70)]               # 曲 2: 50..80
        lines += [perf(80, "Result")]
        lines += [judge(t, "Good", 10, hit=t - 200) for t in (300, 305, 310)]  # perf なしの曲 3
        lines += [judge(t, "Good", 10, hit=1 + (t - 312)) for t in (312, 314)]  # hit-s が巻き戻る → 曲 4
        events = [play_report.parse_line(l) for l in lines]
        events = sorted([e for e in events if e], key=lambda e: e["time"])
        songs = play_report.find_songs(events)
        self.assertEqual(len(songs), 4)
        self.assertEqual(songs[0], (BASE, BASE + timedelta(seconds=40)))
        self.assertEqual(songs[1], (BASE + timedelta(seconds=50), BASE + timedelta(seconds=80)))
        self.assertEqual(songs[2][0], BASE + timedelta(seconds=299))
        self.assertEqual(songs[3][1], BASE + timedelta(seconds=315))

    def test_report_counts_songs(self):
        lines = [perf(t) for t in (10, 20)] + [perf(30, "Result")] + [perf(t) for t in (100, 110)]
        path = write_log(lines)
        try:
            report = play_report.build_report(play_report.load_events(path), None, None)
            self.assertEqual(len(report["songs"]), 2)
        finally:
            os.unlink(path)


class SummaryTests(unittest.TestCase):
    def summary(self, lines):
        events = sorted([e for e in map(play_report.parse_line, lines) if e], key=lambda e: e["time"])
        return play_report.summarize(events, BASE, BASE + timedelta(seconds=120), "t")

    def test_best_compensation_math(self):
        lines = [judge(1, "Perfect", -40), judge(2, "Great", -30), judge(3, "Good", -20),
                 judge(4, "Miss", 300), judge(5, "Bad", 50)]
        s = self.summary(lines)
        j = s["judge"]
        self.assertEqual(j["n"], 5)
        self.assertEqual(j["hits"], 4)                       # Miss は当たりに入れない
        self.assertEqual(j["judge_ms_median"], -25.0)
        self.assertEqual(j["judge_ms_mean"], -10.0)
        self.assertEqual(j["early"], 3)
        self.assertEqual(j["late"], 1)
        self.assertEqual(j["compensation_ms"], 120.0)
        self.assertEqual(j["best_compensation_ms"], 95.0)    # 120 + (-25)
        self.assertEqual(j["song_ms_median"], 95.0)
        self.assertEqual(play_report.best_compensation(120, -31), 89)
        self.assertIsNone(play_report.best_compensation(None, -31))
        self.assertFalse(any("補正ずれ" in p for p in s["problems"]))   # |−25| は 25 以下

    def test_compensation_mismatch_flagged(self):
        s = self.summary([judge(t, "Perfect", -31) for t in range(1, 6)])
        self.assertTrue(any("補正ずれ" in p and "89" in p for p in s["problems"]))

    def test_tethering_warning(self):
        lines = [estimate(5, sender="172.20.10.1", fps="30.0", thermal="serious")]
        s = self.summary(lines)
        self.assertEqual(s["network"]["tethering_host"], ["172.20.10.1"])
        self.assertTrue(any("インターネット共有" in p for p in s["problems"]))
        self.assertTrue(any("30fps" in p for p in s["problems"]))
        self.assertTrue(any("serious" in p for p in s["problems"]))
        text = play_report.render(s)
        self.assertIn("警告", text)

        normal = self.summary([estimate(5)])
        self.assertEqual(normal["network"]["tethering_host"], [])
        self.assertNotIn("警告", play_report.render(normal))
        self.assertEqual(normal["problems"], [])

    def test_duplicate_colour_estimates_count_once_and_contributions_rank(self):
        lines = [estimate(5, "RED"), estimate(5, "BLUE"), estimate(15, "RED", c2s=34.0)]
        s = self.summary(lines)
        self.assertEqual(s["phone"]["c2s_median_ms"]["n"], 2)
        names = [c["name"] for c in s["contributions"]]
        self.assertTrue(names[0].startswith("スマホ"))
        self.assertTrue(names[-1].startswith("ネットワーク"))

    def test_missing_phone_rows_are_ignored(self):
        line = (f"{ts(5)} station=A colour=RED event=latency-estimate status=missing-phone sender=unknown "
                "platform=unknown fps-mode=unknown c2s-median-ms=unknown rtt-ms=unknown pc-median-ms=unknown "
                "display-ms=8.3 total-ms=unknown thermal=unknown")
        s = self.summary([line])
        self.assertEqual(s["phone"]["fps_mode"], {})
        self.assertEqual(s["contributions"], [])
        self.assertEqual(s["problems"], [])

    def test_input_gaps_and_frame_spikes(self):
        lines = [
            f"{ts(10)} station=A colour=RED event=input-silent gap-seconds=1.0",
            f"{ts(14)} station=A colour=RED event=input-back sender=1.2.3.4",
            f"{ts(20)} station=A colour=BLUE event=input-silent gap-seconds=1.0",
            f"{ts(25)} station=A colour=SYSTEM event=session-quit exit-code=0",
            perf(30, over25=4, over50=2, fmax=80.0),
        ]
        s = self.summary(lines)
        self.assertEqual(s["phone"]["input_gaps"]["RED"], {"count": 1, "seconds": 5.0})
        self.assertEqual(s["phone"]["input_gaps"]["BLUE"], {"count": 1, "seconds": 6.0})
        self.assertEqual(s["pc"]["frames_over_50ms"], 2)
        self.assertTrue(any("引っかかり" in p for p in s["problems"]))
        self.assertTrue(any("途切れ" in p for p in s["problems"]))

    def test_judge_latency_passthrough(self):
        lines = [f"{ts(1)} station=A colour=SYSTEM event=judge-latency mode=auto applied-ms=95 auto-ms=92 source=estimate",
                 f"{ts(2)} station=A colour=SYSTEM event=judge-latency mode=auto applied-ms=95 auto-ms=92 source=estimate"]
        s = self.summary(lines)
        self.assertEqual(s["judge_latency"]["n"], 2)
        self.assertEqual(s["judge_latency"]["last"]["applied-ms"], "95")
        self.assertIn("judge-latency 2 回", play_report.render(s))


class CliTests(unittest.TestCase):
    def setUp(self):
        before = [perf(t, red=20.0) for t in (10, 20)] + [estimate(15, fps="30.0", c2s=48.0)]
        before += [judge(t, "Good", -50) for t in (11, 12, 13)]
        after = [perf(t) for t in (210, 220)] + [estimate(215, c2s=30.0)]
        after += [judge(t, "Perfect", -5, latency=75) for t in (211, 212, 213)]
        session = [f"{ts(200)} station=A colour=SYSTEM event=session-start run-in-background=true"]
        self.path = write_log(before + session + after)

    def tearDown(self):
        os.unlink(self.path)

    def test_by_trial_splits_at_f10_marks_and_keeps_the_settings(self):
        marks = [f"{ts(5)} station=A colour=SYSTEM event=trial-mark n=1 judge=game offset-ms=0 applied-ms=120 "
                 "prediction-red-ms=0 prediction-blue-ms=0 frame-cap=2",
                 f"{ts(205)} station=A colour=SYSTEM event=trial-mark n=2 judge=auto offset-ms=0 applied-ms=75 "
                 "prediction-red-ms=0 prediction-blue-ms=0 frame-cap=2"]
        with open(self.path, encoding="utf-8") as handle:
            lines = handle.read().splitlines()
        path = write_log(sorted(lines + marks))
        out_json = path + ".json"
        try:
            code, text = run(["--events", path, "--by-trial", "--json", out_json])
            self.assertEqual(code, 0)
            self.assertIn("試行 1:", text)
            self.assertIn("judge=game", text)
            self.assertIn("試行 2:", text)
            with open(out_json, encoding="utf-8") as handle:
                trials = json.load(handle)["trials"]
            self.assertEqual([t["n"] for t in trials], ["1", "2"])
            self.assertEqual(trials[1]["settings"]["applied-ms"], "75")
            self.assertEqual(trials[0]["report"]["overall"]["judge"]["judge_ms_median"], -50)
            self.assertEqual(trials[1]["report"]["overall"]["judge"]["judge_ms_median"], -5)
        finally:
            os.unlink(path)
            if os.path.exists(out_json):
                os.unlink(out_json)

    def test_by_trial_without_marks_says_so(self):
        code, text = run(["--events", self.path, "--by-trial"])
        self.assertEqual(code, 0)
        self.assertIn("F10 の印", text)

    def test_compare_mode(self):
        out_json = self.path + ".json"
        try:
            code, text = run(["--events", self.path, "--compare", f"{ts(0)}..{ts(100)}",
                              f"{ts(200)}..{ts(300)}", "--json", out_json])
            self.assertEqual(code, 0)
            self.assertIn("プレイ比較", text)
            with open(out_json, encoding="utf-8") as handle:
                data = json.load(handle)
            rows = {r["name"]: r for r in data["compare"]}
            self.assertEqual(rows["c2s 中央値 ms"]["a"], 48.0)
            self.assertEqual(rows["c2s 中央値 ms"]["b"], 30.0)
            self.assertEqual(rows["c2s 中央値 ms"]["diff"], -18.0)
            self.assertEqual(rows["最適な補正（推定）ms"]["a"], 70.0)
            self.assertEqual(rows["最適な補正（推定）ms"]["b"], 70.0)
            self.assertEqual(rows["fps 30 の割合 %"]["diff"], -100.0)
            self.assertEqual(rows["赤 pkt/s"]["diff"], 10.0)
        finally:
            if os.path.exists(out_json):
                os.unlink(out_json)

    def test_compare_with_empty_window(self):
        code, text = run(["--events", self.path, "--compare", f"{ts(1000)}..{ts(1100)}", f"{ts(200)}.."])
        self.assertEqual(code, 0)
        self.assertIn("B−A", text)

    def test_last_session(self):
        code, text = run(["--events", self.path, "--last-session"])
        self.assertEqual(code, 0)
        self.assertIn("1 曲", text)
        self.assertIn("今の補正 75", text)
        self.assertNotIn("-50", text)

    def test_missing_file_and_bad_time(self):
        self.assertEqual(run(["--events", self.path + ".missing"])[0], 2)
        self.assertEqual(run(["--events", self.path, "--since", "nope:time"])[0], 2)

    def test_empty_window(self):
        code, text = run(["--events", self.path, "--since", ts(5000)])
        self.assertEqual(code, 0)
        self.assertIn("記録がありません", text)


if __name__ == "__main__":
    unittest.main()
