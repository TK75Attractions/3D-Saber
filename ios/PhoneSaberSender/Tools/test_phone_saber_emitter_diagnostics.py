#!/usr/bin/env python3
"""Optional emitter diagnostics and per-frame camera state.

Both are additive Debug Recording fields: validators must accept them when
present and keep accepting older bundles that omit them.
"""
from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_metadata_schema import validate_document
from phone_saber_tracking_diagnostics import (
    validate_candidate_geometry, validate_emitter_diagnostics, validate_frame_camera,
)
from phone_saber_triage_codex import _validate_decision_trace
from phone_saber_triage_protocol import BundleError
from test_phone_saber_candidate_geometry import block, candidate


EMITTER = {"emitterScore": 0.894, "emitterScoreThreshold": 0.42, "emitterScoreMargin": 0.474,
           "peakTerm": 0.290909, "meanTerm": 0.217895, "highValueTerm": 0.28, "purityTerm": 0.1056,
           "clippedWhiteTerm": 0.0, "hasEmitterCore": True, "coreByHighValueRatio": True,
           "coreByPeakAndMean": True, "coreByClippedWhite": False, "baseEligible": True,
           "compactRedGate": False, "majorLengthSamples": 100.0, "bladeLengthSupport": 1.0,
           "localContrast": 1.0, "emitterTexture": 0.0, "brightnessVariation": 0.0, "coreSupport": 0.0,
           "longitudinalCoreCoverage": 0.0, "longitudinalHighCoverage": 1.0, "sampleCount": 300,
           "colorSampleCount": 300, "meanMaxChannel": 250.0, "meanSecondChannel": 40.0,
           "maxSecondChannel": 40, "meanMinChannel": 30.0, "nearWhiteFraction": 0.0,
           "brightSecondChannelFraction": 0.0}

GEOMETRY_EMITTER = {key: EMITTER[key] for key in (
    "emitterScore", "emitterScoreMargin", "peakTerm", "meanTerm", "highValueTerm", "purityTerm",
    "clippedWhiteTerm", "hasEmitterCore", "bladeLengthSupport", "localContrast", "emitterTexture",
    "coreSupport", "meanSecondChannel", "meanMinChannel", "nearWhiteFraction")}
CAMERA = {"source": "exif+device", "iso": 320, "exposureDurationSeconds": 0.008333, "exposureBiasEV": 0,
          "brightnessValue": 1.5, "fNumber": 1.78, "exposureTargetBias": 0, "exposureTargetOffset": -0.125,
          "whiteBalanceGains": [1.9, 1.0, 2.1], "deviceSampleAgeSeconds": 0.1}

TRACE = {"index": 0, "sourceType": "color-mask", "eligible": True, "finalScore": 61.2,
         "rejectionReasons": [], "rules": [], "peakValue": 250, "meanValue": 250.0}


class EmitterDiagnosticsValidationTests(unittest.TestCase):
    def test_geometry_accepts_emitter_evidence_and_older_entries(self):
        with_emitter = candidate(0, rank=1)
        with_emitter["emitter"] = copy.deepcopy(GEOMETRY_EMITTER)
        reduced = candidate(1, rank=2, centroid=(300.0, 100.0))
        reduced["emitterDiagnosticsReduced"] = True
        validate_candidate_geometry(block([with_emitter, reduced]))
        validate_candidate_geometry(block([candidate(0, rank=1)]))  # older bundle

    def test_geometry_rejects_malformed_emitter_evidence(self):
        for mutate in (
                lambda c: c["emitter"].__setitem__("emitterScore", "high"),
                lambda c: c["emitter"].__setitem__("unknownKey", 1),
                lambda c: c["emitter"].pop("emitterScoreMargin"),
                lambda c: c.__setitem__("emitterDiagnosticsReduced", True)):
            entry = candidate(0, rank=1)
            entry["emitter"] = copy.deepcopy(GEOMETRY_EMITTER)
            mutate(entry)
            with self.assertRaises(BundleError):
                validate_candidate_geometry(block([entry]))

    def test_decision_trace_accepts_emitter_diagnostics_and_older_traces(self):
        _validate_decision_trace([dict(TRACE, emitterDiagnostics=copy.deepcopy(EMITTER))], "context.json")
        _validate_decision_trace([dict(TRACE)], "context.json")
        blue = copy.deepcopy(EMITTER)
        blue["meanSecondChannel"] = None
        with self.assertRaises(BundleError):  # compact traces never contain nulls here
            _validate_decision_trace([dict(TRACE, emitterDiagnostics=blue)], "context.json")
        del blue["meanSecondChannel"]
        _validate_decision_trace([dict(TRACE, emitterDiagnostics=blue)], "context.json")
        bad = copy.deepcopy(EMITTER)
        bad["hasEmitterCore"] = 1
        with self.assertRaises(BundleError):
            _validate_decision_trace([dict(TRACE, emitterDiagnostics=bad)], "context.json")
        validate_emitter_diagnostics(EMITTER)

    def test_frame_camera_validation(self):
        validate_frame_camera(CAMERA)
        validate_frame_camera({"source": "exif", "iso": 100})
        for bad in ({"iso": 100}, dict(CAMERA, source="guess"), dict(CAMERA, iso="100"),
                    dict(CAMERA, whiteBalanceGains=[1, 2]), dict(CAMERA, lux=3)):
            with self.assertRaises(BundleError):
                validate_frame_camera(bad)

    def test_metadata_schema_types_new_fields_without_requiring_them(self):
        def frame(**extra):
            candidate_entry = {"index": 0, "selected": True, "sourceType": "color-mask", "eligible": True,
                               "finalScore": 61.2, "scoreBreakdown": {}, "rawPCAEndpoints": {},
                               "rawPCASpan": 1.0, "robustMainIntervalLength": 1.0, "finalOutputEndpoints": {},
                               "continuity": 1.0, "density": 3.0, "maxGap": 0, "componentArea": 10,
                               "pointCount": 10, "usedPointLEDFallback": False}
            candidate_entry.update(extra.pop("candidate", {}))
            colors = {"totalCandidateCount": 1, "eligibleCandidateCount": 1, "maskPixelCount": 1,
                      "morphologyPixelCount": 1, "connectedComponentCount": 1, "topCandidates": [candidate_entry]}
            result = {"frameID": 1, "presentationTimeSeconds": 0.0,
                      "red": {"detected": False, "predicted": False},
                      "blue": {"detected": False, "predicted": False},
                      "redDetectionSucceeded": False, "blueDetectionSucceeded": False,
                      "candidateDiagnostics": {"red": colors, "blue": copy.deepcopy(colors)},
                      "forensicCaptured": False, "manualCaptured": False}
            result.update(extra)
            return result

        def warnings(document_frame):
            report = validate_document({"formatVersion": 1, "sessionID": "s", "width": 2, "height": 2,
                                        "frames": [document_frame]}).report.warnings
            return {key for key in report if "frames[*]." in key and "scoreBreakdown" not in key
                    and "Endpoints" not in key}

        self.assertEqual(warnings(frame()), set())  # older bundle
        self.assertEqual(warnings(frame(camera=CAMERA, candidate={"emitterDiagnostics": EMITTER})), set())
        self.assertTrue(any("camera.iso" in key for key in
                            warnings(frame(camera=dict(CAMERA, iso="high")))))



if __name__ == "__main__":
    unittest.main()
