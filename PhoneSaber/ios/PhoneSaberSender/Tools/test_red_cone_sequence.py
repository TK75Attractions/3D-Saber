"""2026-10-29 の赤コーン誤取得を、画像不要の実測候補列で毎回再生する。"""
import hashlib
import json
import os
from pathlib import Path
from unittest import mock
import unittest

import phone_saber_stationary_replay as replay


FIXTURES = Path(__file__).resolve().parent / "fixtures/red_cone_20261029"


def in_cone(endpoints, rectangles):
    """回転前480×640座標の出力中点が、目視で固定したコーン矩形に入るか。"""
    if endpoints is None:
        return False
    x = (endpoints[0] + endpoints[2]) / 2
    y = (endpoints[1] + endpoints[3]) / 2
    return any(x1 <= x <= x2 and y1 <= y <= y2 for x1, y1, x2, y2 in rectangles)


def metrics(clip, output, rectangles):
    # フレームの省略・並替えで、誤取得数だけが小さくなることを防ぐ。
    if [f["f"] for f in clip["frames"]] != [f["f"] for f in output["frames"]]:
        raise ValueError("replay frame IDs differ from fixture")
    selected = [f["red"]["sel"] for f in output["frames"]]
    return {
        "coneSelectedFrames": sum(in_cone(e, rectangles) for e in selected),
        "redOutputAbsentFrames": selected.count(None),
        "recordedConeSelectedFrames": sum(in_cone(f["recorded"]["red"], rectangles)
                                           for f in clip["frames"]),
        "recordedMismatchFrames": sum(e != f["recorded"]["red"]
                                      for f, e in zip(clip["frames"], selected)),
        "resetFrames": sum(f["reset"] for f in clip["frames"]),
        "truncatedFrames": sum(f["observedTruncated"] for f in clip["frames"]),
    }


class RedConeSequenceTests(unittest.TestCase):
    def test_saved_candidates_with_production_selector(self):
        manifest = json.loads((FIXTURES / "manifest.json").read_text())
        self.assertEqual(manifest["schemaVersion"], 1)
        self.assertEqual(len(manifest["clips"]), 12)
        clips = []
        # 実験用の任意環境変数で通常verifyの入力を変えない。
        with mock.patch.dict(os.environ):
            os.environ.pop("PHONESABER_REPLAY_EMITTER_PEAK_FLOOR", None)
            for row in manifest["clips"]:
                path = FIXTURES / row["id"]
                self.assertEqual(hashlib.sha256((path / "summary.json").read_bytes()).hexdigest(),
                                 row["fixtureSHA256"], row["id"])
                clip = replay.load_clip(path)
                frames = clip["frames"]
                self.assertEqual((clip["width"], clip["height"]), (480, 640))
                self.assertEqual(len(frames), row["frameCount"], row["id"])
                self.assertEqual((frames[0]["f"], frames[-1]["f"]),
                                 (row["firstFrame"], row["lastFrame"]), row["id"])
                # 収録時のframe欠落は埋めず、既存loaderのreset規則に任せる。
                self.assertTrue(all(a["f"] < b["f"] for a, b in zip(frames, frames[1:])))
                self.assertTrue(all(a["t"] < b["t"] for a, b in zip(frames, frames[1:])))
                self.assertIn(row["eventFrame"], [f["f"] for f in frames])
                clips.append(clip)
            # policy指定なし。本番ソースのSaberTrackLockをコンパイルして連続実行。
            outputs = replay.run(clips)
        self.assertEqual(len(outputs), len(clips))
        for row, clip, output in zip(manifest["clips"], clips, outputs):
            with self.subTest(clip=row["id"]):
                self.assertEqual(output["name"], clip["name"])
                actual = metrics(clip, output, manifest["coneRects"])
                baseline = row["baseline"]
                print(f"red-cone {row['id']}: frames={len(clip['frames'])} "
                      f"cone={actual['coneSelectedFrames']} "
                      f"red_output_absent={actual['redOutputAbsentFrames']} "
                      f"visual_event_red_absent={int(row['eventRedAbsent'])}/1 "
                      f"recorded_cone={actual['recordedConeSelectedFrames']} "
                      f"recorded_mismatch={actual['recordedMismatchFrames']} "
                      f"resets={actual['resetFrames']} truncated={actual['truncatedFrames']}")
                # expectedFailureにしない。悪化は即失敗、改善も基準値の明示更新を要求。
                self.assertLessEqual(actual["coneSelectedFrames"], baseline["coneSelectedFrames"],
                                     "赤コーン選択が既知上限を超えた")
                self.assertEqual(actual["coneSelectedFrames"], baseline["coneSelectedFrames"],
                                 "赤コーン選択が改善。原画像を再確認しmanifestの上限を下げてください")
                self.assertEqual(actual, baseline,
                                 "不在数/履歴の変化を確認し、理由とともにmanifestを更新してください")


if __name__ == "__main__":
    unittest.main()
