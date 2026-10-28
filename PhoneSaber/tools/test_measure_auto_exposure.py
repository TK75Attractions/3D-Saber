"""カメラ制御のSwift/Python数値parityと、PNG/飽和下限の測定規約。"""
import json
from pathlib import Path
import random
import shutil
import subprocess
import tempfile
import unittest

import measure_auto_exposure as ae

ROOT = Path(__file__).resolve().parents[2]


class MeasurementTests(unittest.TestCase):
    def test_srgb_reference_identity_and_saturated_lower_bound(self):
        self.assertAlmostEqual(ae.scale_srgb(255, .5), 187.516, places=2)
        for value in range(256):
            self.assertAlmostEqual(ae.scale_srgb(value, 1), value, places=8)
        rows = [dict(backgroundMedian=50, selectedPeak=dict(red=255, blue=230))]
        table, minimum = ae.estimate_table(rows, 250)
        self.assertEqual(minimum['red'], 160)
        self.assertIsNone(minimum['blue'])
        self.assertEqual(table[-1]['red']['saturatedFrames'], 1)
        self.assertAlmostEqual(table[-1]['red']['peakMin'], 255)

    def test_context_is_already_point_sampled_and_upper_median(self):
        import numpy as np
        from PIL import Image
        with tempfile.TemporaryDirectory() as name:
            p = Path(name)
            pixels = np.zeros((4, 4, 3), dtype=np.uint8)
            pixels[0, 0, 0], pixels[0, 2, 1] = 10, 20
            pixels[2, 0, 2], pixels[2, 2, 0] = 30, 200
            Image.fromarray(pixels).save(p / 'event.png')
            Image.fromarray(pixels[::2, ::2]).save(p / 'context.png')
            self.assertEqual(ae.background_median(p / 'event.png', 'event'), 30)
            self.assertEqual(ae.background_median(p / 'context.png', 'context'), 30)

    def test_constant_scene_has_no_reversal_and_dark_never_changes(self):
        for brightness in [20, 30, 38, 46, 47, 55, 80, 130, 200]:
            rows = [dict(time=i / 4, backgroundMedian=brightness) for i in range(481)]
            result = ae.simulate(rows, 250)
            self.assertEqual(result['lowerThenRaiseWithin8Seconds'], 0)
            self.assertFalse(any(c['reason'] == 'raise' for c in result['changes']))
            if brightness <= 46:
                self.assertEqual(result['changeCount'], 0)
            self.assertGreaterEqual(result['finalISO'], 32)
            for a, b in zip(result['changes'], result['changes'][1:]):
                self.assertGreaterEqual(b['time'] - a['time'], ae.COOLDOWN)

    def test_estimates_honour_device_floor_and_simulation_disabled_modes(self):
        rows = [dict(backgroundMedian=50, selectedPeak=dict(red=255, blue=230), time=i / 4)
                for i in range(41)]
        table, _ = ae.estimate_table(rows, 250, min_iso=80.5)
        self.assertEqual(table[0]['iso'], 80.5)
        self.assertTrue(all(row['iso'] >= 80.5 for row in table))
        for settings in [dict(enabled=False), dict(emitter=False), dict(shutter=False)]:
            self.assertEqual(ae.simulate(rows, 250, **settings)['changeCount'], 0)


@unittest.skipUnless(shutil.which('swiftc'), 'Swift compilerが必要')
class SwiftParityTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.directory = tempfile.TemporaryDirectory(prefix='phonesaber-auto-exposure-parity-')
        cls.tmp = Path(cls.directory.name)
        source = (ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift').read_text()
        start = source.index('struct AutoExposureController {')
        end = source.index('// 本番既定は1/2000秒', start)
        (cls.tmp / 'Controller.swift').write_text('import Foundation\n' + source[start:end])
        (cls.tmp / 'main.swift').write_text('''import Foundation
let scenarios = try JSONSerialization.jsonObject(with: FileHandle.standardInput.readDataToEndOfFile()) as! [[[String: Any]]]
var output: [[[String: Any]]] = []
for rows in scenarios {
    var controller = AutoExposureController()
    var results: [[String: Any]] = []
    for row in rows {
        let change = controller.observe(time: row["time"] as! Double, background: row["background"] as! Int,
            ceilingISO: (row["ceiling"] as! NSNumber).floatValue, minISO: (row["minimum"] as! NSNumber).floatValue,
            enabled: row["enabled"] as! Bool, emitter: row["emitter"] as! Bool, shutter: row["shutter"] as! Bool)
        var result: [String: Any] = ["iso": controller.appliedISO.map { Double($0) } ?? NSNull()]
        if let change {
            result["change"] = ["time": change.time, "from": Double(change.from), "to": Double(change.to),
                                "background": change.background, "reason": change.reason]
        } else { result["change"] = NSNull() }
        results.append(result)
    }
    output.append(results)
}
FileHandle.standardOutput.write(try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys]))
''')
        result = subprocess.run(['swiftc', '-O', '-module-cache-path', str(cls.tmp / 'cache'),
                                 str(cls.tmp / 'Controller.swift'), str(cls.tmp / 'main.swift'),
                                 '-o', str(cls.tmp / 'parity')], capture_output=True, text=True, timeout=120)
        if result.returncode:
            cls.directory.cleanup()
            raise RuntimeError(result.stdout + result.stderr)

    @classmethod
    def tearDownClass(cls):
        cls.directory.cleanup()

    def test_numeric_parity_all_samples_with_configuration_noise_gaps_and_reversals(self):
        randomizer = random.Random(1028)
        scenarios = []
        for minimum in [32, 34, 64, 300]:
            for brightness in [0, 20, 30, 38, 46, 47, 55, 100, 255]:
                scenarios.append([dict(time=i / 4, background=brightness if i < 240 else 20,
                                       ceiling=250 if i < 420 else 160, minimum=minimum,
                                       enabled=True, emitter=True, shutter=True) for i in range(600)])
        for disabled in ('enabled', 'emitter', 'shutter'):
            rows = [dict(time=i / 4, background=100, ceiling=250, minimum=32,
                         enabled=True, emitter=True, shutter=True) for i in range(100)]
            for row in rows:
                row[disabled] = row['time'] >= 10
            scenarios.append(rows)
        scenarios.append([dict(time=i / 4 + (10 if i > 50 else 0),
                               background=randomizer.choice([20, 30, 38, 46, 47, 100, 255]),
                               ceiling=250, minimum=32, enabled=True, emitter=True, shutter=True)
                          for i in range(1000)])
        result = subprocess.run([str(self.tmp / 'parity')], input=json.dumps(scenarios),
                                capture_output=True, text=True, timeout=60, check=True)
        actual = json.loads(result.stdout)
        for index, rows in enumerate(scenarios):
            controller = ae.AutoExposureController()
            expected = []
            for r in rows:
                change = controller.observe(r['time'], r['background'], r['ceiling'], r['minimum'],
                                             r['enabled'], r['emitter'], r['shutter'])
                expected.append(dict(iso=controller.applied_iso, change=change))
            self.assertEqual(actual[index], expected, f'scenario {index}')


if __name__ == '__main__':
    unittest.main()
