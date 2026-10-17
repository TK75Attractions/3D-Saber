"""低発熱表示の状態機械を実ソースで実行し、capture session に触れないことを確認する。GUI不要。"""
from pathlib import Path
import re
import shutil
import subprocess
import sys
import unittest


IOS = Path(__file__).resolve().parents[2]
SOURCE = IOS / "PhoneSaberSender" / "PhoneSaberSender"
RUNNER = IOS / "PhoneSaberSenderTests" / "run_low_heat_display_host_tests.py"


class IOSLowHeatDisplaySourceTests(unittest.TestCase):
    def test_low_heat_display_never_touches_capture_or_video_output(self):
        controller = (SOURCE / "LowHeatDisplay.swift").read_text()
        policy = (SOURCE / "LowHeatDisplayPolicy.swift").read_text()
        for text in (controller, policy):
            for forbidden in ["startRunning", "stopRunning", "beginConfiguration", "removeOutput",
                              "addOutput", "AVCaptureVideoDataOutput", "activeFormat", "FrameDuration"]:
                self.assertNotIn(forbidden, text)
        # preview は connection の有効/無効だけで止める。
        self.assertIn("connection.isEnabled = enabled", controller)

    def test_preview_view_is_reused_instead_of_recreated(self):
        preview = (SOURCE / "CameraPreview.swift").read_text()
        make = preview[preview.index("func makeUIView("):preview.index("func updateUIView(")]
        self.assertNotIn("PreviewView()", make)
        content = (SOURCE / "ContentView.swift").read_text()
        self.assertIn("previewView: model.lowHeatDisplay.previewView", content)

    def test_lifecycle_modifiers_stay_outside_the_low_heat_swap(self):
        content = (SOURCE / "ContentView.swift").read_text()
        body = content[content.index("    var body: some View {"):content.index("    @ViewBuilder private var mainScreen")]
        # 黒い画面との切り替えで onAppear が再実行されると、停止直後に自動送信が再開しうる。
        for modifier in [".onAppear { model.sceneDidChange", ".onChange(of: scenePhase)", ".task {"]:
            self.assertIn(modifier, body)
        main = content[content.index("    @ViewBuilder private var mainScreen"):]
        main = main[:main.index("\n}\n")]
        self.assertNotIn(".onAppear { model.sceneDidChange", main)
        self.assertNotIn(".onChange(of: scenePhase)", main)

    @unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
    def test_policy_xctests_run_on_host(self):
        result = subprocess.run([sys.executable, str(RUNNER)], capture_output=True, text=True, timeout=240)
        self.assertEqual(result.returncode, 0, result.stdout[-4000:] + result.stderr[-4000:])
        self.assertTrue(re.search(r"low-heat display host tests: PASS \(\d+ tests\)", result.stdout), result.stdout[-2000:])


if __name__ == "__main__":
    unittest.main()
