"""ISO比較の合成AutoClip/真値テスト。画像認識や実機・外部ライブラリは不要。"""
import contextlib
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

import iso_compare as iso

COLUMNS = ['rank', 'x1', 'y1', 'x2', 'y2', 'minX', 'minY', 'maxX', 'maxY',
           'score', 'flags', 'source', *iso.METRICS]
BLADE = [10, 10, 10, 110]
OTHER = [210, 10, 210, 110]


def candidate(points=BLADE, *, flags=1, mean=200, peak=250, purity=.9, hvr=.8, emitter=.7, sat=False):
    item = dict(zip(COLUMNS, [0, *points, points[0], points[1], points[2], points[3],
                              80, flags, 0, mean, peak, purity, hvr, emitter, sat]))
    return {**item, 'eligible': bool(flags & 1), 'selected': bool(flags & 4)}


def observation(frame=1, truth=BLADE, selected=BLADE, candidates=None, **extra):
    return dict(key=('A', .0005, 500., True, 'red'), clip='clip', frame=frame,
                time=frame/60, candidates=candidates if candidates is not None else
                [candidate(), candidate(OTHER, mean=100)], selected=selected,
                truth=truth, total=2, eligible=2, snapshot=False, truncated=False, **extra)


def bundle(root, name='phone_saber_triage_autoclip_20261023_120000_000_manual_both', *,
           camera=None, columns=COLUMNS):
    path = root / name
    path.mkdir()
    frame = {'f': 1, 't': 1/60}
    for color in iso.COLORS:
        frame[color] = dict(n=2, e=2, sel=BLADE, c=[
            [candidate(flags=7).get(k) for k in columns],
            [candidate(OTHER, mean=100).get(k) for k in columns]])
    summary = dict(summaryScope='autoClip', images=[], autoClip=dict(
        camera=camera if camera is not None else dict(exposureDurationSeconds=.000497,
                                                     iso=499.8, shutterISO=500, shutterEmitter=True),
        candidateColumns=columns, sources=['color-emitter'], frames=[frame]))
    (path / 'summary.json').write_text(json.dumps(summary))
    return path, summary


def save(path, summary):
    (path / 'summary.json').write_text(json.dumps(summary))


class IsoCompareTests(unittest.TestCase):
    def test_recorded_camera_grouping_not_requested_iso_and_unknown_not_off(self):
        with tempfile.TemporaryDirectory() as temp:
            path, summary = bundle(Path(temp))
            rows = list(iso.observations(path, 'A', {}))
            self.assertEqual(rows[0]['key'], ('A', .000497, 499.8, True, 'red'))
            summary['autoClip']['camera'] = dict(shutterISO=250, shutterEmitter=False)
            summary['autoClip']['frames'].append({**summary['autoClip']['frames'][0], 'f': 2,
                'camera': dict(exposureDurationSeconds=.001, iso=250, shutterEmitter=True)})
            save(path, summary)
            rows = list(iso.observations(path, 'B', {}))
            self.assertEqual(rows[0]['key'], ('B', None, None, False, 'red'))
            self.assertEqual(rows[2]['key'], ('B', .001, 250., True, 'red'))
            self.assertFalse(rows[2]['snapshot'])
            del summary['autoClip']['camera']['shutterEmitter']
            save(path, summary)
            self.assertIsNone(next(iso.observations(path, None, {}))['key'][3])

    def test_label_join_hash_and_context_recovers_thinned_frame(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            path, summary = bundle(root)
            (path / 'images').mkdir()
            (path / 'frames').mkdir()
            image = path / 'images/image_1_event_2.png'
            image.write_bytes(b'synthetic image bytes, no pixel decoding')
            label = dict(sourceClip=path.name, image=image.name,
                         red=[dict(x=10, y=10), dict(x=10, y=110)], blue='absent',
                         sha256=hashlib.sha256(image.read_bytes()).hexdigest())
            labels_path = root / 'labels.json'
            labels_path.write_text(json.dumps(dict(frames=[label])))
            labels = iso.load_labels(labels_path)
            context = dict(frames=[dict(frameID=2, timestamp=2/60, red=dict(
                candidateCount=1, eligibleCandidateCount=1, endpoint=BLADE,
                autoClipCandidates=[dict(endpoints=BLADE, bbox=[0, 0, 20, 120],
                                         eligible=True, meanValue=123, highValueRatio=.2)]))])
            (path / 'frames/2.json').write_text(json.dumps(context))
            summary['images'] = [dict(frameID=2, timestamp=2/60, path='images/'+image.name, frameContextPath='frames/2.json')]
            save(path, summary)
            rows = list(iso.observations(path, 'A', labels))
            self.assertEqual(len(rows), 4)
            self.assertEqual(rows[2]['truth'], BLADE)
            self.assertEqual(rows[2]['candidates'][0]['meanValue'], 123)
            self.assertEqual(rows[2]['selected'], BLADE)
            self.assertFalse(rows[2]['truncated'])
            self.assertEqual(rows[3]['truth'], 'absent')
            image.write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError, 'ハッシュ不一致'):
                list(iso.observations(path, 'A', labels))

    def test_corrupt_clip_is_rejected_and_cli_excludes_it_with_warning(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            path, summary = bundle(root)
            summary['autoClip']['frames'] *= 2
            save(path, summary)
            with self.assertRaisesRegex(iso.ClipIntegrityError, 'duplicate frame ID'):
                list(iso.observations(path, 'A', {}))
            with contextlib.redirect_stdout(io.StringIO()) as out, contextlib.redirect_stderr(io.StringIO()) as err:
                self.assertEqual(iso.main([str(root), '--labels', str(root/'no-labels')]), 0)
            self.assertIn('対象: 0 clips', out.getvalue())
            self.assertIn('破損クリップを除外', err.getvalue())
            summary['autoClip']['frames'].pop()
            (path/'frames').mkdir()
            (path/'frames/1.json').write_text(json.dumps(dict(frames=[dict(frameID=1, timestamp=1.22+1/60)])))
            summary['images'] = [dict(frameID=1, timestamp=1/60, path='images/1.png', frameContextPath='frames/1.json')]
            save(path, summary)
            with self.assertRaisesRegex(iso.ClipIntegrityError, 'timestamp mismatch'):
                list(iso.observations(path, 'A', {}))

    def test_evidence_margin_and_no_brightness_based_truth(self):
        blade = candidate(mean=150, emitter=None)
        competitor = candidate(OTHER, flags=0, mean=240, purity=.5, hvr=.1, sat=True)
        partial = candidate([10, 40, 10, 60], mean=255)
        group = iso.summarize([observation(candidates=[blade, competitor, partial])])
        self.assertEqual(group['blade']['meanValue'], [150])
        self.assertEqual(group['competitor']['meanValue'], [240])
        self.assertEqual(group['margins'], [-90])
        self.assertEqual(group['blade']['emitterScore'], [])
        self.assertEqual(group['competitor']['nearSaturationPresent'], [1])
        unknown = iso.summarize([observation(truth=None, candidates=[candidate(flags=7), candidate(OTHER, mean=100)])])
        self.assertEqual(unknown['margins'], [])
        self.assertEqual(unknown['proxy_margins'], [100])
        self.assertIn('未ラベル参考', iso.render({('A', .0005, 500., True, 'red'): unknown}))
        self.assertEqual(unknown['unknown'], 1)
        self.assertEqual(unknown['present'], 0)
        absent = iso.summarize([observation(truth='absent')])
        self.assertEqual(absent['missed'], 0)
        self.assertEqual(absent['competitor']['meanValue'], [200])

    def test_misses_switches_geometry_not_candidate_index_and_jitter(self):
        group = iso.summarize([observation(1), observation(2, selected=OTHER),
                               observation(3, selected=None), observation(4)])
        self.assertEqual((group['missed'], group['no_output'], group['wrong_output']), (2, 1, 1))
        self.assertEqual((group['switches'], group['switch_pairs']), (1, 1))
        self.assertEqual(group['switch_proxy'], 1)
        moving = [[10+i*2, 10, 10+i*2, 110] for i in range(3)]
        rows = [observation(i+1, truth=p, selected=p,
                            candidates=[candidate(p), candidate(OTHER)]) for i, p in enumerate(moving)]
        rows[1]['candidates'].reverse()
        smooth = iso.summarize(rows)
        self.assertEqual(smooth['switch_proxy'], 0)
        self.assertEqual(smooth['jitter'], [0, 0])
        self.assertAlmostEqual(smooth['motion_proxy'][0], 0)
        rows[1]['selected'] = [14, 10, 14, 110]
        self.assertEqual(iso.summarize(rows)['jitter'], [2, 2])

    def test_temporal_boundaries_missing_frames_times_and_conditions(self):
        for change in (dict(frame=3), dict(clip='other'), dict(time=1), dict(time=None)):
            row = observation(2, selected=OTHER)
            row.update(change)
            group = iso.summarize([observation(), row])
            self.assertEqual(group['switch_pairs'], 0)
            self.assertEqual(group['proxy_pairs'], 0)
            self.assertEqual(group['motion_proxy'], [])
        # 同じ局でも別条件はmainで別group。欠測/順序逆転を補間しない。
        self.assertIsNone(iso.number(float('nan')))
        self.assertIsNone(iso.number(True))

    def test_partial_blade_output_is_uncertain_not_a_claimed_miss(self):
        group = iso.summarize([observation(selected=[10, 40, 10, 60])])
        self.assertEqual(group['missed'], 0)
        self.assertEqual(group['uncertain_output'], 1)
        self.assertEqual(group['present'], 1)
        group = iso.summarize([observation(candidates=[candidate(), candidate(OTHER, mean=None)])])
        self.assertEqual(group['complete_margins'], [])

    def test_old_columns_missing_evidence_and_truncation_are_not_zero(self):
        with tempfile.TemporaryDirectory() as temp:
            path, summary = bundle(Path(temp), columns=COLUMNS[:12])
            row = next(iso.observations(path, 'A', {}))
            row['truth'] = BLADE
            group = iso.summarize([row])
            self.assertEqual(group['blade']['meanValue'], [])
            self.assertEqual(group['margins'], [])
        group = iso.summarize([dict(observation(), truncated=True)])
        self.assertEqual(group['margins'], [100])
        self.assertEqual(group['complete_margins'], [])
        self.assertIn('—(0)', iso.render({('A', .0005, 500., True, 'red'): group}))

    def test_recommendation_requires_both_colors_detection_and_complete_evidence(self):
        groups = {}
        for station in ('A', 'B'):
            for enabled in (True, False):
                for exposure in (.0005, .001):
                    for gain in (2000., 1000., 500., 250.):
                        for color in iso.COLORS:
                            mean = {2000: 160, 1000: 200, 500: 220, 250: 250}[gain]
                            g = iso.summarize([observation(i+1, candidates=[candidate(mean=mean),
                                candidate(OTHER, mean=100)]) for i in range(5)])
                            # 最大差のISO250は検出維持できない。
                            if gain == 250:
                                g['missed'] = 1
                            groups[station, exposure, gain, enabled, color] = g
        choices = iso.recommend(groups)
        self.assertEqual(len(choices), 8)
        self.assertTrue(all(condition[2] == 500 for _, condition in choices.values()))
        for g in groups.values():
            g['complete_margins'] = []
        self.assertEqual(iso.recommend(groups), {})
        self.assertIn('保留', iso.render(groups))

    def test_cli_date_station_dedup_and_read_only(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            path, _ = bundle(root)
            labels = root / 'empty-labels'
            labels.mkdir()
            before = {p: p.read_bytes() for p in root.rglob('*') if p.is_file()}
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(iso.main(['A='+str(root), 'A='+str(path), '--date', '2026-10-23',
                                           '--labels', str(labels)]), 0)
            self.assertIn('対象: 1 clips', output.getvalue())
            self.assertIn('499.8', output.getvalue())
            self.assertIn('ON', output.getvalue())
            self.assertEqual(before, {p: p.read_bytes() for p in root.rglob('*') if p.is_file()})
            with contextlib.redirect_stdout(io.StringIO()) as output:
                iso.main([str(root), '--date', '2026-10-22', '--labels', str(labels)])
            self.assertIn('対象クリップなし', output.getvalue())
            with self.assertRaisesRegex(ValueError, '局指定が競合'):
                iso.main(['A='+str(path), 'B='+str(path), '--labels', str(labels)])


if __name__ == '__main__':
    unittest.main()
