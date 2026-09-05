"""揺籠の3譜面。音源解析+手設計フレーズ、参照譜面はElDoradoのみ。"""
import json
import math
import hashlib
from pathlib import Path
from collections import Counter
import numpy as np

OUT = Path(__file__).resolve().parents[2] / 'Outputs/YurikagoCharts'
ROOT = Path(r'C:\Users\shike\dev\3D-Saber')
PULSE_MS = 60000 / 172
BPM = 172 / 3
OFFSET_MS = 12
PULSES = json.loads((OUT/'pulse-analysis.json').read_text(encoding='utf-8'))
REFERENCE = json.loads((OUT/'reference-analysis.json').read_text(encoding='utf-8'))

# 区間境界は低域/高域のエネルギー変化と音の立ち上がりから決定。
# Aメロ/サビ等の名称は解析上の便宜的なラベルで、歌詞やステムからの断定ではない。
SECTIONS = [
    (0,24,'intro','導入・余白'), (24,72,'opening','冒頭の主旋律'),
    (72,108,'hook','最初の厚いフレーズ'), (108,120,'break1','引き算の応答'),
    (120,144,'verse','リズム復帰'), (144,192,'build','展開を積み上げる'),
    (192,228,'chorus1','第1ピーク前半'), (228,240,'breath','短い抜き'),
    (240,264,'chorus1b','第1ピーク後半'), (264,293,'interlude','間奏'),
    (293,342,'chorus2','第2ピーク・食い込みから入る'),
    (342,378,'bridge','中間の整理'), (378,401,'drop','高域が落ちる静かな区間'),
    (401,402,'pickup','終盤直前の1パルスのフィル'),
    (402,474,'final','最大ピーク'), (474,480,'release','伴奏が抜ける余韻'),
    (480,522,'outro','導入の揺れへ戻る'), (522,528,'tail','音の減衰のみ'),
]


def section_at(p):
    return next(s for s in SECTIONS if s[0] <= p < s[1])


# 各行は4拍=12パルス。定常拍を並べるのではなく、区間ごとの音価を編集する。
# Easyは大きな揺れ、Normalは主拍と応答、Hardは三連の内声まで拾う。
EASY = [
    [3,9],[0,6],[0,6],[3,9],[0,6],[3,9],
    [0,6],[0,3,9],[0,6,9], [0,6],[0,6],[0,6],
    [0,6],[0,6],[0,6],[0,6,9],
    [0,6,9],[0,6],[0,3,6],[0], [0,6],[0,3,6,9],
    [0,6],[0],[0,6,9], [0,6],[0,6,9],[0,3,6,9],[0,6],
    [0,6],[0,6],[0,6],[0,6],[0,6],
    [0,6,9],[0,3,6,9],[0,6,9],[0,3,6,9],[0,6],[0,3],
    [0,6],[0,6],[0,6],[0],
]
NORMAL = [
    [3,6,9],[0,3,6,9],[0,3,6,9],[0,3,6,9],[0,3,6,9],[0,3,6,9],
    [0,3,6,9],[0,3,4,6,9],[0,3,6,9,11], [0,6,9],[0,3,6,9],[0,3,6,9,11],
    [0,3,6,9],[0,3,6,9],[0,3,6,9],[0,3,6,9,11],
    [0,1,3,6,9],[0,3,6,9],[0,3,6,9,11],[0,6,9], [0,3,6,9],[0,3,6,9,11],
    [0,6,10],[0,3,6,9],[0,5,6,9], [0,3,6,9],[0,3,6,9],[0,3,6,9,11],[0,3,6,9],
    [0,3,6,9],[0,3,6,9],[0,3,6,9],[0,3,6,9],[0,3,5,6,9],
    [0,3,6,9,11],[0,3,6,9],[0,3,6,9,11],[0,3,6,9],[0,3,6,9],[0,3],
    [0,3,6,9],[0,6],[0,3,6,9],[0],
]
HARD = [
    [3,6,7,9,10],[0,1,3,6,7,9], [0,1,3,4,6,9],[0,3,4,6,7,9],
    [0,1,3,6,7,9],[0,3,4,6,9,11],
    [0,1,3,4,6,7,9,10,11],[0,1,3,4,6,7,9,10],[0,1,3,4,6,8,9,11],
    [0,2,3,5,6,9,11],[0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9,10,11],
    [0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9,10,11],
    [0,1,3,4,6,7,9,10,11],[0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9,10,11],
    [0,2,3,5,6,9,11], [0,1,3,4,5,6,7,8,9,10,11],[0,1,2,3,4,5,6,7,8,9,10,11],
    [0,3,6,9,10],[0,1,3,4,6,8,9],[0,3,5,6,7,9,11],
    [0,3,4,5,6,9,10,11],[0,3,4,5,6,7,9,10,11],[0,1,2,3,4,5,6,7,9,10,11],[0,1,2,3,4,5,6,9],
    [0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9,10],[0,1,3,4,6,7,9],
    [0,3,6,9],[0,3,5,6,7,9,10,11],
    [0,1,2,3,4,5,6,7,8,9,10,11],[0,1,2,3,4,5,6,7,9,10,11],
    [0,1,2,3,4,5,6,7,8,9,10,11],[0,1,2,3,4,5,6,7,9,10,11],
    [0,1,3,4,5,6,7,9,10,11],[0,1,3,4,5],
    [0,1,3,6,7,9],[0,1,3,6,7,9],[0,1,3,4,6,9],[0],
]

# パルス位置: (滞留パルス数, 必要カット数)。余韻と音の持続をロングで表す。
LONGS = {
 'easy': {3:(9,3),48:(6,2),114:(6,2),228:(9,3),276:(12,4),378:(9,3),468:(6,3),516:(4,2)},
 'normal': {9:(6,3),33:(3,2),57:(3,2),102:(3,2),114:(6,3),150:(3,2),174:(3,2),222:(3,2),
            228:(9,4),264:(6,3),282:(6,3),327:(3,2),339:(3,2),360:(3,2),384:(6,3),396:(3,2),468:(6,3),516:(4,2)},
 'hard': {3:(6,5),24:(3,3),48:(3,3),63:(2,2),81:(2,2),93:(2,2),105:(2,2),114:(3,3),
          126:(2,2),138:(2,2),153:(2,2),165:(2,2),177:(2,2),189:(2,2),201:(2,2),213:(2,2),225:(2,2),
          228:(6,5),249:(2,2),261:(2,2),270:(6,4),282:(6,4),309:(2,2),321:(2,2),333:(2,2),
          351:(2,2),363:(2,2),375:(2,2),384:(3,3),396:(3,3),411:(2,2),423:(2,2),435:(2,2),
          447:(2,2),459:(2,2),471:(3,3),498:(3,3),516:(4,3)},
}
CHORDS = {
 'easy': {192,402,444},
 'normal': {72,96,192,216,240,252,300,324,402,420,444,456,480},
 'hard': {72,78,84,96,120,132,144,156,168,180,192,198,204,216,240,246,252,258,
          294,300,306,312,318,324,330,336,348,372,402,408,414,420,426,432,438,444,450,456,462,480},
}
PHRASE_HEADS = {3,24,48,72,108,120,144,168,192,216,228,240,264,276,294,300,324,342,354,378,390,402,414,426,438,450,462,480,492,504,516}


def make_chart(diff):
    masks = {'easy':EASY,'normal':NORMAL,'hard':HARD}[diff]
    selected = {b*12+p for b,mask in enumerate(masks) for p in mask}
    selected.update(LONGS[diff])
    selected.update(CHORDS[diff])
    if diff != 'easy':
        selected.update([293,401])  # 音源で確認できる食い込み/ラスサビ前のフィル。
    # 終盤、演奏が抜ける2拍にはノーツを足さない。
    selected = {p for p in selected if not 474 <= p < 480 and p <= 516}
    # ロングはこのゲームでは回数カット。両手を奪う重なりを作らず、専用の時間を確保。
    occupied = []
    for p in sorted(LONGS[diff]):
        length, count = LONGS[diff][p]
        selected = {q for q in selected if not p < q < p+length}
        occupied.append((p,p+length))
    # 本当に弱い三連の末尾を機械的に埋めない。手設計の主拍とフィルは保持する。
    if diff == 'hard':
        selected = {p for p in selected if p%3==0 or p in [293,401] or PULSES[p]['strength'] >= .30}

    groups = []
    previous_hand = 'red'
    hand_indices = {'blue':0,'red':0}
    chord_index = 0
    for p in sorted(selected):
        s = section_at(p)
        chord = p in CHORDS[diff] and p not in LONGS[diff]
        hands = ['blue','red'] if chord else [('blue' if previous_hand=='red' else 'red')]
        group=[]
        for hand in hands:
            sign=-1 if hand=='blue' else 1
            seq=hand_indices[hand]
            hand_indices[hand]+=1
            broad=s[2] in ['hook','chorus1','chorus1b','chorus2','final']
            # エルドラドで多い±0.36/1.07/1.79と高さ0.21/0.64を基本語彙にする。
            widths = [1.071429,.357143,1.071429,1.785714] if broad and diff!='easy' else [.357143,1.071429,1.071429,.357143]
            width=widths[(seq+(p//12)%2)%4]
            if chord:
                width=1.071429 if diff=='easy' or (p//6)%2==0 else 1.785714
            if s[2] in ['drop','intro','interlude','breath']:
                width=.714286
            # 揺り返し/上昇/下降はフレーズごとに統一して、毎ノーツのランダム配置を避ける。
            shape=(p//12)%4
            heights=[.214286,.642857,.214286,-.214286]
            if broad and shape==1: heights=[-.642857,-.214286,.214286,.642857]
            if broad and shape==3: heights=[.642857,.214286,-.214286,-.642857]
            y=heights[seq%4]
            if chord: y=.642857 if (p//6)%2==0 else -.214286
            if s[2]=='drop': y=.214286
            if diff=='easy': y=max(-.214286,min(.642857,y))
            note=dict(beat=round(p/3,7),time=round(p*PULSE_MS,4),x=round(sign*width,6),y=round(y,6),
                      type='tap',color=hand,direction='none',count=1,lengthMs=0.0)
            if p in LONGS[diff]:
                length,count=LONGS[diff][p]
                note.update(type='long',count=count,lengthMs=round(length*PULSE_MS,4))
            group.append(dict(pulse=p,hand=hand,section=s[2],note=note,
                              rationale='phrase' if p in PHRASE_HEADS else ('triplet-response' if p%3 else 'main-pulse')))
        groups.append(group)
        if chord:
            previous_hand='blue' if chord_index%2 else 'red'
            chord_index+=1
        else:
            previous_hand=hands[-1]

    rows=[r for g in groups for r in g]
    # 金色はフレーズの輪郭と強い音の強調。比率はElDoradoの同難易度に近づける。
    gold_ratio={'easy':.46,'normal':.40,'hard':.33}[diff]
    ranked=sorted(groups,key=lambda g:(
        (3 if g[0]['pulse'] in PHRASE_HEADS else 0)
        +(1.1 if g[0]['note']['type']=='long' else 0)
        +(.8 if len(g)==2 else 0)
        +PULSES[g[0]['pulse']]['strength']*.5,
        -g[0]['pulse']),reverse=True)
    gold=0
    for g in ranked:
        if gold >= round(len(rows)*gold_ratio): break
        for r in g:r['note']['color']='gold'
        gold+=len(g)

    flick_target=round(len(rows)*{'easy':.12,'normal':.24,'hard':.32}[diff])
    candidates=[g for g in groups if g[0]['note']['type']=='tap']
    # フレーズ後半のアクセント/上モノの立ち上がりを優先してフリックへ。
    candidates.sort(key=lambda g:(
        (1 if g[0]['pulse']%12 in [6,9,11] else 0)
        +(.7 if section_at(g[0]['pulse'])[2] in ['chorus1','chorus1b','chorus2','final'] else 0)
        +min(2,PULSES[g[0]['pulse']]['high'])*.35,
        -g[0]['pulse']),reverse=True)
    chosen=set()
    flicks=0
    for g in candidates:
        if flicks>=flick_target:break
        if diff=='easy' and len(g)>1:continue
        chosen.add(g[0]['pulse']);flicks+=len(g)
    previous_dir={'blue':'down','red':'down'}
    for g in groups:
        if g[0]['pulse'] not in chosen:continue
        p=g[0]['pulse']
        for r in g:
            hand=r['hand']
            d='up' if previous_dir[hand] in ['down','downleft','downright'] else 'down'
            if diff=='easy': d='up'
            elif p%12==6 and p//12%4==0:
                d='left' if hand=='blue' else 'right'
            elif diff=='normal' and p%12==11:
                d='upleft' if hand=='blue' else 'upright'
            elif diff=='hard' and p%12==9 and p//12%5==0:
                d='upleft' if hand=='blue' else 'upright'
            previous_dir[hand]=d
            r['note'].update(type='direction',direction=d)

    # 同じ手の急な飛びを抑える。譜面全体の意味付けは維持して座標だけ補正する。
    last={}
    for r in rows:
        n=r['note']; hand=r['hand']; t=n['time']/1000
        if hand in last:
            prev=last[hand]; dt=t-prev['time']/1000
            max_distance={'easy':2.1,'normal':2.4,'hard':2.8}[diff]
            distance=math.hypot(n['x']-prev['x'],n['y']-prev['y'])
            if dt<1.1 and distance>max_distance:
                n['x']=round((n['x']+prev['x'])/2,6)
                n['y']=round((n['y']+prev['y'])/2,6)
        last[hand]=n
    chart=dict(_comment='揺籠 / '+diff+' / Original chart, ElDorado-only reference. Compound pulse 172/3 BPM; audio-derived attack offset +12ms. See docs/YurikagoCharts.md.',
               bpm=BPM,coordScale=1.0,offsetMs=OFFSET_MS,notes=[r['note'] for r in rows])
    return chart,rows


def stats(chart,rows):
    notes=chart['notes'];t=np.array([n['time']/1000 for n in notes]);lo=0;peak=0
    for hi,x in enumerate(t):
        while x-t[lo]>2:lo+=1
        peak=max(peak,hi-lo+1)
    return dict(notes=len(notes),cuts=sum(n['count'] for n in notes),first_sec=round(t[0]+.012,4),
                last_sec=round(t[-1]+.012,4),end_of_final_long_sec=round((notes[-1]['time']+notes[-1]['lengthMs']+OFFSET_MS)/1000,4),
                avg_nps=round(len(t)/(t[-1]-t[0]),3),peak_2s_nps=peak/2,
                types=dict(Counter(n['type'] for n in notes)),colors=dict(Counter(n['color'] for n in notes)),
                directions=dict(Counter(n['direction'] for n in notes)),
                simultaneous_extra=int(sum(np.diff(t)<.01)),
                assigned_hands=dict(Counter(r['hand'] for r in rows)),
                sections={s[2]:sum(r['section']==s[2] for r in rows) for s in SECTIONS})


def validate(chart,rows):
    notes=chart['notes']; errors=[];warnings=[]
    groups={}
    for r in rows:groups.setdefault(r['pulse'],[]).append(r)
    last_hand={}
    for p,g in groups.items():
        if len(g)>2:errors.append(f'{p}: three-hand chord')
        if len(g)==2:
            a,b=[r['note'] for r in g]
            if math.hypot(a['x']-b['x'],a['y']-b['y'])<1.2:errors.append(f'{p}: chord collision')
        for r in g:
            n=r['note'];hand=r['hand'];time=(n['time']+OFFSET_MS)/1000
            if abs(n['x'])>2.5 or abs(n['y'])>1.5:errors.append(f'{p}: out of bounds')
            if abs(n['time']-n['beat']*60000/BPM)>.001:errors.append(f'{p}: inconsistent timing')
            if not math.isfinite(n['time']) or n['time']<0:errors.append(f'{p}: invalid time')
            if hand in last_hand:
                prev=last_hand[hand];dt=time-prev[0]
                if dt<.30:errors.append(f'{p}: fast same-hand repeat {dt}')
                if dt>0 and math.hypot(n['x']-prev[1],n['y']-prev[2])/dt>7:
                    warnings.append(f'{p}: fast hand travel')
            last_hand[hand]=(time,n['x'],n['y'])
            if n['count']>1:
                end=time+n['lengthMs']/1000
                if end>184:errors.append(f'{p}: tail overflow')
                if n['count']/max(.01,n['lengthMs']/1000)>3:errors.append(f'{p}: excessive long rate')
                for q in groups:
                    if p<q<p+n['lengthMs']/PULSE_MS-1e-5:errors.append(f'{p}: occupied long overlaps {q}')
    assert not errors,errors
    return dict(errors=errors,warnings=warnings,checks=['time/beat coherence','bounds','two-hand chords','chord separation','same-hand recovery','long occupancy','long cut rate','song tail'])


if __name__=='__main__':
    target=OUT/'Charts';target.mkdir(exist_ok=True)
    summary={}
    for diff in ['easy','normal','hard']:
        chart,rows=make_chart(diff)
        summary[diff]=stats(chart,rows)
        summary[diff]['validation']=validate(chart,rows)
        (target/f'chart_{diff}.json').write_text(json.dumps(chart,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        (OUT/f'authoring_{diff}.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        if diff=='normal':(target/'chart.json').write_text(json.dumps(chart,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        print(diff,json.dumps(summary[diff],ensure_ascii=False),flush=True)
    manifest=dict(reference_song='ElDorado only',audio_sha256=json.loads((OUT/'audio-analysis.json').read_text())['audio_sha256'],
                  bpm=BPM,subdivision_bpm=172,offset_ms=OFFSET_MS,
                  sections=[dict(start_sec=round(a*PULSE_MS/1000+.012,3),end_sec=round(b*PULSE_MS/1000+.012,3),key=k,label=l) for a,b,k,l in SECTIONS],
                  reference=REFERENCE,charts=summary)
    (OUT/'chart-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
