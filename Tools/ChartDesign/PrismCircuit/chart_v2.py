"""PRISM CIRCUIT v4: 両手アクセントと金の応答、振り抜きまで含むフリック監査。

標準ライブラリだけで譜面を再生成・検証できる。chart.py は音声付きの全工程。
python chart_v2.py --output <folder> [--deployed <song folder>]
"""
import argparse
import collections
import json
import math
from pathlib import Path

OUT = Path(__file__).resolve().parents[3] / 'Outputs/OriginalMusic/PrismCircuit'
COMPOSITION = Path(__file__).with_name('composition.json')
if not COMPOSITION.exists():
    COMPOSITION = OUT / 'composition.json'
META = json.loads(COMPOSITION.read_text(encoding='utf-8'))
BPM = META['bpm']
BEAT = 60 / BPM
EVENTS = META['events']
DIFFICULTIES = ('easy', 'normal', 'hard')
DIRECTIONS = {
    'up': (0, 1), 'down': (0, -1),
    'left': (-1, 0), 'right': (1, 0),
    'upleft': (-1, 1), 'upright': (1, 1),
    'downleft': (-1, -1), 'downright': (1, -1),
}
HALF_SWING = .25  # 切断位置の前後を含めた仮想の振り幅（物理メートルではない）
SWING_SPEED_LIMIT = 5.5
ENTRY_COSINE = math.sqrt(.5)  # 45度以内でフリックへ進入する
DIR_NAMES = tuple(DIRECTIONS)
UNIT_DIRECTIONS = {name: tuple(x/math.hypot(*v) for x in v) for name, v in DIRECTIONS.items()}


def dot(a, b):
    return sum(x*y for x, y in zip(a, b))


def swing_edge(a, b, before, after):
    """前打点中心→振り抜き→次打点の手前→中心までを、打点間の時間と照合。"""
    gap = (b['time']-a['time']-a.get('lengthMs', 0))/1000
    if gap <= 0:
        return False
    av, bv = UNIT_DIRECTIONS[before], UNIT_DIRECTIONS[after]
    delta = (b['x']-a['x'], b['y']-a['y'])
    if b['direction'] != 'none':
        distance = math.hypot(*delta)
        if distance < .08 or dot(delta, bv)/distance < ENTRY_COSINE-.00001:
            return False
    if gap < 2*BEAT-.00001:
        # 自由タップを挟んだときも実際の振り方向を追い、暗黙のリセットを隠さない。
        limit = -.5 if a['direction'] != 'none' and b['direction'] != 'none' else 0
        if dot(av, bv) > limit+.00001:
            return False
    connector = tuple(delta[i]-HALF_SWING*(av[i]+bv[i]) for i in (0, 1))
    speed = (2*HALF_SWING+math.hypot(*connector))/gap
    return speed <= SWING_SPEED_LIMIT+.00001


def audit_swing_flow(notes):
    """金を取る/取らない全分岐を、可能な振り方向集合として圧縮して調べる。

    集合の和を取らず、分岐ごとの集合を残すため、一方の手で成功する経路で
    別の取り方の不成立を隠さない。自由タップも8方向のいずれかを実際に振る。
    """
    failures = []
    max_states = 1
    for hand in ('blue', 'red'):
        states = {(-1, 255)}
        for index, n in enumerate(notes):
            if n['color'] not in (hand, 'gold'):
                continue
            allowed = (DIR_NAMES.index(n['direction']),) if n['direction'] != 'none' else range(8)
            optional = n['color'] == 'gold' and n['count'] == 1
            next_states = set(states) if optional else set()
            for previous, mask in sorted(states):
                reachable = 0
                for out_dir in allowed:
                    if previous == -1 or any(mask & (1 << in_dir) and swing_edge(notes[previous], n, DIR_NAMES[in_dir], DIR_NAMES[out_dir]) for in_dir in range(8)):
                        reachable |= 1 << out_dir
                if reachable:
                    # 長い金ロールの最終カットは任意方向。入口と出口の回復は別途照合する。
                    next_states.add((index, 255 if n['count'] > 1 else reachable))
                else:
                    failures.append(dict(hand=hand, previous=previous, note=index,
                                         fromBeat=notes[previous]['beat'] if previous >= 0 else None, beat=n['beat']))
                    # 診断は次の問題も列挙できるよう、中立姿勢から再開する。
                    next_states.add((index, 255))
            states = next_states
            max_states = max(max_states, len(states))
    return dict(failures=failures, allGoldAssignmentsFeasible=not failures,
                optionalGoldTaps=sum(n['color'] == 'gold' and n['count'] == 1 for n in notes),
                halfSwingUnits=HALF_SWING, swingTravelLimitUnitsPerSecond=SWING_SPEED_LIMIT,
                maxEntryAngleDegrees=45, rapidArrowMinimumTurnDegrees=120, maxBranchStates=max_states)


def sounds_at(beat):
    return [(i, e) for i, e in enumerate(EVENTS) if abs(e['beat'] - beat) < .00001]


def source_at(beat, preferred, excluded=()):
    candidates = [(i, e) for i, e in sounds_at(beat) if i not in excluded]
    for role in preferred:
        for i, event in candidates:
            if event['role'] == role:
                return i
    raise ValueError(f'No musical source at beat {beat}: {preferred}')


def phrase_specs(diff):
    """同時・金の前後の空間を句の段階で確保。配置段階でノーツを黙って落とさない。"""
    easy, hard = diff == 'easy', diff == 'hard'
    specs = []

    def add(beat, motif, kind='single', preferred=None, count=1, length=0):
        preferred = preferred or ('hook', 'verse_melody', 'build_pulse', 'intro_motif',
                                   'quiet_melody', 'ending_motif', 'kick', 'snare', 'bass', 'openhat')
        first = source_at(beat, preferred)
        sources = [first]
        if kind == 'pair':
            sources.append(source_at(beat, ('kick', 'snare', 'clap', 'chord_stab', 'crash'), sources))
        if count > 1:
            sources = [source_at(beat + k * length / (count - 1), ('snare',)) for k in range(count)]
        specs.append(dict(beat=beat, motif=motif, kind=kind, sources=sources, count=count, length=length))

    for bar in range(2, 71):
        b = bar * 4
        if bar in (23, 55):
            add(b, 'snare_roll', 'gold', ('snare',),
                {'easy': 3, 'normal': 4, 'hard': 7}[diff], 2 if easy else 3)
        elif bar == 70:
            add(b, 'final_bell', 'gold', ('last_note',))
        elif bar >= 68:
            if bar == 68:
                add(b, 'landing_chord', 'pair', ('ending_chord',))
            if not easy:
                for q in ((2,) if bar == 68 else (0, 2)):
                    add(b + q, 'ending_echo', preferred=('ending_motif',))
        elif bar < 4:
            for q in ((0,) if easy else (0, 2)):
                add(b + q, 'intro_hands')
        elif bar < 16:
            if easy:
                for q in (0, 2):
                    # ゆっくりした金を早めに体験し、後のサビへの文法にする。
                    if bar % 4 == 3 and q == 2:
                        continue
                    add(b + q, 'verse_walk', 'gold' if bar in (6, 10, 14) and q == 0 else 'single')
            elif hard:
                ev = [e for e in EVENTS if e['role'] == 'verse_melody' and b <= e['beat'] < b + 4]
                for e in ev:
                    # ベルの応答を金にする小節は、末尾のメロディと二重に数えない。
                    if bar % 2 == 1 and e['beat'] == b + 3:
                        continue
                    add(e['beat'], 'verse_melody', preferred=('verse_melody',))
                if bar % 4 == 1:
                    add(b + 3, 'bell_answer', 'gold', ('answer',))
                elif bar % 4 == 3:
                    add(b + 3, 'verse_backbeat', 'pair', ('answer',))
            else:
                for q in ((0, 2) if bar % 2 == 0 else (0, 2, 3)):
                    add(b + q, 'verse_backbeat' if q == 3 else 'verse_walk',
                        'gold' if q == 3 and bar % 4 == 1 else
                        'pair' if q == 3 and bar % 4 == 3 else 'single',
                        ('answer', 'snare') if q == 3 else None)
        elif 40 <= bar < 48:
            ev = [e for e in EVENTS if e['role'] == 'quiet_melody' and b <= e['beat'] < b + 4]
            for i, e in enumerate(ev):
                if easy and i > 0:
                    continue
                if not hard and i > 1:
                    continue
                add(e['beat'], 'quiet_keys', 'gold' if bar in (42, 46) and i == 0 else 'single', ('quiet_melody',))
        elif 16 <= bar < 24 or 48 <= bar < 56:
            pos = bar % 8
            if easy:
                steps = (0, 2)
            elif hard and pos in (4, 5, 6):
                steps = (0, .5, 1, 1.5, 2, 2.5, 3, 3.5)
            else:
                steps = (0, 1, 2, 3)
            for q in steps:
                if pos == 6 and q >= 3:
                    continue
                kind = 'single'
                if pos == 1 and q == 2:
                    kind = 'gold'
                if not easy and pos == 2 and q == 0:
                    kind = 'pair'
                add(b + q, 'build_alternation' if hard and pos >= 4 else 'build_steps', kind,
                    ('build_pulse', 'snare', 'kick', 'hat'))
        else:
            # サビ: 開く→メロディ→金の応答→両手着地を繰り返し、最終サビで展開。
            pos = (bar - 24) % 8
            if easy:
                for q in (0, 2):
                    kind = 'pair' if q == 0 and bar % 4 == 0 else (
                        'gold' if q == 2 and bar % 4 == 2 else 'single')
                    add(b + q, 'chorus_open' if kind == 'pair' else 'chorus_walk', kind)
            elif not hard:
                steps = (0, 1, 2, 3) if bar % 2 == 0 else (0, 1.5, 3)
                if bar in (30, 34, 38, 60, 64):
                    steps = (0, 1.5, 2, 3)
                elif bar in (29, 35, 61, 65):
                    steps = (0, 2, 3)
                for q in steps:
                    kind = 'pair' if q == 0 and bar % 2 == 0 else (
                        'gold' if q == 3 and bar % 2 == 1 else 'single')
                    if bar >= 56 and bar % 4 == 2 and q == 3:
                        kind = 'pair'
                    add(b + q, 'chorus_open' if kind == 'pair' else
                        'chorus_gold_reply' if kind == 'gold' else 'chorus_melody', kind)
            else:
                patterns = {
                    0: ((0, 'pair'), (1, 'single'), (1.5, 'single'), (2.5, 'single'), (3, 'single')),
                    1: ((0, 'gold'), (1, 'single'), (2, 'single'), (3, 'pair')),
                    2: ((0, 'single'), (1, 'single'), (1.5, 'single'), (2.5, 'single'), (3, 'single')),
                    3: ((0, 'gold'), (1.5, 'single'), (2, 'single'), (3, 'pair')),
                    4: ((0, 'pair'), (1, 'single'), (1.5, 'single'), (2.5, 'single'), (3, 'single')),
                    5: ((0, 'gold'), (1, 'single'), (2, 'single'), (3, 'pair')),
                    6: tuple((q / 2, 'single') for q in range(7)),
                    7: ((0, 'gold'), (1, 'single'), (1.5, 'single'), (2, 'single'), (2.5, 'single'), (3, 'single')),
                }
                pattern = patterns[pos]
                if 32 <= bar < 40:
                    # 主題の再登場は同じ8小節を貼らず、伴奏との応答へ打点を振り直す。
                    responses = {
                        0: ((0, 'pair'), (1, 'single'), (2, 'single'), (2.5, 'single'), (3, 'single')),
                        1: ((0, 'gold'), (1, 'single'), (1.5, 'single'), (2, 'single'), (3, 'pair')),
                        2: ((0, 'single'), (.5, 'single'), (1.5, 'single'), (2, 'single'), (3, 'single')),
                        3: ((0, 'gold'), (1, 'single'), (2, 'single'), (3, 'pair')),
                        4: ((0, 'pair'), (1, 'single'), (2, 'single'), (2.5, 'single'), (3, 'single')),
                        5: patterns[5],
                        6: ((0, 'single'), (.5, 'single'), (1, 'single'), (2, 'single'), (2.5, 'single'), (3, 'single')),
                        7: ((0, 'gold'), (1, 'single'), (2, 'single'), (2.5, 'single'), (3, 'single')),
                    }
                    pattern = responses[pos]
                if bar >= 56 and pos in (2, 6):
                    pattern = ((0, 'pair'), (1, 'single'), (1.5, 'single'), (2, 'single'), (3, 'pair'))
                if bar == 67:
                    pattern = ((0, 'gold'), (1, 'pair'), (2, 'single'), (3, 'pair'))
                for q, kind in pattern:
                    add(b + q, 'spectrum_open' if bar >= 56 and kind == 'pair' else
                        'chorus_open' if kind == 'pair' else
                        'chorus_gold_reply' if kind == 'gold' else
                        'eighth_weave' if pos in (6, 7) else 'hook_arc', kind)
    specs.sort(key=lambda s: s['beat'])
    # 大きな和音は両手へ、主題・ベースの応答は金へ。増加点を音楽の句で指定する。
    pair_beats = {
        'easy': {bar*4 for bar in range(24, 68) if (bar < 40 or bar >= 56) and bar % 4 == 2},
        'normal': {bar*4 for bar in range(24, 68) if (bar < 40 or bar >= 56) and bar % 2 == 1},
        'hard': {16, 32, 48, 60, 76, 204, 102, 104, 118, 129, 139, 142, 145, 150, 157, 230, 246, 262, 270},
    }[diff]
    gold_beats = {
        'easy': {bar*4+2 for bar in range(24, 68) if (bar < 40 or bar >= 56) and bar % 4 == 0},
        'normal': {24, 40, 56, 18, 38, 50, 66, 74, 82, 194, 202, 210, 164, 180, 274, 278},
        'hard': {18, 27, 38, 51, 59, 66, 74, 78, 194, 202, 206, 101, 117, 141, 149, 229, 245, 261, 274, 278},
    }[diff]
    by_beat = {s['beat']: s for s in specs}
    for beat in sorted(pair_beats | gold_beats):
        spec = by_beat[beat]
        if spec['kind'] != 'single':
            raise ValueError(f'Accent already occupied: {diff}@{beat}')
        spec['kind'] = 'pair' if beat in pair_beats else 'gold'
        if spec['kind'] == 'pair':
            spec['sources'].append(source_at(beat, ('kick', 'snare', 'clap', 'chord_stab', 'crash', 'bass'), spec['sources']))
    if len({s['beat'] for s in specs}) != len(specs):
        raise ValueError('意図しない重複打点がある')
    return specs


# 4小節の動きを音楽の区間に割り当てる。各点は2拍間隔の折り返し地点。
# 乱数や各ノーツへの微小なずらしではなく、腕で追える形を作る。
CONTOURS = {
    'walk': ((1.1, 0), (1.3, .2), (1.45, .35), (1.2, .1), (1.0, -.2), (1.25, -.35), (1.45, -.1), (1.2, .15)),
    'fan': ((1.05, -.5), (1.3, -.2), (1.6, .3), (1.6, .55), (1.3, .3), (1.05, -.3), (1.35, -.5), (1.55, 0)),
    'pendulum': ((1.5, 0), (1.0, 0), (1.4, .3), (1.0, .3), (1.5, 0), (1.0, -.3), (1.45, -.3), (1.05, 0)),
    'stair': ((1.05, -.6), (1.2, -.2), (1.45, .2), (1.6, .6), (1.35, .2), (1.05, -.2), (1.25, -.6), (1.45, -.2)),
    'ribbon': ((1.5, .55), (1.2, .25), (1.0, -.3), (1.35, -.55), (1.55, -.2), (1.35, .3), (1.05, .55), (1.3, .2)),
    'wave': ((1.15, .5), (1.5, .1), (1.2, -.5), (1.0, -.15), (1.45, .5), (1.6, .1), (1.2, -.5), (1.0, -.15)),
}


def pose_at(beat, diff, kind):
    bar = int(beat // 4)
    if bar < 4:
        name, start = 'walk', 0
    elif bar < 16:
        name, start = ('walk', 'stair', 'pendulum')[(bar-4)//4], 16
    elif bar < 24:
        name, start = ('ribbon', 'stair')[(bar-16)//4], 64
    elif bar < 40:
        name, start = ('fan', 'pendulum', 'ribbon', 'wave')[(bar-24)//4], 96
    elif bar < 48:
        name, start = ('walk', 'ribbon')[(bar-40)//4], 160
    elif bar < 56:
        name, start = ('pendulum', 'wave')[(bar-48)//4], 192
    elif bar < 68:
        name, start = ('stair', 'ribbon', 'fan')[(bar-56)//4], 224
    else:
        name, start = 'walk', 272
    phase = (beat-start) % 16 / 2
    i = int(phase)
    a, b = CONTOURS[name][i], CONTOURS[name][(i+1) % 8]
    width, y = (a[k] + (b[k]-a[k]) * (phase-i) for k in (0, 1))
    if diff == 'easy':
        width, y = 1.1 + (width-1.1)*.65, y*.7
    if 40 <= bar < 48:
        width, y = 1.0+(width-1.0)*.5, y*.5
    if kind == 'gold':
        width, y = 0, y*.5
    return round(width, 1), round(y, 1), name


def choose_direction(note, previous, previous_direction, eligible, pair):
    if not eligible or not previous or previous['color'] == 'gold':
        return 'none'
    dx, dy = note['x']-previous['x'], note['y']-previous['y']
    vertical = 'up' if note['y'] > .05 else 'down' if note['y'] < -.05 else None
    horizontal = 'right' if dx > .08 else 'left' if dx < -.08 else None
    candidates = []
    if vertical and dy * (1 if vertical == 'up' else -1) > .07:
        if horizontal and abs(dx) >= .12 and abs(dy) >= .12:
            candidates.append(vertical+horizontal)
        candidates.append(vertical)
    if horizontal:
        candidates.append(horizontal)
    for direction in candidates:
        v = UNIT_DIRECTIONS[direction]
        if pair and ((note['color'] == 'blue' and v[0] > 0) or (note['color'] == 'red' and v[0] < 0)):
            continue
        if previous_direction != 'none':
            old = UNIT_DIRECTIONS[previous_direction]
            limit = -.5 if note['beat']-previous['end'] < 2 else 0
            if dot(old, v) > limit+.00001:
                continue
        # 横から進入して縦に切らせるような浅い一致を除き、45度以内にする。
        if math.hypot(dx, dy) >= .08 and (dx*v[0] + dy*v[1])/math.hypot(dx, dy) >= ENTRY_COSINE-.00001:
            return direction
    return 'none'


def build(diff):
    easy, hard = diff == 'easy', diff == 'hard'
    minbeat = 2 if easy else 1
    last = {'blue': None, 'red': None}
    flow = {'blue': 'none', 'red': 'none'}
    totals = collections.Counter()
    next_hand = 'blue'
    notes, provenance = [], []
    for spec in phrase_specs(diff):
        beat, kind = spec['beat'], spec['kind']
        bar = int(beat // 4)
        width, y, contour = pose_at(beat, diff, kind)
        if kind == 'gold':
            colors = ['gold']
        elif kind == 'pair':
            colors = ['blue', 'red']
        else:
            available = [h for h in ('blue', 'red') if last[h] is None or beat - last[h]['end'] >= minbeat - 1e-6]
            if not available:
                raise ValueError(f'{diff}: no hand available at beat {beat}')
            # 長い空白・両手着地の後は、手の負担を均す側から次の句を始める。
            if all(last[h] is None or beat - last[h]['end'] >= 2 for h in available):
                color = min(available, key=lambda h: (totals[h], h != next_hand))
            else:
                color = next_hand if next_hand in available else available[0]
            colors = [color]
        for index, color in enumerate(colors):
            occupied = ('blue', 'red') if color == 'gold' else (color,)
            for hand in occupied:
                if last[hand] and beat - last[hand]['end'] < minbeat - 1e-6:
                    raise ValueError(f'{diff}: {kind} needs {hand} too soon at {beat}')
            direction = 'none'
            if color == 'gold':
                x, intent = 0., 'free'
            else:
                previous = last[color]
                sign = -1 if color == 'blue' else 1
                x = round(sign * width, 3)
                eligible = (hard and not (bar < 4 or 40 <= bar < 48 or bar >= 68)) or (
                    not easy and not hard and (kind == 'pair' or
                        spec['motif'] == 'chorus_melody' and beat % 8 == 2))
                direction = choose_direction(dict(beat=beat, x=x, y=y, color=color), previous, flow[color], eligible, kind == 'pair')
                intent = direction if direction != 'none' else 'free'
                flow[color] = direction
                totals[color] += 1
            note = dict(beat=beat, time=round(beat * BEAT * 1000, 3), x=x, y=y,
                        type='long' if spec['count'] > 1 else 'direction' if direction != 'none' else 'tap',
                        color=color, direction=direction, count=spec['count'])
            if spec['count'] > 1:
                note['lengthMs'] = round(spec['length'] * BEAT * 1000, 3)
            notes.append(note)
            ids = spec['sources'] if spec['count'] > 1 else [spec['sources'][index]]
            provenance.append(dict(beat=beat, color=color, motif=spec['motif'], contour=contour, intendedSwing=intent,
                                   sourceEventIds=ids, roles=[EVENTS[i]['role'] for i in ids]))
            for hand in occupied:
                last[hand] = dict(end=beat + spec['length'], x=x, y=y, color=color)
                if color == 'gold':
                    flow[hand] = 'none'
        if kind == 'pair':
            next_hand = min(('blue', 'red'), key=lambda h: (totals[h], h == next_hand))
        elif kind != 'gold':
            next_hand = 'red' if colors[-1] == 'blue' else 'blue'
    chart = dict(_comment='Prism Circuit v4: stronger level doubles and gold replies; flick entry, follow-through and both gold-hand choices audited. Longs are repeated cuts.',
                 bpm=BPM, coordScale=1., offsetMs=0., beatZeroMs=0.,
                 displayLevel={'easy': 3, 'normal': 5, 'hard': 8}[diff],
                 timeSignatures=[dict(beat=0, numerator=4, denominator=4)], notes=notes)
    return first_sight_revision(chart, provenance, diff)



def first_sight_revision(chart, provenance, diff):
    """初見改訂: 両手のアクセントを残し、前後の振り方向に自由を作る。"""
    if diff == 'easy':
        return chart, provenance
    if diff == 'normal':
        # サビの金の応答を4拍目から3拍目の実在する音へ移す。
        # 直前の細かい単発を譲り、両手着地まで2拍を確保する。
        replies = {n['beat']: n['beat']-1 for n,p in zip(chart['notes'], provenance)
                   if p['motif'] == 'chorus_gold_reply'}
        kept = [(n,p) for n,p in zip(chart['notes'], provenance)
                if not any(new-.5 <= n['beat'] <= new and n['color'] != 'gold'
                           for new in replies.values())]
        chart['notes'], provenance = map(list, zip(*kept))
        for n,p in zip(chart['notes'], provenance):
            if n['beat'] in replies:
                beat = replies[n['beat']]
                n.update(beat=beat, time=round(beat*BEAT*1000,3))
                p['beat'] = beat
                event = source_at(beat, ('hook','kick','snare','bass','chord_stab'))
                p['sourceEventIds'] = [event]
                p['roles'] = [EVENTS[event]['role']]
        relax = set()
        for hand in ('blue', 'red'):
            seq = [(i, n) for i, n in enumerate(chart['notes']) if n['color'] in (hand, 'gold')]
            for (ai, a), (bi, b) in zip(seq, seq[1:]):
                if b['time']-a['time']-a.get('lengthMs', 0) < 600:
                    relax.update((ai, bi))
        for i in relax:
            n = chart['notes'][i]
            if n['direction'] != 'none':
                n.update(direction='none', type='tap')
                provenance[i]['intendedSwing'] = 'free'
    else:
        # 3拍のスネアロールは四分刻み4回。初見で両手交互を読めなくても入れる。
        for n, p in zip(chart['notes'], provenance):
            if n['beat'] in (92, 220) and n['count'] == 7:
                n['count'] = 4
                p['sourceEventIds'] = p['sourceEventIds'][::2]
                p['roles'] = p['roles'][::2]
    chart['_comment'] += ' First-sight revision 2026-10-03: recovery around doubles and quarter-note rolls.'
    return chart, provenance


def metrics(chart):
    notes = chart['notes']
    groups = collections.defaultdict(list)
    for n in notes:
        groups[n['beat']].append(n)
    gaps, speeds = [], []
    for hand in ('blue', 'red'):
        seq = [n for n in notes if n['color'] in (hand, 'gold')]
        for a, b in zip(seq, seq[1:]):
            gap = (b['time'] - a['time'] - a.get('lengthMs', 0)) / 1000
            gaps.append(gap)
            speeds.append(math.hypot(b['x'] - a['x'], b['y'] - a['y']) / gap if gap > 0 else math.inf)
    times = [n['time'] / 1000 for n in notes]
    cuts = [n['time'] / 1000 + k * n.get('lengthMs', 0) / 1000 / max(1, n['count'] - 1)
            for n in notes for k in range(n['count'])]
    def peak(seq):
        return round(max(sum(t <= v < t + 4 for v in seq) for t in seq) / 4, 3)
    result = dict(notes=len(notes), cuts=len(cuts), averageNps=round(len(notes) / META['durationSeconds'], 3),
                  peak4SecondNps=peak(times), peak4SecondCuts=peak(cuts),
                  minSameHandMs=round(min(gaps) * 1000, 3), maxTravelUnitsPerSecond=round(max(speeds), 3),
                  firstSeconds=times[0], lastSeconds=max(cuts),
                  types=dict(collections.Counter(n['type'] for n in notes)),
                  colors=dict(collections.Counter(n['color'] for n in notes)),
                  simultaneousPairs=sum(len(g) == 2 for g in groups.values()),
                  goldPercent=round(100 * sum(n['color'] == 'gold' for n in notes) / len(notes), 2),
                  pairOnsetPercent=round(100 * sum(len(g) == 2 for g in groups.values()) / len(groups), 2), sections=[])
    rolls = [n for n in notes if n['type'] == 'long']
    result['minRollCutMs'] = round(min(n['lengthMs'] / (n['count'] - 1) for n in rolls), 3)
    result['unequalHeightPairs'] = sum(len(g) == 2 and abs(g[0]['y']-g[1]['y']) > .001 for g in groups.values())
    result['opposedHeightArrows'] = sum(n['direction'].startswith('up') and n['y'] < -.05 or
                                      n['direction'].startswith('down') and n['y'] > .05 for n in notes)
    result['uniquePositions'] = len({(n['x'], n['y']) for n in notes})
    bar_layouts = collections.Counter()
    for bar in range(4, 68):
        group = [n for n in notes if bar*4 <= n['beat'] < bar*4+4]
        if len(group) < 2:
            continue
        # 0.2単位に丸め、微小ずらしを「違う形」と数えない。
        layout = tuple((n['beat'] % 4, n['color'], round(n['x']*5), round(n['y']*5), n['direction']) for n in group)
        bar_layouts[layout] += 1
    result['uniqueBarLayouts'] = len(bar_layouts)
    result['mostRepeatedBarLayout'] = max(bar_layouts.values())
    for section in META['sections']:
        ns = [n for n in notes if section['startBar'] * 4 <= n['beat'] < section['endBar'] * 4]
        result['sections'].append(dict(name=section['name'], notes=len(ns),
            nps=round(len(ns) / (section['endSeconds'] - section['startSeconds']), 3),
            gold=sum(n['color'] == 'gold' for n in ns),
            pairs=sum(len(g) == 2 for b, g in groups.items() if section['startBar'] * 4 <= b < section['endBar'] * 4)))
    return result


def validate(chart, diff, provenance=None):
    """時間・音源・両手・金の両分岐・方向遷移・偏りを生成器とは別に検査。"""
    ns = chart['notes']
    errors = []
    def check(condition, message):
        if not condition:
            errors.append(message)
    check(chart['bpm'] == BPM and chart['offsetMs'] == chart['beatZeroMs'] == 0, 'clock')
    check(ns == sorted(ns, key=lambda n: (n['beat'], n['color'])), 'ordering')
    groups = collections.defaultdict(list)
    for i, n in enumerate(ns):
        label = f'{i}@{n["beat"]}'
        groups[n['beat']].append(n)
        check(abs(n['time'] - n['beat'] * BEAT * 1000) < .001, f'timing {label}')
        check(bool(sounds_at(n['beat'])), f'no sound {label}')
        check(-2.5 <= n['x'] <= 2.5 and -1.5 <= n['y'] <= 1.5, f'range {label}')
        check(n['color'] in ('red', 'blue', 'gold'), f'color {label}')
        check(n['type'] in ('tap', 'direction', 'long'), f'type {label}')
        check(n['direction'] == 'none' or n['direction'] in DIRECTIONS, f'direction {label}')
        check(not (n['direction'].startswith('up') and n['y'] < -.05 or
                   n['direction'].startswith('down') and n['y'] > .05), f'arrow height {label}')
        check((n['type'] == 'direction') == (n['direction'] != 'none'), f'direction type {label}')
        check(n['color'] != 'red' or n['x'] > 0, f'right hand {label}')
        check(n['color'] != 'blue' or n['x'] < 0, f'left hand {label}')
        if n['color'] == 'gold':
            check(n['x'] == 0 and n['direction'] == 'none', f'free gold {label}')
        check(n['time'] + n.get('lengthMs', 0) < META['durationSeconds'] * 1000, f'end {label}')
        if diff == 'easy':
            check(n['beat'] % 1 == 0 and n['direction'] == 'none', f'easy vocabulary {label}')
        if n['type'] == 'long':
            check(n['count'] >= 2 and n.get('lengthMs', 0) > 0, f'long data {label}')
            check(n['lengthMs']/(n['count']-1) >= 400-.001, f'first sight roll recovery {label}')
            end = n['time'] + n['lengthMs']
            check(not any(o is not n and n['time'] <= o['time'] <= end + .001 for o in ns), f'long conflict {label}')
            for k in range(n['count']):
                beat = n['beat'] + k * n['lengthMs'] / (BEAT * 1000) / (n['count'] - 1)
                check(any(e['role'] == 'snare' and abs(e['beat'] - beat) < .00001 for e in EVENTS), f'roll sound {label}:{k}')
            # 両手交互の金ロールでも、一手の回復は通常配置と同じ基準にする。
            check(2 * n['lengthMs'] / (n['count'] - 1) >= (2 if diff == 'easy' else 1) * BEAT * 1000 - .001,
                  f'roll hand interval {label}')
        else:
            check(n['count'] == 1 and n.get('lengthMs', 0) == 0, f'tap data {label}')
    for beat, group in groups.items():
        check(len(group) <= 2, f'triple {beat}')
        if len(group) == 2:
            check({n['color'] for n in group} == {'red', 'blue'}, f'pair hands {beat}')
            check(abs(group[0]['x'] - group[1]['x']) >= 1.8, f'pair separation {beat}')
            check(abs(group[0]['y']-group[1]['y']) < .001, f'pair height {beat}')
            check(all(not n['direction'].endswith('right') for n in group if n['color'] == 'blue') and
                  all(not n['direction'].endswith('left') for n in group if n['color'] == 'red'), f'inward pair {beat}')
    for hand in ('blue', 'red'):
        seq = [n for n in ns if n['color'] in (hand, 'gold')]
        for a, b in zip(seq, seq[1:]):
            gap = (b['time'] - a['time'] - a.get('lengthMs', 0)) / 1000
            check(gap >= (2 if diff == 'easy' else 1) * BEAT - .00001, f'hand interval {hand}@{b["beat"]}')
            if diff == 'normal' and (a['direction'] != 'none' or b['direction'] != 'none'):
                check(gap >= .6-.00001, f'first sight flick recovery {hand}@{b["beat"]}')
            if a['color'] == 'gold':
                check(b['direction'] == 'none', f'gold forced exit {hand}@{b["beat"]}')
            if b['direction'] != 'none':
                bv = DIRECTIONS[b['direction']]
                check((b['x']-a['x'])*bv[0] + (b['y']-a['y'])*bv[1] > .07 - .001,
                      f'wrong approach {hand}@{b["beat"]}')
            if a['direction'] != 'none' and b['direction'] != 'none':
                av, bv = DIRECTIONS[a['direction']], DIRECTIONS[b['direction']]
                check(sum(x * y for x, y in zip(av, bv)) <= 0, f'double direction {hand}@{b["beat"]}')
    stats = metrics(chart)
    if all(n['direction'] == 'none' or n['direction'] in DIRECTIONS for n in ns):
        stats['swingAudit'] = audit_swing_flow(ns)
        check(not stats['swingAudit']['failures'], f'swing flow {stats["swingAudit"]["failures"][:3]}')
    check(stats['firstSeconds'] >= 3 and META['durationSeconds'] - stats['lastSeconds'] >= 3, 'lead/tail')
    check(stats['maxTravelUnitsPerSecond'] <= 4.5, 'travel speed')
    check(stats['mostRepeatedBarLayout'] <= 4, 'repeated layout')
    check(stats['uniqueBarLayouts'] >= {'easy': 20, 'normal': 35, 'hard': 45}[diff], 'layout variety')
    check(abs(stats['colors'].get('red', 0) - stats['colors'].get('blue', 0)) <= 5, 'hand load')
    check(12 <= stats['goldPercent'] <= 22, 'gold budget')
    check({'easy': 13, 'normal': 34, 'hard': 46}[diff] <= stats['simultaneousPairs'], 'pair budget')
    check(stats['pairOnsetPercent'] <= {'easy': 17, 'normal': 24, 'hard': 25}[diff], 'pair excess')
    check(stats['types'].get('direction', 0) >= {'easy': 0, 'normal': 15, 'hard': 70}[diff], 'flick vocabulary')
    section_stats = {s['name']: s for s in stats['sections']}
    check(section_stats['Weightless']['nps'] < section_stats['Prism']['nps'] * .6, 'no breathing section')
    check(section_stats['Weightless']['pairs'] == 0, 'quiet pair excess')
    check(section_stats['Full spectrum']['nps'] >= section_stats['Prism']['nps'], 'final chorus underweight')
    check(sum(section_stats[name]['pairs'] for name in ('Prism', 'Full spectrum')) >= stats['simultaneousPairs'] * .65, 'pair emphasis')
    if provenance is not None:
        check(len(provenance) == len(ns), 'source count')
        for n, p in zip(ns, provenance):
            check((n['beat'], n['color']) == (p['beat'], p['color']), 'source identity')
            check(len(p['sourceEventIds']) == n['count'], 'source cuts')
            for k, event_id in enumerate(p['sourceEventIds']):
                expected = n['beat'] + k * n.get('lengthMs', 0) / (BEAT * 1000) / max(1, n['count'] - 1)
                check(abs(EVENTS[event_id]['beat'] - expected) < .00001, 'source timing')
    if errors:
        raise ValueError(f'{diff}: ' + '; '.join(errors))
    return stats


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=OUT / 'RechartV4')
    parser.add_argument('--deployed', type=Path)
    args = parser.parse_args()
    charts, sources, report = {}, {}, {}
    for diff in DIFFICULTIES:
        charts[diff], sources[diff] = build(diff)
        report[diff] = validate(charts[diff], diff, sources[diff])
        if args.deployed:
            deployed = json.loads((args.deployed / f'chart_{diff}.json').read_text(encoding='utf-8-sig'))
            if charts[diff] != deployed:
                raise ValueError(f'deployed {diff} differs from generator')
            validate(deployed, diff, sources[diff])
    args.output.mkdir(parents=True, exist_ok=True)
    for diff, chart in charts.items():
        (args.output / f'chart_{diff}.json').write_text(json.dumps(chart, ensure_ascii=False, indent=2), encoding='utf-8')
    (args.output / 'chart.json').write_bytes((args.output / 'chart_normal.json').read_bytes())
    (args.output / 'validation.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
    (args.output / 'chart_sources.json').write_text(json.dumps(sources, ensure_ascii=False, indent=2), encoding='utf-8')
    for diff, stats in report.items():
        print(diff, json.dumps({k: v for k, v in stats.items() if k != 'sections'}))


if __name__ == '__main__':
    main()
