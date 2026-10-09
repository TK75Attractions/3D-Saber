import json
import hashlib
from pathlib import Path
import tempfile
import unittest

import jitter_analysis as jitter
import real_motion_extract as extract


class JitterTests(unittest.TestCase):
    def test_order_swap_is_separate_from_physical_translation(self):
        swap = jitter.displacement([0, 0, 100, 0], [100, 1, 0, 1])
        self.assertTrue(swap["swap_suspect"])
        self.assertEqual(1, swap["aligned"])
        self.assertEqual(1, swap["midpoint"])
        # 大きい実移動は入替扱いにしない。
        motion = jitter.displacement([0, 0, 100, 0], [100, 100, 200, 100])
        self.assertFalse(motion["swap_suspect"])
        self.assertAlmostEqual(2**.5*100, motion["aligned"])

    def test_rich_context_dedup_gaps_prediction_and_image_labels(self):
        with tempfile.TemporaryDirectory() as tmp:
            folder = Path(tmp)
            def state(x, detected=True, predicted=False):
                return dict(detected=detected, predicted=predicted, x1=x, y1=0, x2=x+100, y2=0)
            frames = [dict(frameID=i, timestamp=i/30, red=s) for i,s in
                      [(0,state(0)), (1,state(1)), (2,state(2,False)),
                       (3,state(3,True,True)), (5,state(5)), (6,state(6))]]
            metadata = folder/'private_metadata.json'
            metadata.write_text(json.dumps(dict(sessionID='private',frames=frames)))
            contexts = folder/'frames'
            contexts.mkdir()
            enriched = dict(frames[1],red=dict(frames[1]['red'],tracking=dict(candidateSwitch=True)))
            (contexts/'frame_1.json').write_text(json.dumps(dict(sessionID='private',frames=[enriched])))
            rows_before = extract.extract([metadata])[0]
            self.assertEqual(rows_before, extract.extract([folder])[0])
            raw = extract.diagnostic_frames([folder])
            self.assertEqual(6,len(raw))
            self.assertTrue(raw[('private',1,'red')]['tracking']['candidateSwitch'])
            labels = [dict(session='private',frames=[0,1],color='red',motion='static')]
            report = jitter.analyze([folder],labels)
            self.assertEqual(1,report['counts']['gap_pairs_excluded'])
            self.assertEqual(1,report['counts']['dropout_onsets'])
            self.assertEqual(1,report['counts']['recovery_onsets'])
            self.assertEqual(1,report['counts']['predicted_True'])
            self.assertEqual(2,report['counts']['measured_pairs'])
            self.assertEqual(1,report['distributions']['static']['aligned']['n'])
            self.assertEqual(1,report['distributions']['unknown']['aligned']['n'])

    def test_unknown_prediction_is_counted_not_invented(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/'old_metadata.json'
            frames = [dict(frameID=i,timestamp=i/30,red=dict(detected=True,endpoint=[i,0,i+100,0])) for i in range(2)]
            path.write_text(json.dumps(dict(frames=frames)))
            report = jitter.analyze([path])
            self.assertEqual(2,report['counts']['predicted_None'])
            self.assertEqual(1,report['counts']['unknown_prediction_pairs'])
            self.assertEqual(1,report['counts']['measured_pairs'])

    def test_raw_pca_swap_is_absorbed_by_recorded_output(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/'raw_metadata.json'
            frames = []
            for i,raw in enumerate(([0,0,100,0],[100,1,0,1])):
                frames.append(dict(frameID=i,timestamp=i/30,red=dict(detected=True,predicted=False,
                    endpoint=[0,i,100,i],selectedCandidate=dict(sourceType='core-line',
                    endpointPipeline=dict(rawPCA=raw,finalSelected=raw,robustInterval=raw)))))
            path.write_text(json.dumps(dict(frames=frames)))
            report = jitter.analyze([path])
            self.assertEqual(0,report['counts']['swap_suspects'])
            self.assertEqual(1,report['counts']['rawPCAEndpoints_swap_suspects'])
            self.assertEqual(1,report['counts']['same_body_proxy_pairs'])

    def test_labels_fail_on_changed_original(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            path = root/'frame.png'
            path.write_bytes(b'original')
            labels = [dict(images=[dict(path=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest())])]
            self.assertEqual(1,jitter.verify_labels(labels,root,root))
            path.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError,'evidence changed'):
                jitter.verify_labels(labels,root,root)


if __name__ == '__main__':
    unittest.main()
