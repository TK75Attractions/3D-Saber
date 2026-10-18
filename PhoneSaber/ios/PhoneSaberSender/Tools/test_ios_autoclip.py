"""iPhone auto clip end to end on macOS, no GUI: the production AutoClip.swift records
synthetic frames, writes bundles, and the Mac protocol/receiver/report accept them.

Checks that the PNGs are lossless (event frames byte-exact, context frames the exact
step-2 point samples), that each bundle passes the triage protocol validation and is
routed to inbox/autoclips, and that the clip report reads it."""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_clip_report as report  # noqa: E402
import phone_saber_triage_protocol as protocol  # noqa: E402

SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"

MAIN = r'''
import Foundation

let width = 480, height = 640
let directory = URL(fileURLWithPath: CommandLine.arguments[1])
var counter = 0
let recorder = AutoClipRecorder(directory: directory, enabled: true, clipNamer: {
    counter += 1
    return String(format: "20261019_1200%02d_000", counter)
})
let done = DispatchSemaphore(value: 0)
var saved: [String] = []
recorder.onClipSaved = { result in
    switch result {
    case .success(let url): saved.append(url.path)
    case .failure(let error): print("FAIL \(error)")
    }
    done.signal()
}
func candidate(_ a: (Int, Int), _ b: (Int, Int), source: String = "color-mask", eligible: Bool = true) -> SaberCandidate {
    let breakdown = SaberScoreBreakdown(length: 0, aspect: 0, extent: 0, widthConsistency: 0, area: 0,
        peakBrightness: 0, meanBrightness: 0, highBrightnessRatio: 0, colorPurity: 0, localContrast: 0,
        emitterTexture: 0, clippedWhite: 0, longitudinalHighCoverage: 0, coreSupport: 0, longitudinalCoreCoverage: 0)
    let ends = (PixelPoint(x: a.0, y: a.1), PixelPoint(x: b.0, y: b.1))
    return SaberCandidate(source: source, comparisonEndpoints: ends, endpoints: ends,
        boundingBox: SaberBoundingBox(minX: min(a.0, b.0) - 3, minY: min(a.1, b.1) - 3,
                                      maxX: max(a.0, b.0) + 3, maxY: max(a.1, b.1) + 3),
        score: 100, scoreBreakdown: breakdown, isEmitterEligible: eligible, isCompactRed: false,
        peakValue: 255, meanValue: 240, highValueRatio: 0.5, meanColorPurity: 0.6, clippedWhiteRatio: 0,
        brightnessVariation: 0, localContrast: 1, longitudinalHighCoverage: 1, widthVariation: 0,
        coreSupportRatio: 0.5, longitudinalCoreCoverage: 1, longitudinalContinuity: 1, largestLongitudinalGap: 0,
        retainedBodyRatio: 1, rawPCASpan: 100, robustMainIntervalEndpoints: nil, robustMainIntervalLength: 100,
        axialDensity: 1, componentArea: 100, pointCount: 50, usedPointLEDFallback: false)
}
// Static pattern; row 0 carries the frame index so every saved frame is identifiable.
var pixels = [UInt8](repeating: 255, count: width * height * 4)
for y in 0..<height { for x in 0..<width {
    let i = (y * width + x) * 4
    pixels[i] = UInt8(x & 255); pixels[i + 1] = UInt8(y & 255); pixels[i + 2] = UInt8((x ^ y) & 255)
} }
let cone = candidate((400, 560), (420, 620), source: "core-halo")
for frame in 0..<1700 {
    for x in 0..<width {
        let i = x * 4
        pixels[i] = UInt8(frame & 255); pixels[i + 1] = UInt8((frame >> 8) & 255); pixels[i + 2] = UInt8(x & 255)
    }
    // A saber sweeping slowly; a red cone eligible from frame 240; a 1-frame jump at 1500.
    let x = 100 + (frame % 200)
    var saber = candidate((x, 100), (x, 300))
    if frame == 1500 { saber = candidate((300, 500), (300, 620), source: "core-line") }
    let list = frame >= 240 && frame < 400 ? [saber, cone] : [saber]
    let analysis = SaberFrameAnalysis(candidates: [.red: list, .blue: []], selected: [.red: saber.endpoints])
    pixels.withUnsafeBufferPointer { buffer in
        recorder.observe(ObservedFrame(baseAddress: buffer.baseAddress!, width: width, height: height,
            bytesPerRow: width * 4, sequence: UInt64(frame + 1), timestamp: 50 + Double(frame) / 60,
            processingSeconds: 0.01, analysis: analysis, ignoreZones: [:], playArea: nil, captureFPS: 60))
    }
}
for _ in 0..<2 { _ = done.wait(timeout: .now() + 60) }
let status = recorder.statusSnapshot
print("SAVED \(status.savedThisSession)")
for path in saved { print("BUNDLE \(path)") }
'''


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
class IOSAutoClipTests(unittest.TestCase):
    def test_recorded_clips_are_lossless_valid_bundles(self):
        with tempfile.TemporaryDirectory(prefix="phonesaber-autoclip-", dir=os.environ.get("TMPDIR")) as temporary:
            directory = Path(temporary)
            frame_source = (SOURCE / "FrameProcessor.swift").read_text()
            observer = frame_source[frame_source.index("/// One processed camera frame"):
                                    frame_source.index("final class FrameProcessor")]
            (directory / "Observer.swift").write_text("import Foundation\n" + observer)
            (directory / "main.swift").write_text(MAIN)
            binary = directory / "autoclip"
            # The Xcode Debug configuration builds with -O and the DEBUG condition.
            compiled = subprocess.run(
                ["xcrun", "swiftc", "-O", "-D", "DEBUG", "-module-cache-path", str(directory / "module-cache"),
                 str(SOURCE / "DetectionCore.swift"), str(SOURCE / "BGRADetection.swift"),
                 str(directory / "Observer.swift"), str(SOURCE / "AutoClip.swift"), str(directory / "main.swift"),
                 "-o", str(binary)],
                capture_output=True, text=True, timeout=300)
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            output = directory / "phone"
            run = subprocess.run([str(binary), str(output)], capture_output=True, text=True, timeout=180)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
            bundles = [Path(line.split(" ", 1)[1]) for line in run.stdout.splitlines() if line.startswith("BUNDLE ")]
            self.assertIn("SAVED 2", run.stdout)
            self.assertEqual(sorted(b.name.rsplit("_", 2)[-2] for b in bundles), ["confusion", "jump"])
            self.assertEqual({b.parent for b in bundles}, {output / "AutoClips"})

            inbox = directory / "inbox"
            for bundle in bundles:
                info = report.describe(bundle)
                kind = info["trigger"]["kind"]
                self.assertEqual(info["trigger"]["frameID"], 243 if kind == "confusion" else 1501)
                if kind == "confusion":
                    self.assertEqual(info["trigger"]["competitorCount"], 1)
                    self.assertTrue(any(c["competitor"] and c["source"] == "core-halo"
                                        for c in info["triggerFrameCandidates"]["red"]))
                else:
                    self.assertGreater(info["trigger"]["jumpDistancePx"], info["trigger"]["jumpThresholdPx"])
                images = info["images"]
                self.assertLessEqual(len(images), protocol.MAX_IMAGES)
                self.assertEqual(sum(i["role"] == "event" for i in images), 3)
                for image in images:
                    width, height, rgb = report.read_png(bundle / image["path"])
                    frame = image["frameID"] - 1
                    step = 1 if image["role"] == "event" else 2
                    self.assertEqual((width, height), (480 // step, 640 // step))
                    for y in (0, 1, 5, height - 1):
                        for x in (0, 3, width - 1):
                            sx, sy = x * step, y * step
                            expected = (sx & 255, (frame >> 8) & 255, frame & 255) if sy == 0 else \
                                ((sx ^ sy) & 255, sy & 255, sx & 255)
                            # read_png returns RGB; the source bytes are B, G, R.
                            got = tuple(rgb[(y * width + x) * 3:(y * width + x) * 3 + 3])
                            self.assertEqual(got, expected, (image["path"], x, y))
                package = directory / f"{bundle.name}.psbt"
                protocol.pack_bundle(bundle, package)   # full protocol validation
                with package.open("rb") as source:
                    stored = protocol.receive_bundle(source, package.stat().st_size, inbox,
                                                     destination_for=protocol.auto_clip_destination(inbox))
                self.assertEqual(stored.parent, inbox / "autoclips")
                self.assertTrue(report.write_clip_report(stored).is_file())


if __name__ == "__main__":
    unittest.main()
