"""発光分布の測定でRGB順・短い軸の空bin・未ラベルを誤って扱わない。"""
import json
from pathlib import Path
import tempfile
import unittest

import numpy as np

import phone_saber_emission_study as study


class EmissionStudyTests(unittest.TestCase):
    def test_short_unsaturated_bar_has_no_artificial_empty_bins(self):
        rgb = np.zeros((24, 24, 3), dtype=np.uint8)
        rgb[10:15, 4:17] = [146, 8, 4]
        forward = study.pixel_features(rgb, [4, 12, 16, 12], [4, 10, 16, 14], 60, "red")
        reverse = study.pixel_features(rgb, [16, 12, 4, 12], [4, 10, 16, 14], 60, "red")
        self.assertEqual(forward, reverse)
        self.assertEqual(forward["pixelPeak"], 146)
        self.assertEqual(forward["RG"], 146 / 8)
        self.assertEqual(forward["RB"], 146 / 4)
        self.assertEqual(forward["axial220"], 0)
        self.assertEqual(forward["axialRelative"], 1)

    def test_white_highlights_do_not_supply_colored_axis_support(self):
        rgb = np.full((24, 24, 3), 255, dtype=np.uint8)
        result = study.pixel_features(rgb, [4, 12, 16, 12], [4, 10, 16, 14], 60, "blue")
        self.assertEqual(result["pixelCount"], 0)
        self.assertIsNone(result["axialRelative"])
        self.assertIsNone(result["pixelPeak"])

    def test_center_and_edge_saturation_sign_uses_original_pixels(self):
        rgb = np.zeros((24, 24, 3), dtype=np.uint8)
        rgb[8:17, 4:17] = [160, 10, 10]
        rgb[11:14, 4:17] = [250, 120, 120]
        result = study.pixel_features(rgb, [4, 12, 16, 12], [4, 8, 16, 16], 96, "red")
        self.assertEqual(result["centerEdgeValue"], 90)
        self.assertLess(result["centerEdgeSat"], 0)

    def test_labels_require_original_review_and_unique_candidate_identity(self):
        label = dict(clip="clip", frame=1, color="red", source="color-mask",
                     endpoints=[1, 2, 3, 4], imageSHA256="a" * 64, label="blade")
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "labels.json"
            path.write_text(json.dumps(dict(schemaVersion=1, reviewedOriginal=False, candidates=[label])))
            with self.assertRaises(ValueError):
                study.load_labels(path)
            path.write_text(json.dumps(dict(schemaVersion=1, reviewedOriginal=True, candidates=[label, label])))
            with self.assertRaises(ValueError):
                study.load_labels(path)

    def test_unknown_candidates_are_not_counted_as_negatives(self):
        row = dict(color="red", label="unknown", eligible=True)
        result = study.distributions([row])
        self.assertNotIn("|unknown|", result)
        self.assertNotIn("|blade|", result)


if __name__ == "__main__":
    unittest.main()
