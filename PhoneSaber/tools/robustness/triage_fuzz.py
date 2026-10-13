#!/usr/bin/env python3
"""seed 固定の PSBT mutation と全 byte 境界切断。保存は tmp のみ。"""
import argparse
from concurrent.futures import ThreadPoolExecutor
import io
import json
from pathlib import Path
import random
import sys
import tempfile
import time

TOOLS = Path(__file__).resolve().parents[2] / 'ios/PhoneSaberSender/Tools'
sys.path.insert(0, str(TOOLS))
import phone_saber_triage_protocol as protocol
from test_phone_saber_triage_protocol import empty_bundle_files, raw_envelope


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=int, default=20261013)
    parser.add_argument('--seconds', type=float, default=120)
    args = parser.parse_args()
    assert 0 < args.seconds <= 180
    rng = random.Random(args.seed)
    valid = raw_envelope(empty_bundle_files())
    count = accepted = 0
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='phonesaber-triage-fuzz-') as temporary:
        inbox = Path(temporary) / 'inbox'
        def receive(body, length):
            return protocol.receive_bundle(io.BytesIO(body), length, inbox)
        # 同時 retry の原子的な公開。
        def retry(_):
            try:
                receive(valid, len(valid))
                return 'created'
            except protocol.DuplicateBundle:
                return 'duplicate'
        with ThreadPoolExecutor(max_workers=2) as pool:
            assert sorted(pool.map(retry, range(2))) == ['created', 'duplicate']
        final = inbox / 'phone_saber_triage_sample_session'
        final.rename(Path(temporary) / 'saved')
        # データの全 byte 境界で EOF（宣言は全体サイズ）。
        for boundary in range(len(valid)):
            try:
                receive(valid[:boundary], len(valid))
                raise AssertionError(f'partial accepted at {boundary}')
            except protocol.BundleError:
                pass
            assert not list(inbox.glob('*'))
        while time.monotonic() - started < args.seconds:
            body = bytearray(valid)
            choice = rng.randrange(5)
            if choice == 0:
                body = bytearray(rng.randbytes(rng.randrange(1024)))
            elif choice == 1:
                body = body[:rng.randrange(len(body))]
            elif choice == 2:
                for _ in range(rng.randrange(1, 8)):
                    body[rng.randrange(len(body))] = rng.randrange(256)
            elif choice == 3:
                body += rng.randbytes(rng.randrange(32))
            else:
                manifest = json.loads(valid[8:8+int.from_bytes(valid[4:8], 'big')])
                entry = rng.choice(manifest['files'])
                entry[rng.choice(['size', 'path', 'sha256', 'contentType'])] = rng.choice([
                    None, True, [], {}, -1, 2**63, '../summary.json', '/tmp/x', 'images/a/b.png', '\x00'])
                data = json.dumps(manifest).encode()
                body = b'PSBT' + len(data).to_bytes(4, 'big') + data
            declared = rng.choice([len(body), len(valid), protocol.MAX_BUNDLE_BYTES, 2**63])
            try:
                receive(bytes(body), declared)
                accepted += 1
                import shutil
                shutil.rmtree(final)
            except (protocol.BundleError, FileExistsError):
                pass
            except Exception:
                print(f'REPRO seed={args.seed} iteration={count} declared={declared} body={bytes(body).hex()}', file=sys.stderr)
                raise
            assert not list(inbox.glob('*')), f'partial inbox at {count}'
            count += 1
    print(json.dumps(dict(seed=args.seed, iterations=count, accepted=accepted,
                          truncation_boundaries=len(valid), seconds=round(time.monotonic()-started, 2), result='PASS')))


if __name__ == '__main__':
    main()
