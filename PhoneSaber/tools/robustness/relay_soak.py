#!/usr/bin/env python3
"""実際の Mac relay を loopback だけで負荷試験する（GUI/Bonjour なし）。"""
import argparse
import json
from pathlib import Path
import random
import socket
import subprocess
import sys
import tempfile
import threading
import time

TOOLS = Path(__file__).resolve().parents[2] / 'ios/PhoneSaberSender/Tools'
sys.path.insert(0, str(TOOLS))
from test_phone_saber_p2p_bridge import datagram, free_tcp_port, free_udp_port
import phone_saber_p2p_bridge as launcher


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seed', type=int, default=20261013)
    parser.add_argument('--seconds', type=float, default=120)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    assert 0 < args.seconds <= 180
    rng = random.Random(args.seed)
    stop = threading.Event()
    workers = []
    receiver = socket.socket()
    receiver.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
    receiver.bind(('127.0.0.1', 0))
    receiver.listen(64)
    receiver.settimeout(.2)

    def handle(client):
        with client:
            client.settimeout(.2)
            try:
                mode = client.recv(1)
                if mode == b'N':
                    stop.wait(args.seconds + 5)  # accept するが読まない受信先
                else:
                    while not stop.is_set():
                        try:
                            chunk = client.recv(4096)
                        except socket.timeout:
                            continue
                        if not chunk:
                            client.sendall(b'accepted')
                            client.shutdown(socket.SHUT_WR)
                            return
                        if mode == b'S':
                            stop.wait(.02)
            except OSError:
                pass

    def accept():
        while not stop.is_set():
            try:
                client, _ = receiver.accept()
            except socket.timeout:
                continue
            except OSError:
                break
            worker = threading.Thread(target=handle, args=(client,), daemon=True)
            workers.append(worker)
            worker.start()

    threading.Thread(target=accept, daemon=True).start()
    with tempfile.TemporaryDirectory(prefix='phonesaber-robustness-') as temporary:
        cache = Path(temporary)
        # ps は sandbox で禁止されるため、同じ source の試験用コピーに自己計測だけ追加。
        original = launcher.SOURCES[1].read_text()
        monitor = r'''let monitor = DispatchSource.makeTimerSource(queue: .main)
        monitor.schedule(deadline: .now(), repeating: 5)
        monitor.setEventHandler {
            var info = mach_task_basic_info()
            var count = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size / MemoryLayout<integer_t>.size)
            let result = withUnsafeMutablePointer(to: &info) { pointer in
                pointer.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                    task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), $0, &count)
                }
            }
            var usage = rusage()
            getrusage(RUSAGE_SELF, &usage)
            let cpu = Double(usage.ru_utime.tv_sec + usage.ru_stime.tv_sec) +
                Double(usage.ru_utime.tv_usec + usage.ru_stime.tv_usec) / 1_000_000
            print("[ROBUSTNESS] rss=\(result == KERN_SUCCESS ? info.resident_size : 0) cpuSeconds=\(cpu)")
        }
        monitor.resume()
        resourceMonitor = monitor
        dispatchMain()'''
        instrumented = cache / 'PhoneSaberP2PBridge.swift'
        instrumented.write_text(original.replace('        dispatchMain()', monitor).replace(
            '    private static var signalSources:', '    private static var resourceMonitor: DispatchSourceTimer?\n    private static var signalSources:'))
        sources = (launcher.SOURCES[0], instrumented)
        digest = launcher.source_digest
        launcher.SOURCES = sources
        launcher.source_digest = lambda: digest(sources)
        binary = launcher.build(cache)
        udp_port, tcp_port = free_udp_port(), free_tcp_port()
        red, blue = [socket.socket(socket.AF_INET, socket.SOCK_DGRAM) for _ in range(2)]
        for sock in (red, blue):
            sock.bind(('127.0.0.1', 0))
        blue.settimeout(.5)
        phones = [socket.socket(socket.AF_INET, socket.SOCK_DGRAM) for _ in range(48)]
        for sock in phones:
            sock.bind(('127.0.0.1', 0))
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 262144)
        log_path = args.output.with_suffix('.log')
        args.output.parent.mkdir(parents=True, exist_ok=True)
        samples, forwarded, malformed, churn = [], 0, 0, 0
        with log_path.open('w') as log:
            process = subprocess.Popen([str(binary), '--no-bonjour', '--loopback-only',
                '--listen-port', str(udp_port), '--diag-listen-port', str(tcp_port),
                '--diag-port', str(receiver.getsockname()[1]), '--peer-idle-timeout', '2',
                '--red-port', str(red.getsockname()[1]), '--blue-port', str(blue.getsockname()[1]),
                '--stats-interval', '5'], stdout=log, stderr=subprocess.STDOUT)
            clients = []
            try:
                deadline = time.monotonic() + 15
                while 'diag relay: listening' not in log_path.read_text():
                    assert process.poll() is None and time.monotonic() < deadline
                    time.sleep(.05)
                for _ in range(8):
                    client = socket.create_connection(('127.0.0.1', tcp_port), timeout=.3)
                    client.sendall(b'N')
                    clients.append(client)
                def slow_upload():
                    with socket.create_connection(('127.0.0.1', tcp_port), timeout=.5) as client:
                        client.sendall(b'S')
                        while not stop.is_set():
                            client.sendall(b'slow')
                            stop.wait(.05)
                        client.shutdown(socket.SHUT_WR)
                slow = threading.Thread(target=slow_upload, daemon=True)
                slow.start()
                started = next_sample = time.monotonic()
                sequence = 0
                while time.monotonic() - started < args.seconds:
                    assert process.poll() is None
                    sequence += 1
                    # 正常な BLUE は異常 RED と別 session にし、毎回そのままの転送を確認。
                    body = f'{sequence},2,3,4'.encode()
                    phones[0].sendto(datagram(1, 1, 0xffffffff, sequence, body), ('127.0.0.1', udp_port))
                    assert blue.recv(1024) == body
                    forwarded += 1
                    for _ in range(12):
                        phone = rng.choice(phones[1:])
                        valid = datagram(1, 0, rng.randrange(100000), rng.choice([0, 1, sequence, 2**64-1]), b'1,2,3,4')
                        cases = [valid, valid, valid[:rng.randrange(len(valid))],
                                 rng.randbytes(rng.choice([0, 1, 19, 277, 8192, 65000])),
                                 datagram(2, 0, 3, sequence), valid + b'x' * 300]
                        phone.sendto(rng.choice(cases), ('127.0.0.1', udp_port))
                        malformed += 1
                    # half-close / 応答完了を繰り返す。
                    with socket.create_connection(('127.0.0.1', tcp_port), timeout=2) as client:
                        client.sendall(b'H' + rng.randbytes(rng.randrange(256)))
                        client.shutdown(socket.SHUT_WR)
                        reply = b''
                        while chunk := client.recv(1024):
                            reply += chunk
                        assert reply == b'accepted'
                        churn += 1
                    if time.monotonic() >= next_sample:
                        import re
                        metrics = re.findall(r'\[ROBUSTNESS\] rss=(\d+) cpuSeconds=([0-9.]+)', log_path.read_text())
                        rss, cpu = metrics[-1] if metrics else ('0', '0')
                        fd = subprocess.run(['lsof', '-p', str(process.pid), '-Ff'], capture_output=True, text=True, timeout=3)
                        samples.append(dict(seconds=round(time.monotonic()-started, 2), rss_kib=int(rss)//1024,
                                            cpu_seconds=float(cpu), fds=sum(line[1:].isdigit() for line in fd.stdout.splitlines() if line.startswith('f'))))
                        next_sample = time.monotonic() + 5
                    time.sleep(.02)
                # 消えた UDP peer の回収を待つ（正常端末は最終確認で再接続）。
                time.sleep(3)
                phones[0].sendto(datagram(1, 1, 0xffffffff, sequence+1, b'9,8,7,6'), ('127.0.0.1', udp_port))
                assert blue.recv(1024) == b'9,8,7,6'
                result = dict(seed=args.seed, seconds=args.seconds, forwarded=forwarded, random_datagrams=malformed,
                              completed_half_closed_uploads=churn, samples=samples, result='PASS')
                args.output.write_text(json.dumps(result, indent=2) + '\n')
                print(json.dumps(result))
            finally:
                stop.set()
                for client in clients:
                    client.close()
                process.terminate()
                process.wait(timeout=5)
                for sock in phones + [red, blue]:
                    sock.close()
                receiver.close()
                for worker in workers:
                    worker.join(.1)


if __name__ == '__main__':
    main()
