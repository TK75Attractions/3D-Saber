import contextlib
import io
import json
from pathlib import Path
import shutil
import tempfile
import unittest

import check_endpoint_predictor_parity as parity
import endpoint_prediction_eval as replay
import real_motion_eval as real
import real_motion_extract as extract

FIXTURE = Path(__file__).with_name("fixtures") / "real_motion_sample.csv"


class ExtractionTests(unittest.TestCase):
    def test_metadata_context_dedup_flags_anonymity_and_roundtrip(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            frame = dict(frameID=42, presentationTimeSeconds=100,
                         red=dict(detected=True, predicted=False, x1=1,y1=2,x2=3,y2=4),
                         blue=dict(detected=False, predicted=False))
            metadata = folder / "private_metadata.json"
            metadata.write_text(json.dumps(dict(sessionID="private",width=480,height=640,frames=[frame])))
            frames = folder / "frames"
            frames.mkdir()
            (frames / "frame_42.json").write_text(json.dumps(dict(sessionID="private",frames=[
                dict(frameID=42,timestamp=100,red=dict(detected=True,predictionUsed=False,endpoint=[1,2,3,4]))])))
            rows, manifest = extract.extract([folder],480,640)
            self.assertEqual(2,len(rows))
            self.assertEqual("s000",rows[0]["session"])
            self.assertEqual(0,rows[0]["time_s"])
            self.assertEqual([None]*4,[rows[0][k] for k in ("x1","y1","x2","y2")])
            self.assertFalse(rows[1]["predicted"])
            self.assertEqual(2,len(manifest["sessions"][0]["files"]))
            for suffix in (".csv",".json"):
                path = folder / ("output"+suffix)
                extract.write_rows(path,rows)
                loaded = extract.read_rows(path)
                for a,b in zip(rows,loaded):
                    for field in extract.FIELDS:
                        self.assertEqual(str(a[field]),str(b[field])) if field == "frame" else self.assertEqual(a[field],b[field])
            self.assertNotIn("private",(folder / "output.csv").read_text())

    def test_conflict_rejected_and_summary_not_mistaken_for_frames(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            paths = []
            for i in (1,2):
                path = folder / f"{i}_metadata.json"
                path.write_text(json.dumps(dict(sessionID="same",frames=[dict(frameID=1,timestamp=0,
                    red=dict(detected=True,x1=i,y1=2,x2=3,y2=4))])))
                paths.append(path)
            with self.assertRaisesRegex(ValueError,"conflicting duplicate"):
                extract.extract(paths)
            summary = folder / "summary.json"
            summary.write_text(json.dumps(dict(sessionID="same",recordedFrameCount=100,redBlueDetectionSummary={})))
            self.assertEqual([],extract.extract([summary])[0])

    def test_jsonl_nested_endpoints_and_missing_timestamp(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/"frames.jsonl"
            frames = [dict(sessionID="private",frameID=i, captureTimeSeconds=i/30,
                           red=dict(detected=True,endpoints=dict(first=dict(x=1,y=2),second=dict(x=3,y=4)))) for i in range(3)]
            frames.append(dict(sessionID="private",frameID=3,red=dict(detected=True)))
            path.write_text("\n".join(json.dumps(f) for f in frames))
            rows,manifest = extract.extract([path])
            self.assertEqual(3,len(rows))
            self.assertIsNone(rows[0]["predicted"])
            self.assertEqual(1,manifest["skipped_missing_time"])


class RealReplayTests(unittest.TestCase):
    def test_reference_gaps_and_flags_are_not_interpolated(self):
        rows = extract.read_rows(FIXTURE)
        # 同じ時系列の片方の色で、予測・欠測・大きな時間穴を別々に確認。
        rows = [r for r in rows if r["color"] == "blue"][:12]
        for row in rows:
            row["detected"],row["predicted"] = True,False
        rows[3]["predicted"] = True
        rows[7]["detected"] = False
        rows[-1]["time_s"] += 1
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/"rows.json"
            extract.write_rows(path,rows)
            segments,_=real.load_segments(path)
            self.assertEqual([3,3,3],[len(s[2]) for s in segments])
            self.assertIsNone(replay.interpolated_truth(segments[0][2])(rows[3]["time_s"]))
            rows[1]["predicted"]=None
            extract.write_rows(path,rows)
            segments,inventory=real.load_segments(path,allow_unknown_prediction=False)
            self.assertEqual(1,inventory[0]["unknown_prediction_flags"])
            self.assertEqual([3,3],[len(s[2]) for s in segments])

    def test_shared_real_sample_equal_targets_render_rates_and_determinism(self):
        kwargs=dict(horizons=(0,20,40,60),estimators=("baseline","current","ls3","adaptive"),phases=(0,.5))
        report=real.evaluate_real(FIXTURE,**kwargs)
        self.assertEqual(report,real.evaluate_real(FIXTURE,**kwargs))
        self.assertEqual({30,60},{r["render_hz"] for r in report["results"]})
        for hz in (30,60):
            results=[r for r in report["results"] if r["render_hz"]==hz]
            self.assertEqual(1,len({r["endpoint_observations"] for r in results}))
            self.assertEqual(1,len({r["live_observations"] for r in results}))
            self.assertGreater(results[0]["endpoint_observations"],0)
        with tempfile.TemporaryDirectory() as tmp:
            output=Path(tmp)/"report.json"
            with contextlib.redirect_stdout(io.StringIO()):
                cli=replay.main(["--real",str(FIXTURE),"--horizons","0,20,40,60","--phases","0,0.5","--json",str(output)])
            self.assertEqual(json.loads(json.dumps(report)),json.loads(output.read_text()))
            self.assertEqual(report,cli)

    def test_missing_dimensions_and_duplicate_times_rejected(self):
        rows=extract.read_rows(FIXTURE)
        rows[0]["width"]=None
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/"rows.json"
            extract.write_rows(path,rows)
            with self.assertRaisesRegex(ValueError,"source dimensions"):
                real.load_segments(path)
            rows[0]["width"]=480
            rows.append(dict(rows[0]))
            extract.write_rows(path,rows)
            with self.assertRaisesRegex(ValueError,"capture time"):
                real.load_segments(path)

    def test_actual_csharp_and_python_shared_vectors(self):
        self.assertEqual(240,parity.check_python())
        self.assertEqual(240,parity.check_python(baseline=True))
        if shutil.which("dotnet") is None:
            self.skipTest("installed .NET SDK required for actual C# parity")
        self.assertIn("PASS (240 predictions, 960 float components)",parity.check_csharp())


if __name__ == "__main__":
    unittest.main()
