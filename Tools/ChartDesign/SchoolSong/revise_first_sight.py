"""校歌v2の元譜面とprovenance_hard.jsonから初見改訂を再現する。

python revise_first_sight.py --chart <v2/chart_hard.json> --provenance <v2/provenance_hard.json> --output <folder>
元データは変更せず、指定された出力先だけに書く。
"""
import argparse
import collections
import copy
import json
from pathlib import Path

def revise(chart, original_rows):
    assert chart['notes'] == [r['note'] for r in original_rows], '元の制作記録と異なる譜面'
    school, rows = copy.deepcopy((chart, original_rows))
    groups = collections.Counter(n['time'] for n in school['notes'])
    protected = {i for i,r in enumerate(rows) if r['note']['count'] > 1 or groups[r['note']['time']] > 1 or r.get('counterpoint')}
    kept = set(protected)
    removed = []

    def conflict(a, b):
        # 金はどちらの手で取ってもよい。担当を暗記しないと成立しない回復を残さない。
        if a['hand'] != b['hand'] and a['note']['color'] != 'gold' and b['note']['color'] != 'gold':
            return False
        x,y = sorted((a['note'], b['note']), key=lambda n:n['time'])
        return y['time']-x['time']-x['lengthMs'] < 400-0.001

    # メロディ(優先度6)、主要伴奏(5)、細かい装飾(3)の順に残す。
    for i in sorted(set(range(len(rows)))-protected,
                    key=lambda i:(-rows[i]['priority'], abs(rows[i]['local']-round(rows[i]['local'])) > .001, rows[i]['note']['time'])):
        if any(conflict(rows[i], rows[j]) for j in kept):
            removed.append(i)
        else:
            kept.add(i)
    newrows = [copy.deepcopy(r) for i,r in enumerate(rows) if i in kept]
    relax = set()
    for hand in ('blue','red'):
        seq = [(i,r) for i,r in enumerate(newrows) if r['hand']==hand]
        for (ai,a),(bi,b) in zip(seq,seq[1:]):
            an,bn = a['note'],b['note']
            if an['time'] == bn['time']:
                continue
            if bn['time']-an['time']-an['lengthMs'] < 600:
                # 強和音の方向と一振りスタックは保護する。
                if groups[an['time']]==1: relax.add(ai)
                if groups[bn['time']]==1: relax.add(bi)
    for i,r in enumerate(newrows):
        n=r['note']
        if i in relax and n['direction']!='none':
            n.update(direction='none',type='tap')
            r['firstSightChange']='短い間隔の構え直しを自由な振りにする'
        if n['count']>1:
            n['count']=min(n['count'], int((n['lengthMs']+.1)//400)+1)
    # 装飾を間引くと元の往復の片側が消える。残った矢印を同方向へ振り直させない。
    vectors = {'up':(0,1),'down':(0,-1),'left':(-1,0),'right':(1,0),
               'upleft':(-1,1),'upright':(1,1),'downleft':(-1,-1),'downright':(1,-1)}
    for hand in ('blue','red'):
        seq = [r for r in newrows if r['note']['color'] in (hand,'gold')]
        for a,b in zip(seq,seq[1:]):
            an,bn=a['note'],b['note']
            gap=bn['time']-an['time']-an['lengthMs']
            if 0<gap<1200 and an['direction'] in vectors and bn['direction'] in vectors:
                if sum(x*y for x,y in zip(vectors[an['direction']],vectors[bn['direction']]))>0:
                    chosen = b if groups[bn['time']]==1 else a
                    assert groups[chosen['note']['time']]==1, '保護する和音同士の矛盾'
                    chosen['note'].update(direction='none',type='tap')
                    chosen['firstSightChange']='間引き後の同方向の振り直しを自由タップへ'
    school['notes']=[r['note'] for r in newrows]
    school['_comment'] += ' 初見改訂2026-10-03: 同手400ms未満の装飾を整理し、600ms未満の単発の方向拘束を緩和。'
    return school, newrows, [rows[i] for i in sorted(removed)]

if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--chart',type=Path,required=True)
    parser.add_argument('--provenance',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    result=revise(json.loads(args.chart.read_text(encoding='utf-8-sig')),
                  json.loads(args.provenance.read_text(encoding='utf-8-sig')))
    args.output.mkdir(parents=True,exist_ok=True)
    for name,value in zip(('chart_hard.json','provenance_hard.json','removed.json'),result):
        (args.output/name).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
