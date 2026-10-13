"""第2稿の録画イベント・ロングの切断経路・追加単発の画面上の重なりを照合する。"""
import argparse
import collections
import csv
import hashlib
import json
import xml.etree.ElementTree as ET
from pathlib import Path


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--capture', type=Path, required=True)
    parser.add_argument('--tests', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).parent
    repo = root.parents[3]
    chart_path = repo/'Assets/StreamingAssets/Songs/製作中/chart_hard.json'
    chart = json.loads(chart_path.read_text(encoding='utf-8'))
    plan_path = root/'cut-plan.json'
    plan = json.loads(plan_path.read_text(encoding='utf-8'))
    expected, actual = collections.defaultdict(list), collections.defaultdict(list)
    for n, p in zip(chart['notes'], plan['notes']):
        for k, t in enumerate(p['timesMs']):
            expected[(round(n['x'], 3), round(n['y'], 3), k, n['count'])].append((t+chart['offsetMs'])/1000)
    events = list(csv.DictReader((args.capture/'events.csv').open(encoding='utf-8')))
    cuts = [r for r in events if r['kind'] in ('tap', 'flick', 'gold', 'tick', 'finish')]
    for r in cuts:
        actual[(round(float(r['x']), 3), round(float(r['y']), 3), int(r['index']), int(r['total']))].append(float(r['songTime']))
    assert expected.keys() == actual.keys(), '切断位置・回数の組み合わせが不一致'
    errors=[]
    for key, times in expected.items():
        assert len(times) == len(actual[key]), ('切断数', key)
        errors += [abs(a-b)*1000 for a,b in zip(sorted(times), sorted(actual[key]))]
    assert max(errors) < 1, max(errors)
    assert sum(int(r['total']) == 50 for r in cuts) == 100
    visible = list(csv.DictReader((args.capture/'visibility.csv').open(encoding='utf-8')))
    assert len({round(float(r['tapHitTime']), 3) for r in visible}) == 8
    assert len(visible) >= 200
    body = max(float(r['bodyCoverage']) for r in visible)
    label = max(float(r['labelCoverage']) for r in visible)
    # TMPの余白・縁を含む外接矩形では1%未満を許容する。本体の遮蔽と画面外は許容しない。
    assert body < .001 and label < .01, ('投影矩形の重なり', body, label)
    assert all(r['inFrame'] == 'True' for r in visible)
    tests = ET.parse(args.tests).getroot()
    capture = ET.parse(args.capture/'capture-results.xml').getroot()
    assert tests.get('result') == capture.get('result') == 'Passed'
    report = dict(revision=2, songId='製作中', difficulty='hard', expectedNotes=len(chart['notes']),
                  expectedCuts=sum(n['count'] for n in chart['notes']), observedCuts=len(cuts),
                  maximumCutTimeErrorMs=round(max(errors), 6), long50CutEvents=100,
                  chartSha256=hashlib.sha256(chart_path.read_bytes()).hexdigest(),
                  cutPlanSha256=hashlib.sha256(plan_path.read_bytes()).hexdigest(),
                  eventTypes=dict(collections.Counter(r['kind'] for r in cuts)),
                  editModeTestsPassed=int(tests.get('passed')), playModeCapturePassed=True,
                  visibility=dict(extraTaps=8, framesChecked=len(visible),
                                  maximumBodyCoverage=body, maximumCountLabelCoverage=label, allInsideViewport=True),
                  verificationScope='Scripted judgments and projected renderer bounds in captured camera view; not physical input or all gold-hand choices')
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__': main()
