import copy
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

import fixb_offline as fixb


def points(length, x=0, y=0):
    return [{"x": x, "y": y}, {"x": x + length, "y": y}]


def frame(key="a", truth="saber", detected=True):
    candidate = {"eligible": detected, "endpoints": points(100), "source": "core-line",
                 "raw_pca_span": 102, "robust_body_length": 30, "retained_body_ratio": .6,
                 "longitudinal_continuity": .9, "production": {"robust_endpoints": points(30)},
                 "offline": {"body_pca": points(28), "body_points": 20, "body_aspect": 6,
                             "body_extent": .6, "axis_alignment": .99, "tail_points": 20,
                             "tail_core": 0, "tail_high": 0, "core_groups": 1}}
    colors = {c: {"selected": candidate["endpoints"] if detected else None,
                  "candidates": [copy.deepcopy(candidate)]} for c in fixb.COLORS}
    return {"id": key, "truth": {c: truth for c in fixb.COLORS}, "colors": colors}


class FixBOfflineTests(unittest.TestCase):
    def test_weak_tail_uses_recomputed_body_pca_not_projected_interval(self):
        row = frame()
        self.assertFalse(fixb.gate_reasons(fixb.winner(row, "blue"), fixb.Gate()))
        self.assertEqual(fixb.endpoints(row, "blue", "measured_body"), points(28))
        self.assertEqual(fixb.endpoints(row, "blue", "divergent_robust"), points(30))
        self.assertEqual(fixb.endpoints(row, "blue", "production"), points(100))

    def test_long_blade_and_separated_led_are_never_shortened_by_gate(self):
        long_blade = frame()
        c = fixb.winner(long_blade, "blue")
        c["raw_pca_span"], c["robust_body_length"] = 600, 590
        c["endpoints"] = points(598)
        self.assertEqual(fixb.endpoints(long_blade, "blue", "measured_body"), points(598))
        separated_led = frame()
        c = fixb.winner(separated_led, "blue")
        c["offline"]["core_groups"] = 3
        self.assertIn("separated_core_groups", fixb.gate_reasons(c, fixb.Gate()))
        self.assertEqual(fixb.endpoints(separated_led, "blue", "measured_body"), points(100))
        # The deliberately unsafe counterfactual would shorten this LED blade.
        self.assertEqual(fixb.endpoints(separated_led, "blue", "robust_all"), points(30))

    def test_each_missing_gate_condition_vetoes_the_change(self):
        cases = [("body_aspect", 1, "body_not_rod_proxy"),
                 ("tail_core", 10, "tail_core_supported"),
                 ("tail_high", 10, "tail_high_supported"),
                 ("axis_alignment", .5, "body_axis_disagrees"),
                 ("body_pca", None, "body_pca_short_or_missing"),
                 ("tail_points", 0, "no_tail")]
        for key, value, reason in cases:
            with self.subTest(key=key):
                row = frame()
                c = fixb.winner(row, "red")
                c["offline"][key] = value
                self.assertIn(reason, fixb.gate_reasons(c, fixb.Gate()))
                self.assertEqual(fixb.endpoints(row, "red", "measured_body"), points(100))

    def test_endpoint_order_does_not_create_false_changes_or_jitter(self):
        self.assertEqual(fixb.endpoint_delta(points(100), list(reversed(points(100)))), 0)
        self.assertEqual(fixb.endpoint_delta(points(100), points(100, y=3)), 3)

    def test_no_detection_changes_and_unknown_is_not_a_positive(self):
        rows = [frame("a", "no_saber"), frame("b", "saber", False), frame("c", "unknown")]
        result = fixb.change_counts(rows, "measured_body")["red"]
        self.assertEqual(result["no_saber"]["changed"], 1)
        self.assertEqual(result["saber"]["changed"], 0)
        self.assertEqual(result["unknown"]["changed"], 1)
        self.assertEqual(sum(v["detection_changes"] for v in result.values()), 0)
        self.assertIsNone(fixb.endpoints(rows[1], "blue", "robust_all"))

    def test_static_pairs_do_not_cross_gaps_windows_or_missing_frames(self):
        rows = [frame("a"), frame("b"), frame("c"), frame("d", detected=False)]
        for endpoint in fixb.winner(rows[1], "blue")["endpoints"]:
            endpoint["y"] += 10
        labels = [{"session": "private", "color": "blue", "motion": "static", "frames": [1, 2, 4, 5],
                   "images": [{"sha256": key} for key in ("a", "b", "c", "d")]},
                  {"session": "private", "color": "blue", "motion": "static", "frames": [6],
                   "images": [{"sha256": "a"}]}]
        result = next(iter(fixb.static_jitter(rows, labels, "production").values()))
        self.assertEqual(result["displacement_px"]["n"], 1)
        self.assertEqual(result["displacement_px"]["p50"], 10)
        self.assertEqual(result["missing_pairs"], 1)
        self.assertNotIn("private", json.dumps(fixb.static_jitter(rows, labels, "production")))

    def test_formal_tolerance_flags_unsafe_led_shortening(self):
        row = frame()
        c = fixb.winner(row, "blue")
        c["offline"]["core_groups"] = 2
        manifest = {"fixtures": [{"sha256": "a", "color": "BLUE", "expectedDetected": True,
                                 "expectedEndpoint": points(100), "endpointTolerancePx": 3,
                                 "failureClass": "B", "scenario": "point-led"}]}
        safe = fixb.protections([row], manifest, "measured_body")
        unsafe = fixb.protections([row], manifest, "robust_all")
        self.assertEqual(safe["endpoint_or_detection_failures"], 0)
        self.assertEqual(safe["long_or_point_led_changed"], 0)
        self.assertEqual(unsafe["endpoint_or_detection_failures"], 1)
        self.assertEqual(unsafe["long_or_point_led_changed"], 1)

    def test_materialization_writes_only_the_copy_and_checks_contracts(self):
        before = fixb.core_digest(fixb.CORE)
        with tempfile.TemporaryDirectory() as temp:
            destination = Path(temp) / "core"
            fixb.materialize(fixb.CORE, destination)
            copied = (destination / "src/detection.cpp").read_text()
            self.assertIn("c.offline = offline_measure", copied)
            self.assertEqual((destination / "src/frame_processor.cpp").read_bytes(),
                             (fixb.CORE / "src/frame_processor.cpp").read_bytes())
            self.assertIn('\\"offline\\"', (destination / "tools/png_cli.cpp").read_text())
            path = destination / "contract.txt"
            path.write_text("same same")
            with self.assertRaisesRegex(ValueError, "contract changed"):
                fixb.patch_once(path, "same", "other")
        self.assertEqual(fixb.core_digest(fixb.CORE), before)

    def test_stale_core_baseline_is_rejected_before_build(self):
        with self.assertRaisesRegex(ValueError, "core differs"):
            fixb.probe({"core_sha256": "different"}, {})

    def test_cpp_observer_measures_long_body_weak_tail_and_split_led_support(self):
        # Compile the actual observer against the unchanged production PCA.
        text = (fixb.CORE / "src/detection.cpp").read_text()
        pca = text[text.index("std::optional<Endpoints> principal_axis_endpoints("):
                   text.index("namespace detail {")]
        helper = Path(fixb.__file__).with_name("fixb_offline_evidence.cpp.inc").read_text()
        source = '#include "internal.hpp"\n#include <cassert>\n'
        source += "namespace phonesaber {\n" + fixb.STRUCT + pca + "}\n"
        source += "namespace phonesaber::detail {\nstruct Body { Points points; std::size_t point_count; };\n"
        source += helper + "}\n"
        source += r'''
int main() {
    using namespace phonesaber;
    using namespace phonesaber::detail;
    const int w = 80, h = 20;
    Mask value(w*h,100), core(w*h), unused(w*h);
    Evidence evidence{SaberColor::blue,unused,value,unused,unused,core};
    Points points;
    auto rect = [&](int start, int end) {
        for (int y = 2; y <= 4; ++y) for (int x = start; x <= end; ++x) {
            points.push_back({x,y}); value[y*w+x] = 250; core[y*w+x] = 1;
        }
    };
    auto projections = [&]() {
        std::vector<double> result;
        for (auto p : points) result.push_back(double(p.x));
        return result;
    };
    rect(2,60);
    Body whole{{},points.size()};
    auto long_body = offline_measure(points,whole,evidence,w,0,projections());
    assert(long_body.body_points == 177 && long_body.tail_points == 0);
    assert(long_body.body_aspect > 19 && long_body.body_extent == 1);
    assert(long_body.body_pca && long_body.core_groups == 1);
    assert(long_body.axis_alignment == 1);
    points.clear(); value.assign(w*h,100); core.assign(w*h,0);
    rect(2,11);
    Body body{points,points.size()};
    points.push_back({30,3});
    auto weak = offline_measure(points,body,evidence,w,0,projections());
    assert(weak.body_points == 30 && weak.tail_points == 1);
    assert(weak.tail_core == 0 && weak.tail_high == 0 && weak.core_groups == 1);
    assert(weak.body_core == 30 && weak.body_high == 30);
    points.pop_back(); rect(30,35);
    auto split = offline_measure(points,body,evidence,w,0,projections());
    assert(split.body_points == 30 && split.tail_points == 18);
    assert(split.tail_core == 18 && split.tail_high == 18 && split.core_groups == 2);
}
'''
        with tempfile.TemporaryDirectory() as temp:
            cpp, binary = Path(temp) / "observer.cpp", Path(temp) / "observer-test"
            cpp.write_text(source)
            run = subprocess.run(["clang++", "-std=c++17", "-O2", "-ffp-contract=off",
                "-Wall", "-Wextra", "-Werror", "-I" + str(fixb.CORE / "include"),
                "-I" + str(fixb.CORE / "src"), str(cpp), "-o", str(binary)],
                capture_output=True, timeout=120)
            self.assertEqual(run.returncode, 0, run.stderr.decode(errors="replace"))
            self.assertEqual(subprocess.run([str(binary)], capture_output=True, timeout=10).returncode, 0)


if __name__ == "__main__":
    unittest.main()
