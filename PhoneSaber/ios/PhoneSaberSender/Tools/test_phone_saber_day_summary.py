"""日次要約の時間境界・実出力・欠測・描画を合成AutoClipで検証する。"""
import contextlib
from datetime import datetime
import hashlib
import io
import json
from pathlib import Path
import tempfile
import unittest

import phone_saber_clip_report as report
import phone_saber_day_summary as day

COLUMNS = ['rank', 'x1', 'y1', 'x2', 'y2', 'minX', 'minY', 'maxX', 'maxY',
           'score', 'flags', 'source', 'peakValue', 'colorPurity', 'aspect', 'componentArea', 'exclusionReason']


def candidate(x=100, length=200, flags=7, reason='none', rank=0):
    return [rank, x, 100, x, 100 + length, x - 2, 100, x + 2, 100 + length,
            80, flags, 0, 240, .95, 8, 800, reason]


def frame(index, rows=None, selected=True, timestamp=None):
    rows = [candidate()] if rows is None else rows
    endpoint = rows[0][1:5] if selected is True and rows else None if selected is False else selected
    return dict(f=index, t=index / 10 if timestamp is None else timestamp, appliedISO=125,
                backgroundMedian=20, red=dict(c=rows, e=sum(bool(c[10] & 1) for c in rows),
                                             n=len(rows), sel=endpoint, k=0),
                blue=dict(c=[], e=0, n=0, sel=None, k=0))


def bundle(root, stamp='20261028_144800_000', frames=None):
    path = root / f'phone_saber_triage_autoclip_{stamp}_manual'
    path.mkdir()
    summary = dict(summaryScope='autoClip', sessionID=path.name.removeprefix('phone_saber_triage_'),
                   images=[], autoClip=dict(formatVersion=2, candidateColumns=COLUMNS,
                                           sources=['color-mask'], frames=frames if frames is not None else [frame(0)],
                                           frameSize=dict(width=480, height=640), camera=dict(iso=250),
                                           ignoreZones=dict(red=[[0, 0, .5, .5]], blue=[]), playArea=[0, 0, 1, 1],
                                           trigger=dict(kind='manual', frameID=0),
                                           phone=dict(gitCommit='abc123', gitDirty='false', buildTime='2026-10-28T01:02:03Z',
                                                      appBuild='20261028', station='A')))
    save(path, summary)
    return path, summary


def save(path, summary):
    (path / 'summary.json').write_text(json.dumps(summary), encoding='utf-8')


def add_image(path, summary, frame_id=0, role='context', offset=0, components=None):
    (path / 'images').mkdir(exist_ok=True)
    (path / 'frames').mkdir(exist_ok=True)
    image_path = f'images/{role}_{frame_id}.png'
    scale = .5 if role == 'context' else 1
    report.write_png(path / image_path, int(480 * scale), int(640 * scale),
                     bytes((10, 10, 10)) * int(480 * scale) * int(640 * scale))
    source = next(f for f in summary['autoClip']['frames'] if f['f'] == frame_id)
    context_frame = dict(frameID=frame_id, timestamp=source['t'], red=dict(components=components) if components else {})
    context_path = f'frames/{frame_id}.json'
    (path / context_path).write_text(json.dumps(dict(selectedFrameID=frame_id, frames=[context_frame])))
    summary['images'].append(dict(frameID=frame_id, timestamp=source['t'], path=image_path,
                                  frameContextPath=context_path, role=role, scale=scale, offsetSeconds=offset))
    save(path, summary)


class DaySummaryTests(unittest.TestCase):
    def test_time_filter_includes_until_minute_and_excludes_other_days(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for stamp in ('20261028_144759_999', '20261028_144800_000', '20261028_151959_999',
                          '20261028_152000_000', '20261029_144800_000'):
                bundle(root, stamp)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                status = day.main(['--inbox', str(root), '--date', '2026-10-28', '--since', '14:48', '--until', '15:19'])
            self.assertEqual(status, 0)
            self.assertIn('2 clips', output.getvalue())
            self.assertNotIn('144759', output.getvalue())
            self.assertIn('151959', output.getvalue())

    def test_invalid_range_and_time(self):
        for args in (['--since', '17:00', '--until', '14:00'], ['--since', '24:00'], ['--until', '1:00']):
            with self.subTest(args=args), contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as exc:
                day.main(args)
            self.assertEqual(exc.exception.code, 2)

    def test_today_default_and_open_ended_evening(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            today = datetime.now().strftime('%Y%m%d')
            bundle(root, today + '_170000_000')
            bundle(root, today + '_235959_999')
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(day.main(['--inbox', str(root), '--since', '17:00']), 0)
            self.assertIn('2 clips', output.getvalue())

    def test_jump_uses_prior_ema_and_does_not_learn_jump_length(self):
        rows = [candidate(100), candidate(290), candidate(500, 10), candidate(310)]
        # 刃長変更による中点yの移動も距離に含む。2→3は閾値200未満になる配置。
        rows[2][2], rows[2][4] = 195, 205
        with tempfile.TemporaryDirectory() as directory:
            path, _ = bundle(Path(directory), frames=[frame(i, [r]) for i, r in enumerate(rows)])
            stats = day.analyse(path)['colors']['red']
            self.assertEqual(stats['jumps'], 1)
            self.assertIn(('jump', 480, 200), stats['hotspots'])

    def test_strict_jump_threshold_and_endpoint_order(self):
        rows = [candidate(100, 10), candidate(220, 10), candidate(220, 10)]
        rows[2][1:5] = list(rows[2][3:5]) + list(rows[2][1:3])
        with tempfile.TemporaryDirectory() as directory:
            path, _ = bundle(Path(directory), frames=[frame(i, [r]) for i, r in enumerate(rows)])
            self.assertEqual(day.analyse(path)['colors']['red']['jumps'], 0)

    def test_actual_sel_overrides_stale_flags_and_dropout_runs(self):
        frames = [frame(i, selected=selected) for i, selected in enumerate((False, True, False, False, True, False))]
        with tempfile.TemporaryDirectory() as directory:
            path, _ = bundle(Path(directory), frames=frames)
            stats = day.analyse(path)['colors']['red']
            self.assertEqual((stats['selected'], stats['frames'], stats['dropouts'], stats['missing']), (2, 6, 2, 4))

    def test_discontinuity_does_not_create_jump_or_dropout(self):
        frames = [frame(0), frame(10, [candidate(450)]), frame(11, selected=False, timestamp=10)]
        with tempfile.TemporaryDirectory() as directory:
            path, _ = bundle(Path(directory), frames=frames)
            stats = day.analyse(path)['colors']['red']
            self.assertEqual((stats['jumps'], stats['dropouts'], stats['discontinuities']), (0, 0, 2))

    def test_ignore_is_only_counted_for_recorded_eligible_reasons(self):
        rows = [candidate(flags=1, reason='ignore:0,1'), candidate(flags=0, reason='ignore:0'),
                candidate(flags=1, reason='outsidePlayArea'), candidate(flags=1, reason='unknown')]
        with tempfile.TemporaryDirectory() as directory:
            path, _ = bundle(Path(directory), frames=[frame(0, rows, selected=False)])
            stats = day.analyse(path)['colors']['red']
            self.assertEqual(stats['ignored'], 1)
            self.assertEqual(stats['unknown_exclusion'], 1)
            self.assertEqual(stats['zones'], {0: 1, 1: 1})

    def test_legacy_missing_reasons_and_build_identity_stay_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            path, summary = bundle(Path(directory))
            clip = summary['autoClip']
            clip['phone'] = {'appBuild': 'old'}
            clip['candidateColumns'] = COLUMNS[:-1]
            clip['frames'][0]['red']['c'][0] = candidate(flags=1)[:-1]
            save(path, summary)
            result = day.analyse(path)
            self.assertEqual(result['colors']['red']['unknown_exclusion'], 1)
            self.assertEqual(result['colors']['red']['ignored'], 0)
            text = day.render([result], datetime(2026, 10, 28), datetime(2026, 10, 29))
            self.assertIn('unknown / dirty=unknown / unknown (appBuild=old', text)
            self.assertIn("{'unknown': 1}", text)

    def test_component_details_override_compact_snapshot_once_and_exclude_passed(self):
        with tempfile.TemporaryDirectory() as directory:
            path, summary = bundle(Path(directory))
            summary['autoClip']['frames'][0]['red']['components'] = dict(state='collected', omitted=2)
            detailed = dict(state='collected', omitted=2, columns=['path', 'stage', 'reason'],
                            rows=[['color-mask', 'component', 'aspect'], ['color-mask', 'component', 'passed']])
            add_image(path, summary, components=detailed)
            stats = day.analyse(path)['colors']['red']
            self.assertEqual(stats['reasons'], {('color-mask', 'component', 'aspect'): 1})
            self.assertEqual(stats['omitted'], 2)
            summary['autoClip']['componentDiagnostics'] = dict(enabled=False)
            save(path, summary)
            stats = day.analyse(path)['colors']['red']
            self.assertEqual(stats['component_states'], {'notCollected': 1})
            self.assertFalse(stats['reasons'])

    def test_stationary_uses_seconds_not_frame_count_and_ignores_rank(self):
        frames = [frame(i, [candidate(rank=i)]) for i in range(17)]
        with tempfile.TemporaryDirectory() as directory:
            path, summary = bundle(Path(directory), frames=frames)
            runs = day.analyse(path)['colors']['red']['stationary']
            self.assertEqual(len(runs), 1)
            self.assertAlmostEqual(runs[0]['end'] - runs[0]['start'], 1.6)
            self.assertEqual(runs[0]['selected_frames'], 17)
            summary['autoClip']['frames'] = frames[:16]
            save(path, summary)
            self.assertEqual(len(day.analyse(path)['colors']['red']['stationary']), 1)
            summary['autoClip']['frames'] = frames[:15]
            save(path, summary)
            self.assertFalse(day.analyse(path)['colors']['red']['stationary'])

    def test_stationary_resets_on_missing_eligible_and_avoids_slow_drift(self):
        with tempfile.TemporaryDirectory() as directory:
            frames = [frame(i) for i in range(30)]
            path, summary = bundle(Path(directory), frames=frames)
            frames[14]['red']['e'] = 2
            save(path, summary)
            self.assertFalse(day.analyse(path)['colors']['red']['stationary'])
            summary['autoClip']['frames'] = [frame(i, [candidate(100 + i * 5)]) for i in range(30)]
            save(path, summary)
            self.assertFalse(day.analyse(path)['colors']['red']['stationary'])

    def test_hotspot_and_evidence_and_median(self):
        with tempfile.TemporaryDirectory() as directory:
            frames = [frame(i) for i in range(3)]
            for f, iso, bg in zip(frames, [100, 125, 250], [1, 20, 90]):
                f['red']['k'] = 1
                f['appliedISO'], f['backgroundMedian'] = iso, bg
            path, _ = bundle(Path(directory), frames=frames)
            result = day.analyse(path)
            self.assertEqual((result['iso'], result['background']), (125, 20))
            self.assertEqual(len(result['colors']['red']['hotspots'][('competition', 80, 200)]), 3)
            text = day.render([result], datetime(2026, 10, 28), datetime(2026, 10, 29))
            self.assertIn('source=color-mask, peak=240, purity=0.95', text)
            self.assertIn('aspect=8, area=800', text)
            self.assertIn('| red | 3/3（100.0%）', text)
            self.assertIn('| blue | 0/3（0.0%）', text)
            self.assertIn('abc123 / dirty=false', text)

    def test_representative_context_uses_scale_and_sel_even_without_candidates(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            frames = [frame(0), frame(1, [candidate(200)], selected=[200, 100, 200, 300])]
            frames[1]['red']['c'] = []
            path, summary = bundle(root, frames=frames)
            add_image(path, summary, 0, 'event', 0)
            add_image(path, summary, 1, 'context', .1)
            before = {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in path.rglob('*') if p.is_file()}
            result = day.analyse(path)
            output = root / 'representative.png'
            self.assertEqual(day.representative_image(result['model'], output), 1)
            width, height, pixels = report.read_png(output)
            self.assertEqual((width, height), (240, 320))
            index = (100 * width + 100) * 3
            self.assertEqual(pixels[index:index + 3], bytes((255, 70, 70)))
            self.assertEqual(before, {p: hashlib.sha256(p.read_bytes()).hexdigest() for p in path.rglob('*') if p.is_file()})

    def test_incomplete_arrival_reports_error_and_nonzero_status(self):
        with tempfile.TemporaryDirectory() as directory:
            path, _ = bundle(Path(directory))
            (path / 'summary.json').write_text('{')
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                status = day.main(['--inbox', directory, '--date', '2026-10-28'])
            self.assertEqual(status, 2)
            self.assertIn('読み取りエラー（集計未完了）', output.getvalue())

    def test_no_clips_and_image_failure_are_explicit(self):
        with tempfile.TemporaryDirectory() as directory:
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(day.main(['--inbox', directory]), 0)
            self.assertIn('0 clips', output.getvalue())
            path, _ = bundle(Path(directory))
            errors = []
            text = day.render([day.analyse(path)], datetime(2026, 10, 28), datetime(2026, 10, 29),
                              Path(directory) / 'output', errors)
            self.assertTrue(errors)
            self.assertIn('保存画像なし', text)

    def test_cli_images_and_read_only_bundle_guard(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path, summary = bundle(root)
            add_image(path, summary)
            output = io.StringIO()
            args = ['--inbox', directory, '--date', '2026-10-28', '--images']
            with contextlib.redirect_stdout(output):
                self.assertEqual(day.main(args + [str(root / 'output')]), 0)
            self.assertIn('![context]', output.getvalue())
            self.assertTrue((root / 'output' / (path.name + '.png')).is_file())
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(day.main(args + [str(path / 'output')]), 2)
            self.assertFalse((path / 'output').exists())

    def test_missing_inbox_and_frame_integrity_fail_explicitly(self):
        with tempfile.TemporaryDirectory() as directory:
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(day.main(['--inbox', str(Path(directory) / 'missing')]), 2)
            path, summary = bundle(Path(directory))
            summary['autoClip']['frames'] *= 2
            save(path, summary)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                self.assertEqual(day.main(['--inbox', directory, '--date', '2026-10-28']), 2)
            self.assertIn('duplicate frame ID', output.getvalue())


if __name__ == '__main__':
    unittest.main()
