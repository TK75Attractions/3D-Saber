"""原寸全frameの転送・全候補replay・日次描画と破損拒否を小さなfixtureで検証。"""
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import phone_saber_fullres as full
import phone_saber_triage_protocol as protocol
import phone_saber_clip_report as report
import phone_saber_stationary_replay as replay
import phone_saber_day_summary as day

COLUMNS = ['rank', 'x1', 'y1', 'x2', 'y2', 'minX', 'minY', 'maxX', 'maxY',
           'score', 'flags', 'source', 'peakValue', 'emitterScore', 'highValueRatio',
           'meanValue', 'nearSaturationPresent', 'colorPurity', 'componentArea', 'aspect', 'exclusionReason']


def fixture(root):
    root.mkdir()
    rows = []
    for i in range(9):
        candidates = [[rank, 0, 0, 1, 1, 0, 0, 1, 1, 12.3456789, 0, 'shutter-color-emitter',
                       200, .1, .2, 100, False, .9, 4, 2, 'ineligible'] for rank in range(20)]
        rows.append(dict(f=i + 1, t=i / 2, appliedISO=50 + i, exposureDurationSeconds=.0005,
                         cameraMetadataSource='exif', backgroundMedian=20,
                         red=dict(n=20, e=0, k=0, sup=False, sel=None, c=candidates),
                         blue=dict(n=0, e=0, k=0, sup=False, sel=None, c=[])))
    full_info = dict(version=1, state='captured', pixelFormat='BGRA8', pixelsPath='fullres.bgra',
                     metadataPath='fullres.jsonl', width=2, height=2, bytesPerRow=8, frameBytes=16,
                     frameCount=9, startTimestamp=0, endTimestamp=4, captureFPS=2,
                     missingSequenceFrames=0, preComplete=True, postComplete=True, complete=True,
                     candidateColumns=COLUMNS)
    summary = dict(formatVersion=1, summaryScope='autoClip', sessionID='autoclip_20261029_120000_000_gap_red',
                   selectedImageCount=0, images=[], incidentCount=1,
                   redBlueDetectionSummary=dict(red={}, blue={}), dropoutSummary=dict(red={}, blue={}),
                   fullResolution=full_info, autoClip=dict(formatVersion=2, candidateColumns=COLUMNS,
                       sources=[], frames=[], frameSize=dict(width=2, height=2),
                       trigger=dict(kind='gap', frameID=5, timestamp=2), window={}, camera={}))
    (root / 'summary.json').write_text(json.dumps(summary))
    (root / 'prompt.md').write_text('raw fixture')
    (root / 'fullres.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in rows))
    (root / 'fullres.bgra').write_bytes(bytes([10, 20, 30, 255]) * 4 * 9)
    return summary, rows


class FullResolutionTests(unittest.TestCase):
    def test_psbt_roundtrip_duplicate_and_exact_raw_bytes(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            source = root / 'source'
            summary, _ = fixture(source)
            package = root / 'bundle.psbt'
            protocol.pack_bundle(source, package)
            data = package.read_bytes()
            received = protocol.receive_bundle(io.BytesIO(data), len(data), root / 'inbox',
                                                destination_for=protocol.auto_clip_destination(root / 'inbox'))
            self.assertEqual(received.parent.name, 'autoclips')
            for name in full.FILES:
                self.assertEqual((source / name).read_bytes(), (received / name).read_bytes())
            self.assertEqual(len(full.read_frames(received, summary)), 9)
            with self.assertRaises(protocol.DuplicateBundle):
                protocol.receive_bundle(io.BytesIO(data), len(data), received.parent)

    def test_all_candidates_exposure_and_replay_without_summary_thinning(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'fixture'
            fixture(path)
            model = report.load_clip(path)
            self.assertEqual(len(model['frames']), 9)
            self.assertEqual(len(report.frame_candidates(model, 5, 'red')), 20)
            self.assertEqual(model['frames'][5]['exposureDurationSeconds'], .0005)
            self.assertEqual(model['frames'][5]['appliedISO'], 54)
            self.assertEqual(model['frames'][5]['red']['c'][0][9], 12.3456789)
            self.assertEqual(len(replay.load_clip(path)['frames'][0]['observed']['red']), 20)
            self.assertEqual(day.analyse(path)['colors']['red']['frames'], 9)
            self.assertEqual(full.read_rgb(path, model['summary']['fullResolution'], 0), bytes([30, 20, 10]) * 4)

    def test_png_export_all_frames_is_lossless_and_does_not_modify_input(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root / 'fixture'
            fixture(path)
            original = (path / 'fullres.bgra').read_bytes()
            output = root / 'pngs'
            with mock.patch('sys.argv', ['fullres', str(path), '--output', str(output)]), mock.patch('builtins.print'):
                full.main()
            images = sorted(output.glob('*.png'))
            self.assertEqual(len(images), 9)
            self.assertEqual(report.read_png(images[0]), (2, 2, bytearray([30, 20, 10]) * 4))
            self.assertEqual((path / 'fullres.bgra').read_bytes(), original)

    def test_rejects_truncation_wrong_paths_candidate_loss_and_duplicate_id(self):
        mutations = [lambda info, rows: info.update(pixelsPath='../escape'),
                     lambda info, rows: info.update(frameCount=10),
                     lambda info, rows: info.update(frameBytes=15),
                     lambda info, rows: rows[0]['red'].update(n=21),
                     lambda info, rows: rows[1].update(f=1),
                     lambda info, rows: rows[0].pop('appliedISO'),
                     lambda info, rows: rows[1].update(t=float('nan')),
                     lambda info, rows: info.update(complete=False)]
        for mutate in mutations:
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                path = root / 'fixture'
                summary, rows = fixture(path)
                mutate(summary['fullResolution'], rows)
                (path / 'summary.json').write_text(json.dumps(summary))
                (path / 'fullres.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in rows))
                with self.assertRaises(ValueError):
                    full.read_frames(path, summary)
                with self.assertRaises(protocol.BundleError):
                    protocol.pack_bundle(path, root / 'bad.psbt')

    def test_missing_frame_is_explicit_and_replay_resets(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'fixture'
            summary, rows = fixture(path)
            rows.pop(4)
            summary['fullResolution'].update(frameCount=8, missingSequenceFrames=1, complete=False)
            (path / 'fullres.bgra').write_bytes((path / 'fullres.bgra').read_bytes()[:-16])
            (path / 'summary.json').write_text(json.dumps(summary))
            (path / 'fullres.jsonl').write_text(''.join(json.dumps(row) + '\n' for row in rows))
            self.assertEqual(len(full.read_frames(path, summary)), 8)
            self.assertTrue(replay.load_clip(path)['frames'][4]['reset'])

    def test_old_clip_and_explicit_not_captured_remain_readable(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'fixture'
            summary, _ = fixture(path)
            for name in full.FILES:
                (path / name).unlink()
            for description in (None, dict(state='notCaptured', reason='hourly limit')):
                summary['fullResolution'] = description
                (path / 'summary.json').write_text(json.dumps(summary))
                self.assertEqual(full.read_frames(path, summary), [])
                self.assertEqual(replay.load_clip(path)['frames'], [])

    def test_unreferenced_raw_and_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / 'fixture'
            summary, _ = fixture(path)
            summary.pop('fullResolution')
            with self.assertRaises(ValueError):
                full.read_frames(path, summary)
            summary, _ = json.loads((path / 'summary.json').read_text()), None
            (path / 'fullres.bgra').unlink()
            (path / 'fullres.bgra').symlink_to(path / 'prompt.md')
            with self.assertRaises(ValueError):
                full.read_frames(path, summary)


if __name__ == '__main__':
    unittest.main()
