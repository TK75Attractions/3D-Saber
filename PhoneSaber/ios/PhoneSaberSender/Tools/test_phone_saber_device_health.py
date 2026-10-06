"""Health metadata is additive, including the strict Mac triage precheck."""
import copy
import json
import tempfile
import unittest
from pathlib import Path

from phone_saber_metadata_schema import CURRENT_FORMAT_VERSION, validate_document
from phone_saber_triage_codex import input_plan
from phone_saber_triage_protocol import BundleError
from test_phone_saber_triage_codex import write_codex_bundle
from validate_phone_saber_metadata import validate_paths


TOOLS = Path(__file__).resolve().parent
HEALTH = {"thermalState": "serious", "batteryLevel": 0.42, "batteryState": "charging"}


class DeviceHealthMetadataTests(unittest.TestCase):
    def test_old_and_new_recordings_validate_without_warnings(self):
        original = json.loads((TOOLS / "fixtures/unversioned-red-dropout-excerpt.json").read_text())
        original["formatVersion"] = CURRENT_FORMAT_VERSION
        for include_health in (False, True):
            with self.subTest(include_health=include_health), tempfile.TemporaryDirectory() as directory:
                document = copy.deepcopy(original)
                if include_health:
                    for frame in document["frames"]:
                        frame.setdefault("camera", {"source": "device"}).update(HEALTH)
                    for sample in document["cameraSamples"]:
                        sample.update(HEALTH)
                validated = validate_document(document)
                self.assertEqual(validated.report.warning_count, 0)
                if include_health:
                    for camera in [f["camera"] for f in validated.frames] + validated.document["cameraSamples"]:
                        self.assertEqual({k: camera[k] for k in HEALTH}, HEALTH)
                path = Path(directory) / "metadata.json"
                path.write_text(json.dumps(document))
                self.assertEqual(validate_paths([path], strict=True), 0)

    def test_schema_types_optional_health_fields_in_both_locations(self):
        document = json.loads((TOOLS / "fixtures/unversioned-red-dropout-excerpt.json").read_text())
        document["formatVersion"] = CURRENT_FORMAT_VERSION
        bad = {"thermalState": 3, "batteryLevel": "42%", "batteryState": True}
        document["frames"][0]["camera"] = {"source": "device", **bad}
        document["cameraSamples"][0].update(bad)
        validated = validate_document(document)
        for location in ("frames[*].camera", "cameraSamples[*]"):
            for key in HEALTH:
                self.assertTrue(any(f"{location}.{key}: expected" in warning for warning in validated.report.warnings))
        # Codable omits unavailable values; explicit null also remains readable in full metadata.
        document["frames"][0]["camera"] = {"source": "device", **dict.fromkeys(HEALTH)}
        document["cameraSamples"][0].update(dict.fromkeys(HEALTH))
        self.assertEqual(validate_document(document).report.warning_count, 0)

    def test_mac_bundle_precheck_accepts_old_and_all_new_health_states(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            summary = json.loads((bundle / "summary.json").read_text())
            path = bundle / summary["images"][0]["frameContextPath"]
            context = json.loads(path.read_text())
            input_plan(bundle)  # Existing bundles omit camera/health.
            for thermal in ("nominal", "fair", "serious", "critical", "unknown"):
                for battery in ("charging", "full", "unplugged", "unknown"):
                    for level in (None, 0.0, 0.42, 1.0):
                        camera = {"source": "device", "thermalState": thermal, "batteryState": battery}
                        if level is not None:
                            camera["batteryLevel"] = level
                        context["frames"][0]["camera"] = camera
                        path.write_text(json.dumps(context))
                        with self.subTest(camera=camera):
                            self.assertEqual(len(input_plan(bundle).image_paths), 1)
            for bad in ({"thermalState": "hot"}, {"thermalState": []}, {"batteryState": 2},
                        {"batteryLevel": -1}, {"batteryLevel": 1.1}, {"batteryLevel": True},
                        {"batteryLevel": None}, {"batteryLevel": float("nan")}, {"other": 1}):
                context["frames"][0]["camera"] = {"source": "device", **HEALTH, **bad}
                path.write_text(json.dumps(context))
                with self.subTest(bad=bad), self.assertRaisesRegex(BundleError, "invalid frame camera state"):
                    input_plan(bundle)


if __name__ == "__main__":
    unittest.main()
