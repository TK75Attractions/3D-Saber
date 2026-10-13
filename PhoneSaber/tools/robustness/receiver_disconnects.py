#!/usr/bin/env python3
"""実 HTTP の全 byte 境界切断・同時 retry・ENOSPC 後の継続を確認する。"""
import argparse
from contextlib import closing
from concurrent.futures import ThreadPoolExecutor
import errno
from http.client import HTTPConnection
import json
from pathlib import Path
import random
import socket
import sys
import tempfile
import threading
import time
from unittest import mock

TOOLS = Path(__file__).resolve().parents[2] / 'ios/PhoneSaberSender/Tools'
sys.path.insert(0, str(TOOLS))
import phone_saber_triage_protocol as protocol
from phone_saber_triage_receiver import TriageHTTPServer
from test_phone_saber_triage_protocol import write_bundle


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=int, default=20261013)
    args = parser.parse_args()
    rng = random.Random(args.seed)
    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='phonesaber-disconnect-') as temporary:
        root = Path(temporary)
        body = write_bundle(root / 'bundle')
        inbox = root / 'inbox'
        inbox.mkdir()
        server = TriageHTTPServer(('127.0.0.1', 0), inbox, analysis_mode='disabled')
        server.write_report = lambda bundle: None
        errors = []
        server.handle_error = lambda *args: errors.append(sys.exc_info()[1])
        threading.Thread(target=server.serve_forever, daemon=True).start()
        def post():
            with closing(HTTPConnection('127.0.0.1', server.server_port, timeout=3)) as client:
                client.request('POST', '/v1/bundle', body=body, headers={'Content-Type': protocol.CONTENT_TYPE})
                response = client.getresponse()
                response.read()
                return response.status
        def health():
            with closing(HTTPConnection('127.0.0.1', server.server_port, timeout=3)) as client:
                client.request('GET', '/health')
                response = client.getresponse()
                assert response.status == 200
                assert json.loads(response.read())['uploadsInProgress'] == 0
        header = (f'POST /v1/bundle HTTP/1.1\r\nHost: localhost\r\nContent-Type: {protocol.CONTENT_TYPE}\r\n'
                  f'Content-Length: {len(body)}\r\nConnection: close\r\n\r\n').encode()
        try:
            for boundary in range(len(body)):
                assert time.monotonic() - started < 180, 'time box exceeded'
                with socket.create_connection(('127.0.0.1', server.server_port), timeout=3) as client:
                    request = header + body[:boundary]
                    split = rng.randrange(len(request) + 1)
                    client.sendall(request[:split])
                    client.sendall(request[split:])
                    client.shutdown(socket.SHUT_WR)
                    reply = b''
                    while chunk := client.recv(4096):
                        reply += chunk
                    assert reply.startswith(b'HTTP/1.1 400'), (boundary, reply[:80])
                assert not list(inbox.iterdir()), f'partial bundle at {boundary}'
                if boundary % 100 == 0:
                    health()
            with mock.patch.object(protocol.os, 'replace', side_effect=OSError(errno.ENOSPC, 'simulated disk full')):
                assert post() == 400
            assert not list(inbox.iterdir())
            health()
            with ThreadPoolExecutor(max_workers=2) as pool:
                assert sorted(pool.map(lambda _: post(), range(2))) == [200, 201]
            assert len(list(inbox.iterdir())) == 1
            health()
            assert not errors, errors
        finally:
            server.shutdown()
            server.server_close()
    print(json.dumps(dict(seed=args.seed, body_boundaries=len(body), seconds=round(time.monotonic()-started, 2), result='PASS')))


if __name__ == '__main__':
    main()
