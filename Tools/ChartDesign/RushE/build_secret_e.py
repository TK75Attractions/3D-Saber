"""指定動画のBeat Saber譜面をEへ移植する。音源や通常3難易度は変更しない。"""
import argparse
import collections
import hashlib
import json
from pathlib import Path

DESIGN = Path(__file__).resolve().parent
SOURCE_SHA256 = 'c024ae017deda053cfa8172ac0c0eb41b4e7600499e0934bb2746890be4e6722'
INFO_SHA256 = '3ab7958949073f72f76898435bbf03bd38b536a49db4eface8ed49f43f0bab6d'
# 原譜音源と既存の許諾音源を7区間で波形照合。全区間で同じ差分(11025Hzの18263サンプル)。
SOURCE_MINUS_GAME_SECONDS = 18263 / 11025
# 終盤のラッシュはまとまった金色区間、途中の同時押しは独立した金色アクセント。
GOLD_CLIMAX_START_BEAT = 254.75
GOLD_ACCENT_BEATS = {98.1875, 117.5, 134.1875, 194.4375, 227.75, 240.125}
X = (-1.5, -.5, .5, 1.5)
Y = (-.8, 0., .8)
# 元ノーツの(拍, 手)を維持した短いロング。次の同じ手のノーツへ食い込ませない。
LONGS = {(39.75, 1): (3, 700), (52.375, 0): (3, 700),
         (111.375, 0): (3, 700), (124.1875, 0): (3, 700),
         (159.9375, 0): (2, 500), (159.9375, 1): (2, 500),
         (221.6875, 1): (2, 420)}


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def load_source():
    result = []
    for filename, expected in [('ExpertPlus.dat', SOURCE_SHA256), ('info.dat', INFO_SHA256)]:
        raw = (DESIGN / 'BeatSaberSource' / filename).read_bytes()
        if hashlib.sha256(raw).hexdigest() != expected:
            raise ValueError('参照譜面のハッシュ不一致: ' + filename)
        result.append(json.loads(raw.decode('utf-8-sig')))
    chart, info = result
    if chart.get('_BPMChanges') or chart.get('_obstacles') or info['_songTimeOffset'] != 0:
        raise ValueError('参照版とは異なるテンポ・障害物・オフセット')
    return chart, info


def build(output):
    source, info = load_source()
    rows = sorted(enumerate(source['_notes']), key=lambda pair: pair[1]['_time'])
    notes, plan, evidence = [], [], []
    hand_cuts = collections.Counter()
    for index, (source_index, row) in enumerate(rows):
        beat, hand = row['_time'], row['_type']
        if hand not in (0, 1) or row['_lineIndex'] not in range(4) or row['_lineLayer'] not in range(3):
            raise ValueError('対応していないノーツ: ' + str(source_index))
        time_ms = round((beat * 60 / info['_beatsPerMinute'] - SOURCE_MINUS_GAME_SECONDS) * 1000, 4)
        count, length = LONGS.get((beat, hand), (1, 0))
        gold_reason = 'climax' if beat >= GOLD_CLIMAX_START_BEAT else 'accent' if beat in GOLD_ACCENT_BEATS else None
        assert not (gold_reason and count > 1), '短いロングの色と担当手は維持する'
        # Beat Saberの赤=左/青=右を、このゲームの青=左/赤=右へ対応させる。位置は左右反転しない。
        note = dict(beat=beat, time=time_ms, x=X[row['_lineIndex']], y=Y[row['_lineLayer']],
                    type='long' if count > 1 else 'tap', color='gold' if gold_reason else 'blue' if hand == 0 else 'red',
                    direction='none', count=count, lengthMs=length)
        times = [round(time_ms + k * (length - 100) / (count - 1), 4) for k in range(count)] if count > 1 else [time_ms]
        directions = ['down' if (hand_cuts[hand] + k) % 2 == 0 else 'up' for k in range(count)]
        hand_cuts[hand] += count
        notes.append(note)
        plan.append(dict(index=index, timesMs=times, directions=directions))
        evidence.append(dict(index=index, sourceIndex=source_index, sourceBeat=beat,
                             sourceHand=hand, goldReason=gold_reason, sourceColumn=row['_lineIndex'], sourceRow=row['_lineLayer'],
                             timeMs=time_ms, adaptation='short-long' if count > 1 else 'no-arrows', cutTimesMs=times))
    assert len(notes) == 1158 and sum(n['count'] > 1 for n in notes) == len(LONGS)
    for i, note in enumerate(notes):
        assert 0 <= note['time'] <= note['time'] + note['lengthMs'] < 144219.1384
        if note['count'] > 1:
            following = next(n for n in notes[i+1:] if n['color'] == note['color'])
            assert following['time'] - (note['time'] + note['lengthMs']) >= 250, 'ロング後の手の戻し時間'
    chart = dict(_comment='指定動画HkKRAd7JYkUのRUSH E (v2) / JonathanRune / Expert+ (BeatSaver 1eb9)。全1158ノーツの時刻・4列3段配置を移植。フリックなし、7か所のみ短いロング。終盤ラッシュと途中6組の同時押しを金へ変更し、金以外の担当手を維持。',
                 bpm=info['_beatsPerMinute'], coordScale=1, offsetMs=0,
                 beatZeroMs=round(-SOURCE_MINUS_GAME_SECONDS*1000, 4), displayLevel=10,
                 timeSignatures=[dict(beat=0, numerator=4, denominator=4)],
                 editorTempoMap=[dict(beat=0, timeMs=round(-SOURCE_MINUS_GAME_SECONDS*1000, 4)),
                                 dict(beat=288, timeMs=round((144-SOURCE_MINUS_GAME_SECONDS)*1000, 4))],
                 editorMarkers=sorted(
                     [dict(timeMs=n['time'], name='E / 短いロング') for n in notes if n['count'] > 1]
                     + [dict(timeMs=round((b * .5 - SOURCE_MINUS_GAME_SECONDS) * 1000, 4),
                             name='E / 金ラッシュ開始' if b == GOLD_CLIMAX_START_BEAT else 'E / 金アクセント')
                        for b in sorted(GOLD_ACCENT_BEATS | {GOLD_CLIMAX_START_BEAT})],
                     key=lambda marker: marker['timeMs']), notes=notes)
    groups = collections.Counter(n['time'] for n in notes)
    audit = dict(revision='beatsaber-1eb9-gold-2026-10-14', sourceMapId='1eb9', sourceNotesSha256=SOURCE_SHA256,
                 notes=len(notes), cuts=sum(n['count'] for n in notes), types=dict(collections.Counter(n['type'] for n in notes)),
                 colors=dict(collections.Counter(n['color'] for n in notes)),
                 goldClimaxStartMs=round((GOLD_CLIMAX_START_BEAT * .5 - SOURCE_MINUS_GAME_SECONDS) * 1000, 4),
                 goldClimaxNotes=sum(e['goldReason'] == 'climax' for e in evidence),
                 goldAccentNotes=sum(e['goldReason'] == 'accent' for e in evidence),
                 goldAccentTimesMs=sorted({e['timeMs'] for e in evidence if e['goldReason'] == 'accent'}),
                 sourceNotesPreserved=len(notes), droppedSourceNotes=0, flicks=0,
                 sourceMinusGameSeconds=SOURCE_MINUS_GAME_SECONDS, simultaneousGroups=dict(collections.Counter(groups.values())),
                 firstNoteMs=notes[0]['time'], lastNoteEndMs=max(n['time']+n['lengthMs'] for n in notes),
                 longTimesMs=[n['time'] for n in notes if n['count'] > 1])
    save(output / 'Assets/StreamingAssets/Songs/RushE/chart_e.json', chart)
    dest = output / 'Tools/ChartDesign/RushE'
    save(dest / 'e-cut-plan.json', dict(notes=plan))
    save(dest / 'e-evidence.json', evidence)
    save(dest / 'e-audit.json', audit)
    return audit


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True, help='生成先。本編へ反映する場合はリポジトリルートを明示')
    args = parser.parse_args()
    print(json.dumps(build(args.output.resolve()), ensure_ascii=False, indent=2))
