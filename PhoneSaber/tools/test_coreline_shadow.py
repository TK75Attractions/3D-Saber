import unittest
import json
from pathlib import Path
import subprocess
import tempfile

from coreline_shadow import CORE, accepts, select, summarize


def candidate(source="core-line", eligible=True, **overrides):
    value = dict(source=source, eligible=eligible, score=100,
                 endpoints=[{"x": 0, "y": 0}, {"x": 100, "y": 0}],
                 color_purity=.3, high_value_ratio=.7, point_count=100,
                 core_support=.6, used_point_led_fallback=True, largest_longitudinal_gap=0,
                 support=dict(span_per_core=3, lit_gap_px=40, core_gap_px=50,
                              lit_coverage=.4, core_coverage=.2))
    value.update(overrides)
    return value


class CorelineShadowTests(unittest.TestCase):
    def test_native_observer_ignores_dark_bridge_points(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "probe.cpp"
            source.write_text('''#include "coreline_shadow_support.hpp"
int main() {
    using namespace phonesaber; using namespace phonesaber::detail;
    Mask core{1,0,0,0,1}, zero(5);
    Evidence e{SaberColor::blue,zero,zero,zero,zero,core};
    Scored s; s.support_points={{0,0},{1,0},{2,0},{3,0},{4,0}};
    shadow_support(s,e,5,2);
}
''')
            executable = Path(directory) / "probe"
            subprocess.run(["clang++", "-std=c++17", "-I" + str(CORE / "include"),
                            "-I" + str(CORE / "src"), "-I" + str(Path(__file__).parent),
                            str(source), "-o", str(executable)], check=True, capture_output=True)
            result = json.loads(subprocess.run([str(executable)], check=True,
                                             capture_output=True).stderr)
            self.assertEqual(result["grid_pixels"], 5)
            self.assertEqual(result["core_pixels"], 2)
            self.assertEqual(result["lit_coverage"], .4)
            self.assertEqual(result["lit_gap_px"], 6)
            self.assertEqual(result["span_per_core"], 2.5)

    def test_actual_support_rejects_synthetic_continuity(self):
        c = candidate()
        self.assertTrue(accepts(c, "blue", {"reported_gap_px": 4}, 307200))
        self.assertFalse(accepts(c, "blue", {"lit_gap_px": 32}, 307200))
        self.assertFalse(accepts(c, "blue", {"lit_coverage": .5}, 307200))
        self.assertTrue(accepts(c, "blue", {"lit_coverage": .4}, 307200))

    def test_reselects_current_eligible_without_mutation(self):
        first, second, rejected = candidate(), candidate("color-mask"), candidate(eligible=False)
        data = {"selected": first["endpoints"], "candidates": [rejected, first, second]}
        self.assertIs(select(data, "red", {"lit_gap_px": 16}, 307200), second)
        self.assertEqual(first["score"], 100)
        self.assertTrue(first["eligible"])
        self.assertFalse(accepts(rejected, "red", {}, 307200))

    def test_blue_mask_area_is_sampled_pixel_area(self):
        c = candidate("color-mask", point_count=1536, high_value_ratio=.35)
        self.assertTrue(accepts(c, "blue", {"mask_area": .02, "mask_hv": .35}, 307200))
        c["point_count"] += 1
        self.assertFalse(accepts(c, "blue", {"mask_area": .02}, 307200))
        self.assertTrue(accepts(c, "red", {"mask_area": .02}, 307200))
        self.assertTrue(accepts(candidate("core-halo"), "blue", {"mask_area": 0}, 307200))

    def test_null_and_changed_output_are_distinct(self):
        c = candidate()
        row = dict(truth={"red": "unknown", "blue": "saber"}, area_pixels=307200,
                   colors={color: dict(selected=c["endpoints"], candidates=[c])
                           for color in ("red", "blue")})
        result = summarize([row], {"lit_coverage": .5})
        self.assertEqual(result["red"]["unknown_changed"], 1)
        self.assertEqual(result["blue"]["saber_lost"], 1)
        self.assertEqual(result["blue"]["saber_after"], 0)

    def test_guard_needs_current_local_supported_alternative(self):
        line = candidate()
        body = candidate("color-mask", support=dict(lit_coverage=.95), core_support=.45)
        data = dict(candidates=[line, body])
        self.assertIs(select(data, "red", {"guarded_lit": .6}, 307200), body)
        body["eligible"] = False
        self.assertIs(select(data, "red", {"guarded_lit": .6}, 307200), line)
        body["eligible"] = True
        body["endpoints"] = [{"x": 400, "y": 400}, {"x": 450, "y": 400}]
        self.assertIs(select(data, "red", {"guarded_lit": .6}, 307200), line)
        body["endpoints"] = line["endpoints"]
        line["used_point_led_fallback"] = False
        self.assertIs(select(data, "red", {"guarded_lit": .6}, 307200), line)


if __name__ == "__main__":
    unittest.main()
