"""Hardのみを改訂。Easy/Normal/互換chart.jsonは読み取り専用で保護する。"""
import json
import math
import hashlib
from pathlib import Path
from collections import Counter
import numpy as np
import build_yurikago as base

OUT=base.OUT/'HardV2'
OUT.mkdir(parents=True,exist_ok=True)
ROOT=base.ROOT

# 2打の長短ループをやめ、音源の3連を丸ごと取る/抜く/食い込む単位で組み直す。
base.HARD=[
 [3,6,9],[0,3,4,5,9,11],[0,2,3,6,8,9,10,11],[0,3,4,5,6,9],
 [0,1,2,6,9,10,11],[0,3,5,6,8,9,10,11],
 [0,1,2,3,6,8,9,10,11],[0,3,4,5,7,9,10,11],[0,1,2,4,6,7,8,9,11],
 [0,2,3,5,6,11],[0,3,4,5,8,9,10,11],[0,1,2,3,6,9,10,11],
 [0,1,2,3,6,8,9,11],[0,3,5,6,7,8,9],[0,2,3,4,5,6,9,10,11],[0,3,6,7,8,9,10,11],
 [0,1,2,3,5,6,7,8,9,11],[0,2,3,4,5,6,9,10,11],[0,1,2,3,6,7,8,9,10,11],
 [0,3,6,8,9,11],[0,1,2,3,5,6,7,8,10,11],[0,1,2,3,4,5,6,9,10,11],
 [0,3,6,9,10],[0,2,3,6,8,9,11],[0,3,5,6,7,8,9,11],
 [0,1,2,3,5,6,9,10,11],[0,3,4,5,6,8,9,11],[0,1,2,3,6,7,8,9,10,11],[0,2,3,4,5,6,9,10,11],
 [0,3,4,5,7,9,10],[0,1,2,3,6,8,9,11],[0,3,5,6,7,8,9],
 [0,3,6,9],[0,3,5,6,7,8,9,10,11],
 [0,1,2,3,5,6,7,8,9,11],[0,1,2,3,4,5,6,8,9,10,11],
 [0,2,3,4,5,6,7,8,9,10,11],[0,1,2,3,6,7,8,9,10,11],
 [0,1,2,3,5,6,7,8,9,11],[0,1,2,3,4,5],
 [0,3,4,5,6,9],[0,1,2,6,9],[0,3,6,7,8,9],[0],
]
base.LONGS['hard']={3:(6,5),48:(3,3),114:(3,3),159:(3,3),180:(3,3),222:(3,3),228:(6,5),
                    264:(6,4),282:(6,4),315:(3,3),330:(3,3),360:(3,3),384:(3,3),396:(3,3),
                    426:(3,3),444:(3,3),498:(3,3),516:(4,3)}
base.CHORDS['hard']={72,75,84,96,99,108,120,129,132,144,150,168,174,192,195,204,210,216,219,
                    240,243,249,252,258,261,294,300,303,312,318,324,327,336,339,348,354,366,372,
                    402,405,408,414,417,420,423,432,435,438,441,450,453,456,459,462,465,480,492,504}

# ハイハット/フィルの立ち上がりが確認できた場所だけを半パルス(約174ms)へ細分。
# 3連→休符、5連、1打→3連、4連を使い分け、速さ自体も固定リズムにしない。
BURSTS={87:[0,.5,1,2],105:[0,.5,1,1.5,2],153:[0,1,1.5,2],177:[0,.5,1],
        189:[0,.5,1,1.5,2],237:[0,1,1.5,2],255:[0,.5,1,1.5],309:[0,1,1.5,2],
        333:[0,.5,1,1.5,2],411:[0,.5,1,2],429:[0,.5,1,1.5,2],447:[0,1,1.5,2],471:[0,.5,1,1.5,2]}


def rhythm_stats(notes):
    unique=sorted({round(n['time']/base.PULSE_MS,4) for n in notes})
    occupied={int(p//3) for p in unique}
    paired=0
    for beat in occupied:
        local=tuple(round(p-beat*3,3) for p in unique if beat*3<=p<(beat+1)*3)
        paired+=local==(0,1)
    masks=[]
    for bar in range(44):
        masks.append(tuple(round(p-bar*12,3) for p in unique if bar*12<=p<(bar+1)*12))
    intervals=Counter(round((b-a)*base.PULSE_MS,1) for a,b in zip(unique,unique[1:]))
    run=longest=0
    for a,b in zip(masks,masks[1:]):
        run=run+1 if a==b else 0
        longest=max(longest,run)
    return dict(two_hit_swing_beats=paired,occupied_beats=len(occupied),
                two_hit_swing_share=round(paired/max(1,len(occupied)),4),distinct_bar_rhythms=len(set(masks)),
                max_identical_bar_run=longest+1,interval_histogram_ms=dict(intervals),
                half_pulse_hits=sum(abs(p-round(p))>.01 for p in unique))


def build():
    chart,rows=base.make_chart('hard',filter_weak_hard=False)
    for start,pattern in BURSTS.items():
        sample=next(r for r in rows if r['pulse']>=start)
        rows=[r for r in rows if not start<=r['pulse']<=start+2]
        for d in pattern:
            p=start+d
            n=dict(sample['note'])
            n.update(time=round(p*base.PULSE_MS,4),beat=round(p/3,7),type='tap',direction='none',
                     count=1,lengthMs=0.0,color='blue')
            rows.append(dict(pulse=p,hand='blue',section=base.section_at(p)[2],note=n,
                             rationale='audio-supported-short-roll',burst=start))
    rows.sort(key=lambda r:(r['pulse'],r['note']['x']))
    groups=[]
    for r in rows:
        if not groups or groups[-1][0]['pulse']!=r['pulse']:groups.append([])
        groups[-1].append(r)
    previous='red';chord_index=0;count_by_hand=Counter();last_flick={'blue':'down','red':'down'}
    for g in groups:
        p=g[0]['pulse'];chord=len(g)==2
        hands=['blue','red'] if chord else [('blue' if previous=='red' else 'red')]
        burst='burst' in g[0]
        for r,hand in zip(g,hands):
            n=r['note'];r['hand']=hand;sign=-1 if hand=='blue' else 1
            if n['color']!='gold':n['color']=hand
            n['x']=round(abs(n['x'])*sign,6)
            # 速い連打は手首で追える内側の小さな弧。速度と大移動を同時に要求しない。
            if burst:
                k=BURSTS[r['burst']].index(p-r['burst'])
                n['x']=round(sign*(.62 if k%2==0 else .85),6)
                n['y']=round([.214286,.428571,.642857,.428571,.214286][min(k,4)],6)
                final=k==len(BURSTS[r['burst']])-1
                if final:
                    n['color']='gold'
                    n.update(type='direction',direction='up')
            if n['type']=='direction':
                if burst:n['direction']='up'
                else:
                    # 左右の外向きと上下の切り返しをフレーズ単位で使う。
                    d='up' if last_flick[hand] in ['down','downleft','downright'] else 'down'
                    if int(p)%12==6 and int(p//12)%3==0:d='left' if hand=='blue' else 'right'
                    if int(p)%12==9 and int(p//12)%4==0:d='upleft' if hand=='blue' else 'upright'
                    n['direction']=d
                last_flick[hand]=n['direction']
            count_by_hand[hand]+=1
        if chord:
            previous='blue' if chord_index%2 else 'red';chord_index+=1
        else:previous=hands[-1]
    # 速い流れへ入る前後も含め、担当手の移動速度を抑制する。
    previous_notes={}
    for r in rows:
        n=r['note'];hand=r['hand']
        if hand in previous_notes:
            prev=previous_notes[hand];dt=(n['time']-prev['time'])/1000
            distance=math.hypot(n['x']-prev['x'],n['y']-prev['y'])
            limit=min(2.8,dt*6.0)
            if distance>limit and dt>0:
                ratio=limit/distance
                n['x']=round(prev['x']+(n['x']-prev['x'])*ratio,6)
                n['y']=round(prev['y']+(n['y']-prev['y'])*ratio,6)
        previous_notes[hand]=n
    chart.update(_comment='揺籠 Hard revision 2 / Lv.9. Phrase-based triplet groups, syncopations, rests and audio-supported 174ms bursts. ElDorado-only reference. Normal and Easy unchanged.',
                 displayLevel=9,notes=[r['note'] for r in rows])
    return chart,rows


if __name__=='__main__':
    song=ROOT/'Assets/StreamingAssets/Songs/揺籠'
    protected={name:hashlib.sha256((song/name).read_bytes()).hexdigest() for name in ['chart_easy.json','chart_normal.json','chart.json']}
    baseline=ROOT/'Tools/ChartDesign/Backups/Yurikago-hard-before-v2-20260905/chart_hard.json'
    if not baseline.exists():baseline=song/'chart_hard.json'
    old=json.loads(baseline.read_text(encoding='utf-8-sig'))
    chart,rows=build()
    validation=base.validate(chart,rows)
    before,after=rhythm_stats(old['notes']),rhythm_stats(chart['notes'])
    assert after['two_hit_swing_share']<before['two_hit_swing_share']*.5,(before,after)
    assert after['distinct_bar_rhythms']>=30,after
    assert after['half_pulse_hits']>=16,after
    features=np.load(base.OUT/'audio-features.npz')
    support=[]
    for r in rows:
        p=r['pulse']
        if p%1==0:continue
        near=abs(features['times']-p*base.PULSE_MS/1000-.012)<.045
        strength=float(features['onset'][near].max())
        assert strength>.25,(p,strength)
        support.append(dict(pulse=p,time=round(p*base.PULSE_MS/1000+.012,4),onset_strength=round(strength,3)))
    summary=dict(display_level=9,previous=before,revised=after,stats=base.stats(chart,rows),validation=validation,
                 protected_sha256=protected,half_pulse_audio_support=support)
    (OUT/'chart_hard.json').write_text(json.dumps(chart,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (OUT/'authoring_hard.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (OUT/'validation.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(summary,ensure_ascii=False,indent=2))
