#!/usr/bin/env python3
"""今回のPlayerログのみでsoakの完了・異常を検証する。"""
import argparse
import math
from pathlib import Path
import re

DONE = re.compile(r'^\[PhoneSaberSoak\] done loops=(\d+) exceptions=(\d+) leak=(\S+) errors=(\d+) input=(\S+) flow=(\S+)$')
LOOP = re.compile(r'\bevent=soak loop=(\d+)\b')


def summarise(text):
    rows = []
    done = []
    ordered = True
    for line in text.splitlines():
        if LOOP.search(line):
            ordered &= not done
            rows.append(line)
        match = DONE.fullmatch(line)
        if match:
            done.append(match)
    if len(done) != 1:
        return 1, 'soak: 判定=FAILED（完了行が無い、または複数）'
    loops, exceptions, leak, errors, input_state, flow = done[0].groups()
    count = int(loops)
    numbered = [int(LOOP.search(row).group(1)) for row in rows]
    valid = ordered and count > 0 and numbered == list(range(1, count + 1))
    valid &= exceptions == '0' and errors == '0' and input_state == flow == 'ok'
    # Playerの最低標本数(暖機3周+8点)と矛盾する完了行を認証しない。
    valid &= leak == ('stable' if count >= 11 else 'insufficient')
    for row in rows:
        fields = dict(re.findall(r'(\S+)=(\S+)', row.split(' messages="', 1)[0]))
        valid &= all(fields.get(field) == '0' for field in ('exceptions', 'errors'))
        valid &= all(fields.get(field) == 'ok' for field in ('input', 'flow'))
        valid &= all(fields.get(field) == 'True' for field in ('red-active', 'blue-active', 'red-moved', 'blue-moved'))
        for name in ('red-packets', 'blue-packets', 'fps', 'worst-frame-ms', 'game-ms',
                     'title-ms', 'select-ms', 'load-game-ms', 'return-ms', 'managed-bytes',
                     'allocated-bytes', 'reserved-bytes', 'AudioSource', 'Material', 'Texture2D',
                     'Mesh', 'GameObject', 'MonoBehaviour', 'SaberInputBridge', 'InputPoint'):
            try:
                value = float(fields[name])
                minimum = 30 if name.endswith('-packets') else 0
                valid &= math.isfinite(value) and value >= minimum
            except (KeyError, ValueError):
                valid = False
    verdict = 'PASS' if valid else 'FAILED'
    note = '（漏れ判定は11周以上必要、まだ未判定）' if leak == 'insufficient' else ''
    return (0 if valid else 1), f'soak: 判定={verdict} 周回={count} 例外={exceptions} Error={errors} leak={leak}{note}'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('log', type=Path)
    args = parser.parse_args()
    try:
        code, summary = summarise(args.log.read_text(errors='replace'))
    except OSError as error:
        code, summary = 1, 'soak: 判定=FAILED（ログを読めません: ' + str(error) + '）'
    print(summary)
    return code


if __name__ == '__main__':
    raise SystemExit(main())
