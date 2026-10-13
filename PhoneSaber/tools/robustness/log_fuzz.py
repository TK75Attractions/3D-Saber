#!/usr/bin/env python3
"""巨大/途中/非 UTF-8 ログを tmp に生成し、純粋な parser と status 読取りを試す。"""
import argparse
import contextlib
import io
import json
from pathlib import Path
import random
import sys
import tempfile
import time

HERE = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(HERE), str(HERE.parent / 'ios/PhoneSaberSender/Tools')]
import latency_report as latency
import phone_saber_status as status


BASE = ('2026-10-13T00:00:01Z station=A event=latency-block block=1 '
        'max-queued-frames=1 median-ms=20 p95-ms=30 n=15 red-pkt-per-s=30 route=LAN platform=Mac misses=0\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=int, default=20261013)
    parser.add_argument('--seconds', type=float, default=120)
    args = parser.parse_args()
    assert 0 < args.seconds <= 180
    rng = random.Random(args.seed)
    started = time.monotonic()
    count = 0
    with tempfile.TemporaryDirectory(prefix='phonesaber-log-fuzz-') as temporary:
        root = Path(temporary)
        ctx = status.Context(home=root, repo_root=root, unity_root=None, inbox=root/'inbox', runner=lambda *a: None)
        ctx.editor_log.parent.mkdir(parents=True)
        # 64 MiB / 非 UTF-8 / 末尾切断。status は 4 MiB の tail に限定される。
        with ctx.editor_log.open('wb') as stream:
            for _ in range(64):
                stream.write(b'\xff' * 1024 * 1024)
            stream.write(b'\n[PhoneSaber][P2P] last 10s: RED=1 BLUE=2 peers=1\n' + BASE.encode())
        assert status.check_editor_log(ctx, True)
        with contextlib.redirect_stdout(io.StringIO()):
            assert latency.main([str(ctx.editor_log)]) == 0
        while time.monotonic() - started < args.seconds:
            lines = [BASE[:rng.randrange(len(BASE))], rng.randbytes(rng.randrange(2048)).decode('utf-8', 'replace'),
                     BASE.replace('median-ms=20', 'median-ms=' + rng.choice(['nan', 'inf', '-1', '1e999', 'oops', '20'])),
                     '[PhoneSaber][P2P] last 10s: RED=' + '9' * rng.randrange(5000) + ' BLUE=2']
            blocks = latency.parse(lines)
            latency.summarize(blocks)
            latency.summarize_estimates(latency.parse_estimates(lines))
            status.parse_stats(lines[-1])
            if count % 1000 == 0:
                ctx.editor_log.write_bytes('\n'.join(lines).encode())
                status.check_editor_log(ctx, True)
            count += 1
    print(json.dumps(dict(seed=args.seed, iterations=count, seconds=round(time.monotonic()-started, 2), result='PASS')))


if __name__ == '__main__':
    main()
